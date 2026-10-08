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
    /// integer, a value of a thousand, and texts of seven bytes among 50 000.
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
                rows[row] = new Row(
                    $"name-{mix % 300_000:D7}",
                    (int)((mix >> 20) % 150_000),
                    (int)((mix >> 40) % 7),
                    (long)((mix >> 44) % 1_000),
                    $"t-{(mix >> 8) % 50_000:D5}");
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
    public partial record struct Row(string Name, int Key, int Small, long Value, string Text);

    [VortexRecord]
    public partial record struct NameTotal(string Name, long Count, long Total);

    [VortexRecord]
    public partial record struct PairTotal(string Name, int Small, long Count, long Total);

    [VortexRecord]
    public partial record struct KeyTexts(int Key, string? Least, string? Greatest);

    [VortexRecord]
    public partial record struct KeyCount(int Key, long Count);
}
