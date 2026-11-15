using System;
using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading;
using Vorticity.Buffers;

namespace Vorticity.Aggregating;

/// <summary>
/// What one query holds of its <see cref="QueryMemoryBudget"/>: reserved at
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
    private long _beside;
    private int _left;

    /// <summary>A query's memory under <paramref name="budget"/>, one more query to share its ceiling with until it is given back.</summary>
    internal QueryMemory(QueryMemoryBudget budget)
    {
        _budget = budget;
        budget.Enter();
    }

    /// <summary>The memory of a question that starts here, under <paramref name="session"/>'s budget, the process's when it gives none.</summary>
    internal static QueryMemory Start(VortexSession session) => new QueryMemory(QueryMemoryBudget.Of(session));

    /// <summary>
    /// The memory of the question <paramref name="spec"/> serves, or, for a question that starts here, one made under
    /// <paramref name="session"/>'s budget: the lease gives back only what it made.
    /// </summary>
    internal static Lease For(ScanSpec spec, VortexSession session) =>
        spec.Memory is { } memory ? new Lease(memory, owned: false) : new Lease(Start(session), owned: true);

    /// <summary>
    /// What a step that reads ahead reserved: <paramref name="bytes"/> of the question's memory, given back when the step
    /// ends; or a memory of its own, for a step of no question, given back whole. The default reserves nothing.
    /// </summary>
    internal readonly struct Reservation(QueryMemory? memory, long bytes, bool owned) : IDisposable
    {
        /// <summary>Whether it reserved anything.</summary>
        internal bool Holds => memory is not null;

        public void Dispose()
        {
            if (memory is null)
            {
                return;
            }

            if (owned)
            {
                memory.Dispose();
            }
            else
            {
                memory.Shrink(bytes);
            }
        }
    }

    /// <summary>A question's memory as a step of it holds it: given back when the step ends only when the step started the question.</summary>
    internal readonly struct Lease(QueryMemory memory, bool owned) : IDisposable
    {
        /// <summary>The question's memory.</summary>
        internal QueryMemory Memory => memory;

        /// <summary>Whether the step started the question, and gives its memory back.</summary>
        internal bool Owned => owned;

        public void Dispose()
        {
            if (owned)
            {
                memory.Dispose();
            }
        }
    }

    // The account the query's blocks are counted in, made at the first block it rents.
    private WorkAccount? _account;

    /// <summary>
    /// The account the query's blocks are counted in, its result's batches and what its sorts and tops build: under its
    /// budget, given back whole with the rest of the query's memory, whatever a caller still holds of its result.
    /// </summary>
    internal WorkAccount Account
    {
        get
        {
            if (Volatile.Read(ref _account) is { } account)
            {
                return account;
            }

            WorkAccount made = new WorkAccount(_budget, "query");
            WorkAccount? won = Interlocked.CompareExchange(ref _account, made, null);
            if (won is null)
            {
                return made;
            }

            GC.SuppressFinalize(made);
            return won;
        }
    }

    /// <summary>
    /// The account the blocks of the work <paramref name="memory"/> counts are rented under; for work of no question, an
    /// estimate's sample folded, the caller's, counted nowhere: what it builds may reach a caller, whom no account outlives.
    /// </summary>
    internal static MemoryAccount AccountOf(QueryMemory? memory) => memory?.Account ?? MemoryAccount.Caller;

    /// <summary>The bytes the query holds of its budget.</summary>
    internal long Held => Volatile.Read(ref _held);

    /// <summary>The most bytes the query held of its budget at once.</summary>
    internal long Peak => Volatile.Read(ref _peak);

    // What a plan keeps for this query alone (the layout of its key's tuples, its long texts numbered once for every lane),
    // dropped with the query: the first owner's in place, any other's in a list made for it.
    private object? _stateOwner;
    private object? _state;
    private System.Collections.Generic.List<(object Owner, object State)>? _moreStates;

    /// <summary>
    /// What <paramref name="owner"/> keeps for this query, made by <paramref name="make"/> the first time it is asked for, the
    /// same for every lane after, and dropped with the query: a table keyed by the query would make a weak table's container
    /// a question, and its finalizer.
    /// </summary>
    internal T StateOf<T>(object owner, Func<object, QueryMemory, T> make)
        where T : class
    {
        lock (this)
        {
            if (ReferenceEquals(_stateOwner, owner))
            {
                return (T)_state!;
            }

            if (_moreStates is { } more)
            {
                foreach ((object kept, object state) in more)
                {
                    if (ReferenceEquals(kept, owner))
                    {
                        return (T)state;
                    }
                }
            }

            T made = make(owner, this);
            if (_stateOwner is null)
            {
                (_stateOwner, _state) = (owner, made);
            }
            else
            {
                (_moreStates ??= []).Add((owner, made));
            }

            return made;
        }
    }

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
    /// the core; past what the process may hold, the query fails instead.
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

    /// <summary>
    /// Reserves and measures <paramref name="bytes"/> the query holds beside its groups for its whole run,
    /// which its lanes share: the long texts of a key's tuples (<see cref="TupleLayout"/>). Past the
    /// ceiling when the budget refuses them, as a table that grows within a batch: the lanes' next readings
    /// spill or turn to the core. Kept with the result until it is delivered.
    /// </summary>
    internal void HoldBeside(long bytes)
    {
        if (!TryGrow(bytes))
        {
            Force(bytes);
        }

        Measure(bytes);
        Interlocked.Add(ref _beside, bytes);
    }

    /// <summary>
    /// Keeps <paramref name="bytes"/> of what the query holds and measures, the result it delivers, and
    /// what it holds beside its groups (<see cref="HoldBeside"/>), and gives back the rest.
    /// </summary>
    internal void Keep(long bytes)
    {
        // What it holds beside its groups stays, whether the bytes count it (what it holds) or not (a
        // result's own).
        long held = Held;
        long kept = Math.Max(bytes, Math.Min(held, bytes + Volatile.Read(ref _beside)));
        if (held > kept)
        {
            Shrink(held - kept);
        }

        Measure(kept - Measured);
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

        // The blocks' account closed: a result's batches are the caller's from now on, counted nowhere.
        Volatile.Read(ref _account)?.Dispose();

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
