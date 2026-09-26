using Vorticity.Buffers;
using Xunit;

namespace Vorticity.Tests.Buffers;

public sealed class OwnedBatchBytesTests
{
    [Fact]
    public void ABalancePastItsThresholdAsksForACollectionAndTheThresholdFollowsTheBalance()
    {
        int collections = 0;
        OwnedBatchBytes balance = new OwnedBatchBytes(1_000, () => collections++);

        balance.Hold(600);
        Assert.Equal(0, collections);

        // Past the floor: one collection, and the next only once the balance has doubled.
        balance.Hold(600);
        Assert.Equal(1, collections);
        Assert.Equal(2_400, balance.Threshold);
        balance.Hold(600);
        Assert.Equal(1, collections);

        // Under a quarter of its threshold, the balance halves it again, down to the floor.
        balance.Release(1_500);
        Assert.Equal(300, balance.Held);
        Assert.Equal(1_200, balance.Threshold);
        balance.Release(300);
        Assert.Equal(1_000, balance.Threshold);

        balance.Hold(1_000);
        Assert.Equal(2, collections);
    }

    [Fact]
    public void ALoopThatGivesEachBatchBackNeverAsksForACollection()
    {
        int collections = 0;
        OwnedBatchBytes balance = new OwnedBatchBytes(1_000, () => collections++);
        for (int i = 0; i < 10_000; i++)
        {
            balance.Hold(900);
            balance.Release(900);
        }

        Assert.Equal(0, collections);
        Assert.Equal(0, balance.Held);
    }
}
