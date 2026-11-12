using System;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Parquet.Codecs;
using Xunit;

namespace Vorticity.Tests.Parquet;

/// <summary>
/// A page's codec on a lane: the column that comes to a page its lane still runs never waits on a thread for it. It awaits
/// the lane, or is called back when the lane finishes; a page it gives up is given back by the lane as it finishes; a
/// page no lane has begun runs where the column comes to it.
/// </summary>
public sealed class LaneWorkTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void APageNoLaneHasBegunRunsWhereTheColumnComesToIt()
    {
        Held work = new Held(new PageLanes(2));
        work.Release.Set();
        Assert.True(work.TryComplete());
        Assert.True(work.Done);
        Assert.Equal(Environment.CurrentManagedThreadId, work.RanOn);

        // The lane that comes to it later finds it claimed and does nothing.
        Assert.False(work.TryRun());
        Assert.Equal(1, work.Runs);
    }

    [Fact]
    public async Task AColumnAwaitsThePageItsLaneRuns()
    {
        Held work = new Held(new PageLanes(2));
        Task<bool> lane = Task.Run(work.TryRun, Ct);
        await work.Started.Task.WaitAsync(Ct);

        // The lane holds it: the column cannot take it, and awaits it.
        Assert.False(work.TryComplete());
        Assert.True(work.Running);
        Task joined = work.JoinAsync().AsTask();
        Assert.False(joined.IsCompleted);

        work.Release.Set();
        await joined.WaitAsync(Ct);
        Assert.True(work.Done);
        Assert.True(await lane);
        Assert.Equal(1, work.Runs);
        Assert.Equal(0, work.Discards);
    }

    [Fact]
    public async Task AColumnIsCalledBackOnceItsLaneFinishes()
    {
        Held work = new Held(new PageLanes(2));
        Task<bool> lane = Task.Run(work.TryRun, Ct);
        await work.Started.Task.WaitAsync(Ct);

        // Called back on the lane's thread, which has the page in its cache, before it goes on.
        TaskCompletionSource<int> resumed = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(work.OnFinished(() => resumed.SetResult(Environment.CurrentManagedThreadId)));
        Assert.False(resumed.Task.IsCompleted);

        work.Release.Set();
        Assert.Equal(work.RanOn, await resumed.Task.WaitAsync(Ct));
        Assert.True(await lane);

        // Done: no call back, nothing to wait for.
        Assert.False(work.OnFinished(() => throw new InvalidOperationException("Called back for a page already done.")));
    }

    [Fact]
    public async Task APageGivenUpWhileItsLaneRunsIsGivenBackByTheLane()
    {
        Held work = new Held(new PageLanes(2));
        Task<bool> lane = Task.Run(work.TryRun, Ct);
        await work.Started.Task.WaitAsync(Ct);

        // Given up mid-run: the column gives nothing back, the lane does as it finishes, and an await of it then completes.
        Assert.False(work.Abandon());
        Task joined = work.JoinAsync().AsTask();
        Assert.Equal(0, work.Discards);
        work.Release.Set();
        await joined.WaitAsync(Ct);
        Assert.True(await lane);
        Assert.Equal(1, work.Discards);
        Assert.Equal(work.RanOn, work.DiscardedOn);
    }

    [Fact]
    public void APageGivenUpBeforeOrAfterItsLaneIsTheColumnsToGiveBack()
    {
        // No lane has begun it: never run, the column's to give back.
        Held never = new Held(new PageLanes(2));
        Assert.True(never.Abandon());
        Assert.False(never.TryRun());
        Assert.Equal(0, never.Runs);
        Assert.Equal(0, never.Discards);

        // Its lane finished it: the column's too.
        Held finished = new Held(new PageLanes(2));
        finished.Release.Set();
        Assert.True(finished.TryRun());
        Assert.True(finished.Abandon());
        Assert.Equal(0, finished.Discards);
    }

    /// <summary>A work that holds its lane until the test lets it go, and counts what was done with it.</summary>
    private sealed class Held(PageLanes lanes) : LaneWork(lanes)
    {
        private int _runs;
        private int _discards;

        internal TaskCompletionSource Started { get; } = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        internal ManualResetEventSlim Release { get; } = new ManualResetEventSlim(false);

        internal int Runs => Volatile.Read(ref _runs);

        internal int Discards => Volatile.Read(ref _discards);

        internal int RanOn { get; private set; }

        internal int DiscardedOn { get; private set; }

        protected override void Run()
        {
            Interlocked.Increment(ref _runs);
            RanOn = Environment.CurrentManagedThreadId;
            Started.TrySetResult();
            Release.Wait(Ct);
        }

        protected override void Discard()
        {
            DiscardedOn = Environment.CurrentManagedThreadId;
            Interlocked.Increment(ref _discards);
        }
    }
}
