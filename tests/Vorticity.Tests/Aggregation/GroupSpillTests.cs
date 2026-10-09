using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Vorticity.Tests.Aggregation;

/// <summary>
/// The spill of the lanes' tables: a group by whose budget cannot hold its lanes' tables, on keys or
/// states the core cannot take (a text, a composite holding one, a text's extremes, distinct counts),
/// writes each table to the scratch when the budget holds it no more, then reads the runs back part by
/// part, exact, every reservation and every file given back. Budgets are set against the result: what one
/// lane holds once it has met every row.
/// </summary>
[Collection(nameof(CorePressureCollection))]
public sealed partial class GroupSpillTests
{
    private const int Rows = 1_200_000;

    public static TheoryData<int, int> Budgets => new TheoryData<int, int>
    {
        { 1, 200 },
        { 1, 50 },
        { 1, 10 },
        { 4, 50 },
        { 4, 10 },
        { 14, 50 },
        { 14, 10 },
    };

    [Theory]
    [MemberData(nameof(Budgets))]
    public async Task ATextKeySpillsAndEndsExact(int degree, int percent)
    {
        (_, Row[] rows) = await Fixture.Async;
        Dictionary<string, NameTotal> expected = rows.GroupBy(r => r.Name).ToDictionary(g => g.Key, g => new NameTotal(g.Key, g.Count(), g.Sum(r => r.Value)));
        await ExactAsync(degree, percent, Names, (NameTotal total) => total.Name, expected);
    }

    [Theory]
    [MemberData(nameof(Budgets))]
    public async Task ATextAndAnIntegerSpillAndEndExact(int degree, int percent)
    {
        (_, Row[] rows) = await Fixture.Async;
        Dictionary<(string, int), PairTotal> expected = rows.GroupBy(r => (r.Name, r.Small))
            .ToDictionary(g => g.Key, g => new PairTotal(g.Key.Name, g.Key.Small, g.Count(), g.Sum(r => r.Value)));
        await ExactAsync(
            degree,
            percent,
            file => file.Scan<Row>().GroupBy(r => (r.Name, r.Small)).Select(g => (g.Key.Name, g.Key.Small, g.Count(), g.Sum(x => x.Value))),
            (PairTotal total) => (total.Name, total.Small),
            expected);
    }

    [Theory]
    [MemberData(nameof(Budgets))]
    public async Task ATextsExtremesByAnIntegerSpillAndEndExact(int degree, int percent)
    {
        (_, Row[] rows) = await Fixture.Async;
        Dictionary<int, KeyTexts> expected = rows.GroupBy(r => r.Key).ToDictionary(
            g => g.Key,
            g => new KeyTexts(g.Key, g.Select(r => r.Text).Min(StringComparer.Ordinal), g.Select(r => r.Text).Max(StringComparer.Ordinal)));
        await ExactAsync(
            degree,
            percent,
            file => file.Scan<Row>().GroupBy(r => r.Key).Select(g => (g.Key, g.Min(x => x.Text), g.Max(x => x.Text))),
            (KeyTexts texts) => texts.Key,
            expected);
    }

    [Theory]
    [MemberData(nameof(Budgets))]
    public async Task ADistinctCountByAnIntegerSpillsAndEndsExact(int degree, int percent)
    {
        (_, Row[] rows) = await Fixture.Async;
        Dictionary<int, KeyCount> expected = rows.GroupBy(r => r.Key).ToDictionary(g => g.Key, g => new KeyCount(g.Key, g.Select(r => r.Value).Distinct().LongCount()));
        await ExactAsync(
            degree,
            percent,
            file => file.Scan<Row>().GroupBy(r => r.Key).Select(g => (g.Key, g.CountDistinct(x => x.Value))),
            (KeyCount count) => count.Key,
            expected);
    }

