// The process's budget of requests over a store: eight questions at once never pass its requests in flight nor its
// requests a second, and a service that throttles past a rate sees the budget's rate halve under it, every read ending.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.IO;
using Vorticity.ObjectStorage;
using Xunit;

namespace Vorticity.Tests.ObjectStorage;

public sealed class RequestBudgetTests
{
    private const string Bucket = "lake";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// Eight readers of 60 reads each, a millisecond a read at the service, under a budget of 4 in flight and 400 a second:
    /// the service never sees more than 4 at once, nor more in any quarter of a second than the quarter's rate and the
    /// bucket's tenth of a second.
    /// </summary>
    [Fact]
    public async Task EightQuestionsAtOnceKeepWithinTheBudget()
    {
        using S3Emulator emulator = new S3Emulator();
        Seed(emulator);
        int inFlight = 0;
        int most = 0;
        ConcurrentQueue<long> sent = new ConcurrentQueue<long>();
        emulator.Hold = async (request, _, token) =>
        {
            sent.Enqueue(Stopwatch.GetTimestamp());
            int now = Interlocked.Increment(ref inFlight);
            InterlockedMax(ref most, now);
            await Task.Delay(1, token);
            Interlocked.Decrement(ref inFlight);
        };

        RequestBudget budget = new RequestBudget(maxInFlight: 4, requestsPerSecond: 400);
        await using S3ObjectStore store = Store(emulator, budget);
        long begin = Stopwatch.GetTimestamp();
        await Task.WhenAll(Enumerable.Range(0, 8).Select(reader => ReadAsync(store, reader, reads: 60)));
        double seconds = Stopwatch.GetElapsedTime(begin).TotalSeconds;

        Assert.InRange(most, 1, 4);
        Assert.Equal(480, sent.Count);
        Assert.True(seconds >= (480 - 40) / 400.0 * 0.95, $"480 reads in {seconds:F2} s, past 400 a second");
        Assert.True(MostIn(sent, TimeSpan.FromMilliseconds(250)) <= (400 * 0.25) + 40 + 2, $"{MostIn(sent, TimeSpan.FromMilliseconds(250))} reads in a quarter of a second");
    }

    /// <summary>
    /// A service that throttles every request past 150 a second over its last 200 ms: under a budget of 1 600 a second the
    /// store's rate halves until the service stops throttling, then grows and halves around what the service sustains; every
    /// read ends, the throttles stay a few dozen, and the store's rate ends within twice the service's.
    /// </summary>
    [Fact]
    public async Task AServiceThatThrottlesPastARateIsReadUnderIt()
    {
        using S3Emulator emulator = new S3Emulator();
        Seed(emulator);
        object gate = new object();
        Queue<long> recent = new Queue<long>();
        ConcurrentQueue<long> answered = new ConcurrentQueue<long>();
        long window = Stopwatch.Frequency / 5;
        int throttled = 0;
        emulator.Intercept = (request, _) =>
        {
            long now = Stopwatch.GetTimestamp();
            lock (gate)
            {
                while (recent.Count > 0 && now - recent.Peek() > window)
                {
                    recent.Dequeue();
                }

                if (recent.Count >= 30)
                {
                    Interlocked.Increment(ref throttled);
                    return S3Emulator.Error(HttpStatusCode.ServiceUnavailable, "SlowDown", "Please reduce your request rate.");
                }

                recent.Enqueue(now);
            }

            answered.Enqueue(now);
            return null;
        };

        RequestBudget budget = new RequestBudget(maxInFlight: 16, requestsPerSecond: 1_600);
        await using S3ObjectStore store = Store(emulator, budget, retryDelay: TimeSpan.FromMilliseconds(20));
        await Task.WhenAll(Enumerable.Range(0, 8).Select(reader => ReadAsync(store, reader, reads: 50)));

        Assert.InRange(throttled, 1, 80);
        Assert.True(store.Transport.RequestRate <= 2 * 150, $"the store sends {store.Transport.RequestRate} a second after the throttles");
        Assert.Equal(RequestBudget.DefaultRequestsPerSecond, RequestBudget.Process.RequestsPerSecond);
        Assert.Equal(400, answered.Count);
    }

    /// <summary>Reads <paramref name="reads"/> of the seeded objects, one after the other.</summary>
    private static async Task ReadAsync(S3ObjectStore store, int reader, int reads)
    {
        byte[] into = new byte[16];
        for (int i = 0; i < reads; i++)
        {
            ObjectRead read = await store.ReadAsync($"o/{(reader * 7 + i) % 16}", 0, into, null, Ct);
            Assert.Equal(16, read.BytesRead);
        }
    }

    private static void Seed(S3Emulator emulator)
    {
        for (int i = 0; i < 16; i++)
        {
            emulator.Seed(Bucket, $"o/{i}", [.. Enumerable.Range(0, 64).Select(b => (byte)(b + i))]);
        }
    }

    /// <summary>The most of <paramref name="times"/> that fall in any span of <paramref name="span"/>.</summary>
    private static int MostIn(IEnumerable<long> times, TimeSpan span)
    {
        long[] sorted = [.. times];
        Array.Sort(sorted);
        long width = (long)(span.TotalSeconds * Stopwatch.Frequency);
        int most = 0;
        for (int first = 0, last = 0; last < sorted.Length; last++)
        {
            while (sorted[last] - sorted[first] >= width)
            {
                first++;
            }

            most = Math.Max(most, last - first + 1);
        }

        return most;
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int seen = Volatile.Read(ref target);
        while (value > seen)
        {
            int was = Interlocked.CompareExchange(ref target, value, seen);
            if (was == seen)
            {
                return;
            }

            seen = was;
        }
    }

    private static S3ObjectStore Store(S3Emulator emulator, RequestBudget budget, TimeSpan? retryDelay = null) =>
        new S3ObjectStore(new S3StoreOptions
        {
            Bucket = Bucket,
            Region = "eu-west-3",
            Credentials = StoreCredentials.AccessKey(S3Emulator.AccessKey, S3Emulator.Secret),
            Handler = emulator,
            Transport = new StoreTransportOptions { RetryBaseDelay = retryDelay ?? TimeSpan.Zero, RequestBudget = budget, MaxAttempts = 10 },
        });
}
