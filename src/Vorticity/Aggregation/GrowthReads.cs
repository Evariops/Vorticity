using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.Compute;
using Vorticity.Scanning;
using Vorticity.Types;

namespace Vorticity.Aggregating;

/// <summary>
/// The settling of the growths of an aggregation once its pass is done: each group's runs of points folded into one where
/// they follow each other, and the points of the groups whose runs cross read again, from the rows those runs lie between
/// alone, then sorted and folded.
/// </summary>
/// <remarks>
/// <para>
/// A run keeps the row of its first point. The rows of the runs read again are merged into ranges, each read in order with a
/// table of keys of its own, and a key's first point in a range is the first point of one of its group's runs there: the
/// group's earliest row in the range lies in one of its runs, all of which the range holds whole. A key whose first point
/// is no run's first is not read again; one whose first point is takes that group's points. The keys are told apart as
/// the pass told them, with no comparison against the result's own: a result cut in parts reads alike.
/// </para>
/// <para>
/// The ranges read again are the runs' rows: a group whose late point fell in another object reads that object's rows
/// around the point and its own object's rows around its run, not the rows between them. The points are held under the
/// query's memory; where they do not fit, the groups are read again in halves, each half its own rows, down to a group
/// alone, which fails when its own points do not fit.
/// </para>
/// </remarks>
internal static class GrowthReads
{
    /// <summary>Settles the growths of every group of <paramref name="outcome"/>, once; a later call does nothing.</summary>
    /// <param name="outcome">The aggregation, its pass done.</param>
    /// <param name="source">The source the pass read.</param>
    /// <param name="spec">The pass's scan, whose filter and rows the reads keep.</param>
    /// <param name="metrics">Where the reads count.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    internal static async ValueTask SettleAsync(AggregationOutcome outcome, ScanSource source, ScanSpec spec, ScanCounters metrics, CancellationToken cancellationToken)
    {
        if (!outcome.Plan.Ordered || outcome.RunsSettled)
        {
            return;
        }

        int count = outcome.Keys?.Count ?? 1;
        int[] all = new int[count];
        for (int g = 0; g < count; g++)
        {
            all[g] = g;
        }

        await SettleAsync(outcome, source, spec, metrics, all, cancellationToken).ConfigureAwait(false);
        outcome.RunsSettled = true;
    }

