// Where one node of one chunk gets its ingest statistics, and how to reach a child's.
//
// `ArrayBlobWriter` walks the CANONICAL tree; `ColumnWriter` is a tree of the same shape built at
// ingest (docs/11-write-strategy.md §3.0). This is the cursor that keeps the two in step as the
// writer descends, so that `ColumnCompressor.Choose` is handed the summary of the column it is
// actually looking at rather than only the one at the top.
//
// IT IS A CURSOR AND NOT A VALUE because the merge is per (column, chunk): the block range is the
// chunk's and never changes on the way down -- a struct's fields and an extension's storage are
// row-aligned with their parent, so they share its blocks exactly -- while the column changes at
// every step. Merging at each level costs a loop over the chunk's blocks, a few dozen additions.
//
// AN ABSENT CURSOR IS THE SAFE ANSWER, and every path that cannot answer takes it: a child a scheme
// invented (a dictionary's values, ALP's integers), a list's elements, a shape that disagrees with
// the first batch's. `Choose` then measures the column itself, which is what it did before any of
// this existed.
using System;

namespace Vorticity.Writing;

/// <summary>A position in the ingest statistics, for one chunk, that can descend to a child.</summary>
internal readonly struct ChunkStats
{
    private readonly ColumnWriter? _column;
    private readonly int _firstBlock;
    private readonly int _blockCount;

    /// <summary>Points at <paramref name="column"/> over the chunk's block range.</summary>
    /// <param name="column">The column's ingest state, or <see langword="null"/> for none.</param>
    /// <param name="firstBlock">The chunk's first block.</param>
    /// <param name="blockCount">How many blocks the chunk covers.</param>
    internal ChunkStats(ColumnWriter? column, int firstBlock, int blockCount)
    {
        _column = column;
        _firstBlock = firstBlock;
        _blockCount = blockCount;
    }

    /// <summary>This column's summary over the chunk, or an absent one.</summary>
    internal BlockStats Stats =>
        _column is null ? default : _column.Chunk(_firstBlock, _blockCount);

    /// <summary>
    /// The chunk's pair of bit-width histograms, when every block of it carries them.
    /// </summary>
    /// <param name="destination">Receives the sum; must hold <see cref="BitPackWidths.Length"/>.</param>
    /// <returns>Whether the histograms were available.</returns>
    internal bool Widths(Span<int> destination) =>
        _column is not null && _column.Widths(_firstBlock, _blockCount, destination);

    /// <summary>
    /// The cursor for field <paramref name="index"/>, which covers the same blocks because a
    /// struct's fields and an extension's storage are row-aligned with their parent.
    /// </summary>
    /// <param name="index">The field index, in the canonical node's own order.</param>
    internal ChunkStats Field(int index) =>
        _column is null
            ? default
            : new ChunkStats(_column.Field(index), _firstBlock, _blockCount);
}
