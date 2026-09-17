// The streaming builder of `vorticity.bloom.sbbf.v1` (docs/10-indexes.md §5.1, §4.3, §5.4, §7.2).
//
// FED BY THE INGEST, ONE ROW RANGE AT A TIME, while the batch's arena is live. Each valid row is
// hashed -- XxHash3-64 over the bytes §5.1's table names -- into the open block's hash set, which is
// also its exact distinct count. When the block closes, the set sizes and fills the block's filter
// and is folded into the generation's set; when the k-th block closes, the generation's filter is
// sized and filled from that set. Nothing is re-read.
//
// EXACT COUNTS AT EVERY LEVEL, where 10 §4.3 allows the sum. A generation "sized from the sum of its
// blocks' distinct counts" over-sizes by the overlap, and a status column with five values per
// block would get a generation filter sized for eighty; the union set is one insert per block
// distinct, and it sizes the generation for five -- below the policy's floor, so no filter at all,
// which is the right answer for a column the zone map already prunes. The file-level set is the
// same union over the whole file, which is why that resolution is opt-in and gives up past a
// ceiling.
//
// THE HASH PER ROW IS THE COST, and `Auto` stops paying it as early as a fact allows: +24 % on a
// string column and +37 % on an i64 one whose filters were kept, +27 % on a dictionary column whose
// filter was dropped only at the end. A memo of hashes already computed was measured and removed:
// on those columns its lookup costs what the hash did. What pays is not hashing -- the verdicts
// below come within the first block or the first generation.
//
// THE FILTERS GO OUT WHEN THEIR GENERATION CLOSES, not at `CompleteAsync`. 10 §7.2 says both
// "nothing is buffered across chunks" and "runs are written at CompleteAsync"; holding every filter
// to the end is megabytes per column per million rows, so the streaming half wins and the regions
// land between data chunks. A region is a file region no layout references, so where it lies
// changes nothing for any reader (10 §3.2).
//
// THEY GO OUT AS A TREE (13 §6.2, step 24): a generation's sixteen block filters are the leaves of a
// level-1 node, whose filter is the generation's, and `BloomTreeWriter` stacks the nodes sixteen to
// a level up to one root, the run's payload. The bits of every filter are what they were; a node
// whose union passes `max_blocks` is now not built, where a generation filter used to be clamped
// into uselessness.
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Vorticity.Arrays;
using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Compute;
using Vorticity.Indexes;
using Vorticity.Types;
using Vorticity.Types.Numerics;

namespace Vorticity.Writing;

/// <summary>Builds one column's split-block Bloom filters, block by block.</summary>
internal sealed class BloomBuilder : IndexBuilder
{
    /// <summary>
    /// Blocks per generation: 10 §4.3's `k`, which is the tree's fanout and not a free parameter.
    /// </summary>
    /// <remarks>
    /// STEP 33 TRIED TO CALIBRATE IT AND FOUND IT WAS NOT A KNOB. `k` is how many blocks one
    /// generation covers; <see cref="BloomIndexOptions.Fanout"/> is how many children a node of the
    /// tree takes, it is written into every entry's options and a reader rejects a file whose
    /// fanout is not its own. They are one number: at `k` = 8 with the fanout left at 16 the
    /// generations no longer line up with the nodes, `table_mixed`'s filters grow from 10 672 264
    /// bytes to 20 019 048 and every probe measured prunes nothing at all; moving both to 8 fails
    /// 28 tests of the index suite, the file filter among them. Changing it is a format version,
    /// a code change and a fresh calibration, not a default to tune (docs/10-indexes.md §11).
    /// </remarks>
    internal const int GenerationBlocks = BloomIndexOptions.Fanout;

    /// <summary>The file-level filter's own ceiling, 10 §5.4: 1 MiB blocks, 32 MiB.</summary>
    internal const int FileMaxBlocks = 1 << 20;

    /// <summary>Distinct values the file-level resolution may hold before it gives up.</summary>
    internal const int FileHashCeiling = 1 << 22;

    /// <summary>Rows between two of `Auto`'s checks inside a block.</summary>
    private const int CheckStride = 256;

    /// <summary>The blocks `Auto` watches for a column that repeats one set.</summary>
    private const int RepeatBlocks = 4;

    private readonly IndexPolicy _policy;
    private readonly int _maxBlocks;
    private int _firstDistinct;
    private long _blockRawStart;
    private readonly HashSet64 _block = new HashSet64();
    private HashSet64? _file;

