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
    /// <summary>The files of a cgroup v2's memory: its limit, its usage, its statistics.</summary>
    internal static readonly CgroupFiles V2 = new CgroupFiles("/sys/fs/cgroup", "memory.max", "memory.current", "memory.stat", "inactive_file");

    /// <summary>The files of a cgroup v1's memory controller.</summary>
    internal static readonly CgroupFiles V1 = new CgroupFiles("/sys/fs/cgroup/memory", "memory.limit_in_bytes", "memory.usage_in_bytes", "memory.stat", "total_inactive_file");

    /// <summary>The container's memory now; null outside Linux, outside a container, or under no limit.</summary>
    internal static ContainerMemory? Read()
    {
        if (!OperatingSystem.IsLinux())
        {
            return null;
        }

        string? cgroups = ReadText("/proc/self/cgroup");
        return Read(V2, CgroupPath(cgroups, v2: true)) ?? Read(V1, CgroupPath(cgroups, v2: false));
    }

    /// <summary>
    /// The memory of the process's cgroup at <paramref name="path"/> under <paramref name="files"/>' mount: the least limit of
    /// its own and of the cgroups above it, which the kernel holds it to as well, and the usage of the cgroup whose limit
    /// that is. Read at the mount's root alone, a limit set on a parent, a pod's over a container of none, or the process's
    /// own outside a namespace of cgroups, whose root has no limit, went unseen.
    /// </summary>
    internal static ContainerMemory? Read(CgroupFiles files, string path)
    {
        try
        {
            long? limit = null;
            string? bound = null;
            string directory = Path.Join(files.Root, path.TrimStart('/'));
            while (true)
            {
                if (ReadText(Path.Join(directory, files.Limit)) is { } text && Bytes(text) is long found && (limit is null || found < limit))
                {
                    limit = found;
                    bound = directory;
                }

                if (directory.Length <= files.Root.Length || Path.GetDirectoryName(directory) is not { } parent || parent.Length < files.Root.Length)
                {
                    break;
                }

                directory = parent;
            }

            if (limit is not long held || bound is null)
            {
                return null;
            }

            long usage = (ReadText(Path.Join(bound, files.Usage)) is { } used ? Bytes(used) : null) ?? 0;
            return new ContainerMemory(held, WorkingSetOf(usage, ReadText(Path.Join(bound, files.Stat)) ?? string.Empty, files.InactiveFile));
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

    /// <summary>
    /// The process's cgroup in <paramref name="cgroups"/>, its <c>/proc/self/cgroup</c>: the unified hierarchy's
    /// (<c>0::/path</c>) for v2, the memory controller's for v1; the root when the file says none, as in a namespace of
    /// cgroups, whose root is the container's own.
    /// </summary>
    internal static string CgroupPath(string? cgroups, bool v2)
    {
        foreach (ReadOnlySpan<char> line in (cgroups ?? string.Empty).AsSpan().EnumerateLines())
        {
            int first = line.IndexOf(':');
            int second = first < 0 ? -1 : line[(first + 1)..].IndexOf(':');
            if (second < 0)
            {
                continue;
            }

            ReadOnlySpan<char> controllers = line.Slice(first + 1, second);
            ReadOnlySpan<char> path = line[(first + second + 2)..];
            bool memory = false;
            foreach (Range controller in controllers.Split(','))
            {
                memory |= controllers[controller].SequenceEqual("memory");
            }

            if (v2 ? line[..first].SequenceEqual("0") && controllers.IsEmpty : memory)
            {
                return path.ToString();
            }
        }

        return "/";
    }

    /// <summary>The text of a file of the kernel's; null when it is not there or not readable.</summary>
    private static string? ReadText(string path)
    {
        try
        {
            return System.IO.File.ReadAllText(path);
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

    /// <summary>Where a hierarchy's memory files lie, and their names: its mount, its limit, its usage, its statistics and their inactive file pages.</summary>
    internal sealed record CgroupFiles(string Root, string Limit, string Usage, string Stat, string InactiveFile);

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
