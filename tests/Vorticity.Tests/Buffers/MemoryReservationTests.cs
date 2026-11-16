// A reservation that waits for room (QueryMemoryBudget.ReserveAsync): granted at once when the room is there, in its turn
// once the work under the budget gives memory back, told no at once when nothing would; and a budget a grant funds
// (HardChild(MemoryGrant)), whose reservations take the grant's bytes first.
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Vorticity.Tests.Buffers;

public sealed class MemoryReservationTests
{
    private const long Mib = 1L << 20;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A budget of its own ceiling under the process's, as a host's is.</summary>
    private static QueryMemoryBudget Budget(long ceiling) => new QueryMemoryBudget(ceiling, QueryMemoryBudget.Process);

    [Fact]
    public async Task AReservationThatFitsIsGrantedAtOnce()
    {
        QueryMemoryBudget budget = Budget(Mib);
        ValueTask<MemoryGrant?> reserving = budget.ReserveAsync(Mib / 2, Ct);
        Assert.True(reserving.IsCompletedSuccessfully);
        MemoryGrant grant = (await reserving)!;
        Assert.Equal(Mib / 2, budget.ReservedBytes);
        grant.Release();
        grant.Release();
        Assert.Equal(0, budget.ReservedBytes);
    }

    [Fact]
    public async Task AReservationPastTheCeilingIsToldNoAtOnce()
    {
        QueryMemoryBudget budget = Budget(Mib);
        ValueTask<MemoryGrant?> reserving = budget.ReserveAsync(2 * Mib, Ct);
        Assert.True(reserving.IsCompletedSuccessfully);
        Assert.Null(await reserving);
        Assert.Equal(0, budget.ReservedBytes);
    }

    /// <summary>Another work holds the room: the reservation waits, and takes the room in the step that gives it back.</summary>
    [Fact]
    public async Task AReservationWaitsForTheRoomAnotherGivesBack()
    {
        QueryMemoryBudget budget = Budget(Mib);
        Assert.True(budget.TryReserve(3 * Mib / 4));
        Task<MemoryGrant?> reserving = budget.ReserveAsync(Mib / 2, Ct).AsTask();
        Assert.False(reserving.IsCompleted);
        budget.Release(3 * Mib / 4);
        MemoryGrant grant = (await reserving.WaitAsync(TimeSpan.FromSeconds(10), Ct))!;
        Assert.Equal(Mib / 2, budget.ReservedBytes);
        grant.Release();
        Assert.Equal(0, budget.ReservedBytes);
    }

    /// <summary>
    /// The budget holds nothing, and its parent no room, which another child holds: no release under the budget would wake
    /// the reservation, which is told no at once.
    /// </summary>
    [Fact]
    public async Task AReservationNoReleaseWouldWakeIsToldNoAtOnce()
    {
        QueryMemoryBudget parent = Budget(Mib);
        QueryMemoryBudget budget = parent.HardChild(Mib);
        QueryMemoryBudget other = parent.HardChild(Mib);
        Assert.True(other.TryReserve(3 * Mib / 4));
        ValueTask<MemoryGrant?> reserving = budget.ReserveAsync(Mib / 2, Ct);
        Assert.True(reserving.IsCompleted);
        Assert.Null(await reserving);
        Assert.Equal(0, budget.ReservedBytes);
        other.Release(3 * Mib / 4);
        Assert.Equal(0, parent.ReservedBytes);
    }

    /// <summary>A large reservation at the head of the turn is not passed by a small one that would fit before it.</summary>
    [Fact]
    public async Task AReservationIsNotPassedByOneBehindIt()
    {
        QueryMemoryBudget budget = Budget(Mib);
        Assert.True(budget.TryReserve(Mib - (64L << 10)));
        Task<MemoryGrant?> large = budget.ReserveAsync(256L << 10, Ct).AsTask();
        Task<MemoryGrant?> small = budget.ReserveAsync(16L << 10, Ct).AsTask();
        Assert.False(large.IsCompleted);
        Assert.False(small.IsCompleted);
        budget.Release(512L << 10);
        MemoryGrant first = (await large.WaitAsync(TimeSpan.FromSeconds(10), Ct))!;
        MemoryGrant second = (await small.WaitAsync(TimeSpan.FromSeconds(10), Ct))!;
        first.Release();
        second.Release();
        budget.Release(Mib - (64L << 10) - (512L << 10));
        Assert.Equal(0, budget.ReservedBytes);
    }

