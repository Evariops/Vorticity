using System;
using System.Threading;
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
    /// 🧪 The share of the ceiling, one in this many, within which the reservations have the memory kept let go of that
    /// much above its floors before they need it, and the least a reservation short of room has it let go of:
    /// provisional, calibrated by measurement with the floors.
    /// </summary>
    private const int KeptMarginShare = 8;

    /// <summary>
    /// 🧪 The share of a container's limit, one in this many, the process keeps as a margin: what the container uses past
    /// its last reading, a timer's period at most. Provisional, calibrated by measurement.
    /// </summary>
    private const int ContainerMarginShare = 8;

    /// <summary>🧪 How often the process's budget reads its container's memory again, in milliseconds: provisional, to be measured.</summary>
    internal const int ContainerReadMilliseconds = 1_000;

    // What the collector may commit, read once: a hard limit, its share of a container's limit, or the machine's memory.
    private static readonly long s_collector = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;

    // What the container said of its memory at its last reading; null outside a container.
    private static ContainerMemory? s_reading = ContainerMemory.Read();

    // A limit a bench sets, as a container's would be, under the one read; 0 for none.
    private static long s_limitOverride;

    /// <summary>
    /// 🧪 The share of the process's limit, one in this many, the jobs' own budget holds by default (<see cref="Jobs"/>): what
    /// the work got under a limit of 4 GiB, 1 660 MB of 3 584 MiB, when the jobs shared the queries' budget. Provisional,
    /// calibrated by measurement.
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

    // A budget carved out of the process's limit, one in this many of it, read again as the limit moves; 0 for a ceiling of
    // its own. What it reserves the process counts, and never refuses: its share is the guard.
    private readonly int _share;

    // Whether a reservation was refused since the budget was made: what tells a job that ran out of its budget.
    private int _refused;

    // The process's own: the bytes the queries' tables hold, as last measured; the rest of the
    // process, read after the last full collection; what the queries let go since, still in the heap;
    // whether a collection is being asked for, and the full collection after which the last one asked
    // for left the collector holding what it held.
    private long _measured;
    private Rest _rest = new Rest(-1, -1, 0, 0, 0);
    private long _pending;
    private int _collecting;
    private int _futile = -1;

    // The count of collections at which the reservations nearing the ceiling last had the memory kept let go of a share.
    private int _keptGivenAt = -1;

    // The process's own, in a container: what the container used past what the registry knows, at its last reading; and the
    // timer that reads it again.
    private long _containerRest;
    private readonly Timer? _containerTimer;

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
    /// The budget of the jobs carved out of the process's limit: <paramref name="share"/>, one in this many of the limit, read
    /// again as the limit moves. The process counts what it reserves, and refuses none of it: what the rest of the process
    /// holds does not take a job's room, which its share keeps for it.
    /// </summary>
    private QueryMemoryBudget(int share)
        : this(0, Process, share)
    {
    }

    private QueryMemoryBudget(long ceilingBytes, QueryMemoryBudget? process, int share)
    {
        _ceiling = ceilingBytes;
        _process = process;
        _share = share;
        Account = new BudgetAccount(this, process is null ? "library's own work" : "work");
        if (process is null && Volatile.Read(ref s_reading) is not null)
        {
            // In a container, its memory read again off the threads that reserve. Made with no flow of the thread that makes
            // the budget: its locals are no business of the timer's.
            ReadContainer();
            using (ExecutionContext.SuppressFlow())
            {
                _containerTimer = new Timer(static budget => ((QueryMemoryBudget)budget!).ReadContainer(), this, ContainerReadMilliseconds, ContainerReadMilliseconds);
            }
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
    internal static QueryMemoryBudget Process { get; } = new QueryMemoryBudget(ProcessLimit(), process: null);

    /// <summary>
    /// The jobs' budget of every session given none of its own: <see cref="JobsShare"/>, one in this many, of the process's
    /// limit as it stands, carved out of it. The process counts what the jobs reserve, so that the queries see it, and
    /// refuses none of it: a job's room does not depend on what the rest of the process holds, which a job that just ended
    /// leaves counted until the next collection.
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
    internal QueryMemoryBudget HardChild(long ceilingBytes = long.MaxValue / 4) => new QueryMemoryBudget(ceilingBytes, this) { Hard = true };

    /// <summary>Holds the process to <paramref name="bytes"/> at most, as a container's limit would: a bench's, never the library's.</summary>
    internal static void OverrideLimit(long bytes) => Volatile.Write(ref s_limitOverride, Math.Max(0, bytes));

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
            long modelled = rest.Live + rest.Uncertain + rest.Reclaimable + Volatile.Read(ref _pending);
            return _containerTimer is null ? modelled : Math.Max(modelled, Volatile.Read(ref _containerRest));
        }
    }

    /// <summary>The bytes the work under the budget may hold: the host's ceiling; for the process's own, its limit as last read.</summary>
    private long Ceiling => _process is null ? ProcessLimit() : OwnCeiling;

    /// <summary>
    /// Reads the container's memory again, and what it uses past what the registry knows: the reservations, and the memory
    /// kept (<see cref="HeldMemory"/>). On the budget's timer, and after a collection the budget asked for.
    /// </summary>
    private void ReadContainer()
    {
        if (ContainerMemory.Read() is { } reading)
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
    internal bool TryReserve(long bytes)
    {
        if (_process is null)
        {
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

        if (!TryAdd(ref _reserved, bytes, OwnCeiling))
        {
            Refuse();
            return false;
        }

        if (_share > 0)
        {
            // Carved out of the process's limit: counted there, never refused there.
            _process.Carve(bytes);
            return true;
        }

        if (!_process.TryReserve(bytes))
        {
            Interlocked.Add(ref _reserved, -bytes);
            Refuse();
            return false;
        }

        return true;
    }

    /// <summary>
    /// 🧪 What the pool may keep past the room a hard budget's first soft parent leaves before a rent under it has the pool
    /// let go of the rest: its floors, some 3.5 MiB, and room for the blocks a job gives back between two rents.
    /// Provisional, calibrated by measurement.
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
    /// Counts <paramref name="bytes"/> a budget carved out of this one reserved, past its ceiling if need be: what the jobs'
    /// share holds, which the queries see and which the limit already set aside.
    /// </summary>
    private void Carve(long bytes)
    {
        Raise(Interlocked.Add(ref _reserved, bytes));
        _process?.Carve(bytes);
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
    internal bool Force(long bytes)
    {
        if (_process is null)
        {
            Observe();
            if (!TryAdd(ref _reserved, bytes, Available() + (Ceiling / 16)))
            {
                return false;
            }

            return true;
        }

        if (Hard)
        {
            // A hard budget is never passed: the work under it gives way.
            Refuse();
            return false;
        }

        if (_share > 0)
        {
            _process.Carve(bytes);
        }
        else if (!_process.Force(bytes))
        {
            return false;
        }

        Raise(Interlocked.Add(ref _reserved, bytes));
        return true;
    }

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

    /// <summary>Gives back <paramref name="bytes"/> reserved, to this budget and the process's.</summary>
    internal void Release(long bytes)
    {
        Interlocked.Add(ref _reserved, -bytes);
        _process?.Release(bytes);
    }

    /// <summary>
    /// The queries' tables hold <paramref name="delta"/> bytes more, or less: what they let go stays in
    /// the heap, the rest of the process's memory, until a collection takes it.
    /// </summary>
    internal void Measure(long delta)
    {
        QueryMemoryBudget process = Root;
        Interlocked.Add(ref process._measured, delta);
        if (delta < 0)
        {
            Interlocked.Add(ref process._pending, -delta);
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
    internal void Returned(long bytes) => Interlocked.Add(ref Root._measured, -bytes);

    /// <summary>The array of a block of <paramref name="bytes"/> let go to the collector: in the heap until a collection takes it.</summary>
    internal static void BlockFreed(long bytes) => Interlocked.Add(ref Process._pending, bytes);

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

        Rest rest = Volatile.Read(ref _rest);
        Volatile.Write(ref _rest, rest with { Live = Math.Max(0, rest.Live - bytes) });
        Interlocked.Add(ref _pending, bytes);
    }

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
    /// with the rest until the next.
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
        Rest rest = Volatile.Read(ref _rest);
        int full = GC.CollectionCount(2);
        int any = GC.CollectionCount(0);
        if (full == rest.Collections && any == rest.Ephemeral)
        {
            return;
        }

        if (full != rest.Collections)
        {
            Interlocked.Exchange(ref _pending, 0);
            GCMemoryInfo blocking = GC.GetGCMemoryInfo(GCKind.FullBlocking);
            GCMemoryInfo background = GC.GetGCMemoryInfo(GCKind.Background);
            GCMemoryInfo info = background.Index > blocking.Index ? background : blocking;
            long inUse = info.HeapSizeBytes - info.FragmentedBytes;
            Volatile.Write(ref _rest, new Rest(full, any, Math.Max(0, inUse - Volatile.Read(ref _measured)), 0, Math.Max(0, info.TotalCommittedBytes - inUse)));
            return;
        }

        // What the queries let go is in the heap or already taken: never more than the heap holds past what lived at the
        // last full collection and what the tables hold. Bytes given back that the heap never held, a lane's working memory
        // admitted and its reads ahead, counted all the same, and 15.7 GB of them piled up between two full collections at
        // 16 clients under a gen0 budget of 128 MiB, for a heap of 1.3 GB: the queries were left nothing, and a query was
        // refused the 64 KiB of a lane (2026-11-08).
        GCMemoryInfo ephemeral = GC.GetGCMemoryInfo(GCKind.Ephemeral);
        long inHeap = ephemeral.HeapSizeBytes - ephemeral.FragmentedBytes - Volatile.Read(ref _measured);
        long most = Math.Max(0, inHeap - rest.Live);
        long pending = Volatile.Read(ref _pending);
        if (pending > most)
        {
            Interlocked.Add(ref _pending, most - pending);
            pending = most;
        }

        Volatile.Write(ref _rest, rest with { Ephemeral = any, Uncertain = Math.Max(0, inHeap - pending - rest.Live) });
    }

    /// <summary>
    /// The collection that gives back what the collector keeps committed past its heap and takes what
    /// the queries let go, when that is what stands between the reservations and <paramref name="bytes"/>
    /// more, and is worth a sixteenth of the ceiling at least: the ceiling to reserve against after it,
    /// or null when no collection would help. Each such collection gives back that much, which the
    /// queries must let go again before the next: a refusal fails a whole query, a collection does not.
    /// One whose collector kept what it held is not asked for again before the collector runs a full
    /// collection of its own. A thread that finds one under way reserves against what it will leave,
    /// the ceiling past what the heap holds live: it allocates once the collection is done, which
    /// every thread waits for anyway.
    /// </summary>
    private long? Collect(long bytes)
    {
        Rest rest = Volatile.Read(ref _rest);
        long reclaimable = rest.Reclaimable + rest.Uncertain + Volatile.Read(ref _pending);
        long ceiling = Ceiling;
        if (reclaimable < Math.Max(bytes, ceiling / 16) || ReservedBytes + bytes > ceiling - rest.Live || GC.CollectionCount(2) == Volatile.Read(ref _futile))
        {
            return null;
        }

        if (Interlocked.Exchange(ref _collecting, 1) != 0)
        {
            return ceiling - rest.Live;
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
            if (_containerTimer is not null)
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

    private bool TryAdd(ref long reserved, long bytes, long ceiling)
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
                Raise(now + bytes);
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
    /// collector's limit, and in a container, read again as it runs, an eighth 🧪 of the container's when
    /// that is less, the container's use being measured rather than modelled.
    /// </summary>
    internal static long ProcessLimit()
    {
        long limit = s_collector - (s_collector / 8);
        if (Volatile.Read(ref s_reading) is { } container)
        {
            limit = Math.Min(limit, container.Limit - (container.Limit / ContainerMarginShare));
        }

        // A bench's limit, taken as a container's is, its margin less.
        if (Volatile.Read(ref s_limitOverride) is > 0 and long set)
        {
            limit = Math.Min(limit, set - (set / ContainerMarginShare));
        }

        return Math.Max(limit, 64L << 20);
    }

    /// <summary>
    /// The rest of the process as read after a full collection: that collection's number, and the
    /// count of collections of any generation since read; what its heap held past the queries' tables,
    /// and what a collection of the youngest generations since saw held past that; what the collector
    /// kept committed past its heap.
    /// </summary>
    private sealed record Rest(int Collections, int Ephemeral, long Live, long Uncertain, long Reclaimable);
}