    [Theory]
    [InlineData(1, 10)]
    [InlineData(14, 10)]
    public async Task ADistinctCountByAWideIntegerHoldsItsPeakFromTheFirstBatch(int degree, int percent)
    {
        // A key of 10⁶ values numbered by value: a lane's first batch meets nearly every page of its span,
        // 4 MB a lane, past what the budget leaves each. Lanes that asked for their arrays' doubling until
        // they first turned held twice their budget before the first wrote its table.
        (_, Row[] rows) = await Fixture.Async;
        Dictionary<int, KeyCount> expected = rows.GroupBy(r => r.Wide).ToDictionary(g => g.Key, g => new KeyCount(g.Key, g.Select(r => r.Value).Distinct().LongCount()));
        await ExactAsync(
            degree,
            percent,
            file => file.Scan<Row>().GroupBy(r => r.Wide).Select(g => (g.Key, g.CountDistinct(x => x.Value))),
            (KeyCount count) => count.Key,
            expected);
    }

    [Theory]
    [InlineData(1, 10)]
    [InlineData(4, 10)]
    public async Task ATextsDistinctCountByAnIntegerSpillsAndEndsExact(int degree, int percent)
    {
        (_, Row[] rows) = await Fixture.Async;
        Dictionary<int, KeyCount> expected = rows.GroupBy(r => r.Key).ToDictionary(g => g.Key, g => new KeyCount(g.Key, g.Select(r => r.Text).Distinct().LongCount()));
        await ExactAsync(
            degree,
            percent,
            file => file.Scan<Row>().GroupBy(r => r.Key).Select(g => (g.Key, g.CountDistinct(x => x.Text))),
            (KeyCount count) => count.Key,
            expected);
    }

    [Fact]
    public async Task LanesThatEachMeetMostKeysRetireAndWriteNothing()
    {
        // The texts, 50 000 values over 1.2M rows, each lane meeting most of them. Under two and a half
        // times what one lane holds at its peak, the lanes that cannot grow merge into one table and
        // retire, and nothing goes to the scratch; without that, they spill. At twice it, the lanes'
        // tables two thirds full and the shared one's doubling pass it: four runs, where they wrote 30.
        (string path, Row[] rows) = await Fixture.Async;
        Dictionary<string, TextTotal> expected = rows.GroupBy(r => r.Text).ToDictionary(g => g.Key, g => new TextTotal(g.Key, g.Count(), g.Sum(r => r.Value)));
        Func<VortexFile, Vorticity.Aggregation> query = file => file.Scan<Row>().GroupBy(r => r.Text).Select(g => (g.Key, g.Count(), g.Sum(x => x.Value)));
        long result = await PeakAsync<TextTotal>(path, query);
        foreach (bool retire in (bool[])[true, false])
        {
            string scratch = Directory.CreateTempSubdirectory("vorticity-group-spill-").FullName;
            try
            {
                QueryMemoryBudget budget = new QueryMemoryBudget(result / 2 * 5);
                await using VortexSession session = Session(14, budget, scratch);
                await using VortexFile file = await session.OpenAsync(path, cancellationToken: Ct);
                Vorticity.Aggregation grouped = query(file);
                grouped.Plan.LanesRetire = retire;
                Dictionary<string, TextTotal> read = [];
                await foreach (TextTotal total in grouped.As<TextTotal>().ToRecordsAsync(Ct))
                {
                    read.Add(total.Text, total);
                }

                Assert.Equal(expected, read);
                int runs = grouped.Plan.LastRun!.SpilledRuns;
                Assert.True(retire ? runs == 0 : runs > 0, $"{runs} runs, retiring {retire}");
                Assert.Equal(0, budget.ReservedBytes);
                Assert.Empty(Directory.EnumerateFileSystemEntries(scratch));
            }
            finally
            {
                Directory.Delete(scratch, recursive: true);
            }
        }
    }

