// Keys whose columns bear the names of a table's own columns: its bucket, its count of rows, or a state of one of its
// measures. Each takes a name of its own in the tables and in the batches of states, and a question over them gives the
// bits its rows give.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Vorticity.Aggregating;
using Vorticity.Dataset;
using Vorticity.Tests.Writing;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed partial class SynopsisAnswerTests
{
    private static readonly string[] Shelves = ["low", "mid", "top"];

    private static readonly string[] Tallies = ["one", "two", "three", "four"];

    /// <summary>
    /// The columns <c>bucket</c> and <c>count</c>, and <c>Bytes.min</c>, named as the least of the measure <c>Bytes</c>,
    /// all three keys of the tables: grouped by, filtered on or counted distinct, they give the bits their rows give, from
    /// the tables.
    /// </summary>
    [Theory]
    [InlineData("count by count")]
    [InlineData("every measure by bucket and count")]
    [InlineData("under a count")]
    [InlineData("distinct counts by bucket")]
    public async Task KeysNamedAsATablesColumnsGiveTheBitsOfTheirRows(string question)
    {
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await ClashingAsync(store);
        (List<string> on, ScanMetrics metrics) = await AskClashAsync(dataset, question, synopses: true);
        (List<string> off, ScanMetrics without) = await AskClashAsync(dataset, question, synopses: false);
        Assert.Equal(off, on);
        Assert.NotEmpty(on);
        Assert.True(metrics.SynopsisRows > 0, "no state folded");
        Assert.True(metrics.Rows < without.Rows, $"{metrics.Rows} rows read, as many as without");
    }

    /// <summary>
    /// Rows a delete marked under keys named as a table's columns: each comes out of its table's row, found by its keys'
    /// columns in the rows, so a count and a sum by them come from the tables corrected, as the rows left give them.
    /// </summary>
    [Fact]
    public async Task RowsADeleteMarkedComeOutOfTheStatesOfKeysNamedAsATablesColumns()
    {
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await ClashingAsync(store, marking: true);
        RowChangeResult deleted = await dataset.DeleteAsync(VortexExpr.Parse("bucket = 'low' and count = 'two'"), Ct);
        Assert.True(deleted.ObjectsMarked > 0);
        (List<string> on, ScanMetrics metrics) = await AskClashAsync(dataset, "count by count", synopses: true);
        (List<string> off, _) = await AskClashAsync(dataset, "count by count", synopses: false);
        Assert.Equal(off, on);
        Assert.True(metrics.SynopsisRows > 0, "no state folded under the marks");
    }

    /// <summary>
    /// A key keeps its own name unless a column of states could take it, or it ends in the mark the others take: a table
    /// none of whose keys is named so keeps the names it always had.
    /// </summary>
    [Theory]
    [InlineData("service", "service")]
    [InlineData("http.method", "http.method")]
    [InlineData("counts", "counts")]
    [InlineData("count", "count#key")]
    [InlineData("bucket", "bucket#key")]
    [InlineData("Bytes.min", "Bytes.min#key")]
    [InlineData("Bytes.sum.m0", "Bytes.sum.m0#key")]
    [InlineData("route.distinct", "route.distinct#key")]
    [InlineData("count#key", "count#key#key")]
    public void AKeyTakesANameNoOtherColumnTakes(string name, string column) => Assert.Equal(column, StateRequest.KeyColumn(name));

    /// <summary>The groups of <paramref name="question"/> over <paramref name="dataset"/>, each a line of its key and results, and what reading them did.</summary>
    private static Task<(List<string> Groups, ScanMetrics Metrics)> AskClashAsync(VortexDataset dataset, string question, bool synopses) =>
        GroupsAsync(Clash(dataset.Scan<Clashing>().With(new ScanOptions { UseSynopses = synopses }), question));

    /// <summary>The aggregation <paramref name="question"/> names, over <paramref name="scan"/>.</summary>
    private static Vorticity.Aggregation Clash(Scan<Clashing> scan, string question) => question switch
    {
        "count by count" => scan.GroupBy(r => r.Count).Select(g => (g.Key, g.Count(), g.Sum(r => r.Bytes))),
        "every measure by bucket and count" => scan.GroupBy(r => (r.Bucket, r.Count)).Select(g => (
            g.Key.Bucket, g.Key.Count, g.Count(), g.Sum(r => r.Bytes), g.Min(r => r.Bytes), g.Max(r => r.Bytes), g.Average(r => r.Bytes))),
        "under a count" => scan.Where(r => r.Count == "two").GroupBy(r => r.Bucket).Select(g => (g.Key, g.Count(), g.Max(r => r.Bytes))),
        _ => scan.GroupBy(r => r.Bucket).Select(g => (g.Key, g.CountDistinct(r => r.Count), g.ApproximateCountDistinct(r => r.Count))),
    };

    /// <summary>
    /// Two hours and a quarter of rows from noon, a row every 1.25 s, appended in sixteen parts and compacted, as
    /// <see cref="FilledAsync"/> writes them: two values of <c>Bytes.min</c>, three of <c>bucket</c> and four of
    /// <c>count</c>, the synopsis columns in that order. With <paramref name="marking"/>, a delete marks the rows it takes.
    /// </summary>
    private static async Task<VortexDataset> ClashingAsync(Vorticity.IO.IObjectStore store, bool marking = false)
    {
        DatasetOptions options = new DatasetOptions
        {
            ClusteringKey = [nameof(Clashing.At)],
            Periods = new PeriodOptions { Lateness = TimeSpan.FromMinutes(5), Length = SliceWidth.Hour, SliceTargetRows = 100 },
            Write = new VortexWriteOptions { Indexes = IndexPolicy.None.WithBudgetPerMille(10_000) },
            MarkedObjectBytes = marking ? 0 : 1L << 20,
            MarkedShare = marking ? 1 : 8,
        };
        VortexDataset dataset = await VortexDataset.CreateAsync(store, Clashing.Schema, options, Ct);
        Random random = new Random(43);
        for (int part = 0; part < 16; part++)
        {
            Clashing[] rows = [.. Enumerable.Range(0, 400).Select(i => new Clashing(
                Noon.AddSeconds((part * 400 + i) * 1.25),
                Shelves[random.Next(Shelves.Length)],
                Tallies[random.Next(Tallies.Length)],
                random.Next(2) == 0 ? "east" : "west",
                random.NextInt64(1, 1L << 40)))];
            await using ObjectDraft draft = dataset.StartObject();
            await draft.Writer.WriteAsync<Clashing>(rows, Ct);
            await dataset.AppendAsync(draft, Ct);
        }

        while (await dataset.CompactAsync(new CompactionOptions { LevelZeroCeiling = 2 }, Ct) is not null)
        {
        }

        return dataset;
    }

    /// <summary>A row whose columns take the names of a table's bucket, of its count of rows, and of the least of its bytes.</summary>
    [VortexRecord]
    public partial record struct Clashing(
        DateTimeOffset At,
        [VortexColumn("bucket")] string Bucket,
        [VortexColumn("count")] string Count,
        [VortexColumn("Bytes.min")] string Zone,
        long Bytes);
}
