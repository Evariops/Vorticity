// Several datasets read as one: a question over the set answers what the same question answers over one dataset that
// holds the rows of them all, in the order of the set.
//
// THE ORACLE IS THE ONE DATASET: the same rows are written to one dataset, and to two whose rows follow one another in
// it. Every answer of the set is held to the one dataset's, each value compared by its text, which tells apart any two
// doubles that differ in a bit but a NaN's payload.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.IO;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed partial class DatasetSetTests
{
    private const int Objects = 6;
    private const int FirstObjects = 4;
    private const int PerObject = 20_000;

    private static readonly DateTimeOffset Start = new(2026, 10, 25, 0, 0, 0, TimeSpan.Zero);
    private static readonly string[] Regions = ["eu-west", "eu-north", "us-east", "us-west", "ap-south", "sa-east", "af-south"];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// Counts, sums, a mean, extremes, a quantile, an approximate and an exact distinct count and a filtered count, by region
    /// and hour over a window that cuts both datasets: the set answers every group the one dataset answers, to the bit, on
    /// one lane and on four.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public async Task AnAggregationOverTheSetAnswersAsOverOneDataset(int degree)
    {
        await using Fixture data = await Fixture.CreateAsync();
        List<string> whole = await AnswersAsync(Hourly(data.Whole.Scan<Request>().With(Lanes(degree))));
        List<string> set = await AnswersAsync(Hourly(data.Set.Scan<Request>().With(Lanes(degree))));
        Assert.Equal(Regions.Length * 8, whole.Count);
        Assert.Equal(whole, set);
    }

    /// <summary>
    /// A filter on groups, an order on an aggregate and a window: one run holds the groups of both datasets, so the five
    /// kept are the five of the whole, which no merge of each dataset's five would give.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public async Task TheBusiestGroupsOfTheSetAreThoseOfOneDataset(int degree)
    {
        await using Fixture data = await Fixture.CreateAsync();
        List<string> whole = await AnswersAsync(Busiest(data.Whole.Scan<Request>().With(Lanes(degree))), ordered: true);
        List<string> set = await AnswersAsync(Busiest(data.Set.Scan<Request>().With(Lanes(degree))), ordered: true);
        Assert.Equal(5, whole.Count);
        Assert.Equal(whole, set);
    }

    /// <summary>
    /// A chosen row of each group, its first, and the one of the greatest latency: its columns are read by the row's
    /// place among the set's rows, in whichever dataset it lies.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public async Task AChosenRowIsReadInTheDatasetThatHoldsIt(int degree)
    {
        await using Fixture data = await Fixture.CreateAsync();
        List<string> whole = await ChosenAsync(data.Whole.Scan<Request>().With(Lanes(degree)));
        List<string> set = await ChosenAsync(data.Set.Scan<Request>().With(Lanes(degree)));
        Assert.Equal(Regions.Length, whole.Count);
        Assert.Equal(whole, set);
    }

    /// <summary>The aggregates of a whole scan: a count, a filtered count, whether a row is, extremes, a sum and a distinct count.</summary>
    [Fact]
    public async Task TheAnswersOfAWholeScanAreThoseOfOneDataset()
    {
        await using Fixture data = await Fixture.CreateAsync();
        DateTimeOffset late = Start.AddHours(9);
        foreach (Func<VortexDataset?, Scan<Request>> scan in new Func<VortexDataset?, Scan<Request>>[]
        {
            dataset => dataset is null ? data.Set.Scan<Request>() : dataset.Scan<Request>(),
            dataset => (dataset is null ? data.Set.Scan<Request>() : dataset.Scan<Request>()).Where(r => r.At >= late && r.Region == "us-east"),
        })
        {
            Assert.Equal(await scan(data.Whole).CountAsync(Ct), await scan(null).CountAsync(Ct));
            Assert.Equal(await scan(data.Whole).AnyAsync(Ct), await scan(null).AnyAsync(Ct));
            Assert.Equal(await scan(data.Whole).MinAsync(r => r.At, Ct), await scan(null).MinAsync(r => r.At, Ct));
            Assert.Equal(await scan(data.Whole).MaxAsync(r => r.At, Ct), await scan(null).MaxAsync(r => r.At, Ct));
            Assert.Equal(await scan(data.Whole).MinAsync(r => r.Score, Ct), await scan(null).MinAsync(r => r.Score, Ct));
            Assert.Equal(await scan(data.Whole).MaxAsync(r => r.Latency, Ct), await scan(null).MaxAsync(r => r.Latency, Ct));
            Assert.Equal(await scan(data.Whole).SumAsync(r => r.Bytes, Ct), await scan(null).SumAsync(r => r.Bytes, Ct));
            Assert.Equal(await scan(data.Whole).CountDistinctAsync(r => r.User, Ct), await scan(null).CountDistinctAsync(r => r.User, Ct));
        }

        Assert.Equal(Objects * (long)PerObject, await data.Set.Scan<Request>().CountAsync(Ct));
        Assert.Equal(Objects * (long)PerObject, data.Set.RowCount);
        Assert.False(await data.Set.Scan<Request>().Where(r => r.At < Start.AddDays(-1)).AnyAsync(Ct));
    }

    /// <summary>The rows a filter keeps come in the order of the set's rows, the first dataset's first.</summary>
    [Fact]
    public async Task TheRowsOfTheSetComeInTheOrderOfItsDatasets()
    {
        await using Fixture data = await Fixture.CreateAsync();
        DateTimeOffset from = Start.AddHours(7);
        DateTimeOffset to = Start.AddHours(9);
        List<Request> whole = await data.Whole.Scan<Request>().Where(r => r.At >= from && r.At < to && r.Region == "eu-west").ToRecordsAsync(Ct).ToListAsync(Ct);
        List<Request> set = await data.Set.Scan<Request>().Where(r => r.At >= from && r.At < to && r.Region == "eu-west").ToRecordsAsync(Ct).ToListAsync(Ct);
        Assert.True(whole.Count > 1_000, $"{whole.Count} rows");
        Assert.Equal(whole, set);
        Assert.True(set[0].At < Start.AddHours(8) && set[^1].At >= Start.AddHours(8), "the window cuts both datasets");
    }

    /// <summary>
    /// The rows in the order of the column the datasets are sorted by, up and down, over a window that cuts both and over
    /// one that lies in the second alone: each dataset orders its own rows, and the set merges them.
    /// </summary>
    [Theory]
    [InlineData(false, 7.5, 8.5)]
    [InlineData(true, 7.5, 8.5)]
    [InlineData(false, 9.0, 9.5)]
    [InlineData(true, 9.0, 9.5)]
    public async Task TheRowsOfTheSetInTheOrderOfTheirKeyAreThoseOfOneDataset(bool descending, double fromHour, double toHour)
    {
        await using Fixture data = await Fixture.CreateAsync();
        DateTimeOffset from = Start.AddHours(fromHour);
        DateTimeOffset to = Start.AddHours(toHour);
        Scan<Request> Ordered(Scan<Request> scan)
        {
            Scan<Request> kept = scan.Where(r => r.At >= from && r.At < to && r.Bytes > 0L);
            return descending ? kept.OrderByDescending(r => r.At) : kept.OrderBy(r => r.At);
        }

        List<Request> whole = await Ordered(data.Whole.Scan<Request>()).ToRecordsAsync(Ct).ToListAsync(Ct);
        List<Request> set = await Ordered(data.Set.Scan<Request>()).ToRecordsAsync(Ct).ToListAsync(Ct);
        Assert.True(whole.Count > 2_000, $"{whole.Count} rows");
        Assert.Equal(whole, set);
    }

    /// <summary>
    /// An order neither dataset has a key source for is refused by the set as each dataset refuses it alone: a set orders
    /// what its datasets order, and sorts nothing they would not.
    /// </summary>
    [Fact]
    public async Task AnOrderItsDatasetsRefuseIsRefusedByTheSet()
    {
        await using Fixture data = await Fixture.CreateAsync();
        VortexUnsupportedException alone = await Assert.ThrowsAsync<VortexUnsupportedException>(
            async () => await data.Whole.Scan<Request>().OrderBy(r => r.Score).ToRecordsAsync(Ct).ToListAsync(Ct));
        VortexUnsupportedException set = await Assert.ThrowsAsync<VortexUnsupportedException>(
            async () => await data.Set.Scan<Request>().OrderBy(r => r.Score).ToRecordsAsync(Ct).ToListAsync(Ct));
        Assert.Equal(alone.Message, set.Message);
    }

    /// <summary>
    /// A range of rows that starts in the first dataset and ends in the second, and rows asked by their places in both:
    /// the set numbers its rows as the one dataset does.
    /// </summary>
    [Fact]
    public async Task ARangeAndPlacesOfTheSetsRowsCrossItsDatasets()
    {
        await using Fixture data = await Fixture.CreateAsync();
        long edge = FirstObjects * (long)PerObject;
        RowRange range = new RowRange(edge - 1_500, edge + 2_500);
        List<Request> whole = await data.Whole.Scan<Request>().Rows(range).ToRecordsAsync(Ct).ToListAsync(Ct);
        List<Request> set = await data.Set.Scan<Request>().Rows(range).ToRecordsAsync(Ct).ToListAsync(Ct);
        Assert.Equal(4_000, whole.Count);
        Assert.Equal(whole, set);

        long[] places = [3, edge - 1, edge, edge + 17, (Objects * (long)PerObject) - 1];
        List<Request> wholeAt = await data.Whole.Scan<Request>().Rows(places).ToRecordsAsync(Ct).ToListAsync(Ct);
        List<Request> setAt = await data.Set.Scan<Request>().Rows(places).ToRecordsAsync(Ct).ToListAsync(Ct);
        Assert.Equal(places.Length, wholeAt.Count);
        Assert.Equal(wholeAt, setAt);
    }

    /// <summary>
    /// The best rows of a column no dataset is sorted by, up and down: the places of the window's rows are found among
    /// the rows of both datasets, then those rows alone are read, each in the dataset that holds it.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TheBestRowsOfAColumnAreThoseOfOneDataset(bool descending)
    {
        await using Fixture data = await Fixture.CreateAsync();
        DateTimeOffset from = Start.AddHours(6);
        DateTimeOffset to = Start.AddHours(10);
        Projection<double> Best(Scan<Request> scan)
        {
            Scan<Request> kept = scan.Where(r => r.At >= from && r.At < to && r.Region == "ap-south");
            return (descending ? kept.OrderByDescending(r => r.Latency) : kept.OrderBy(r => r.Latency)).Select(r => r.Latency).Take(40);
        }

        List<double> whole = await Best(data.Whole.Scan<Request>()).ToValuesAsync(Ct).ToListAsync(Ct);
        List<double> set = await Best(data.Set.Scan<Request>()).ToValuesAsync(Ct).ToListAsync(Ct);
        Assert.Equal(40, whole.Count);
        Assert.Equal(whole, set);
    }

    /// <summary>
    /// The best rows of a column of many equal values and of nulls, with no filter and under one: each dataset finds its
    /// own best rows by its own path, and the rows that tie are kept by their place among the set's rows, as the one
    /// dataset keeps them.
    /// </summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task TheBestRowsOfAColumnOfTiesAreKeptByTheirPlace(bool descending, bool filtered)
    {
        await using Fixture data = await Fixture.CreateAsync();
        async Task<List<ScoreAt>> BestAsync(Scan<Request> scan)
        {
            Scan<Request> kept = filtered ? scan.Where(r => r.Region == "sa-east" && r.Bytes > 0L) : scan;
            Scan<Request> ordered = descending ? kept.OrderByDescending(r => r.Score) : kept.OrderBy(r => r.Score);
            return await ordered.Select(r => (r.Score, r.At)).Take(150).As<ScoreAt>().ToRecordsAsync(Ct).ToListAsync(Ct);
        }

        List<ScoreAt> whole = await BestAsync(data.Whole.Scan<Request>());
        List<ScoreAt> set = await BestAsync(data.Set.Scan<Request>());
        Assert.Equal(150, whole.Count);
        Assert.Contains(whole, static row => row.At >= Start.AddHours(8));
        Assert.Contains(whole, static row => row.At < Start.AddHours(8));
        Assert.Equal(whole, set);
    }

    /// <summary>
    /// An estimate of the set draws its ranges from both datasets, and ends with the exact answer: the one dataset's, to
    /// the bit.
    /// </summary>
    [Fact]
    public async Task AnEstimateOfTheSetEndsWithTheAnswerOfOneDataset()
    {
        await using Fixture data = await Fixture.CreateAsync();
        static Vorticity.Aggregation Question(Scan<Request> scan) =>
            scan.GroupBy(r => r.Region).Select(g => (g.Key, g.Count(), g.Sum(r => r.Bytes), g.Average(r => r.Latency)));

        List<string> whole = [.. (await Question(data.Whole.Scan<Request>()).As<Totals>().ToRecordsAsync(Ct).ToListAsync(Ct))
            .OrderBy(t => t.Region, StringComparer.Ordinal).Select(t => t.ToString())];
        EstimateOptions tiers = new EstimateOptions { Shares = [0.05, 0.2], RangeBytes = 1 << 10, ExactUnder = TimeSpan.Zero, Seed = 3 };
        List<double> shares = [];
        bool exact = false;
        await foreach (AggregationEstimate estimate in Question(data.Set.Scan<Request>()).EstimateAsync(tiers, Ct))
        {
            List<Estimated> groups = await estimate.As<Estimated>().ToRecordsAsync(Ct).ToListAsync(Ct);
            if (!estimate.IsExact)
            {
                shares.Add(estimate.Share);
                Assert.Equal(Regions.Length, groups.Count);
                continue;
            }

            exact = true;
            Assert.Equal(whole, [.. groups.OrderBy(g => g.Region, StringComparer.Ordinal).Select(g => new Totals(g.Region, g.Rows, g.Bytes, g.Mean).ToString())]);
        }

        Console.WriteLine($"ESTIMATE OF A SET: shares {string.Join(", ", shares.Select(static share => share.ToString("P1", System.Globalization.CultureInfo.InvariantCulture)))}");
        Assert.True(exact, "the exact answer ends the estimate");
        Assert.NotEmpty(shares);
        Assert.All(shares, static share => Assert.InRange(share, 0.01, 0.9));
    }

    /// <summary>
    /// A dataset whose jobs wrote objects that keep counts answers its part from them, and the other its rows, in one run:
    /// the set takes the states its datasets give, and answers as the one dataset.
    /// </summary>
    [Fact]
    public async Task ADatasetOfTheSetAnswersFromItsCounts()
    {
        await using MemoryObjectStore wholeStore = new MemoryObjectStore();
        await using MemoryObjectStore firstStore = new MemoryObjectStore();
        await using MemoryObjectStore secondStore = new MemoryObjectStore();
        await using VortexDataset whole = await FilledAsync(wholeStore, 0, Objects + 6);
        await using VortexDataset first = await FilledAsync(firstStore, 0, Objects + 4);
        await using VortexDataset second = await FilledAsync(secondStore, Objects + 4, 2);
        while (await first.CompactAsync(new CompactionOptions { LevelZeroCeiling = 1 }, Ct) is not null)
        {
        }

        static Vorticity.Aggregation Question(Scan<Request> scan) =>
            scan.GroupBy(r => r.Region).Select(g => (g.Key, g.Count(), g.Sum(r => r.Bytes), g.Quantile(r => r.Latency, 0.9)));

        VortexDatasetSet set = new VortexDatasetSet(first, second);
        ScanPlan alone = await Question(first.Scan<Request>()).ExplainAsync(Ct);
        ScanPlan plan = await Question(set.Scan<Request>()).ExplainAsync(Ct);
        Console.WriteLine($"UNITS OF A SET: {string.Join(", ", plan.Units.GroupBy(static unit => unit.Alternative).Select(static g => $"{g.Count()} {g.Key}"))}");
        Assert.Contains(alone.Units, static unit => unit.Alternative is "counts" or "rollup");
        Assert.Contains(plan.Units, static unit => unit.Alternative is "counts" or "rollup");
        Assert.Contains(plan.Units, static unit => unit.Alternative == "rows");
        foreach (int degree in new[] { 1, 4 })
        {
            Assert.Equal(
                await RegionsAsync(Question(whole.Scan<Request>().With(Lanes(degree)))),
                await RegionsAsync(Question(set.Scan<Request>().With(Lanes(degree)))));
        }
    }

    /// <summary>
    /// A set of a dataset sorted by the instant, an empty one, and one partitioned by day and sorted by region: the
    /// datasets are laid out as each was created, and an aggregation over them answers as the one dataset.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public async Task DatasetsLaidOutDifferentlyAndAnEmptyOneAnswerAsOne(int degree)
    {
        await using Fixture data = await Fixture.CreateAsync();
        await using MemoryObjectStore emptyStore = new MemoryObjectStore();
        await using MemoryObjectStore otherStore = new MemoryObjectStore();
        await using VortexDataset empty = await FilledAsync(emptyStore, 0, 0);
        await using VortexDataset other = await VortexDataset.CreateAsync(
            otherStore,
            Request.Schema,
            new DatasetOptions
            {
                Layout = new DatasetLayout
                {
                    Partition = LayoutValue.Column(nameof(Request.At)).Truncate(CalendarUnit.Day),
                    SortKey = [LayoutValue.Column(nameof(Request.Region)), LayoutValue.Column(nameof(Request.At))],
                },
            },
            Ct);
        for (int o = FirstObjects; o < Objects; o++)
        {
            await AppendAsync(other, o);
        }

        VortexDatasetSet set = new VortexDatasetSet(data.First, empty, other);
        Assert.Equal(Objects * (long)PerObject, await set.Scan<Request>().CountAsync(Ct));
        Assert.Equal(
            await AnswersAsync(Hourly(data.Whole.Scan<Request>().With(Lanes(degree)))),
            await AnswersAsync(Hourly(set.Scan<Request>().With(Lanes(degree)))));
        Assert.Equal(
            await AnswersAsync(Busiest(data.Whole.Scan<Request>().With(Lanes(degree))), ordered: true),
            await AnswersAsync(Busiest(set.Scan<Request>().With(Lanes(degree))), ordered: true));
    }

    /// <summary>
    /// The paths of a variant column over two datasets whose rows hold different keys: the set lists every path a kept
    /// row of either holds, and the exact census counts the rows of both, as over one dataset.
    /// </summary>
    [Fact]
    public async Task ThePathsOfTheSetsVariantsAreThoseOfOneDataset()
    {
        await using MemoryObjectStore wholeStore = new MemoryObjectStore();
        await using MemoryObjectStore firstStore = new MemoryObjectStore();
        await using MemoryObjectStore secondStore = new MemoryObjectStore();
        await using VortexDataset whole = await VortexDataset.CreateAsync(wholeStore, Event.Schema, cancellationToken: Ct);
        await using VortexDataset first = await VortexDataset.CreateAsync(firstStore, Event.Schema, cancellationToken: Ct);
        await using VortexDataset second = await VortexDataset.CreateAsync(secondStore, Event.Schema, cancellationToken: Ct);

        // The first dataset's rows hold a deploy one row in five, the second's a zone one row in three: neither holds the other's key.
        Event[] early = [.. Enumerable.Range(0, 4_000).Select(i => new Event(
            Start.AddSeconds(i),
            i % 4,
            i % 5 == 0
                ? Variant.Object(("route", Variant.From($"/r/{i % 7}")), ("deploy", Variant.From((long)i)))
                : Variant.Object(("route", Variant.From($"/r/{i % 7}")))))];
        Event[] late = [.. Enumerable.Range(4_000, 3_000).Select(i => new Event(
            Start.AddSeconds(i),
            i % 4,
            i % 3 == 0
                ? Variant.Object(("route", Variant.From($"/r/{i % 7}")), ("zone", Variant.From("eu")))
                : Variant.Object(("route", Variant.From($"/r/{i % 7}")))))];
        foreach ((VortexDataset dataset, Event[] rows) in new[] { (whole, early), (whole, late), (first, early), (second, late) })
        {
            await using ObjectDraft draft = dataset.StartObject();
            await draft.Writer.WriteAsync<Event>(rows, Ct);
            await dataset.AppendAsync(draft, Ct);
        }

        VortexDatasetSet set = new VortexDatasetSet(first, second);
        foreach (bool filtered in new[] { false, true })
        {
            Scan<Event> Kept(Scan<Event> scan) => filtered ? scan.Where(r => r.Source == 2) : scan;
            VariantPaths expected = await Kept(whole.Scan<Event>()).PathsAsync(r => r.Attributes, new PathsOptions { Exact = true }, Ct);
            VariantPaths exact = await Kept(set.Scan<Event>()).PathsAsync(r => r.Attributes, new PathsOptions { Exact = true }, Ct);
            Assert.True(exact.Exact && exact.Complete, $"filtered {filtered}");
            Assert.Equal(PathRows(expected), PathRows(exact));
            Assert.Contains(exact, static path => path.Keys.Contains("deploy"));
            Assert.Contains(exact, static path => path.Keys.Contains("zone"));

            VariantPaths listed = await Kept(set.Scan<Event>()).PathsAsync(r => r.Attributes, cancellationToken: Ct);
            Assert.True(listed.Complete, $"filtered {filtered}");
            Assert.Equal(PathRows(expected).Select(static path => path.Split('=')[0]), PathRows(listed).Select(static path => path.Split('=')[0]));
        }
    }

    /// <summary>Each path and class with the rows that hold it, in the order of the paths.</summary>
    private static List<string> PathRows(VariantPaths paths) =>
        [.. paths.Select(static path => $"{string.Concat(path.Keys.Select(static key => "/" + key))}#{path.Class}={path.Rows}").Order(StringComparer.Ordinal)];

    /// <summary>The set's question cut into parts, each run apart and merged, answers as the one dataset's whole question.</summary>
    [Fact]
    public async Task ThePartsOfASetsQuestionMergeToTheAnswersOfOneDataset()
    {
        await using Fixture data = await Fixture.CreateAsync();
        List<string> whole = await AnswersAsync(Mergeable(data.Whole.Scan<Request>()));
        RowRange[] parts = await Mergeable(data.Set.Scan<Request>()).PartsAsync(4, Ct);
        Assert.InRange(parts.Length, 2, 4);
        List<AggregationPartial> partials = [];
        foreach (RowRange part in parts)
        {
            partials.Add(await Mergeable(data.Set.Scan<Request>().Rows(part)).PartialAsync(Ct));
        }

        Assert.Equal(whole, await AnswersAsync(Mergeable(data.Set.Scan<Request>()).Merge(partials)));
    }

    /// <summary>
    /// The plan of the set's question names the units of both datasets, and its rows are theirs: what the set will do is
    /// what each dataset would do for the same question.
    /// </summary>
    [Fact]
    public async Task ThePlanOfTheSetIsTheSumOfItsDatasets()
    {
        await using Fixture data = await Fixture.CreateAsync();
        ScanPlan first = await Hourly(data.First.Scan<Request>()).ExplainAsync(Ct);
        ScanPlan second = await Hourly(data.Second.Scan<Request>()).ExplainAsync(Ct);
        ScanPlan set = await Hourly(data.Set.Scan<Request>()).ExplainAsync(Ct);
        Assert.Equal(first.Rows + second.Rows, set.Rows);
        Assert.Equal(first.Units.Length + second.Units.Length, set.Units.Length);
        Assert.Equal(first.Units.Select(static unit => unit.Alternative).Concat(second.Units.Select(static unit => unit.Alternative)), set.Units.Select(static unit => unit.Alternative));
        Assert.Equal(first.BytesToRead + second.BytesToRead, set.BytesToRead);
        Assert.Contains("2 sources read as one", set.Logical, StringComparison.Ordinal);
    }

    /// <summary>
    /// The requests a question of the set makes to each store are those the same question makes of each dataset alone: no
    /// more. A window that lies in the first dataset alone asks the second for no data.
    /// </summary>
    [Fact]
    public async Task TheSetAsksEachStoreWhatItsDatasetAloneWouldBeAsked()
    {
        MemoryObjectStore firstInner = new MemoryObjectStore();
        MemoryObjectStore secondInner = new MemoryObjectStore();
        await using CountingObjectStore firstStore = new CountingObjectStore(firstInner, ownsInner: true);
        await using CountingObjectStore secondStore = new CountingObjectStore(secondInner, ownsInner: true);
        await using (VortexDataset first = await FilledAsync(firstStore, 0, FirstObjects))
        await using (VortexDataset second = await FilledAsync(secondStore, FirstObjects, Objects - FirstObjects))
        {
        }

        // The window of the first dataset's second hour: the second dataset's summaries refute it.
        DateTimeOffset from = Start.AddHours(1);
        DateTimeOffset to = Start.AddHours(2);
        Vorticity.Aggregation Question(Scan<Request> scan) => scan
            .Where(r => r.At >= from && r.At < to)
            .GroupBy(r => r.Region)
            .Select(g => (g.Key, g.Count(), g.Sum(r => r.Bytes), g.Quantile(r => r.Latency, 0.9)));

        // Each dataset alone, under a session of its own, so that nothing a run read serves the next.
        (long first, long second) alone;
        List<string> expected = [];
        await using (VortexSession session = VortexSession.Create(static options => options.MaxDegreeOfParallelism = 2))
        await using (VortexDataset first = await VortexDataset.OpenAsync(firstStore, new DatasetOptions { Session = session }, Ct))
        await using (VortexDataset second = await VortexDataset.OpenAsync(secondStore, new DatasetOptions { Session = session }, Ct))
        {
            firstStore.Reset();
            secondStore.Reset();
            expected.AddRange(await RegionsAsync(Question(first.Scan<Request>())));
            Assert.Empty(await RegionsAsync(Question(second.Scan<Request>())));
            alone = (firstStore.Requests, secondStore.Requests);
        }

        await using (VortexSession session = VortexSession.Create(static options => options.MaxDegreeOfParallelism = 2))
        await using (VortexDataset first = await VortexDataset.OpenAsync(firstStore, new DatasetOptions { Session = session }, Ct))
        await using (VortexDataset second = await VortexDataset.OpenAsync(secondStore, new DatasetOptions { Session = session }, Ct))
        {
            VortexDatasetSet set = new VortexDatasetSet(first, second);
            firstStore.Reset();
            secondStore.Reset();
            Assert.Equal(expected, await RegionsAsync(Question(set.Scan<Request>())));
            Console.WriteLine($"REQUESTS: alone {alone.first} and {alone.second}, in the set {firstStore.Requests} and {secondStore.Requests}");
            Assert.True(alone.first > 0, "the first dataset is read");
            Assert.InRange(firstStore.Requests, 1, alone.first);
            Assert.InRange(secondStore.Requests, 0, alone.second);
        }

        // A window that cuts both datasets: each is asked what it is asked alone.
        from = Start.AddHours(7);
        to = Start.AddHours(9);
        await using (VortexSession session = VortexSession.Create(static options => options.MaxDegreeOfParallelism = 2))
        await using (VortexDataset first = await VortexDataset.OpenAsync(firstStore, new DatasetOptions { Session = session }, Ct))
        await using (VortexDataset second = await VortexDataset.OpenAsync(secondStore, new DatasetOptions { Session = session }, Ct))
        {
            firstStore.Reset();
            secondStore.Reset();
            Assert.NotEmpty(await RegionsAsync(Question(first.Scan<Request>())));
            Assert.NotEmpty(await RegionsAsync(Question(second.Scan<Request>())));
            alone = (firstStore.Requests, secondStore.Requests);
        }

        await using (VortexSession session = VortexSession.Create(static options => options.MaxDegreeOfParallelism = 2))
        await using (VortexDataset first = await VortexDataset.OpenAsync(firstStore, new DatasetOptions { Session = session }, Ct))
        await using (VortexDataset second = await VortexDataset.OpenAsync(secondStore, new DatasetOptions { Session = session }, Ct))
        {
            VortexDatasetSet set = new VortexDatasetSet(first, second);
            firstStore.Reset();
            secondStore.Reset();
            Assert.Equal(Regions.Length, (await RegionsAsync(Question(set.Scan<Request>()))).Count);
            Console.WriteLine($"REQUESTS ACROSS: alone {alone.first} and {alone.second}, in the set {firstStore.Requests} and {secondStore.Requests}");
            Assert.True(alone.first > 0 && alone.second > 0, "both datasets are read");
            Assert.InRange(firstStore.Requests, 1, alone.first);
            Assert.InRange(secondStore.Requests, 1, alone.second);
        }
    }

    /// <summary>A refresh of the set moves every handle to its dataset's latest version.</summary>
    [Fact]
    public async Task ARefreshOfTheSetMovesEveryHandle()
    {
        await using MemoryObjectStore firstStore = new MemoryObjectStore();
        await using MemoryObjectStore secondStore = new MemoryObjectStore();
        await using VortexDataset firstWriter = await FilledAsync(firstStore, 0, 1);
        await using VortexDataset secondWriter = await FilledAsync(secondStore, 1, 1);
        await using VortexDataset first = await VortexDataset.OpenAsync(firstStore, cancellationToken: Ct);
        await using VortexDataset second = await VortexDataset.OpenAsync(secondStore, cancellationToken: Ct);
        VortexDatasetSet set = new VortexDatasetSet(first, second);
        Assert.Equal(2L * PerObject, await set.Scan<Request>().CountAsync(Ct));

        await AppendAsync(firstWriter, 2);
        await AppendAsync(secondWriter, 3);
        Assert.Equal(2L * PerObject, await set.Scan<Request>().CountAsync(Ct));
        await set.RefreshAsync(Ct);
        Assert.Equal(4L * PerObject, await set.Scan<Request>().CountAsync(Ct));
        Assert.Equal(4L * PerObject, set.RowCount);
    }

    /// <summary>A set of no dataset, of a dataset twice, of two schemas or of two sessions is refused where it is declared, and a set walks no keys.</summary>
    [Fact]
    public async Task ASetThatCannotBeReadIsRefused()
    {
        await using MemoryObjectStore firstStore = new MemoryObjectStore();
        await using MemoryObjectStore otherStore = new MemoryObjectStore();
        await using VortexDataset first = await FilledAsync(firstStore, 0, 1);
        await using VortexDataset other = await VortexDataset.CreateAsync(otherStore, HourTotal.Schema, cancellationToken: Ct);
        Assert.Throws<ArgumentException>(static () => new VortexDatasetSet());
        Assert.Throws<ArgumentException>(() => new VortexDatasetSet(first, first));
        ArgumentException schemas = Assert.Throws<ArgumentException>(() => new VortexDatasetSet(first, other));
        Assert.Contains("one schema", schemas.Message, StringComparison.Ordinal);

        await using VortexSession session = VortexSession.Create(static options => options.MaxDegreeOfParallelism = 1);
        await using VortexDataset elsewhere = await VortexDataset.OpenAsync(firstStore, new DatasetOptions { Session = session }, Ct);
        ArgumentException sessions = Assert.Throws<ArgumentException>(() => new VortexDatasetSet(first, elsewhere));
        Assert.Contains("session", sessions.Message, StringComparison.Ordinal);

        VortexDatasetSet alone = new VortexDatasetSet(first);
        Assert.Equal(PerObject, await alone.Scan<Request>().CountAsync(Ct));
        await Assert.ThrowsAsync<NotSupportedException>(async () => await alone.Scan<Request>().Keys(r => r.At).OpenAsync(Ct));
    }

    private static ScanOptions Lanes(int degree) => new ScanOptions { DegreeOfParallelism = degree };

    /// <summary>Hours 3 to 20, by region and hour: what a state merges, and what none does, a filtered count and an exact distinct count.</summary>
    private static Vorticity.Aggregation Hourly(Scan<Request> scan) => scan
        .Where(r => r.At >= Start.AddHours(3) && r.At < Start.AddHours(11))
        .GroupBy(r => (r.Region, r.At.Truncate(CalendarUnit.Hour)))
        .Select(g => (g.Key.Region, g.Key.Item2, g.Count(), g.Sum(r => r.Bytes), g.Sum(r => r.Latency), g.Average(r => r.Latency),
            g.Min(r => r.Score), g.Max(r => r.Latency), g.Quantile(r => r.Latency, 0.9), g.ApproximateCountDistinct(r => r.User),
            g.Count(r => r.Bytes > 0L), g.CountDistinct(r => r.User)));

    /// <summary>The question of <see cref="Hourly"/> without what takes no parts.</summary>
    private static Vorticity.Aggregation Mergeable(Scan<Request> scan) => scan
        .Where(r => r.At >= Start.AddHours(3) && r.At < Start.AddHours(11))
        .GroupBy(r => (r.Region, r.At.Truncate(CalendarUnit.Hour)))
        .Select(g => (g.Key.Region, g.Key.Item2, g.Count(), g.Sum(r => r.Bytes), g.Sum(r => r.Latency), g.Average(r => r.Latency),
            g.Min(r => r.Score), g.Max(r => r.Latency), g.Quantile(r => r.Latency, 0.9), g.ApproximateCountDistinct(r => r.User),
            g.Count(), g.Count()));

    /// <summary>The five regions and hours of the most bytes among those of more than 400 requests.</summary>
    private static Vorticity.Aggregation Busiest(Scan<Request> scan) => scan
        .GroupBy(r => (r.Region, r.At.Truncate(CalendarUnit.Hour)))
        .Where(g => g.Count() > 400L)
        .OrderByDescending(g => g.Sum(r => r.Bytes))
        .Take(5)
        .Select(g => (g.Key.Region, g.Key.Item2, g.Count(), g.Sum(r => r.Bytes), g.Sum(r => r.Latency), g.Average(r => r.Latency),
            g.Min(r => r.Score), g.Max(r => r.Latency), g.Quantile(r => r.Latency, 0.9), g.ApproximateCountDistinct(r => r.User),
            g.Count(r => r.Bytes > 0L), g.CountDistinct(r => r.User)));

    /// <summary>The answers as text, each double's shortest text telling its bits apart, in the order of their keys unless delivered in order.</summary>
    private static async Task<List<string>> AnswersAsync(Vorticity.Aggregation question, bool ordered = false)
    {
        List<HourTotal> answers = await question.As<HourTotal>().ToRecordsAsync(Ct).ToListAsync(Ct);
        IEnumerable<HourTotal> listed = ordered ? answers : answers.OrderBy(a => a.Region, StringComparer.Ordinal).ThenBy(a => a.Hour);
        return [.. listed.Select(a => a.ToString())];
    }

    /// <summary>Each region's first row and its row of the greatest latency, by region.</summary>
    private static async Task<List<string>> ChosenAsync(Scan<Request> scan)
    {
        List<Chosen> answers = await scan
            .GroupBy(r => r.Region)
            .Select(g => (g.Key, g.Count(), g.First().User, g.First().At, g.MaxBy(r => r.Latency).User, g.MaxBy(r => r.Latency).At))
            .As<Chosen>().ToRecordsAsync(Ct).ToListAsync(Ct);
        return [.. answers.OrderBy(a => a.Region, StringComparer.Ordinal).Select(a => a.ToString())];
    }

    private static async Task<List<string>> RegionsAsync(Vorticity.Aggregation question)
    {
        List<RegionTotal> answers = await question.As<RegionTotal>().ToRecordsAsync(Ct).ToListAsync(Ct);
        return [.. answers.OrderBy(a => a.Region, StringComparer.Ordinal).Select(a => a.ToString())];
    }

    /// <summary>A dataset sorted by the instant, of <paramref name="count"/> objects from object <paramref name="first"/>, appended in the order of their hours.</summary>
    private static async Task<VortexDataset> FilledAsync(IObjectStore store, int first, int count)
    {
        VortexDataset dataset = await VortexDataset.CreateAsync(
            store, Request.Schema, new DatasetOptions { Layout = new DatasetLayout { SortKey = [LayoutValue.Column(nameof(Request.At))] } }, Ct);
        for (int o = first; o < first + count; o++)
        {
            await AppendAsync(dataset, o);
        }

        return dataset;
    }

    private static async Task AppendAsync(VortexDataset dataset, int o)
    {
        await using ObjectDraft draft = dataset.StartObject();
        await draft.Writer.WriteAsync<Request>(Rows(o * PerObject, PerObject).ToArray(), Ct);
        await dataset.AppendAsync(draft, Ct);
    }

    /// <summary>
    /// Rows <c>[first, first + count)</c> of half a day, a row every 0.36 s, two hours an object: bytes of either sign,
    /// latencies of many magnitudes, whose sums no double holds exactly, a score null one row in seven.
    /// </summary>
    private static List<Request> Rows(int first, int count)
    {
        Random random = new Random(0x5E7 + first);
        return [.. Enumerable.Range(first, count).Select(i => new Request(
            Regions[random.Next(Regions.Length)],
            Start.AddTicks(i * 3_600_000L),
            random.NextInt64(-1_000_000, 10_000_000),
            random.NextDouble() * Math.Pow(10, random.Next(-3, 7)),
            i % 7 == 0 ? null : random.Next(-500, 500),
            $"user-{random.Next(3_000)}"))];
    }

    /// <summary>The same rows in one dataset, and in two whose rows follow one another in it: the first four objects, then the last two.</summary>
    private sealed class Fixture : IAsyncDisposable
    {
        private readonly MemoryObjectStore[] _stores = [new MemoryObjectStore(), new MemoryObjectStore(), new MemoryObjectStore()];

        internal VortexDataset Whole { get; private set; } = null!;

        internal VortexDataset First { get; private set; } = null!;

        internal VortexDataset Second { get; private set; } = null!;

        internal VortexDatasetSet Set { get; private set; } = null!;

        internal static async Task<Fixture> CreateAsync()
        {
            Fixture fixture = new Fixture();
            fixture.Whole = await FilledAsync(fixture._stores[0], 0, Objects);
            fixture.First = await FilledAsync(fixture._stores[1], 0, FirstObjects);
            fixture.Second = await FilledAsync(fixture._stores[2], FirstObjects, Objects - FirstObjects);
            fixture.Set = new VortexDatasetSet(fixture.First, fixture.Second);
            return fixture;
        }

        public async ValueTask DisposeAsync()
        {
            await Whole.DisposeAsync();
            await First.DisposeAsync();
            await Second.DisposeAsync();
            foreach (MemoryObjectStore store in _stores)
            {
                await store.DisposeAsync();
            }
        }
    }

    [VortexRecord]
    public partial record struct Request(string Region, DateTimeOffset At, long Bytes, double Latency, int? Score, string User);

    [VortexRecord]
    public partial record struct HourTotal(
        string Region, DateTimeOffset Hour, long Count, long Bytes, double Latency, double? MeanLatency, int? LeastScore, double? GreatestLatency, double? Latency90,
        long Users, long Positive, long ExactUsers);

    [VortexRecord]
    public partial record struct Chosen(string Region, long Count, string FirstUser, DateTimeOffset FirstAt, string? SlowestUser, DateTimeOffset? SlowestAt);

    [VortexRecord]
    public partial record struct RegionTotal(string Region, long Count, long Bytes, double? Latency90);

    [VortexRecord]
    public partial record struct ScoreAt(int? Score, DateTimeOffset At);

    [VortexRecord]
    public partial record struct Event(DateTimeOffset At, int Source, Variant Attributes);

    [VortexRecord]
    public partial record struct Totals(string Region, long Rows, long Bytes, double? Mean);

    [VortexRecord]
    public partial record struct Estimated(string Region, long Rows, long Bytes, double? Mean, double RowsMargin, double BytesMargin, double MeanMargin);
}
