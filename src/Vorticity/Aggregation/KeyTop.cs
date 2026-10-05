using System;
using System.Buffers;
using System.Threading;

namespace Vorticity.Aggregating;

/// <summary>
/// The groups a blocking pass keeps on each lane when the query takes the first <c>k</c> groups of
/// an order on the key: the <c>k</c> best keys the lane has met, trimmed back to <c>k</c> once they
/// pass one and a half times as many, by the ranking the result itself uses.
/// </summary>
/// <remarks>
/// Exact, whatever the order the rows come in. A key dropped lies past the <c>k</c>-th best the lane
/// holds, and that one only gets better as keys come, so a key dropped never comes back into the top:
/// a group made for it again is dropped again, and the groups kept hold every one of their rows. A key
/// of the whole top has fewer than <c>k</c> better ones on its own lane, so it is kept there, and the
/// lanes' groups merged hold the whole top.
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

    private KeyTop(AggregationQuery query, GroupOrder? order, int keep)
    {
        _query = query;
        _order = order;
        _keep = keep;
        _ceiling = keep + ((keep + 1) / 2);
        Descending = order is { } ordered && ordered.Keys[0].Descending;
        _narrows = order is not null && query.Keys.Length == 1 && order.Keys.Length == 1;
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
    /// alone, and the windows that follow it at once, the result's own after them when nothing else
    /// comes, reach no further than its first <see cref="MostKept"/> groups. Anything else first, a
    /// filter on the groups, could take groups out of the top the trims kept.
    /// </summary>
    internal static KeyTop? Of(AggregationQuery query)
    {
        GroupOperator[] operators = query.Operators;
        if (!query.Plan.Grouped || operators.Length == 0 || operators[0] is not GroupOrder order)
        {
            return null;
        }

        foreach (OrderKey key in order.Keys)
        {
            if (key.Field.Component < 0)
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

        return reach is > 0 and <= MostKept ? new KeyTop(query, order, (int)reach) : null;
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

        return reach is > 0 and <= MostKept ? new KeyTop(query, null, (int)reach) : null;
    }

    /// <summary>A window of <paramref name="skip"/> and <paramref name="take"/> over the groups from <paramref name="start"/> on, of which those before <paramref name="reach"/> are left.</summary>
    private static (long Start, long Reach) Narrowed(long start, long reach, long skip, long take)
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
            int[] groups = ArrayPool<int>.Shared.Rent(count);
            try
            {
                for (int g = 0; g < count; g++)
                {
                    groups[g] = g;
                }

                AggregationOutcome outcome = new AggregationOutcome(_query.Plan, partition.Slots, keys, []);
                (int[] best, int kept) = GroupSelection.Order(_query, outcome, _order, groups, count, _keep, CancellationToken.None);

                // In the order they were met, as the groups of a lane are numbered; the worst kept,
                // numbered again with them, is the frontier no later row past it joins.
                int worst = best[kept - 1];
                Array.Sort(best, 0, kept);
                partition.Keep(best.AsSpan(0, kept));
                partition.TopFrontier = _narrows && kept == _keep ? Array.BinarySearch(best, 0, kept, worst) : -1;
            }
            finally
            {
                ArrayPool<int>.Shared.Return(groups);
            }
        }

        if (final)
        {
            Interlocked.Add(ref _peak, partition.PeakGroups);
            partition.PeakGroups = 0;
        }
    }
}