    [Theory]
    [InlineData(200)]
    [InlineData(50)]
    [InlineData(10)]
    public async Task ADistinctOnATextSpillsAndTellsEachValueOnce(int percent)
    {
        // The reader's thread holds the index, the core unable to take a text: under the budget, the values
        // met before its first run are told as they come, the others at the end, each once; a window
        // takes its stretch of them.
        (string path, Row[] rows) = await Fixture.Async;
        HashSet<string> expected = [.. rows.Select(r => r.Name)];
        QueryMemoryBudget large = new QueryMemoryBudget(1L << 30);
        await using (VortexSession measure = Session(4, large, Path.GetTempPath()))
        await using (VortexFile file = await measure.OpenAsync(path, cancellationToken: Ct))
        {
            Assert.Equal(expected.Count, (await file.Scan<Row>().Select(r => r.Name).Distinct().ToListAsync(Ct)).Count);
        }

        string scratch = Directory.CreateTempSubdirectory("vorticity-group-spill-").FullName;
        try
        {
            QueryMemoryBudget budget = new QueryMemoryBudget(large.PeakBytes / 100 * percent);
            await using VortexSession session = Session(4, budget, scratch);
            await using VortexFile file = await session.OpenAsync(path, cancellationToken: Ct);
            List<string> read = await file.Scan<Row>().Select(r => r.Name).Distinct().ToListAsync(Ct);
            Assert.Equal(expected.Count, read.Count);
            Assert.True(expected.SetEquals(read));
            List<string> window = await file.Scan<Row>().Select(r => r.Name).Distinct().Skip(100_000).Take(150_000).ToListAsync(Ct);
            Assert.Equal(150_000, window.Count);
            Assert.Equal(150_000, window.Distinct().Count());
            Assert.True(window.All(expected.Contains));
            Assert.Equal(0, budget.ReservedBytes);
            Assert.True(budget.PeakBytes <= budget.CeilingBytes * 106 / 100, $"peak {budget.PeakBytes:N0} of {budget.CeilingBytes:N0}");
            Assert.Empty(Directory.EnumerateFileSystemEntries(scratch));
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
        }
    }

    /// <summary>The catalogue's aggregates by a text key, each in a query of its own.</summary>
    public static TheoryData<string> Catalogue => new TheoryData<string>
    {
        "count", "sum", "float sum", "average", "least", "greatest", "least text", "greatest text", "variance", "deviation",
        "distinct", "distinct text", "any", "all", "first", "last", "min by", "max by", "custom", "filtered", "filtered by a chosen row",
    };

    [Theory]
    [MemberData(nameof(Catalogue))]
    public async Task EveryAggregateOfTheCatalogueSpillsAndEndsAsItWould(string aggregate)
    {
        // Each answer as text, the same whether the lanes spill or not: every state the catalogue makes
        // goes to the scratch and comes back.
        (string path, _) = await Fixture.Async;
        Dictionary<string, string> expected = await AnswersAsync(path, aggregate, 1, new QueryMemoryBudget(1L << 30), Path.GetTempPath());
        long result = await PeakAsync<NameAnswer>(path, file => Of(file, "count"));
        string scratch = Directory.CreateTempSubdirectory("vorticity-group-spill-").FullName;
        try
        {
            QueryMemoryBudget budget = new QueryMemoryBudget(result / 10);
            Assert.Equal(expected, await AnswersAsync(path, aggregate, 4, budget, scratch));
            Assert.Equal(0, budget.ReservedBytes);
            Assert.Empty(Directory.EnumerateFileSystemEntries(scratch));
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
        }
    }

    [Fact]
    public async Task AStateWithReferencesIsRefusedTypedPastTheBudget()
    {
        // A caller's aggregator whose state holds a reference: no byte holds it, and the lanes cannot
        // spill it; past the budget, the typed refusal, nothing left.
        (string path, _) = await Fixture.Async;
        long result = await PeakAsync<NameAnswer>(path, file => Of(file, "count"));
        string scratch = Directory.CreateTempSubdirectory("vorticity-group-spill-").FullName;
        try
        {
            QueryMemoryBudget budget = new QueryMemoryBudget(result / 10);
            await using VortexSession session = Session(4, budget, scratch);
            await using VortexFile file = await session.OpenAsync(path, cancellationToken: Ct);
            await Assert.ThrowsAsync<VortexMemoryException>(async () =>
            {
                await foreach (NameLargest _ in file.Scan<Row>().GroupBy(r => r.Name).Select(g => (g.Key, g.Aggregate<double, LargestAsText, Largest>(x => x.Price)))
                    .As<NameLargest>().ToRecordsAsync(Ct))
                {
                }
            });
            Assert.Equal(0, budget.ReservedBytes);
            Assert.Empty(Directory.EnumerateFileSystemEntries(scratch));
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
        }
    }

