using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Aggregating;
using Vorticity.Compute;
using Vorticity.Expressions;
using Vorticity.Scanning;
using Vorticity.Types;

namespace Vorticity.Dataset;

internal sealed partial class DatasetScanSource
{
    /// <summary>
    /// The places of the best <paramref name="keep"/> rows <paramref name="spec"/> keeps in the order of
    /// <paramref name="path"/> (spec G, 3): the objects from the best bound their summaries give the column, those they do
    /// not bound first; in an object whose granules' statistics bound it, its best granules first, as many as hold the rows
    /// kept; and once the top holds them, its last row a bound every read after takes in its filter, which the zone maps and
    /// the statistics prune by. The first object whose bound cannot reach it ends the walk: every one after is worse.
    /// </summary>
    public async ValueTask<long[]?> TopPositionsAsync(
        ScanSpec spec, string path, bool descending, long keep, ScanCounters metrics, CancellationToken cancellationToken)
    {
        VortexType type = TopRows.TypeOf(this, path);

        // A float's NaN comes after +∞ in the order and outside every bound: descending, no bound ends the walk.
        bool stops = !(descending && type.Kind == VortexTypeKind.Primitive && type.PrimitiveType.IsFloat());
        int field = _version.Schema.Columns.IndexOfName(path);
        long from = spec.Rows?.Start ?? 0;
        long to = spec.Rows?.End ?? long.MaxValue;
        SummaryPruner? pruner = spec.Options.UseStatistics && spec.Filter is { } filter ? new SummaryPruner(FunctionFieldExpr.Ranges(filter)) : null;
        List<(PositionedObject Held, FilterLiteral? Bound)> objects = [];
        await foreach (PositionedObject held in _version.WalkAsync(pruner, from, to, null, cancellationToken).ConfigureAwait(false))
        {
            objects.Add((held, BoundOf(held, path, descending)));
        }

        objects.Sort((left, right) => Better(left.Bound, right.Bound, descending) is int order and not 0 ? order : left.Held.FirstRow.CompareTo(right.Held.FirstRow));
        using QueryMemory memory = new QueryMemory(Session.Options.MemoryBudget ?? QueryMemoryBudget.Process);
        using TopKeep top = new TopKeep(Session, type, descending, TopRows.Kept(keep), memory);
        foreach ((PositionedObject held, FilterLiteral? bound) in objects)
        {
            if (stops && top.Edge is { } reached && bound is { } best && !Reaches(best, reached, descending))
            {
                break;
            }

            RowRange rows = new RowRange(Math.Max(from, held.FirstRow), Math.Max(Math.Max(from, held.FirstRow), Math.Min(to, held.FirstRow + held.Entry.Rows)));
            if (rows.IsEmpty)
            {
                continue;
            }

            if (top.Edge is null && field >= 0 && !held.Entry.HasDeletions && rows.Length == held.Entry.Rows
                && await GranulesAsync(held, field, descending, cancellationToken).ConfigureAwait(false) is { } granules)
            {
                // The best granules first, as many as hold the rows kept; the others after, under the bound they set.
                int wave = Wave(granules, keep);
                foreach (RowRange run in Runs(held, granules.Stats, granules.Order.AsSpan(0, wave)))
                {
                    await FeedAsync(spec, run, path, null, descending, top, metrics, cancellationToken).ConfigureAwait(false);
                }

                FilterLiteral? set = top.Edge;
                foreach (RowRange run in Runs(held, granules.Stats, granules.Order.AsSpan(wave)))
                {
                    await FeedAsync(spec, run, path, set, descending, top, metrics, cancellationToken).ConfigureAwait(false);
                }

                continue;
            }

            await FeedAsync(spec, rows, path, top.Edge, descending, top, metrics, cancellationToken).ConfigureAwait(false);
        }

        return top.Places(cancellationToken);
    }

    /// <summary>Gives <paramref name="top"/> the rows <paramref name="rows"/> of the scan keeps, under the bound <paramref name="edge"/> sets.</summary>
    private ValueTask FeedAsync(
        ScanSpec spec, RowRange rows, string path, FilterLiteral? edge, bool descending, TopKeep top, ScanCounters metrics, CancellationToken cancellationToken) =>
        TopRows.FeedAsync(this, spec with { Rows = rows, Filter = Bounded(spec.Filter, path, edge, descending) }, path, top, metrics, cancellationToken);

    /// <summary>
    /// <paramref name="filter"/> with the rows past <paramref name="edge"/> left out: not below it descending, not above it
    /// ascending, which keeps a NaN where the order puts it, after +∞. The filter itself before the top holds its rows, or
    /// for a NaN edge, which no comparison orders.
    /// </summary>
    private static VortexExpr? Bounded(VortexExpr? filter, string path, FilterLiteral? edge, bool descending)
    {
        if (edge is not { } bound || (bound.Kind == FilterLiteralKind.Float && double.IsNaN(bound.FloatValue)))
        {
            return filter;
        }

        VortexExpr past = Expr.Not(new ComparisonExpr(new ColumnFieldExpr(path), descending ? ComparisonOp.Less : ComparisonOp.Greater, bound));
        return filter is null ? past : Expr.And(filter, past);
    }

