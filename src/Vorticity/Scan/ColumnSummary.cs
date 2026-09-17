// One column's bounds over a whole file, and the pruning a caller can do from bounds it has KEPT
// rather than from a file it has opened - docs/11-write-strategy.md §6.3's "an engine over many
// files calls it before opening a scan", and docs/13-dataset.md §4.2's "summaries, bounded: per
// summarised column, min, max, null_count".
//
// WHY THIS IS A SEAM AND NOT A COPY. `VortexFile.MayMatch` answers "can this predicate select a row
// here" from a file that is already open. A dataset's parent node holds the same three aggregates
// for an object it has NOT opened, and for the union of a whole subtree, and has to answer from
// those alone: not opening the object is the entire point of the summary. Two implementations of
// "does this predicate survive these bounds" would be two chances to disagree, and
// docs/08-semantics.md §1 -- only a positive proof prunes -- is what they would disagree about. So
// there is one implementation, the zone pruner, and this is its door for a caller who holds bounds
// instead of a file.
//
// AN UNION OF EXACT BOUNDS IS EXACT, which is why the precision travels with the bound: min(min A,
// min B) is the true minimum of A ∪ B whenever both were true minima. It turns false the moment one
// side is a `vortex.bounded_min`, and then the `min == max ⇒ constant` shortcut is off for the
// union too. Carrying one flag is what keeps that from being re-derived, wrongly, by each caller.
using System;
using System.Collections.Generic;
using Vorticity.Compute;
using Vorticity.Expressions;
using Vorticity.File;
using Vorticity.Serialization.Schemas;
using Vorticity.Types;

namespace Vorticity.Scan;

/// <summary>What one column says about a whole file: a lower bound, an upper bound, and nulls.</summary>
/// <param name="Path">The column, as a filter names it.</param>
/// <param name="Min">A lower bound on its values.</param>
/// <param name="HasMin">Whether <paramref name="Min"/> was recorded.</param>
/// <param name="Max">An upper bound on its values.</param>
/// <param name="HasMax">Whether <paramref name="Max"/> was recorded.</param>
/// <param name="IsExact">
/// Whether the bounds are the true extremes rather than conservative ones. False for
/// <c>vortex.bounded_min</c> / <c>vortex.bounded_max</c>, and false for any union that involved one.
/// </param>
/// <param name="NullCount">How many of the rows are null.</param>
/// <param name="HasNullCount">Whether <paramref name="NullCount"/> was recorded.</param>
public readonly record struct ColumnSummary(
    string Path,
    FilterLiteral Min,
    bool HasMin,
    FilterLiteral Max,
    bool HasMax,
    bool IsExact,
    long NullCount,
    bool HasNullCount)
{
    /// <summary>The default number of columns summarised, which docs/13-dataset.md §4.2 fixes.</summary>
    public const int DefaultLimit = 32;

    /// <summary>Whether this summary licenses nothing, so that holding it would buy nothing.</summary>
    public bool IsEmpty => !HasMin && !HasMax && !HasNullCount;
}

/// <summary>Reads a file's per-column summaries, and prunes with them.</summary>
public static class ColumnSummaries
{
    /// <summary>The summaries of a file's first <paramref name="limit"/> top-level columns.</summary>
    /// <param name="file">An open file; no data segment is read.</param>
    /// <param name="limit">
    /// How many columns to summarise, so that the result has a bounded size whatever the schema
    /// (docs/13-dataset.md §4.2). <see cref="ColumnSummary.DefaultLimit"/> by default.
    /// </param>
    /// <returns>The summaries, in the schema's field order; empty when the file records none.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="file"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="limit"/> is negative.</exception>
    /// <remarks>
    /// THE STATISTICS ARE SHALLOW, one entry per top-level field (docs/02-format.md §3), so a nested
    /// path has no bound to read and does not appear. A field whose bounds are of a kind no filter
    /// literal can hold, and one with no statistic at all, are left out for the same reason: an
    /// absent summary licenses nothing, and saying so with silence costs no bytes.
    /// </remarks>
    public static IReadOnlyList<ColumnSummary> Of(VortexFile file, int limit = ColumnSummary.DefaultLimit)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentOutOfRangeException.ThrowIfNegative(limit);
        DType schema = file.Schema;
        if (!file.HasFileStatistics || schema.Kind != DTypeKind.Struct)
        {
            return [];
        }

        FileStatistics statistics = file.Statistics;
        int count = Math.Min(Math.Min(limit, schema.FieldCount), statistics.FieldCount);
        List<ColumnSummary> summaries = new List<ColumnSummary>(count);
        for (int index = 0; index < count; index++)
        {
            ColumnSummary summary = Read(schema.GetFieldName(index), statistics.GetField(index));
            if (!summary.IsEmpty)
            {
                summaries.Add(summary);
            }
        }

