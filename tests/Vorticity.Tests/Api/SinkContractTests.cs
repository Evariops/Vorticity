using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.IO;
using Xunit;

namespace Vorticity.Tests.Api;

/// <summary>
/// What a sink promises beyond its bytes: an owned batch carries the scan's one schema, a scan's
/// activity ends however the sink ends and never stays current on the caller, and a scan counts
/// the cache hits that were its own.
/// </summary>
[Trait("Category", "ApiContract")]
public sealed class SinkContractTests
{
    [Fact]
    public async Task OwnedBatchesShareTheScansSchema()
    {
        await using VortexFile file = await VortexFile.OpenAsync(await ContractFile.PathAsync(), TestContext.Current.CancellationToken);

        VortexSchema? first = null;
        int batches = 0;
        await foreach (RecordBatch batch in file.Scan<Reading>().ToBatchesAsync(TestContext.Current.CancellationToken))
        {
            using (batch)
            {
                first ??= batch.Schema;
                Assert.Same(first, batch.Schema);
                Assert.Same(RecordBinding.For<Reading>(first, null), RecordBinding.For<Reading>(batch.Schema, null));
                batches++;
            }
        }

        Assert.True(batches > 1, "the file gave one batch, too few to compare");
        Assert.Equal(3, first!.Count);

        // The batch kept out of a borrowed enumeration carries the same one.
        VortexSchema? kept = null;
        await foreach (Columns<Reading> columns in file.Scan<Reading>().WithCancellation(TestContext.Current.CancellationToken))
        {
            using RecordBatch owned = columns.ToOwned();
            kept ??= owned.Schema;
            Assert.Same(kept, owned.Schema);
        }
    }

    [Fact]
    public async Task AScansActivityEndsWithItsSinkAndLeavesTheCallersCurrentAlone()
    {
        await using VortexFile file = await VortexFile.OpenAsync(await ContractFile.PathAsync(), TestContext.Current.CancellationToken);
        using Activity caller = new Activity("caller").Start();

        // The listener hears every scan of the process, the neighbouring tests' included: only the
        // children of this test's own activity are this test's.
        int started = 0;
        int stopped = 0;
        using ActivityListener listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == VortexDiagnostics.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> options) => ActivitySamplingResult.AllData,
            ActivityStarted = activity => Count(activity, caller, ref started),
            ActivityStopped = activity => Count(activity, caller, ref stopped),
        };
        ActivitySource.AddActivityListener(listener);

        // A borrowed enumeration run to its end.
        await foreach (Columns<Reading> columns in file.Scan<Reading>().WithCancellation(TestContext.Current.CancellationToken))
        {
            Assert.Same(caller, Activity.Current);
        }

        Assert.Equal((1, 1), (started, stopped));
        Assert.Same(caller, Activity.Current);

        // An owned enumeration left early.
        await foreach (RecordBatch batch in file.Scan<Reading>().ToBatchesAsync(TestContext.Current.CancellationToken))
        {
            batch.Dispose();
            break;
        }

        Assert.Equal((2, 2), (started, stopped));
        Assert.Same(caller, Activity.Current);

        // A count that fails.
        using CancellationTokenSource cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await file.Scan<Reading>().Where(r => r.Celsius > 30.0).CountAsync(cancelled.Token));

        Assert.Equal((3, 3), (started, stopped));
        Assert.Same(caller, Activity.Current);

        // An aggregation, and a tool scan.
        _ = await file.Scan<Reading>().SumAsync(r => r.Day, TestContext.Current.CancellationToken);
        await foreach (BatchView view in file.Scan("City").WithCancellation(TestContext.Current.CancellationToken))
        {
            Assert.Same(caller, Activity.Current);
        }

        Assert.Equal((5, 5), (started, stopped));
        Assert.Same(caller, Activity.Current);
    }

    private static void Count(Activity activity, Activity parent, ref int counter)
    {
        if (ReferenceEquals(activity.Parent, parent))
        {
            Interlocked.Increment(ref counter);
        }
    }

    [Fact]
    public async Task AScanCountsTheCacheHitsThatWereItsOwn()
    {
        await using VortexSession session = VortexSession.Create(options => options.SegmentCache = new SegmentCache(64L * 1024 * 1024));
        CountingSource source = new CountingSource(new MemoryMappedSegmentSource(await ContractFile.PathAsync()));
        await using VortexFile file = await session.OpenAsync(source, cancellationToken: TestContext.Current.CancellationToken);

        // The first scan fills the cache; every later one is served from it.
        Scan<Reading> cold = file.Scan<Reading>();
        await foreach (Columns<Reading> columns in cold.WithCancellation(TestContext.Current.CancellationToken))
        {
        }

        Assert.Equal(0, cold.Statistics.CacheHits);
        Assert.True(cold.Statistics.Requests > 0);

        // A scan that runs while another scan of the session runs counts its own hits, not both.
        Scan<Reading> outer = file.Scan<Reading>();
        Scan<Reading>.AsyncEnumerator batches = outer.GetAsyncEnumerator(TestContext.Current.CancellationToken);
        try
        {
            Assert.True(await batches.MoveNextAsync());

            Scan<Reading> inner = file.Scan<Reading>();
            await foreach (Columns<Reading> columns in inner.WithCancellation(TestContext.Current.CancellationToken))
            {
            }

            Assert.Equal(inner.Statistics.Requests, inner.Statistics.CacheHits);

            while (await batches.MoveNextAsync())
            {
            }
        }
        finally
        {
            await batches.DisposeAsync();
        }

        Assert.Equal(outer.Statistics.Requests, outer.Statistics.CacheHits);
        Assert.Equal(cold.Statistics.Requests, outer.Statistics.Requests);
    }
}