    /// <summary>A cancelled reservation leaves its turn and holds nothing, and the one behind it is served.</summary>
    [Fact]
    public async Task ACancelledReservationLeavesItsTurnAndHoldsNothing()
    {
        QueryMemoryBudget budget = Budget(Mib);
        Assert.True(budget.TryReserve(Mib));
        using CancellationTokenSource cancel = new CancellationTokenSource();
        Task<MemoryGrant?> cancelled = budget.ReserveAsync(Mib / 2, cancel.Token).AsTask();
        Task<MemoryGrant?> behind = budget.ReserveAsync(Mib / 4, Ct).AsTask();
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        budget.Release(Mib / 2);
        MemoryGrant grant = (await behind.WaitAsync(TimeSpan.FromSeconds(10), Ct))!;
        Assert.Equal((Mib / 2) + (Mib / 4), budget.ReservedBytes);
        grant.Release();
        budget.Release(Mib / 2);
        Assert.Equal(0, budget.ReservedBytes);
    }

    /// <summary>A hundred reservations wait with no thread of theirs, and are all served in their turn.</summary>
    [Fact]
    public async Task AHundredReservationsWaitOnNoThreadAndAreAllServed()
    {
        QueryMemoryBudget budget = Budget(Mib);
        Assert.True(budget.TryReserve(Mib));
        int threads = System.Diagnostics.Process.GetCurrentProcess().Threads.Count;
        List<Task<MemoryGrant?>> waiting = [];
        for (int i = 0; i < 100; i++)
        {
            waiting.Add(budget.ReserveAsync(8L << 10, Ct).AsTask());
        }

        Assert.All(waiting, task => Assert.False(task.IsCompleted));
        Assert.InRange(System.Diagnostics.Process.GetCurrentProcess().Threads.Count, 0, threads + 4);
        budget.Release(Mib);
        MemoryGrant?[] grants = await Task.WhenAll(waiting).WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Equal(100L * (8L << 10), budget.ReservedBytes);
        foreach (MemoryGrant? grant in grants)
        {
            grant!.Release();
        }

        Assert.Equal(0, budget.ReservedBytes);
    }

    /// <summary>
    /// A budget a grant funds takes the grant's bytes first, which its parent counts already, and asks the parent for what
    /// passes them; its releases go to the parent, which counts what is left of the grant and what the budget holds, exactly;
    /// closed, the grant gives back what is left of it, and what the budget still holds goes back as it is released.
    /// </summary>
    [Fact]
    public async Task ABudgetAGrantFundsTakesTheGrantFirst()
    {
        QueryMemoryBudget parent = Budget(4 * Mib);
        MemoryGrant grant = (await parent.ReserveAsync(Mib, Ct))!;
        QueryMemoryBudget child = parent.HardChild(grant);
        grant.Release();
        Assert.Equal(Mib, parent.ReservedBytes);

        Assert.True(child.TryReserve(Mib / 2));
        Assert.Equal(Mib, parent.ReservedBytes);
        Assert.Equal(Mib / 2, child.GrantLeft);
        Assert.True(child.TryReserve(Mib));
        Assert.Equal(Mib + (Mib / 2), parent.ReservedBytes);
        child.Release(Mib);
        Assert.Equal(Mib / 2, parent.ReservedBytes);
        Assert.Equal(Mib / 2, child.ReservedBytes);

        Assert.True(child.TryReserve(Mib / 4));
        Assert.Equal(child.GrantLeft + child.ReservedBytes, parent.ReservedBytes);
        child.CloseGrant();
        Assert.Equal(child.ReservedBytes, parent.ReservedBytes);
        child.Release((Mib / 2) + (Mib / 4));
        Assert.Equal(0, parent.ReservedBytes);
        Assert.Equal(0, child.ReservedBytes);
    }

    /// <summary>A grant closed while its budget holds nothing gives back all that is left of it.</summary>
    [Fact]
    public async Task AGrantClosedUnusedGivesItsBytesBack()
    {
        QueryMemoryBudget parent = Budget(4 * Mib);
        MemoryGrant grant = (await parent.ReserveAsync(Mib, Ct))!;
        QueryMemoryBudget child = parent.HardChild(grant);
        child.CloseGrant();
        child.CloseGrant();
        Assert.Equal(0, parent.ReservedBytes);
        Assert.True(child.TryReserve(Mib / 2));
        Assert.Equal(Mib / 2, parent.ReservedBytes);
        child.Release(Mib / 2);
        Assert.Equal(0, parent.ReservedBytes);
    }
}