    /// <summary>
    /// The tree, which also holds the open generation's union (<see cref="BloomTreeWriter.Generation"/>);
    /// created by the first block the builder keeps, so that a column `Auto` gives up inside its first
    /// block costs none.
    /// </summary>
    private BloomTreeWriter? _tree;

    private BloomTreeWriter TreeWriter => _tree ??= new BloomTreeWriter(
        _policy.FalsePositivePpm, _maxBlocks, _policy.MinDistinct, filters: _policy.Resolutions >= 2, this);

    private int _generationFirst;
    private long _rawBytes;
    private uint[]? _generationBlockWords;
    private int _generationBlockWordCount;
    private int _blocks;

    private readonly bool _trigrams;

    /// <param name="policy">A <see cref="IndexPolicyKind.Bloom"/> or <see cref="IndexPolicyKind.NgramBloom"/> policy.</param>
    internal BloomBuilder(IndexPolicy policy)
    {
        _policy = policy;
        _maxBlocks = MaxBlocksOf(policy);
        _trigrams = policy.Kind == IndexPolicyKind.NgramBloom;
        _file = policy.Resolutions >= 3 ? new HashSet64() : null;
    }

    /// <summary>A policy's ceiling, within what a reader takes (<see cref="BloomBuilderLimits.MaxFilterBlocks"/>).</summary>
    /// <param name="policy">The policy.</param>
    internal static int MaxBlocksOf(IndexPolicy policy) =>
        (int)Math.Min((uint)policy.MaxBlocks, BloomBuilderLimits.MaxFilterBlocks);

    /// <summary>The kind this builder writes.</summary>
    internal string Kind => _trigrams ? IndexKinds.BloomNgram3 : IndexKinds.BloomSbbf;

    /// <inheritdoc/>
    /// <remarks>
    /// The open generation's block filters and the node filters waiting for their level to go out:
    /// uncompressed, so their size is their size.
    /// </remarks>
    protected override long OpenBytes => ((long)_generationBlockWordCount * sizeof(uint)) + (_tree?.OpenBytes ?? 0);

    /// <summary>
    /// Rows per block, which bounds a block's raw bytes before it closes, so that `Auto` can give up
    /// inside the first block; 0 when blocks are the caller's batches and have no fixed length.
    /// </summary>
    internal int BlockRows { get; init; }

    /// <summary>Why the file-level filter was dropped, when it was asked for and could not be kept.</summary>
    internal string? FileAbandoned { get; private set; }

    /// <summary>Filter blocks per closed block, in block order: 0 where a block got no filter.</summary>
    internal List<int> BlockFilterBlocks { get; } = [];

    /// <summary>Every generation closed, with the region of its leaves, in block order.</summary>
    internal List<BloomRun> Runs { get; } = [];

    /// <summary>The tree, once <see cref="EndOfData"/> finished it.</summary>
    internal BloomTree? Tree { get; private set; }

    /// <summary>The blocks, since the builder's first, whose filter was built.</summary>
    internal int Leaves { get; private set; }

    /// <summary>The policy this builder serves.</summary>
    internal IndexPolicy Policy => _policy;

    /// <summary>Whether the dtype can be indexed at all, and why not.</summary>
    /// <param name="dtype">The column's dtype.</param>
    /// <param name="reason">Why not.</param>
    /// <returns>Whether a filter can be built.</returns>
    internal static bool Supports(DType dtype, out string? reason) => Supports(dtype, trigrams: false, out reason);

