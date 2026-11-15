using System;
using System.Globalization;
using System.IO;

namespace Vorticity;

/// <summary>
/// What the container the process runs in says of its memory, read from its cgroup, v2 then v1: its limit, and its working
/// set, what it uses less the file pages the kernel reclaims first, as the kubelet reckons it. Null outside Linux, outside a
/// container, or when the limit is unbounded. .NET exposes no such reading: the collector's own share of the limit is all
/// <see cref="GC.GetGCMemoryInfo()"/> tells, and nothing of what the native allocator keeps.
/// </summary>
/// <remarks>
/// Read once when the process's budget is made, then again by its slow timer (<see cref="QueryMemoryBudget"/>), never on
/// a thread that reserves: a read of three small files costs microseconds, which a reservation for each block a scan
/// reads would pay.
/// </remarks>
/// <param name="Limit">The container's limit in bytes.</param>
/// <param name="WorkingSet">What the container uses, less its inactive file pages.</param>
internal sealed record ContainerMemory(long Limit, long WorkingSet)
{
    private static readonly string[] V2 = ["/sys/fs/cgroup/memory.max", "/sys/fs/cgroup/memory.current", "/sys/fs/cgroup/memory.stat"];
    private static readonly string[] V1 = ["/sys/fs/cgroup/memory/memory.limit_in_bytes", "/sys/fs/cgroup/memory/memory.usage_in_bytes", "/sys/fs/cgroup/memory/memory.stat"];

    /// <summary>The container's memory now; null outside Linux, outside a container, or under no limit.</summary>
    internal static ContainerMemory? Read()
    {
        if (!OperatingSystem.IsLinux())
        {
            return null;
        }

        return Read(V2, inactiveFile: "inactive_file") ?? Read(V1, inactiveFile: "total_inactive_file");
    }

    private static ContainerMemory? Read(string[] paths, string inactiveFile)
    {
        try
        {
            if (Bytes(System.IO.File.ReadAllText(paths[0])) is not long limit)
            {
                return null;
            }

            long usage = Bytes(System.IO.File.ReadAllText(paths[1])) ?? 0;
            return new ContainerMemory(limit, WorkingSetOf(usage, System.IO.File.ReadAllText(paths[2]), inactiveFile));
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>A count of bytes as a cgroup file gives it; null for <c>max</c>, an unbounded limit, or what does not parse.</summary>
    internal static long? Bytes(string text) =>
        long.TryParse(text.AsSpan().Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out long bytes) && bytes > 0 && bytes < long.MaxValue / 2
            ? bytes
            : null;

    /// <summary>
    /// <paramref name="usage"/> less the inactive file pages <paramref name="stat"/>, a cgroup's <c>memory.stat</c>, counts
    /// under <paramref name="inactiveFile"/>: what the kernel cannot take back without the process giving it.
    /// </summary>
    internal static long WorkingSetOf(long usage, string stat, string inactiveFile)
    {
        foreach (ReadOnlySpan<char> line in stat.AsSpan().EnumerateLines())
        {
            int space = line.IndexOf(' ');
            if (space > 0 && line[..space].SequenceEqual(inactiveFile)
                && long.TryParse(line[(space + 1)..].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out long inactive))
            {
                return Math.Max(0, usage - inactive);
            }
        }

        return usage;
    }
}
