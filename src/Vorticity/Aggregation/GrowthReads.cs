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
        int value = Array.FindIndex(columns, known => known.Is(node.Input!));
        int order = Array.FindIndex(columns, known => known.Is(node.Order!));
        int filter = plan.Filters.FilterOf[aggregate];

        // In order, on one lane: a key's first point is told by the row it lies at.
        ScanSpec pass = AggregationEngine.PassSpec(spec, columns, plan, source.Schema) with
        {
            Answering = null,
            Options = spec.Options with { DegreeOfParallelism = 1 },
        };

        FilterMasks? masks = filter >= 0 ? new FilterMasks(plan.Filters) : null;
        GroupKeys? keys = plan.Grouped ? plan.CreateKeys(sorted: false) : null;
        GrowthPoints points = ((IGrowthRuns)crossed[0].Slot).Points(memory);
        GroupRanges keyRanges = new GroupRanges();
        int[] nodes = new int[columns.Length];
        int[] rowGroups = [];
        int[] status = [];
        int[] taken = [];
        int[] targets = [];
        long number = 0;
        long total = 0;
        foreach (RowRange range in ranges)
        {
            total += range.Length;
        }

        long done = 0;
        try
        {
            foreach (RowRange range in ranges)
            {
                RowRange rows = spec.Rows is { } asked ? asked.Intersect(range) : range;
                if (rows.IsEmpty)
                {
                    continue;
                }

                plan.ReadAgain(0, rows.Length);
                long next = rows.Start;
                await foreach (RecordBatch batch in source.Rows.BatchesAsync(pass with { Rows = rows }, metrics).WithCancellation(cancellationToken).ConfigureAwait(false))
                {
                    int count = batch.RowCount;
                    if (batch.StartRow < next)
                    {
                        throw new InvalidOperationException($"A read of rows {rows.Start} to {rows.End} delivered row {batch.StartRow} after row {next}.");
                    }

                    next = batch.StartRow + count;
                    done += count;
                    if (count == 0 || batch.SelectedRows == 0)
                    {
                        continue;
                    }

                    CanonicalArena arena = batch.Arena;
                    int root = batch.RootIndex;
                    for (int c = 0; c < columns.Length; c++)
                    {
                        int resolved = FilterEvaluator.Resolve(arena, root, columns[c].Field, count);
                        while (arena.RecordRef(resolved).Kind == CanonicalKind.Extension)
                        {
                            resolved = arena.GetNode(resolved).StorageIndex;
                        }

                        nodes[c] = resolved;
                    }

                    ReadOnlySpan<ulong> selection = batch.SelectionWords;
                    if (masks is not null)
                    {
                        masks.Evaluate(arena, root, count, selection);
                        if (masks.KeepsNone(filter))
                        {
                            continue;
                        }

                        selection = masks.Selection(filter);
                    }

                    // Each row's key, numbered in the read's own table.
                    Scratch.Grow(ref rowGroups, count);
                    if (keys is not null)
                    {
                        keyRanges.Clear();
                        if (keys.Assign(arena, nodes.AsSpan(0, plan.Keys.Length), count, selection, rowGroups, keyRanges))
                        {
                            for (int r = 0; r < keyRanges.Count; r++)
                            {
                                rowGroups.AsSpan(keyRanges.StartAt(r), keyRanges.EndAt(r) - keyRanges.StartAt(r)).Fill(keyRanges.GroupAt(r));
                            }
                        }
                    }
                    else
                    {
                        rowGroups.AsSpan(0, count).Clear();
                    }

                    if (status.Length < (keys?.Count ?? 1))
                    {
                        int[] grown = new int[Scratch.Capacity(keys?.Count ?? 1, status.Length)];
                        status.CopyTo(grown, 0);
                        status = grown;
                    }

                    // A key's first point names its group, or none: the points of the groups named are kept.
                    number++;
                    ReadOnlySpan<ulong> kept = points.Rows(arena, number, nodes[value], nodes[order], count, selection);
                    Scratch.Grow(ref taken, count);
                    Scratch.Grow(ref targets, count);
                    int held = 0;
                    RowCursor cursor = new RowCursor(kept, 0, count);
                    while (cursor.Next(out int row))
                    {
                        int key = rowGroups[row];
                        int named = status[key];
                        if (named == 0)
                        {
                            int at = Array.BinarySearch(anchors, batch.StartRow + row);
                            named = status[key] = at >= 0 ? owners[at] + 1 : -1;
                        }

                        if (named > 0)
                        {
                            taken[held] = row;
                            targets[held] = named - 1;
                            held++;
                        }
                    }

                    if (held > 0 && !points.Add(arena, number, nodes[value], nodes[order], taken.AsSpan(0, held), targets.AsSpan(0, held)))
                    {
                        return (false, total == 0 ? 1 : (double)done / total);
                    }
                }
            }

            return (points.Settle(crossed.ToArray()), 1);
        }
        finally
        {
            points.Release();
            keys?.Release();
            keyRanges.Release();
            Scratch.Return(ref rowGroups);
            Scratch.Return(ref taken);
            Scratch.Return(ref targets);
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
