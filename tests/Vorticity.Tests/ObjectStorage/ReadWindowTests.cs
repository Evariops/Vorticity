// Reading at the speed of the link: the window of reads in flight over a store, a place per request,
// sized from what the store measured of its link; the connections halved when the service throttles;
// a slow read sent a second time; and a scan over a network starting its first batch on two lanes.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Buffers;
using Vorticity.Dataset;
using Vorticity.File;
using Vorticity.IO;
using Vorticity.ObjectStorage;
using Vorticity.ObjectStorage.Http;
using Vorticity.Scanning;
using Vorticity.Serialization.Schemas;
using Vorticity.Tests.Dataset;
using Xunit;

namespace Vorticity.Tests.ObjectStorage;

public sealed class ReadWindowTests
{
    private const string Bucket = "lake";

    [Fact]
    public async Task AWindowHoldsItsRequestsAndLetsTheWaitingInInTheOrderTheyCame()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        ReadWindow window = new ReadWindow(maxRequests: 2, ceiling: 1 << 20);
        await window.EnterAsync(10, ct);
        await window.EnterAsync(10, ct);

        ValueTask third = window.EnterAsync(10, ct);
        ValueTask fourth = window.EnterAsync(10, ct);
        Assert.False(third.IsCompleted);
        Assert.False(fourth.IsCompleted);