    /// <summary>Whether the dtype can be indexed by this kind, and why not.</summary>
    /// <param name="dtype">The column's dtype.</param>
    /// <param name="trigrams">Whether the index holds trigrams, which only text has.</param>
    /// <param name="reason">Why not.</param>
    /// <returns>Whether a filter can be built.</returns>
    internal static bool Supports(DType dtype, bool trigrams, out string? reason)
    {
        while (dtype.Kind == DTypeKind.Extension)
        {
            dtype = dtype.StorageType;
        }

        if (trigrams)
        {
            reason = dtype.Kind is DTypeKind.Utf8 or DTypeKind.Binary
                ? null
                : $"a trigram index needs text, and this column is {dtype.Kind}";
            return reason is null;
        }

        if (dtype.Kind is DTypeKind.List or DTypeKind.FixedSizeList)
        {
            // 10 §5.1: a list's elements, each into its row's block (step 28b).
            if (trigrams)
            {
                reason = $"a trigram index needs text, and this column is {dtype.Kind}";
                return false;
            }

            DType element = dtype.ElementType;
            while (element.Kind == DTypeKind.Extension)
            {
                element = element.StorageType;
            }

            reason = element.Kind switch
            {
                DTypeKind.Primitive or DTypeKind.Decimal or DTypeKind.Utf8 or DTypeKind.Binary => null,
                DTypeKind.Bool => "a two-value domain has nothing a Bloom filter could skip",
                _ => $"a Bloom filter indexes the scalar elements of a list, and this list holds {element.Kind}",
            };
            return reason is null;
        }

        reason = dtype.Kind switch
        {
            DTypeKind.Primitive or DTypeKind.Decimal or DTypeKind.Utf8 or DTypeKind.Binary => null,
            DTypeKind.Bool => "a two-value domain has nothing a Bloom filter could skip",
            _ => $"a Bloom filter does not index a {dtype.Kind} column",
        };
        return reason is null;
    }

    /// <summary>Hashes rows <c>[start, start + count)</c> of the node into the open block.</summary>
    /// <param name="arena">The batch's arena.</param>
    /// <param name="nodeIndex">The column in it.</param>
    /// <param name="start">The first row.</param>
    /// <param name="count">How many rows.</param>
    internal override void Accumulate(CanonicalArena arena, int nodeIndex, int start, int count)
    {
        if (Abandoned is not null || count <= 0)
        {
            return;
        }

        CanonicalNode storage = arena.GetNode(nodeIndex);
        while (storage.Kind == CanonicalKind.Extension)
        {
            storage = arena.GetNode(storage.StorageIndex);
        }

        if (storage.Kind is CanonicalKind.ListView or CanonicalKind.FixedSizeList)
        {
            Lists(arena, nodeIndex, start, count);
            return;
        }

        Values(arena, nodeIndex, start, count, capacity: BlockRows);
    }

    /// <summary>
    /// A list column's rows: each element of a valid row, into the row's block (10 §5.1, step 28b),
    /// so the filter answers "does any element of a row in this block equal v".
    /// </summary>
    /// <remarks>
    /// A null list names nothing, and a null element is not inserted. The ranges of consecutive
    /// rows that lie end to end are hashed as one, which is every row of a list the writer laid
    /// out. `Auto` judges inside the block only where the block's elements have a bound: a
    /// fixed-size list's are its rows times the size; a list view's are unbounded, and its
    /// verdicts wait for the block's close, where the raw bytes are known.
    /// </remarks>
    private void Lists(CanonicalArena arena, int nodeIndex, int start, int count)
    {
        if (_trigrams)
        {
            Abandon("a trigram index needs text, and this column is a list");
            return;
        }

        CanonicalNode outer = arena.GetNode(nodeIndex);
        CanonicalNode node = outer;
        while (node.Kind == CanonicalKind.Extension)
        {
            node = arena.GetNode(node.StorageIndex);
        }

        ValidityMask own = ValidityMask.From(arena, node.Validity);
        ValidityMask wrapper = ValidityMask.From(arena, outer.Validity);
        if (own.AllInvalid || wrapper.AllInvalid)
        {
            return;
        }

        bool allValid = own.AllValid && wrapper.AllValid;
        int elements = node.ElementsIndex;
        if (node.Kind == CanonicalKind.FixedSizeList)
        {
            int size = checked((int)node.FixedSize);
            int end = start + count;
            for (int row = start; size > 0 && row < end && Abandoned is null;)
            {
                if (!allValid && !(own.IsValid(row) && wrapper.IsValid(row)))
                {
                    row++;
                    continue;
                }

                int run = row + 1;
                while (run < end && (allValid || (own.IsValid(run) && wrapper.IsValid(run))))
                {
                    run++;
                }

                Values(arena, elements, checked(row * size), checked((run - row) * size), capacity: (long)BlockRows * size);
                row = run;
            }

            return;
        }

        ListRanges ranges = new ListRanges(this, arena, elements);
        switch (node.OffsetPType)
        {
            case PType.U8: ranges.OnSizes<byte>(node, own, wrapper, allValid, start, count); return;
            case PType.U16: ranges.OnSizes<ushort>(node, own, wrapper, allValid, start, count); return;
            case PType.U32: ranges.OnSizes<uint>(node, own, wrapper, allValid, start, count); return;
            case PType.U64: ranges.OnSizes<ulong>(node, own, wrapper, allValid, start, count); return;
            case PType.I8: ranges.OnSizes<sbyte>(node, own, wrapper, allValid, start, count); return;
            case PType.I16: ranges.OnSizes<short>(node, own, wrapper, allValid, start, count); return;
            case PType.I32: ranges.OnSizes<int>(node, own, wrapper, allValid, start, count); return;
            default: ranges.OnSizes<long>(node, own, wrapper, allValid, start, count); return;
        }
    }

