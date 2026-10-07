using System;
using System.Buffers;
using System.Numerics;
using System.Threading;
using Vorticity.Arrays;
using Vorticity.Types;

namespace Vorticity.Aggregating;

/// <summary>
/// The groups a blocking pass keeps on each lane when the query takes the first <c>k</c> groups of
/// an order on the key, or on a column's largest or smallest value: the <c>k</c> best groups the lane
/// has met, trimmed back to <c>k</c> once they pass one and a half times as many, by the ranking the
/// result itself uses.
/// </summary>
/// <remarks>
/// <para>
/// Exact, whatever the order the rows come in. A key dropped lies past the <c>k</c>-th best the lane
/// holds, and that one only gets better as keys come, so a key dropped never comes back into the top:
/// a group made for it again is dropped again, and the groups kept hold every one of their rows. A key
/// of the whole top has fewer than <c>k</c> better ones on its own lane, so it is kept there, and the
/// lanes' groups merged hold the whole top.
/// </para>
/// <para>
/// On a column's largest value, from the largest, or its smallest, from the smallest
/// (PLAN-HIGH-CARDINALITY, H7), a group dropped held a value past the <c>k</c>-th best: made again, it
/// enters the top only with a better value than every one it held, so the rows it lost change nothing
/// of it; the query reads nothing else of its groups. Once a lane holds <c>k</c> groups, a row whose
/// value lies past the <c>k</c>-th best changes none of them, nor makes one: the lane drops it before
/// grouping it. One pass, no table of every group.
/// </para>
/// </remarks>
internal sealed class KeyTop
{
    /// <summary>The most groups a top keeps: past it, ranking every lane's groups at each trim costs more than holding them.</summary>
    internal const long MostKept = 100_000;

    private readonly AggregationQuery _query;
    private readonly GroupOrder? _order;
    private readonly int _keep;
    private readonly int _ceiling;
    private long _peak;

    // Whether the order is a column's largest value, from the largest, or its smallest, from the smallest.
    private readonly bool _extreme;

    private KeyTop(AggregationQuery query, GroupOrder? order, int keep, bool extreme)
    {
        _query = query;
        _order = order;
        _keep = keep;
        _ceiling = keep + ((keep + 1) / 2);
        _extreme = extreme;
        Descending = order is { } ordered && ordered.Keys[0].Descending;
        _narrows = !extreme && order is not null && query.Keys.Length == 1 && order.Keys.Length == 1;
    }

    /// <summary>
    /// Whether the top keeps the groups first met, for a window with no order before it: exact on one
    /// lane alone, since lanes would each keep groups the others drop.
    /// </summary>
    internal bool FirstMet => _order is null;

    // Whether the order is the one key's alone, which a lane's frontier compares a row's key to.
    private readonly bool _narrows;

    /// <summary>Whether the order's first column, the one a frontier compares to, goes from the greatest down.</summary>
    internal bool Descending { get; }

    /// <summary>The groups the lanes held at most, summed: what the pass held at once at worst.</summary>
    internal long Peak => Interlocked.Read(ref _peak);

    /// <summary>
    /// The top of <paramref name="query"/>, or null: its first operator orders the groups on their key
    /// alone, or on a column's extreme alone (<see cref="Extreme"/>), and the windows that follow it at
    /// once, the result's own after them when nothing else comes, reach no further than its first
    /// <see cref="MostKept"/> groups. Anything else first, a filter on the groups, could take groups out
    /// of the top the trims kept.
    /// </summary>
    internal static KeyTop? Of(AggregationQuery query)
    {
        GroupOperator[] operators = query.Operators;
        if (!query.Plan.Grouped || operators.Length == 0 || operators[0] is not GroupOrder order)
        {
            return null;
        }

        bool extreme = Extreme(query, order);
        foreach (OrderKey key in order.Keys)
        {
            if (key.Field.Component < 0 && !extreme)
            {
                return null;
            }
        }

        // The groups of the order the windows can reach: each passes over its skip from where the one
        // before it starts, and keeps its take from there.
        long start = 0;
        long reach = long.MaxValue;
        int o = 1;
        for (; o < operators.Length && operators[o] is GroupWindow window; o++)
        {
            (start, reach) = Narrowed(start, reach, window.Skip, window.Take);
        }

        if (o == operators.Length)
        {
            (start, reach) = Narrowed(start, reach, query.Skip, query.Take);
        }

        return reach is > 0 and <= MostKept ? new KeyTop(query, order, (int)reach, extreme) : null;
    }

