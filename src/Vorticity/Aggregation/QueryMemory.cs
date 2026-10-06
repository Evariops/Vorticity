using System;
using System.Globalization;
using System.Threading;

namespace Vorticity.Aggregating;

/// <summary>
/// What one query holds of its <see cref="QueryMemoryBudget"/> (PLAN-HIGH-CARDINALITY, H2): reserved at
/// the rare points where its memory grows, a lane's table after a batch, a part of a merge once built;
/// brought down to its result once its groups are merged, and given back when the result is delivered,
/// or when the query fails. Beside what it reserves, what its tables hold, as measured at the same
/// points: the process's budget tells the rest of the process's memory from it.
/// </summary>
/// <remarks>
/// A caller who leaves a result unread and never disposes its enumerator would keep the reservation
/// for good, and the budget would shrink by it for every query after: the finalizer gives it back
/// once nothing holds the result.
/// </remarks>
internal sealed class QueryMemory : IDisposable
{
    /// <summary>
    /// The least a lane reserves ahead: a megabyte, so that a table that grows reserves once a megabyte
    /// rather than once a batch, and the lanes meet on the budget's count that much less often.
    /// </summary>
    internal const long Chunk = 1 << 20;

    private readonly QueryMemoryBudget _budget;
    private long _held;
    private long _measured;

    internal QueryMemory(QueryMemoryBudget budget) => _budget = budget;

    /// <summary>The bytes the query holds of its budget.</summary>
    internal long Held => Volatile.Read(ref _held);

    /// <summary>The bytes its tables hold, as last measured: at most what it holds of its budget, which keeps room for their growth.</summary>
    internal long Measured => Volatile.Read(ref _measured);

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

    /// <summary>Its tables hold <paramref name="delta"/> bytes more, or less: what they let go stays in the heap until a collection takes it.</summary>
    internal void Measure(long delta)
    {
        if (delta != 0)
        {
            Interlocked.Add(ref _measured, delta);
            _budget.Measure(delta);
        }
    }

    /// <summary>Arrays of <paramref name="bytes"/> its tables replaced with larger ones: in the heap until a collection takes them.</summary>
    internal void Discard(long bytes)
    {
        if (bytes > 0)
        {
            _budget.Discard(bytes);
        }
    }

    /// <summary>Keeps <paramref name="bytes"/> of what the query holds and measures, the result it delivers, and gives back the rest.</summary>
    internal void Keep(long bytes)
    {
        long held = Held;
        if (held > bytes)
        {
            Shrink(held - bytes);
        }

        Measure(bytes - Measured);
    }

    /// <summary>Gives back everything the query holds.</summary>
    public void Dispose()
    {
        GiveBack();
        GC.SuppressFinalize(this);
    }

    ~QueryMemory() => GiveBack();

    private void GiveBack()
    {
        long held = Interlocked.Exchange(ref _held, 0);
        if (held != 0)
        {
            _budget.Release(held);
        }

        long measured = Interlocked.Exchange(ref _measured, 0);
        if (measured != 0)
        {
            _budget.Measure(-measured);
        }
    }

    /// <summary>The exception a query fails with when <paramref name="what"/>, holding <paramref name="groups"/> groups, asks for <paramref name="asking"/> bytes its budget does not grant.</summary>
    internal VortexMemoryException Exceeded(string what, long groups, long asking)
    {
        QueryMemoryBudget process = QueryMemoryBudget.Process;
        return new VortexMemoryException(string.Create(
            CultureInfo.InvariantCulture,
            $"The {what} holds {groups:N0} groups in {Held:N0} bytes and asks for {asking:N0} more, past its memory budget of {_budget.CeilingBytes:N0} bytes, {_budget.ReservedBytes:N0} of which every query under it holds; the process leaves its queries {process.CeilingBytes:N0} bytes, the rest of its memory holding {process.RestBytes:N0}. Give its session a larger QueryMemoryBudget, group by fewer keys at once, or lower the degree of parallelism: each lane holds a table of the groups it meets."));
    }
}
