// A handle's cache of its partitions' indexes holds no more than its budget: past it, the indexes least recently asked
// for go, their pages with them, and one that alone passes it is not kept.
using System;
using Vorticity.Dataset;
using Vorticity.Indexes;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed class PartitionIndexCacheTests
{
    [Fact]
    public void PastItsBudgetTheCacheLetsTheIndexesLeastRecentlyAskedGo()
    {
        // The cache keeps what it is given and reads nothing of a directory: none is needed to weigh it.
        PartitionIndex directory = null!;
        PartitionIndexCache cache = new PartitionIndexCache(budget: 10_000);
        PartitionIndexCache.Held first = cache.Keep("a", 1, directory, 2_000, new byte[1_000], 0)!;
        PartitionIndexCache.Held second = cache.Keep("b", 2, directory, 2_000, new byte[1_000], 0)!;
        Assert.Equal(6_000, cache.Bytes);

        // The first asked again, then a page of the second's kept: the third to come lets the second go, least recently asked.
        Assert.Same(first, cache.Of("a", 1));
        second.Keep(4_096, new byte[1_000]);
        Assert.Equal(7_000, cache.Bytes);
        Assert.NotNull(cache.Keep("c", 3, directory, 2_500, new byte[1_000], 0));
        Assert.Null(cache.Of("b", 2));
        Assert.True(second.Gone);
        Assert.Same(first, cache.Of("a", 1));
        Assert.Equal(6_500, cache.Bytes);

        // A page loaded after its index went is not kept; an index that alone passes the budget is not kept.
        second.Keep(8_192, new byte[1_000]);
        Assert.Equal(6_500, cache.Bytes);
        Assert.Null(cache.Keep("d", 4, directory, 10_000, new byte[1], 0));
        Assert.Null(cache.Of("d", 4));
        Assert.Equal(1_000, cache.Of("a", 1)!.Tail.Length);
    }
}
