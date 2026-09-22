// The counter behind the dataset's request budget, on its own: what it counts, and the one
// number a total of requests cannot give.
using System;
using System.Text;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed class CountingObjectStoreTests
{
    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    [Fact]
    public async Task ItCountsEveryOperationByKindAndTheBytesBothWays()
    {
        await using MemoryObjectStore inner = new MemoryObjectStore();
        await using CountingObjectStore store = new CountingObjectStore(inner);

        Assert.Equal(PutOutcome.Created, await store.PutIfAbsentAsync("data/a", Bytes("12345"), default));
        Assert.Equal(PutOutcome.Exists, await store.PutIfAbsentAsync("data/a", Bytes("67"), default));
        Assert.NotNull(await store.HeadAsync("data/a", default));
        using (ObjectRange range = await store.GetRangeAsync("data/a", 1, 3, default))
        {
            Assert.Equal(3, range.Length);
        }

        Assert.Single(await store.ListAsync("data/", null, 10, default));
        await store.DeleteAsync(["data/a"], default);
        Assert.Null(await inner.HeadAsync("data/a", default));

        Assert.Equal(2, store.CountOf(ObjectOperation.PutIfAbsent));
        Assert.Equal(1, store.CountOf(ObjectOperation.Head));
        Assert.Equal(1, store.CountOf(ObjectOperation.GetRange));
        Assert.Equal(1, store.CountOf(ObjectOperation.List));
        Assert.Equal(1, store.CountOf(ObjectOperation.Delete));
        Assert.Equal(6, store.Requests);

        Assert.Equal(3, store.BytesRead);

        // The refused put's bytes count too: they crossed the seam, which is what a network bills.
        Assert.Equal(7, store.BytesWritten);
        Assert.Equal(1, store.KeysListed);
    }

    [Fact]
    public async Task ResetForgetsTheCountsAndNotTheStore()
    {
        await using MemoryObjectStore inner = new MemoryObjectStore();
        await using CountingObjectStore store = new CountingObjectStore(inner);
        await store.PutIfAbsentAsync("data/a", Bytes("x"), default);
        store.Reset();

        Assert.Equal(0, store.Requests);
        Assert.Equal(0, store.BytesWritten);
        Assert.Equal(0, store.DependentSteps);
        Assert.NotNull(await store.HeadAsync("data/a", default));
        Assert.Equal(1, store.Requests);
    }

    [Fact]
    public async Task ItCountsWhatFailedToo()
    {
        // A request that throws is a request that was made: a counter that only saw the successes
        // would say a retrying reader was cheap.
        await using MemoryObjectStore inner = new MemoryObjectStore
        {
            Fails = (operation, _) => operation == ObjectOperation.GetRange,
        };
        await using CountingObjectStore store = new CountingObjectStore(inner);
        await store.PutIfAbsentAsync("data/a", Bytes("x"), default);
        store.Reset();

        await Assert.ThrowsAsync<ObjectStoreException>(
            async () => await store.GetRangeAsync("data/a", 0, 1, default));
        Assert.Equal(1, store.CountOf(ObjectOperation.GetRange));
        Assert.Equal(0, store.BytesRead);
        Assert.Equal(1, store.DependentSteps);
    }

    [Fact]
    public async Task DisposingItLeavesTheStoreAloneUnlessItOwnsIt()
    {
        MemoryObjectStore inner = new MemoryObjectStore();
        await store(new CountingObjectStore(inner, ownsInner: false));
        Assert.Equal(1, inner.Count);

        await store(new CountingObjectStore(inner, ownsInner: true));
        Assert.Equal(0, inner.Count);

        static async Task store(CountingObjectStore counting)
        {
            await using (counting)
            {
                await counting.PutIfAbsentAsync($"data/{Guid.NewGuid():N}", Bytes("x"), default);
            }
        }
    }
}
