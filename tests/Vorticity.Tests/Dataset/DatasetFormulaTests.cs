// The price of an aggregation's exact answer, by the formula, against what it reads: every path an object takes, its counts,
// its blocks' statistics and the rows of the blocks they cannot answer, a partial learned or its rows, priced before a read.
//
// THE ORACLE IS THE STORE: a cold handle's bytes, counted by the store, stay within a twentieth over the price the planner
// announced, a bound (an index that narrows the rows, or blocks that answer, read less); and each object a job writes says
// how many of its rows lie in blocks of one value, which the price of its blocks rests on.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Aggregating;
using Vorticity.Dataset;
using Vorticity.IO;
using Vorticity.Tests.Writing;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed partial class DatasetFormulaTests : IClassFixture<DatasetFormulaTests.Fixture>
{
    private static readonly DateTimeOffset Noon = new DateTimeOffset(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);

    // Four services of 20 000 calls over two hours, then three hundred of fifty: the first fill blocks of their own, an
    // hour each, the others share blocks of several services and both hours.
    private const int Large = 4;
    private const int LargeCalls = 20_000;
    private const int Small = 300;
    private const int SmallCalls = 50;

    private readonly Fixture _fixture;

    public DatasetFormulaTests(Fixture fixture) => _fixture = fixture;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string, bool> Questions()
    {
        TheoryData<string, bool> questions = [];
        foreach (string question in (string[])["count by service and hour", "count by route, a large service", "count by route, a small service",
            "5xx by service", "measures by host", "count by status, a latency window", "count by route under 5xx", "count by service, a window"])
        {
            questions.Add(question, true);
            questions.Add(question, false);
        }

        return questions;
    }

    /// <summary>
    /// Each object a job writes says the rows of its blocks that hold one service and one hour: every call of the large
    /// services, an hour of one filling blocks of its own, and none of the small ones, whose blocks gather several.
    /// </summary>
    [Fact]
    public async Task AnObjectSaysTheRowsOfItsBlocksOfOneValue()
    {
        await using VortexDataset dataset = await VortexDataset.OpenAsync(_fixture.ByService, new DatasetOptions(), Ct);
        long pure = 0;
        int objects = 0;
        await foreach (PositionedObject held in dataset.ScanBuilder().ObjectsAsync(Ct))
        {
            objects++;
            Assert.NotNull(held.Entry.Contents);
            long rows = held.Entry.Rows;
            Assert.InRange(held.Entry.Contents.PureBlockRows, 0, rows);
            pure += held.Entry.Contents.PureBlockRows;
        }

        Console.WriteLine($"FORMULA pure blocks: {pure:N0} rows of {Large * LargeCalls:N0} large calls in {objects} objects");
        Assert.Equal(Large * LargeCalls, pure);
    }

    /// <summary>
    /// Each question, clustered by service or in the order of arrival, reads no more than a twentieth over what the planner
    /// priced its exact answer at before reading, and gives the rows' answer.
    /// </summary>
    [Theory]
    [MemberData(nameof(Questions))]
    public async Task AQuestionReadsWithinItsPrice(string question, bool byService)
    {
        await using CountingObjectStore store = new CountingObjectStore(byService ? _fixture.ByService : _fixture.ByArrival);
        (long Requests, long Bytes)? priced;
        await using (VortexSession session = VortexSession.Create(_ => { }))
        await using (VortexDataset dataset = await VortexDataset.OpenAsync(store, new DatasetOptions { Session = session }, Ct))
        {
            priced = await EstimateRun.ReadsOfAsync((AggregationQuery)Ask(dataset, question, synopses: true).Query, Ct);
        }

        (long read, List<string> answer) = await ReadAsync(store, question, synopses: true);
        (long rows, List<string> expected) = await ReadAsync(store, question, synopses: false);
        Console.WriteLine($"FORMULA {question}, {(byService ? "by service" : "by arrival")}: priced {priced}, read {read:N0}, rows {rows:N0}");
        Assert.Equal(expected, answer);
        Assert.NotNull(priced);
        Assert.True(read <= priced.Value.Bytes + (priced.Value.Bytes / 20), $"{question}: {read} bytes read, priced at {priced.Value.Bytes}");
    }

    /// <summary>
    /// In the order of arrival every block gathers several instants, each its own value of the key's first column. Under a
    /// question where two columns vary, the route and the status, no block would be answered: the objects read their
    /// rows, and no statistics for nothing. Under counts by service within a window of the instant, where the service
    /// alone varies in a block and the window holds the block whole or not at all but at its edges, the blocks answer still.
    /// </summary>
    [Theory]
    [InlineData("count by route under 5xx", false)]
    [InlineData("count by service, a window", true)]
    public async Task BlocksThatGatherSeveralValuesAnswerOnlyWhereOneColumnVaries(string question, bool answered)
    {
        await using CountingObjectStore store = new CountingObjectStore(_fixture.ByArrival);
        await using (VortexDataset dataset = await VortexDataset.OpenAsync(store, new DatasetOptions(), Ct))
        {
            await foreach (PositionedObject held in dataset.ScanBuilder().ObjectsAsync(Ct))
            {
                Assert.Equal(0, held.Entry.Contents!.PureBlockRows);
            }

            ScanPlan plan = await Ask(dataset, question, synopses: true).ExplainAsync(Ct);
            Assert.Equal(answered, plan.States.Any(step => step.Read == "blocks"));
        }

        (long read, _) = await ReadAsync(store, question, synopses: true);
        (long rows, _) = await ReadAsync(store, question, synopses: false);
        Assert.True(answered || read <= rows, $"{question}: {read} bytes through the plan, {rows} through the rows");
    }

    /// <summary>A question asked cold, by a handle and a session of its own: the store's bytes, and the answer.</summary>
    private static async Task<(long Bytes, List<string> Answer)> ReadAsync(CountingObjectStore store, string question, bool synopses)
    {
        await using VortexSession session = VortexSession.Create(_ => { });
        await using VortexDataset dataset = await VortexDataset.OpenAsync(store, new DatasetOptions { Session = session }, Ct);
        store.Reset();
        List<string> rows = [];
        await using (IAsyncEnumerator<RecordBatch> batches = Ask(dataset, question, synopses).Query.Batches(Ct))
        {
            while (await batches.MoveNextAsync())
            {
                Values.DescribeRows(batches.Current, rows);
            }
        }

        rows.Sort(StringComparer.Ordinal);
        return (store.BytesRead, rows);
    }

    private static Vorticity.Aggregation Ask(VortexDataset dataset, string question, bool synopses)
    {
        Scan<Call> scan = dataset.Scan<Call>().With(new ScanOptions { UseSynopses = synopses });
        return question switch
        {
            "count by service, a window" => scan.Where(c => c.At >= Noon.AddMinutes(10) && c.At < Noon.AddMinutes(50)).GroupBy(c => c.Service).Select(g => (g.Key, g.Count())),
            "count by service and hour" => scan.GroupBy(c => (c.Service, c.At.Truncate(CalendarUnit.Hour))).Select(g => (g.Key.Service, g.Key.Item2, g.Count())),
            "count by route, a large service" => scan.Where(c => c.Service == "large-1").GroupBy(c => c.Route).Select(g => (g.Key, g.Count())),
            "count by route, a small service" => scan.Where(c => c.Service == "small-150").GroupBy(c => c.Route).Select(g => (g.Key, g.Count())),
            "5xx by service" => scan.Where(c => c.Status >= 500).GroupBy(c => c.Service).Select(g => (g.Key, g.Count())),
            "measures by host" => scan.GroupBy(c => c.Host).Select(g => (g.Key, g.Count(), g.Sum(c => c.Size), g.Average(c => c.Latency))),
            "count by status, a latency window" => scan.Where(c => c.Latency >= 100 && c.Latency < 200).GroupBy(c => c.Status).Select(g => (g.Key, g.Count())),
            "count by route under 5xx" => scan.Where(c => c.Status >= 500).GroupBy(c => c.Route).Select(g => (g.Key, g.Count())),
            _ => throw new ArgumentException($"No question named '{question}'.", nameof(question)),
        };
    }

    /// <summary>
    /// The calls, keyed by the service then the instant with blocks ending at each hour, compacted into objects of a
    /// mebibyte; and the same calls keyed by the instant, an object an hour.
    /// </summary>
    public sealed class Fixture : IAsyncLifetime
    {
        internal MemoryObjectStore ByService { get; } = new MemoryObjectStore();

        internal MemoryObjectStore ByArrival { get; } = new MemoryObjectStore();

        public async ValueTask InitializeAsync()
        {
            List<Call> rows = Draw();
            await FillAsync(ByService, rows, [nameof(Call.Service), nameof(Call.At)], new ClusteringOptions
            {
                ObjectBytes = 1 << 20,
                BlockValue = ClusteringValue.Truncate(CalendarUnit.Hour),
            });
            await FillAsync(ByArrival, rows, [nameof(Call.At)], new ClusteringOptions { Value = ClusteringValue.Truncate(CalendarUnit.Hour) });
        }

        private static List<Call> Draw()
        {
            Random random = new Random(0xF0A);
            List<Call> rows = [];
            for (int service = 0; service < Large + Small; service++)
            {
                (string name, int calls) = service < Large ? ($"large-{service}", LargeCalls) : ($"small-{service - Large:D3}", SmallCalls);
                for (int i = 0; i < calls; i++)
                {
                    rows.Add(new Call(
                        name,
                        Noon.AddTicks(random.NextInt64(TimeSpan.FromHours(2).Ticks)),
                        $"h-{service}-{random.Next(3)}",
                        $"/svc{service}/r{random.Next(6)}",
                        (short)(random.Next(20) switch { < 14 => 200, < 17 => 404, < 19 => 500, _ => 503 }),
                        random.Next(4_000) / 4.0,
                        random.Next(1_000_000)));
                }
            }

            random.Shuffle(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(rows));
            return rows;
        }

        private static async Task FillAsync(MemoryObjectStore store, List<Call> rows, string[] key, ClusteringOptions clustering)
        {
            DatasetOptions options = new DatasetOptions
            {
                ClusteringKey = key,
                Clustering = clustering,
                ContentsObjectBytes = 1,
                SheetObjectBytes = 0,
            };
            await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Call.Schema, options, Ct);
            for (int from = 0; from < rows.Count; from += 25_000)
            {
                await using ObjectDraft draft = dataset.StartObject();
                await draft.Writer.WriteAsync<Call>(rows.Skip(from).Take(25_000).ToArray(), Ct);
                await dataset.AppendAsync(draft, Ct);
            }

            while (await dataset.CompactAsync(new CompactionOptions { LevelZeroCeiling = 1 }, Ct) is not null)
            {
            }
        }

        public async ValueTask DisposeAsync()
        {
            await ByService.DisposeAsync();
            await ByArrival.DisposeAsync();
        }
    }

    [VortexRecord]
    public partial record struct Call(string Service, DateTimeOffset At, string Host, string Route, short Status, double Latency, long Size);
}