    /// <summary>The query of one aggregate of the catalogue by the names, its answer as the second column.</summary>
    private static Vorticity.Aggregation Of(VortexFile file, string aggregate)
    {
        GroupedScan<Row, Sym<string>> names = file.Scan<Row>().GroupBy(r => r.Name);
        return aggregate switch
        {
            "count" => names.Select(g => (g.Key, g.Count())),
            "sum" => names.Select(g => (g.Key, g.Sum(x => x.Value))),
            "float sum" => names.Select(g => (g.Key, g.Sum(x => x.Price))),
            "average" => names.Select(g => (g.Key, g.Average(x => x.Value))),
            "least" => names.Select(g => (g.Key, g.Min(x => x.Value))),
            "greatest" => names.Select(g => (g.Key, g.Max(x => x.Price))),
            "least text" => names.Select(g => (g.Key, g.Min(x => x.Text))),
            "greatest text" => names.Select(g => (g.Key, g.Max(x => x.Text))),
            "variance" => names.Select(g => (g.Key, g.Variance(x => x.Value))),
            "deviation" => names.Select(g => (g.Key, g.StandardDeviation(x => x.Price))),
            "distinct" => names.Select(g => (g.Key, g.CountDistinct(x => x.Key))),
            "distinct text" => names.Select(g => (g.Key, g.CountDistinct(x => x.Text))),
            "any" => names.Select(g => (g.Key, g.Any(x => x.Value > 990))),
            "all" => names.Select(g => (g.Key, g.All(x => x.Small < 6))),
            "first" => names.Select(g => (g.Key, g.First().Value)),
            "last" => names.Select(g => (g.Key, g.Last().Text)),
            "min by" => names.Select(g => (g.Key, g.MinBy(x => x.Value).Text)),
            "max by" => names.Select(g => (g.Key, g.MaxBy(x => x.Price).Key)),
            "custom" => names.Select(g => (g.Key, g.Aggregate<long, UnitsTotal, long>(x => x.Value))),
            "filtered" => names.Where(g => g.Count() > 4).Select(g => (g.Key, g.Sum(x => x.Value))),
            "filtered by a chosen row" => names.Where(g => g.Last().Small > 2).Select(g => (g.Key, g.Count())),
            _ => throw new ArgumentOutOfRangeException(nameof(aggregate)),
        };
    }

