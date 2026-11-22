using System;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Execution;
using Xunit;

namespace Vorticity.Tests.Execution;

public sealed class HeldLaneTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// A flow that holds the only lane of a budget and waits for what computes nothing gives the lane back meanwhile: another
    /// work takes it, and the flow, its wait done, takes it again once that work gives it back, holding none while it waits.
    /// </summary>
    [Fact]
    public async Task AFlowGivesItsLaneBackWhileItWaitsAndTakesItAgainAfter()
    {
        LaneBudget budget = new LaneBudget(1);
        HeldLane held = new HeldLane(budget, await budget.EnterAsync(Ct));
        Assert.False(budget.TryEnter(out _));

        TaskCompletionSource<int> store = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        ValueTask<int> waited = held.WhileAsync(new ValueTask<int>(store.Task));
        Assert.Equal(0, budget.Held);
        Assert.True(budget.TryEnter(out long other));

        // Its wait done, the flow waits for the lane the other work holds.
        store.SetResult(42);
        await Task.Delay(50, Ct);
        Assert.False(waited.IsCompleted);
        budget.Exit(other);
        Assert.Equal(42, await waited.AsTask().WaitAsync(TimeSpan.FromSeconds(30), Ct));
        Assert.Equal(1, budget.Held);
        Assert.Equal(1, held.GivenBack);

        held.Exit();
        Assert.Equal(0, budget.Held);
    }

    /// <summary>A wait already done keeps the lane: nothing is given back.</summary>
    [Fact]
    public async Task AWaitAlreadyDoneKeepsTheLane()
    {
        LaneBudget budget = new LaneBudget(1);
        HeldLane held = new HeldLane(budget, await budget.EnterAsync(Ct));
        Assert.Equal(7, await held.WhileAsync(new ValueTask<int>(7)));
        Assert.Equal(1, budget.Held);
        Assert.Equal(0, held.GivenBack);
        held.Exit();
    }
}
