// A lane's sketches moved into a part of a merge: taken as they are where their arrays count in no query's memory, merged
// where they do; the counts of the lane's memory settle either way.
using System;
using System.Linq;
using Vorticity.Aggregating;
using Xunit;

namespace Vorticity.Tests.Aggregation;

public sealed partial class DistributionTests
{
    /// <summary>
    /// A lane's slot of a quantile, its sketches from the lane's shelf, moved into a part's slot that holds no group yet: the
    /// sketch of three buckets, its arrays under a page, is the part's from then on and the lane's no more; the sketch of
    /// forty, a window of five kilobytes the lane's memory counts, is merged into a sketch of the part's own and stays the
    /// lane's. Both give the quantiles the lane's gave, and once both slots let go, the lane's memory holds nothing. Made
    /// anew in each part and merged, the sketches of a quantile per service and hour over a week (T6) at 32 lanes were
    /// made twice an ask: 60 ms where one lane took 40.
    /// </summary>
    [Fact]
    public void ALanesSketchesMoveIntoAPartAsTheyAreWhereNoMemoryCountsTheirArrays()
    {
        using QueryMemory memory = new QueryMemory(new QueryMemoryBudget(64 << 20));
        ArrayShelf laneShelf = new ArrayShelf(memory);
        DistributionSlot<double, double?> lane = new DistributionSlot<double, double?>(4, static s => s?.Quantile(0.99), shelved: true);
        lane.Govern(laneShelf);
        lane.EnsureGroups(2);
        QuantileSketch few = lane.Sketch(0);
        few.AddAll<double>([1, 2, 4]);
        QuantileSketch many = lane.Sketch(1);
        many.AddAll<double>([.. Enumerable.Range(0, 40).Select(static power => Math.Pow(2, power))]);
        (double? FewP99, double? ManyP99) expected = (few.Quantile(0.99), many.Quantile(0.99));
        Assert.True(memory.Measured > 0, "the window counts in the lane's memory");

        DistributionSlot<double, double?> part = new DistributionSlot<double, double?>(4, static s => s?.Quantile(0.99), shelved: true);
        part.Govern(ArrayShelf.Retained);
        part.EnsureGroups(2);
        part.MoveFrom(lane, [0, 1], [1, 0]);

        Assert.Same(few, part.Held(1));
        Assert.Null(lane.Held(0));
        Assert.NotSame(many, part.Held(0));
        Assert.Same(many, lane.Held(1));
        Assert.Equal(expected, (part.Held(1)!.Quantile(0.99), part.Held(0)!.Quantile(0.99)));

        lane.Release();
        laneShelf.LetGo();
        part.Release();
        Assert.Equal(0, memory.Measured);
        Assert.Equal(0, memory.Held);
    }
}
