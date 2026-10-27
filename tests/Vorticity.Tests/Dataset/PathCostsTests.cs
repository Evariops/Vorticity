// What a path costs an object, by the formula: its bytes, and its requests at the bytes a round trip is worth.
//
// THE ORACLE IS THE ARITHMETIC: regions within the profile's joined gap of each other read as one request, the gap between
// them with them, apart as two.
using Vorticity.Dataset;
using Vorticity.Indexes;
using Vorticity.IO;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed partial class PathCostsTests
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

    /// <summary>
    /// A column written against an object dictionary costs its dictionary's bytes besides its chunks', in its first chunk's
    /// request, the dictionary lying right before it; for a column of one chunk, among the last chunks, so that the gap
    /// between two columns read across it counts it too. A run of a share of the rows reads the dictionary whole.
    /// </summary>
    [Fact]
    public void AColumnsDictionaryIsReadWithItsFirstChunk()
    {
        CostProfile profile = CostProfile.Remote;
        PathCosts costs = new PathCosts(profile, 1 << 20);
        long gap = profile.JoinedGap;

        // Three columns of one chunk each, the first and the second written against a dictionary; the second's chunk and
        // dictionary pass the joined gap by a byte.
        ObjectEntry entry = Entry([100_000, 50_000, 30_000], [1, 1, 1], [5_000, gap - 50_000 + 1, 0]);
        long open = PathCosts.OpenBytes(entry);
        Assert.Equal((2, open + 105_000), Read(costs.Rows(entry, [0])));
        Assert.Equal((3, open + 135_000), Read(costs.Rows(entry, [2, 0])));

        // A byte less, and the first and the last are read together, across the second.
        ObjectEntry joined = Entry([100_000, 50_000, 30_000], [1, 1, 1], [5_000, gap - 50_000, 0]);
        Assert.Equal((2, open + 135_000 + gap), Read(costs.Rows(joined, [0, 2])));

        // The second of several chunks: its dictionary lies before its first, not among the last chunks.
        ObjectEntry chunked = Entry([100_000, 4_000_000, 30_000], [1, 40, 1], [5_000, 400_000, 0]);
        Assert.Equal((2, open + 135_000 + 100_000), Read(costs.Rows(chunked, [0, 2])));

        // A tenth of the rows of the first column, of one chunk: the chunk, and the dictionary apart.
        Assert.Equal((3, open + 105_000), Read(costs.Share(entry, [0], 0.1)));
    }

    /// <summary>
    /// A run of a share of the rows of an object that keeps its columns' marks reads, of each column, the granules it lies in
    /// and one more it starts inside, a request a column at least, as a scan reads a part of a chunk: of 100 000 rows, 13
    /// granules of 8 192, a hundredth reads 1.13 of them; past three quarters of a column, its chunk whole. An object that
    /// keeps no marks reads its chunks whole.
    /// </summary>
    [Fact]
    public void AShareOfAnObjectThatKeepsItsMarksReadsItsGranules()
    {
        PathCosts costs = new PathCosts(CostProfile.Remote, 1 << 20, granuleRows: 8_192);
        ObjectEntry marked = Entry([100_000, 50_000, 30_000], [1, 1, 1], [0, 0, 0], marks: true);
        long open = PathCosts.OpenBytes(marked);
        Assert.Equal((2, open + 8_692), Read(costs.Share(marked, [0], 0.01)));
        Assert.Equal((3, open + 15_646), Read(costs.Share(marked, [0, 2], 0.01)));
        Assert.Equal((2, open + 100_000), Read(costs.Share(marked, [0], 0.9)));

        ObjectEntry unmarked = Entry([100_000, 50_000, 30_000], [1, 1, 1], [0, 0, 0]);
        Assert.Equal((2, open + 100_000), Read(costs.Share(unmarked, [0], 0.01)));
    }

    private static (int Requests, long Bytes) Read(PathCosts.Cost cost) => (cost.Requests, cost.Bytes);

    /// <summary>
    /// An object of three columns of <paramref name="columns"/> bytes in <paramref name="chunks"/> chunks, each written
    /// against a dictionary of the bytes <paramref name="dictionaries"/> says, none for 0; with <paramref name="marks"/>,
    /// the marks of its columns kept.
    /// </summary>
    private static ObjectEntry Entry(long[] columns, long[] chunks, long[] dictionaries, bool marks = false)
    {
        ColumnDictionary?[] held = new ColumnDictionary?[dictionaries.Length];
        for (int field = 0; field < held.Length; field++)
        {
            held[field] = dictionaries[field] == 0 ? null : new ColumnDictionary(0, (uint)dictionaries[field], 6, 100, Vorticity.Types.PType.U8, Ordered: true);
        }

        ObjectContents contents = new ObjectContents(
            Vorticity.Types.Serialization.DTypeFlatBuffers.Serialize(VortexTypes.ToDType(Three.Schema, new Vorticity.Types.DTypeArena())),
            ["vortex.primitive"],
            100_000,
            [new IndexSegment(0, 64, 0), new IndexSegment(64, 64, 0), new IndexSegment(128, 64, 0)],
            [])
        {
            ColumnBytes = columns,
            ColumnChunks = chunks,
            Dictionaries = held,
            Marks = marks ? [new IndexSegment(1_000, 100, 0), new IndexSegment(1_100, 100, 0), new IndexSegment(1_200, 100, 0)] : [],
        };
        return new ObjectEntry("data/a.vortex", 7, 100_000, 50_000_000, 11, (ObjectSummaries?)null).WithFooterBytes(8_000).WithTableOfContents(contents.ToBytes());
    }

    [VortexRecord]
    public partial record struct Three(string A, string B, long C);
}
