using System;
using System.Globalization;
using System.IO;
using System.Threading;

namespace Vorticity;

/// <summary>
/// The memory the queries of the sessions that share it may hold at once: the tables of their groups,
/// lane by lane, and the parts their merges build. A host makes one and gives it to each session it
/// shares through <see cref="VortexSessionOptions.MemoryBudget"/>; a session given none shares the
/// process's, whose ceiling is a margin under the memory the process may use.
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
    // The process's budget, which this one reserves in too; null for the process's own.
    private readonly QueryMemoryBudget? _process;
    private long _reserved;

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
        CeilingBytes = ceilingBytes;
        _process = process;
    }

    /// <summary>The bytes the queries under the budget may hold at once.</summary>
    public long CeilingBytes { get; }

    /// <summary>The bytes they hold now.</summary>
    public long ReservedBytes => Volatile.Read(ref _reserved);

    /// <summary>
    /// The process's budget, which every session shares unless given another: three quarters of the
    /// memory the collector may use — the heap's hard limit when one is set, its share of a container's
    /// limit in a container, the machine's memory otherwise — and three quarters of a container's own
    /// limit, which counts the engine's native memory as well.
    /// </summary>
    internal static QueryMemoryBudget Process { get; } = new QueryMemoryBudget(ProcessCeiling(), process: null);

    /// <summary>Reserves <paramref name="bytes"/> under this budget and the process's; false, and nothing reserved, when either would pass its ceiling.</summary>
    internal bool TryReserve(long bytes)
    {
        if (!TryAdd(ref _reserved, bytes, CeilingBytes))
        {
            return false;
        }

        if (_process is not null && !_process.TryReserve(bytes))
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

    private static long ProcessCeiling()
    {
        long ceiling = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 4 * 3;
        if (ContainerLimit() is long container)
        {
            ceiling = Math.Min(ceiling, container / 4 * 3);
        }

        return Math.Max(ceiling, 64L << 20);
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
}
