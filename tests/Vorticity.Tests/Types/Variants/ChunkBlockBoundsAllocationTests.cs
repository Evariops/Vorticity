// A writer counts each chunk's shredded keys block by block in one set of counts a column, emptied for the next chunk,
// and each block is encoded as it closes the way the file's statistics hold it: counting a chunk of texts, integers and
// booleans allocates nothing once those buffers have grown, where each block of text took two arrays of its bounds.
using System;
using Vorticity.Indexes;
using Vorticity.Types.Variants;
using Xunit;

namespace Vorticity.Tests.Types.Variants;

public sealed class ChunkBlockBoundsAllocationTests
{
    [Fact]
    public void AChunkCountedBlockByBlockAllocatesNothingOnceItsBuffersHaveGrown()
    {
        ChunkBlockBounds bounds = new ChunkBlockBounds([(0, "route", PathClass.String), (1, "status", PathClass.Integer), (2, "ok", PathClass.Boolean)], blockRows: 64, stringBoundBytes: 8);
        Variant[] routes = [Variant.From("/api/v1/orders"), Variant.From("/a"), Variant.From("/health/live")];
        Variant[] statuses = [Variant.From(200L), Variant.From(404L), Variant.From(503L)];
        Variant[] flags = [Variant.From(true), Variant.From(false)];
        VariantBlockStats.Builder file = new VariantBlockStats.Builder([("route", PathClass.String), ("status", PathClass.Integer), ("ok", PathClass.Boolean)]);
        for (int chunk = 0; chunk < 3; chunk++)
        {
            Count(bounds.Cleared(), routes, statuses, flags);
            file.Add(chunk * 64, bounds);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        Count(bounds.Cleared(), routes, statuses, flags);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(64, bounds.Keys[0].Count);
        Assert.True(allocated == 0, $"{allocated} bytes allocated counting a chunk of 4 096 rows in 64 blocks");
    }

    private static void Count(ChunkBlockBounds bounds, Variant[] routes, Variant[] statuses, Variant[] flags)
    {
        for (int row = 0; row < 4_096; row++)
        {
            bounds.Keys[0].Note(row, VariantScalar.Read(routes[row % routes.Length].ValueSpan));
            bounds.Keys[1].Note(row, VariantScalar.Read(statuses[row % statuses.Length].ValueSpan));
            bounds.Keys[2].Note(row, VariantScalar.Read(flags[row % flags.Length].ValueSpan));
        }

        _ = bounds.Keys[0].Count + bounds.Keys[1].Count + bounds.Keys[2].Count;
    }
}
