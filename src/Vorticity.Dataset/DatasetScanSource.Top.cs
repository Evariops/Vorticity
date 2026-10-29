using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
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
    /// The reads of a top in flight together at least, its objects' statistics, then its granules: a store's round trip each,
    /// which the reads one after another waited on in turn.
    /// </summary>
    private const int TopReads = 16;

    /// <summary>
    /// The places of the best <paramref name="keep"/> rows <paramref name="spec"/> keeps in the order of
    /// <paramref name="path"/> (spec G, 3): the objects from the best bound their summaries give the column, those they do
    /// not bound first; read side by side under no filter, where the bounds of their granules' statistics rank rows of the
    /// question (<see cref="SideBySideAsync"/>), in turn otherwise (<see cref="InTurnAsync"/>): a filter may leave out the
    /// row that holds a granule's bound, and a float's NaN, which comes first descending, lies outside every bound.
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
        if (stops && field >= 0 && spec.Filter is null && !spec.MatchesNothing)
        {
            await SideBySideAsync(spec, path, field, descending, objects, top, metrics, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await InTurnAsync(spec, path, field, descending, keep, stops, objects, top, metrics, cancellationToken).ConfigureAwait(false);
        }

        return top.Places(cancellationToken);
    }

    /// <summary>
    /// Gives <paramref name="top"/> the best rows of <paramref name="objects"/>, every row of them, from the best, read side by
    /// side: their granules' statistics on the column numbered <paramref name="field"/> asked ahead, the best bounds among
    /// their granules a floor no row of the top falls below (<see cref="TopFloor"/>), the first object whose bound cannot
    /// reach it ending the walk, every one after being worse; then the granules that reach it read side by side, each under
    /// the edge the top holds as it starts; then the objects whose statistics bound none of their granules, in turn, as
    /// <see cref="InTurnAsync"/> reads them. Read in turn under the edge the objects before set, each object waited on its
    /// statistics and its granules after the one before: the 100 slowest of a month of requests, 55 objects, on 370 round
    /// trips.
    /// </summary>
    private async ValueTask SideBySideAsync(
        ScanSpec spec, string path, int field, bool descending, List<(PositionedObject Held, FilterLiteral? Bound)> objects,
        TopKeep top, ScanCounters metrics, CancellationToken cancellationToken)
    {
        int reads = Math.Max(Math.Max(1, Session.Options.MaxDegreeOfParallelism), TopReads);
        long from = spec.Rows?.Start ?? 0;
        long to = spec.Rows?.End ?? long.MaxValue;
        TopFloor floor = new TopFloor(descending, top.Keep);
        List<(PositionedObject Held, RowRange Rows, Granules? Granules)> reached = [];
        Queue<(PositionedObject Held, RowRange Rows, Task<Granules?>? Granules)> asked = [];
        try
        {
            int next = 0;
            while (true)
            {
                // The statistics of the next objects asked while those before come, as many as the reads in flight.
                while (next < objects.Count && asked.Count < reads)
                {
                    (PositionedObject held, FilterLiteral? bound) = objects[next++];
                    if (floor.Literal is { } set && bound is { } best && !Reaches(best, set, descending))
                    {
                        next = objects.Count;
                        break;
                    }

                    RowRange rows = RowsOf(held, from, to);
                    if (!rows.IsEmpty)
                    {
                        bool whole = !held.Entry.HasDeletions && rows.Length == held.Entry.Rows;
                        asked.Enqueue((held, rows, whole ? GranulesAsync(held, field, descending, cancellationToken).AsTask() : null));
                    }
                }

                if (!asked.TryDequeue(out (PositionedObject Held, RowRange Rows, Task<Granules?>? Granules) first))
                {
                    break;
                }

                Granules? granules = first.Granules is { } reading ? await reading.ConfigureAwait(false) : null;
                if (granules is not null)
                {
                    floor.Fold(granules.Stats);
                }

                reached.Add((first.Held, first.Rows, granules));
            }
        }
        finally
        {
            // A failure lets the statistics still on their way go.
            foreach ((_, _, Task<Granules?>? late) in asked)
            {
                if (late is not null)
                {
                    await ((Task)late).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                }
            }
        }

        // The granules that reach the floor, by runs of granules that follow one another, the best objects' first.
        List<RowRange> runs = [];
        foreach ((PositionedObject held, _, Granules? granules) in reached)
        {
            if (granules is not null)
            {
                runs.AddRange(Runs(held, granules.Stats, CollectionsMarshal.AsSpan(floor.Reaching(granules.Stats))));
            }
        }

        Lock gate = new Lock();
        ParallelOptions parallel = new ParallelOptions { CancellationToken = cancellationToken, MaxDegreeOfParallelism = reads };
        await Parallel.ForEachAsync(runs, parallel, async (run, token) =>
        {
            FilterLiteral? edge;
            lock (gate)
            {
                edge = top.Edge;
            }

            await FeedAsync(spec, run, path, edge, descending, top, gate, metrics, token).ConfigureAwait(false);
        }).ConfigureAwait(false);

        // The objects whose statistics bound none of their granules, whole, in turn under the edge the top holds then: the
        // first whose bound cannot reach it, or the floor, ends them.
        foreach ((PositionedObject held, RowRange rows, Granules? granules) in reached)
        {
            if (granules is not null)
            {
                continue;
            }

            if (BoundOf(held, path, descending) is { } best
                && ((top.Edge is { } edge && !Reaches(best, edge, descending)) || (floor.Literal is { } set && !Reaches(best, set, descending))))
            {
                break;
            }

            await FeedAsync(spec, rows, path, top.Edge, descending, top, null, metrics, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Gives <paramref name="top"/> the best rows of <paramref name="objects"/>, from the best, one after another: in an object
    /// whose granules' statistics bound the column numbered <paramref name="field"/>, its best granules first, as many as hold
    /// the rows kept; and once the top holds them, its last row a bound every read after takes in its filter, which the zone
    /// maps and the statistics prune by. When <paramref name="stops"/>, the first object whose bound cannot reach it ends the
    /// walk: every one after is worse.
    /// </summary>
    private async ValueTask InTurnAsync(
        ScanSpec spec, string path, int field, bool descending, long keep, bool stops, List<(PositionedObject Held, FilterLiteral? Bound)> objects,
        TopKeep top, ScanCounters metrics, CancellationToken cancellationToken)
    {
        long from = spec.Rows?.Start ?? 0;
        long to = spec.Rows?.End ?? long.MaxValue;
        foreach ((PositionedObject held, FilterLiteral? bound) in objects)
        {
            if (stops && top.Edge is { } reached && bound is { } best && !Reaches(best, reached, descending))
            {
                break;
            }

            RowRange rows = RowsOf(held, from, to);
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
                    await FeedAsync(spec, run, path, null, descending, top, null, metrics, cancellationToken).ConfigureAwait(false);
                }

                FilterLiteral? set = top.Edge;
                foreach (RowRange run in Runs(held, granules.Stats, granules.Order.AsSpan(wave)))
                {
                    await FeedAsync(spec, run, path, set, descending, top, null, metrics, cancellationToken).ConfigureAwait(false);
                }

                continue;
            }

            await FeedAsync(spec, rows, path, top.Edge, descending, top, null, metrics, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>The rows of <paramref name="held"/> within the scan's <c>[from, to)</c>, as the dataset's.</summary>
    private static RowRange RowsOf(in PositionedObject held, long from, long to) =>
        new RowRange(Math.Max(from, held.FirstRow), Math.Max(Math.Max(from, held.FirstRow), Math.Min(to, held.FirstRow + held.Entry.Rows)));

    /// <summary>
    /// Gives <paramref name="top"/> the rows <paramref name="rows"/> of the scan keeps, under the bound <paramref name="edge"/>
    /// sets; under <paramref name="gate"/> when reads beside it feed the same top.
    /// </summary>
    private ValueTask FeedAsync(
        ScanSpec spec, RowRange rows, string path, FilterLiteral? edge, bool descending, TopKeep top, Lock? gate, ScanCounters metrics, CancellationToken cancellationToken) =>
        TopRows.FeedAsync(this, spec with { Rows = rows, Filter = Bounded(spec.Filter, path, edge, descending) }, path, top, gate, metrics, cancellationToken);

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

    /// <summary>
    /// The worst of the best bounds among the granules folded in, as many as the top keeps rows, each granule's greatest value
    /// descending, its least ascending: each granule of a value holds one at its bound, so that once as many granules as the
    /// top keeps are folded, as many rows at least are no worse than the floor, and no row of the top is worse (spec G, 3).
    /// Exact, the measures' words compared as their kind orders them.
    /// </summary>
    private sealed class TopFloor(bool descending, int keep)
    {
        // The best bounds folded, the worst of them first, and how a bound ranks against another: better is greater.
        private PriorityQueue<long, long>? _best;
        private Comparer<long>? _better;
        private MeasureKind _kind;
        private bool _mixed;

        /// <summary>
        /// The floor as a filter compares it, once as many granules of a value as the top keeps are folded; null before, or
        /// when the objects' measures differ in kind.
        /// </summary>
        internal FilterLiteral? Literal { get; private set; }

        /// <summary>Folds the bounds of the granules of <paramref name="stats"/>, a measure's, those of a value.</summary>
        internal void Fold(Indexes.GranuleStats stats)
        {
            MeasureKind kind = stats.Measure!.Value;
            if (_best is null)
            {
                _kind = kind;
                _better = Better(kind, descending);
                _best = new PriorityQueue<long, long>(_better);
            }
            else if (kind != _kind)
            {
                _mixed = true;
                Literal = null;
            }

            if (_mixed)
            {
                return;
            }

            int word = Word(kind);
            for (int g = 0; g < stats.Count; g++)
            {
                ReadOnlySpan<long> measure = stats.MeasureOf(g);
                long bound = measure[word];
                if (measure[MeasureLayout.Values] <= 0 || (kind == MeasureKind.Float && double.IsNaN(BitConverter.Int64BitsToDouble(bound))))
                {
                    continue;
                }

                if (_best.Count < keep)
                {
                    _best.Enqueue(bound, bound);
                }
                else if (_better!.Compare(bound, _best.Peek()) > 0)
                {
                    _best.EnqueueDequeue(bound, bound);
                }
            }

            if (_best.Count == keep)
            {
                long floor = _best.Peek();
                Literal = kind switch
                {
                    MeasureKind.Signed => FilterLiteral.From(floor),
                    MeasureKind.Unsigned => FilterLiteral.From((ulong)floor),
                    _ => FilterLiteral.From(BitConverter.Int64BitsToDouble(floor)),
                };
            }
        }

        /// <summary>
        /// The granules of <paramref name="stats"/>, ascending, that may hold a row of the top: those whose bound reaches the
        /// floor, or that no bound ranks, a float's NaN; every one while the floor is unknown, a granule of nulls among them,
        /// whose rows the top keeps when fewer are of a value.
        /// </summary>
        internal List<int> Reaching(Indexes.GranuleStats stats)
        {
            List<int> reaching = [];
            bool known = Literal is not null && stats.Measure == _kind;
            int word = Word(_kind);
            long floor = known ? _best!.Peek() : 0;
            for (int g = 0; g < stats.Count; g++)
            {
                ReadOnlySpan<long> measure = stats.MeasureOf(g);
                if (!known || (measure[MeasureLayout.Values] > 0
                    && ((_kind == MeasureKind.Float && double.IsNaN(BitConverter.Int64BitsToDouble(measure[word]))) || _better!.Compare(measure[word], floor) >= 0)))
                {
                    reaching.Add(g);
                }
            }

            return reaching;
        }

        /// <summary>The word of a granule's measure that bounds it: its greatest value descending, its least ascending.</summary>
        private int Word(MeasureKind kind) =>
            descending ? MeasureLayout.Greatest(kind == MeasureKind.Float) : MeasureLayout.Least(kind == MeasureKind.Float);

        /// <summary>How a bound of <paramref name="kind"/> ranks against another, the better greater: the larger descending.</summary>
        private static Comparer<long> Better(MeasureKind kind, bool descending)
        {
            Comparison<long> ascending = kind switch
            {
                MeasureKind.Signed => static (a, b) => a.CompareTo(b),
                MeasureKind.Unsigned => static (a, b) => ((ulong)a).CompareTo((ulong)b),
                _ => static (a, b) => BitConverter.Int64BitsToDouble(a).CompareTo(BitConverter.Int64BitsToDouble(b)),
            };
            return descending ? Comparer<long>.Create(ascending) : Comparer<long>.Create((a, b) => ascending(b, a));
        }
    }
}
