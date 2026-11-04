// A run-end plan keeps the runs its count rented, until it is written or let go: choosing one for a column of long runs,
// as a shredded chunk's typed columns are, allocates nothing once the pool holds their arrays, where the plan copied its
// runs into two arrays of its own each time.
using System;
using Vorticity.Arrays;
using Vorticity.Tests.Columns;
using Vorticity.Writing;
using Xunit;

namespace Vorticity.Tests.Writing;

public sealed class RunEndPlanAllocationTests
{
    [Fact]
    public void ARunEndPlanChosenAndLetGoAllocatesNothing()
    {
        using ColumnFixture fixture = new ColumnFixture();
        long[] values = new long[65_536];
        for (int row = 0; row < values.Length; row++)
        {
            values[row] = row / 64;
        }

        int node = fixture.Int64Node(values, Validity.NonNullable);
        for (int warm = 0; warm < 3; warm++)
        {
            ColumnPlan plan = ColumnCompressor.Choose(fixture.Arena, node);
            Assert.Equal(ColumnScheme.RunEnd, plan.Scheme);
            Assert.Equal(1_024, plan.RunCount);
            plan.ReleaseCodes();
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 16; i++)
        {
            ColumnCompressor.Choose(fixture.Arena, node).ReleaseCodes();
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated == 0, $"{allocated} bytes allocated choosing a run-end plan sixteen times");
    }
}
