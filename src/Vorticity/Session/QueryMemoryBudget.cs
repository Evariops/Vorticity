using System;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Buffers;

namespace Vorticity;

/// <summary>
/// The memory the work of the sessions that share it may hold at once: the segments their scans read and
/// the batches built from them, the tables of their queries' groups, lane by lane, and the parts their
/// merges build; given as a session's <see cref="VortexSessionOptions.JobMemoryBudget"/>, what its
/// writers and its datasets' background work hold. A host makes one and gives it to each session it
/// shares through <see cref="VortexSessionOptions.MemoryBudget"/>; a session given none shares the
/// process's, whose ceiling is what the process's limit leaves past the rest of its memory.
/// </summary>
/// <remarks>
/// <para>
/// A query reserves in its budget, and the budget in the process's: the sum of the ceilings the host
/// sets may pass its limits, the process's ceiling stays the last guard. A query that needs more than
/// its budget grants fails with a <see cref="VortexMemoryException"/>, everything it held given back.
/// </para>
/// <para>
/// The budget belongs to the host: a session disposed leaves it to the sessions that share it. What a
/// query delivers once its groups are merged, its result, is the caller's, and no longer counted.
/// </para>
/// </remarks>
public sealed class QueryMemoryBudget
{
    /// <summary>
    /// The share of the ceiling, one in this many, within which the reservations have the memory kept let go of that much
    /// above its floors before they need it, and the least a reservation short of room has it let go of. A quarter, an
    /// eighth and a sixteenth compacted the backlog bench in 53 to 56 s under 2 GiB, at a peak resident of 1 174, 1 191 and
    /// 1 368 MiB (2026-11-16).
    /// </summary>
    private const int KeptMarginShare = 8;

    /// <summary>
    /// The share of a container's limit, one in this many, the process keeps as a margin: what the container uses past
    /// its last reading, a timer's period at most. What no reservation and no holder of kept memory counted grew by 155 MiB
    /// in a second at most, while a backlog compacted under 2 GiB, whose eighth is 256 MiB, and by 150 MiB under 4 GiB
    /// (measured 2026-11-16).
    /// </summary>
    private const int ContainerMarginShare = 8;

    /// <summary>
    /// How often the process's budget reads its container's memory again, in milliseconds: within a second, what nothing
    /// counts grows by less than the margin (<see cref="ContainerMarginShare"/>, measured 2026-11-16).
    /// </summary>
    internal const int ContainerReadMilliseconds = 1_000;

    // What the process used against its limit at the last reading: a container's, or the limit the host set and the
    // process's resident memory (VortexMemory); null when nothing holds the process but the collector.
    private static ContainerMemory? s_reading = VortexMemory.Read();

    /// <summary>
    /// The share of the process's limit, one in this many, the jobs' own budget holds by default (<see cref="Jobs"/>). A
    /// backlog of 9 million metric records compacted in 6 jobs and 60 s under 2 GiB at a half, 10 jobs and 68 s at a third;
    /// under 4 GiB, 3 jobs and 50 s, 5 jobs and 60 s. The whole limit made one job of 41 s under 4 GiB but 7 jobs and 93 s
    /// under 2 GiB, the rest of the process held in what the collector leaves: a host whose process runs jobs alone gives
    /// them more through <see cref="VortexSessionOptions.JobMemoryBudget"/> from 4 GiB up (measured 2026-11-16).
    /// </summary>
    internal const int JobsShare = 2;

    // The budget this one reserves in too: the process's, or a parent's of a fixed ceiling; null for the process's own.
    private readonly QueryMemoryBudget? _process;

    // The host's ceiling; the process's limit, less a margin, for the process's own. What its queries
    // hold, the most they held at once, and the queries that hold memory under it now.
    private readonly long _ceiling;
    private long _reserved;
    private long _peak;
    private int _active;

    // A budget whose ceiling is a share of the process's limit, one in this many of it, read again as the limit moves; 0 for
    // a ceiling of its own. What it reserves asks the process too, as any budget's does.
    private readonly int _share;

    // Whether a reservation was refused since the budget was made: what tells a job that ran out of its budget.
    private int _refused;

    // A child funded by a reservation its parent granted (MemoryGrant): the grant's bytes, which the parent counts for it
    // already, and what is left of them, which the child's first reservations take before they ask the parent; -1 once
    // closed (CloseGrant); 0 for any other budget.
    private readonly long _granted;
    private long _grantLeft;

    // The reservations that wait for room (ReserveAsync), the oldest first, and how many: served in order as the work under
    // the budget gives memory back.
    private readonly Lock _waitGate = new Lock();
    private Waiter? _firstWaiter;
    private Waiter? _lastWaiter;
    private int _waiting;

    // The process's own: the bytes the queries' tables hold, as last measured; the rest of the
    // process, read after the last full collection; what the queries let go since, still in the heap;
    // whether a collection is being asked for, and the full collection after which the last one asked
    // for left the collector holding what it held.
    private long _measured;
    private Rest _rest = new Rest(-1, -1, -1, -1, 0, 0, 0);
    private long _pending;

    // The fewest bytes measured since the rest was last read from a collection: never more than what was measured when the
    // collector ran, which happened since. The heap a collection read, less what is measured when the budget reads it, left
    // the rest out by what the queries took between the two: 145 MiB in the heap, 227 measured by then, a rest read as none,
    // and eight queries under a heap held to 512 MiB were granted what the next collection found taken (2026-11-16).
    private long _measuredLow;

    // Taken by every writer of the rest: a give-back that wrote the rest it read before a collection's was read put the
    // collection's number back, and the next reservation read the same collection again, its let go and its reuse lost.
    private readonly object _restGate = new object();

    // The bytes measured work took back since the last full collection from what the pool keeps, parked or free in its slabs,
    // less those it gave back for the pool to keep: the rest's no more, or the rest's again when below zero. Read as none
    // below zero, the blocks a writer gave back went uncounted until the next collection, and a writer under a limit of 1 GiB
    // was granted a block of 64 MiB the heap had no room for, beside 252 MiB of them (2026-11-16).
    private long _reused;
    private int _collecting;
    private int _futile = -1;

