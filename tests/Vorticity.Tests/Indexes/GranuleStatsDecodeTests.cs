// A column's statistics over its granules decoded from their bytes: the values each granule lists, and what the decode
// allocates for them.
using System;
using System.Linq;
using Vorticity.Indexes;
using Vorticity.Serialization.Protobuf;
using Xunit;

namespace Vorticity.Tests.Indexes;

public sealed class GranuleStatsDecodeTests
{
    /// <summary>
    /// A thousand granules of 8 192 rows, each listing two hundred of a dictionary's values with their rows: decoded, each
    /// granule gives its values back, and the decode allocates what the statistics hold and little more, under one and a
    /// half times it. Grown a value at a time and then copied, the lists cost three times their length: the statistics of
    /// a week of S2's requests (T2) came to some 700 MB at a question's first asking.
    /// </summary>
    [Fact]
    public void TheValuesTheGranulesListDecodeIntoListsOfTheirLength()
    {
        const int Granules = 1_000;
        const int Listed = 200;
        const int Rows = 8_192;
        byte[] bytes = Encode(Granules, Listed, Rows);

        // Once first, so that the shelf the decode borrows from holds its arrays.
        Assert.NotNull(GranuleStats.From(bytes));
        long before = GC.GetAllocatedBytesForCurrentThread();
        GranuleStats stats = GranuleStats.From(bytes)!;
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        for (int granule = 0; granule < Granules; granule += 97)
        {
            (ReadOnlyMemory<int> ids, ReadOnlyMemory<long> rows) = stats.ValuesOf(granule);
            Assert.Equal(Ids(granule, Listed), ids.ToArray());
            Assert.Equal(RowsOf(granule, Listed, Rows), rows.ToArray());
        }

        Assert.True(allocated < stats.Bytes * 3 / 2, $"{allocated} bytes allocated for statistics of {stats.Bytes}");
    }

    private static int[] Ids(int granule, int listed) => [.. Enumerable.Range(0, listed).Select(i => (granule % 7) + (3 * i))];

    private static long[] RowsOf(int granule, int listed, int rows)
    {
        long[] taken = [.. Enumerable.Repeat((long)(rows / listed), listed)];
        taken[granule % listed] += rows - ((rows / listed) * listed);
        return taken;
    }

    private static byte[] Encode(int granules, int listed, int rows)
    {
        ProtoWriter writer = new ProtoWriter(1 << 20);
        try
        {
            GranuleStats.WriteHeader(ref writer, granules, values: true, measure: null);
            for (int granule = 0; granule < granules; granule++)
            {
                GranuleStats.WriteRows(ref writer, rows, 0);
            }

            for (int granule = 0; granule < granules; granule++)
            {
                GranuleStats.WriteValues(ref writer, listed, Ids(granule, listed), RowsOf(granule, listed, rows));
            }

            int named = 6 + (3 * listed);
            GranuleStats.WriteDictionary(ref writer, [.. Enumerable.Range(0, named).Select(static id => BitConverter.GetBytes(id))]);
            return writer.WrittenSpan.ToArray();
        }
        finally
        {
            writer.Dispose();
        }
    }
}
