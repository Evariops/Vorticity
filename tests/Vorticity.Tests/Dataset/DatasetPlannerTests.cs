// Questions on one column answered from the objects' counts, against the same questions read from the rows.
//
// WHAT IS HELD: a question on one column and the measures, its keys the column and functions of another column each
// object holds constant, the instant truncated to the hour or coarser here, its aggregates counts, sums, extremes,
// means, quantiles and distinct counts, its filter a window on the instant and values of the column, gives the same
// answer whether the objects' counts answer it as states or every object is read as rows; a window that cuts an object
// reads that object's rows; and the plan says, object by object, which objects their counts answer, at one request
// each. So do questions drawn at random on tables drawn at random, columns of every kind with nulls among them, through
// the path whose columns are named at run time.
//
// THE DATASET: four hours of calls, an object an hour once compacted, each with its columns' counts; then the first ten
// minutes of the next hour, still in their appends, which keep no counts and are read as rows.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.Tests.Writing;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed partial class DatasetPlannerTests : IClassFixture<DatasetPlannerTests.Fixture>
{
    private static readonly DateTimeOffset Noon = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);

    private static readonly string[] Services = ["api", "auth", "billing", "cart", "search", "media"];

    private const int Hours = 4;

    private const int RowsPerHour = 12_000;

    private readonly Fixture _fixture;

    public DatasetPlannerTests(Fixture fixture) => _fixture = fixture;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The questions <see cref="Ask(VortexDataset, string, bool)"/> knows by name.</summary>
    internal static readonly string[] Named =
    [
        "count by service", "count by service and hour", "measures by service", "count by day", "count by hour, one service",
        "count by service, an aligned window", "measures by service, an unaligned window", "quantiles by service",
        "distinct services by hour", "count by route and day",
    ];

    public static TheoryData<string> Questions() => [.. Named];

    [Theory]
    [MemberData(nameof(Questions))]
    public async Task AQuestionOnOneColumnAnswersAsItsRowsDo(string question)
    {
        await using CountingObjectStore store = new CountingObjectStore(_fixture.Store);
        await using VortexDataset dataset = await VortexDataset.OpenAsync(store, Options(), Ct);
        store.Reset();
        List<string> rows = await AskAsync(dataset, question, synopses: false);
        long rowBytes = store.BytesRead;
        store.Reset();
        List<string> states = await AskAsync(dataset, question, synopses: true);
        (long requests, long bytes) = (store.Requests, store.BytesRead);
        ScanPlan plan = await Ask(dataset, question, synopses: true).ExplainAsync(Ct);
        Console.WriteLine($"PLANNER {question}: {rowBytes} bytes read as rows, {bytes} in {requests} requests with the counts of " +
            $"{plan.States.Count(step => step.Read == "states")} objects");
        Assert.True(bytes < rowBytes, $"{question}: {bytes} bytes with the counts, against {rowBytes} as rows");
        Assert.NotEmpty(rows);
        Assert.Equal(rows, states);
        Assert.Contains(plan.States, step => step.Read == "states");
    }

    [Fact]
    public async Task AWindowThatCutsAnObjectReadsItsRows()
    {
        // Cut at 37.5 and 155.25 minutes, the window holds the second hour's object whole, whose counts answer, and reads the
        // rows of the two it cuts; on the hours, it holds its two objects whole.
        await using VortexDataset dataset = await VortexDataset.OpenAsync(_fixture.Store, Options(), Ct);
        ScanPlan plan = await Ask(dataset, "measures by service, an unaligned window", synopses: true).ExplainAsync(Ct);
        Assert.Equal(1, plan.States.Count(step => step.Read == "states"));
        ScanPlan aligned = await Ask(dataset, "count by service, an aligned window", synopses: true).ExplainAsync(Ct);
        Assert.Equal(2, aligned.States.Count(step => step.Read == "states"));
    }

    // Questions drawn at random on the tables DatasetCountsTests draws, columns of every kind and nulls among them: a key
    // column or none, counts, sums, extremes, means and the key's distinct values, under a window of the instant, on the
    // hour or not, and values of the key, through the path whose columns are named at run time.
    [Theory]
    [InlineData(21)]
    [InlineData(22)]
    [InlineData(23)]
    [InlineData(24)]
    public async Task AQuestionDrawnAtRandomAnswersAsItsRowsDo(int seed)
    {
        Random random = new Random(seed);
        DatasetCountsTests.Drawn[] columns = DatasetCountsTests.Draw(random);
        object?[][] rows = DatasetCountsTests.Rows(columns, random);
        await using MemoryObjectStore memory = new MemoryObjectStore();
        await using CountingObjectStore store = new CountingObjectStore(memory);
        await using VortexDataset dataset = await DatasetCountsTests.WriteAsync(store, columns, rows);
        const int Questions = 24;
        int answered = 0;
        for (int question = 0; question < Questions; question++)
        {
            (string[] keys, string[] results, string? filter) = Draw(random, columns, rows);
            string asked = $"seed {seed}: by ({string.Join(", ", keys)}) {string.Join(", ", results)} where {filter ?? "true"}";
            List<string> read = await AskAsync(dataset, keys, results, filter, synopses: false);
            List<string> counted = await AskAsync(dataset, keys, results, filter, synopses: true);
            Assert.True(read.SequenceEqual(counted), $"{asked}: {read.Count} groups as rows, {counted.Count} with the counts, " +
                $"first apart: '{read.Except(counted).FirstOrDefault()}' against '{counted.Except(read).FirstOrDefault()}'");
            ScanPlan plan = await Ask(dataset, keys, results, filter, synopses: true).ExplainAsync(Ct);
            answered += plan.States.Any(step => step.Read == "states") ? 1 : 0;
        }

        // A window that cuts every object it reaches reads their rows, and so does a number column of many values, which no
        // counts keep: the counts answer a third of the questions at least.
        Console.WriteLine($"PLANNER seed {seed}: the counts answered {answered} questions of {Questions}");
        Assert.True(answered >= Questions / 3, $"seed {seed}: the counts answered {answered} questions of {Questions}");
    }

    // An object written before the schema moved its columns holds its synopses by its own columns' places: its sheets and
    // its counts answer for none of the version's columns, and it is read as rows.
    [Fact]
    public async Task AnObjectOfAnEarlierSchemaReadsItsRows()
    {
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Call.Schema, Options() with
        {
            ClusteringKey = [nameof(Call.At)],
            Clustering = new ClusteringOptions { Value = ClusteringValue.Truncate(CalendarUnit.Hour) }, ContentsObjectBytes = 0,
        }, Ct);
        Random random = new Random(87);
        for (int part = 0; part < 24; part++)
        {
            Call[] rows = new Call[1_000];
            for (int i = 0; i < rows.Length; i++)
            {
                int service = random.Next(Services.Length);
                rows[i] = new Call(
                    Noon.AddSeconds(((part * 1_000) + i) * 0.3), Services[service], $"/{Services[service]}/{random.Next(40)}",
                    random.NextInt64(1_000, 5_000_000), Math.Round(random.NextDouble() * 100, 2));
            }

            await using ObjectDraft draft = dataset.StartObject();
            await draft.Writer.WriteAsync<Call>(rows, Ct);
            await dataset.AppendAsync(draft, Ct);
        }

        while (await dataset.CompactAsync(new CompactionOptions { LevelZeroCeiling = 2 }, Ct) is not null)
        {
        }

        // The route before the service: each object written before holds the service where the version holds the route.
        static VortexField Field(string name) => Call.Schema[Call.Schema.IndexOf(name)];
        await dataset.EvolveSchemaAsync(VortexSchema.Create([
            Field(nameof(Call.At)), Field(nameof(Call.Route)), Field(nameof(Call.Service)), Field(nameof(Call.DurationNs)), Field(nameof(Call.Weight))]),
            cancellationToken: Ct);
        foreach ((string[] keys, string[] results, string? filter) in ((string[], string[], string?)[])[
            ([nameof(Call.Service)], [nameof(Call.Service), "count()", "sum(DurationNs)"], null),
            ([nameof(Call.Service)], [nameof(Call.Service), "count()"], "Service = 'api'"),
            ([nameof(Call.Route)], [nameof(Call.Route), "count()"], "Route in ('/api/3', '/cart/7')")])
        {
            // Every object's rows, with no statistic read, against the sheets, which the rows' scan reads too, then the counts.
            List<string> read = await AskAsync(dataset, keys, results, filter, synopses: false, statistics: false);
            Assert.NotEmpty(read);
            Assert.Equal(read, await AskAsync(dataset, keys, results, filter, synopses: false));
            Assert.Equal(read, await AskAsync(dataset, keys, results, filter, synopses: true));
        }
    }

    /// <summary>A question on <paramref name="columns"/>: its key, its results, and its filter as text, null for none.</summary>
    internal static (string[] Keys, string[] Results, string? Filter) Draw(Random random, DatasetCountsTests.Drawn[] columns, object?[][] rows)
    {
        int key = random.Next(4) == 0 ? -1 : random.Next(columns.Length);
        string[] keys = key < 0 ? [] : [$"c{key}"];
        List<string> results = [.. keys, "count()"];
        string[] functions = ["sum", "min", "max", "avg"];
        for (int m = 0; m < columns.Length; m++)
        {
            string result = $"{functions[random.Next(functions.Length)]}(c{m})";
            if (m != key && columns[m].Number && random.Next(3) == 0 && !results.Contains(result))
            {
                results.Add(result);
            }
        }

        if (key >= 0 && random.Next(4) == 0)
        {
            results.Add($"count_distinct(c{key})");
        }

        // A window of the instant, on the hour or cutting one; values of the key, one or a set; both, or neither.
        List<string> conjuncts = [];
        int shape = random.Next(5);
        if (shape is 1 or 3)
        {
            bool aligned = random.Next(2) == 0;
            DateTimeOffset from = aligned
                ? DatasetCountsTests.Noon.AddHours(random.Next(DatasetCountsTests.Hours))
                : DatasetCountsTests.Noon.AddMinutes(random.Next(DatasetCountsTests.Hours * 60));
            DateTimeOffset to = aligned ? from.AddHours(1 + random.Next(2)) : from.AddMinutes(1 + random.Next(150));
            conjuncts.Add(string.Create(
                System.Globalization.CultureInfo.InvariantCulture, $"At >= '{from:O}' and At < '{to:O}'"));
        }

        if (shape is 2 or 3 && key >= 0)
        {
            string[] values = [.. Enumerable.Range(0, 1 + random.Next(3)).Select(_ => rows[random.Next(rows.Length)][key + 1])
                .Where(value => value is not null).Select(Literal).Distinct()];
            if (values.Length > 0)
            {
                conjuncts.Add(values.Length == 1 ? $"c{key} = {values[0]}" : $"c{key} in ({string.Join(", ", values)})");
            }
        }

        return (keys, [.. results], conjuncts.Count == 0 ? null : string.Join(" and ", conjuncts));
    }

    /// <summary><paramref name="value"/> as the filter's grammar writes it: a text quoted, a number as it reads back.</summary>
    private static string Literal(object? value) => value switch
    {
        string text => $"'{text}'",
        IFormattable number => number.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
        _ => throw new ArgumentException($"No literal for {value}.", nameof(value)),
    };

    /// <summary>The groups the question answers, each a line of its values, sorted; on <paramref name="lanes"/> lanes, 0 for the session's.</summary>
    internal static async Task<List<string>> AskAsync(
        VortexDataset dataset, string[] keys, string[] results, string? filter, bool synopses, bool statistics = true, int lanes = 0)
    {
        List<string> rows = [];
        await foreach (RecordBatch batch in Ask(dataset, keys, results, filter, synopses, statistics, lanes).ToBatchesAsync(Ct))
        {
            Values.DescribeRows(batch, rows);
        }

        rows.Sort(StringComparer.Ordinal);
        return rows;
    }

    internal static Vorticity.Scan Ask(
        VortexDataset dataset, string[] keys, string[] results, string? filter, bool synopses, bool statistics = true, int lanes = 0)
    {
        Vorticity.Scan scan = dataset.Scan().With(new ScanOptions { UseSynopses = synopses, UseStatistics = statistics, DegreeOfParallelism = lanes });
        if (filter is not null)
        {
            scan.Where(VortexExpr.Parse(filter));
        }

        return keys.Length == 0 ? scan.Aggregate(results) : scan.GroupBy(keys).Select(results);
    }

    internal static DatasetOptions Options() => new DatasetOptions { SheetObjectBytes = 0 };

    /// <summary>The rows <paramref name="question"/> answers, each a line of its values, sorted; none when not <paramref name="describe"/>.</summary>
    internal static async Task<List<string>> AskAsync(VortexDataset dataset, string question, bool synopses, bool describe = true)
    {
        List<string> rows = [];
        await using (IAsyncEnumerator<RecordBatch> batches = Ask(dataset, question, synopses).Query.Batches(Ct))
        {
            while (await batches.MoveNextAsync())
            {
                if (describe)
                {
                    Values.DescribeRows(batches.Current, rows);
                }
            }
        }

        rows.Sort(StringComparer.Ordinal);
        return rows;
    }

    internal static Vorticity.Aggregation Ask(VortexDataset dataset, string question, bool synopses)
    {
        Scan<Call> scan = dataset.Scan<Call>().With(new ScanOptions { UseSynopses = synopses });
        DateTimeOffset from = Noon.AddHours(1);
        DateTimeOffset to = Noon.AddHours(3);
        DateTimeOffset cutFrom = Noon.AddMinutes(37.5);
        DateTimeOffset cutTo = Noon.AddMinutes(155.25);
        return question switch
        {
            "count by service" => scan.GroupBy(c => c.Service).Select(g => (g.Key, g.Count())),
            "count by service and hour" => scan.GroupBy(c => (c.Service, c.At.Truncate(CalendarUnit.Hour))).Select(g => (g.Key.Service, g.Key.Item2, g.Count())),
            "measures by service" => scan.GroupBy(c => c.Service)
                .Select(g => (g.Key, g.Count(), g.Sum(c => c.DurationNs), g.Min(c => c.DurationNs), g.Max(c => c.Weight), g.Average(c => c.Weight))),
            "count by day" => scan.GroupBy(c => c.At.Truncate(CalendarUnit.Day)).Select(g => (g.Key, g.Count(), g.Sum(c => c.Weight))),
            "count by hour, one service" => scan.Where(c => c.Service == "api").GroupBy(c => c.At.Truncate(CalendarUnit.Hour)).Select(g => (g.Key, g.Count())),
            "count by service, an aligned window" => scan.Where(c => c.At >= from && c.At < to).GroupBy(c => c.Service).Select(g => (g.Key, g.Count())),
            "measures by service, an unaligned window" => scan.Where(c => c.At >= cutFrom && c.At < cutTo).GroupBy(c => c.Service)
                .Select(g => (g.Key, g.Count(), g.Sum(c => c.DurationNs), g.Max(c => c.Weight))),
            "quantiles by service" => scan.GroupBy(c => c.Service).Select(g => (g.Key, g.Quantile(c => c.DurationNs, 0.5), g.Quantile(c => c.Weight, 0.99))),
            "distinct services by hour" => scan.GroupBy(c => c.At.Truncate(CalendarUnit.Hour)).Select(g => (g.Key, g.CountDistinct(c => c.Service))),
            "count by route and day" => scan.GroupBy(c => (c.Route, c.At.Truncate(CalendarUnit.Day))).Select(g => (g.Key.Route, g.Key.Item2, g.Count())),
            _ => throw new ArgumentException($"No question named '{question}'.", nameof(question)),
        };
    }

    /// <summary>
    /// Four hours of calls from noon, appended in parts of five minutes and compacted: an object an hour, its columns
    /// counted; then two parts more, appended after, in objects of their own.
    /// </summary>
    public sealed class Fixture : IAsyncLifetime
    {
        internal MemoryObjectStore Store { get; } = new MemoryObjectStore();

        public async ValueTask InitializeAsync()
        {
            await using VortexDataset dataset = await VortexDataset.CreateAsync(Store, Call.Schema, Options() with
            {
                ClusteringKey = [nameof(Call.At)],
                Clustering = new ClusteringOptions { Value = ClusteringValue.Truncate(CalendarUnit.Hour) }, ContentsObjectBytes = 0,
            });
            Random random = new Random(83);
            int row = 0;
            for (int part = 0; part < (Hours * 12) + 2; part++)
            {
                if (part == Hours * 12)
                {
                    while (await dataset.CompactAsync(new CompactionOptions { LevelZeroCeiling = 2 }) is not null)
                    {
                    }
                }

                Call[] rows = new Call[RowsPerHour / 12];
                for (int i = 0; i < rows.Length; i++, row++)
                {
                    int service = random.Next(Services.Length);
                    rows[i] = new Call(
                        Noon.AddSeconds(row * 3_600.0 / RowsPerHour),
                        Services[service],
                        $"/{Services[service]}/{random.Next(40)}",
                        random.NextInt64(1_000, 5_000_000),
                        Math.Round(random.NextDouble() * 100, 2));
                }

                await using ObjectDraft draft = dataset.StartObject();
                await draft.Writer.WriteAsync<Call>(rows);
                await dataset.AppendAsync(draft);
            }
        }

        public ValueTask DisposeAsync() => Store.DisposeAsync();
    }

    [VortexRecord]
    public partial record struct Call(DateTimeOffset At, string Service, string Route, long DurationNs, double Weight);
}