    /// <summary>A list view's rows, walked with both physical types resolved, as their element ranges.</summary>
    private readonly ref struct ListRanges(BloomBuilder builder, CanonicalArena arena, int elements)
    {
        internal void OnSizes<TOffset>(
            CanonicalNode node, ValidityMask own, ValidityMask wrapper, bool allValid, int start, int count)
            where TOffset : unmanaged
        {
            switch (node.SizePType)
            {
                case PType.U8: Rows<TOffset, byte>(node, own, wrapper, allValid, start, count); return;
                case PType.U16: Rows<TOffset, ushort>(node, own, wrapper, allValid, start, count); return;
                case PType.U32: Rows<TOffset, uint>(node, own, wrapper, allValid, start, count); return;
                case PType.U64: Rows<TOffset, ulong>(node, own, wrapper, allValid, start, count); return;
                case PType.I8: Rows<TOffset, sbyte>(node, own, wrapper, allValid, start, count); return;
                case PType.I16: Rows<TOffset, short>(node, own, wrapper, allValid, start, count); return;
                case PType.I32: Rows<TOffset, int>(node, own, wrapper, allValid, start, count); return;
                default: Rows<TOffset, long>(node, own, wrapper, allValid, start, count); return;
            }
        }

        private void Rows<TOffset, TSize>(
            CanonicalNode node, ValidityMask own, ValidityMask wrapper, bool allValid, int start, int count)
            where TOffset : unmanaged
            where TSize : unmanaged
        {
            ReadOnlySpan<TOffset> offsets = MemoryMarshal.Cast<byte, TOffset>(node.Offsets.Span).Slice(start, count);
            ReadOnlySpan<TSize> sizes = MemoryMarshal.Cast<byte, TSize>(node.Sizes.Span).Slice(start, count);
            long from = 0;
            long to = 0;
            for (int i = 0; i < count; i++)
            {
                long size = ListViewDecoder.Widen(sizes[i]);
                if (size <= 0 || (!allValid && !(own.IsValid(start + i) && wrapper.IsValid(start + i))))
                {
                    continue;
                }

                long offset = ListViewDecoder.Widen(offsets[i]);
                if (offset == to && to > from)
                {
                    to += size;
                    continue;
                }

                Flush(from, to);
                from = offset;
                to = offset + size;
            }

            Flush(from, to);
        }

        private void Flush(long from, long to)
        {
            if (to > from && builder.Abandoned is null)
            {
                builder.Values(arena, elements, checked((int)from), checked((int)(to - from)), capacity: 0);
            }
        }
    }