    /// <summary>
    /// Whether <paramref name="order"/> is an integer column's largest value of each group alone, from
    /// the largest, or its smallest, from the smallest, and the query reads nothing else of its groups —
    /// no other aggregate, no chosen row — but their key (PLAN-HIGH-CARDINALITY, H7). A group's extreme
    /// then only gets better as its rows come, a null last whichever the direction; a float's would not,
    /// its NaN first from the largest and passed over once a number comes.
    /// </summary>
    private static bool Extreme(AggregationQuery query, GroupOrder order)
    {
        AggregationPlan plan = query.Plan;
        if (!plan.TopOnExtremes || order.Keys.Length != 1 || plan.Aggregates.Length != 1 || plan.Chosen.Length > 0)
        {
            return false;
        }

        OrderKey key = order.Keys[0];
        return key.Field.Result is IAggregateNode node
            && node.Identity.Equals(plan.Aggregates[0].Identity)
            && node.Input is { Kind: StorageKind.Primitive, PType: PType.I8 or PType.I16 or PType.I32 or PType.I64 or PType.U8 or PType.U16 or PType.U32 or PType.U64 }
            && (node.Kind == AggregateKind.Max ? key.Descending : node.Kind == AggregateKind.Min && !key.Descending);
    }

    /// <summary>
    /// The top of <paramref name="query"/> that keeps the groups first met, or null: windows open the
    /// operators, or the result's own closes none, and reach no further than <see cref="MostKept"/>
    /// groups, which come in no order promised. On one lane, a group made once the top is full is
    /// dropped, again whenever its key comes back, and those kept hold every one of their rows.
    /// </summary>
    internal static KeyTop? FirstOf(AggregationQuery query)
    {
        GroupOperator[] operators = query.Operators;
        if (!query.Plan.Grouped || (operators.Length > 0 && operators[0] is not GroupWindow))
        {
            return null;
        }

        long start = 0;
        long reach = long.MaxValue;
        int o = 0;
        for (; o < operators.Length && operators[o] is GroupWindow window; o++)
        {
            (start, reach) = Narrowed(start, reach, window.Skip, window.Take);
        }

        if (o == operators.Length)
        {
            (start, reach) = Narrowed(start, reach, query.Skip, query.Take);
        }

        return reach is > 0 and <= MostKept ? new KeyTop(query, null, (int)reach, extreme: false) : null;
    }

    /// <summary>A window of <paramref name="skip"/> and <paramref name="take"/> over the groups from <paramref name="start"/> on, of which those before <paramref name="reach"/> are left.</summary>
    internal static (long Start, long Reach) Narrowed(long start, long reach, long skip, long take)
    {
        long from = skip > long.MaxValue - start ? long.MaxValue : start + skip;
        long to = take == long.MaxValue || take > long.MaxValue - from ? long.MaxValue : from + take;
        return (from, Math.Min(reach, to));
    }

    /// <summary>
    /// Trims <paramref name="partition"/> back to its <c>k</c> best groups once it holds more than
    /// one and a half times as many, or more than <c>k</c> when <paramref name="final"/>: its pass,
    /// or a range of it, is over.
    /// </summary>
    internal void Trim(AggregationPartition partition, bool final)
    {
        GroupKeys keys = partition.Keys!;
        int count = keys.Count;
        partition.PeakGroups = Math.Max(partition.PeakGroups, count);
        if (_order is null)
        {
            // The first met are the first numbered: the others go.
            if (count > (final ? _keep : _ceiling))
            {
                partition.Keep(Numbers.Upto(_keep));
            }
        }
        else if (count > (final ? _keep : _ceiling))
        {
            // The order's scratch under the query's memory, as the lane's tables (PLAN-HIGH-CARDINALITY, H2).
            QueryMemory? memory = partition.Memory;
            int[] groups = QueryArrays.Rent<int>(memory, count, "top of a group by");
            try
            {
                for (int g = 0; g < count; g++)
                {
                    groups[g] = g;
                }

                AggregationOutcome outcome = new AggregationOutcome(_query.Plan, partition.Slots, keys, []) { Memory = memory };
                (int[] best, int kept) = GroupSelection.Order(_query, outcome, _order, groups, count, _keep, CancellationToken.None);

                // In the order they were met, as the groups of a lane are numbered; the worst kept,
                // numbered again with them, is the frontier no later row past it joins; on an extreme,
                // its value, read before the groups are numbered again.
                int worst = best[kept - 1];
                partition.TopEdge = _extreme && kept == _keep ? Edge(outcome, worst) : null;
                Array.Sort(best, 0, kept);
                partition.Keep(best.AsSpan(0, kept));
                partition.TopFrontier = _narrows && kept == _keep ? Array.BinarySearch(best, 0, kept, worst) : -1;

                // The order the lane kept by is let go, not delivered.
                memory?.LetGo((long)kept * sizeof(int));
            }
            finally
            {
                QueryArrays.Return(memory, groups);
            }
        }

        if (final)
        {
            Interlocked.Add(ref _peak, partition.PeakGroups);
            partition.PeakGroups = 0;
        }
    }