    // The count of collections at which the reservations nearing the ceiling last had the memory kept let go of a share.
    private int _keptGivenAt = -1;

    // The process's own, held to a container's limit or the host's: what the process used past what the registry knows, at
    // its last reading; and the timer that reads it again, made once something holds the process.
    private long _containerRest;
    private Timer? _containerTimer;

    /// <summary>A budget under which the queries of its sessions hold <paramref name="ceilingBytes"/> at most, together.</summary>
    /// <param name="ceilingBytes">The bytes they may hold at once.</param>
    /// <exception cref="ArgumentOutOfRangeException">The ceiling is not positive.</exception>
    public QueryMemoryBudget(long ceilingBytes)
        : this(ceilingBytes, Process)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ceilingBytes);
    }

    /// <summary>
    /// A budget under <paramref name="process"/> rather than the process's own, or the process's own when
    /// null. Internal for the tests: a parent of a fixed ceiling holds two budgets under it as the
    /// process does, without the process's ceiling, which moves with what the rest of the process holds.
    /// </summary>
    internal QueryMemoryBudget(long ceilingBytes, QueryMemoryBudget? process)
        : this(ceilingBytes, process, share: 0)
    {
    }

    /// <summary>
    /// The budget of the jobs: <paramref name="share"/>, one in this many of the process's limit, read again as the limit
    /// moves. What it reserves asks the process too, which weighs it against the rest of its memory.
    /// </summary>
    private QueryMemoryBudget(int share)
        : this(0, Process, share)
    {
    }

    private QueryMemoryBudget(long ceilingBytes, QueryMemoryBudget? process, int share, long granted = 0)
    {
        _ceiling = ceilingBytes;
        _process = process;
        _share = share;
        _granted = granted;
        _grantLeft = granted;
        Account = new BudgetAccount(this, process is null ? "library's own work" : "work");
        if (process is null && Volatile.Read(ref s_reading) is not null)
        {
            Watch();
        }
    }

    /// <summary>
    /// Reads again what the process uses against its limit, once the host set one (<see cref="VortexMemory.SetLimit"/>), and
    /// from then on each second: the process's own budget alone.
    /// </summary>
    internal void ReadMemory()
    {
        Volatile.Write(ref s_reading, VortexMemory.Read());
        if (_process is null && Volatile.Read(ref s_reading) is not null)
        {
            Watch();
        }
    }

    /// <summary>
    /// Reads the process's memory now, and starts the timer that reads it again off the threads that reserve, once. Made with
    /// no flow of the thread that starts it: its locals are no business of the timer's.
    /// </summary>
    private void Watch()
    {
        ReadContainer();
        if (Volatile.Read(ref _containerTimer) is not null)
        {
            return;
        }

        Timer timer;
        using (ExecutionContext.SuppressFlow())
        {
            timer = new Timer(static budget => ((QueryMemoryBudget)budget!).ReadContainer(), this, Timeout.Infinite, Timeout.Infinite);
        }

        if (Interlocked.CompareExchange(ref _containerTimer, timer, null) is null)
        {
            timer.Change(ContainerReadMilliseconds, ContainerReadMilliseconds);
        }
        else
        {
            timer.Dispose();
        }
    }

    /// <summary>
    /// The account of the work under the budget that is not a query and gives its blocks back itself, in a <c>finally</c> or
    /// at its own end: for the process's own, a sealed read's buffers, a sealing sink's frame, an index builder's rents.
    /// </summary>
    internal BudgetAccount Account { get; }

    /// <summary>
    /// The bytes the queries under the budget may hold at once: the host's ceiling; for the process's
    /// own, what its limit leaves past the rest of its memory, which moves as the process does.
    /// </summary>
    public long CeilingBytes => _process is null ? Available() : OwnCeiling;

    /// <summary>A child's own ceiling: its share of the process's limit as it stands, or the one it was given.</summary>
    private long OwnCeiling => _share > 0 ? ProcessLimit() / _share : _ceiling;

    /// <summary>
    /// Whether a block the work under the budget rents may never pass its ceiling: an account under it refuses the block
    /// rather than count it past (<see cref="QueryMemoryBudget.Force"/>), and the work that asked gives way. What a
    /// compaction job's budget is, whose blocks are its whole memory.
    /// </summary>
    internal bool Hard { get; init; }

    /// <summary>Whether a reservation under the budget was refused since it was made.</summary>
    internal bool WasRefused => Volatile.Read(ref _refused) != 0;

    /// <summary>The bytes they hold now.</summary>
    public long ReservedBytes => Volatile.Read(ref _reserved);

    /// <summary>The most bytes they held at once, since the budget was made.</summary>
    internal long PeakBytes => Volatile.Read(ref _peak);

    /// <summary>
    /// The process's budget, which every session shares unless given another. Its limit is the memory
    /// the collector may commit — the heap's hard limit when one is set, its share of a container's
    /// limit in a container, the machine's memory otherwise — and a container's own limit, read again as
    /// it runs, each less a margin. Of it, the work gets what the rest of the process leaves: a limit of
    /// 4 GB is not 4 GB for the queries. The jobs reserve in it too, unless a session gives them a budget
    /// of their own: one registry for both, and the shares the host sets.
    /// </summary>
    /// <remarks>Its ceiling is read again at each use (<see cref="ProcessLimit"/>), never from what it was made with.</remarks>
    internal static QueryMemoryBudget Process { get; } = new QueryMemoryBudget(0, process: null);

    /// <summary>
    /// The jobs' budget of every session given none of its own: <see cref="JobsShare"/>, one in this many, of the process's
    /// limit as it stands. Each block a job takes asks the process too, which weighs it against the rest of its memory as
    /// it weighs a question's, has the memory kept let go and the collector run before it refuses, and counts it so that
    /// the questions see it. A writer's account that forces past its budget is counted there and never refused.
    /// </summary>
    internal static QueryMemoryBudget Jobs { get; } = new QueryMemoryBudget(JobsShare);

    /// <summary>The budget the questions and scans of <paramref name="session"/> reserve in: its own, the process's when it gives none.</summary>
    internal static QueryMemoryBudget Of(VortexSession session) => session.Options.MemoryBudget ?? Process;

    /// <summary>The process's budget, which this one reserves in too: itself for the process's own.</summary>
    internal QueryMemoryBudget Root => _process?.Root ?? this;

    /// <summary>The budget the jobs and writers of <paramref name="session"/> reserve in: its own, the jobs' share of the process's limit when it gives none.</summary>
    internal static QueryMemoryBudget JobsOf(VortexSession session) => session.Options.JobMemoryBudget ?? Jobs;

    /// <summary>
    /// A budget under this one that refuses a block past its own ceiling and past this one's (<see cref="Hard"/>): what a
    /// compaction job's inputs, or its writer, rent under, so that the job learns what each held at most.
    /// </summary>
    internal QueryMemoryBudget HardChild(long ceilingBytes = long.MaxValue / 4, bool countsPast = false) =>
        new QueryMemoryBudget(ceilingBytes, this) { Hard = true, CountsPast = countsPast };

    /// <summary>
    /// Whether a hard budget counts past its ceiling a block its work could not reserve, the process bounding it
    /// (<see cref="Force"/>), rather than refuse it: what the budget of a compaction job's writer is, and the job's own
    /// for it. A writer cannot wait in the middle of a chunk it encodes, and does not yet reserve what it takes before it
    /// takes it: refused a block, it gave up the job and what the job had written. What it takes past its share, the
    /// job's inputs, which reserve before they take, find taken, and wait or end their merge at the key it reached. A
    /// reservation under such a budget is still told no at its ceiling.
    /// </summary>
    internal bool CountsPast { get; init; }

    /// <summary>
    /// <see cref="HardChild(long, bool)"/>, funded by <paramref name="grant"/>, a reservation this budget granted
    /// (<see cref="ReserveAsync"/>), which the child takes over: its first reservations take the grant's bytes, which this
    /// budget counts already, and ask this one for what passes them; its releases go back to this one, as any child's do.
    /// What is left of the grant goes back when the child's work closes it (<see cref="CloseGrant"/>).
    /// </summary>
    internal QueryMemoryBudget HardChild(MemoryGrant grant, bool countsPast = false) =>
        new QueryMemoryBudget(long.MaxValue / 4, this, share: 0, granted: grant.TakeOver()) { Hard = true, CountsPast = countsPast };

    /// <summary>What is left of the grant that funds the budget (<see cref="HardChild(MemoryGrant, bool)"/>): 0 for one no grant funds, or once closed.</summary>
    internal long GrantLeft => Math.Max(0, Volatile.Read(ref _grantLeft));

    /// <summary>
    /// Ends the grant that funds the budget, once its work is done: what is left of it goes back to the parent now; what the
    /// budget holds goes back as it is released, as it always does.
    /// </summary>
    internal void CloseGrant()
    {
        if (_granted == 0)
        {
            return;
        }

        long left = Interlocked.Exchange(ref _grantLeft, -1);
        if (left > 0)
        {
            _process!.Release(left);
        }
    }

    /// <summary>
    /// Reserves <paramref name="bytes"/> under this budget and the process's, waiting for room when the work under the budget
    /// holds it: the reservation takes its turn, the oldest first, and is taken in the same step as the room another gives
    /// back, so that none passes it; no thread waits meanwhile. Granted at once when the room is there. Null, nothing
    /// reserved, when it can never be granted: past the budget's ceiling, or nothing under the budget holds what would make
    /// room, which no release would then wake.
    /// </summary>
    /// <remarks>
    /// What a compaction job asks before it reads anything, holding nothing: a job of another table that holds the memory
    /// gives it back as it ends, and the job runs then at its width, where a block refused at its start gave it up and the
    /// next plan narrowed it. A job that holds memory never waits for more (the rule of the two cases): it writes a chunk
    /// early, or gives way.
    /// </remarks>
    /// <exception cref="OperationCanceledException">The wait was cancelled; nothing is reserved.</exception>
    internal ValueTask<MemoryGrant?> ReserveAsync(long bytes, CancellationToken cancellationToken)
    {
        if (bytes <= 0)
        {
            return new ValueTask<MemoryGrant?>(new MemoryGrant(this, 0));
        }

        if (bytes > Ceiling)
        {
            return new ValueTask<MemoryGrant?>((MemoryGrant?)null);
        }

        if (Volatile.Read(ref _waiting) == 0 && TryReserve(bytes))
        {
            return new ValueTask<MemoryGrant?>(new MemoryGrant(this, bytes));
        }

        cancellationToken.ThrowIfCancellationRequested();
        Waiter waiter = new Waiter(bytes) { Budget = this };
        lock (_waitGate)
        {
            if (_lastWaiter is null)
            {
                _firstWaiter = waiter;
            }
            else
            {
                _lastWaiter.Next = waiter;
            }

            _lastWaiter = waiter;
            Interlocked.Increment(ref _waiting);
        }

        if (cancellationToken.CanBeCanceled)
        {
            waiter.Cancellation = cancellationToken.UnsafeRegister(static (state, token) => ((Waiter)state!).Cancel(token), waiter);
        }

        // Room given back between the refusal above and the turn taken is served here, as is a budget that holds nothing.
        Serve();
        return new ValueTask<MemoryGrant?>(waiter.Task);
    }

    /// <summary>
    /// Serves the reservations that wait, the oldest first, while the room is there: each taken in the same step, its task
    /// ended outside the gate. The first that does not fit stops the turn, unless nothing under the budget holds memory: no
    /// release would wake it, and it is told no. A cancelled one leaves its turn.
    /// </summary>
    private void Serve()
    {
        // One turn at a time: a reservation taken in a turn may have the memory kept let go, whose release serves again on
        // the same thread, and would grant the head twice; a release on another thread meanwhile asks the turn to run again.
        while (Volatile.Read(ref _waiting) != 0)
        {
            if (Interlocked.CompareExchange(ref _serving, 1, 0) != 0)
            {
                Volatile.Write(ref _serveAgain, 1);
                return;
            }

            ServeTurn();
            Volatile.Write(ref _serving, 0);
            if (Interlocked.Exchange(ref _serveAgain, 0) == 0)
            {
                return;
            }
        }
    }

    // Whether a turn serves the waiting reservations now, and whether a release asked for another meanwhile.
    private int _serving;
    private int _serveAgain;

    /// <summary>One turn of <see cref="Serve"/>.</summary>
    private void ServeTurn()
    {
        Waiter? served = null;
        Waiter? lastServed = null;
        lock (_waitGate)
        {
            while (_firstWaiter is { } head)
            {
                bool cancelled = head.Task.IsCompleted;
                bool granted = !cancelled && CanReserve(head.Bytes) && TryReserve(head.Bytes);
                if (!cancelled && !granted && ReservedBytes > 0)
                {
                    break;
                }

                _firstWaiter = head.Next;
                if (_firstWaiter is null)
                {
                    _lastWaiter = null;
                }

                Interlocked.Decrement(ref _waiting);
                head.Next = null;
                if (!cancelled)
                {
                    // Kept in their order, ended in it.
                    head.Granted = granted;
                    if (lastServed is null)
                    {
                        served = head;
                    }
                    else
                    {
                        lastServed.Next = head;
                    }

                    lastServed = head;
                }
            }
        }

        // Ended past the gate: a continuation runs elsewhere (RunContinuationsAsynchronously), and a grant whose waiter was
        // cancelled meanwhile goes back.
        while (served is { } waiter)
        {
            served = waiter.Next;
            waiter.Next = null;
            waiter.End(this);
        }
    }

    /// <summary>A reservation waiting its turn: its bytes, its task, and the next one.</summary>
    private sealed class Waiter(long bytes) : TaskCompletionSource<MemoryGrant?>(TaskCreationOptions.RunContinuationsAsynchronously)
    {
        internal readonly long Bytes = bytes;
        internal Waiter? Next;
        internal bool Granted;
        internal CancellationTokenRegistration Cancellation;
        internal QueryMemoryBudget? Budget;

        /// <summary>Ends the wait, granted or told no; a grant no one takes any more goes back to <paramref name="budget"/>.</summary>
        internal void End(QueryMemoryBudget budget)
        {
            Cancellation.Dispose();
            if (!Granted)
            {
                TrySetResult(null);
            }
            else if (!TrySetResult(new MemoryGrant(budget, Bytes)))
            {
                budget.Release(Bytes);
            }
        }

        /// <summary>Leaves the turn at once, the ones behind it served when the room is there.</summary>
        internal void Cancel(CancellationToken token)
        {
            if (TrySetCanceled(token))
            {
                Budget?.Serve();
            }
        }
    }

    /// <summary>
    /// The process's own: the parts <see cref="RestBytes"/> is made of, as last read. What its heap held past the measured
    /// work at the last full collection, less what the work took back from the pool since; what a collection of the
    /// youngest generations saw held past that; what the collector kept committed past its heap; what the work let go
    /// since; what a container uses past what the registry knows; then the bytes the work measures now, and the fewest it
    /// measured since the rest was last read. For a tool that tells which part of the rest moves
    /// (<c>vortex-queries --memory-timeline</c>).
    /// </summary>
    internal (long Live, long Uncertain, long Reclaimable, long Pending, long Container, long Measured, long MeasuredLow) RestParts
    {
        get
        {
            Rest rest = Volatile.Read(ref _rest);
            return (LiveOf(rest), rest.Uncertain, rest.Reclaimable, Volatile.Read(ref _pending), Volatile.Read(ref _containerRest),
                Volatile.Read(ref _measured), Volatile.Read(ref _measuredLow));
        }
    }

    /// <summary>
    /// The rest of the process's memory: what its heap held past the queries' tables at the last full
    /// collection, what the collector kept committed past its heap, and what the queries let go since; in
    /// a container, what the container uses past what the registry knows, when that is more, which the
    /// collector's model does not see whole: what the native allocator and the runtime keep.
    /// </summary>
    internal long RestBytes
    {
        get
        {
            Rest rest = Volatile.Read(ref _rest);
            long modelled = LiveOf(rest) + rest.Uncertain + rest.Reclaimable + Volatile.Read(ref _pending);
            return Volatile.Read(ref _containerTimer) is null ? modelled : Math.Max(modelled, Volatile.Read(ref _containerRest));
        }
    }

    /// <summary>The bytes the work under the budget may hold: the host's ceiling; for the process's own, its limit as last read.</summary>
    private long Ceiling => _process is null ? ProcessLimit() : OwnCeiling;

    /// <summary>
    /// Reads the process's memory again, a container's or its resident memory under the host's limit, and what it uses past
    /// what the registry knows: the reservations, and the memory kept (<see cref="HeldMemory"/>). On the budget's timer, and
    /// after a collection the budget asked for.
    /// </summary>
    private void ReadContainer()
    {
        if (VortexMemory.Read() is { } reading)
        {
            Volatile.Write(ref s_reading, reading);
        }

        if (Volatile.Read(ref s_reading) is { } now)
        {
            Volatile.Write(ref _containerRest, Math.Max(0, now.WorkingSet - ReservedBytes - HeldMemory.TotalBytes()));
        }
    }

    /// <summary>The queries that hold memory under the budget now, those of its sessions; every query of the process, for the process's own.</summary>
    internal int ActiveQueries => Volatile.Read(ref _active);

    /// <summary>A query starts under the budget, and under the process's: one more to share their ceilings with.</summary>
    internal void Enter()
    {
        Interlocked.Increment(ref _active);
        _process?.Enter();
    }

    /// <summary>A query under the budget ended, everything it held given back.</summary>
    internal void Leave()
    {
        Interlocked.Decrement(ref _active);
        _process?.Leave();
    }

    /// <summary>
    /// Reserves <paramref name="bytes"/> under this budget and the process's; false, and nothing reserved,
    /// when either would pass its ceiling. Short of room, the memory kept lets go of what it holds above its
    /// floors, in the registry's order (<see cref="HeldMemory.GiveBack"/>), for the collection that follows to
    /// take it; and the reservations nearing the ceiling have it let go of a share before they get there.
    /// </summary>
    /// <remarks>
    /// With <paramref name="kept"/>, the bytes are those of a block the pool kept, parked or free in a slab alive
    /// (<see cref="BlockPool.Rent"/>): each budget counts them under its own ceiling as any others, and the process's
    /// counts them without weighing them against the rest of its memory (<see cref="TakeKept"/>).
    /// </remarks>
    internal bool TryReserve(long bytes, bool kept = false)
    {
        if (_process is null)
        {
            if (kept)
            {
                TakeKept(bytes);
                return true;
            }

            Observe();
            long available = Available();
            if (TryAdd(ref _reserved, bytes, available))
            {
                GiveBackNear(available);
                return true;
            }

            LetGo(HeldMemory.GiveBack(Math.Max(bytes, Ceiling / KeptMarginShare)));
            if (TryAdd(ref _reserved, bytes, Available()))
            {
                return true;
            }

            if (Collect(bytes) is long after)
            {
                return TryAdd(ref _reserved, bytes, after);
            }

            // No collection would help, by the state read now: one another thread observed since may have left room the
            // reads above did not see.
            Observe();
            return TryAdd(ref _reserved, bytes, Available());
        }

        // The peak raised once the parent granted the bytes too: raised before, a block the parent refused counted as held,
        // and a job under a budget of 16 MiB was said to have held 20 MB (2026-11-16).
        if (!TryAdd(ref _reserved, bytes, OwnCeiling, raise: false))
        {
            Refuse();
            return false;
        }

        // The jobs' share asks the process too, which weighs it against the rest of its memory as it weighs the questions':
        // carved out of the limit and never refused there, the jobs given the whole limit filled it with the blocks they
        // rented and those the pool parked for them, 1 222 MiB, and the writer's own arrays, which no account counts,
        // found no room in a heap held to 1.5 GiB, at a new site each run (2026-11-16). A budget a grant funds takes the
        // grant's bytes first, which the parent counts already, and asks it for the rest alone.
        long funded = _granted > 0 ? TakeGrant(bytes) : 0;
        if (bytes > funded && !_process.TryReserve(bytes - funded, kept))
        {
            if (funded > 0)
            {
                FillGrant(funded);
            }

            Interlocked.Add(ref _reserved, -bytes);
            Refuse();
            return false;
        }

        Raise(Volatile.Read(ref _reserved));
        return true;
    }

    /// <summary>
    /// What the pool may keep past the room a hard budget's first soft parent leaves before a rent under it has the pool
    /// let go of the rest: its floors, some 3.5 MiB, and room for the blocks a job gives back between two rents. None,
    /// 16 MiB and 64 MiB compacted the backlog bench in 52, 56 and 53 s under 2 GiB, at a peak resident of 1 251, 1 191 and
    /// 1 216 MiB, and in 48, 49 and 52 s under 4 GiB (2026-11-16).
    /// </summary>
    private const long PoolSlackBytes = 16L << 20;

    /// <summary>
    /// Has the pool let go of the blocks it keeps past what the budget the jobs reserve in leaves, the first that is not
    /// hard: the blocks a job rents and those the pool keeps for it then hold within that budget's ceiling. The pool keeps
    /// every block given back, of every size it was asked for, and a job that rents other sizes than the one before keeps
    /// the old ones parked: 762 MiB parked beside 1 347 MiB rented, under a limit of 4 GiB.
    /// </summary>
    internal void KeepPoolWithin()
    {
        QueryMemoryBudget soft = this;
        while (soft.Hard && soft._process is { } parent)
        {
            soft = parent;
        }

        // The pool is the process's: only a share carved out of the process's limit speaks for what it keeps. A host's budget
        // of a few mebibytes would empty it for every other work of the process at each rent, and the process's governor
        // already has it let go as the reservations under that budget near the limit.
        if (soft._share == 0)
        {
            return;
        }

        BlockPool pool = MemoryAccount.Pool;
        long over = soft.ReservedBytes + pool.ParkedBytes - soft.CeilingBytes - PoolSlackBytes;
        if (over > 0)
        {
            pool.GiveBack(KeptKind.Blocks, over);
        }
    }

    /// <summary>Notes a reservation refused, once: a job reads it to tell its own failure from another's.</summary>
    private void Refuse()
    {
        if (Volatile.Read(ref _refused) == 0)
        {
            Volatile.Write(ref _refused, 1);
        }
    }

    /// <summary>
    /// Whether a query holding <paramref name="held"/> may take <paramref name="bytes"/> more of a
    /// ceiling of <paramref name="ceiling"/> without being governed first, its fair share. Past the
    /// threshold, seven eighths of the ceiling, a query that would hold more than
    /// its share, the ceiling over the queries active under the budget, is told no: its lanes turn to
    /// the core first, and the last eighth stays for the queries within their share. A reservation is
    /// never refused for it: without a way to give memory back, a refusal fails a query that would have
    /// fit. Alone, a query's share is the whole ceiling.
    /// </summary>
    private bool Fair(long bytes, long held, long ceiling)
    {
        int active = Volatile.Read(ref _active);
        return active <= 1
            || ReservedBytes + bytes <= ceiling - (ceiling / 8)
            || held + bytes <= ceiling / active;
    }

    /// <summary>
    /// Reserves <paramref name="bytes"/> past this budget's ceiling: what a lane takes in the middle of a
    /// batch when the budget refuses it, or emptying its table into the core, counted all the same,
    /// before it gives back its table. The process's ceiling it
    /// passes by a sixteenth of the limit at most, half the margin the collector keeps: past that, the
    /// heap itself would run out, and nothing is reserved.
    /// </summary>
    /// <returns>Whether it was reserved.</returns>
    /// <remarks>
    /// With <paramref name="kept"/>, the bytes of a block the pool kept: the process's budget counts them whatever it
    /// holds (<see cref="TakeKept"/>), and only a hard budget that is never passed refuses them.
    /// </remarks>
    internal bool Force(long bytes, bool kept = false)
    {
        if (_process is null)
        {
            if (kept)
            {
                TakeKept(bytes);
                return true;
            }

            Observe();
            if (!TryAdd(ref _reserved, bytes, Available() + (Ceiling / 16)))
            {
                return false;
            }

            return true;
        }

        if (Hard && !CountsPast)
        {
            // A hard budget is never passed: the work under it gives way.
            Refuse();
            return false;
        }

        // The jobs' share passed as any budget is, the process bounding it: carved out of the process with no refusal, an append
        // of 324 000 rows under a limit of 1 GiB held 524 MiB of the share's 336, and a block of 64 MiB more failed the heap
        // (2026-11-16).
        if (!_process.Force(bytes, kept))
        {
            return false;
        }

        Raise(Interlocked.Add(ref _reserved, bytes));
        return true;
    }

    /// <summary>
    /// The process's own: counts <paramref name="bytes"/> of a block the pool kept, parked or free in a slab alive, without
    /// weighing them against the rest of the process's memory. The rest holds them already: the block's account takes them
    /// out of it once the block is its work's (<see cref="Reuse"/>), and the heap holds what it held. Refused there, a
    /// block of a few kilobytes failed a compaction's writer in the middle of a chunk under a pool that kept 505 MiB,
    /// 464 of them free in its slabs (2026-11-21).
    /// </summary>
    /// <remarks>
    /// Counted when the limit left the work no room for them, and when it left none within the sixteenth a block may count
    /// past (<see cref="Force"/>): the blocks that, asked as new memory, had the memory kept let go and the collector run,
    /// and those refused (<see cref="KeptPastRoom"/>, <see cref="KeptPastMargin"/>).
    /// </remarks>
    private void TakeKept(long bytes)
    {
        Observe();
        long held = Interlocked.Add(ref _reserved, bytes);
        Raise(held);
        long available = Available();
        if (held > available)
        {
            Interlocked.Increment(ref _keptPastRoom);
            if (held > available + (Ceiling / 16))
            {
                Interlocked.Increment(ref _keptPastMargin);
            }
        }
    }

    private long _keptPastRoom;
    private long _keptPastMargin;

    /// <summary>The process's own: the blocks the pool kept that work took while the limit left it no room for them (<see cref="TakeKept"/>).</summary>
    internal long KeptPastRoom => Interlocked.Read(ref _keptPastRoom);

    /// <summary>The process's own: those of <see cref="KeptPastRoom"/> taken while the limit left none within the sixteenth a block may count past: refused, when asked as new memory.</summary>
    internal long KeptPastMargin => Interlocked.Read(ref _keptPastMargin);

    /// <summary>Whether this budget and the process's would grant <paramref name="bytes"/> more now to a query that holds <paramref name="held"/>, without reserving them.</summary>
    internal bool CanReserve(long bytes, long held = 0)
    {
        if (_process is null)
        {
            Observe();
            long available = Available();
            return ReservedBytes + bytes <= available && Fair(bytes, held, available);
        }

        long ceiling = OwnCeiling;
        return ReservedBytes + bytes <= ceiling && Fair(bytes, held, ceiling) && (_share > 0 || _process.CanReserve(bytes, held));
    }

    /// <summary>
    /// Gives back <paramref name="bytes"/> reserved, to this budget and the process's, the reservations that wait for room
    /// taking their turn then. A budget a grant funds gives them to its parent too: the grant funds its first reservations
    /// alone, and its parent counts what is left of it and what the budget holds, exactly. Filled again by the releases,
    /// the grant would keep the parent counting the most the budget ever held.
    /// </summary>
    internal void Release(long bytes)
    {
        Interlocked.Add(ref _reserved, -bytes);
        _process?.Release(bytes);
        Serve();
    }

    /// <summary>Takes up to <paramref name="bytes"/> of what is left of the grant that funds the budget, and returns what it took.</summary>
    private long TakeGrant(long bytes)
    {
        long left = Volatile.Read(ref _grantLeft);
        while (true)
        {
            long take = Math.Min(bytes, left);
            if (take <= 0)
            {
                return 0;
            }

            long seen = Interlocked.CompareExchange(ref _grantLeft, left - take, left);
            if (seen == left)
            {
                return take;
            }

            left = seen;
        }
    }

    /// <summary>Gives back to the grant that funds the budget up to <paramref name="bytes"/> a reservation refused took of it, never past its bytes, and returns what it took back.</summary>
    private long FillGrant(long bytes)
    {
        long left = Volatile.Read(ref _grantLeft);
        while (true)
        {
            // A grant closed (CloseGrant) takes nothing back: its holder gave what was left of it to the parent.
            long put = left < 0 ? 0 : Math.Min(bytes, _granted - left);
            if (put <= 0)
            {
                return 0;
            }

            long seen = Interlocked.CompareExchange(ref _grantLeft, left + put, left);
            if (seen == left)
            {
                return put;
            }

            left = seen;
        }
    }

    /// <summary>
    /// The queries' tables hold <paramref name="delta"/> bytes more, or less: what they let go stays in
    /// the heap, the rest of the process's memory, until a collection takes it.
    /// </summary>
    internal void Measure(long delta)
    {
        QueryMemoryBudget process = Root;
        long measured = Interlocked.Add(ref process._measured, delta);
        if (delta < 0)
        {
            Interlocked.Add(ref process._pending, -delta);
            process.Lower(measured);
        }
    }

    /// <summary>The bytes measured fell to <paramref name="measured"/>: the fewest since the rest was last read, when they are.</summary>
    private void Lower(long measured)
    {
        long low = Volatile.Read(ref _measuredLow);
        while (measured < low)
        {
            long seen = Interlocked.CompareExchange(ref _measuredLow, measured, low);
            if (seen == low)
            {
                return;
            }

            low = seen;
        }
    }

    /// <summary>Arrays of <paramref name="bytes"/> the queries' tables replaced with larger ones: in the heap until a collection takes them.</summary>
    internal void Discard(long bytes) => Interlocked.Add(ref Root._pending, bytes);

    /// <summary>
    /// Blocks of <paramref name="bytes"/> the work under the budget gave back: no longer measured, and not let go either. The
    /// pool keeps them for the next rent, and what the pool keeps is part of the rest of the process, which the next
    /// observation reads in the heap; a block the pool lets go reaches the collector apart (<see cref="BlockFreed"/>).
    /// Counted as let go, each block a job rents and gives back again counted once more: 3.8 GB of rest were reckoned for a
    /// process of 1.4 GB, after a compaction under a limit of 4 GiB, and the next job was refused a block.
    /// </summary>
    internal void Returned(long bytes)
    {
        QueryMemoryBudget process = Root;
        process.Lower(Interlocked.Add(ref process._measured, -bytes));
    }

    /// <summary>The array of a block of <paramref name="bytes"/> let go to the collector: in the heap until a collection takes it.</summary>
    internal static void BlockFreed(long bytes) => Interlocked.Add(ref Process._pending, bytes);

    /// <summary>
    /// A slab made for a rent, <paramref name="free"/> of its bytes past the rented piece: in the heap from now on and measured
    /// by no work, the rest's as the blocks the pool keeps are, which the pieces cut from it later take back. Unknown until the
    /// next full collection, the room of the slabs made since was taken back from a rest that never held it: a writer under a
    /// limit of 1 GiB was granted a block of 64 MiB, the rest read at 52 MiB in a heap holding 161 past the work (2026-11-16).
    /// </summary>
    internal static void SlabMade(long free)
    {
        QueryMemoryBudget process = Process;
        process.Observe();
        process.Reuse(-free);
    }

    /// <summary>
    /// A slab of <paramref name="bytes"/> let go with its last piece, a rented one given back: its free bytes,
    /// <paramref name="free"/>, the rest of the process's until now, and all of it in the heap until a collection takes it.
    /// </summary>
    internal static void SlabFreed(long free, long bytes)
    {
        QueryMemoryBudget process = Process;
        process.LetGo(free);
        Interlocked.Add(ref process._pending, bytes - free);
    }

    /// <summary>
    /// Once the reservations come within <see cref="KeptMarginShare"/> of what the limit leaves, has the memory kept let go
    /// of that share above its floors, before a reservation finds no room: once a collection at most, which the rest read
    /// after the next sees.
    /// </summary>
    private void GiveBackNear(long available)
    {
        long margin = Ceiling / KeptMarginShare;
        if (ReservedBytes <= available - margin)
        {
            return;
        }

        int collections = GC.CollectionCount(0);
        if (Volatile.Read(ref _keptGivenAt) == collections || Interlocked.Exchange(ref _keptGivenAt, collections) == collections)
        {
            return;
        }

        LetGo(HeldMemory.GiveBack(margin));
    }

    /// <summary>
    /// The memory kept let go of <paramref name="bytes"/>: no longer live, which the last full collection read it as, and
    /// in the heap until a collection takes it, as what the queries let go is.
    /// </summary>
    private void LetGo(long bytes)
    {
        if (bytes <= 0)
        {
            return;
        }

        // Out of what the rest read in the heap, as it lived at the last full collection and as it grew since: taken from the
        // first alone, the bytes the uncertain part held stayed counted there as well as let go, and the budget read 1.2 GB
        // let go beside 726 MB uncertain in a heap of 1.5 GiB, and refused a job everything (2026-11-16).
        lock (_restGate)
        {
            Rest rest = Volatile.Read(ref _rest);
            long fromLive = Math.Min(bytes, rest.Live);
            long fromUncertain = Math.Min(bytes - fromLive, rest.Uncertain);
            Volatile.Write(ref _rest, rest with { Live = rest.Live - fromLive, Uncertain = rest.Uncertain - fromUncertain });
            Interlocked.Add(ref _pending, bytes);
        }
    }

    /// <summary>
    /// The process's own: work that measures its blocks took <paramref name="bytes"/> back from what the pool kept, or gave
    /// them back to it when negative. Until the next full collection reads them where they are, the rest no longer counts
    /// them: they are the work's now.
    /// </summary>
    internal void Reuse(long bytes) => Interlocked.Add(ref _reused, bytes);

    /// <summary>
    /// What the heap held past the measured work at the last full collection, less what the work took back from the pool
    /// since, and more what it gave back for the pool to keep.
    /// </summary>
    private long LiveOf(Rest rest) => Math.Max(0, rest.Live - Volatile.Read(ref _reused));

    /// <summary>
    /// What the process's limit leaves the queries past the rest of its memory: the library's blocks are
    /// managed, so the collector counts them with the rest of its heap.
    /// </summary>
    private long Available() => Math.Max(0, Ceiling - RestBytes);

    /// <summary>
    /// The rest of the process, read again after each full collection, in two parts. What its heap
    /// held past the queries' tables: the host's memory, the runtime's, the scans' buffers, what a
    /// caller still holds of a result. What the collector kept committed past its heap: the regions
    /// it freed and keeps for what comes next, which a collection that gives them back returns
    /// (300 MiB of them after eight queries at once under 512 MiB, 85 MiB live). What the queries let go before the collection, it took; what they let go since counts
    /// with the rest until the next. What the slabs hold free between their pieces counts with it too: it is room for the
    /// pool's next blocks only, never for the arrays a query's tables grow, and counted as room, eight queries under a heap
    /// held to 512 MiB ran it out of memory (2026-11-16).
    /// </summary>
    /// <remarks>
    /// Between two collections, a counter read; once a collection, the collector's state, read from
    /// the full collection itself: one of the youngest generations after it would count the large
    /// arrays let go since as live. What the heap holds at a collection of the youngest generations,
    /// past the queries' tables and what they let go, is the uncertain part: the host's growth since
    /// the last full collection, and the garbage the oldest generation keeps, which only a full one
    /// tells apart. Before the first full collection, it is all the process knows. It counts with the
    /// rest, and as what a collection may give back (<see cref="Collect"/>).
    /// </remarks>
    private void Observe()
    {
        Rest seen = Volatile.Read(ref _rest);
        if (GC.CollectionCount(2) == seen.Collections && GC.CollectionCount(0) == seen.Ephemeral)
        {
            return;
        }

        lock (_restGate)
        {
            Rest rest = Volatile.Read(ref _rest);
            int full = GC.CollectionCount(2);
            int any = GC.CollectionCount(0);
            if (full == rest.Collections && any == rest.Ephemeral)
            {
                return;
            }

            // What was measured when the collector ran, at least: the fewest bytes measured since the rest was last read,
            // before which no collection it reads now ran. The window starts again from what is measured now.
            long measured = Volatile.Read(ref _measured);
            long low = Math.Min(Interlocked.Exchange(ref _measuredLow, measured), measured);
            Lower(Volatile.Read(ref _measured));

            // A full collection is read once it is done. A background one is counted from its start, and the collector tells
            // the one before's memory until it ends: read as the new one's, the let go and the reuse since were dropped, the
            // room of four slabs made since among them, and the collection, marked read, was never read once done. A writer
            // under a limit of 1 GiB was granted a block of 64 MiB in a heap that held 108 MiB more than the rest said
            // (2026-11-16).
            GCMemoryInfo blocking = GC.GetGCMemoryInfo(GCKind.FullBlocking);
            GCMemoryInfo background = GC.GetGCMemoryInfo(GCKind.Background);
            GCMemoryInfo info = background.Index > blocking.Index ? background : blocking;
            if (info.Index > rest.FullIndex)
            {
                Interlocked.Exchange(ref _pending, 0);
                Interlocked.Exchange(ref _reused, 0);
                long inUse = info.HeapSizeBytes - info.FragmentedBytes;
                rest = new Rest(full, any, info.Index, info.Index, Math.Max(0, inUse - low), 0, Math.Max(0, info.TotalCommittedBytes - inUse));
            }

            // What the queries let go is in the heap or already taken: never more than the heap holds past what lived at the
            // last full collection and what the tables hold. Bytes given back that the heap never held, a lane's working
            // memory admitted and its reads ahead, counted all the same, and 15.7 GB of them piled up between two full
            // collections at 16 clients under a gen0 budget of 128 MiB, for a heap of 1.3 GB: the queries were left nothing,
            // and a query was refused the 64 KiB of a lane (2026-11-08). A collection read before is not read again.
            GCMemoryInfo ephemeral = GC.GetGCMemoryInfo(GCKind.Ephemeral);
            if (ephemeral.Index > rest.Read)
            {
                long inHeap = ephemeral.HeapSizeBytes - ephemeral.FragmentedBytes - low;
                long live = LiveOf(rest);
                long most = Math.Max(0, inHeap - live);
                long pending = Volatile.Read(ref _pending);
                if (pending > most)
                {
                    Interlocked.Add(ref _pending, most - pending);
                    pending = most;
                }

                rest = rest with { Read = ephemeral.Index, Uncertain = Math.Max(0, inHeap - pending - live) };
            }

            Volatile.Write(ref _rest, rest with { Collections = full, Ephemeral = any });
        }
    }

    /// <summary>
    /// The collection that gives back what the collector keeps committed past its heap and takes what
    /// the queries let go, when that is what stands between the reservations and <paramref name="bytes"/>
    /// more, and is worth a sixteenth of the ceiling at least: the ceiling to reserve against after it,
    /// or null when no collection would help. Each such collection gives back that much, which the
    /// queries must let go again before the next: a refusal fails a whole query, a collection does not.
    /// One whose collector kept what it held is not asked for again before the collector runs a full
    /// collection of its own. A thread that finds one under way waits for it, which every thread that
    /// allocates does anyway, and reserves against what it left.
    /// </summary>
    private long? Collect(long bytes)
    {
        // A collection whose collector kept what it held is not asked for again, unless a sixteenth of the ceiling was let go
        // since, which it takes whatever it keeps: told futile on what the collector kept committed alone, 100 MB, the next
        // never came, while 1.1 GB of slabs let go waited for it, and a job was refused everything (2026-11-16).
        Rest rest = Volatile.Read(ref _rest);
        long pending = Volatile.Read(ref _pending);
        long reclaimable = rest.Reclaimable + rest.Uncertain + pending;
        long ceiling = Ceiling;
        long live = LiveOf(rest);
        bool futile = GC.CollectionCount(2) == Volatile.Read(ref _futile) && pending < ceiling / 16;
        if (reclaimable < Math.Max(bytes, ceiling / 16) || ReservedBytes + bytes > ceiling - live || futile)
        {
            return null;
        }

        if (Interlocked.Exchange(ref _collecting, 1) != 0)
        {
            // Reserved against the ceiling past what lived at the last full collection, as if the collection would take all
            // the heap grew by since, eight queries under a heap held to 512 MiB reserved 276 MiB beside 235 MiB the
            // collection found live, and the heap ran out (2026-11-16).
            SpinWait waiting = default;
            while (Volatile.Read(ref _collecting) != 0)
            {
                waiting.SpinOnce();
            }

            Observe();
            return Available();
        }

        try
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            Observe();
            if (Volatile.Read(ref _rest).Reclaimable >= ceiling / 16)
            {
                Volatile.Write(ref _futile, GC.CollectionCount(2));
            }

            // What the collection gave back, as the container counts it: read now rather than at the timer's next turn.
            if (Volatile.Read(ref _containerTimer) is not null)
            {
                ReadContainer();
            }
        }
        finally
        {
            Volatile.Write(ref _collecting, 0);
        }

        return Available();
    }

    private bool TryAdd(ref long reserved, long bytes, long ceiling, bool raise = true)
    {
        long now = Volatile.Read(ref reserved);
        while (true)
        {
            if (now + bytes > ceiling)
            {
                return false;
            }

            long seen = Interlocked.CompareExchange(ref reserved, now + bytes, now);
            if (seen == now)
            {
                if (raise)
                {
                    Raise(now + bytes);
                }

                return true;
            }

            now = seen;
        }
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
    /// The process's limit, less a margin for what the collector needs to work: an eighth of the
    /// collector's limit, and in a container, read again as it runs, an eighth of the container's when
    /// that is less, the container's use being measured rather than modelled. A limit the host set is
    /// taken as a container's is (<see cref="VortexMemory"/>).
    /// </summary>
    internal static long ProcessLimit()
    {
        long collector = VortexMemory.CollectorBytes;
        long limit = collector - (collector / 8);
        if (Volatile.Read(ref s_reading) is { } container)
        {
            limit = Math.Min(limit, container.Limit - (container.Limit / ContainerMarginShare));
        }

        return Math.Max(limit, 64L << 20);
    }

    /// <summary>
    /// The rest of the process as read after a full collection: the counts of full collections and of collections of any
    /// generation when it was last read; the index of the full collection it was read from, and of the last collection
    /// of any kind; what its heap held past the queries' tables, and what a collection of the youngest generations since
    /// saw held past that; what the collector kept committed past its heap.
    /// </summary>
    private sealed record Rest(int Collections, int Ephemeral, long FullIndex, long Read, long Live, long Uncertain, long Reclaimable);
}
