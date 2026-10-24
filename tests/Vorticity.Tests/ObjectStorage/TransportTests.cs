// The transport's deadlines and retries against the S3 emulator: a body timed by its progress rather
// than by the first byte of its answer, a refusal the service never applied sent again without asking
// what became of it, a throttle that halves the requests in flight once per episode, a region learned
// for every caller whoever gives up, and an upload given up stopped at once and aborted under a
// deadline of its own.
using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipelines;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.IO;
using Vorticity.ObjectStorage;
using Vorticity.ObjectStorage.Http;
using Xunit;

namespace Vorticity.Tests.ObjectStorage;

public sealed class TransportTests
{
    private const string Bucket = "lake";

    [Fact]
    public async Task ABodyThatStallsIsSentAgainAsNeverAppliedWithoutAHead()
    {
        // The connection takes the first slice of the first put and nothing more: past the stall
        // deadline the attempt is cut short, its body unfinished, so the conditional put goes again
        // without asking the service whether it landed.
        CancellationToken ct = TestContext.Current.CancellationToken;
        using S3Emulator emulator = new S3Emulator();
        int puts = 0;
        emulator.Hold = async (request, _, token) =>
        {
            if (request.Method == HttpMethod.Put && Interlocked.Increment(ref puts) == 1)
            {
                await request.Content!.CopyToAsync(new StallingStream(256 << 10), token);
            }
        };
        await using S3ObjectStore store = Store(
            emulator,
            new StoreTransportOptions { RetryBaseDelay = TimeSpan.Zero, StallTimeout = TimeSpan.FromMilliseconds(300) },
            threshold: 32L << 20);

        Assert.Equal(PutOutcome.Created, await store.PutIfAbsentAsync("commit/1", new byte[1 << 20], ct));
        Assert.Equal(2, emulator.Log.Count(line => line.StartsWith("PUT", StringComparison.Ordinal)));
        Assert.DoesNotContain(emulator.Log, line => line.StartsWith("HEAD", StringComparison.Ordinal));
        Assert.Equal(1 << 20, emulator.BytesOf(Bucket, "commit/1")!.Length);
    }

    [Fact]
    public async Task ASlowBodyThatProgressesIsWaitedForPastTheFirstByteDeadline()
    {
        // Each slice takes a while, the whole body far longer than the first-byte deadline: it goes.
        CancellationToken ct = TestContext.Current.CancellationToken;
        using S3Emulator emulator = new S3Emulator();
        emulator.Hold = async (request, _, token) =>
        {
            if (request.Method == HttpMethod.Put)
            {
                await request.Content!.CopyToAsync(new SlowStream(TimeSpan.FromMilliseconds(150)), token);
            }
        };
        await using S3ObjectStore store = Store(
            emulator,
            new StoreTransportOptions
            {
                RetryBaseDelay = TimeSpan.Zero,
                FirstByteTimeout = TimeSpan.FromMilliseconds(400),
                StallTimeout = TimeSpan.FromSeconds(5),
                MaxAttempts = 1,
            },
            threshold: 32L << 20);

        Assert.Equal(PutOutcome.Created, await store.PutIfAbsentAsync("commit/1", new byte[1 << 20], ct));
        Assert.Single(emulator.Log, line => line.StartsWith("PUT", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AThrottledConditionalPutIsSentAgainWithoutAHead()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using S3Emulator emulator = new S3Emulator();
        emulator.Intercept = (request, index) => request.Method == HttpMethod.Put && index == 0
            ? S3Emulator.Error(HttpStatusCode.ServiceUnavailable, "SlowDown", "Please reduce your request rate.")
            : null;
        await using S3ObjectStore store = Store(emulator);

        Assert.Equal(PutOutcome.Created, await store.PutIfAbsentAsync("commit/1", new byte[] { 1 }, ct));
        Assert.Equal(2, emulator.Log.Count(line => line.StartsWith("PUT", StringComparison.Ordinal)));
        Assert.DoesNotContain(emulator.Log, line => line.StartsWith("HEAD", StringComparison.Ordinal));
        Assert.Equal(32, store.Transport.ConcurrencyLimit);
    }

    [Fact]
    public void ThrottlesOfOneEpisodeHalveTheLimitOnce()
    {
        // Sixty-four requests throttled together were all sent under the old limit: one halving.
        ConcurrencyLimit limit = new ConcurrencyLimit(64, TimeProvider.System);
        int episode = limit.Episode;
        for (int i = 0; i < 64; i++)
        {
            limit.Throttled(episode);
        }

        Assert.Equal(32, limit.Limit);

        // A request sent under the halved limit and throttled opens the next episode.
        limit.Throttled(limit.Episode);
        Assert.Equal(16, limit.Limit);
    }

    [Fact]
    public async Task ACallerWhoGivesUpOnTheRegionDoesNotGiveUpForTheOthers()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using S3Emulator emulator = new S3Emulator();
        emulator.Seed(Bucket, "a", [1, 2, 3]);
        TaskCompletionSource answered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        emulator.Hold = (request, _, token) => request.Method == HttpMethod.Head && request.RequestUri!.AbsolutePath.Length <= 1
            ? answered.Task.WaitAsync(token)
            : Task.CompletedTask;
        await using S3ObjectStore store = S3ReadTests.Store(emulator, region: null);

        using CancellationTokenSource first = new CancellationTokenSource();
        Task<ObjectRead> givenUp = store.ReadAsync("a", 0, new byte[3], null, first.Token).AsTask();
        Task<ObjectRead> kept = store.ReadAsync("a", 0, new byte[3], null, ct).AsTask();
        await first.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => givenUp);

        answered.SetResult();
        Assert.Equal(3, (await kept).BytesRead);
    }

