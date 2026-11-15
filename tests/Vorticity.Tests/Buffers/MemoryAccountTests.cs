using Vorticity.Buffers;
using Xunit;

namespace Vorticity.Tests.Buffers;

/// <summary>
/// A rent of the pool names its account: the block's capacity is counted there from the rent until its last holder gives it
/// back, a refusal throws as a reservation does, and an account that ends gives back what its blocks hold whatever comes back
/// after.
/// </summary>
public sealed class MemoryAccountTests
{
    [Fact]
    public void ARentCountsItsCapacityUntilItsLastHolderGivesItBack()
    {
        BlockPool pool = new BlockPool(maxPerBucket: 4);
        TestAccount account = new TestAccount();

        // A block is 4 KiB at least: its capacity is what counts, not the length asked for.
        BlockOwner block = pool.Rent(account, 1000, 64);
        Assert.Equal(block.Capacity, account.Held);
        Assert.Equal(4096, account.Held);

        SegmentOwner held = block.Retain();
        block.Release();
        Assert.Equal(4096, account.Held);

        held.Release();
        Assert.Equal(0, account.Held);
        Assert.Equal(1, pool.ParkedCount(1000));
    }

    [Fact]
    public void ARefusedRentThrowsAndCountsNothingTheBlockBackInThePool()
    {
        BlockPool pool = new BlockPool(maxPerBucket: 4);
        Refusing account = new Refusing();

        VortexMemoryException refused = Assert.Throws<VortexMemoryException>(() => pool.Rent(account, 4096, 64));
        Assert.Contains("4,096", refused.Message, System.StringComparison.Ordinal);
        Assert.Equal(1, pool.ParkedCount(4096));
    }

    [Fact]
    public void AWorkAccountClosedGivesBackWhatItsBlocksHoldAndIgnoresThemAfter()
    {
        // A ceiling of its own under the process's budget: a stand-in for the process, made with no parent, would read the
        // heap the whole suite holds as its own rest.
        QueryMemoryBudget budget = new QueryMemoryBudget(1L << 30);
        WorkAccount work = new WorkAccount(budget, "test scan");
        BlockPool pool = new BlockPool(maxPerBucket: 4);

        BlockOwner first = pool.Rent(work, 4096, 64);
        BlockOwner second = pool.Rent(work, 8192, 64);
        Assert.Equal(4096 + 8192, work.Held);
        Assert.Equal(4096 + 8192, budget.ReservedBytes);

        first.Release();
        Assert.Equal(8192, budget.ReservedBytes);

        // The work ends while a caller still holds a block: everything goes back, and the block's return later is ignored.
        work.Dispose();
        Assert.Equal(0, budget.ReservedBytes);
        second.Release();
        Assert.Equal(0, budget.ReservedBytes);
        Assert.Equal(0, work.Held);

        // A block rented after the end is the caller's alone, counted nowhere.
        BlockOwner late = pool.Rent(work, 4096, 64);
        Assert.Equal(0, budget.ReservedBytes);
        late.Release();
        Assert.Equal(0, budget.ReservedBytes);
    }

    [Fact]
    public void ABudgetsAccountReservesInItAndRefusesPastItsCeiling()
    {
        QueryMemoryBudget budget = new QueryMemoryBudget(64 << 10);
        BlockPool pool = new BlockPool(maxPerBucket: 4);

        BlockOwner block = pool.Rent(budget.Account, 32 << 10, 64);
        Assert.Equal(32 << 10, budget.ReservedBytes);

        VortexMemoryException refused = Assert.Throws<VortexMemoryException>(() => pool.Rent(budget.Account, 64 << 10, 64));
        Assert.Contains("memory budget of 65,536 bytes", refused.Message, System.StringComparison.Ordinal);
        Assert.Equal(32 << 10, budget.ReservedBytes);

        block.Release();
        Assert.Equal(0, budget.ReservedBytes);
    }

    /// <summary>An account that refuses every rent.</summary>
    private sealed class Refusing : MemoryAccount
    {
        internal override bool TryTake(long bytes) => false;

        internal override void Give(long bytes) => Assert.Fail("Nothing was taken.");

        internal override VortexMemoryException Refused(long bytes) =>
            new VortexMemoryException(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"Refused {bytes:N0} bytes."));
    }
}