    /// <summary>Each group's answer as text, a float's as its bits, read as the record the aggregate's answer takes.</summary>
    private static async Task<Dictionary<string, string>> AnswersAsync(string path, string aggregate, int degree, QueryMemoryBudget budget, string scratch)
    {
        await using VortexSession session = Session(degree, budget, scratch);
        await using VortexFile file = await session.OpenAsync(path, cancellationToken: Ct);
        Vorticity.Aggregation grouped = Of(file, aggregate);
        Dictionary<string, string> answers = [];
        switch (aggregate)
        {
            case "float sum":
                await foreach (NameDouble answer in grouped.As<NameDouble>().ToRecordsAsync(Ct))
                {
                    answers.Add(answer.Name, $"{BitConverter.DoubleToInt64Bits(answer.Value):X}");
                }

                break;
            case "average" or "greatest" or "variance" or "deviation":
                await foreach (NameMaybeDouble answer in grouped.As<NameMaybeDouble>().ToRecordsAsync(Ct))
                {
                    answers.Add(answer.Name, answer.Value is double value ? $"{BitConverter.DoubleToInt64Bits(value):X}" : "null");
                }

                break;
            case "least":
                await foreach (NameMaybeLong answer in grouped.As<NameMaybeLong>().ToRecordsAsync(Ct))
                {
                    answers.Add(answer.Name, $"{answer.Value}");
                }

                break;
            case "max by":
                await foreach (NameMaybeInt answer in grouped.As<NameMaybeInt>().ToRecordsAsync(Ct))
                {
                    answers.Add(answer.Name, $"{answer.Value}");
                }

                break;
            case "any" or "all":
                await foreach (NameBool answer in grouped.As<NameBool>().ToRecordsAsync(Ct))
                {
                    answers.Add(answer.Name, $"{answer.Value}");
                }

                break;
            case "least text" or "greatest text" or "last" or "min by":
                await foreach (NameText answer in grouped.As<NameText>().ToRecordsAsync(Ct))
                {
                    answers.Add(answer.Name, answer.Value ?? "null");
                }

                break;
            default:
                await foreach (NameAnswer answer in grouped.As<NameAnswer>().ToRecordsAsync(Ct))
                {
                    answers.Add(answer.Name, $"{answer.Value}");
                }

                break;
        }

        return answers;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(14)]
    public async Task AFloatSumBySpilledGroupsIsTheSameBitsAtEveryDegree(int degree)
    {
        // The indexed sum's states merge in any order: read back from runs in sections, a group's states
        // come together in another order than the lanes' merge, the same bits.
        (string path, _) = await Fixture.Async;
        Func<VortexFile, Vorticity.Aggregation> query = file => file.Scan<Row>().GroupBy(r => r.Name).Select(g => (g.Key, g.Sum(x => x.Price), g.Average(x => x.Price)));
        Dictionary<string, (long, long)> expected = await BitsAsync(path, query, 1, new QueryMemoryBudget(1L << 30));
        long result = await PeakAsync<NamePrice>(path, query);
        string scratch = Directory.CreateTempSubdirectory("vorticity-group-spill-").FullName;
        try
        {
            QueryMemoryBudget budget = new QueryMemoryBudget(result / 10);
            Assert.Equal(expected, await BitsAsync(path, query, degree, budget, scratch));
            Assert.Equal(0, budget.ReservedBytes);
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
        }

        static async Task<Dictionary<string, (long, long)>> BitsAsync(string path, Func<VortexFile, Vorticity.Aggregation> query, int degree, QueryMemoryBudget budget, string? scratch = null)
        {
            await using VortexSession session = Session(degree, budget, scratch ?? Path.GetTempPath());
            await using VortexFile file = await session.OpenAsync(path, cancellationToken: Ct);
            Vorticity.Aggregation grouped = query(file);
            Dictionary<string, (long, long)> bits = [];
            await foreach (NamePrice price in grouped.As<NamePrice>().ToRecordsAsync(Ct))
            {
                bits.Add(price.Name, (BitConverter.DoubleToInt64Bits(price.Sum), BitConverter.DoubleToInt64Bits(price.Mean ?? double.NaN)));
            }

            Assert.True(scratch is null || grouped.Plan.LastRun!.SpilledRuns > 0, "no run written");
            return bits;
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public async Task AnOrderAndAWindowOnSpilledGroupsHoldTheirAnswers(int degree)
    {
        // The spilled parts sorted in runs by the reader, then the window's first groups.
        (string path, Row[] rows) = await Fixture.Async;
        NameTotal[] expected = [.. rows.GroupBy(r => r.Name).Select(g => new NameTotal(g.Key, g.Count(), g.Sum(r => r.Value)))
            .OrderByDescending(t => t.Total).ThenBy(t => t.Name, StringComparer.Ordinal).Take(1_000)];
        string scratch = Directory.CreateTempSubdirectory("vorticity-group-spill-").FullName;
        try
        {
            long result = await PeakAsync<NameTotal>(path, Names);
            QueryMemoryBudget budget = new QueryMemoryBudget(result / 10);
            await using VortexSession session = Session(degree, budget, scratch);
            await using VortexFile file = await session.OpenAsync(path, cancellationToken: Ct);
            Vorticity.Aggregation ordered = file.Scan<Row>().GroupBy(r => r.Name)
                .OrderByDescending(g => g.Sum(x => x.Value)).ThenBy(g => g.Key).Take(1_000)
                .Select(g => (g.Key, g.Count(), g.Sum(x => x.Value)));
            List<NameTotal> read = [];
            await foreach (NameTotal total in ordered.As<NameTotal>().ToRecordsAsync(Ct))
            {
                read.Add(total);
            }

            Assert.Equal(expected, read);
            Assert.True(ordered.Plan.LastRun!.SpilledRuns > 0, "no run written");
            Assert.Equal(0, budget.ReservedBytes);
            Assert.Empty(Directory.EnumerateFileSystemEntries(scratch));
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(14)]
    public async Task AConsumerThatStopsAtItsFirstGroupGivesEverythingBack(int degree)
    {
        (string path, _) = await Fixture.Async;
        string scratch = Directory.CreateTempSubdirectory("vorticity-group-spill-").FullName;
        try
        {
            long result = await PeakAsync<NameTotal>(path, Names);
            QueryMemoryBudget budget = new QueryMemoryBudget(result / 10);
            await using VortexSession session = Session(degree, budget, scratch);
            await using VortexFile file = await session.OpenAsync(path, cancellationToken: Ct);
            Vorticity.Aggregation grouped = Names(file);
            await foreach (NameTotal _ in grouped.As<NameTotal>().ToRecordsAsync(Ct))
            {
                break;
            }

            Assert.True(grouped.Plan.LastRun!.SpilledRuns > 0, "no run written");
            Assert.Equal(0, budget.ReservedBytes);
            Assert.Empty(Directory.EnumerateFileSystemEntries(scratch));
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
        }
    }

    [Fact]
    public async Task ACancelledSpillLeavesNoFileAndNoReservation()
    {
        (string path, _) = await Fixture.Async;
        string scratch = Directory.CreateTempSubdirectory("vorticity-group-spill-").FullName;
        try
        {
            long result = await PeakAsync<NameTotal>(path, Names);
            QueryMemoryBudget budget = new QueryMemoryBudget(result / 10);
            await using VortexSession session = Session(4, budget, scratch);
            await using VortexFile file = await session.OpenAsync(path, cancellationToken: Ct);
            for (int after = 1; after <= 256; after *= 4)
            {
                using CancellationTokenSource cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
                cancel.CancelAfter(TimeSpan.FromMilliseconds(after));
                try
                {
                    await foreach (NameTotal _ in Names(file).As<NameTotal>().ToRecordsAsync(cancel.Token))
                    {
                    }
                }
                catch (OperationCanceledException)
                {
                }

                Assert.Equal(0, budget.ReservedBytes);
                Assert.Empty(Directory.EnumerateFileSystemEntries(scratch));
            }
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
        }
    }

    [Fact]
    public async Task TwoQueriesSpillAtOnceAndBothEndExact()
    {
        (string path, Row[] rows) = await Fixture.Async;
        Dictionary<string, NameTotal> expected = rows.GroupBy(r => r.Name).ToDictionary(g => g.Key, g => new NameTotal(g.Key, g.Count(), g.Sum(r => r.Value)));
        string scratch = Directory.CreateTempSubdirectory("vorticity-group-spill-").FullName;
        try
        {
            long result = await PeakAsync<NameTotal>(path, Names);
            QueryMemoryBudget first = new QueryMemoryBudget(result / 5);
            QueryMemoryBudget second = new QueryMemoryBudget(result / 5);
            await Task.WhenAll(ReadAsync(first), ReadAsync(second));
            Assert.Equal(0, first.ReservedBytes);
            Assert.Equal(0, second.ReservedBytes);
            Assert.Empty(Directory.EnumerateFileSystemEntries(scratch));

            async Task ReadAsync(QueryMemoryBudget budget)
            {
                await using VortexSession session = Session(4, budget, scratch);
                await using VortexFile file = await session.OpenAsync(path, cancellationToken: Ct);
                Vorticity.Aggregation grouped = Names(file);
                Dictionary<string, NameTotal> read = [];
                await foreach (NameTotal total in grouped.As<NameTotal>().ToRecordsAsync(Ct))
                {
                    read.Add(total.Name, total);
                }

                Assert.Equal(expected, read);
                Assert.True(grouped.Plan.LastRun!.SpilledRuns > 0, "no run written");
            }
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
        }
    }

    [Fact]
    public async Task AScratchThatCannotBeWrittenFailsTheQueryCleanly()
    {
        (string path, _) = await Fixture.Async;
        long result = await PeakAsync<NameTotal>(path, Names);
        QueryMemoryBudget budget = new QueryMemoryBudget(result / 10);
        await using VortexSession session = Session(4, budget, Path.Combine(Path.GetTempPath(), $"vorticity-absent-{Guid.NewGuid():N}", "scratch"));
        await using VortexFile file = await session.OpenAsync(path, cancellationToken: Ct);
        await Assert.ThrowsAnyAsync<IOException>(async () =>
        {
            await foreach (NameTotal _ in Names(file).As<NameTotal>().ToRecordsAsync(Ct))
            {
            }
        });
        Assert.Equal(0, budget.ReservedBytes);
    }

    [Fact]
    public async Task AScratchBudgetTheSpillPassesFailsTheQueryCleanly()
    {
        (string path, _) = await Fixture.Async;
        string scratch = Directory.CreateTempSubdirectory("vorticity-group-spill-").FullName;
        try
        {
            long result = await PeakAsync<NameTotal>(path, Names);
            QueryMemoryBudget budget = new QueryMemoryBudget(result / 10);
            ScratchBudget room = new ScratchBudget(64 * 1024);
            await using VortexSession session = VortexSession.Create(options =>
            {
                options.MaxDegreeOfParallelism = 4;
                options.MemoryBudget = budget;
                options.ScratchDirectory = scratch;
                options.ScratchBudget = room;
            });

            await using VortexFile file = await session.OpenAsync(path, cancellationToken: Ct);
            VortexMemoryException refused = await Assert.ThrowsAsync<VortexMemoryException>(async () =>
            {
                await foreach (NameTotal _ in Names(file).As<NameTotal>().ToRecordsAsync(Ct))
                {
                }
            });
            Assert.Contains("scratch budget", refused.Message, StringComparison.Ordinal);
            Assert.Equal(0, budget.ReservedBytes);
            Assert.Equal(0, room.ReservedBytes);
            Assert.Empty(Directory.EnumerateFileSystemEntries(scratch));
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
        }
    }

    /// <summary>A count and a sum by the names, a text key.</summary>
    private static Vorticity.Aggregation Names(VortexFile file) => file.Scan<Row>().GroupBy(r => r.Name).Select(g => (g.Key, g.Count(), g.Sum(x => x.Value)));

    /// <summary>
    /// The query under a budget of <paramref name="percent"/> of its result, at <paramref name="degree"/>:
    /// every group's answer, written to the scratch under half and a tenth of it, nothing at one lane under
    /// twice it; the peak within 6 % of the ceiling, nothing reserved and no file left after.
    /// </summary>
    private static async Task ExactAsync<TResult, TKey>(
        int degree, int percent, Func<VortexFile, Vorticity.Aggregation> query, Func<TResult, TKey> keyOf, Dictionary<TKey, TResult> expected)
        where TResult : IVortexRecord<TResult>
        where TKey : notnull
    {
        (string path, _) = await Fixture.Async;
        string scratch = Directory.CreateTempSubdirectory("vorticity-group-spill-").FullName;
        try
        {
            long result = await PeakAsync<TResult>(path, query);
            QueryMemoryBudget budget = new QueryMemoryBudget(result / 100 * percent);
            await using VortexSession session = Session(degree, budget, scratch);
            await using VortexFile file = await session.OpenAsync(path, cancellationToken: Ct);
            Vorticity.Aggregation grouped = query(file);
            Dictionary<TKey, TResult> read = [];
            await foreach (TResult record in grouped.As<TResult>().ToRecordsAsync(Ct))
            {
                read.Add(keyOf(record), record);
            }

            Assert.Equal(expected.Count, read.Count);
            foreach ((TKey key, TResult answer) in expected)
            {
                Assert.Equal(answer, read[key]);
            }

            int runs = grouped.Plan.LastRun!.SpilledRuns;
            if (percent <= 50)
            {
                Assert.True(runs > 0, $"no run written under {budget.CeilingBytes:N0} bytes of {result:N0}");
            }
            else if (degree == 1)
            {
                Assert.Equal(0, runs);
            }

            Assert.True(budget.PeakBytes <= budget.CeilingBytes * 106 / 100, $"peak {budget.PeakBytes:N0} of {budget.CeilingBytes:N0}");
            Assert.Equal(0, budget.ReservedBytes);
            Assert.Empty(Directory.EnumerateFileSystemEntries(scratch));
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
        }
    }

    private static VortexSession Session(int degree, QueryMemoryBudget budget, string scratch) => VortexSession.Create(options =>
    {
        options.MaxDegreeOfParallelism = degree;
        options.MemoryBudget = budget;
        options.ScratchDirectory = scratch;
    });

    /// <summary>What the query holds at most at one lane, under a budget that grants it all: its result, as a lane holds it.</summary>
    private static async Task<long> PeakAsync<TResult>(string path, Func<VortexFile, Vorticity.Aggregation> query)
        where TResult : IVortexRecord<TResult>
    {
        QueryMemoryBudget large = new QueryMemoryBudget(1L << 30);
        await using VortexSession session = VortexSession.Create(options =>
        {
            options.MaxDegreeOfParallelism = 1;
            options.MemoryBudget = large;
        });

        await using VortexFile file = await session.OpenAsync(path, cancellationToken: Ct);
        await foreach (TResult _ in query(file).As<TResult>().ToRecordsAsync(Ct))
        {
        }

        return large.PeakBytes;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// The rows, written once for the class: 300 000 names of twelve bytes, 150 000 integer keys, a small
    /// integer, a value of a thousand, texts of seven bytes among 50 000, a price over forty binades,
    /// of both signs, NaN one row in 97, and integers over a span of 10⁶.
    /// </summary>
    private static class Fixture
    {
        private static readonly Lazy<Task<(string Path, Row[] Rows)>> s_written = new Lazy<Task<(string, Row[])>>(WriteAsync);

        internal static Task<(string Path, Row[] Rows)> Async => s_written.Value;

        private static async Task<(string, Row[])> WriteAsync()
        {
            Row[] rows = new Row[GroupSpillTests.Rows];
            for (int row = 0; row < rows.Length; row++)
            {
                ulong mix = Mix((ulong)row);
                double price = row % 97 == 0 ? double.NaN : Math.ScaleB(((mix >> 11) % 1_000_003) + 1, (int)((mix >> 52) % 41) - 20) * ((mix & 1) == 0 ? 1 : -1);
                rows[row] = new Row(
                    $"name-{mix % 300_000:D7}",
                    (int)((mix >> 20) % 150_000),
                    (int)((mix >> 40) % 7),
                    (long)((mix >> 44) % 1_000),
                    $"t-{(mix >> 8) % 50_000:D5}",
                    price,
                    (int)((mix >> 24) % 1_000_000));
            }

            string directory = System.IO.Path.Combine(AppContext.BaseDirectory, "group-spill");
            Directory.CreateDirectory(directory);
            string path = System.IO.Path.Combine(directory, $"rows-{Environment.ProcessId}.vortex");
            await using (VortexFileWriter writer = VortexSession.Default.CreateWriter<Row>(path))
            {
                await writer.WriteAsync<Row>(rows, CancellationToken.None);
                await writer.CompleteAsync(CancellationToken.None);
            }

            return (path, rows);
        }
    }

    private static ulong Mix(ulong x)
    {
        x += 0x9E37_79B9_7F4A_7C15UL;
        x = (x ^ (x >> 30)) * 0xBF58_476D_1CE4_E5B9UL;
        x = (x ^ (x >> 27)) * 0x94D0_49BB_1331_11EBUL;
        return x ^ (x >> 31);
    }

    [VortexRecord]
    public partial record struct Row(string Name, int Key, int Small, long Value, string Text, double Price, int Wide);

    [VortexRecord]
    public partial record struct NamePrice(string Name, double Sum, double? Mean);

    [VortexRecord]
    public partial record struct TextTotal(string Text, long Count, long Total);

    [VortexRecord]
    public partial record struct NameAnswer(string Name, long Value);

    [VortexRecord]
    public partial record struct NameDouble(string Name, double Value);

    [VortexRecord]
    public partial record struct NameMaybeDouble(string Name, double? Value);

    [VortexRecord]
    public partial record struct NameMaybeLong(string Name, long? Value);

    [VortexRecord]
    public partial record struct NameMaybeInt(string Name, int? Value);

    [VortexRecord]
    public partial record struct NameBool(string Name, bool Value);

    [VortexRecord]
    public partial record struct NameText(string Name, string? Value);

    [VortexRecord]
    public partial record struct NameLargest(string Name, Largest Value);

    [VortexRecord]
    public partial record struct NameTotal(string Name, long Count, long Total);

    [VortexRecord]
    public partial record struct PairTotal(string Name, int Small, long Count, long Total);

    [VortexRecord]
    public partial record struct KeyTexts(int Key, string? Least, string? Greatest);

    [VortexRecord]
    public partial record struct KeyCount(int Key, long Count);
}
