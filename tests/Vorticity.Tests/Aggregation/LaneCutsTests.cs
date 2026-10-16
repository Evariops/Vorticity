using System.Collections.Generic;
using System.Linq;
using Vorticity.Aggregating;
using Xunit;

namespace Vorticity.Tests.Aggregation;

/// <summary>
/// The ranges a group by's queue hands its lanes hold live rows, every one of them: a range the
/// filter left nothing alive in would be a lane's pass for nothing, and the lane that drew it alone
/// would group no row.
/// </summary>
public sealed class LaneCutsTests
{
    private const long BlockRows = 1_024;

    [Theory]
    [InlineData(31, 37)]
    [InlineData(0, 6)]
    [InlineData(57, 63)]
    [InlineData(0, 63)]
    [InlineData(20, 20)]
    public void EveryRangeHoldsLiveRowsAndTheyTileTheRows(int firstLive, int lastLive)
    {
        // Sixty-four blocks, a boundary at every one, and the live ones contiguous: what a filter on a
        // sorted key leaves of a file.
        RowRange rows = new RowRange(0, 64 * BlockRows);
        long alive = (lastLive - firstLive + 1) * BlockRows;
        LaneCuts cuts = new LaneCuts(rows, alive, degree: 4, BlockRows);
        for (int block = 0; block < 63; block++)
        {
            cuts.Boundary((block + 1) * BlockRows, block >= firstLive && block <= lastLive ? BlockRows : 0);
        }

        RowRange[] ranges = cuts.Finish(lastLive == 63 ? BlockRows : 0) ?? [rows];
        Assert.Equal(rows.Start, ranges[0].Start);
        Assert.Equal(rows.End, ranges[^1].End);
        for (int i = 1; i < ranges.Length; i++)
        {
            Assert.Equal(ranges[i - 1].End, ranges[i].Start);
        }

        RowRange live = new RowRange(firstLive * BlockRows, (lastLive + 1) * BlockRows);
        List<RowRange> empty = [.. ranges.Where(range => range.Intersect(live).IsEmpty)];
        Assert.True(empty.Count == 0, $"{empty.Count} of {ranges.Length} ranges hold no live row, the first {empty.FirstOrDefault()}");
    }
}
