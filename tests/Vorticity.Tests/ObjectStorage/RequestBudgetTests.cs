// The process's budget of requests over a store: a burst then a pace, a bound in flight, and a store's own rate that a
// throttle halves once per episode, down to a floor, and that grows back once its service is calm. The budget's rule runs
// on a clock the test moves; the stores are checked on a few requests for what they wire to it.
using System;
using System.Collections.Concurrent;
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
    /// 100 requests a second: a tenth of a second's requests go at once, the next one waits for its turn, a hundredth of a
    /// second, and not a tick less.
    /// </summary>
    [Fact]
    public async Task ABudgetLetsABurstThroughThenPacesEachRequest()
    {
        StepClock clock = new StepClock();
        RequestBudget budget = new RequestBudget(maxInFlight: 1_000, requestsPerSecond: 100, clock);
        for (int burst = 0; burst < 10; burst++)
        {
            Assert.True(budget.EnterAsync(Ct).IsCompletedSuccessfully, $"request {burst} of the burst waited");
        }

        Task next = budget.EnterAsync(Ct).AsTask();
        clock.Advance(TimeSpan.FromMilliseconds(10) - TimeSpan.FromTicks(1));
        Assert.False(next.IsCompleted, "a request went before its turn");
        clock.Advance(TimeSpan.FromTicks(1));
        await next.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        // Another second: a hundred turns, and no more.
        Task[] second = [.. Enumerable.Range(0, 101).Select(_ => budget.EnterAsync(Ct).AsTask())];
        for (int step = 0; step < 100; step++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(10));
        }

        await Task.WhenAll(second.Take(100)).WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.False(second[100].IsCompleted, "a second gave more than its hundred turns");
        clock.Advance(TimeSpan.FromMilliseconds(10));
        await second[100].WaitAsync(TimeSpan.FromSeconds(10), Ct);
    }

    /// <summary>
    /// Past the burst, the requests that wait are served in the order they came, each at its own turn's time, a hundredth of
    /// a second apart, none before.
    /// </summary>
    [Fact]
    public async Task RequestsThatWaitAreServedInTheOrderTheyCame()
    {
        StepClock clock = new StepClock();
        RequestBudget budget = new RequestBudget(maxInFlight: 1_000, requestsPerSecond: 100, clock);
        for (int burst = 0; burst < 10; burst++)
        {
            await budget.EnterAsync(Ct);
        }

        Task[] waiting = [.. Enumerable.Range(0, 4).Select(_ => budget.EnterAsync(Ct).AsTask())];
        for (int turn = 0; turn < waiting.Length; turn++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(10));
            await waiting[turn].WaitAsync(TimeSpan.FromSeconds(10), Ct);
            Assert.All(waiting[(turn + 1)..], later => Assert.False(later.IsCompleted, $"a request went at the turn of request {turn}"));
        }
    }

    /// <summary>Two in flight: a third waits until one of them leaves.</summary>
    [Fact]
    public async Task ABudgetHoldsItsRequestsInFlight()
    {
        RequestBudget budget = new RequestBudget(maxInFlight: 2, requestsPerSecond: 1_000_000, new StepClock());
        Assert.True(budget.EnterAsync(Ct).IsCompletedSuccessfully);
        Assert.True(budget.EnterAsync(Ct).IsCompletedSuccessfully);
        Task third = budget.EnterAsync(Ct).AsTask();
        Assert.False(third.IsCompleted, "a third request went while two were in flight");
        budget.Exit();
        await third.WaitAsync(TimeSpan.FromSeconds(10), Ct);
    }

    /// <summary>
    /// A store's rate of 1 600 a second: a throttle halves it, and its bucket empties; a throttle of a request that entered
    /// before that halving, the same episode, leaves it; each new episode halves it again, down to a sixty-fourth.
    /// </summary>
    [Fact]
    public void AThrottleHalvesAStoresRateOncePerEpisodeDownToItsFloor()
    {
        StepClock clock = new StepClock();
        RequestBudget store = RequestBudget.ForStore(1_600, clock);
        int entered = store.Episode;
        store.Throttled(entered);
        Assert.Equal(800, store.CurrentRequestsPerSecond);
        Assert.False(store.EnterAsync(Ct).IsCompleted, "the bucket kept its turns past the throttle");

        store.Throttled(entered);
        Assert.Equal(800, store.CurrentRequestsPerSecond);

        for (int episode = 0; episode < 10; episode++)
        {
            store.Throttled(store.Episode);
        }

        Assert.Equal(1_600.0 / 64, store.CurrentRequestsPerSecond);
    }

    /// <summary>
    /// After a throttle, a store's rate stays down for a second of calm, then grows back by a sixteenth of its ceiling a
    /// second, up to the ceiling.
    /// </summary>
    [Fact]
    public void AStoresRateGrowsBackOnceItsServiceIsCalm()
    {
        StepClock clock = new StepClock();
        RequestBudget store = RequestBudget.ForStore(1_600, clock);
        store.Throttled(store.Episode);
        Assert.Equal(800, store.CurrentRequestsPerSecond);

        // Calm for a second, then a second more before the first step.
        clock.Advance(TimeSpan.FromMilliseconds(1_999));
        store.Succeeded();
        Assert.Equal(800, store.CurrentRequestsPerSecond);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        store.Succeeded();
        Assert.Equal(900, store.CurrentRequestsPerSecond);

        // A step a second, whatever the successes between.
        clock.Advance(TimeSpan.FromMilliseconds(500));
        store.Succeeded();
        Assert.Equal(900, store.CurrentRequestsPerSecond);
        for (int second = 0; second < 20; second++)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            store.Succeeded();
        }

        Assert.Equal(1_600, store.CurrentRequestsPerSecond);
    }

    /// <summary>A budget the stores share does not move when one of their services throttles: the other stores' may not.</summary>
    [Fact]
    public void ASharedBudgetDoesNotMoveWhenAServiceThrottles()
    {
        RequestBudget shared = new RequestBudget(maxInFlight: 16, requestsPerSecond: 1_600, new StepClock());
        shared.Throttled(shared.Episode);
        Assert.Equal(1_600, shared.CurrentRequestsPerSecond);
    }

    /// <summary>
    /// Eight readers at once through a store under a budget of four in flight, each read held a millisecond at the service:
    /// the service never sees more than four at once.
    /// </summary>
    [Fact]
    public async Task AStoreSendsWithinItsBudgetsRequestsInFlight()
    {
        using S3Emulator emulator = new S3Emulator();
        Seed(emulator);
        int inFlight = 0;
        int most = 0;
        int sent = 0;
        emulator.Hold = async (request, _, token) =>
        {
            Interlocked.Increment(ref sent);
            InterlockedMax(ref most, Interlocked.Increment(ref inFlight));
            await Task.Delay(1, token);
            Interlocked.Decrement(ref inFlight);
        };

        await using S3ObjectStore store = Store(emulator, new RequestBudget(maxInFlight: 4, requestsPerSecond: 1_000_000));
        await Task.WhenAll(Enumerable.Range(0, 8).Select(reader => ReadAsync(store, reader, reads: 6)));

        Assert.InRange(most, 1, 4);
        Assert.Equal(48, sent);
    }

    /// <summary>
    /// A service that throttles its first requests: every read ends once it answers, the store's own rate halved, the
    /// budget the stores share and the process's left as they were.
    /// </summary>
    [Fact]
    public async Task AStoreWhoseServiceThrottlesHalvesItsOwnRateAndEndsEveryRead()
    {
        using S3Emulator emulator = new S3Emulator();
        Seed(emulator);
        int throttled = 0;
        ConcurrentQueue<int> answered = new ConcurrentQueue<int>();
        emulator.Intercept = (request, _) =>
        {
            if (Interlocked.Increment(ref throttled) <= 4)
            {
                return S3Emulator.Error(HttpStatusCode.ServiceUnavailable, "SlowDown", "Please reduce your request rate.");
            }

            answered.Enqueue(0);
            return null;
        };

        // A rate no read waits for once halved: the pace is the budget's own test's, the halving this one's.
        RequestBudget budget = new RequestBudget(maxInFlight: 16, requestsPerSecond: 1_000_000);
        await using S3ObjectStore store = Store(emulator, budget);
        await Task.WhenAll(Enumerable.Range(0, 8).Select(reader => ReadAsync(store, reader, reads: 5)));

        Assert.Equal(40, answered.Count);
        Assert.True(store.Transport.RequestRate < 1_000_000, $"the store still sends {store.Transport.RequestRate} a second");
        Assert.Equal(1_000_000, budget.CurrentRequestsPerSecond);
        Assert.Equal(RequestBudget.DefaultRequestsPerSecond, RequestBudget.Process.RequestsPerSecond);
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

    private static S3ObjectStore Store(S3Emulator emulator, RequestBudget budget) =>
        new S3ObjectStore(new S3StoreOptions
        {
            Bucket = Bucket,
            Region = "eu-west-3",
            Credentials = StoreCredentials.AccessKey(S3Emulator.AccessKey, S3Emulator.Secret),
            Handler = emulator,
            Transport = new StoreTransportOptions { RetryBaseDelay = TimeSpan.Zero, RequestBudget = budget, MaxAttempts = 10 },
        });
}
