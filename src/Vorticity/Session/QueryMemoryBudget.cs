using System;
using System.Globalization;
using System.IO;
using System.Threading;

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
    /// <summary>The least time between two collections the process's budget asks for, in milliseconds.</summary>
    private const long CollectEveryMs = 20;

    // The process's budget, which this one reserves in too; null for the process's own.
    private readonly QueryMemoryBudget? _process;

    // The host's ceiling; the process's limit, less a margin, for the process's own.
    private readonly long _ceiling;
    private long _reserved;

    // The process's own: the bytes the queries' tables hold, as last measured; the rest of the
    // process, read after the last full collection; what the queries let go since, still in the heap;
    // when it last asked for a collection, and whether one is being asked for.
    private long _measured;
    private Rest _rest = new Rest(-1, 0, 0);
    private long _pending;
    private long _collected = -CollectEveryMs;
    private int _collecting;

    /// <summary>A budget under which the queries of its sessions hold <paramref name="ceilingBytes"/> at most, together.</summary>
    /// <param name="ceilingBytes">The bytes they may hold at once.</param>
    /// <exception cref="ArgumentOutOfRangeException">The ceiling is not positive.</exception>
    public QueryMemoryBudget(long ceilingBytes)
        : this(ceilingBytes, Process)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ceilingBytes);
    }

    private QueryMemoryBudget(long ceilingBytes, QueryMemoryBudget? process)
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
            return rest.Live + rest.Reclaimable + Volatile.Read(ref _pending);
        }
    }

    /// <summary>Reserves <paramref name="bytes"/> under this budget and the process's; false, and nothing reserved, when either would pass its ceiling.</summary>
    internal bool TryReserve(long bytes)
    {
        if (_process is null)
        {
            Observe();
            return TryAdd(ref _reserved, bytes, Available()) || (Collect(bytes) && TryAdd(ref _reserved, bytes, Available()));
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

    /// <summary>What the process's limit leaves the queries past the rest of its memory.</summary>
    private long Available() => Math.Max(0, _ceiling - RestBytes);

    /// <summary>
    /// The rest of the process, read again after each full collection, in two parts. What its heap
    /// held past the queries' tables: the host's memory, the runtime's, the scans' buffers, what a
    /// caller still holds of a result. What the collector kept committed past its heap: the regions
    /// it freed and keeps for what comes next, which a collection that gives them back returns
    /// (PLAN-HIGH-CARDINALITY, H2: 300 MiB of them after eight queries at once under 512 MiB, 85 MiB
    /// live). What the queries let go before the collection, it took; what they let go since counts
    /// with the rest until the next.
    /// </summary>
    /// <remarks>
    /// Between two full collections, a counter read; once a collection, the collector's state, read
    /// from the full collection itself: one of the youngest generations after it would count the large
    /// arrays let go since as live.
    /// </remarks>
    private void Observe()
    {
        Rest rest = Volatile.Read(ref _rest);
        int full = GC.CollectionCount(2);
        if (full == rest.Collections)
        {
            return;
        }

        Interlocked.Exchange(ref _pending, 0);
        GCMemoryInfo blocking = GC.GetGCMemoryInfo(GCKind.FullBlocking);
        GCMemoryInfo background = GC.GetGCMemoryInfo(GCKind.Background);
        GCMemoryInfo info = background.Index > blocking.Index ? background : blocking;
        long inUse = info.HeapSizeBytes - info.FragmentedBytes;
        Volatile.Write(ref _rest, new Rest(full, Math.Max(0, inUse - Volatile.Read(ref _measured)), Math.Max(0, info.TotalCommittedBytes - inUse)));
    }

    /// <summary>
    /// The collection that gives back what the collector keeps committed past its heap and takes what
    /// the queries let go, when that is what stands between the reservations and <paramref name="bytes"/>
    /// more; then the rest read again. Once in <see cref="CollectEveryMs"/> at most, by one thread: a
    /// process whose memory stays full refuses the reservation rather than collect without end.
    /// </summary>
    private bool Collect(long bytes)
    {
        Rest rest = Volatile.Read(ref _rest);
        if (rest.Reclaimable + Volatile.Read(ref _pending) == 0 || ReservedBytes + bytes > _ceiling - rest.Live)
        {
            return false;
        }

        long now = Environment.TickCount64;
        if (now - Volatile.Read(ref _collected) < CollectEveryMs || Interlocked.Exchange(ref _collecting, 1) != 0)
        {
            return false;
        }

        try
        {
            Volatile.Write(ref _collected, now);
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        }
        finally
        {
            Volatile.Write(ref _collecting, 0);
        }

        Observe();
        return true;
    }

    private static bool TryAdd(ref long reserved, long bytes, long ceiling)
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
                return true;
            }

            now = seen;
        }
    }

    /// <summary>The process's limit, less a margin for what the collector needs to work: an eighth of the collector's limit, a quarter of a container's.</summary>
    private static long ProcessLimit()
    {
        long collector = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        long limit = collector - (collector / 8);
        if (ContainerLimit() is long container)
        {
            limit = Math.Min(limit, container / 4 * 3);
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
    /// The rest of the process as read after a full collection: that collection's number, what its
    /// heap held past the queries' tables, and what the collector kept committed past its heap.
    /// </summary>
    private sealed record Rest(int Collections, long Live, long Reclaimable);
}
