using System;

namespace Vorticity.Writing;

/// <summary>What the writer counts about the cursors it hands out.</summary>
internal interface IChunkLedger
{
    /// <summary>Whether a list's elements may be summarized from their blocks at all.</summary>
    bool ElementsServe { get; }

    /// <summary>Records that a list chunk's elements had to be counted directly.</summary>
    void ElementsUnserved();

    /// <summary>Whether a column is priced by its bytes alone, decode speed aside.</summary>
    bool SizeFirst { get; }
}

/// <summary>
/// A position in the ingest statistics, for one chunk, that can descend to a child: the blob writer
/// walks the canonical tree while the ingest state is a tree of the same shape, and this cursor
/// keeps the two in step so the chooser is handed the summary of the column it is looking at rather
/// than only the one at the top.
/// </summary>
/// <remarks>
/// It is a cursor rather than a value because the block range is the chunk's and never changes on
/// the way down, while the column changes at every step. An absent cursor is the safe answer, and
/// every path that cannot answer takes it -- a child a scheme invented, a list whose element ranges
/// do not abut, a shape that disagrees with the first batch's -- because the chooser then measures
/// the column itself.
/// </remarks>
internal readonly struct ChunkStats
{
    private readonly ColumnWriter? _column;
    private readonly int _firstBlock;
    private readonly int _blockCount;
    private readonly IChunkLedger? _ledger;
    private readonly bool _dry;

    /// <summary>Points at <paramref name="column"/> over the chunk's block range.</summary>
    /// <param name="column">The column's ingest state, or <see langword="null"/> for none.</param>
    /// <param name="firstBlock">The chunk's first block.</param>
    /// <param name="blockCount">How many blocks the chunk covers.</param>
    /// <param name="ledger">Where the misses of a list's elements are counted, or none.</param>
    internal ChunkStats(ColumnWriter? column, int firstBlock, int blockCount, IChunkLedger? ledger = null)
        : this(column, firstBlock, blockCount, ledger, dry: false)
    {
    }

    private ChunkStats(ColumnWriter? column, int firstBlock, int blockCount, IChunkLedger? ledger, bool dry)
    {
        _column = column;
        _firstBlock = firstBlock;
        _blockCount = blockCount;
        _ledger = ledger;
        _dry = dry;
    }

    /// <summary>
    /// The same statistics for a write that is only measured: priced under the rules every profile
    /// but size first applies, and leaving the column's memory and counters as they were.
    /// </summary>
    internal ChunkStats Dry() => new ChunkStats(_column, _firstBlock, _blockCount, ledger: null, dry: true);

    /// <summary>Whether this is a <see cref="Dry"/> chunk: one priced as every profile but size first prices it.</summary>
    internal bool IsDry => _dry;

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
    /// This is the one predicate, used by the chooser to decide and by the writer to count, so that
    /// the two never disagree: a fallback the writer could not see would be a fallback nobody
    /// measures.
    /// </remarks>
    /// <param name="rows">The chunk's row count.</param>
    internal bool TableServes(int rows)
    {
        DistinctTable? table = Table;
        (int distinct, _) = TableAtClose;
        return table is { Abandoned: false } && table.Rows >= rows
            && distinct > 0 && distinct <= table.Distinct;
    }

    /// <summary>The column's memory of its last chunk, or none; none for a dry cursor, which prices in full.</summary>
    internal ColumnWriter.PlanMemory? Memory => _dry ? null : _column?.Memory;

    /// <summary>Whether the file asks for its columns priced by their bytes alone.</summary>
    internal bool SizeFirst => _ledger is { SizeFirst: true };

    /// <summary>Tells the column its bit-packing was priced from the ingested widths.</summary>
    internal void NoteWidthsServed()
    {
        if (!_dry)
        {
            _column?.NoteWidthsServed();
        }
    }

    /// <summary>
    /// Hands the column what its chunk was encoded as and what that produced, for the next chunk's
    /// memory. An absent cursor remembers nothing, which is what a child a scheme invented gets,
    /// and neither does a dry one.
    /// </summary>
    /// <param name="plan">The plan the encoder just wrote.</param>
    /// <param name="actualBytes">The buffer bytes it produced.</param>
    internal void Remember(in ColumnPlan plan, long actualBytes)
    {
        if (!_dry)
        {
            _column?.Remember(in plan, actualBytes, _firstBlock, _blockCount);
        }
    }

    /// <summary>
    /// The cursor for field <paramref name="index"/>, which covers the same blocks because a
    /// struct's fields and an extension's storage are row-aligned with their parent.
    /// </summary>
    /// <param name="index">The field index, in the canonical node's own order.</param>
    internal ChunkStats Field(int index) =>
        _column is null
            ? default
            : new ChunkStats(_column.Field(index), _firstBlock, _blockCount, _ledger, _dry);

    /// <summary>
    /// The cursor for a list's elements, which their parent's blocks cover, or an absent one when
    /// those blocks do not describe the <paramref name="elements"/> elements the chunk writes in
    /// the order it writes them.
    /// </summary>
    /// <param name="elements">The length of the chunk's elements child, after narrowing.</param>
    internal ChunkStats Elements(long elements)
    {
        if (_column is null)
        {
            return default;
        }

        ColumnWriter? child = _ledger is { ElementsServe: false }
            ? null
            : _column.ElementsOver(_firstBlock, _blockCount, elements);
        if (child is null)
        {
            _ledger?.ElementsUnserved();
            return default;
        }

        return new ChunkStats(child, _firstBlock, _blockCount, _ledger, _dry);
    }
}
