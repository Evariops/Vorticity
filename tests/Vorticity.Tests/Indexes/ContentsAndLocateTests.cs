// An object's synopsis regions found by their column through the table made at the first lookup, as a scan of the list
// finds them: the first of a column wins, a column with none and one past every listed column have none. And a value's
// granules or-ed into a map from where the index keeps them, as a list or as a map of any length, against the granules
// drawn: every value's, in maps of several sizes around the vectors' width.
using System;
using System.Collections.Generic;
using Vorticity.Indexes;
using Vorticity.Types.Variants;
using Xunit;

namespace Vorticity.Tests.Indexes;

public sealed class ContentsAndLocateTests
{
    [Fact]
    public void EachColumnsRegionIsTheFirstListedForIt()
    {
        List<(int Field, IndexSegment Region)> sheets = [(3, Region(30)), (0, Region(0)), (3, Region(31)), (7, Region(70))];
        ObjectContents contents = new ObjectContents([], [], 0, []) { Sheets = sheets, Counts = [(2, Region(20))] };
        Assert.Equal(Region(0), contents.SheetOf(0));
        Assert.Null(contents.SheetOf(1));
        Assert.Equal(Region(30), contents.SheetOf(3));
        Assert.Equal(Region(70), contents.SheetOf(7));
        Assert.Null(contents.SheetOf(8));
        Assert.Null(contents.SheetOf(-1));
        Assert.Equal(Region(20), contents.CountsOf(2));
        Assert.Null(contents.CountsOf(0));
        Assert.Null(contents.LocateOf(0));
    }

    // A variant path's region by its column, key and class, scanned among few paths and through a table among many: the
    // first of a path wins, another class or column has none.
    [Theory]
    [InlineData(5)]
    [InlineData(40)]
    public void EachPathsRegionIsTheFirstListedForIt(int paths)
    {
        List<(int Field, string Key, PathClass Class, IndexSegment Region)> locates = [];
        for (int p = 0; p < paths; p++)
        {
            locates.Add((1, $"attr_{p:D3}", PathClass.String, Region(p)));
        }

        locates.Add((1, "attr_000", PathClass.String, Region(999)));
        locates.Add((1, "attr_000", PathClass.Integer, Region(500)));
        ObjectContents contents = new ObjectContents([], [], 0, []) { PathLocates = locates, PathGranuleStats = locates, PathSingleRows = [7, 8] };
        for (int p = 0; p < paths; p++)
        {
            Assert.Equal(Region(p), contents.PathLocateOf(1, $"attr_{p:D3}", PathClass.String));
        }

        Assert.Equal(Region(500), contents.PathLocateOf(1, "attr_000", PathClass.Integer));
        Assert.Null(contents.PathLocateOf(1, "attr_000", PathClass.Double));
        Assert.Null(contents.PathLocateOf(2, "attr_000", PathClass.String));
        Assert.Null(contents.PathLocateOf(1, "absent", PathClass.String));
        Assert.Equal((Region(1), 8L), contents.PathGranuleStatsOf(1, "attr_001", PathClass.String));
        Assert.Equal((Region(2), 0L), contents.PathGranuleStatsOf(1, "attr_002", PathClass.String));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(63)]
    [InlineData(64)]
    [InlineData(255)]
    [InlineData(257)]
    [InlineData(1_000)]
    [InlineData(4_133)]
    public void EachValuesGranulesAreOredIn(int granuleCount)
    {
        Random random = new Random(granuleCount);
        const int Values = 40;
        byte[]?[] values = new byte[]?[Values];
        int[] starts = new int[Values + 1];
        List<int> granules = [];
        List<HashSet<int>> expected = [];
        for (int v = 0; v < Values; v++)
        {
            values[v] = [(byte)'v', (byte)v];
            starts[v] = granules.Count;
            HashSet<int> mine = [];

            // A few granules, kept as a list, or many, kept as a map.
            int odds = v % 3 == 0 ? 50 : 3;
            for (int g = 0; g < granuleCount; g++)
            {
                if (random.Next(odds) == 0)
                {
                    granules.Add(g);
                    mine.Add(g);
                }
            }

            expected.Add(mine);
        }

        starts[Values] = granules.Count;
        byte[] bytes = ColumnLocate.ToBytes(granuleCount * 16L, 16, new ArrayValues(values), starts, granules.ToArray(), most: granuleCount);
        ColumnLocate locate = ColumnLocate.From(bytes) ?? throw new InvalidOperationException("The index does not read back.");
        for (int v = 0; v < Values; v++)
        {
            ulong[] map = new ulong[(granuleCount + 63) / 64];
            locate.Or(v, map);
            for (int g = 0; g < map.Length * 64; g++)
            {
                bool set = ((map[g >> 6] >> (g & 63)) & 1) != 0;
                Assert.True(expected[v].Contains(g) == set, $"value {v}, granule {g} of {granuleCount}: {set}");
            }
        }
    }

    private static IndexSegment Region(int at) => new IndexSegment((ulong)at * 4096, 4096, 0);
}
