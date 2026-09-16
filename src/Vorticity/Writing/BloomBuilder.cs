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
// A RUN IS WRITTEN WHEN ITS GENERATION CLOSES, not at `CompleteAsync`. 10 §7.2 says both "nothing
// is buffered across chunks" and "runs are written at CompleteAsync"; holding every filter to the
// end is megabytes per column per million rows, so the streaming half wins and the run lands
// between data chunks. A run is a file region no layout references, so where it lies changes
// nothing for any reader (10 §3.2).
using System;
using System.Buffers;
using System.Collections.Generic;
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
    /// <summary>Blocks per generation: 10 §4.3's `k`.</summary>
    internal const int GenerationBlocks = 16;

    /// <summary>The file-level filter's own ceiling, 10 §5.4: 1 MiB blocks, 32 MiB.</summary>
    internal const int FileMaxBlocks = 1 << 20;

    /// <summary>Distinct values the file-level resolution may hold before it gives up.</summary>
    internal const int FileHashCeiling = 1 << 22;

    /// <summary>Rows between two of `Auto`'s checks inside a block.</summary>
    private const int CheckStride = 256;

    /// <summary>The blocks `Auto` watches for a column that repeats one set.</summary>
    private const int RepeatBlocks = 4;

    private readonly IndexPolicy _policy;
    private int _firstDistinct;
    private long _blockRawStart;
    private readonly HashSet64 _block = new HashSet64();
    private readonly HashSet64? _generation;
    private HashSet64? _file;

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
        _trigrams = policy.Kind == IndexPolicyKind.NgramBloom;
        _generation = policy.Resolutions >= 2 ? new HashSet64() : null;
        _file = policy.Resolutions >= 3 ? new HashSet64() : null;
    }

    /// <summary>The kind this builder writes.</summary>
    internal string Kind => _trigrams ? IndexKinds.BloomNgram3 : IndexKinds.BloomSbbf;

    /// <inheritdoc/>
    /// <remarks>The open generation's block filters: uncompressed, so their size is their size.</remarks>
    protected override long OpenBytes => (long)_generationBlockWordCount * sizeof(uint);

    /// <summary>
    /// Rows per block, which bounds a block's raw bytes before it closes, so that `Auto` can give up
    /// inside the first block; 0 when blocks are the caller's batches and have no fixed length.
    /// </summary>
    internal int BlockRows { get; init; }

    /// <summary>Why the file-level filter was dropped, when it was asked for and could not be kept.</summary>
    internal string? FileAbandoned { get; private set; }

    /// <summary>Filter blocks per closed block, in block order: 0 where a block got no filter.</summary>
    internal List<int> BlockFilterBlocks { get; } = [];

    /// <summary>Every generation closed, written or not, in block order.</summary>
    internal List<BloomRun> Runs { get; } = [];

    /// <summary>The file-level filter, once <see cref="EndOfData"/> built it.</summary>
    internal BloomRun? File { get; private set; }

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
                Fixed(node.Values.Span, node.PType.ByteWidth(), start, count, allValid, own, wrapper, hash);
                return;

            case CanonicalKind.Decimal:
                Fixed(node.Values.Span, DecimalStorage.ByteWidth(node.Storage), start, count, allValid, own, wrapper, hash);
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
    /// reached here is the one the block's close would reach, a block's hashing earlier.
    /// </summary>
    private void Fixed(
        ReadOnlySpan<byte> values, int width, int start, int count, bool allValid,
        ValidityMask own, ValidityMask wrapper, BloomHash hash)
    {
        _rawBytes += (long)count * width;
        HashSet64 block = _block;
        bool checks = AutoShare > 0 && BlockRows > 0;
        int end = start + count;
        int row = start;
        while (row < end)
        {
            int stop = Math.Min(end, row + CheckStride);
            for (; row < stop; row++)
            {
                if (allValid || (own.IsValid(row) && wrapper.IsValid(row)))
                {
                    block.Add(SplitBlockBloom.Hash(values.Slice(row * width, width), hash));
                }
            }

            if (checks && GivesUpInBlock(_blockRawStart + ((long)BlockRows * width)))
            {
                return;
            }
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
            distinct, _policy.FalsePositivePpm, _policy.MaxBlocks) * SplitBlockBloom.WordsPerBlock * sizeof(uint));
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
            filterBlocks = SplitBlockBloom.BlocksFor(distinct, _policy.FalsePositivePpm, _policy.MaxBlocks);
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
        if (_generation is not null)
        {
            _generation.AddAll(_block);
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
    /// `Auto`'s verdict on a column whose first blocks hold one and the same set: a block filter
    /// then says "maybe" for every value the generation holds, and prunes nothing.
    /// </summary>
    /// <remarks>
    /// The sets are equal exactly when, after each block's fold, the union is as large as the block
    /// and as the first block: no block lacked a value another had. A cycle of seventeen values
    /// over a million rows -- the corpus's `zstd_buffers` -- is this column, and its filter cost
    /// the write +37 %.
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

        if (distinct == 0 || distinct != _firstDistinct || _generation!.Count != distinct)
        {
            _firstDistinct = -1;
            return false;
        }

        if (block + 1 < RepeatBlocks)
        {
            return false;
        }

        Abandon(
            $"Auto gave it up: its first {RepeatBlocks} blocks hold the same {distinct} values, so a " +
            "block filter prunes nothing (docs/10-indexes.md §5.5)");
        _block.Clear();
        return true;
    }

    /// <summary>Closes a partial generation at the end of the data, and builds the file-level filter.</summary>
    internal override void EndOfData()
    {
        if (_blocks > _generationFirst)
        {
            CloseGeneration();
        }

        HashSet64? file = _file;
        _file = null;
        if (file is null || Abandoned is not null)
        {
            file?.Dispose();
            return;
        }

        using (file)
        {
            int distinct = file.Count;
            if (distinct == 0 || distinct < _policy.MinDistinct)
            {
                FileAbandoned = $"the file holds {distinct} distinct values, under the policy's floor of {_policy.MinDistinct}";
                return;
            }

            int blocks = SplitBlockBloom.BlocksFor(distinct, _policy.FalsePositivePpm, FileMaxBlocks);
            uint[] words = new uint[blocks * SplitBlockBloom.WordsPerBlock];
            file.InsertInto(words);
            File = new BloomRun(0, _blocks, null, PendingPayload.U32(words, compress: false), blocks);
            Enqueue(File.Generation!);
        }
    }

    private void CloseGeneration()
    {
        int first = _generationFirst;
        int count = _blocks - first;
        uint[] blockWords = _generationBlockWords is null
            ? []
            : _generationBlockWords.AsSpan(0, _generationBlockWordCount).ToArray();
        uint[] generationWords = [];
        int generationBlocks = 0;
        if (Abandoned is null && _generation is not null && AutoShare > 0 && first == 0
            && count == GenerationBlocks && _generation.Count < _policy.MinDistinct)
        {
            // A FULL FIRST GENERATION UNDER THE FLOOR IS A DICTIONARY'S COLUMN: no block and no
            // generation of it got a filter, the dictionary probe answers its equalities, and
            // hashing the rest of the file would buy nothing.
            Abandon(
                $"Auto gave it up: the first {count} blocks hold {_generation.Count} distinct values, " +
                $"under the floor of {_policy.MinDistinct} (docs/10-indexes.md §5.5)");
        }

        if (Abandoned is null && _generation is not null)
        {
            int distinct = _generation.Count;
            if (distinct > 0 && distinct >= _policy.MinDistinct)
            {
                generationBlocks = SplitBlockBloom.BlocksFor(distinct, _policy.FalsePositivePpm, _policy.MaxBlocks);
                generationWords = new uint[generationBlocks * SplitBlockBloom.WordsPerBlock];
                _generation.InsertInto(generationWords);
            }

            // A generation's set is as large as sixteen blocks; the next one starts small again.
            _generation.Shrink();
        }

        if (Abandoned is null)
        {
            // NOT COMPRESSED: a filter is uniform bits by construction, and pricing the column's
            // candidates over it is work whose answer is known.
            BloomRun run = new BloomRun(
                first, count,
                blockWords.Length > 0 ? PendingPayload.U32(blockWords, compress: false) : null,
                generationWords.Length > 0 ? PendingPayload.U32(generationWords, compress: false) : null,
                generationBlocks);
            Runs.Add(run);
            if (run.Blocks is { } blocks)
            {
                Enqueue(blocks);
            }

            if (run.Generation is { } generation)
            {
                Enqueue(generation);
            }
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
        _generation?.Dispose();
        _file?.Dispose();
        _file = null;
        if (_generationBlockWords is not null)
        {
            ArrayPool<uint>.Shared.Return(_generationBlockWords);
            _generationBlockWords = null;
        }
    }
}

/// <summary>One generation's filters.</summary>
/// <param name="FirstBlock">The generation's first block.</param>
/// <param name="BlockCount">Its blocks.</param>
/// <param name="Blocks">The block filters of the generation, concatenated in block order; none when no block has one.</param>
/// <param name="Generation">The generation's own filter, or the file's; none when there is none.</param>
/// <param name="GenerationBlocks">That filter's 256-bit blocks.</param>
internal sealed record BloomRun(
    int FirstBlock, int BlockCount, PendingPayload? Blocks, PendingPayload? Generation, int GenerationBlocks);