    /// <summary>The bound the summaries of <paramref name="held"/> give the column: its greatest value descending, its least ascending; null for none.</summary>
    private static FilterLiteral? BoundOf(in PositionedObject held, string path, bool descending)
    {
        foreach (ColumnSummary summary in held.Entry.Summaries.Columns)
        {
            if (string.Equals(summary.Path, path, StringComparison.Ordinal))
            {
                return descending ? summary.HasMax ? summary.Max : null : summary.HasMin ? summary.Min : null;
            }
        }

        return null;
    }

    /// <summary>Which of two bounds comes first: an object with none, which may hold anything, then the best; 0 for two alike or not compared.</summary>
    private static int Better(FilterLiteral? left, FilterLiteral? right, bool descending)
    {
        if (left is not { } a || right is not { } b)
        {
            return left is null == right is null ? 0 : left is null ? -1 : 1;
        }

        return ZonePruner.TryCompare(a, b, out int order) ? descending ? -order : order : 0;
    }

    /// <summary>Whether an object of bound <paramref name="bound"/> may hold a row at or before <paramref name="edge"/>.</summary>
    private static bool Reaches(FilterLiteral bound, FilterLiteral edge, bool descending) =>
        !ZonePruner.TryCompare(bound, edge, out int order) || (descending ? order >= 0 : order <= 0);

    /// <summary>
    /// The granules of <paramref name="held"/> from the best bound on the column numbered <paramref name="field"/> its
    /// statistics give, those of no value last; null when they give none, a column of no measure.
    /// </summary>
    private async ValueTask<Granules?> GranulesAsync(PositionedObject held, int field, bool descending, CancellationToken cancellationToken)
    {
        (Indexes.GranuleStats?[] stats, _) = await _dataset.ReadGranuleStatsAsync(held.Entry, [field], [], cancellationToken).ConfigureAwait(false);
        if (stats[0] is not { Measure: { } kind } granules || granules.TotalRows != held.Entry.Rows)
        {
            return null;
        }

        bool floats = kind == MeasureKind.Float;
        int word = descending ? MeasureLayout.Greatest(floats) : MeasureLayout.Least(floats);
        int count = granules.Count;
        int[] order = new int[count];
        double[] keys = new double[count];
        for (int g = 0; g < count; g++)
        {
            order[g] = g;
            ReadOnlySpan<long> measure = granules.MeasureOf(g);
            long value = measure[word];
            keys[g] = measure[MeasureLayout.Values] == 0 ? double.NaN : kind switch
            {
                MeasureKind.Float => BitConverter.Int64BitsToDouble(value),
                MeasureKind.Unsigned => (ulong)value,
                _ => value,
            };
        }

        // The best first: a granule of no value last, then by its bound, the earlier granule first between two alike. A
        // bound of a large integer only orders the reads; the filter's bound prunes exactly.
        Array.Sort(order, (a, b) =>
        {
            bool none = double.IsNaN(keys[a]);
            if (none != double.IsNaN(keys[b]))
            {
                return none ? 1 : -1;
            }

            int by = none ? 0 : descending ? keys[b].CompareTo(keys[a]) : keys[a].CompareTo(keys[b]);
            return by != 0 ? by : a.CompareTo(b);
        });

        return new Granules(granules, order);
    }

    /// <summary>The best granules of <paramref name="granules"/> that hold <paramref name="keep"/> values at least, one at least.</summary>
    private static int Wave(Granules granules, long keep)
    {
        long values = 0;
        int wave = 0;
        while (wave < granules.Order.Length && (wave == 0 || values < keep))
        {
            int granule = granules.Order[wave++];
            values += granules.Stats.Rows[granule] - granules.Stats.Nulls[granule];
        }

        return wave;
    }

    /// <summary>The dataset's rows of <paramref name="chosen"/>, granules of <paramref name="held"/>, as runs of granules that follow one another.</summary>
    private static List<RowRange> Runs(in PositionedObject held, Indexes.GranuleStats stats, ReadOnlySpan<int> chosen)
    {
        int[] sorted = chosen.ToArray();
        Array.Sort(sorted);
        List<RowRange> runs = [];
        int at = 0;
        while (at < sorted.Length)
        {
            int last = at;
            while (last + 1 < sorted.Length && sorted[last + 1] == sorted[last] + 1)
            {
                last++;
            }

            runs.Add(new RowRange(held.FirstRow + stats.FirstRow(sorted[at]), held.FirstRow + stats.FirstRow(sorted[last] + 1)));
            at = last + 1;
        }

        return runs;
    }

    /// <summary>An object's granules' statistics on the column of the order, and the granules from the best.</summary>
    private sealed record Granules(Indexes.GranuleStats Stats, int[] Order);
}
