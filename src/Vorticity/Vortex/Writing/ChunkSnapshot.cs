namespace Vorticity.Writing;

/// <summary>
/// What a column's ingest knows of one of its chunks, taken from its <see cref="ColumnWriter"/> as the chunk leaves its
/// gathering for an emission of its own (<see cref="ColumnWriter.Freeze"/>): the emission reads it on any lane while the
/// column ingests the blocks after the chunk, and what the emission decides comes back to the column when the chunk lands
/// in the file (<see cref="ColumnWriter.Land"/>). A tree of the shape of the column's.
/// </summary>
/// <remarks>
/// The column's own state could not be read there: its lists of blocks grow and move as blocks close, its width slots are
/// taken and given back, and its distinct table runs on the next chunk's rows. So the chunk's summary, its widths' sum and
/// what its table held at its last block are copied, and its table itself goes with it, the column starting the next
/// chunk in another. What the emission decides for the next chunk's ingest, whether its table and its widths run, is held
/// here until the chunk lands, a point of the rows' own order, so that the work the ingest does is the same at any degree.
/// </remarks>
internal sealed class ChunkSnapshot
{
    /// <summary>The column the chunk is of, whose memory of its plans the emission reads and writes.</summary>
    internal ColumnWriter Column = null!;

    /// <summary>The chunk's first block and its blocks.</summary>
    internal int FirstBlock;

    /// <inheritdoc cref="FirstBlock"/>
    internal int BlockCount;

    /// <summary>The column's summary over the chunk.</summary>
    internal BlockStats Stats;

    /// <summary>The chunk's pair of width histograms, summed, when every block of it carried them (<see cref="HasWidths"/>).</summary>
    internal int[] Widths = new int[BitPackWidths.Length];

    /// <inheritdoc cref="Widths"/>
    internal bool HasWidths;

    /// <summary>The chunk's distinct table, taken from the column; null when it ran none. Given back once the chunk is compressed.</summary>
    internal DistinctTable? Table;

    /// <summary>What the table held when the chunk's last block closed, or <c>(-1, 0)</c>.</summary>
    internal (int Distinct, long Heap) TableAtClose;

    /// <summary>The snapshots of the column's children, <see cref="ChildCount"/> of them.</summary>
    internal ChunkSnapshot?[] Children = [];

    /// <inheritdoc cref="Children"/>
    internal int ChildCount;

    /// <summary>Whether the emission wrote the chunk under a plan it remembered (<see cref="Remember"/>).</summary>
    internal bool Remembered;

    /// <summary>What the chunk was written as, one more than its scheme, for its blocks.</summary>
    internal byte Written;

    /// <summary>Whether the chunks the column ingests after this one lands run its distinct table.</summary>
    internal bool Live;

    /// <summary>Whether they count its widths.</summary>
    internal bool WidthsLive;

    /// <summary>
    /// Takes what the chunk was written as, against what it was priced at, into the column's memory of its plans, which the
    /// column's next emission reads; what it decides for the ingest waits here until the chunk lands.
    /// </summary>
    internal void Remember(in ColumnPlan plan, long actualBytes)
    {
        (Written, Live, WidthsLive) = Column.RememberPlan(in plan, actualBytes);
        Remembered = true;
    }

    /// <summary>Gives every table of the tree back to its column, emptied: the chunk is compressed.</summary>
    internal void GiveTables()
    {
        if (Table is { } table)
        {
            Table = null;
            Column.GiveTable(table);
        }

        for (int i = 0; i < ChildCount; i++)
        {
            Children[i]?.GiveTables();
        }
    }
}