    [Fact]
    public async Task APutGivenUpStopsItsPartsAtOnceAndAbortsItsUploadWhateverItsToken()
    {
        // Every part hangs until its request is cancelled: the put's token, cancelled, stops them, and
        // the upload is aborted under the abort's own deadline although that token is gone.
        CancellationToken ct = TestContext.Current.CancellationToken;
        using S3Emulator emulator = new S3Emulator { MinimumPartSize = 64 << 10 };
        TaskCompletionSource parked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        emulator.Hold = async (request, _, token) =>
        {
            if (request.RequestUri!.Query.Contains("partNumber", StringComparison.Ordinal))
            {
                parked.TrySetResult();
                await Task.Delay(Timeout.Infinite, token);
            }
        };
        await using S3ObjectStore store = Store(emulator, new StoreTransportOptions
        {
            RetryBaseDelay = TimeSpan.Zero,
            FirstByteTimeout = TimeSpan.FromMinutes(5),
        });

        // Room for the whole megabyte, so that the write does not wait for the parts that never end.
        Pipe pipe = new Pipe(new PipeOptions(pauseWriterThreshold: 8 << 20, resumeWriterThreshold: 4 << 20));
        using CancellationTokenSource giveUp = new CancellationTokenSource();
        Task<PutOutcome> put = store.PutAsync("files/given-up", pipe.Reader, -1, PutCondition.None, giveUp.Token).AsTask();
        await pipe.Writer.WriteAsync(new byte[1 << 20], ct);
        await parked.Task.WaitAsync(ct);
        Stopwatch watch = Stopwatch.StartNew();
        await giveUp.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => put);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"the put took {watch.Elapsed} to give up");
        Assert.Equal(0, emulator.OpenUploads);
        await pipe.Writer.CompleteAsync();
    }

    [Fact]
    public async Task AFileWriterGivenUpCancelsItsUploadAtOnce()
    {
        // The parts of a file written to a location hang until their requests are cancelled: disposing
        // the writer without completing it cancels them, rather than waiting out their deadlines.
        CancellationToken ct = TestContext.Current.CancellationToken;
        using S3Emulator emulator = new S3Emulator { MinimumPartSize = 64 << 10 };
        emulator.Hold = async (request, _, token) =>
        {
            if (request.RequestUri!.Query.Contains("partNumber", StringComparison.Ordinal))
            {
                await Task.Delay(Timeout.Infinite, token);
            }
        };
        await using S3ObjectStore lake = Store(emulator, new StoreTransportOptions { RetryBaseDelay = TimeSpan.Zero, FirstByteTimeout = TimeSpan.FromMinutes(5) });
        await using VortexSession session = VortexSession.Create(o => o.ObjectStores.Add("s3://lake", lake));
        VortexFileWriter writer = session.CreateWriter<LocationTests.Reading>("s3://lake/out/given-up.vortex");
        Stopwatch waited = Stopwatch.StartNew();
        for (int day = 0; emulator.OpenUploads == 0; day++)
        {
            Assert.True(waited.Elapsed < TimeSpan.FromSeconds(30), "no upload started");
            await writer.WriteAsync<LocationTests.Reading>([.. Enumerable.Range(0, 20_000).Select(i => new LocationTests.Reading(day, (i * 7919 % 4000) / 100.0))], ct);
            await writer.FlushAsync(ct);
        }

        Stopwatch watch = Stopwatch.StartNew();
        await writer.DisposeAsync();
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"the writer took {watch.Elapsed} to give its upload up");

        // The abort goes on its own, so that the caller is not held by it: under the load of the whole
        // suite it may land after the disposal returned, within its deadline.
        while (emulator.OpenUploads > 0 && watch.Elapsed < TimeSpan.FromSeconds(10))
        {
            await Task.Delay(10, ct);
        }

        Assert.Equal(0, emulator.OpenUploads);
        Assert.Null(emulator.BytesOf(Bucket, "out/given-up.vortex"));
    }

    [Fact]
    public async Task AnAbortThatHangsIsLeftAfterItsOwnDeadline()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using S3Emulator emulator = new S3Emulator { MinimumPartSize = 64 << 10 };
        emulator.Intercept = (request, _) => request.RequestUri!.Query.Contains("partNumber", StringComparison.Ordinal)
            ? S3Emulator.Error(HttpStatusCode.Forbidden, "AccessDenied", "Parts are refused.")
            : null;
        emulator.Hold = (request, _, token) => request.Method == HttpMethod.Delete
            ? Task.Delay(Timeout.Infinite, token)
            : Task.CompletedTask;
        await using S3ObjectStore store = Store(emulator, new StoreTransportOptions
        {
            RetryBaseDelay = TimeSpan.Zero,
            FirstByteTimeout = TimeSpan.FromMinutes(5),
            AbortTimeout = TimeSpan.FromMilliseconds(300),
        });

        Stopwatch watch = Stopwatch.StartNew();
        await Assert.ThrowsAsync<ObjectStoreException>(async () => await store.PutIfAbsentAsync("files/refused", new byte[1 << 20], ct));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"the abort held the failure for {watch.Elapsed}");
        Assert.Equal(1, emulator.OpenUploads);
    }

    /// <summary>A store whose parts are small past <paramref name="threshold"/>, 256 KiB unless a test sends a whole megabyte.</summary>
    private static S3ObjectStore Store(S3Emulator emulator, StoreTransportOptions? transport = null, long threshold = 256 << 10) =>
        new S3ObjectStore(new S3StoreOptions
        {
            Bucket = Bucket,
            Region = "eu-west-3",
            Credentials = StoreCredentials.AccessKey(S3Emulator.AccessKey, S3Emulator.Secret),
            Handler = emulator,
            PartThreshold = threshold,
            PartSize = 64 << 10,
            MinimumPartSize = 64 << 10,
            Transport = transport ?? new StoreTransportOptions { RetryBaseDelay = TimeSpan.Zero },
        });

    /// <summary>A connection that takes <paramref name="taken"/> bytes, then nothing until its write is cancelled.</summary>
    private sealed class StallingStream(int taken) : WriteOnlyStream
    {
        private long _taken;

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_taken + buffer.Length > taken)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            _taken += buffer.Length;
        }
    }

    /// <summary>A slow connection: each write takes <paramref name="pause"/>.</summary>
    private sealed class SlowStream(TimeSpan pause) : WriteOnlyStream
    {
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            await Task.Delay(pause, cancellationToken);
    }

    private abstract class WriteOnlyStream : Stream
    {
        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
