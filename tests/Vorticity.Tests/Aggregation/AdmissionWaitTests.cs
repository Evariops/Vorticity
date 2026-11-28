// A question's admission (AggregationEngine.AdmitAsync): a question that holds nothing waits for its first lane's working
// memory when its budget has no room, in turn behind the reservations that wait already, and starts once the work holding
// the room gives it back; a question that holds memory never waits, and fails as before; a lane that can never be granted
// fails at once.
using System;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Aggregating;
using Xunit;

namespace Vorticity.Tests.Aggregation;

public sealed class AdmissionWaitTests
{
    private const long Mib = 1L << 20;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A budget of its own ceiling under the process's, as a host's is.</summary>
    private static QueryMemoryBudget Budget(long ceiling) => new QueryMemoryBudget(ceiling, QueryMemoryBudget.Process);

    [Fact]
    public async Task AQuestionThatHoldsNothingWaitsForTheRoomAnotherGivesBack()
    {
        QueryMemoryBudget budget = Budget(3 * Mib / 2);
        QueryMemory holder = new QueryMemory(budget);
        Assert.True(holder.TryGrow(Mib));
        using QueryMemory waiting = new QueryMemory(budget);
        Assert.True(waiting.HoldsNothing);

        Task<int> admitted = AggregationEngine.AdmitAsync(waiting, 4, 0, Ct).AsTask();
        Assert.False(admitted.IsCompleted);
        holder.Dispose();

        Assert.Equal(1, await admitted.WaitAsync(TimeSpan.FromSeconds(10), Ct));
        Assert.InRange(waiting.Held, 1, Mib);
        Assert.Equal(waiting.Held, waiting.Measured);
        Assert.Equal(waiting.Held, budget.ReservedBytes);
        waiting.Dispose();
        Assert.Equal(0, budget.ReservedBytes);
    }

    [Fact]
    public async Task AQuestionThatHoldsMemoryFailsRatherThanWait()
    {
        QueryMemoryBudget budget = Budget(3 * Mib / 2);
        using QueryMemory holder = new QueryMemory(budget);
        Assert.True(holder.TryGrow(Mib));
        using QueryMemory holding = new QueryMemory(budget);
        Assert.True(holding.TryGrow(4096));
        Assert.False(holding.HoldsNothing);

        await Assert.ThrowsAsync<VortexMemoryException>(async () => await AggregationEngine.AdmitAsync(holding, 1, 0, Ct));
        Assert.Equal(4096, holding.Held);
    }

    [Fact]
    public async Task ALaneNoBudgetCouldGrantFailsAtOnce()
    {
        QueryMemoryBudget budget = Budget(Mib / 2);
        using QueryMemory memory = new QueryMemory(budget);
        ValueTask<int> admitting = AggregationEngine.AdmitAsync(memory, 1, 0, Ct);
        Assert.True(admitting.IsCompleted);
        await Assert.ThrowsAsync<VortexMemoryException>(async () => await admitting);
        Assert.Equal(0, budget.ReservedBytes);
    }

    [Fact]
    public async Task AQuestionAdmittedAtOnceWaitsForNothing()
    {
        QueryMemoryBudget budget = Budget(64 * Mib);
        using QueryMemory memory = new QueryMemory(budget);
        ValueTask<int> admitting = AggregationEngine.AdmitAsync(memory, 4, 0, Ct);
        Assert.True(admitting.IsCompletedSuccessfully);
        Assert.Equal(4, await admitting);
        Assert.Equal(memory.Held, memory.Measured);
    }
}
