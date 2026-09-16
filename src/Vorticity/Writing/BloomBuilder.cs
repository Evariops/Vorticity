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
internal sealed class BloomBuilder : IDisposable
{
    /// <summary>Blocks per generation: 10 §4.3's `k`.</summary>
    internal const int GenerationBlocks = 16;

    /// <summary>The file-level filter's own ceiling, 10 §5.4: 1 MiB blocks, 32 MiB.</summary>
    internal const int FileMaxBlocks = 1 << 20;

    /// <summary>Distinct values the file-level resolution may hold before it gives up.</summary>
    internal const int FileHashCeiling = 1 << 22;

    private readonly IndexPolicy _policy;
    private readonly HashSet64 _block = new HashSet64();
    private readonly HashSet64? _generation;
    private HashSet64? _file;

    private int _generationFirst;
    private uint[]? _generationBlockWords;
    private int _generationBlockWordCount;
    private int _blocks;

    /// <param name="policy">A <see cref="IndexPolicyKind.Bloom"/> policy.</param>
    internal BloomBuilder(IndexPolicy policy)
    {
        _policy = policy;
        _generation = policy.Resolutions >= 2 ? new HashSet64() : null;
        _file = policy.Resolutions >= 3 ? new HashSet64() : null;
    }

    /// <summary>Why the whole index was dropped; <see langword="null"/> while it lives.</summary>
    internal string? Abandoned { get; private set; }

    /// <summary>Why the file-level filter was dropped, when it was asked for and could not be kept.</summary>
    internal string? FileAbandoned { get; private set; }

    /// <summary>Filter blocks per closed block, in block order: 0 where a block got no filter.</summary>
    internal List<int> BlockFilterBlocks { get; } = [];

    /// <summary>Generations closed and waiting for their payloads to be written.</summary>
    internal Queue<BloomRun> Pending { get; } = new Queue<BloomRun>();

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
    internal static bool Supports(DType dtype, out string? reason)
    {
        while (dtype.Kind == DTypeKind.Extension)
        {
            dtype = dtype.StorageType;
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
    internal void Accumulate(CanonicalArena arena, int nodeIndex, int start, int count)
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
        switch (node.Kind)
        {
            case CanonicalKind.Primitive:
                Fixed(block, node.Values.Span, node.PType.ByteWidth(), start, count, allValid, own, wrapper, hash);
                return;

            case CanonicalKind.Decimal:
                Fixed(block, node.Values.Span, DecimalStorage.ByteWidth(node.Storage), start, count, allValid, own, wrapper, hash);
                return;

            case CanonicalKind.Constant:
                for (int row = start; row < start + count; row++)
                {
                    if (own.IsValid(row) && wrapper.IsValid(row))
                    {
                        block.Add(SplitBlockBloom.Hash(node.ConstantElement, hash));
                        return;
                    }
                }

                return;

            case CanonicalKind.VarBinView:
                for (int row = start; row < start + count; row++)
                {
                    if (allValid || (own.IsValid(row) && wrapper.IsValid(row)))
                    {
                        block.Add(SplitBlockBloom.Hash(LiteralReader.ViewAt(node, row), hash));
                    }
                }

                return;

            default:
                Abandon($"a Bloom filter does not index a {node.Kind} column");
                return;
        }
    }

    private static void Fixed(
        HashSet64 block, ReadOnlySpan<byte> values, int width, int start, int count, bool allValid,
        ValidityMask own, ValidityMask wrapper, BloomHash hash)
    {
        if (allValid)
        {
            ReadOnlySpan<byte> window = values.Slice(start * width, count * width);
            for (int offset = 0; offset < window.Length; offset += width)
            {
                block.Add(SplitBlockBloom.Hash(window.Slice(offset, width), hash));
            }

            return;
        }

        for (int row = start; row < start + count; row++)
        {
            if (own.IsValid(row) && wrapper.IsValid(row))
            {
                block.Add(SplitBlockBloom.Hash(values.Slice(row * width, width), hash));
            }
        }
    }

    /// <summary>
    /// Seals the open block: sizes and fills its filter, folds its values into the coarser sets,
    /// and closes the generation on its k-th block.
    /// </summary>
    internal void CloseBlock()
    {
        int block = _blocks++;
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
            EnsureWords(ref _generationBlockWords, _generationBlockWordCount + words);
            Span<uint> filter = _generationBlockWords.AsSpan(_generationBlockWordCount, words);
            filter.Clear();
            _block.InsertInto(filter);
            _generationBlockWordCount += words;
        }

        BlockFilterBlocks.Add(filterBlocks);
        _generation?.AddAll(_block);
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

    /// <summary>Closes a partial generation at the end of the data, and builds the file-level filter.</summary>
    internal void EndOfData()
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
            File = new BloomRun(0, _blocks, [], words, blocks);
            Pending.Enqueue(File);
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
            BloomRun run = new BloomRun(first, count, blockWords, generationWords, generationBlocks);
            Runs.Add(run);
            Pending.Enqueue(run);
        }

        _generationFirst = _blocks;
        _generationBlockWordCount = 0;
    }

    /// <summary>Drops the whole index, with its reason; what was written stays dead weight.</summary>
    /// <param name="reason">Why.</param>
    internal void Abandon(string reason)
    {
        Abandoned ??= reason;
        Pending.Clear();
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
    public void Dispose()
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

/// <summary>One generation's filters, and where their payloads landed.</summary>
/// <param name="FirstBlock">The generation's first block.</param>
/// <param name="BlockCount">Its blocks.</param>
/// <param name="BlockWords">The block filters of the generation, concatenated in block order.</param>
/// <param name="GenerationWords">The generation's own filter; empty when it has none.</param>
/// <param name="GenerationBlocks">That filter's 256-bit blocks.</param>
internal sealed record BloomRun(
    int FirstBlock, int BlockCount, uint[] BlockWords, uint[] GenerationWords, int GenerationBlocks)
{
    /// <summary>Where <see cref="BlockWords"/> was written.</summary>
    internal IndexSegment? BlockSegment { get; set; }

    /// <summary>Where <see cref="GenerationWords"/> was written.</summary>
    internal IndexSegment? GenerationSegment { get; set; }
}
