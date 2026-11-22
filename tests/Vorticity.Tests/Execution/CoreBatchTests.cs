using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Execution;
using Xunit;

namespace Vorticity.Tests.Execution;

public sealed class CoreBatchTests
{
    private const int Writers = 16;

    private const int Pieces = 200;

    private const int Items = 8;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// Writers spreading pieces at once over one core of three lanes, from threads of the pool, more of them than the
    /// lanes: every item runs once, the writers end, and once they have every lane comes back, those of the pumps that
    /// claimed a piece as it closed included, which no writer waited for.
    /// </summary>
    [Fact]
    public async Task PiecesSpreadAtOnceRunEveryItemOnceAndGiveEveryLaneBack()
    {
        LaneBudget budget = new LaneBudget(3);
        FlowCore core = new FlowCore(budget);
        int[] runs = new int[Writers * Pieces * Items];
        Task[] writers = [.. Enumerable.Range(0, Writers).Select(writer => Task.Run(
            async () =>
            {
                CoreBatch batch = CoreBatch.Rent(core, 4);
                try
                {
                    for (int piece = 0; piece < Pieces; piece++)
                    {
                        await batch.RunAsync(Counting.Instance, Items, runs, ((writer * Pieces) + piece) * Items);
                    }
                }
                finally
                {
                    CoreBatch.Return(batch);
                }
            },
            Ct))];
        await Task.WhenAll(writers).WaitAsync(TimeSpan.FromSeconds(60), Ct);
        Assert.All(runs, count => Assert.Equal(1, count));
        await AssertEveryLaneBackAsync(budget);
    }

    /// <summary>
    /// Units offered to a core of two lanes, many more than its lanes, each counting how many run at once: every unit runs
    /// once, never more than two at a time, and those its owner takes back before a pump does run on the owner's flow.
    /// </summary>
    [Fact]
    public async Task UnitsRunOnceAndNeverPastTheLanes()
    {
        LaneBudget budget = new LaneBudget(2);
        FlowCore core = new FlowCore(budget);
        Probe[] units = [.. Enumerable.Range(0, 400).Select(_ => new Probe())];
        foreach (Probe unit in units)
        {
            unit.Offer(core, Flow.Write);
        }

        foreach (Probe unit in units)
        {
            await unit.RunOrWaitAsync(core);
        }

        Assert.All(units, unit => Assert.Equal(1, unit.Runs));
        Assert.True(Probe.Most <= 3, $"{Probe.Most} units ran at once on two lanes and the owner's flow");
        await AssertEveryLaneBackAsync(budget);
    }

    /// <summary>A unit that throws rethrows to whoever awaits it, run by a pump or taken back by its owner.</summary>
    [Fact]
    public async Task AUnitThatThrowsRethrowsToItsOwner()
    {
        LaneBudget budget = new LaneBudget(1);
        FlowCore core = new FlowCore(budget);
        Failing unit = new Failing();
        unit.Offer(core, Flow.Read);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await unit.RunOrWaitAsync(core));

        // Held by another work, the only lane leaves the unit to its owner, who runs it and sees it throw.
        Assert.True(budget.TryEnter(out long held));
        unit.Offer(core, Flow.Read);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await unit.RunOrWaitAsync(core));
        budget.Exit(held);
        await AssertEveryLaneBackAsync(budget);
    }

    /// <summary>
    /// A work that waits for a lane, holding none, gets the one a pump gives back at its unit's end, while units are still
    /// queued: the pump does not keep its lane from one unit to the next past a waiter.
    /// </summary>
    [Fact]
    public async Task APumpGivesItsLaneToAWaiterAtAUnitsEnd()
    {
        LaneBudget budget = new LaneBudget(1);
        FlowCore core = new FlowCore(budget);
        using ManualResetEventSlim release = new ManualResetEventSlim(false);
        Gated first = new Gated(release);
        Gated[] after = [.. Enumerable.Range(0, 8).Select(_ => new Gated(null))];
        first.Offer(core, Flow.Write);
        foreach (Gated unit in after)
        {
            unit.Offer(core, Flow.Write);
        }

        Assert.True(first.Started.Wait(TimeSpan.FromSeconds(10), Ct), "no pump ran the first unit");
        ValueTask<long> waiter = budget.EnterAsync(Ct);
        release.Set();
        long entered = await waiter.AsTask().WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.True(core.Yields > 0, "the pump kept its lane past the waiter");
        budget.Exit(entered);
        foreach (Gated unit in after)
        {
            await unit.RunOrWaitAsync(core).AsTask().WaitAsync(TimeSpan.FromSeconds(10), Ct);
        }

        await first.RunOrWaitAsync(core);
        await AssertEveryLaneBackAsync(budget);
    }

    /// <summary>Every lane of <paramref name="budget"/> taken back once, then given back: none is held, none given twice.</summary>
    private static async Task AssertEveryLaneBackAsync(LaneBudget budget)
    {
        int taken = 0;
        Stopwatch waited = Stopwatch.StartNew();
        while (taken < budget.Lanes && waited.Elapsed < TimeSpan.FromSeconds(10))
        {
            if (budget.TryEnter(out _))
            {
                taken++;
            }
            else
            {
                await Task.Delay(10, Ct);
            }
        }

        Assert.Equal(budget.Lanes, taken);
        Assert.False(budget.TryEnter(out _), "a lane was given back twice");
        for (int i = 0; i < taken; i++)
        {
            budget.ExitQuietly(Stopwatch.GetTimestamp());
        }
    }

    /// <summary>Counts each item of a piece in the array the piece reads, from the place it starts at.</summary>
    private sealed class Counting : IBatchWork
    {
        internal static readonly Counting Instance = new Counting();

        public void Run(CoreBatch batch, int item) => Interlocked.Increment(ref ((int[])batch.State!)[batch.Value + item]);
    }

    /// <summary>A unit that counts its runs and the units running beside it.</summary>
    private sealed class Probe : CoreUnit
    {
        private static int s_running;
        private static int s_most;

        internal static int Most => Volatile.Read(ref s_most);

        internal int Runs;

        protected override void Execute()
        {
            int running = Interlocked.Increment(ref s_running);
            int most;
            while (running > (most = Volatile.Read(ref s_most)) && Interlocked.CompareExchange(ref s_most, running, most) != most)
            {
            }

            Thread.SpinWait(2_000);
            Interlocked.Increment(ref Runs);
            Interlocked.Decrement(ref s_running);
        }
    }

    private sealed class Failing : CoreUnit
    {
        protected override void Execute() => throw new InvalidOperationException("failed");
    }

    /// <summary>A unit that says it started, then waits for its gate, if it has one.</summary>
    private sealed class Gated(ManualResetEventSlim? gate) : CoreUnit
    {
        internal ManualResetEventSlim Started { get; } = new ManualResetEventSlim(false);

        protected override void Execute()
        {
            Started.Set();
            gate?.Wait(TimeSpan.FromSeconds(10));
        }
    }
}
