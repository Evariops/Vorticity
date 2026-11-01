// What a lane's granule plans work in, kept by its scan context from one scan to the next.
//
// THE ORACLE IS THE ALLOCATOR: plans made over a kept scratch allocate the plans alone.
using System;
using Vorticity.Indexes;
using Xunit;

namespace Vorticity.Tests.Indexes;

public sealed class GranuleScratchTests
{
    /// <summary>
    /// A lane's plans are made for each scan of each object a question reads; over the scratch its context keeps, they
    /// allocate themselves alone, where plans of their own make an arena of nodes, a planner and their dictionaries, some
    /// twenty kilobytes each. The scratch hands its dictionaries over emptied.
    /// </summary>
    [Fact]
    public void PlansOverAKeptScratchAllocateThemselvesAlone()
    {
        GranuleScratch scratch = new GranuleScratch();
        _ = new GranulePlans(null!, null!, scratch: scratch);
        long before = GC.GetAllocatedBytesForCurrentThread();
        _ = new GranulePlans(null!, null!, scratch: scratch);
        long kept = GC.GetAllocatedBytesForCurrentThread() - before;

        before = GC.GetAllocatedBytesForCurrentThread();
        _ = new GranulePlans(null!, null!);
        long own = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(kept * 20 < own, $"{kept} bytes over a kept scratch, {own} of their own");

        scratch.Sparse()[7] = true;
        Assert.Empty(scratch.Sparse());
    }
}
