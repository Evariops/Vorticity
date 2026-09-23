using System;
using System.Collections.Immutable;

namespace Vorticity;

/// <summary>The bytes of a written file, by what they hold; they sum to its length.</summary>
/// <param name="Total">The file's length.</param>
/// <param name="Data">The magic and the column chunks, padding included.</param>
/// <param name="Statistics">The file statistics segment.</param>
/// <param name="ZoneMaps">The zones tables.</param>
/// <param name="Indexes">Index runs and the index directory.</param>
/// <param name="Footer">The dtype, layout, footer, user metadata, identity, postscript and end-of-file marker.</param>
public readonly record struct WriteBytes(long Total, long Data, long Statistics, long ZoneMaps, long Indexes, long Footer);

/// <summary>What one top-level column was written as.</summary>
/// <param name="Path">The column, as a scan spells it; empty for a file whose root is not a struct.</param>
/// <param name="Encodings">
/// What each chunk's values were written as, in chunk order: the name of the
/// <see cref="EncodingHint"/> scheme, <c>Canonical</c> for values stored plain, or the array id of
/// an encoding no scheme writes, which only a chunk an append kept from another writer can carry.
/// An extension is looked through to its storage and a list to its elements; a struct reads
/// <c>{field: encoding, ...}</c>, and so do a map's entries and a variant's metadata and value.
/// </param>
public sealed record ColumnWriteReport(string Path, ImmutableArray<string> Encodings)
{
    /// <summary>Chunks that had a remembered plan to consult.</summary>
    internal int PlansPriced { get; init; }

    /// <summary>Of those, the ones whose remembered plan held and was kept.</summary>
    internal int PlansHeld { get; init; }

    /// <summary>How often plan memory held, or 0 for a column that never had a plan to consult.</summary>
    internal double PlanMemoryHitRate => PlansPriced == 0 ? 0 : (double)PlansHeld / PlansPriced;
}

/// <summary>Whether an index a policy asked for is in the file.</summary>
public enum IndexOutcome
{
    /// <summary>Written, and listed in the directory.</summary>
    Built = 0,

    /// <summary>Dropped whole, for the reason the report gives; it left no bytes in the file.</summary>
    Abandoned = 1,
}

/// <summary>One index a policy asked for, and what became of it.</summary>
/// <param name="Column">The column, or the key's columns in parentheses.</param>
/// <param name="Kind">The index kind, as the index directory names it.</param>
/// <param name="Outcome">Whether it is in the file.</param>
/// <param name="Reason">Why it was abandoned, or what it was built without; null otherwise.</param>
/// <param name="Bytes">The bytes its runs take in the file.</param>
public sealed record IndexWriteReport(string Column, string Kind, IndexOutcome Outcome, string? Reason, long Bytes)
{
    /// <summary>The filters it carries coarser than a block.</summary>
    internal int Generations { get; init; }

    /// <summary>The runs its directory entry lists.</summary>
    internal int Runs { get; init; }

    /// <summary>Whether the policy marked it required, so that its abandonment fails the write.</summary>
    internal bool Required { get; init; }
}

/// <summary>What one <see cref="VortexFileWriter"/> wrote.</summary>
/// <param name="RowCount">Rows in the file.</param>
/// <param name="BlockRows">Rows per block: the zone length, and the unit index runs are counted in.</param>
/// <param name="ChunkRows">The rows of every chunk, in order; each chunk but the last is a whole number of blocks.</param>
/// <param name="Bytes">The file's bytes, by kind.</param>
/// <param name="Columns">One entry per top-level column.</param>
/// <param name="Indexes">One entry per index the policy asked for, built or abandoned.</param>
public sealed record WriteReport(
    long RowCount,
    int BlockRows,
    ImmutableArray<int> ChunkRows,
    WriteBytes Bytes,
    ImmutableArray<ColumnWriteReport> Columns,
    ImmutableArray<IndexWriteReport> Indexes)
{
    /// <summary>
    /// The rows of each chunk of column <paramref name="column"/>, in order: <see cref="ChunkRows"/>,
    /// or the column's own when <see cref="VortexWriteOptions.ColumnChunkTargetBytes"/> gave it a
    /// target, each of its chunks then spanning whole chunks of the file.
    /// </summary>
    /// <param name="column">The column's position in <see cref="Columns"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="column"/> names no column.</exception>
    public ImmutableArray<int> ChunkRowsOf(int column)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(column);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(column, Columns.Length);
        return ColumnChunkRows?[column] is { IsDefault: false } own ? own : ChunkRows;
    }

    /// <summary>By column, the rows of its own chunks, default for a column chunked with the file; null when every column is.</summary>
    internal ImmutableArray<int>[]? ColumnChunkRows { get; init; }

    /// <summary>The report of one column's index of one kind, or null when the policy never asked for it.</summary>
    internal IndexWriteReport? Index(string column, string kind)
    {
        foreach (IndexWriteReport index in Indexes)
        {
            if (string.Equals(index.Column, column, StringComparison.Ordinal) && string.Equals(index.Kind, kind, StringComparison.Ordinal))
            {
                return index;
            }
        }

        return null;
    }
}
