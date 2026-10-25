// Aggregations a dataset's objects answer from their columns' statistics over their blocks, where their counts do not:
// the blocks whose columns of the question hold one value each, or one varying under a question of counts, give states;
// the others are read as rows.
//
// THE ORACLE IS THE ROWS: every question gives the same answer whether the blocks answer it or every object is read as
// rows; and a question of errors by service reads a fraction of the bytes its rows take.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.IO;
using Vorticity.Tests.Writing;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed partial class DatasetBlockAnswerTests : IClassFixture<DatasetBlockAnswerTests.Fixture>
{
    private static readonly DateTimeOffset Noon = new DateTimeOffset(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);

    private readonly Fixture _fixture;

    public DatasetBlockAnswerTests(Fixture fixture) => _fixture = fixture;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    internal static readonly string[] Named =
    [
        "5xx by service", "5xx by service and hour", "count by service and status", "measures by service",
        "count by route, one service", "measures by service and hour", "count by host", "measures by service under 5xx",
        "count by status, a latency window", "count by service, two statuses",
    ];

    public static TheoryData<string> Questions() => [.. Named];

    public static TheoryData<string, bool> QuestionsByLayout()
    {
        TheoryData<string, bool> questions = [];
        foreach (string question in Named)
        {
            questions.Add(question, false);
            questions.Add(question, true);
        }

        return questions;
    }

    /// <summary>Each question gives the rows' answer through its blocks, whether they end at each hour or not.</summary>
    [Theory]
    [MemberData(nameof(QuestionsByLayout))]
    public async Task AQuestionAnswersAsItsRowsDo(string question, bool hourly)
    {
        await using VortexDataset dataset = await VortexDataset.OpenAsync(hourly ? _fixture.Hourly : _fixture.Store, new DatasetOptions(), Ct);
        List<string> rows = await AskAsync(dataset, question, synopses: false);
        List<string> blocks = await AskAsync(dataset, question, synopses: true);
        Assert.NotEmpty(rows);
        Assert.Equal(rows, blocks);
    }

    /// <summary>
    /// Errors by service, each service a run of blocks and the status listed in each: the blocks answer it from the
    /// statistics of the service and the status alone, a quarter of the bytes the rows of both columns take or less. By
    /// the hour as well, the blocks whose instants fall in one hour answer, and the others, some fifty minutes each here,
    /// are read as rows: never more than the rows take.
    /// </summary>
    [Theory]
    [InlineData("5xx by service", 4, false)]
    [InlineData("5xx by service and hour", 1, false)]
    [InlineData("5xx by service and hour", 4, true)]
    [InlineData("measures by service and hour", 4, true)]
    public async Task ErrorsByServiceReadTheirBlocksStatistics(string question, int fewer, bool hourly)
    {
        await using CountingObjectStore store = new CountingObjectStore(hourly ? _fixture.Hourly : _fixture.Store);
        long rowBytes;
        long blockBytes;
        await using (VortexSession session = VortexSession.Create(_ => { }))
        await using (VortexDataset dataset = await VortexDataset.OpenAsync(store, new DatasetOptions { Session = session }, Ct))
        {
            store.Reset();
            await AskAsync(dataset, question, synopses: false);
            rowBytes = store.BytesRead;
        }

        await using (VortexSession session = VortexSession.Create(_ => { }))
        await using (VortexDataset dataset = await VortexDataset.OpenAsync(store, new DatasetOptions { Session = session }, Ct))
        {
            store.Reset();
            await AskAsync(dataset, question, synopses: true);
            blockBytes = store.BytesRead;
        }

        Console.WriteLine($"BLOCK ANSWER {question}{(hourly ? ", blocks ending at each hour" : string.Empty)}: {blockBytes:N0} bytes through the blocks, {rowBytes:N0} through the rows");
        Assert.True(blockBytes * fewer < rowBytes, $"{question}: {blockBytes} bytes through the blocks, {rowBytes} through the rows");

        // The plan says so object by object: the blocks' path, its requests and bytes, and the rows' it was weighed against.
        await using VortexDataset explained = await VortexDataset.OpenAsync(store, new DatasetOptions(), Ct);
        ScanPlan plan = await Ask(explained, question, synopses: true).ExplainAsync(Ct);
        StatesStep[] blocks = [.. plan.States.Where(step => step.Read == "blocks")];
        Console.WriteLine($"BLOCK PLAN {question}: {blocks.Length} objects, {blocks.FirstOrDefault()?.Detail}");
        Assert.NotEmpty(blocks);
        Assert.All(blocks, step => Assert.True(step.Count >= 1, step.Detail));
    }

    /// <summary>
    /// Each object a job writes says the bytes of each of its columns in its table of contents, what reading its rows costs:
    /// together no more than the object, and the wide columns more than the narrow.
    /// </summary>
    [Fact]
    public async Task AnObjectSaysEachColumnsBytes()
    {
        await using VortexDataset dataset = await VortexDataset.OpenAsync(_fixture.Store, new DatasetOptions(), Ct);
        int objects = 0;
        await foreach (PositionedObject held in dataset.ScanBuilder().ObjectsAsync(Ct))
        {
            if (held.Entry.Contents is not { ColumnBytes.Count: > 0 } contents)
            {
                continue;
            }

            objects++;
            Assert.Equal(Call.Schema.Count, contents.ColumnBytes.Count);
            Assert.All(contents.ColumnBytes, bytes => Assert.True(bytes > 0));
            Assert.True(contents.ColumnBytes.Sum() < held.Entry.Bytes, $"{contents.ColumnBytes.Sum()} bytes of columns in an object of {held.Entry.Bytes}");

            // The instant and the size, of many values each, weigh more than the status, of four.
            Assert.True(contents.ColumnBytes[1] > contents.ColumnBytes[4] && contents.ColumnBytes[6] > contents.ColumnBytes[4]);
        }

        Assert.True(objects >= 2, $"{objects} objects say their columns' bytes");
    }

    /// <summary>
    /// Questions drawn at random over the columns, named at run time: none, one or two keys, counts or measures, and a
    /// filter of statuses, values, sets, a window of latencies, conjunctions and disjunctions of them; each gives the rows'
    /// answer through the blocks.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task ADrawnQuestionAnswersAsItsRowsDo(int seed)
    {
        Random random = new Random(seed);
        string[] columns = ["Service", "Host", "Route", "Status"];
        string[] measures = ["sum(Size)", "min(Latency)", "max(Latency)", "avg(Latency)", "min(Size)"];
        string[] filters =
        [
            "Status >= 500", "Status = 404", "Route = '/svc1/r2'", "Service = 'svc-2'", "Host in ('h-1-0', 'h-2-1')",
            "Latency >= 100 and Latency < 200", "Service = 'svc-3' and Status >= 500", "Status = 503 or Route = '/svc0/r1'",
        ];
        await using VortexDataset dataset = await VortexDataset.OpenAsync(seed % 2 == 0 ? _fixture.Hourly : _fixture.Store, new DatasetOptions(), Ct);
        for (int question = 0; question < 12; question++)
        {
            string[] keys = [.. columns.OrderBy(_ => random.Next()).Take(random.Next(3))];
            List<string> results = [.. keys, "count()"];
            if (random.Next(2) == 0)
            {
                results.Add(measures[random.Next(measures.Length)]);
            }

            string? filter = random.Next(4) == 0 ? null : filters[random.Next(filters.Length)];
            List<string> rows = await DatasetPlannerTests.AskAsync(dataset, keys, [.. results], filter, synopses: false);
            List<string> blocks = await DatasetPlannerTests.AskAsync(dataset, keys, [.. results], filter, synopses: true);
            Assert.True(rows.SequenceEqual(blocks), $"by {string.Join(", ", keys)}, {string.Join(", ", results)}, under {filter}: {blocks.Count} groups through the blocks, {rows.Count} through the rows");
        }
    }

    private static async Task<List<string>> AskAsync(VortexDataset dataset, string question, bool synopses)
    {
        List<string> rows = [];
        await using (IAsyncEnumerator<RecordBatch> batches = Ask(dataset, question, synopses).Query.Batches(Ct))
        {
            while (await batches.MoveNextAsync())
            {
                Values.DescribeRows(batches.Current, rows);
            }
        }

        rows.Sort(StringComparer.Ordinal);
        return rows;
    }

    private static Vorticity.Aggregation Ask(VortexDataset dataset, string question, bool synopses)
    {
        Scan<Call> scan = dataset.Scan<Call>().With(new ScanOptions { UseSynopses = synopses });
        return question switch
        {
            "5xx by service" => scan.Where(c => c.Status >= 500).GroupBy(c => c.Service).Select(g => (g.Key, g.Count())),
            "5xx by service and hour" => scan.Where(c => c.Status >= 500).GroupBy(c => (c.Service, c.At.Truncate(CalendarUnit.Hour)))
                .Select(g => (g.Key.Service, g.Key.Item2, g.Count())),
            "count by service and status" => scan.GroupBy(c => (c.Service, c.Status)).Select(g => (g.Key.Service, g.Key.Status, g.Count())),
            "measures by service" => scan.GroupBy(c => c.Service)
                .Select(g => (g.Key, g.Count(), g.Sum(c => c.Size), g.Min(c => c.Latency), g.Max(c => c.Latency), g.Average(c => c.Latency))),
            "count by route, one service" => scan.Where(c => c.Service == "svc-1").GroupBy(c => c.Route).Select(g => (g.Key, g.Count())),
            "measures by service and hour" => scan.GroupBy(c => (c.Service, c.At.Truncate(CalendarUnit.Hour)))
                .Select(g => (g.Key.Service, g.Key.Item2, g.Count(), g.Sum(c => c.Size), g.Average(c => c.Latency))),
            "count by host" => scan.GroupBy(c => c.Host).Select(g => (g.Key, g.Count())),
            "measures by service under 5xx" => scan.Where(c => c.Status >= 500).GroupBy(c => c.Service).Select(g => (g.Key, g.Count(), g.Average(c => c.Latency))),
            "count by status, a latency window" => scan.Where(c => c.Latency >= 100 && c.Latency < 200).GroupBy(c => c.Status).Select(g => (g.Key, g.Count())),
            "count by service, two statuses" => scan.Where(c => c.Status.In(500, 503)).GroupBy(c => c.Service).Select(g => (g.Key, g.Count())),
            _ => throw new ArgumentException($"No question named '{question}'.", nameof(question)),
        };
    }

    /// <summary>
    /// Four services, each 150 000 calls over two hours from noon, from three hosts of its own, on six routes, keyed by the
    /// service then the instant, appended in parts and compacted into objects of 4 MiB: each service a run of blocks of
    /// some 65 000 rows, about fifty minutes each. The same calls again in a dataset whose clustering declares a block
    /// value, the instant's hour, the application's choice: its blocks end at each hour too.
    /// </summary>
    public sealed class Fixture : IAsyncLifetime
    {
        internal MemoryObjectStore Store { get; } = new MemoryObjectStore();

        internal MemoryObjectStore Hourly { get; } = new MemoryObjectStore();

        public async ValueTask InitializeAsync()
        {
            List<Call> rows = Draw();
            await FillAsync(Store, rows, new ClusteringOptions { ObjectBytes = 4 << 20 });
            await FillAsync(Hourly, rows, new ClusteringOptions { ObjectBytes = 4 << 20, BlockValue = ClusteringValue.Truncate(CalendarUnit.Hour) });
        }

        private static List<Call> Draw()
        {
            Random random = new Random(0xD4B);
            List<Call> rows = [];
            for (int service = 0; service < 4; service++)
            {
                for (int i = 0; i < 150_000; i++)
                {
                    rows.Add(new Call(
                        $"svc-{service}",
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

        private static async Task FillAsync(MemoryObjectStore store, List<Call> rows, ClusteringOptions clustering)
        {
            DatasetOptions options = new DatasetOptions
            {
                ClusteringKey = [nameof(Call.Service), nameof(Call.At)],
                Clustering = clustering,
                ContentsObjectBytes = 1,
            };
            await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Call.Schema, options, Ct);
            for (int from = 0; from < rows.Count; from += 100_000)
            {
                await using ObjectDraft draft = dataset.StartObject();
                await draft.Writer.WriteAsync<Call>(rows.Skip(from).Take(100_000).ToArray(), Ct);
                await dataset.AppendAsync(draft, Ct);
            }

            while (await dataset.CompactAsync(new CompactionOptions { LevelZeroCeiling = 1 }, Ct) is not null)
            {
            }
        }

        public async ValueTask DisposeAsync()
        {
            await Store.DisposeAsync();
            await Hourly.DisposeAsync();
        }
    }

    [VortexRecord]
    public partial record struct Call(string Service, DateTimeOffset At, string Host, string Route, short Status, double Latency, long Size);
}
