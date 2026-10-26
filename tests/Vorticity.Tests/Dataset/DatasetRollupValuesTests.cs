// The rollups' totals of a column of many values, read in part under the few values a question asks.
//
// WHAT IS HELD: over six hours of hits on 1,000 routes, each with four measures, cut by the hour and rolled up, a question
// of one route or two by the hour takes the rollups that hold an hour, and reads of each its totals' header, then the
// chunks that hold the routes asked, side by side with their sketches' for a quantile, two requests a rollup; its answer
// is the rows'. A count of one route reads less than half the bytes of the rollups' totals whole, and a second ask of the
// handle reads nothing. A question of every route reads the totals whole.
//
// WHY ONLY ONE ROUTE'S BYTES: a read bridges segments under 1 MiB apart, so that two routes' chunks, or a chunk of the
// counts and those of their sketches, which follow them, come with what lies between; totals of megabytes, a day's of a
// busy service, leave them apart.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed partial class DatasetRollupValuesTests : IClassFixture<DatasetRollupValuesTests.Fixture>
{
    private static readonly DateTimeOffset Noon = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);

    private const int Hours = 6;

    private const int RowsPerHour = 24_000;

    private const int Routes = 1_000;

    private readonly Fixture _fixture;

    public DatasetRollupValuesTests(Fixture fixture) => _fixture = fixture;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string> Questions() =>
    [
        "count by hour, one route", "bytes by hour, two routes", "median bytes by hour, one route", "count by route",
    ];

    [Theory]
    [MemberData(nameof(Questions))]
    public async Task AQuestionOfAFewValuesReadsTheirChunksAlone(string question)
    {
        await using CountingObjectStore store = new CountingObjectStore(_fixture.Store);
        await using VortexDataset dataset = await VortexDataset.OpenAsync(store, Options(), Ct);
        List<string> rows = await AskAsync(dataset, question, synopses: false);
        ScanPlan plan = await Ask(dataset, question, synopses: true).ExplainAsync(Ct);
        store.Reset();
        List<string> states = await AskAsync(dataset, question, synopses: true);
        (long requests, long bytes) = (store.Requests, store.BytesRead);
        store.Reset();
        Assert.Equal(states, await AskAsync(dataset, question, synopses: true));
        long again = store.Requests;

        StatesStep[] rolled = [.. plan.States.Where(step => step.Read == "rollup")];
        int inPart = rolled.Count(step => step.Detail.StartsWith("the header of the totals", StringComparison.Ordinal));
        long whole = 0;
        foreach (RollupEntry rollup in await RollupsAsync(dataset))
        {
            if (rolled.Any(step => step.Object == rollup.Key))
            {
                whole += rollup.Columns.Sum(column => column.Counts.Length + (question.StartsWith("median", StringComparison.Ordinal) ? column.Sketches?.Length ?? 0 : 0));
            }
        }

        Console.WriteLine(
            $"ROLLUP VALUES {question}: {rolled.Length} rollups, {inPart} read in part, {plan.States.Length - rolled.Length} other steps; " +
            $"{requests} requests, {bytes} bytes against {whole} for the rollups' regions whole; {again} requests again");
        Assert.NotEmpty(rows);
        Assert.Equal(rows, states);
        Assert.Equal(0, again);
        Assert.NotEmpty(rolled);
        if (question == "count by route")
        {
            Assert.Equal(0, inPart);
        }
        else
        {
            Assert.Equal(rolled.Length, inPart);
            Assert.Equal((2 * rolled.Length) + plan.States.Length - rolled.Length, requests);
        }

        if (question == "count by hour, one route")
        {
            Assert.True(bytes * 2 < whole, $"{bytes} bytes read, {whole} for the rollups' regions whole");
        }
    }

    private static async Task<List<RollupEntry>> RollupsAsync(VortexDataset dataset)
    {
        List<RollupEntry> rollups = [];
        await foreach (TreeEntry entry in dataset.Snapshot.Levels.Rollups.EnumerateAsync(dataset.Snapshot.Pages, Ct))
        {
            rollups.Add(RollupEntry.FromBytes(entry.Key.Span, entry.Value.Span));
        }

        return rollups;
    }

    /// <summary>The groups <paramref name="question"/> answers, each a line of its values, sorted.</summary>
    private static async Task<List<string>> AskAsync(VortexDataset dataset, string question, bool synopses)
    {
        List<string> rows = [];
        await using (IAsyncEnumerator<RecordBatch> batches = Ask(dataset, question, synopses).Query.Batches(Ct))
        {
            while (await batches.MoveNextAsync())
            {
                Vorticity.Tests.Writing.Values.DescribeRows(batches.Current, rows);
            }
        }

        rows.Sort(StringComparer.Ordinal);
        return rows;
    }

    private static Vorticity.Aggregation Ask(VortexDataset dataset, string question, bool synopses)
    {
        Scan<Hit> scan = dataset.Scan<Hit>().With(new ScanOptions { UseSynopses = synopses });
        string one = Route(17);
        string two = Route(1_234);
        return question switch
        {
            "count by hour, one route" => scan.Where(h => h.Route == one).GroupBy(h => h.At.Truncate(CalendarUnit.Hour)).Select(g => (g.Key, g.Count())),
            "bytes by hour, two routes" => scan.Where(h => h.Route == one || h.Route == two).GroupBy(h => (h.Route, h.At.Truncate(CalendarUnit.Hour)))
                .Select(g => (g.Key.Route, g.Key.Item2, g.Count(), g.Sum(h => h.Bytes))),
            "median bytes by hour, one route" => scan.Where(h => h.Route == one).GroupBy(h => h.At.Truncate(CalendarUnit.Hour))
                .Select(g => (g.Key, g.Quantile(h => h.Bytes, 0.5))),
            "count by route" => scan.GroupBy(h => h.Route).Select(g => (g.Key, g.Count())),
            _ => throw new ArgumentException($"No question named '{question}'.", nameof(question)),
        };
    }

    private static string Route(int route) => $"/api/v1/routes/{route:D4}";

    private static DatasetOptions Options() => new DatasetOptions
    {
        Seed = 0x5EED_0F_A12,
        SheetObjectBytes = 0,
        ContentsObjectBytes = 0,
        RollupFanout = 2,
    };

    /// <summary>
    /// Six hours of hits on 1,000 routes, appended by the half hour, cut by the hour, compacted to the end and rolled up: an
    /// object an hour in the deepest level, each counting its routes.
    /// </summary>
    public sealed class Fixture : IAsyncLifetime
    {
        public MemoryObjectStore Store { get; } = new MemoryObjectStore();

        public async ValueTask InitializeAsync()
        {
            await using VortexDataset dataset = await VortexDataset.CreateAsync(Store, Hit.Schema, Options() with
            {
                ClusteringKey = [nameof(Hit.At)],
                Clustering = new ClusteringOptions { Value = ClusteringValue.Truncate(CalendarUnit.Hour) },
            });
            Random random = new Random(31);
            int part = RowsPerHour / 2;
            for (int row = 0; row < Hours * RowsPerHour; row += part)
            {
                Hit[] hits = new Hit[part];
                for (int i = 0; i < part; i++)
                {
                    hits[i] = new Hit(
                        Noon.AddSeconds((row + i) * 3_600.0 / RowsPerHour),
                        Route(random.Next(Routes)),
                        (short)(random.Next(10) < 8 ? 200 : 500),
                        random.NextInt64(100, 100_000),
                        Math.Round(random.NextDouble() * 250, 3),
                        random.NextInt64(1_000, 1_000_000),
                        random.NextInt64(1_000, 1_000_000));
                }

                await using ObjectDraft draft = dataset.StartObject();
                await draft.Writer.WriteAsync<Hit>(hits);
                await dataset.AppendAsync(draft);
            }

            while (await dataset.CompactAsync(new CompactionOptions { LevelZeroCeiling = 1 }) is not null)
            {
            }

            int put = await dataset.RollUpAsync();
            Console.WriteLine($"ROLLUP VALUES {dataset.Snapshot.Levels.Occupied().Sum(level => level.Tree.Entries)} objects, {put} rollups");
        }

        public async ValueTask DisposeAsync() => await Store.DisposeAsync();
    }

    [VortexRecord]
    public partial record struct Hit(DateTimeOffset At, string Route, short Status, long Bytes, double Latency, long Sent, long Received);
}