    /// <summary>Settles the growths of <paramref name="groups"/>, those not settled yet: the groups an operator or the result reads.</summary>
    /// <param name="outcome">The aggregation, its pass done.</param>
    /// <param name="source">The source the pass read.</param>
    /// <param name="spec">The pass's scan, whose filter and rows the reads keep.</param>
    /// <param name="metrics">Where the reads count.</param>
    /// <param name="groups">The groups.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    internal static async ValueTask SettleAsync(
        AggregationOutcome outcome, ScanSource source, ScanSpec spec, ScanCounters metrics, ReadOnlyMemory<int> groups, CancellationToken cancellationToken)
    {
        AggregationPlan plan = outcome.Plan;
        if (!plan.Ordered || outcome.RunsSettled)
        {
            return;
        }

        long settling = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            await SettleEachAsync(outcome, source, spec, metrics, groups, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            plan.Settled(System.Diagnostics.Stopwatch.GetTimestamp() - settling);
        }
    }

    private static async ValueTask SettleEachAsync(
        AggregationOutcome outcome, ScanSource source, ScanSpec spec, ScanCounters metrics, ReadOnlyMemory<int> groups, CancellationToken cancellationToken)
    {
        AggregationPlan plan = outcome.Plan;
        for (int a = 0; a < plan.Aggregates.Length; a++)
        {
            IAggregateNode aggregate = plan.Aggregates[a];
            if (aggregate.Kind != AggregateKind.Increase)
            {
                continue;
            }

            AggregateSlot slot = outcome.SlotOf(aggregate);
            List<(AggregateSlot Slot, int Group, long First)> found = [];
            List<(long First, long Last)> spans = [];
            foreach (int g in groups.Span)
            {
                AggregateSlot part = slot.PartOf(g, out int local);
                if (!((IGrowthRuns)part).TrySettle(local))
                {
                    spans.Clear();
                    ((IGrowthRuns)part).Spans(local, spans);
                    long first = long.MaxValue;
                    foreach ((long start, _) in spans)
                    {
                        first = Math.Min(first, start);
                    }

                    found.Add((part, local, first));
                }
            }

            if (found.Count == 0)
            {
                continue;
            }

            // In the order of their first rows, so that a share of them reads a stretch of the source; in as many shares as
            // their points need to fit the query's memory, each read on its own, as a read that ran out of it tells.
            plan.ReadAgain(found.Count, 0);
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            found.Sort(static (a, b) => a.First.CompareTo(b.First));
            List<(AggregateSlot Slot, int Group)> crossed = found.ConvertAll(static f => (f.Slot, f.Group));
            Stack<(int Start, int End)> shares = new Stack<(int Start, int End)>([(0, crossed.Count)]);
            while (shares.TryPop(out (int Start, int End) share))
            {
                (bool read, double done) = await TryReadAgainAsync(
                    plan, a, crossed.GetRange(share.Start, share.End - share.Start), outcome.Memory, source, spec, metrics, cancellationToken).ConfigureAwait(false);
                if (read)
                {
                    continue;
                }

                int count = share.End - share.Start;
                if (count == 1)
                {
                    throw outcome.Memory!.Exceeded("points of a group a growth reads again", 1, 0);
                }

                // The read held a share of the rows' points when it ran out: the shares a fourth smaller, two at least.
                int parts = (int)Math.Clamp(Math.Ceiling(1.25 / Math.Max(done, 1.0 / count)), 2, count);
                for (int p = parts - 1; p >= 0; p--)
                {
                    shares.Push((share.Start + (int)((long)count * p / parts), share.Start + (int)((long)count * (p + 1) / parts)));
                }
            }

            plan.ReadAgain(0, 0, System.Diagnostics.Stopwatch.GetTimestamp() - started);
        }
    }

    /// <summary>
    /// Reads the points of <paramref name="crossed"/> again and settles each from them: false, nothing settled, when the
    /// query's memory does not hold them, with the share of the rows read by then.
    /// </summary>
    private static async ValueTask<(bool Read, double Done)> TryReadAgainAsync(
        AggregationPlan plan, int aggregate, List<(AggregateSlot Slot, int Group)> crossed, QueryMemory? memory, ScanSource source, ScanSpec spec, ScanCounters metrics,
        CancellationToken cancellationToken)
    {
        // The rows each group's runs lie between, and the first row of each run, which tells its group in the read.
        List<(long First, long Last)> spans = [];
        List<(long Row, int Target)> firsts = [];
        for (int t = 0; t < crossed.Count; t++)
        {
            int before = spans.Count;
            ((IGrowthRuns)crossed[t].Slot).Spans(crossed[t].Group, spans);
            for (int s = before; s < spans.Count; s++)
            {
                firsts.Add((spans[s].First, t));
            }
        }

        firsts.Sort(static (a, b) => a.Row.CompareTo(b.Row));
        long[] anchors = new long[firsts.Count];
        int[] owners = new int[firsts.Count];
        for (int i = 0; i < anchors.Length; i++)
        {
            (anchors[i], owners[i]) = firsts[i];
        }

        List<RowRange> ranges = Merged(spans);
        IAggregateNode node = plan.Aggregates[aggregate];
        (ColumnShape[] columns, _) = AggregationEngine.Columns(plan, new AggregateSlot?[plan.Aggregates.Length]);
        Reading reading = new Reading(
            plan, columns, Array.FindIndex(columns, known => known.Is(node.Input!)), Array.FindIndex(columns, known => known.Is(node.Order!)),
            plan.Filters.FilterOf[aggregate], anchors, owners, source, spec, metrics,

            // A lane reads its ranges in order: a key's first point is told by the row it lies at.
            AggregationEngine.PassSpec(spec, columns, plan, source.Schema) with { Answering = null, Options = spec.Options with { DegreeOfParallelism = 1 } });

        // The ranges side by side, a stretch of them a lane, each lane with a table of keys of its own: a key's first point
        // in a range names its group whatever the other ranges hold.
        int degree = spec.Options.DegreeOfParallelism > 0 ? spec.Options.DegreeOfParallelism : source.Session.Options.MaxDegreeOfParallelism;
        int lanes = Math.Clamp(degree, 1, ranges.Count);
        long total = 0;
        foreach (RowRange range in ranges)
        {
            total += range.Length;
        }

        Lane[] read = new Lane[lanes];
        int first = 0;
        long given = 0;
        for (int l = 0; l < lanes; l++)
        {
            // About as many rows a lane: the ranges up to its share of them.
            int last = first;
            long share = total * (l + 1) / lanes;
            while (last < ranges.Count && (last == first || given + ranges[last].Length <= share || l == lanes - 1))
            {
                given += ranges[last].Length;
                last++;
            }

            read[l] = new Lane(reading, ((IGrowthRuns)crossed[0].Slot).Points(memory), ranges.GetRange(first, last - first));
            first = last;
        }

        try
        {
            Task<bool>[] tasks = new Task<bool>[lanes];
            for (int l = 0; l < lanes; l++)
            {
                Lane lane = read[l];
                tasks[l] = lanes == 1 ? lane.ReadAsync(cancellationToken).AsTask() : Task.Run(() => lane.ReadAsync(cancellationToken).AsTask(), cancellationToken);
            }

            await Task.WhenAll(tasks).ConfigureAwait(false);
            long done = 0;
            bool all = true;
            foreach (Lane lane in read)
            {
                done += lane.Done;
                all &= lane.Held;
            }

            if (!all)
            {
                return (false, total == 0 ? 1 : (double)done / total);
            }

            // Every lane's points in the first's, then sorted and folded.
            for (int l = 1; l < lanes; l++)
            {
                if (!read[0].Points.Absorb(read[l].Points))
                {
                    return (false, 1);
                }
            }

            return (read[0].Points.Settle(crossed.ToArray()), 1);
        }
        finally
        {
            foreach (Lane lane in read)
            {
                lane.Release();
            }
        }
    }

    /// <summary>What every lane of a read again shares: the columns, the runs' first rows and the groups they name, the scan.</summary>
    private sealed record Reading(
        AggregationPlan Plan, ColumnShape[] Columns, int Value, int Order, int Filter, long[] Anchors, int[] Owners, ScanSource Source, ScanSpec Spec,
        ScanCounters Metrics, ScanSpec Pass);

    /// <summary>A lane of a read again: a stretch of the ranges, read in order, its keys numbered in a table of its own.</summary>
    private sealed class Lane(Reading reading, GrowthPoints points, List<RowRange> ranges)
    {
        private readonly FilterMasks? _masks = reading.Filter >= 0 ? new FilterMasks(reading.Plan.Filters) : null;
        private readonly GroupKeys? _keys = reading.Plan.Grouped ? reading.Plan.CreateKeys(sorted: false) : null;
        private readonly GroupRanges _keyRanges = new GroupRanges();
        private readonly int[] _nodes = new int[reading.Columns.Length];
        private int[] _rowGroups = [];
        private int[] _status = [];
        private int[] _taken = [];
        private int[] _targets = [];
        private long _number;

        /// <summary>The points the lane gathered.</summary>
        internal GrowthPoints Points => points;

        /// <summary>The rows the lane read.</summary>
        internal long Done { get; private set; }

        /// <summary>Whether the query's memory held every point the lane met.</summary>
        internal bool Held { get; private set; } = true;

        internal async ValueTask<bool> ReadAsync(CancellationToken cancellationToken)
        {
            foreach (RowRange range in ranges)
            {
                RowRange rows = reading.Spec.Rows is { } asked ? asked.Intersect(range) : range;
                if (rows.IsEmpty)
                {
                    continue;
                }

                reading.Plan.ReadAgain(0, rows.Length);
                long next = rows.Start;
                await foreach (RecordBatch batch in reading.Source.Rows.BatchesAsync(reading.Pass with { Rows = rows }, reading.Metrics)
                    .WithCancellation(cancellationToken).ConfigureAwait(false))
                {
                    if (batch.StartRow < next)
                    {
                        throw new InvalidOperationException($"A read of rows {rows.Start} to {rows.End} delivered row {batch.StartRow} after row {next}.");
                    }

                    next = batch.StartRow + batch.RowCount;
                    Done += batch.RowCount;
                    if (batch.RowCount > 0 && batch.SelectedRows > 0 && !Take(batch))
                    {
                        Held = false;
                        return false;
                    }
                }
            }

            return true;
        }

        /// <summary>The points of a batch whose keys a run's first point named; false when the query's memory refuses them.</summary>
        private bool Take(RecordBatch batch)
        {
            int count = batch.RowCount;
            CanonicalArena arena = batch.Arena;
            int root = batch.RootIndex;
            for (int c = 0; c < reading.Columns.Length; c++)
            {
                int resolved = FilterEvaluator.Resolve(arena, root, reading.Columns[c].Field, count);
                while (arena.RecordRef(resolved).Kind == CanonicalKind.Extension)
                {
                    resolved = arena.GetNode(resolved).StorageIndex;
                }

                _nodes[c] = resolved;
            }

            ReadOnlySpan<ulong> selection = batch.SelectionWords;
            if (_masks is not null)
            {
                _masks.Evaluate(arena, root, count, selection);
                if (_masks.KeepsNone(reading.Filter))
                {
                    return true;
                }

                selection = _masks.Selection(reading.Filter);
            }

            // Each row's key, numbered in the lane's own table.
            Scratch.Grow(ref _rowGroups, count);
            if (_keys is not null)
            {
                _keyRanges.Clear();
                if (_keys.Assign(arena, _nodes.AsSpan(0, reading.Plan.Keys.Length), count, selection, _rowGroups, _keyRanges))
                {
                    for (int r = 0; r < _keyRanges.Count; r++)
                    {
                        _rowGroups.AsSpan(_keyRanges.StartAt(r), _keyRanges.EndAt(r) - _keyRanges.StartAt(r)).Fill(_keyRanges.GroupAt(r));
                    }
                }
            }
            else
            {
                _rowGroups.AsSpan(0, count).Clear();
            }

            if (_status.Length < (_keys?.Count ?? 1))
            {
                int[] grown = new int[Scratch.Capacity(_keys?.Count ?? 1, _status.Length)];
                _status.CopyTo(grown, 0);
                _status = grown;
            }

            // A key's first point names its group, or none: the points of the groups named are kept.
            _number++;
            ReadOnlySpan<ulong> kept = points.Rows(arena, _number, _nodes[reading.Value], _nodes[reading.Order], count, selection);
            Scratch.Grow(ref _taken, count);
            Scratch.Grow(ref _targets, count);
            int held = 0;
            RowCursor cursor = new RowCursor(kept, 0, count);
            while (cursor.Next(out int row))
            {
                int key = _rowGroups[row];
                int named = _status[key];
                if (named == 0)
                {
                    int at = Array.BinarySearch(reading.Anchors, batch.StartRow + row);
                    named = _status[key] = at >= 0 ? reading.Owners[at] + 1 : -1;
                }

                if (named > 0)
                {
                    _taken[held] = row;
                    _targets[held] = named - 1;
                    held++;
                }
            }

            return held == 0 || points.Add(arena, _number, _nodes[reading.Value], _nodes[reading.Order], _taken.AsSpan(0, held), _targets.AsSpan(0, held));
        }

        internal void Release()
        {
            points.Release();
            _keys?.Release();
            _keyRanges.Release();
            Scratch.Return(ref _rowGroups);
            Scratch.Return(ref _taken);
            Scratch.Return(ref _targets);
        }
    }

    /// <summary>The spans of rows, each from its first to its last, merged where they overlap or touch, in order.</summary>
    private static List<RowRange> Merged(List<(long First, long Last)> spans)
    {
        spans.Sort(static (a, b) => a.First.CompareTo(b.First));
        List<RowRange> ranges = [];
        long start = spans[0].First;
        long end = spans[0].Last + 1;
        for (int i = 1; i < spans.Count; i++)
        {
            if (spans[i].First <= end)
            {
                end = Math.Max(end, spans[i].Last + 1);
                continue;
            }

            ranges.Add(new RowRange(start, end));
            start = spans[i].First;
            end = spans[i].Last + 1;
        }

        ranges.Add(new RowRange(start, end));
        return ranges;
    }
}
