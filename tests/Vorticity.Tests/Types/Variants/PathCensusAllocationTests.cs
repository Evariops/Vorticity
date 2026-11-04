// A census counts the keys of rows whose metadata changes from one row to the next, as the rows of a merge coming from
// several sources do, without a string a key and a row: once their keys are met, counting them allocates nothing.
using System;
using Vorticity.Types.Variants;
using Xunit;

namespace Vorticity.Tests.Types.Variants;

public sealed class PathCensusAllocationTests
{
    [Fact]
    public void RowsOfChangingMetadataAreCountedWithoutAllocating()
    {
        Variant first = Variant.Object(("route", Variant.From("/a")), ("status", Variant.From(200L)), ("user", Variant.From("u1")));
        Variant second = Variant.Object(("extra", Variant.From(1L)), ("route", Variant.From("/b")), ("status", Variant.From(404L)));
        PathCensus census = new PathCensus();
        census.Add(first.MetadataSpan, first.ValueSpan);
        census.Add(second.MetadataSpan, second.ValueSpan);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1_000; i++)
        {
            census.Add(first.MetadataSpan, first.ValueSpan);
            census.Add(second.MetadataSpan, second.ValueSpan);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(2_002, census.Rows);
        Assert.True(allocated == 0, $"{allocated} bytes allocated counting 2 000 rows whose keys were met before");
    }

    /// <summary>
    /// A census cleared, as a lane's is between two inputs of a job, counts rows of the keys it met before, nested, bounded
    /// texts among them, without allocating: its tree, its keys' names and its text bounds kept.
    /// </summary>
    [Fact]
    public void ACensusClearedCountsRowsOfKeysItMetBeforeWithoutAllocating()
    {
        Variant first = Variant.Object(("route", Variant.From("/a/long/route")), ("user", Variant.Object(("id", Variant.From(7L)), ("name", Variant.From("u1")))));
        Variant second = Variant.Object(("route", Variant.From("/b")), ("status", Variant.From(404L)), ("tags", Variant.Array("a", "b")));
        PathCensus census = new PathCensus(stringBoundBytes: 8);
        census.Add(first.MetadataSpan, first.ValueSpan);
        census.Add(second.MetadataSpan, second.ValueSpan);
        census.Clear();

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1_000; i++)
        {
            census.Add(first.MetadataSpan, first.ValueSpan);
            census.Add(second.MetadataSpan, second.ValueSpan);
        }

        census.Clear();
        census.Add(second.MetadataSpan, second.ValueSpan);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(1, census.Rows);
        Assert.Equal(1, census.Root.Keys["status"].Values);
        Assert.Equal(0, census.Root.Keys["user"].Values);
        Assert.True(allocated == 0, $"{allocated} bytes allocated counting rows into a cleared census of their keys");
    }
}
