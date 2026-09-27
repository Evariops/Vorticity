using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Compute;
using Vorticity.Indexes;
using Vorticity.Types;
using Vorticity.Types.Numerics;

namespace Vorticity.Writing;

/// <summary>
/// Builds one column's split-block Bloom filters, block by block, from the row ranges the ingest
/// hands it while the batch's arena is live: each valid row is hashed into the open block's set,
/// which is also its exact distinct count, and nothing is ever re-read.
/// </summary>
/// <remarks>
/// A block's set is folded into the generation's and, when the policy asks for it, into a file-wide
/// one, so every level is sized from an exact union rather than from the sum of its children's
/// counts, which would over-size by their overlap. Filters leave as soon as their generation closes
/// rather than at completion, because holding a column's filters to the end of the file would cost
/// more memory than the writer may spend; where the regions land is invisible to a reader, since no
/// layout references them.
/// </remarks>
internal sealed class BloomBuilder : IndexBuilder
{
    /// <summary>Blocks per generation, which is the tree's fanout and not a free parameter.</summary>
    /// <remarks>
    /// How many blocks a generation covers and how many children a node takes are one number: the
    /// fanout is written into every entry's options and a reader rejects a file whose fanout is not
    /// its own, and generations that do not line up with the nodes give filters that prune nothing.
    /// Moving it is a format version and a code change, not a default to tune.
    /// </remarks>
    internal const int GenerationBlocks = BloomIndexOptions.Fanout;

    /// <summary>The file-level filter's own ceiling, in filter blocks.</summary>
    internal const int FileMaxBlocks = 1 << 20;

    /// <summary>Distinct values the file-level resolution may hold before it gives up.</summary>
    internal const int FileHashCeiling = 1 << 22;

    /// <summary>Rows between two of `Auto`'s checks inside a block.</summary>
    private const int CheckStride = 256;

    /// <summary>Values hashed together before their hashes go to the set: a quarter of <see cref="CheckStride"/>.</summary>
    private const int HashBatch = 64;

    /// <summary>The blocks `Auto` watches for a column that repeats one set.</summary>
    private const int RepeatBlocks = 4;

    private readonly IndexSpec _policy;
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
    internal BloomBuilder(IndexSpec policy)
    {
        _policy = policy;
        _maxBlocks = MaxBlocksOf(policy);
        _trigrams = policy.Kind == IndexPolicyKind.NgramBloom;
        _file = policy.Resolutions >= 3 ? new HashSet64() : null;
    }

    /// <summary>A policy's ceiling, within what a reader takes (<see cref="BloomBuilderLimits.MaxFilterBlocks"/>).</summary>
    /// <param name="policy">The policy.</param>
    internal static int MaxBlocksOf(IndexSpec policy) =>
        (int)Math.Min((uint)policy.MaxBlocks, BloomBuilderLimits.MaxFilterBlocks);

    /// <summary>The kind this builder writes.</summary>
    internal string Kind => _trigrams ? IndexKinds.BloomNgram3 : IndexKinds.BloomSbbf;

    /// <remarks>
    /// The open generation's block filters and the node filters waiting for their level to go out.
    /// They are uncompressed, so what they take in memory is what they will take in the file.
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

    /// <summary>The tree, once <see cref="EndOfDataAsync"/> finished it.</summary>
    internal BloomTree? Tree { get; private set; }

    /// <summary>The blocks, since the builder's first, whose filter was built.</summary>
    internal int Leaves { get; private set; }

