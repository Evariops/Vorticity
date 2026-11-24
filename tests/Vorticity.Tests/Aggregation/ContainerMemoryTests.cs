// What the process reads of its container's memory: a limit, or none when unbounded; and a working set, what the
// container uses less the inactive file pages the kernel reclaims first, as v2 and v1 name them.
using System;
using System.IO;
using Xunit;

namespace Vorticity.Tests.Aggregation;

public sealed class ContainerMemoryTests
{
    [Fact]
    public void ALimitIsItsBytesAndAnUnboundedOneIsNone()
    {
        Assert.Equal(1L << 30, ContainerMemory.Bytes("1073741824\n"));
        Assert.Null(ContainerMemory.Bytes("max\n"));
        Assert.Null(ContainerMemory.Bytes("9223372036854771712\n"));
        Assert.Null(ContainerMemory.Bytes("0\n"));
        Assert.Null(ContainerMemory.Bytes(string.Empty));
    }

    [Fact]
    public void TheWorkingSetIsTheUseLessTheInactiveFilePages()
    {
        const string V2 = "anon 400\nfile 600\nkernel 50\nactive_file 200\ninactive_file 300\ninactive_file_extra 7\n";
        const string V1 = "cache 600\nrss 400\ninactive_file 1\ntotal_active_file 200\ntotal_inactive_file 300\n";
        Assert.Equal(700, ContainerMemory.WorkingSetOf(1_000, V2, "inactive_file"));
        Assert.Equal(700, ContainerMemory.WorkingSetOf(1_000, V1, "total_inactive_file"));

        // A stat that names none counts the use whole; one past the use counts nothing.
        Assert.Equal(1_000, ContainerMemory.WorkingSetOf(1_000, "anon 400\n", "inactive_file"));
        Assert.Equal(0, ContainerMemory.WorkingSetOf(100, V2, "inactive_file"));
    }

    /// <summary>The process's cgroup is the unified hierarchy's for v2, the memory controller's for v1, and the root when none is named.</summary>
    [Fact]
    public void TheProcesssCgroupIsTheOneItsHierarchyNames()
    {
        Assert.Equal("/kubepods/pod1/c2", ContainerMemory.CgroupPath("0::/kubepods/pod1/c2\n", v2: true));
        Assert.Equal("/", ContainerMemory.CgroupPath("0::/\n", v2: true));
        const string V1 = "12:pids:/docker/a\n9:memory:/docker/a\n4:cpu,cpuacct:/docker/a\n";
        Assert.Equal("/docker/a", ContainerMemory.CgroupPath(V1, v2: false));
        Assert.Equal("/", ContainerMemory.CgroupPath(V1, v2: true));
        Assert.Equal("/", ContainerMemory.CgroupPath(null, v2: false));
    }

    /// <summary>
    /// A limit set on a cgroup above the process's holds it too: the least of the limits on the way up to the root is the
    /// process's, read with the usage of the cgroup it is set on. Read at the root alone, a pod's limit over a container of
    /// none, or the process's own cgroup outside a namespace, went unseen.
    /// </summary>
    [Fact]
    public void TheLeastLimitOnTheWayUpIsTheProcesss()
    {
        string root = Path.Combine(Path.GetTempPath(), "vorticity-cgroup-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "pod", "container"));
            System.IO.File.WriteAllText(Path.Combine(root, "pod", "memory.max"), "1073741824\n");
            System.IO.File.WriteAllText(Path.Combine(root, "pod", "memory.current"), "1000\n");
            System.IO.File.WriteAllText(Path.Combine(root, "pod", "memory.stat"), "inactive_file 300\n");
            System.IO.File.WriteAllText(Path.Combine(root, "pod", "container", "memory.max"), "max\n");
            System.IO.File.WriteAllText(Path.Combine(root, "pod", "container", "memory.current"), "900\n");
            ContainerMemory.CgroupFiles files = ContainerMemory.V2 with { Root = root };

            ContainerMemory? read = ContainerMemory.Read(files, "/pod/container");
            Assert.Equal(new ContainerMemory(1L << 30, 700), read);

            // A tighter limit below binds instead, with its own usage.
            System.IO.File.WriteAllText(Path.Combine(root, "pod", "container", "memory.max"), "536870912\n");
            Assert.Equal(new ContainerMemory(1L << 29, 900), ContainerMemory.Read(files, "/pod/container"));

            // No limit anywhere is none.
            Assert.Null(ContainerMemory.Read(files with { Limit = "memory.none" }, "/pod/container"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