    /// <summary>Hashes rows <c>[start, start + count)</c> of a node of scalar values into the open block.</summary>
    /// <param name="arena">The batch's arena.</param>
    /// <param name="nodeIndex">The column, or a list's elements.</param>
    /// <param name="start">The first row.</param>
    /// <param name="count">How many rows.</param>
    /// <param name="capacity">
    /// The most values the open block can hold, which bounds its raw bytes for `Auto`'s verdicts
    /// inside the block; 0 when nothing bounds it.
    /// </param>
    private void Values(CanonicalArena arena, int nodeIndex, int start, int count, long capacity)
    {
        CanonicalNode outer = arena.GetNode(nodeIndex);
        CanonicalNode node = outer;
        while (node.Kind == CanonicalKind.Extension)
        {
            node = arena.GetNode(node.StorageIndex);
        }

        ValidityMask own = ValidityMask.From(arena, node.Validity);
        ValidityMask wrapper = ValidityMask.From(arena, outer.Validity);
        if (own.AllInvalid || wrapper.AllInvalid)
        {
            return;
        }

        bool allValid = own.AllValid && wrapper.AllValid;
        BloomHash hash = _policy.Hash;
        HashSet64 block = _block;
        if (_trigrams && node.Kind != CanonicalKind.VarBinView)
        {
            Abandon($"a trigram index needs text, and this column is {node.Kind}");
            return;
        }

        switch (node.Kind)
        {
            case CanonicalKind.Primitive:
                Fixed(node.Values.Span, node.PType.ByteWidth(), start, count, allValid, own, wrapper, hash, capacity);
                return;

            case CanonicalKind.Decimal:
                Fixed(node.Values.Span, DecimalStorage.ByteWidth(node.Storage), start, count, allValid, own, wrapper, hash, capacity);
                return;

            case CanonicalKind.Constant:
                _rawBytes += (long)count * node.ConstantElement.Length;
                for (int row = start; row < start + count; row++)
                {
                    if (own.IsValid(row) && wrapper.IsValid(row))
                    {
                        block.Add(SplitBlockBloom.Hash(node.ConstantElement, hash));
                        return;
                    }
                }

                return;

            case CanonicalKind.VarBinView when _trigrams:
                Span<byte> trigram = stackalloc byte[Trigrams.Length];
                bool fold = _policy.CaseInsensitive;
                _rawBytes += 16L * count;
                for (int row = start; row < start + count; row++)
                {
                    if (allValid || (own.IsValid(row) && wrapper.IsValid(row)))
                    {
                        ReadOnlySpan<byte> value = LiteralReader.ViewAt(node, row);
                        _rawBytes += value.Length;
                        for (int i = 0; i + Trigrams.Length <= value.Length; i++)
                        {
                            Trigrams.Copy(value.Slice(i, Trigrams.Length), fold, trigram);
                            block.Add(SplitBlockBloom.Hash(trigram, hash));
                        }
                    }
                }

                return;

            case CanonicalKind.VarBinView:
                _rawBytes += 16L * count;
                for (int row = start; row < start + count; row++)
                {
                    if (allValid || (own.IsValid(row) && wrapper.IsValid(row)))
                    {
                        ReadOnlySpan<byte> value = LiteralReader.ViewAt(node, row);
                        _rawBytes += value.Length;
                        block.Add(SplitBlockBloom.Hash(value, hash));
                    }
                }

                return;

            default:
                Abandon($"a Bloom filter does not index a {node.Kind} column");
                return;
        }
    }

    /// <summary>
    /// Fixed-width values. `Auto` checks its share every <see cref="CheckStride"/> rows: the block's
    /// distinct count so far can only grow, and a full block bounds its raw bytes, so a verdict
    /// reached here is the one the block's close would reach, a block's hashing earlier. The block
    /// holds at most <paramref name="capacity"/> values: its rows, or a fixed-size list's rows times
    /// the size; a list view's elements pass 0, and are judged at the block's close.
    /// </summary>
    private void Fixed(
        ReadOnlySpan<byte> values, int width, int start, int count, bool allValid,
        ValidityMask own, ValidityMask wrapper, BloomHash hash, long capacity)
    {
        _rawBytes += (long)count * width;
        HashSet64 block = _block;
        bool checks = capacity > 0 && AutoShare > 0 && BlockRows > 0;
        int end = start + count;
        int row = start;
        bool inline = hash == BloomHash.XxHash3 && allValid && width <= 16;
        while (row < end)
        {
            int stop = Math.Min(end, row + CheckStride);
            if (inline)
            {
                HashRows(values, width, row, stop, block);
                row = stop;
            }

            for (; row < stop; row++)
            {
                if (allValid || (own.IsValid(row) && wrapper.IsValid(row)))
                {
                    block.Add(SplitBlockBloom.Hash(values.Slice(row * width, width), hash));
                }
            }

            if (checks && GivesUpInBlock(_blockRawStart + (capacity * width)))
            {
                return;
            }
        }
    }

    /// <summary>
    /// Rows <c>[start, stop)</c> of an all-valid fixed-width column, hashed by XxHash3's own short
    /// path for the width, resolved once (docs/11-write-strategy.md §4.1, "hashing, fixed width").
    /// </summary>
    private static void HashRows(ReadOnlySpan<byte> values, int width, int start, int stop, HashSet64 block)
    {
        switch (width)
        {
            case 4:
            {
                ReadOnlySpan<uint> words = MemoryMarshal.Cast<byte, uint>(values)[start..stop];
                foreach (uint word in words)
                {
                    block.Add(XxHash3Fixed.Hash4(word));
                }

                return;
            }

            case 8:
            {
                ReadOnlySpan<ulong> words = MemoryMarshal.Cast<byte, ulong>(values)[start..stop];
                foreach (ulong word in words)
                {
                    block.Add(XxHash3Fixed.Hash8(word));
                }

                return;
            }

            case <= 3:
                for (int row = start; row < stop; row++)
                {
                    block.Add(XxHash3Fixed.Hash1To3(values.Slice(row * width, width)));
                }

                return;

            default:
                for (int row = start; row < stop; row++)
                {
                    block.Add(XxHash3Fixed.Hash9To16(values.Slice(row * width, width)));
                }

                return;
        }
    }

