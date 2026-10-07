using System;
using System.Collections.Immutable;
using Vorticity.Scanning;

namespace Vorticity.Aggregating;

/// <summary>The plan of a group by as <c>ExplainAsync</c> says it (docs/design/16-queries.md §10), worked out from the query and the statistics alone.</summary>
internal static class GroupPlans
{
    internal static GroupPlan Of(AggregationQuery query)
    {
        AggregationPlan plan = query.Plan;
        ScanSource source = query.Host.Source;
        ColumnShape[] keys = plan.Keys;
        KeyFacts facts = AggregationEngine.Facts(source, keys);
        ImmutableArray<GroupKeyPlan>.Builder components = ImmutableArray.CreateBuilder<GroupKeyPlan>(keys.Length);
        long? most = 1;
        for (int k = 0; k < keys.Length; k++)
        {
            long? values = facts.Bounds[k] is { } bounds ? Values(bounds) : null;
            components.Add(new GroupKeyPlan(keys[k].Column.Field.Path, facts.Sorted[k], values));
            most = most is long groups && values is long distinct ? Times(groups, distinct + (keys[k].Type.IsNullable ? 1 : 0)) : null;
        }

        int streaming = plan.Blocking ? -1 : StreamingGroupBatches.Streaming(query);
        (GroupOrdering order, long? kept) = Ordering(query, streaming);
        ScanSpec spec = query.Host.Spec();
        int degree = spec.Options.DegreeOfParallelism > 0 ? spec.Options.DegreeOfParallelism : source.Session.Options.MaxDegreeOfParallelism;
        (int capacity, int alpha) = streaming < 0 ? Core(plan, source, facts, degree) : (0, 0);
        return new GroupPlan(
            components.MoveToImmutable(),
            streaming >= 0 ? streaming : null,
            streaming >= 0 ? null : NotStreaming(query, facts),
            plan.Aggregates.Length,
            query.RowFilter?.ToString(),
            order,
            kept,
            plan.Chosen.Length,
            degree,
            capacity > 0,
            capacity,
            alpha,
            most);
    }

    /// <summary>The values between a bound's extremes, saturated.</summary>
    private static long Values(KeyBounds bounds)
    {
        ulong span = unchecked((ulong)(bounds.Max - bounds.Min));
        return span >= long.MaxValue ? long.MaxValue : (long)span + 1;
    }

    private static long Times(long left, long right) => right != 0 && left > long.MaxValue / right ? long.MaxValue : left * right;

    /// <summary>Why no component of the key streams.</summary>
    private static string NotStreaming(AggregationQuery query, KeyFacts facts)
    {
        if (query.Plan.Blocking)
        {
            return "the plan holds every group to the end of the pass";
        }

        for (int k = 0; k < facts.Sorted.Length; k++)
        {
            if (facts.Sorted[k] || query.Host.Source.OrdersOnAsking(query.Plan.Keys[k].Column.Field))
            {
                return "the order asked for is not the one the groups would close in";
            }
        }

        return "no component of the key is sorted, as the statistics say, nor brought in order by the source";
    }

    /// <summary>How the groups are ordered: by the key they stream on, by a heap a window keeps, or a sort of them all.</summary>
    private static (GroupOrdering Order, long? Kept) Ordering(AggregationQuery query, int streaming)
    {
        GroupOperator[] operators = query.Operators;
        for (int o = 0; o < operators.Length; o++)
        {
            if (operators[o] is not GroupOrder)
            {
                continue;
            }

            if (streaming >= 0)
            {
                return (GroupOrdering.Streamed, null);
            }

            long keep = o + 1 < operators.Length
                ? operators[o + 1] is GroupWindow window ? Saturated(window.Skip, window.Take) : long.MaxValue
                : Saturated(query.Skip, query.Take);
            return keep < long.MaxValue ? (GroupOrdering.Top, keep) : (GroupOrdering.Sort, null);
        }

        return (streaming >= 0 ? GroupOrdering.Streamed : GroupOrdering.None, null);
    }

    private static long Saturated(long skip, long take) => take == long.MaxValue || skip > long.MaxValue - take ? long.MaxValue : skip + take;

    /// <summary>The capacity of a lane's cache and α, when the plan has the core hold the groups at <paramref name="degree"/>; none otherwise.</summary>
    private static (int Capacity, int Alpha) Core(AggregationPlan plan, ScanSource source, KeyFacts facts, int degree)
    {
        AggregateSlot?[] settled = new AggregateSlot?[plan.Aggregates.Length];
        (ColumnShape[] columns, int[] inputs) = AggregationEngine.Columns(plan, settled);
        return GroupCore.Of(plan, settled, columns, inputs, source, facts, sorted: false, top: null, degree) is { } core ? (core.Capacity, core.Alpha) : (0, 0);
    }
}
