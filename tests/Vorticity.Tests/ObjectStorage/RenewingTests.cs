// A credential renewed before it expires: in the background within the margin, a pause after a failed renewal, and a
// request's cancellation ending its own wait alone.
//
// THE ORACLE IS THE SOURCE'S CALLS: how many renewals it was asked for, and whether a request waited for one.
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.ObjectStorage.Credentials;
using Vorticity.Tests.Dataset;
using Xunit;

namespace Vorticity.Tests.ObjectStorage;

public sealed class RenewingTests
{
    private static readonly DateTimeOffset Start = new DateTimeOffset(2026, 11, 24, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// Within five minutes of its expiry, a credential still valid serves every request at once while a renewal the
    /// service does not answer goes on behind them: a request renewed it itself before, and waited for the service.
    /// </summary>
    [Fact]
    public async Task ACredentialInItsMarginServesAtOnceWhileItIsRenewedBehind()
    {
        ManualClock clock = new ManualClock(Start);
        Source source = new Source(clock);
        Renewing<Credential> renewing = new Renewing<Credential>(source.TakeAsync, static c => c.ExpiresOn, clock);
        Assert.Equal("1", (await renewing.GetAsync(Ct)).Value);

        clock.Advance(TimeSpan.FromMinutes(56));
        TaskCompletionSource answer = source.Hold();
        for (int request = 0; request < 3; request++)
        {
            Assert.Equal("1", (await renewing.GetAsync(Ct).AsTask().WaitAsync(TimeSpan.FromSeconds(30), Ct)).Value);
        }

        Assert.Equal(2, source.Calls);
        answer.SetResult();
        await source.Answered.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        await WaitUntilAsync(async () => (await renewing.GetAsync(Ct)).Value == "2");
        Assert.Equal(2, source.Calls);
    }

    /// <summary>
    /// A renewal that fails in the margin is not tried again before 30 seconds, then is; a credential expired meanwhile is
    /// renewed at once, its request waiting, pause or not.
    /// </summary>
    [Fact]
    public async Task ARenewalThatFailsInTheMarginPausesBeforeTheNext()
    {
        ManualClock clock = new ManualClock(Start);
        Source source = new Source(clock);
        Renewing<Credential> renewing = new Renewing<Credential>(source.TakeAsync, static c => c.ExpiresOn, clock);
        await renewing.GetAsync(Ct);

        clock.Advance(TimeSpan.FromMinutes(56));
        source.Failing = true;
        await renewing.GetAsync(Ct);
        await WaitUntilAsync(() => Task.FromResult(source.Failures == 1));
        for (int request = 0; request < 5; request++)
        {
            Assert.Equal("1", (await renewing.GetAsync(Ct)).Value);
        }

        Assert.Equal(2, source.Calls);
        clock.Advance(TimeSpan.FromSeconds(31));
        await renewing.GetAsync(Ct);
        await WaitUntilAsync(() => Task.FromResult(source.Failures == 2));
        Assert.Equal(3, source.Calls);

        source.Failing = false;
        clock.Advance(TimeSpan.FromMinutes(4));
        Assert.Equal("4", (await renewing.GetAsync(Ct)).Value);
    }

    /// <summary>
    /// A request that waits for a renewal and is cancelled ends its wait alone: the renewal goes on, and the next request
    /// takes what it brings without asking the source again.
    /// </summary>
    [Fact]
    public async Task ACancelledRequestLeavesTheRenewalToTheOthers()
    {
        ManualClock clock = new ManualClock(Start);
        Source source = new Source(clock);
        Renewing<Credential> renewing = new Renewing<Credential>(source.TakeAsync, static c => c.ExpiresOn, clock);
        TaskCompletionSource answer = source.Hold();

        using CancellationTokenSource cancelled = new CancellationTokenSource();
        Task<Credential> first = renewing.GetAsync(cancelled.Token).AsTask();
        Task<Credential> second = renewing.GetAsync(Ct).AsTask();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);

        answer.SetResult();
        Assert.Equal("1", (await second.WaitAsync(TimeSpan.FromSeconds(30), Ct)).Value);
        Assert.Equal(1, source.Calls);
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        for (int i = 0; i < 3_000 && !await condition(); i++)
        {
            await Task.Delay(10, Ct);
        }

        Assert.True(await condition());
    }

    private sealed record Credential(string Value, DateTimeOffset ExpiresOn);

    /// <summary>Credentials valid an hour, numbered by the call; held until the test answers, or failing.</summary>
    private sealed class Source(ManualClock clock)
    {
        private readonly Lock _gate = new Lock();
        private TaskCompletionSource? _held;
        private int _calls;
        private int _failures;

        internal int Calls => Volatile.Read(ref _calls);

        internal int Failures => Volatile.Read(ref _failures);

        internal bool Failing { get; set; }

        internal Task Answered => _answered.Task;

        private readonly TaskCompletionSource _answered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource Hold()
        {
            lock (_gate)
            {
                return _held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        internal async ValueTask<Credential> TakeAsync(CancellationToken cancellationToken)
        {
            int call = Interlocked.Increment(ref _calls);
            TaskCompletionSource? held;
            lock (_gate)
            {
                held = _held;
                _held = null;
            }

            if (held is not null)
            {
                await held.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                _answered.TrySetResult();
            }

            if (Failing)
            {
                Interlocked.Increment(ref _failures);
                throw new InvalidOperationException("The identity service did not answer.");
            }

            return new Credential(call.ToString(System.Globalization.CultureInfo.InvariantCulture), clock.GetUtcNow() + TimeSpan.FromHours(1));
        }
    }
}