    /// <summary>
    /// `Auto`'s check inside a block: the filter the distinct values seen so far need, against a
    /// bound on the block's raw bytes at its close.
    /// </summary>
    /// <param name="rawBound">The column's raw bytes once the block is full.</param>
    /// <returns>Whether the builder gave up.</returns>
    private bool GivesUpInBlock(long rawBound)
    {
        int distinct = _block.Count;
        if (distinct == 0 || distinct < _policy.MinDistinct)
        {
            return false;
        }

        long projected = Bytes + ((long)SplitBlockBloom.BlocksFor(
            distinct, _policy.FalsePositivePpm, _maxBlocks) * SplitBlockBloom.WordsPerBlock * sizeof(uint));
        if (projected * 1000 <= rawBound * AutoShare)
        {
            return false;
        }

        GiveUp(projected, rawBound);
        return true;
    }

    private void GiveUp(long projected, long raw) =>
        Abandon(
            $"Auto gave it up: {projected} bytes of filters against {raw} raw bytes of column, " +
            $"over its share of {AutoShare}‰ (docs/10-indexes.md §5.5)");

    /// <summary>
    /// Seals the open block: sizes and fills its filter, folds its values into the coarser sets,
    /// and closes the generation on its k-th block.
    /// </summary>
    internal override void CloseBlock()
    {
        int block = _blocks++;
        _blockRawStart = _rawBytes;
        if (Abandoned is not null)
        {
            BlockFilterBlocks.Add(0);
            return;
        }

        int distinct = _block.Count;
        int filterBlocks = 0;
        if (distinct > 0 && distinct >= _policy.MinDistinct)
        {
            // A LEAF IS CLAMPED, as it always was: its bits are the reference's (BloomVectorTests).
            filterBlocks = SplitBlockBloom.BlocksFor(distinct, _policy.FalsePositivePpm, _maxBlocks);
            int words = filterBlocks * SplitBlockBloom.WordsPerBlock;

            // `AUTO` GIVES UP BEFORE BUILDING when the filters would already outweigh their share of
            // the column's RAW bytes: the column compresses to no more than those, so the verdict
            // the chunk would reach is already known, and the filter is never laid out.
            long projected = Bytes + ((long)words * sizeof(uint));
            if (AutoShare > 0 && projected * 1000 > _rawBytes * AutoShare)
            {
                GiveUp(projected, _rawBytes);
                BlockFilterBlocks.Add(0);
                return;
            }

            EnsureWords(ref _generationBlockWords, _generationBlockWordCount + words);
            Span<uint> filter = _generationBlockWords.AsSpan(_generationBlockWordCount, words);
            filter.Clear();
            _block.InsertInto(filter);
            _generationBlockWordCount += words;
        }

        BlockFilterBlocks.Add(filterBlocks);
        Leaves += filterBlocks > 0 ? 1 : 0;
        if (_policy.Resolutions >= 2)
        {
            if (TreeWriter.Generation is { } generation)
            {
                generation.AddAll(_block);
                if (generation.Count > TreeWriter.Capacity && PassesTheCeiling())
                {
                    return;
                }
            }

            if (AutoShare > 0 && block < RepeatBlocks && GivesUpOnRepeats(block, distinct))
            {
                return;
            }
        }

        if (_file is not null)
        {
            _file.AddAll(_block);
            if (_file.Count > FileHashCeiling)
            {
                FileAbandoned = $"the file holds more than {FileHashCeiling} distinct values";
                _file.Dispose();
                _file = null;
            }
        }

        _block.Clear();
        if (block + 1 - _generationFirst == GenerationBlocks)
        {
            CloseGeneration();
        }
    }