        window.Exit(10);
        await third;
        Assert.False(fourth.IsCompleted);
        window.Exit(10);
        await fourth;
        Assert.Equal(2, window.Requests);
        Assert.Equal(20, window.Bytes);
    }

    [Fact]
    public async Task AWindowCountsBytesAndARangeLargerThanItEntersAlone()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        ReadWindow window = new ReadWindow(maxRequests: 8, ceiling: 100);
        await window.EnterAsync(60, ct);
        ValueTask second = window.EnterAsync(60, ct);
        Assert.False(second.IsCompleted);

        // A small range behind a large one waits too: the large one would be passed over forever.
        ValueTask small = window.EnterAsync(1, ct);
        Assert.False(small.IsCompleted);

        window.Exit(60);
        await second;
        await small;

        window.Exit(60);
        window.Exit(1);
        await window.EnterAsync(500, ct);
        Assert.Equal(500, window.Bytes);
        ValueTask behind = window.EnterAsync(1, ct);
        Assert.False(behind.IsCompleted);
        window.Exit(500);
        await behind;
    }

    [Fact]
    public async Task ARequestCancelledWhileItWaitsTakesNoPlace()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        ReadWindow window = new ReadWindow(maxRequests: 1, ceiling: 1 << 20);
        await window.EnterAsync(10, ct);
        using CancellationTokenSource cancel = new CancellationTokenSource();
        ValueTask cancelled = window.EnterAsync(10, cancel.Token);
        ValueTask next = window.EnterAsync(10, ct);
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await cancelled);

        window.Exit(10);
        await next;
        Assert.Equal(1, window.Requests);
        Assert.Equal(10, window.Bytes);
    }

    [Theory]
    [InlineData(0, 64L << 20)]
    [InlineData(1L << 20, 8L << 20)]
    [InlineData(20L << 20, 40L << 20)]
    [InlineData(100L << 20, 64L << 20)]
    public void AWindowHoldsTwiceWhatTheLinkHoldsInFlightBetweenItsFloorAndItsCeiling(long measured, long expected)
    {
        ReadWindow window = new ReadWindow(maxRequests: 64, ceiling: 64L << 20, new Measured(64, measured));
        Assert.Equal(expected, window.MaxBytes);
    }

    [Fact]
    public void ALinkSaysItsMedianFromEightSamplesAndItsPercentilesFromSixtyFour()
    {
        LinkStatistics link = new LinkStatistics(TimeProvider.System);
        for (int i = 1; i <= 7; i++)
        {
            link.FirstByte(TimeSpan.FromMilliseconds(i));
        }

        Assert.Null(link.Percentile(50, minimum: 8));
        link.FirstByte(TimeSpan.FromMilliseconds(8));
        Assert.Equal(TimeSpan.FromMilliseconds(4), link.Percentile(50, minimum: 8));

        // Each sample before the sixty-fourth is counted at once, not at the next sort.
        link.FirstByte(TimeSpan.FromMilliseconds(9));
        Assert.Equal(TimeSpan.FromMilliseconds(5), link.Percentile(50, minimum: 8));
        Assert.Equal(TimeSpan.FromMilliseconds(9), link.Percentile(100, minimum: 8));
        Assert.Null(link.Percentile(99));
    }

    [Fact]
    public void ALinksRateIsTheBestOfItsRecentPeriodsAndAnIdleOneSaysNothing()
    {
        TickClock clock = new TickClock();
        LinkStatistics link = new LinkStatistics(clock);
        Assert.Equal(0, link.BytesPerSecond);

        // A busy quarter of a second at 40 MB/s.
        link.Delivered(5_000_000);
        clock.Advance(TimeSpan.FromMilliseconds(125));
        link.Delivered(5_000_000);
        Assert.Equal(0, link.BytesPerSecond);
        clock.Advance(TimeSpan.FromMilliseconds(125));
        link.Delivered(0);
        Assert.Equal(40_000_000, link.BytesPerSecond, 1e-3);

        // Ten idle seconds then a small body: a period of a low rate, which the best ignores.
        clock.Advance(TimeSpan.FromSeconds(10));
        link.Delivered(1_000);
        Assert.Equal(40_000_000, link.BytesPerSecond, 1e-3);

        for (int i = 1; i <= 8; i++)
        {
            link.FirstByte(TimeSpan.FromMilliseconds(30));
        }

        Assert.Equal(1_200_000, link.BandwidthDelayBytes);
    }

    [Fact]
    public async Task AThrottleHalvesTheConnectionsAndCalmGrowsThemBackOneAtATime()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using S3Emulator emulator = new S3Emulator();
        emulator.Seed(Bucket, "one", Encoding.ASCII.GetBytes("0123456789"));
        ManualClock clock = new ManualClock(DateTimeOffset.UtcNow);
        await using S3ObjectStore store = S3ReadTests.Store(
            emulator, transport: new StoreTransportOptions { MaxConnections = 8, RetryBaseDelay = TimeSpan.Zero, TimeProvider = clock });
        emulator.Intercept = (_, index) => index < 2 ? S3Emulator.Error(HttpStatusCode.ServiceUnavailable, "SlowDown", "Please reduce your request rate.") : null;

        byte[] destination = new byte[10];
        Assert.Equal(10, (await store.ReadAsync("one", 0, destination, null, ct)).BytesRead);
        Assert.Equal(2, store.Transport.ConcurrencyLimit);

        // Not calm yet: a success leaves the limit down.
        await store.ReadAsync("one", 0, destination, null, ct);
        Assert.Equal(2, store.Transport.ConcurrencyLimit);

        clock.Advance(ConcurrencyLimit.Calm);
        await store.ReadAsync("one", 0, destination, null, ct);
        await store.ReadAsync("one", 0, destination, null, ct);
        Assert.Equal(4, store.Transport.ConcurrencyLimit);
    }

    [Fact]
    public async Task ALimitHeldDownLetsNoMoreRequestsThroughThanItAllows()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        ConcurrencyLimit limit = new ConcurrencyLimit(4, TimeProvider.System);
        limit.Throttled();
        Assert.Equal(2, limit.Limit);
        await limit.EnterAsync(ct);
        await limit.EnterAsync(ct);
        ValueTask third = limit.EnterAsync(ct);
        Assert.False(third.IsCompleted);
        limit.Exit();
        await third;
        limit.Exit();
        limit.Exit();
    }

    [Fact]
    public async Task AReadSlowerThanThePercentileIsSentAgainAndTheFirstAnswerKept()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using S3Emulator emulator = new S3Emulator();
        byte[] content = Encoding.ASCII.GetBytes("0123456789");
        emulator.Seed(Bucket, "one", content);
        await using S3ObjectStore store = S3ReadTests.Store(
            emulator, transport: new StoreTransportOptions { HedgeAfterPercentile = 99, RetryBaseDelay = TimeSpan.Zero });
        byte[] destination = new byte[10];
        for (int i = 0; i < 64; i++)
        {
            await store.ReadAsync("one", 0, destination, null, ct);
        }

        Assert.Equal(0, store.Transport.Hedged);
        int slow = emulator.Requests;
        emulator.Hold = (_, index, token) => index == slow ? Task.Delay(TimeSpan.FromSeconds(20), token) : Task.CompletedTask;
        Array.Clear(destination);
        Stopwatch elapsed = Stopwatch.StartNew();
        ObjectRead read = await store.ReadAsync("one", 0, destination, null, ct);

        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(10), $"the hedge should have answered, not the slow path ({elapsed.Elapsed})");
        Assert.Equal(10, read.BytesRead);
        Assert.Equal(content, destination);
        Assert.Equal(1, store.Transport.Hedged);
        Assert.Equal(slow + 2, emulator.Requests);
    }

    [Fact]
    public async Task NoReadIsHedgedWithoutABudget()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using S3Emulator emulator = new S3Emulator();
        emulator.Seed(Bucket, "one", Encoding.ASCII.GetBytes("0123456789"));
        await using S3ObjectStore store = S3ReadTests.Store(
            emulator, transport: new StoreTransportOptions { HedgeAfterPercentile = 50, HedgeBudgetPercent = 0, RetryBaseDelay = TimeSpan.Zero });
        emulator.Hold = (_, index, token) => index % 2 == 0 ? Task.Delay(5, token) : Task.CompletedTask;
        byte[] destination = new byte[10];
        for (int i = 0; i < 80; i++)
        {
            await store.ReadAsync("one", 0, destination, null, ct);
        }

        Assert.Equal(0, store.Transport.Hedged);
        Assert.Equal(80, emulator.Requests);
    }

    [Fact]
    public async Task AWindowBoundsTheRequestsOfABatchOneAPlaceEach()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using MemoryObjectStore inner = new MemoryObjectStore();
        Assert.Equal(PutOutcome.Created, await inner.PutIfAbsentAsync("one", new byte[64 * 1024], ct));
        await using Overlapping store = new Overlapping(inner);
        ReadWindow window = new ReadWindow(maxRequests: 2, ceiling: 1 << 20);
        await using ObjectStoreSource source = new ObjectStoreSource(
            store, "one", 64 * 1024, new SegmentReadOptions { CoalesceGapBytes = 0 }, window: window);
        using SegmentRequestSet set = new SegmentRequestSet();
        for (int i = 0; i < 8; i++)
        {
            set.Add(new SegmentSpec((ulong)(i * 8192), 64, 0, 0, 0));
        }

        await source.ReadManyAsync(set, ct);

        Assert.True(set.IsPopulated);
        Assert.Equal(8, store.Reads);
        Assert.Equal(2, store.Peak);
        Assert.Equal(0, window.Requests);
        Assert.Equal(0, window.Bytes);
        Assert.True(((ISegmentReader)source).CountsItsReads);
        Assert.False(((ISegmentReader)source).ReadsOverNetwork);
    }

    [Fact]
    public async Task TheSessionCountsNoBatchOfAReaderThatCountsItsRequests()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using MemoryObjectStore inner = new MemoryObjectStore();
        Assert.Equal(PutOutcome.Created, await inner.PutIfAbsentAsync("one", new byte[64 * 1024], ct));
        await using Overlapping store = new Overlapping(inner);
        await using VortexSession session = VortexSession.Create(o => o.MaxConcurrentReads = 1);

        async Task<int> PeakAsync(ReadWindow? window)
        {
            store.Reset();
            ISegmentReader reader = SessionReader.Wrap(new ObjectStoreSource(store, "one", 64 * 1024, window: window), session);
            await using (reader)
            {
                using SegmentRequestSet first = new SegmentRequestSet();
                using SegmentRequestSet second = new SegmentRequestSet();
                first.Add(new SegmentSpec(0, 64, 0, 0, 0));
                second.Add(new SegmentSpec(32_768, 64, 0, 0, 0));
                await Task.WhenAll(reader.ReadManyAsync(first, ct).AsTask(), reader.ReadManyAsync(second, ct).AsTask());
            }

            return store.Peak;
        }

        Assert.Equal(1, await PeakAsync(window: null));
        Assert.Equal(2, await PeakAsync(new ReadWindow(maxRequests: 4, ceiling: 1 << 20)));
    }

    [Fact]
    public async Task EveryViewOfAStoreSharesOneWindowAndAStoreThatMeasuresNothingHasNone()
    {
        using S3Emulator emulator = new S3Emulator();
        await using S3ObjectStore store = S3ReadTests.Store(emulator, transport: new StoreTransportOptions { MaxConnections = 12 });
        await using MemoryObjectStore memory = new MemoryObjectStore();
        await using VortexSession session = VortexSession.Create(o => o.ReadWindowBytes = 32L << 20);

        ReadWindow window = Assert.IsType<ReadWindow>(session.WindowFor(store));
        Assert.Same(window, session.WindowFor(new PrefixedObjectStore(store, "tables/")));
        Assert.Same(window, session.WindowFor(new PrefixedObjectStore(new PrefixedObjectStore(store, "a/"), "b/")));
        Assert.Equal(32L << 20, window.MaxBytes);
        Assert.Null(session.WindowFor(memory));
        Assert.Null(session.WindowFor(new PrefixedObjectStore(memory, "x/")));
        Assert.Throws<ArgumentOutOfRangeException>(() => VortexSession.Create(o => o.ReadWindowBytes = 0));
    }

    [Fact]
    public async Task AScanOverAStoreStartsTwoSplitsBeforeItsFirstBatchAndWidensAtTheNext()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        byte[] bytes = await System.IO.File.ReadAllBytesAsync(Vorticity.Tests.Scan.Corpus.Path("containers/zoned_many_zones_nulls"), ct);
        using S3Emulator emulator = new S3Emulator();
        emulator.Seed(Bucket, "multi.vortex", bytes);
        await using VortexSession session = VortexSession.Create(o => o.ObjectStores.Add("s3://lake", S3ReadTests.Store(emulator)));

        // What the scan sends before its first batch, every request parked until it has stopped
        // coming; then, once the consumer asks for the second, what it sends within ten seconds.
        async Task<(int First, int Widened)> StartedAsync(int degree)
        {
            await using VortexFile file = await session.OpenAsync("s3://lake/multi.vortex", cancellationToken: ct);
            Assert.True(file.Source.ReadsOverNetwork);
            TaskCompletionSource release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int parked = 0;
            emulator.Hold = async (_, _, token) =>
            {
                Interlocked.Increment(ref parked);
                await release.Task.WaitAsync(token);
            };

            IAsyncEnumerator<RecordBatch> batches = file.ScanBuilder().WithMaxBatchRows(1024)
                .WithDegreeOfParallelism(degree).ExecuteAsync().GetAsyncEnumerator(ct);
            try
            {
                ValueTask<bool> moving = batches.MoveNextAsync();
                int first = await SettledAsync(() => Volatile.Read(ref parked), ct);
                emulator.Hold = null;
                release.SetResult();
                Assert.True(await moving);
                int before = emulator.Requests;
                Assert.True(await batches.MoveNextAsync());
                Stopwatch waited = Stopwatch.StartNew();
                while (emulator.Requests - before < degree - first && waited.Elapsed < TimeSpan.FromSeconds(10))
                {
                    await Task.Delay(10, ct);
                }

                return (first, emulator.Requests - before);
            }
            finally
            {
                emulator.Hold = null;
                release.TrySetResult();
                await batches.DisposeAsync();
            }
        }

        // At degree 1 the first batch reads its own split alone: what a split costs in requests.
        (int split, _) = await StartedAsync(degree: 1);
        (int wide, int widened) = await StartedAsync(degree: 8);

        Assert.InRange(split, 1, 2);
        Assert.Equal(BatchAsyncEnumerator.FirstSplitsOverNetwork * split, wide);
        Assert.True(widened >= 8 - wide, $"the second batch should widen the scan to its degree ({widened} requests)");
    }

    /// <summary>Waits until <paramref name="count"/> is positive and has not moved for a fifth of a second.</summary>
    private static async Task<int> SettledAsync(Func<int> count, CancellationToken ct)
    {
        int seen = -1;
        Stopwatch still = Stopwatch.StartNew();
        while (true)
        {
            int now = count();
            if (now != seen)
            {
                seen = now;
                still.Restart();
            }
            else if (now > 0 && still.Elapsed > TimeSpan.FromMilliseconds(200))
            {
                return now;
            }

            await Task.Delay(10, ct);
        }
    }

    private sealed class Measured(int connections, long bandwidthDelayBytes) : IMeasuredObjectStore
    {
        public int Connections => connections;

        public long BandwidthDelayBytes => bandwidthDelayBytes;
    }

    /// <summary>A clock whose timestamps a test moves by hand.</summary>
    private sealed class TickClock : TimeProvider
    {
        private long _ticks = 1;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => _ticks;

        internal void Advance(TimeSpan by) => _ticks += by.Ticks;
    }

    /// <summary>A store that holds each read a moment, and counts the reads in flight at once.</summary>
    private sealed class Overlapping(IObjectStore inner) : ForwardingStore(inner)
    {
        private int _inFlight;
        private int _peak;
        private int _reads;

        internal int Peak => Volatile.Read(ref _peak);

        internal int Reads => Volatile.Read(ref _reads);

        internal void Reset()
        {
            _peak = 0;
            _reads = 0;
        }

        public override async ValueTask<ObjectRead> ReadAsync(string key, long offset, Memory<byte> destination, string? ifToken, CancellationToken cancellationToken)
        {
            int now = Interlocked.Increment(ref _inFlight);
            Interlocked.Increment(ref _reads);
            int peak;
            while (now > (peak = Volatile.Read(ref _peak)) && Interlocked.CompareExchange(ref _peak, now, peak) != peak)
            {
            }

            try
            {
                await Task.Delay(30, cancellationToken);
                return await Inner.ReadAsync(key, offset, destination, ifToken, cancellationToken);
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }
    }
}