        return summaries;
    }

    /// <summary>The summaries of the named columns of a file.</summary>
    /// <param name="file">An open file; no data segment is read.</param>
    /// <param name="paths">The columns, as a filter names them. Duplicates and unknown names are skipped.</param>
    /// <returns>The summaries, in the order the paths were given.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static IReadOnlyList<ColumnSummary> Of(VortexFile file, IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(paths);
        DType schema = file.Schema;
        if (!file.HasFileStatistics || schema.Kind != DTypeKind.Struct)
        {
            return [];
        }

        FileStatistics statistics = file.Statistics;
        List<ColumnSummary> summaries = new List<ColumnSummary>(paths.Count);
        HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < paths.Count; i++)
        {
            string path = paths[i];
            if (path is null || !seen.Add(path) || path.Contains('.', StringComparison.Ordinal))
            {
                continue;
            }

            int index = schema.IndexOfField(path);
            if (index < 0 || index >= statistics.FieldCount)
            {
                continue;
            }

            ColumnSummary summary = Read(path, statistics.GetField(index));
            if (!summary.IsEmpty)
            {
                summaries.Add(summary);
            }
        }

        return summaries;
    }

    /// <summary>
    /// Whether a row <paramref name="filter"/> selects can lie behind these summaries.
    /// </summary>
    /// <param name="filter">The predicate.</param>
    /// <param name="summaries">What is known about the columns, in any order.</param>
    /// <param name="rows">How many rows the summaries cover.</param>
    /// <returns><see langword="false"/> only when the summaries prove no row can match.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <remarks>
    /// One call, one predicate. A caller asking the same predicate of many summaries -- which is
    /// what a dataset's pruning walk is -- builds a <see cref="SummaryPruner"/> once instead.
    /// </remarks>
    public static bool MayMatch(VortexExpr filter, IReadOnlyList<ColumnSummary> summaries, long rows) =>
        new SummaryPruner(filter).MayMatch(summaries, rows);

    /// <summary>One field's statistics as a summary, or an empty one when it records nothing usable.</summary>
    /// <param name="path">The column's path.</param>
    /// <param name="field">Its file-level statistics.</param>
    internal static ColumnSummary Read(string path, FieldStatistics field)
    {
        FilterLiteral min = default;
        FilterLiteral max = default;
        bool hasMin = field.HasMin && FileStatisticsPruner.TryLiteral(field.Min, out min);
        bool hasMax = field.HasMax && FileStatisticsPruner.TryLiteral(field.Max, out max);
        bool exact = (!field.HasMin || field.MinPrecision == StatPrecision.Exact)
            && (!field.HasMax || field.MaxPrecision == StatPrecision.Exact);
        bool hasNulls = field.TryGetNullCount(out ulong nulls) && nulls <= long.MaxValue;
        return new ColumnSummary(
            path, min, hasMin, max, hasMax, exact, hasNulls ? (long)nulls : 0, hasNulls);
    }

    /// <summary>A summary as the zone pruner's bounds over a zone of <paramref name="rows"/> rows.</summary>
    /// <param name="summary">The summary.</param>
    /// <param name="rows">The rows it covers.</param>
    internal static ZoneBounds Bounds(ColumnSummary summary, long rows) => ZoneBounds.Create(
        summary.Min,
        summary.HasMin,
        summary.Max,
        summary.HasMax,
        summary.IsExact,
        summary.HasNullCount ? Math.Min(summary.NullCount, rows) : 0,
        summary.HasNullCount);
}

/// <summary>One predicate, asked of many summarised extents.</summary>
/// <remarks>
/// A dataset asks the same question of every node of a tree and of every object a node does not
/// refute, so the parts of the question that do not change -- which columns the predicate reads --
/// are worked out once, here, instead of once per node.
/// </remarks>
public sealed class SummaryPruner
{
    private readonly VortexExpr _filter;
    private readonly List<string> _paths = [];

    /// <summary>Prepares to ask <paramref name="filter"/> of summaries.</summary>
    /// <param name="filter">The predicate.</param>
    /// <exception cref="ArgumentNullException"><paramref name="filter"/> is null.</exception>
    public SummaryPruner(VortexExpr filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        _filter = filter;
        filter.CollectFields(_paths);
    }

    /// <summary>Whether the predicate reads any column at all; one that does not prunes nothing.</summary>
    public bool ReadsColumns => _paths.Count > 0;

    /// <summary>The columns the predicate reads, so a caller can summarise those and no others.</summary>
    internal IReadOnlyList<string> Paths => _paths;

    /// <summary>Whether a row the predicate selects can lie behind these summaries.</summary>
    /// <param name="summaries">What is known about the columns, in any order.</param>
    /// <param name="rows">How many rows the summaries cover.</param>
    /// <returns><see langword="false"/> only when the summaries prove no row can match.</returns>
    /// <remarks>
    /// The summarised extent is ONE ZONE AS LONG AS ITSELF, which is what lets the zone pruner --
    /// the only implementation of docs/08-semantics.md §1 in this library -- answer the question.
    /// A column the filter names and the summaries do not cover licenses nothing and is simply not
    /// passed on, so the answer degrades to "may match" rather than to a wrong prune.
    /// </remarks>
    public bool MayMatch(IReadOnlyList<ColumnSummary> summaries, long rows)
    {
        if (summaries is null || summaries.Count == 0 || rows <= 0 || _paths.Count == 0)
        {
            return true;
        }

        List<ZoneColumn>? columns = null;
        for (int i = 0; i < summaries.Count; i++)
        {
            ColumnSummary summary = summaries[i];
            if (summary.IsEmpty || !_paths.Contains(summary.Path))
            {
                continue;
            }

            columns ??= [];
            columns.Add(new ZoneColumn(
                Expr.Field(summary.Path), rows, rows, [ColumnSummaries.Bounds(summary, rows)]));
        }

        return columns is null || new ZonePruner(_filter, columns.ToArray()).MayMatch(new RowRange(0, rows));
    }
}