    /// <summary>The policy this builder serves.</summary>
    internal IndexSpec Policy => _policy;

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
            // A list is indexed through its elements, each into the block of the row that holds it.
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
    /// A list column's rows: each element of a valid row, into the row's block, so the filter
    /// answers "does any element of a row in this block equal v".
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
    /// path for the width, resolved once for the whole range instead of per row.
    /// </summary>
    /// <remarks>
    /// Four- and eight-byte values are hashed <see cref="HashBatch"/> at a time where
    /// <see cref="XxHash3Fixed.IsVectorized"/>, eight an instruction, and the batch then added to
    /// the set, whose probe is the one part left a value at a time.
    /// </remarks>
    [SkipLocalsInit]
    internal static void HashRows(ReadOnlySpan<byte> values, int width, int start, int stop, HashSet64 block)
    {
        Span<ulong> hashes = stackalloc ulong[HashBatch];
        switch (width)
        {
            case 4:
            {
                ReadOnlySpan<uint> words = MemoryMarshal.Cast<byte, uint>(values)[start..stop];
                int i = 0;
                if (XxHash3Fixed.IsVectorized)
                {
                    for (; i <= words.Length - HashBatch; i += HashBatch)
                    {
                        XxHash3Fixed.Hash4(words.Slice(i, HashBatch), hashes);
                        foreach (ulong hash in hashes)
                        {
                            block.Add(hash);
                        }
                    }
                }

                foreach (uint word in words[i..])
                {
                    block.Add(XxHash3Fixed.Hash4(word));
                }

                return;
            }

            case 8:
            {
                ReadOnlySpan<ulong> words = MemoryMarshal.Cast<byte, ulong>(values)[start..stop];
                int i = 0;
                if (XxHash3Fixed.IsVectorized)
                {
                    for (; i <= words.Length - HashBatch; i += HashBatch)
                    {
                        XxHash3Fixed.Hash8(words.Slice(i, HashBatch), hashes);
                        foreach (ulong hash in hashes)
                        {
                            block.Add(hash);
                        }
                    }
                }

                foreach (ulong word in words[i..])
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
        if (distinct == 0 || distinct < _policy.MinDistinct || Committed)
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
            $"over its share of {AutoShare}‰. An explicit Bloom policy overrides the share");

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
            // A leaf is clamped to the ceiling rather than dropped, so its bits stay those of the
            // reference implementation.
            filterBlocks = SplitBlockBloom.BlocksFor(distinct, _policy.FalsePositivePpm, _maxBlocks);
            int words = filterBlocks * SplitBlockBloom.WordsPerBlock;

            // `Auto` gives up before building when the filters would already outweigh their share
            // of the column's raw bytes: the column compresses to no more than those, so the
            // verdict the chunk would reach is already known and the filter is never laid out.
            long projected = Bytes + ((long)words * sizeof(uint));
            if (AutoShare > 0 && !Committed && projected * 1000 > _rawBytes * AutoShare)
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
    /// `Auto` gives the column up when that is its first generation, because a probe would then
    /// have to read every block's filter, and a sorted run serves such a column instead.
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
            "probe would read every block's filter; a sorted-runs index serves such a column");
        return true;
    }

    /// <summary>
    /// `Auto`'s verdict on a column whose first blocks hold one and the same set: a block filter
    /// then says "maybe" for every value the generation holds, and prunes nothing.
    /// </summary>
    /// <remarks>
    /// The sets are equal exactly when, after each block's fold, the union is as large as the block
    /// and as the first block: no block lacked a value another had. A column cycling through a
    /// handful of values over a whole file is the case this catches.
    /// <para>
    /// The verdict is deliberately pessimistic, and it is not the whole truth: the leaves of such a
    /// column prune nothing, but the root of its tree still prunes the whole file for a value that
    /// is absent everywhere. What that root costs is not its bytes but the pass that fills it, one
    /// hash and one set insert per row, which is invisible on a wide table and doubles the write of
    /// a column the chooser already encodes for almost nothing. A writer cannot tell those two
    /// apart without timing itself, which would make the bytes it produces depend on the machine,
    /// so it declines here and an explicit <see cref="IndexSpec"/> Bloom buys the root back.
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
            "default write may spend; an explicit Bloom policy buys it");
        _block.Clear();
        return true;
    }

    /// <summary>The first block this builder saw: 0, or an append's boundary.</summary>
    private int _start;

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
    /// file-level filter when the policy asks for one; all of it in memory, so it completes at once.
    /// </summary>
    internal override ValueTask EndOfDataAsync(CancellationToken cancellationToken)
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
            }
            else
            {
                // Over the blocks it hashed: after an append, those since the boundary.
                Tree = _tree?.Finish(_start, file is null ? null : () => FileFilter(file));
            }
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>The file-wide filter under the file-level ceiling, or null with the reason kept.</summary>
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
            // A full first generation under the floor is a dictionary's column: no block and no
            // generation of it got a filter, the dictionary probe answers its equalities, and
            // hashing the rest of the file would buy nothing.
            Abandon(
                $"Auto gave it up: the first {count} blocks hold {union} distinct values, " +
                $"under the floor of {_policy.MinDistinct} the policy asks before a filter pays");
        }

        if (Abandoned is null)
        {
            // Left uncompressed: a filter is uniform bits by construction, so pricing the column's
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

    /// <remarks>
    /// A Bloom filter on a sorted column is wasted: the zone map already prunes every equality,
    /// block by block, from bounds it holds anyway. The first block is the evidence, a column whose
    /// first block climbs being taken for sorted, because waiting for the file's statistics would
    /// mean waiting for the end, once every filter had been paid for.
    /// </remarks>
    internal override void FirstBlock(bool? sorted)
    {
        if (AutoShare > 0 && Abandoned is null && sorted == true && !_trigrams)
        {
            Abandon("Auto gave it up: the column is sorted, so its zone map already prunes an equality");
        }
    }

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
