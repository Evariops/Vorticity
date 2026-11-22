using System.Threading;

namespace Vorticity;

/// <summary>
/// A reservation a budget granted (<see cref="QueryMemoryBudget.ReserveAsync"/>): its bytes, counted under the budget and the
/// process's until it is disposed. A budget it funds (<see cref="QueryMemoryBudget.HardChild(MemoryGrant, bool)"/>) rents within
/// its bytes without asking the budget again; the grant goes back whole once that budget holds nothing.
/// </summary>
internal sealed class MemoryGrant
{
    private long _bytes;

    internal MemoryGrant(QueryMemoryBudget budget, long bytes)
    {
        Budget = budget;
        _bytes = bytes;
        Bytes = bytes;
    }

    /// <summary>The budget that granted it.</summary>
    internal QueryMemoryBudget Budget { get; }

    /// <summary>The bytes granted.</summary>
    internal long Bytes { get; }

    /// <summary>Hands the bytes over to a budget the grant funds, which gives them back itself (<see cref="QueryMemoryBudget.CloseGrant"/>): <see cref="Release"/> does nothing after.</summary>
    internal long TakeOver() => Interlocked.Exchange(ref _bytes, 0);

    /// <summary>Gives the bytes back to the budget, once, unless a budget took them over: the reservations that wait for room take their turn then.</summary>
    internal void Release()
    {
        long bytes = Interlocked.Exchange(ref _bytes, 0);
        if (bytes > 0)
        {
            Budget.Release(bytes);
        }
    }
}
