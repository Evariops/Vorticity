// What `CompleteAsync` returns: docs/11-write-strategy.md §7.3 and docs/10-indexes.md §7.1.
//
// A REPORT, NOT A LOG. Everything in it is a number the writer already held; nothing is measured
// for the report's sake. Its point is that a caller who expected an index and got none reads the
// reason here instead of inferring it from a slow scan three weeks later -- which is why every
// index a policy asked for appears in it, built or abandoned, and an abandoned one always carries
// its reason.
//
// A VIEW, AND FREE UNTIL READ. The write allocation ceilings hold a file to a few dozen bytes of
// fixed cost, and a report object with its lists refused them by 56 to 800 bytes a file. So the
// report is a struct over the completed writer, and its lists are built when a caller asks for
// them -- a caller who discards the result, which is every existing caller, pays nothing.
using System;
using System.Collections.Generic;

namespace Vorticity.Writing;

/// <summary>The bytes of a written file, by what they hold.</summary>
/// <param name="Data">The magic and the column chunks, padding included.</param>
/// <param name="ZoneMaps">The <c>vortex.zoned</c> zones tables.</param>
/// <param name="Statistics">The file statistics segment.</param>
/// <param name="Indexes">Index runs and the index directory.</param>
/// <param name="Footer">The dtype, layout, footer, postscript and EOF marker.</param>
public readonly record struct WriteBytes(long Data, long ZoneMaps, long Statistics, long Indexes, long Footer)
{
    /// <summary>The file's length.</summary>
    public long Total => Data + ZoneMaps + Statistics + Indexes + Footer;
}

/// <summary>What one top-level column was written as.</summary>
/// <param name="Path">The column, as a scan spells it; empty for a non-struct root.</param>
/// <param name="Encodings">The scheme each chunk was encoded with, in chunk order.</param>
/// <param name="PlansPriced">Chunks that had a remembered plan to consult.</param>
/// <param name="PlansHeld">Of those, the ones whose remembered plan held and was kept.</param>
public sealed record ColumnWriteReport(
    string Path,
    IReadOnlyList<string> Encodings,
    int PlansPriced,
    int PlansHeld)
{
    /// <summary>
    /// How often plan memory held (docs/11-write-strategy.md §3.4.3), or <c>0</c> for a column
    /// that never had a plan to consult.
    /// </summary>
    public double PlanMemoryHitRate => PlansPriced == 0 ? 0 : (double)PlansHeld / PlansPriced;
}

/// <summary>Whether an index a policy asked for is in the file.</summary>
public enum IndexOutcome
{
    /// <summary>Written, and listed in the directory.</summary>
    Built = 0,

    /// <summary>Dropped whole, for the reason the report gives.</summary>
    Abandoned = 1,
}

/// <summary>One index a policy asked for, and what became of it.</summary>
/// <param name="Path">The column.</param>
/// <param name="Kind">The kind name.</param>
/// <param name="Outcome">Built or abandoned.</param>
/// <param name="Reason">Why it was abandoned; <see langword="null"/> when it was built.</param>
/// <param name="Bytes">The payload bytes it occupies in the file.</param>
/// <param name="Generations">Filters coarser than a block that it carries.</param>
/// <param name="Runs">Runs listed in the directory.</param>
public sealed record IndexWriteReport(
    string Path,
    string Kind,
    IndexOutcome Outcome,
    string? Reason,
    long Bytes,
    int Generations,
    int Runs);

/// <summary>What one <see cref="VortexFileWriter"/> wrote.</summary>
/// <remarks>
/// A view over the completed writer: it stays valid after the writer is disposed, and
/// <c>default</c> reads as an empty file.
/// </remarks>
public readonly struct WriteReport : IEquatable<WriteReport>
{
    private readonly VortexFileWriter? _writer;

    internal WriteReport(VortexFileWriter writer) => _writer = writer;

    /// <summary>Rows written.</summary>
    public long RowCount => _writer?.RowCount ?? 0;

    /// <summary>
    /// Rows per block: the zone length, and the unit index runs are counted in; <c>0</c> when
    /// repartitioning was off, in which case every chunk is one block.
    /// </summary>
    public int BlockRows => _writer?.ReportBlockRows ?? 0;

    /// <summary>
    /// The row count of every chunk, in order. Chunks are shared by every column, and each but the
    /// last is a whole number of blocks.
    /// </summary>
    public IReadOnlyList<long> ChunkRows => _writer?.ReportChunkRows ?? Array.Empty<long>();

    /// <summary>The file's bytes, by kind; they sum to its length.</summary>
    public WriteBytes Bytes => _writer?.ReportBytes ?? default;

    /// <summary>One entry per top-level column. Built on the first read.</summary>
    public IReadOnlyList<ColumnWriteReport> Columns =>
        _writer?.ReportColumns() ?? Array.Empty<ColumnWriteReport>();

    /// <summary>One entry per index a policy asked for, built or abandoned.</summary>
    public IReadOnlyList<IndexWriteReport> Indexes =>
        _writer?.ReportIndexes ?? Array.Empty<IndexWriteReport>();

    /// <summary>The report for one column's index of one kind.</summary>
    /// <param name="path">The column.</param>
    /// <param name="kind">The kind name.</param>
    /// <returns>The entry, or <see langword="null"/> when the policy never asked for it.</returns>
    public IndexWriteReport? Index(string path, string kind)
    {
        foreach (IndexWriteReport index in Indexes)
        {
            if (string.Equals(index.Path, path, StringComparison.Ordinal)
                && string.Equals(index.Kind, kind, StringComparison.Ordinal))
            {
                return index;
            }
        }

        return null;
    }

    /// <summary>Whether two reports describe the same write.</summary>
    /// <param name="other">The other report.</param>
    /// <returns>Whether both view the same writer.</returns>
    public bool Equals(WriteReport other) => ReferenceEquals(_writer, other._writer);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is WriteReport other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => _writer?.GetHashCode() ?? 0;

    /// <summary>Whether two reports describe the same write.</summary>
    /// <param name="left">One report.</param>
    /// <param name="right">The other.</param>
    /// <returns>Whether they are equal.</returns>
    public static bool operator ==(WriteReport left, WriteReport right) => left.Equals(right);

    /// <summary>Whether two reports describe different writes.</summary>
    /// <param name="left">One report.</param>
    /// <param name="right">The other.</param>
    /// <returns>Whether they differ.</returns>
    public static bool operator !=(WriteReport left, WriteReport right) => !left.Equals(right);
}
