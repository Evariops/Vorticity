using System;

namespace Vorticity.Execution;

/// <summary>
/// The order a batch's items run in, longest first by what each took the time before: the reading
/// or writing thread claims the first while the others wake, and the item that holds every batch is
/// not the one that waits for a thread.
/// </summary>
/// <remarks>
/// The order is kept from one batch to the next and sorted again in place. Durations change little
/// between batches, so the insertion sort meets an order almost sorted and takes a pass; sorted again
/// from the items' own order it took n²/4 steps a batch, a million for 2 000 columns.
/// </remarks>
internal sealed class LongestFirst
{
    private readonly int[] _order;

    /// <summary>The order of <paramref name="items"/> items, their own to start with.</summary>
    internal LongestFirst(int items)
    {
        _order = new int[items];
        for (int i = 0; i < items; i++)
        {
            _order[i] = i;
        }
    }

    /// <summary>
    /// Sorts the kept order by <paramref name="took"/>, longest first, ties in the order they had,
    /// and lays it out in <paramref name="into"/>, which holds an item each.
    /// </summary>
    internal void Order(ReadOnlySpan<long> took, Span<int> into)
    {
        int[] order = _order;
        for (int i = 1; i < order.Length; i++)
        {
            int item = order[i];
            long ticks = took[item];
            int at = i;
            while (at > 0 && took[order[at - 1]] < ticks)
            {
                order[at] = order[at - 1];
                at--;
            }

            order[at] = item;
        }

        order.CopyTo(into);
    }
}
