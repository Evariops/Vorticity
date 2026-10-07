using System;
using System.Buffers;
using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Vorticity.Aggregating;

/// <summary>
/// The scratch arrays of the steps that grow with the groups — the order of a result, its top, the
/// sort of the rows a fetch reads, a lane's top — rented from the shared pool under the query's
/// memory (PLAN-HIGH-CARDINALITY, H2, decision 13): reserved before they are rented, at the length
/// the pool hands out, and given back with them. What the pool keeps of them afterwards is the rest
/// of the process's memory, which the process's budget reads at its next full collection. An array
/// under a page goes uncounted both ways, as a shelf's.
/// </summary>
internal static class QueryArrays
{
    /// <summary>The least array counted: a page.</summary>
    private const long LeastCounted = 4096;

    /// <summary>An array of <paramref name="length"/> elements at least, from the shared pool, reserved first under <paramref name="memory"/> for <paramref name="what"/>.</summary>
    /// <exception cref="VortexMemoryException">The query's budget does not grant the array.</exception>
    internal static T[] Rent<T>(QueryMemory? memory, int length, string what)
    {
        if (memory is not null && Bytes<T>(Pooled(length)) is long bytes and >= LeastCounted)
        {
            if (!memory.TryGrow(bytes))
            {
                throw memory.Exceeded(what, -1, bytes);
            }

            memory.Measure(bytes);
        }

        return ArrayPool<T>.Shared.Rent(length);
    }

    /// <summary>An array <see cref="Rent{T}"/> handed out, back to the shared pool, its bytes given back to <paramref name="memory"/>.</summary>
    internal static void Return<T>(QueryMemory? memory, T[] array)
    {
        if (memory is not null && Bytes<T>(array.Length) is long bytes and >= LeastCounted)
        {
            memory.Shrink(bytes);
            memory.Measure(-bytes);
        }

        ArrayPool<T>.Shared.Return(array);
    }

    /// <summary>The length the shared pool hands out for <paramref name="length"/>: its power of two from sixteen, past a gigabyte of elements the length itself.</summary>
    private static int Pooled(int length) => length <= 1 << 30 ? (int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(length, 16)) : length;

    private static long Bytes<T>(int length) => (long)length * Unsafe.SizeOf<T>();
}

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
    private long _peak;
    private long _measured;
    private int _left;

    /// <summary>A query's memory under <paramref name="budget"/>, one more query to share its ceiling with until it is given back.</summary>
    internal QueryMemory(QueryMemoryBudget budget)
    {
        _budget = budget;
        budget.Enter();
    }

    /// <summary>The bytes the query holds of its budget.</summary>
    internal long Held => Volatile.Read(ref _held);

    /// <summary>The most bytes the query held of its budget at once.</summary>
    internal long Peak => Volatile.Read(ref _peak);

    /// <summary>The ceiling of the query's budget, as it stands.</summary>
    internal long Ceiling => _budget.CeilingBytes;

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

        Raise(Interlocked.Add(ref _held, bytes));
        return true;
    }

    /// <summary>The most bytes held at once, raised to <paramref name="held"/> when it passes it.</summary>
    private void Raise(long held)
    {
        long peak = Volatile.Read(ref _peak);
        while (held > peak)
        {
            long seen = Interlocked.CompareExchange(ref _peak, held, peak);
            if (seen == peak)
            {
                return;
            }

            peak = seen;
        }
    }

    /// <summary>
    /// Whether the budget would grant <paramref name="bytes"/> more now, without reserving them: a lane
    /// asks it before a batch, to turn to the core while its table can still be emptied into it. Past
    /// the budget's threshold, a query over its share is told no first.
    /// </summary>
    internal bool CanGrow(long bytes) => _budget.CanReserve(bytes, Held);

    /// <summary>Reserves and measures <paramref name="bytes"/> the result holds until it is delivered, for <paramref name="what"/>; past the budget, the query fails.</summary>
    /// <exception cref="VortexMemoryException">The query's budget does not grant them.</exception>
    internal void Hold(long bytes, string what)
    {
        if (!TryGrow(bytes))
        {
            throw Exceeded(what, -1, bytes);
        }

        Measure(bytes);
    }

    /// <summary>Gives back <paramref name="bytes"/> <see cref="Hold"/> took for scratch the query let go: in the heap until a collection takes it.</summary>
    internal void LetGo(long bytes)
    {
        Shrink(bytes);
        Measure(-bytes);
    }

    /// <summary>
    /// Reserves <paramref name="bytes"/> past its budget's ceiling, a lane's overdraft before it turns to
    /// the core (H4, milestone 2); past what the process may hold, the query fails instead.
    /// </summary>
    /// <exception cref="VortexMemoryException">The process cannot take the overdraft.</exception>
    internal void Force(long bytes)
    {
        if (!_budget.Force(bytes))
        {
            throw Exceeded("group by", -1, bytes);
        }

        Raise(Interlocked.Add(ref _held, bytes));
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

        if (Interlocked.Exchange(ref _left, 1) == 0)
        {
            _budget.Leave();
        }
    }

    /// <summary>
    /// The exception a query fails with when <paramref name="what"/>, holding <paramref name="groups"/>
    /// groups (-1 when a table that grows does not tell), asks for <paramref name="asking"/> bytes its
    /// budget does not grant.
    /// </summary>
    internal VortexMemoryException Exceeded(string what, long groups, long asking)
    {
        QueryMemoryBudget process = QueryMemoryBudget.Process;
        string holds = groups >= 0
            ? string.Create(CultureInfo.InvariantCulture, $"holds {groups:N0} groups in {Held:N0} bytes")
            : string.Create(CultureInfo.InvariantCulture, $"holds {Held:N0} bytes");
        int active = _budget.ActiveQueries;
        string shared = active > 1
            ? string.Create(CultureInfo.InvariantCulture, $"; {active} queries share it, a share of {_budget.CeilingBytes / active:N0} each once it is seven eighths full")
            : string.Empty;
        return new VortexMemoryException(string.Create(
            CultureInfo.InvariantCulture,
            $"The {what} {holds} and asks for {asking:N0} more, past its memory budget of {_budget.CeilingBytes:N0} bytes, {_budget.ReservedBytes:N0} of which every query under it holds{shared}; the process leaves its queries {process.CeilingBytes:N0} bytes, the rest of its memory holding {process.RestBytes:N0}. Give its session a larger QueryMemoryBudget, group by fewer keys at once, or lower the degree of parallelism: each lane holds a table of the groups it meets."));
    }
}