    /// <summary>
    /// The open generation holds more values than a node of the ceiling: its node gets no filter.
    /// `Auto` gives the column up when that is its first generation (13 §6.5's symmetric rule): the
    /// probe would read every block's filter, and a sorted run is the structure for such a column.
    /// </summary>
    /// <returns>Whether the builder gave up.</returns>
    private bool PassesTheCeiling()
    {
        TreeWriter.PassGeneration();
        if (AutoShare <= 0 || _generationFirst != _start)
        {
            return false;
        }

        Abandon(
            $"Auto gave it up: its first generation holds more than {TreeWriter.Capacity} distinct values, " +
            $"more than a filter of {_maxBlocks} blocks holds at {_policy.FalsePositivePpm} ppm, so a " +
            "probe would read every block's filter; a sorted run serves such a column (docs/13-dataset.md §6.5)");
        return true;
    }

    /// <summary>
    /// `Auto`'s verdict on a column whose first blocks hold one and the same set: a block filter
    /// then says "maybe" for every value the generation holds, and prunes nothing.
    /// </summary>
    /// <remarks>
    /// The sets are equal exactly when, after each block's fold, the union is as large as the block
    /// and as the first block: no block lacked a value another had. A cycle of seventeen values
    /// over a million rows -- the corpus's `zstd_buffers` -- is this column.
    /// <para>
    /// WHAT STEP 33 MEASURED, and why the rule stays although it is not the whole truth: the LEAVES
    /// prune nothing, as the name says -- on `table_mixed`'s `label`, a present value reads 5 840
    /// bytes of filters and keeps all 123 blocks -- but the ROOT of that same tree answers an
    /// ABSENT value in 180 bytes and prunes all 123, where the bare zone map reads 7 385 628. The
    /// cost of owning that root is not its bytes (6 kB) but the pass that fills it: one hash and
    /// one set insert per row, ~9 ms per million rows. On a column the chooser writes for almost
    /// nothing -- `zstd_buffers`, 630 724 bytes for a million rows -- that pass takes the write
    /// from 5 ms to 14, far past the +10 % 11 §5.3 allows; on `table_mixed`, where five other
    /// columns pay the bill, it does not show at all. A writer cannot tell those two apart without
    /// timing itself, which would make its bytes depend on the machine, so `Auto` keeps the
    /// pessimistic verdict and an explicit <see cref="IndexPolicy"/> Bloom buys the root back at
    /// a measured 9 ms and 6 kB per million rows (docs/10-indexes.md §11).
    /// </para>
    /// </remarks>
    /// <param name="block">The block just sealed.</param>
    /// <param name="distinct">Its distinct count.</param>
    /// <returns>Whether the builder gave up.</returns>
    private bool GivesUpOnRepeats(int block, int distinct)
    {
        if (block == 0)
        {
            _firstDistinct = distinct;
        }

        if (distinct == 0 || distinct != _firstDistinct || (_tree?.GenerationCount ?? 0) != distinct)
        {
            _firstDistinct = -1;
            return false;
        }

        if (block + 1 < RepeatBlocks)
        {
            return false;
        }

        Abandon(
            $"Auto gave it up: its first {RepeatBlocks} blocks hold the same {distinct} values, so its " +
            "block filters prune nothing and the pass that would fill their root costs more than a " +
            "default write may spend; an explicit Bloom policy buys it (docs/10-indexes.md §5.5)");
        _block.Clear();
        return true;
    }

    /// <summary>The first block this builder saw: 0, or an append's boundary.</summary>
    private int _start;

    /// <inheritdoc/>
    internal override void Start(int block, long row)
    {
        _start = block;
        _blocks = block;
        _generationFirst = block;

        // The block-level table is indexed by the file's block; the old blocks' counts are the old
        // entry's, laid over these zeros when the directory merges the two.
        for (int i = 0; i < block; i++)
        {
            BlockFilterBlocks.Add(0);
        }
    }

    /// <summary>
    /// Closes a partial generation at the end of the data, then the tree, whose root takes the
    /// file-level filter when the policy asks for one.
    /// </summary>
    internal override void EndOfData()
    {
        if (_blocks > _generationFirst)
        {
            CloseGeneration();
        }

        HashSet64? file = _file;
        _file = null;
        using (file)
        {
            if (Abandoned is not null)
            {
                _tree?.Abandon();
                return;
            }

            // Over the blocks it hashed: after an append, those since the boundary.
            Tree = _tree?.Finish(_start, file is null ? null : () => FileFilter(file));
        }
    }

