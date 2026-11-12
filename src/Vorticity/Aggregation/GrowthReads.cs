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
/// the pass told them, with no comparison against the result's own: a result cut in parts or held by a core reads alike.
/// </para>
/// <para>
/// The ranges read again are the runs' rows: a group whose late point fell in another object reads that object's rows
/// around the point and its own object's rows around its run, not the rows between them.
/// </para>
/// </remarks>
internal static class GrowthReads
{
    /// <summary>Settles every growth of <paramref name="outcome"/>, once; a later call does nothing.</summary>
    /// <param name="outcome">The aggregation, its pass done.</param>
    /// <param name="source">The source the pass read.</param>
    /// <param name="spec">The pass's scan, whose filter and rows the reads keep.</param>
    /// <param name="metrics">Where the reads count.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    internal static async ValueTask SettleAsync(AggregationOutcome outcome, ScanSource source, ScanSpec spec, ScanCounters metrics, CancellationToken cancellationToken)
    {
        AggregationPlan plan = outcome.Plan;
        if (!plan.Ordered || outcome.RunsSettled)
        {
            return;
        }

        int groups = outcome.Keys?.Count ?? 1;
        for (int a = 0; a < plan.Aggregates.Length; a++)
        {
            IAggregateNode aggregate = plan.Aggregates[a];
            if (aggregate.Kind != AggregateKind.Increase)
            {
                continue;
            }

            AggregateSlot slot = outcome.SlotOf(aggregate);
            List<(AggregateSlot Slot, int Group)> crossed = [];
            for (int g = 0; g < groups; g++)
            {
                AggregateSlot part = slot.PartOf(g, out int local);
                if (!((IGrowthRuns)part).TrySettle(local))
                {
                    crossed.Add((part, local));
                }
            }

            if (crossed.Count > 0)
            {
                await ReadAgainAsync(plan, a, crossed, outcome.Memory, source, spec, metrics, cancellationToken).ConfigureAwait(false);
            }
        }

        outcome.RunsSettled = true;
    }

    private static async ValueTask ReadAgainAsync(
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
        plan.ReadAgain(crossed.Count, 0);

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

                    if (held > 0)
                    {
                        points.Add(arena, number, nodes[value], nodes[order], taken.AsSpan(0, held), targets.AsSpan(0, held));
                    }
                }
            }

            points.Settle(crossed.ToArray());
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
