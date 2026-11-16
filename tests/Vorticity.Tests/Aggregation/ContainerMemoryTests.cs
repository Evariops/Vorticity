// What the process reads of its container's memory: a limit, or none when unbounded; and a working set, what the
// container uses less the inactive file pages the kernel reclaims first, as v2 and v1 name them.
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
}
