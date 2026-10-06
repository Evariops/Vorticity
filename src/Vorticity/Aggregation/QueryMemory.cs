using System;
using System.Globalization;
using System.Threading;

namespace Vorticity.Aggregating;

/// <summary>
/// What one query holds of its <see cref="QueryMemoryBudget"/> (PLAN-HIGH-CARDINALITY, H2): reserved at
/// the rare points where its memory grows, a lane's table after a batch, a part of a merge once built,
/// and given back all at once when the query has merged its groups, or failed.
/// </summary>
internal sealed class QueryMemory : IDisposable
{
    /// <summary>
    /// The least a lane reserves ahead: a megabyte, so that a table that grows reserves once a megabyte
    /// rather than once a batch, and the lanes meet on the budget's count that much less often.
    /// </summary>
    internal const long Chunk = 1 << 20;

    private readonly QueryMemoryBudget _budget;
    private long _held;

    internal QueryMemory(QueryMemoryBudget budget) => _budget = budget;

    /// <summary>The bytes the query holds of its budget.</summary>
    internal long Held => Volatile.Read(ref _held);

    /// <summary>Reserves <paramref name="bytes"/> more; false, and nothing reserved, past the budget's ceiling or the process's.</summary>
    internal bool TryGrow(long bytes)
    {
        if (bytes <= 0)
        {
            return true;
        }

        if (!_budget.TryReserve(bytes))
        {
            return false;
        }

        Interlocked.Add(ref _held, bytes);
        return true;
    }

    /// <summary>Gives back <paramref name="bytes"/> the query held.</summary>
    internal void Shrink(long bytes)
    {
        Interlocked.Add(ref _held, -bytes);
        _budget.Release(bytes);
    }

    /// <summary>Gives back everything the query holds.</summary>
    public void Dispose()
    {
        long held = Interlocked.Exchange(ref _held, 0);
        if (held != 0)
        {
            _budget.Release(held);
        }
    }

    /// <summary>The exception a query fails with when <paramref name="what"/>, holding <paramref name="groups"/> groups, asks for <paramref name="asking"/> bytes its budget does not grant.</summary>
    internal VortexMemoryException Exceeded(string what, long groups, long asking) =>
        new VortexMemoryException(string.Create(
            CultureInfo.InvariantCulture,
            $"The {what} holds {groups:N0} groups in {Held:N0} bytes and asks for {asking:N0} more, past its memory budget of {_budget.CeilingBytes:N0} bytes, {_budget.ReservedBytes:N0} of which every query under it holds. Give its session a larger QueryMemoryBudget, group by fewer keys at once, or lower the degree of parallelism: each lane holds a table of the groups it meets."));
}
