// The three injections of the in-memory store — latency, failures, crashes — and what each one
// is for.
//
// THE LATENCY IS NOT A TEST HELPER: the dataset's lookup invariant is stated in units of λ. The
// in-memory store injects a latency λ and no CPU cost; a cold clustering-key lookup completes
// within D × λ, D the number of dependent requests it makes, which no total of requests can
// prove, since parallel requests hide in a total. So the store must be able to be slow, and the
// first test here is the smallest possible instance of that invariant: requests issued together
// cost one λ, and requests issued in a row cost one λ each.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed class MemoryStoreFaultTests
{
    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    [Fact]
    public async Task RequestsIssuedTogetherCostOneLatencyAndOneStep()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using MemoryObjectStore inner = new MemoryObjectStore();
        await inner.PutIfAbsentAsync("data/one", Bytes("0123456789"), ct);
        await using CountingObjectStore store = new CountingObjectStore(inner);

        TimeSpan latency = TimeSpan.FromMilliseconds(50);
        inner.Latency = latency;
        store.Reset();

        Stopwatch watch = Stopwatch.StartNew();
        Task<ObjectRange>[] reads = new Task<ObjectRange>[8];
        for (int i = 0; i < reads.Length; i++)
        {
            reads[i] = store.GetRangeAsync("data/one", i, 1, ct).AsTask();
        }

        foreach (Task<ObjectRange> read in reads)
        {
            (await read).Dispose();
        }

        TimeSpan together = watch.Elapsed;
        Assert.Equal(8, store.CountOf(ObjectOperation.GetRange));

        // Eight requests, one step: they never waited for each other.
        Assert.Equal(1, store.DependentSteps);
        Assert.True(
            together < TimeSpan.FromMilliseconds(8 * 50 * 0.5),
            $"eight parallel requests at {latency.TotalMilliseconds} ms took {together.TotalMilliseconds} ms");

        store.Reset();
        watch.Restart();
        for (int i = 0; i < 4; i++)
        {
            using ObjectRange range = await store.GetRangeAsync("data/one", i, 1, ct);
        }

        // Four requests one after another: four steps, and the clock agrees.
        Assert.Equal(4, store.DependentSteps);
        Assert.True(
            watch.Elapsed >= TimeSpan.FromMilliseconds(4 * 50 * 0.8),
            $"four dependent requests at {latency.TotalMilliseconds} ms took {watch.Elapsed.TotalMilliseconds} ms");
    }

    [Fact]
    public async Task AFailureThrowsAndChangesNothing()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using MemoryObjectStore store = new MemoryObjectStore();
        await store.PutIfAbsentAsync("data/kept", Bytes("kept"), ct);

        store.Fails = (operation, key) => operation == ObjectOperation.PutIfAbsent && key == "data/new";
        await Assert.ThrowsAsync<ObjectStoreException>(
            async () => await store.PutIfAbsentAsync("data/new", Bytes("never"), ct));
        Assert.Null(await store.HeadAsync("data/new", ct));
        Assert.Equal(1, store.Count);

        store.Fails = null;
        Assert.Equal(PutOutcome.Created, await store.PutIfAbsentAsync("data/new", Bytes("now"), ct));
    }

    [Fact]
    public async Task ACrashLeavesTheObjectAndNeverTellsItsWriter()
    {
        // The state the rebase exists for: the put succeeded, the writer saw an exception, and
        // the key is now taken by bytes it wrote itself. A test that cannot produce this state
        // tests the easy half of the commit protocol.
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using MemoryObjectStore store = new MemoryObjectStore();
        store.CrashesAfterPut = key => key == "commit/00000000000000000009.vxc";

        await Assert.ThrowsAsync<ObjectStoreException>(
            async () => await store.PutIfAbsentAsync("commit/00000000000000000009.vxc", Bytes("mine"), ct));

        ObjectHead head = Assert.NotNull(await store.HeadAsync("commit/00000000000000000009.vxc", ct));
        Assert.Equal(4, head.Length);

        // And the retry finds the key taken, which is exactly what a rebase has to recognise.
        store.CrashesAfterPut = null;
        Assert.Equal(
            PutOutcome.Exists,
            await store.PutIfAbsentAsync("commit/00000000000000000009.vxc", Bytes("mine"), ct));
    }

    [Fact]
    public async Task AFailedListDoesNotHideTheKeys()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using MemoryObjectStore store = new MemoryObjectStore();
        await store.PutIfAbsentAsync("data/a", Bytes("a"), ct);
        int calls = 0;
        store.Fails = (operation, _) => operation == ObjectOperation.List && calls++ == 0;

        await Assert.ThrowsAsync<ObjectStoreException>(
            async () => await store.ListAsync("data/", null, 10, ct));
        Assert.Equal(["data/a"], await store.ListAsync("data/", null, 10, ct));
    }

    [Fact]
    public async Task CancellationIsHonouredWhileWaitingOutTheLatency()
    {
        await using MemoryObjectStore store = new MemoryObjectStore { Latency = TimeSpan.FromSeconds(30) };
        using CancellationTokenSource cancellation = new CancellationTokenSource();
        Task put = store.PutIfAbsentAsync("data/slow", Bytes("x"), cancellation.Token).AsTask();
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await put);
        store.Latency = TimeSpan.Zero;
        Assert.Null(await store.HeadAsync("data/slow", TestContext.Current.CancellationToken));
    }
}
