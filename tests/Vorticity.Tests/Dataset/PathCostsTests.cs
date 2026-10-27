// What a path costs an object, by the formula: its bytes, and its requests at the bytes a round trip is worth.
//
// THE ORACLE IS THE ARITHMETIC: regions within the profile's joined gap of each other read as one request, the gap between
// them with them, apart as two.
using Vorticity.Dataset;
using Vorticity.Indexes;
using Vorticity.IO;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed class PathCostsTests
{
    [Fact]
    public void RegionsWithinAGapReadAsOneRequestAndApartAsTwo()
    {
        CostProfile profile = CostProfile.Remote;
        PathCosts costs = new PathCosts(profile, 1 << 20);
        long gap = profile.JoinedGap;
        PathCosts.Cost joined = costs.Regions(1, [new IndexSegment(0, 1_000, 0), new IndexSegment((ulong)(1_000 + gap - 1), 2_000, 0), null]);
        Assert.Equal(new PathCosts.Cost(1, 1_000 + gap - 1 + 2_000), joined);

        PathCosts.Cost apart = costs.Regions(1, [new IndexSegment(10_000_000, 500, 0), new IndexSegment(0, 1_000, 0)]);
        Assert.Equal(new PathCosts.Cost(2, 1_500), apart);
        Assert.Equal(1_500 + (2.0 * profile.RequestBytes), costs.Weight(apart));

        Assert.Equal(new PathCosts.Cost(0, 0), costs.Regions(1, []));
    }
}