    /// <summary>The file-wide filter under the file-level ceiling (10 §5.4), or null with the reason kept.</summary>
    private uint[]? FileFilter(HashSet64 file)
    {
        int distinct = file.Count;
        if (distinct == 0 || distinct < _policy.MinDistinct)
        {
            FileAbandoned = $"the file holds {distinct} distinct values, under the policy's floor of {_policy.MinDistinct}";
            return null;
        }

        int blocks = SplitBlockBloom.BlocksFor(distinct, _policy.FalsePositivePpm, FileMaxBlocks);
        uint[] words = new uint[blocks * SplitBlockBloom.WordsPerBlock];
        file.InsertInto(words);
        return words;
    }

    private void CloseGeneration()
    {
        int first = _generationFirst;
        int count = _blocks - first;
        int union = _tree?.GenerationCount ?? 0;
        if (Abandoned is null && _policy.Resolutions >= 2 && AutoShare > 0 && first == 0
            && count == GenerationBlocks && !(_tree?.GenerationPassed ?? false) && union < _policy.MinDistinct)
        {
            // A FULL FIRST GENERATION UNDER THE FLOOR IS A DICTIONARY'S COLUMN: no block and no
            // generation of it got a filter, the dictionary probe answers its equalities, and
            // hashing the rest of the file would buy nothing.
            Abandon(
                $"Auto gave it up: the first {count} blocks hold {union} distinct values, " +
                $"under the floor of {_policy.MinDistinct} (docs/10-indexes.md §5.5)");
        }

        if (Abandoned is null)
        {
            // NOT COMPRESSED: a filter is uniform bits by construction, and pricing the column's
            // candidates over it is work whose answer is known. The leaves go out before the node
            // that names them.
            PendingPayload? leaves = _generationBlockWordCount > 0
                ? PendingPayload.U32(_generationBlockWords.AsSpan(0, _generationBlockWordCount).ToArray(), compress: false)
                : null;
            if (leaves is not null)
            {
                Enqueue(leaves);
            }

            Runs.Add(new BloomRun(first, count, leaves));
            int[] leafWords = new int[count];
            for (int i = 0; i < count; i++)
            {
                leafWords[i] = BlockFilterBlocks[first + i] * SplitBlockBloom.WordsPerBlock;
            }

            // The tree takes the generation's union with the node, and starts the next one.
            TreeWriter.CloseGeneration(leafWords, leaves);
        }

        _generationFirst = _blocks;
        _generationBlockWordCount = 0;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// A BLOOM ON A SORTED COLUMN IS WASTED (10 §5.5): the zone map already prunes every equality,
    /// block by block, from bounds it holds anyway. The first block is the evidence -- a column whose
    /// first block climbs is taken to be sorted -- because waiting for the file statistics would be
    /// waiting for the end, after every filter had been paid for.
    /// </remarks>
    internal override void FirstBlock(bool? sorted)
    {
        if (AutoShare > 0 && Abandoned is null && sorted == true && !_trigrams)
        {
            Abandon("Auto gave it up: the column is sorted, so its zone map already prunes an equality (docs/10-indexes.md §5.5)");
        }
    }

    /// <inheritdoc/>
    internal override void Abandon(string reason)
    {
        base.Abandon(reason);
        _block.Clear();
        _generationBlockWordCount = 0;
        _file?.Dispose();
        _file = null;
        _tree?.Abandon();
    }

    private static void EnsureWords(ref uint[]? words, int needed)
    {
        if (words is not null && words.Length >= needed)
        {
            return;
        }

        uint[] grown = ArrayPool<uint>.Shared.Rent(Math.Max(needed, (words?.Length ?? 0) * 2));
        if (words is not null)
        {
            words.AsSpan().CopyTo(grown);
            ArrayPool<uint>.Shared.Return(words);
        }

        words = grown;
    }

    /// <inheritdoc/>
    public override void Dispose()
    {
        _block.Dispose();
        _file?.Dispose();
        _file = null;
        _tree?.Dispose();
        if (_generationBlockWords is not null)
        {
            ArrayPool<uint>.Shared.Return(_generationBlockWords);
            _generationBlockWords = null;
        }
    }
}

/// <summary>One generation's leaves.</summary>
/// <param name="FirstBlock">The generation's first block.</param>
/// <param name="BlockCount">Its blocks.</param>
/// <param name="Blocks">The block filters of the generation, concatenated in block order; none when no block has one.</param>
internal sealed record BloomRun(int FirstBlock, int BlockCount, PendingPayload? Blocks);
