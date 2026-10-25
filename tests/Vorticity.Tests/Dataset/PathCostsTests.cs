// What a path costs an object, by the formula: its bytes, and its requests at the bytes a round trip is worth.
//
// THE ORACLE IS THE ARITHMETIC: regions within 256 KiB of each other read as one request, apart as two; the rows of a
// column cost an open and a request a mebibyte of chunk.
using Vorticity.Dataset;
using Vorticity.Indexes;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed class PathCostsTests
{
    [Fact]
    public void RegionsWithinAGapReadAsOneRequestAndApartAsTwo()
    {
        PathCosts.Cost joined = PathCosts.Regions([new IndexSegment(0, 1_000, 0), new IndexSegment(100_000, 2_000, 0), null]);
        Assert.Equal(new PathCosts.Cost(1, 3_000), joined);

        PathCosts.Cost apart = PathCosts.Regions([new IndexSegment(10_000_000, 500, 0), new IndexSegment(0, 1_000, 0)]);
        Assert.Equal(new PathCosts.Cost(2, 1_500), apart);
        Assert.Equal(1_500 + (2 * PathCosts.RequestBytes), apart.Weight);

        Assert.Equal(new PathCosts.Cost(0, 0), PathCosts.Regions([]));
    }
}
