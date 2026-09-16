// The live blocks of one scan - docs/11-write-strategy.md §6.1, the read contract.
//
// ONE OPERATION FOR EVERY PRUNING STRUCTURE: refine this mask. The scan builds it once per query,
// a bit per block of the file's row space (123 bits for a million rows, 15 KiB for a billion), and
// runs the pruners over it cheapest first -- the zone map today, the Bloom generations, postings and
// exact indexes of docs/10-indexes.md through the same `IBlockPruner.Refine` when they exist --
// stopping as soon as nothing is live. Every split then asks one question of it, `AnyLive`, where
// it used to ask every pruner in turn.
//
// A BLOCK IS THE ZONE MAP'S ZONE, and the writer's row block: `SplitPlan.NaturalBatchRows`, 8 192
// rows by default. Blocks are aligned from row 0 of the file, whatever the chunks are, which is
// what lets a chunk that is not zone-aligned (a foreign writer's) still be answered: a range that
// straddles two blocks is live when either is, exactly what `ZonePruner.MayMatch(RowRange)` said
// about the zones it overlapped.
//
// Only a positive proof kills a block (docs/08-semantics.md §1: "pruning may never eliminate a row
// that full materialization would have returned"), and nothing here revives one: a pruner can only
// clear bits, so the order the pruners run in changes the work and never the answer.
using System;
using System.Numerics;
using Vorticity.File;

namespace Vorticity.Compute;

/// <summary>One bit per block of a file's rows: the blocks a scan still has to read.</summary>
internal sealed class BlockMask
{
    private readonly ulong[] _bits;

    /// <summary>A mask over <paramref name="rowCount"/> rows, every block live.</summary>
    /// <param name="rowCount">The file's rows, at the layout root.</param>
    /// <param name="blockRows">Rows per block; must be positive.</param>
    internal BlockMask(long rowCount, long blockRows)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(rowCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(blockRows);

        RowCount = rowCount;
        BlockRows = blockRows;
        long blocks = (rowCount + blockRows - 1) / blockRows;
        BlockCount = checked((int)blocks);

        _bits = new ulong[(BlockCount + 63) / 64];
        _bits.AsSpan().Fill(ulong.MaxValue);

        // The bits past the last block stay clear, so that `LiveCount` is a popcount and nothing
        // else, and no caller can be told a block beyond the file is live.
        int tail = BlockCount & 63;
        if (tail != 0)
        {
            _bits[^1] = ulong.MaxValue >> (64 - tail);
        }
    }

    /// <summary>The rows the mask covers.</summary>
    internal long RowCount { get; }

    /// <summary>Rows per block, the last block excepted.</summary>
    internal long BlockRows { get; }

    /// <summary>How many blocks the rows make, the last one short unless the count divides.</summary>
    internal int BlockCount { get; }

    /// <summary>How many blocks are still live.</summary>
    internal int LiveCount
    {
        get
        {
            int live = 0;
            for (int i = 0; i < _bits.Length; i++)
            {
                live += BitOperations.PopCount(_bits[i]);
            }

            return live;
        }
    }

    /// <summary>Whether nothing is left to read.</summary>
    internal bool IsEmpty => LiveCount == 0;

    /// <summary>Whether block <paramref name="block"/> may still hold a matching row.</summary>
    /// <param name="block">A block index below <see cref="BlockCount"/>.</param>
    internal bool IsLive(int block) =>
        (uint)block < (uint)BlockCount && (_bits[block >> 6] & (1UL << (block & 63))) != 0;

    /// <summary>Marks block <paramref name="block"/> as proven to hold no matching row.</summary>
    /// <param name="block">A block index below <see cref="BlockCount"/>.</param>
    internal void Kill(int block)
    {
        if ((uint)block < (uint)BlockCount)
        {
            _bits[block >> 6] &= ~(1UL << (block & 63));
        }
    }

    /// <summary>
    /// Leaves live exactly the blocks <paramref name="rows"/> overlaps, whatever was live before.
    /// </summary>
    /// <param name="rows">The rows about to be decoded, in file coordinates.</param>
    /// <remarks>
    /// NOT A PRUNER'S OPERATION -- a pruner only clears bits (docs/11 §6.1). This is a consumer's:
    /// a terminal that decodes one split of a chunk hands the readers a mask that says so, and the
    /// restricted decode of step 8b materializes that split rather than the chunk, which on a
    /// single-chunk file is the difference between one block and the whole column.
    /// </remarks>
    internal void KeepOnly(RowRange rows)
    {
        Array.Clear(_bits);
        if (rows.IsEmpty || BlockCount == 0)
        {
            return;
        }

        long first = Math.Min(rows.Start / BlockRows, BlockCount - 1);
        long last = Math.Min((rows.End - 1) / BlockRows, BlockCount - 1);
        for (long block = first; block <= last; block++)
        {
            _bits[block >> 6] |= 1UL << (int)(block & 63);
        }
    }

    /// <summary>The rows of block <paramref name="block"/>, the last one clipped to the file.</summary>
    /// <param name="block">A block index below <see cref="BlockCount"/>.</param>
    internal RowRange BlockRange(int block)
    {
        long start = Math.Min((long)block * BlockRows, RowCount);
        long end = Math.Min(start + BlockRows, RowCount);
        return new RowRange(start, end);
    }

    /// <summary>
    /// Whether some block overlapping <paramref name="rows"/> is dead -- the question a layout
    /// reader asks of a chunk before deciding whether to decode it whole or block by block.
    /// </summary>
    /// <param name="rows">A row range in the file's coordinates; rows past the end are ignored.</param>
    internal bool HasDeadBlocks(RowRange rows)
    {
        long end = Math.Min(rows.End, RowCount);
        if (rows.Start >= end)
        {
            return false;
        }

        int first = (int)(rows.Start / BlockRows);
        int last = (int)((end - 1) / BlockRows);
        for (int block = first; block <= last; block++)
        {
            if (!IsLive(block))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether any block overlapping <paramref name="rows"/> is live -- the question a split asks
    /// before it is read.
    /// </summary>
    /// <param name="rows">A row range in the file's coordinates; rows past the end are ignored.</param>
    internal bool AnyLive(RowRange rows)
    {
        long end = Math.Min(rows.End, RowCount);
        if (rows.Start >= end)
        {
            return false;
        }

        int first = (int)(rows.Start / BlockRows);
        int last = (int)((end - 1) / BlockRows);
        for (int block = first; block <= last; block++)
        {
            if (IsLive(block))
            {
                return true;
            }
        }

        return false;
    }
}
