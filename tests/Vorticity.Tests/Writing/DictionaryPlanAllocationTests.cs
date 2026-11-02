// A dictionary plan walked for keeps the first rows its walk rented, until it is written or let go: choosing one for a
// column of interleaved values allocates nothing once the pool holds their arrays, where the plan copied the first row of
// each entry into an array of its own each time, a tenth of what a consolidation of log records allocated.
using System;
using Vorticity.Arrays;
using Vorticity.Tests.Columns;
using Vorticity.Writing;
using Xunit;

namespace Vorticity.Tests.Writing;

public sealed class DictionaryPlanAllocationTests
{
    [Fact]
    public void ADictionaryPlanChosenAndLetGoAllocatesNothing()
    {
        using ColumnFixture fixture = new ColumnFixture();
        Random random = new Random(41);
        long[] entries = new long[300];
        for (int entry = 0; entry < entries.Length; entry++)
        {
            entries[entry] = random.NextInt64();
        }

        long[] values = new long[65_536];
        for (int row = 0; row < values.Length; row++)
        {
            values[row] = entries[(row * 7) % entries.Length];
        }

        int node = fixture.Int64Node(values, Validity.NonNullable);
        for (int warm = 0; warm < 3; warm++)
        {
            ColumnPlan plan = ColumnCompressor.Choose(fixture.Arena, node);
            Assert.Equal(ColumnScheme.Dict, plan.Scheme);
            Assert.Equal(entries.Length, plan.Entries);
            Assert.Equal(entries.Length, plan.EntryRows.Length);
            plan.ReleaseCodes();
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 16; i++)
        {
            ColumnCompressor.Choose(fixture.Arena, node).ReleaseCodes();
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated == 0, $"{allocated} bytes allocated choosing a dictionary plan sixteen times");
    }
}
