// The writer's working arrays: below a mebibyte the shared pool's, past it the process's shelf's at a power of two, which
// a full collection does not trim and which finds an array given back on another thread again.
using System;
using System.Threading;
using Vorticity.Aggregating;
using Vorticity.Tests.Scan;
using Xunit;

namespace Vorticity.Tests.Aggregation;

[Collection(nameof(AllocationCollection))]
[Trait("Category", "RunsAlone")]
public sealed class WorkArraysTests
{
    /// <summary>
    /// An array of 3 MB asked is one of 4 MB, given back on another thread and taken again after a full collection: the
    /// same array, none made new (a few bytes of the shelf's own at most), where the shared pool kept it in that thread's own
    /// slot, which a collection trims.
    /// </summary>
    [Fact]
    public void ALargeArrayGivenBackOnAnotherThreadIsTakenAgainPastAFullCollection()
    {
        const int Asked = 3 << 20;
        byte[] first = WorkArrays.Rent<byte>(Asked);
        Assert.Equal(4 << 20, first.Length);
        Thread other = new Thread(() => WorkArrays.Return(first));
        other.Start();
        other.Join();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true);
        GC.WaitForPendingFinalizers();

        long before = GC.GetAllocatedBytesForCurrentThread();
        byte[] again = WorkArrays.Rent<byte>(Asked);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Same(first, again);
        Assert.True(allocated < 1_024, $"{allocated} bytes allocated taking it again");
        WorkArrays.Return(again);
    }

    /// <summary>A small array is the shared pool's, of its length; a large one given back cleared is the one taken again.</summary>
    [Fact]
    public void ASmallArrayIsTheSharedPoolsAndALargeOneGivenBackIsTakenAgain()
    {
        int[] small = WorkArrays.Rent<int>(1_000);
        Assert.Equal(1_024, small.Length);
        WorkArrays.Return(small);

        long[] large = WorkArrays.Rent<long>(300_000);
        Assert.Equal(1 << 19, large.Length);
        large.AsSpan().Fill(7);
        WorkArrays.Return(large, clearArray: true);
        long[] again = WorkArrays.Rent<long>(300_000);
        Assert.Same(large, again);
        WorkArrays.Return(again);
    }
}
