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

    /// <summary>The column's running distinct table, or <see langword="null"/>.</summary>
    internal DistinctTable? Table => _column?.Table;

    /// <summary>
    /// The table's distinct count and heap bytes when the chunk's last block closed — the entries
    /// that are this chunk's, as opposed to the carried tail's.
    /// </summary>
    internal (int Distinct, long Heap) TableAtClose =>
        _column is null ? (-1, 0) : _column.TableAtClose(_firstBlock + _blockCount - 1);

    /// <summary>
    /// Whether a table was running when the chunk's last block closed — so a table that cannot
    /// serve is a fallback to count, and not a table plan memory deliberately turned off.
    /// </summary>
    internal bool TableExpected => TableAtClose.Distinct >= 0;

    /// <summary>
    /// Whether the table can answer for a chunk of <paramref name="rows"/> rows: it exists, it
    /// was not abandoned, it has probed at least those rows, and the count at the last block's
    /// close is a prefix of what it holds.
    /// </summary>
    /// <remarks>
    /// THE ONE PREDICATE, used by the chooser to decide and by the writer to count: a fallback the
    /// writer could not see would be a fallback nobody measures, which is how
    /// <c>ChunksWithoutStatistics</c> came to exist.
    /// </remarks>
    /// <param name="rows">The chunk's row count.</param>
    internal bool TableServes(int rows)
    {
        DistinctTable? table = Table;
        (int distinct, _) = TableAtClose;
        return table is { Abandoned: false } && table.Rows >= rows
            && distinct > 0 && distinct <= table.Distinct;
    }

    /// <summary>The column's memory of its last chunk (docs/11 §3.4.3), or none.</summary>
    internal ColumnWriter.PlanMemory? Memory => _column?.Memory;

    /// <summary>
    /// Hands the column what its chunk was encoded as and what that produced, for the next chunk's
    /// memory. An absent cursor remembers nothing, which is what a child a scheme invented gets.
    /// </summary>
    /// <param name="plan">The plan the encoder just wrote.</param>
    /// <param name="actualBytes">The buffer bytes it produced.</param>
    internal void Remember(in ColumnPlan plan, long actualBytes) =>
        _column?.Remember(in plan, actualBytes);

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
