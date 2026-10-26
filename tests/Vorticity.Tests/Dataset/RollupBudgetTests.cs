// The questions the rollups answer over a month, held to a ratchet.
//
// WHAT IS HELD, per question: the requests and the bytes a fresh handle asks of the store, its open apart, and an answer
// equal to the rows'. The dataset is 30 days of hits on 2,000 routes, cut by the day, an object a day, rolled up with a
// fanout of 4: rollups of rank 2 and up keep their days apart. Each ceiling sits at what its question costs, lowered by
// hand in the commit that earns it.
//
// THE QUESTIONS: every route's count over the month, which the totals of the top rollups answer whole; one route's count
// by the day, which their counts by bucket answer, each read by its header and the chunk that holds the route; and the
// same over days 3 to 26, whose edges keep a few of a rollup's buckets.
//
// AT THE REFERENCE SCALE (a day a value, objects of 1 GB, a fanout of 64, 19,000 routes over 13 months, the design note's
// section 4.5): ~21 rollups of rank 2 cover the 13 months, each ~19 days. One route by the day reads, in each, the
// header of its counts by bucket, a directory entry of ~40 bytes a 16 KiB chunk of 23 MB, ~56 KB, then the chunk that
// holds the route: 2 requests and ~72 KB a rollup, ~42 requests and ~1.5 MB in all, besides the tree of rollups and the
// open tail. Every route reads the totals of the same rollups whole, ~1.2 MB each: ~21 requests and ~25 MB.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed partial class RollupBudgetTests : IClassFixture<RollupBudgetTests.Fixture>
{
    private static readonly DateTimeOffset Midnight = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);

    private const int Days = 30;

    private const int RowsPerDay = 8_000;

    private const int Routes = 2_000;

    /// <summary>
    /// The ceilings of each question: requests and bytes, measured on 2026-10-25. Every route: the totals of 3 rollups
    /// whole and the counts of the last day's object, which the open run leaves out. One route by the day: the counts by
    /// bucket of 3 rollups, each by its header and a chunk, and that object's counts. Over days 3 to 26: 2 rollups so.
    /// </summary>
    private static readonly Dictionary<string, (long Requests, long Bytes)> Ceilings = new(StringComparer.Ordinal)
    {
        ["count by route"] = (4, 261_698),
        ["count by day, one route"] = (7, 115_755),
        ["count by day, one route, days 3 to 26"] = (4, 35_025),
    };

    private readonly Fixture _fixture;

    public RollupBudgetTests(Fixture fixture) => _fixture = fixture;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string> Questions() => [.. Ceilings.Keys];

    [Theory]
    [MemberData(nameof(Questions))]
    public async Task AQuestionStaysWithinItsBudget(string question)
    {
        (long requestsCeiling, long bytesCeiling) = Ceilings[question];
        await using CountingObjectStore store = new CountingObjectStore(_fixture.Store);
        await using VortexDataset dataset = await VortexDataset.OpenAsync(store, Options(), Ct);
        List<string> rows = await AskAsync(dataset, question, synopses: false);
        ScanPlan plan = await Ask(dataset, question, synopses: true).ExplainAsync(Ct);
        await using CountingObjectStore fresh = new CountingObjectStore(_fixture.Store);
        await using VortexDataset asked = await VortexDataset.OpenAsync(fresh, Options(), Ct);
        fresh.Reset();
        List<string> states = await AskAsync(asked, question, synopses: true);
        (long requests, long bytes) = (fresh.Requests, fresh.BytesRead);

        int rollups = plan.States.Count(step => step.Read == "rollup");
        int byBucket = plan.States.Count(step => step.Read == "rollup" && step.Detail.Contains("by bucket", StringComparison.Ordinal));
        int inPart = plan.States.Count(step => step.Read == "rollup" && step.Detail.StartsWith("the header", StringComparison.Ordinal));
        Console.WriteLine(
            $"ROLLUP BUDGET {question}: {rollups} rollups, {byBucket} by bucket, {inPart} in part, {plan.States.Length - rollups} objects' counts; " +
            $"{requests} requests and {bytes} bytes (ceilings {Shown(requestsCeiling)}, {Shown(bytesCeiling)}), {states.Count} rows answered");
        Assert.NotEmpty(rows);
        Assert.Equal(rows, states);
        Assert.True(requests <= requestsCeiling, $"{question}: {requests} requests, over {requestsCeiling}");
        Assert.True(bytes <= bytesCeiling, $"{question}: {bytes} bytes, over {bytesCeiling}");
    }

    private static string Shown(long ceiling) => ceiling == long.MaxValue ? "none" : ceiling.ToString(System.Globalization.CultureInfo.InvariantCulture);

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
        string route = Route(17);
        DateTimeOffset from = Midnight.AddDays(3);
        DateTimeOffset to = Midnight.AddDays(27);
        return question switch
        {
            "count by route" => scan.GroupBy(h => h.Route).Select(g => (g.Key, g.Count())),
            "count by day, one route" => scan.Where(h => h.Route == route).GroupBy(h => h.At.Truncate(CalendarUnit.Day)).Select(g => (g.Key, g.Count())),
            "count by day, one route, days 3 to 26" => scan.Where(h => h.Route == route && h.At >= from && h.At < to)
                .GroupBy(h => h.At.Truncate(CalendarUnit.Day)).Select(g => (g.Key, g.Count())),
            _ => throw new ArgumentException($"No question named '{question}'.", nameof(question)),
        };
    }

    private static string Route(int route) => $"/api/v1/routes/{route:D4}";

    private static DatasetOptions Options() => new DatasetOptions
    {
        Seed = 0x5EED_0F_A13,
        SheetObjectBytes = 0,
        ContentsObjectBytes = 0,
        RollupFanout = 4,
    };

    /// <summary>Thirty days of hits on 2,000 routes, appended by the half day, cut by the day, compacted and rolled up.</summary>
    public sealed class Fixture : IAsyncLifetime
    {
        public MemoryObjectStore Store { get; } = new MemoryObjectStore();

        public async ValueTask InitializeAsync()
        {
            await using VortexDataset dataset = await VortexDataset.CreateAsync(Store, Hit.Schema, Options() with
            {
                ClusteringKey = [nameof(Hit.At)],
                Clustering = new ClusteringOptions { Value = ClusteringValue.Truncate(CalendarUnit.Day) },
            });
            Random random = new Random(37);
            int part = RowsPerDay / 2;
            for (int row = 0; row < Days * RowsPerDay; row += part)
            {
                Hit[] hits = new Hit[part];
                for (int i = 0; i < part; i++)
                {
                    hits[i] = new Hit(Midnight.AddSeconds((row + i) * 86_400.0 / RowsPerDay), Route(random.Next(Routes)), random.NextInt64(100, 100_000));
                }

                await using ObjectDraft draft = dataset.StartObject();
                await draft.Writer.WriteAsync<Hit>(hits);
                await dataset.AppendAsync(draft);
            }

            while (await dataset.CompactAsync(new CompactionOptions { LevelZeroCeiling = 1 }) is not null)
            {
            }

            int put = await dataset.RollUpAsync();
            Console.WriteLine($"ROLLUP BUDGET {dataset.Snapshot.Levels.Occupied().Sum(level => level.Tree.Entries)} objects, {put} rollups");
        }

        public async ValueTask DisposeAsync() => await Store.DisposeAsync();
    }

    [VortexRecord]
    public partial record struct Hit(DateTimeOffset At, string Route, long Bytes);
}
