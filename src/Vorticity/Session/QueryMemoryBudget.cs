using System;
using System.Globalization;
using System.IO;
using System.Threading;
using Vorticity.Buffers;

namespace Vorticity;

/// <summary>
/// The memory the queries of the sessions that share it may hold at once: the tables of their groups,
/// lane by lane, and the parts their merges build. A host makes one and gives it to each session it
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
    // The container's limit less its margin, which the engine's native memory counts against beside the
    // heap; null outside a container.
    private static readonly long? s_container = ContainerLimit() is long container ? container / 4 * 3 : null;

    // The process's budget, which this one reserves in too; null for the process's own.
    private readonly QueryMemoryBudget? _process;

    // The host's ceiling; the process's limit, less a margin, for the process's own. What its queries
    // hold, the most they held at once, and the queries that hold memory under it now.
    private readonly long _ceiling;
    private long _reserved;
    private long _peak;
    private int _active;

    // The process's own: the bytes the queries' tables hold, as last measured; the rest of the
    // process, read after the last full collection; what the queries let go since, still in the heap;
    // whether a collection is being asked for, and the full collection after which the last one asked
    // for left the collector holding what it held.
    private long _measured;
    private Rest _rest = new Rest(-1, -1, 0, 0, 0);
    private long _pending;
    private int _collecting;
    private int _futile = -1;

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
    {
        _ceiling = ceilingBytes;
        _process = process;
    }

    /// <summary>
    /// The bytes the queries under the budget may hold at once: the host's ceiling; for the process's
    /// own, what its limit leaves past the rest of its memory, which moves as the process does.
    /// </summary>
    public long CeilingBytes => _process is null ? Available() : _ceiling;

    /// <summary>The bytes they hold now.</summary>
    public long ReservedBytes => Volatile.Read(ref _reserved);

    /// <summary>The most bytes they held at once, since the budget was made.</summary>
    internal long PeakBytes => Volatile.Read(ref _peak);

    /// <summary>
    /// The process's budget, which every session shares unless given another. Its limit is the memory
    /// the collector may commit — the heap's hard limit when one is set, its share of a container's
    /// limit in a container, the machine's memory otherwise — and a container's own limit, which
    /// counts the engine's native memory as well, each less a margin. Of it, the queries get what the
    /// rest of the process leaves: a limit of 4 GB is not 4 GB for the queries.
    /// </summary>
    internal static QueryMemoryBudget Process { get; } = new QueryMemoryBudget(ProcessLimit(), process: null);

    /// <summary>
    /// The rest of the process's memory: what its heap held past the queries' tables at the last full
    /// collection, what the collector kept committed past its heap, and what the queries let go since.
    /// </summary>
    internal long RestBytes
    {
        get
        {
            Rest rest = Volatile.Read(ref _rest);
            return rest.Live + rest.Uncertain + rest.Reclaimable + Volatile.Read(ref _pending);
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
    /// when either would pass its ceiling. Short of room, the process's shelf lets its large arrays go
    /// first, for the collection that follows to take them.
    /// </summary>
    internal bool TryReserve(long bytes)
    {
        if (_process is null)
        {
            Observe();
            if (TryAdd(ref _reserved, bytes, Available()))
            {
                return true;
            }

            Aggregating.ArrayShelf.Retained.Relieve();
            return Collect(bytes) is long after && TryAdd(ref _reserved, bytes, after);
        }

        if (!TryAdd(ref _reserved, bytes, _ceiling))
        {
            return false;
        }

        if (!_process.TryReserve(bytes))
        {
            Interlocked.Add(ref _reserved, -bytes);
            return false;
        }

        return true;
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
            if (!TryAdd(ref _reserved, bytes, Available() + (_ceiling / 16)))
            {
                return false;
            }

            return true;
        }

        if (!_process.Force(bytes))
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

        return ReservedBytes + bytes <= _ceiling && Fair(bytes, held, _ceiling) && _process.CanReserve(bytes, held);
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
        QueryMemoryBudget process = _process ?? this;
        Interlocked.Add(ref process._measured, delta);
        if (delta < 0)
        {
            Interlocked.Add(ref process._pending, -delta);
        }
    }

    /// <summary>Arrays of <paramref name="bytes"/> the queries' tables replaced with larger ones: in the heap until a collection takes them.</summary>
    internal void Discard(long bytes) => Interlocked.Add(ref (_process ?? this)._pending, bytes);

    /// <summary>
    /// The process's shelf keeps an array of <paramref name="bytes"/> for the next query: reserved and
    /// measured as the queries' tables are, within the ceiling and with no collection asked for; false,
    /// and nothing counted, past it. <paramref name="fromQuery"/>: whether its query had counted it and
    /// given it back as let go, which it no longer is. The process's own budget.
    /// </summary>
    internal bool TryKeep(long bytes, bool fromQuery)
    {
        Observe();
        if (!TryAdd(ref _reserved, bytes, Available()))
        {
            return false;
        }

        Interlocked.Add(ref _measured, bytes);
        if (fromQuery)
        {
            Interlocked.Add(ref _pending, -bytes);
        }

        return true;
    }

    /// <summary>
    /// The process's shelf lets an array of <paramref name="bytes"/> go: to a query that counts it, its
    /// count handed over (<paramref name="counted"/>); else to the collector or to code that counts
    /// nothing, the rest of the process until the next full collection reads it again.
    /// </summary>
    internal void Unkeep(long bytes, bool counted)
    {
        Interlocked.Add(ref _reserved, -bytes);
        Interlocked.Add(ref _measured, -bytes);
        if (!counted)
        {
            Interlocked.Add(ref _pending, bytes);
        }
    }

    /// <summary>
    /// What the process's limits leave the queries past the rest of its memory: the collector's, past
    /// the rest of its heap; in a container, the container's too, past the heap's rest and the engine's
    /// native blocks, which it counts and the collector does not.
    /// </summary>
    private long Available()
    {
        long rest = RestBytes;
        long available = _ceiling - rest;
        if (s_container is long container)
        {
            available = Math.Min(available, container - rest - NativeSegmentOwner.NativeBytes);
        }

        return Math.Max(0, available);
    }

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

        GCMemoryInfo ephemeral = GC.GetGCMemoryInfo(GCKind.Ephemeral);
        long held = ephemeral.HeapSizeBytes - ephemeral.FragmentedBytes - Volatile.Read(ref _measured) - Volatile.Read(ref _pending);
        Volatile.Write(ref _rest, rest with { Ephemeral = any, Uncertain = Math.Max(0, held - rest.Live) });
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
        if (reclaimable < Math.Max(bytes, _ceiling / 16) || ReservedBytes + bytes > _ceiling - rest.Live || GC.CollectionCount(2) == Volatile.Read(ref _futile))
        {
            return null;
        }

        if (Interlocked.Exchange(ref _collecting, 1) != 0)
        {
            return _ceiling - rest.Live;
        }

        try
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            Observe();
            if (Volatile.Read(ref _rest).Reclaimable >= _ceiling / 16)
            {
                Volatile.Write(ref _futile, GC.CollectionCount(2));
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
    /// collector's limit; a container's, a quarter of it, applies past the native memory too
    /// (<see cref="Available"/>).
    /// </summary>
    private static long ProcessLimit()
    {
        long collector = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        long limit = collector - (collector / 8);
        if (s_container is long container)
        {
            limit = Math.Min(limit, container);
        }

        return Math.Max(limit, 64L << 20);
    }

    /// <summary>
    /// The memory limit of the container the process runs in, read from its cgroup, v2 then v1; null
    /// outside Linux, outside a container, or when the limit is unbounded. .NET exposes no such limit:
    /// the collector's own share of it is all <see cref="GC.GetGCMemoryInfo()"/> tells.
    /// </summary>
    private static long? ContainerLimit()
    {
        if (!OperatingSystem.IsLinux())
        {
            return null;
        }

        foreach (string path in (string[])["/sys/fs/cgroup/memory.max", "/sys/fs/cgroup/memory/memory.limit_in_bytes"])
        {
            try
            {
                if (long.TryParse(System.IO.File.ReadAllText(path).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out long limit) && limit > 0 && limit < long.MaxValue / 2)
                {
                    return limit;
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return null;
    }

    /// <summary>
    /// The rest of the process as read after a full collection: that collection's number, and the
    /// count of collections of any generation since read; what its heap held past the queries' tables,
    /// and what a collection of the youngest generations since saw held past that; what the collector
    /// kept committed past its heap.
    /// </summary>
    private sealed record Rest(int Collections, int Ephemeral, long Live, long Uncertain, long Reclaimable);
}