    /// <summary>
    /// The key of 64 bits of <paramref name="group"/>'s extreme in the order (<see cref="ValuesOrder"/>):
    /// the edge a row's value must reach to join the top; null when the value is null, or not a number
    /// read into such keys.
    /// </summary>
    private long? Edge(AggregationOutcome outcome, int group)
    {
        ResultColumn column = _order!.Keys[0].Field.Column(_query.Keys);
        if (!column.ReadsOrder)
        {
            return null;
        }

        ColumnOrder? order = column.OrderOf(outcome, [group], Descending);
        try
        {
            return order?.KeyAt(0);
        }
        finally
        {
            order?.Release();
        }
    }
}

/// <summary>
/// The rows of a batch that can still join a top on a column's largest value, from the largest, or its
/// smallest, from the smallest (PLAN-HIGH-CARDINALITY, H7): those whose value reaches the edge, the key
/// of the <c>k</c>-th best a lane holds, keys of 64 bits as the order's (<see cref="ValuesOrder"/>), a
/// value's complement from the largest. A null joins no top a lane holds <c>k</c> values of.
/// </summary>
internal static class ValueFrontier
{
    /// <summary>
    /// Narrows <paramref name="selection"/> into <paramref name="narrowed"/> to the rows whose value of
    /// <paramref name="node"/>, a column of <paramref name="shape"/>, reaches <paramref name="edge"/>;
    /// false, leaving every row, when the column is not of signed integers in canonical form.
    /// </summary>
    internal static bool Narrow(CanonicalArena arena, int node, ColumnShape shape, int rows, ReadOnlySpan<ulong> selection, long edge, bool descending, Span<ulong> narrowed)
    {
        if (shape.Kind != StorageKind.Primitive || EncodedForms.EncodingOf(arena, node) != ColumnEncoding.Canonical)
        {
            return false;
        }

        return shape.PType switch
        {
            PType.I64 => Narrow<long>(arena, node, rows, selection, edge, descending, narrowed),
            PType.I32 => Narrow<int>(arena, node, rows, selection, edge, descending, narrowed),
            PType.I16 => Narrow<short>(arena, node, rows, selection, edge, descending, narrowed),
            PType.I8 => Narrow<sbyte>(arena, node, rows, selection, edge, descending, narrowed),
            _ => false,
        };
    }

    private static bool Narrow<T>(CanonicalArena arena, int node, int rows, ReadOnlySpan<ulong> selection, long edge, bool descending, Span<ulong> narrowed)
        where T : unmanaged, IBinaryInteger<T>, ISignedNumber<T>
    {
        T[] scratch = [];
        ReadOnlySpan<T> values = FixedReader.Values(arena, node, StorageKind.Primitive, ref scratch, out ReadOnlySpan<ulong> validity);
        int words = (rows + 63) >> 6;
        for (int w = 0; w < words; w++)
        {
            ulong word = selection.IsEmpty ? (w < words - 1 || (rows & 63) == 0 ? ulong.MaxValue : (1UL << (rows & 63)) - 1) : selection[w];
            if (!validity.IsEmpty)
            {
                word &= validity[w];
            }

            if (word == ulong.MaxValue)
            {
                narrowed[w] = Reached(values.Slice(w << 6, 64), edge, descending);
                continue;
            }

            ulong kept = 0;
            while (word != 0)
            {
                int bit = BitOperations.TrailingZeroCount(word);
                long value = long.CreateTruncating(values[(w << 6) + bit]);
                kept |= (descending ? ~value : value) <= edge ? 1UL << bit : 0;
                word &= word - 1;
            }

            narrowed[w] = kept;
        }

        return true;
    }

    /// <summary>
    /// The bits of the 64 <paramref name="values"/> of a word every row of which is kept that reach the
    /// edge, compared with no branch: taken from its bits a row at a time, each row waited on the one
    /// before it to clear its bit, and the frontier took three quarters of a top by a column's largest
    /// value (PLAN-HIGH-CARDINALITY, profiling).
    /// </summary>
    private static ulong Reached<T>(ReadOnlySpan<T> values, long edge, bool descending)
        where T : unmanaged, IBinaryInteger<T>, ISignedNumber<T>
    {
        // From the largest, a value's key is its complement: at most the edge when the value is at least the edge's.
        ulong reached = 0;
        if (descending)
        {
            long least = ~edge;
            for (int i = 0; i < values.Length; i++)
            {
                reached |= (long.CreateTruncating(values[i]) >= least ? 1UL : 0UL) << i;
            }
        }
        else
        {
            for (int i = 0; i < values.Length; i++)
            {
                reached |= (long.CreateTruncating(values[i]) <= edge ? 1UL : 0UL) << i;
            }
        }

        return reached;
    }
}
