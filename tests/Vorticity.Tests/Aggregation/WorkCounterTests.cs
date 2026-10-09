using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Aggregating;
using Xunit;

namespace Vorticity.Tests.Aggregation;

/// <summary>
/// The work a group by does, counted, not timed: on files cut from the high-cardinality bench's, each
/// query's rows by lane, groups, the arrays its tables took as they grew and the bytes they copied, the
/// key blocks by how they were grouped, the merge's entries, the most it reserved, and the core's
/// counts. At one lane every count is exact and written below; at four, where the queue hands out its
/// ranges as the lanes come, bounded. A change that moves a count fails here until the line is written
/// again, in the commit that moved it, with the reason in its message: a ratchet on the work, which a
/// shared machine's clock cannot resolve.
/// </summary>
public sealed partial class WorkCounterTests
{
    private const int SpreadRows = 300_000;
    private const int PageRows = 200_000;
    private const int VisitRows = 300_000;

    /// <summary>The counts at one lane, a line a query, as <see cref="Line"/> writes them.</summary>
    private static readonly string[] Expected =
    [
        "count and sum by 10^3 values: result 1501672601, rows 300000, lanes 1, lane groups 1000, arrays 8 of 40888 B, copied 0 B, blocks 0/0/4, merge 0 parts 0 entries, peak 1310720 B",
        "count and sum by 10^5 values: result 1501672601, rows 300000, lanes 1, lane groups 95013, arrays 19 of 3653560 B, copied 0 B, blocks 0/0/4, merge 0 parts 0 entries, peak 4177976 B",
        "count and sum by 10^6 values: result 1501672601, rows 300000, lanes 1, lane groups 259343, arrays 35 of 12566512 B, copied 0 B, blocks 0/0/4, merge 0 parts 0 entries, peak 12566640 B",
        "count and sum by an ordered key: result 1501672601, rows 300000, lanes 1, lane groups 1, arrays 51 of 3720508 B, copied 0 B, blocks 4/0/0, merge 0 parts 0 entries, peak 3606448 B",
        "count and sum by a strided long: result 1501672601, rows 300000, lanes 1, lane groups 259147, arrays 78 of 27797100 B, copied 0 B, blocks 0/0/4, merge 0 parts 0 entries, peak 17680988 B",
        "count and sum by 10^6 values, the core: result 1501672601, rows 300000, lanes 1, lane groups 37176, arrays 72 of 18380500 B, copied 0 B, blocks 0/0/4, merge 0 parts 0 entries, core: flushes 2 of 267855, bypassed 0, applied 267855 made 259343",
        "distinct count by 10^5 values: result 299955, rows 300000, lanes 1, lane groups 95013, arrays 65 of 30391360 B, copied 8388352 B, blocks 0/0/4, merge 0 parts 0 entries, peak 19906560 B",
        "distinct values of 10^6: result 129508936719, rows 0, lanes 0, lane groups 0, arrays 0 of 0 B, copied 0 B, blocks 0/0/0, merge  parts 0 entries, peak 0 B",
        "count by a text: result 200000, rows 200000, lanes 1, lane groups 49081, arrays 50 of 7730472 B, copied 1833792 B, blocks 0/12/1, merge 0 parts 0 entries, peak 6029368 B",
        "count by a text and an int: result 200000, rows 200000, lanes 1, lane groups 196058, arrays 94 of 14492304 B, copied 2357952 B, blocks 0/0/0, merge 0 parts 0 entries, peak 12126112 B",
        "least and greatest text by 10^3 values: result 56000, rows 200000, lanes 1, lane groups 1000, arrays 21 of 171968 B, copied 0 B, blocks 0/0/13, merge 0 parts 0 entries, peak 1310720 B",
        "distinct users by day: result 299552, rows 300000, lanes 1, lane groups 1, arrays 370 of 2940924 B, copied 0 B, blocks 4/0/0, merge 0 parts 0 entries, peak 2621440 B",
    ];

    [Fact]
    public async Task EveryQueryDoesTheWorkItDidAtOneLane()
    {
        List<string> lines = [];
        foreach ((string name, Func<VortexFile, Task<(long Result, AggregationPlan Plan)>> run, Func<Task<string>> path) in Queries())
        {
            await using VortexSession session = VortexSession.Create(options => options.MaxDegreeOfParallelism = 1);
            await using VortexFile file = await session.OpenAsync(await path(), cancellationToken: Ct);
            (long result, AggregationPlan plan) = await run(file);
            lines.Add(Line(name, result, plan));
        }

        // Every line printed when one differs, to be written again above once the change is meant.
        List<string> differ = [.. lines.Where((line, i) => i >= Expected.Length || Expected[i] != line)];
        Assert.True(differ.Count == 0 && lines.Count == Expected.Length, $"{differ.Count} of {lines.Count} lines differ; the counts now:\n{string.Join('\n', lines)}");
    }

    [Fact]
    public async Task AtFourLanesTheWorkStaysWithinItsBounds()
    {
        foreach ((string name, Func<VortexFile, Task<(long Result, AggregationPlan Plan)>> run, Func<Task<string>> path) in Queries())
        {
            await using VortexSession one = VortexSession.Create(options => options.MaxDegreeOfParallelism = 1);
            await using VortexFile alone = await one.OpenAsync(await path(), cancellationToken: Ct);
            (long result, AggregationPlan plan) = await run(alone);
            Counts single = Counts.Of(plan);

            await using VortexSession four = VortexSession.Create(options => options.MaxDegreeOfParallelism = 4);
            await using VortexFile file = await four.OpenAsync(await path(), cancellationToken: Ct);
            (long fourResult, AggregationPlan fourPlan) = await run(file);
            Counts counts = Counts.Of(fourPlan);
            string line = Line(name, fourResult, fourPlan);

            // The same answer, every row folded once, by four lanes at most; each lane's table grows as
            // one alone would, a lane's at most what the one lane's took; the merge puts no more groups
            // into a table than the lanes hold.
            Assert.True(fourResult == result, line);
            Assert.True(counts.Rows == single.Rows, line);
            Assert.True(counts.Lanes is >= 1 and <= 4 || (counts.Lanes == 0 && single.Lanes == 0), line);
            Assert.True(counts.Arrays <= (counts.Lanes * single.Arrays) + 64, line);
            Assert.True(counts.Copied <= counts.Lanes * Math.Max(single.Copied, 1L << 20), line);
            Assert.True(counts.MergeEntries <= counts.LaneGroups, line);
        }
    }

    /// <summary>The queries, each with the file it reads.</summary>
    private static IEnumerable<(string Name, Func<VortexFile, Task<(long Result, AggregationPlan Plan)>> Run, Func<Task<string>> Path)> Queries()
    {
        yield return ("count and sum by 10^3 values", file => TotalAsync(file, s => s.K3), SpreadAsync);
        yield return ("count and sum by 10^5 values", file => TotalAsync(file, s => s.K5), SpreadAsync);
        yield return ("count and sum by 10^6 values", file => TotalAsync(file, s => s.K6), SpreadAsync);
        yield return ("count and sum by an ordered key", file => TotalAsync(file, s => s.Ordered), SpreadAsync);
        yield return ("count and sum by a strided long", StridedAsync, SpreadAsync);
        yield return ("count and sum by 10^6 values, the core", file => TotalAsync(file, s => s.K6, core: true), SpreadAsync);
        yield return ("distinct count by 10^5 values", DistinctByKeyAsync, SpreadAsync);
        yield return ("distinct values of 10^6", DistinctAsync, SpreadAsync);
        yield return ("count by a text", UrlsAsync, PagesAsync);
        yield return ("count by a text and an int", UrlSmallsAsync, PagesAsync);
        yield return ("least and greatest text by 10^3 values", TextExtremesAsync, PagesAsync);
        yield return ("distinct users by day", UsersByDayAsync, VisitsAsync);
    }

    private static async Task<(long, AggregationPlan)> TotalAsync(VortexFile file, Func<Probe<Spread>, Sym<int>> key, bool core = false)
    {
        Vorticity.Aggregation query = file.Scan<Spread>().GroupBy(key).Select(g => (g.Key, g.Count(), g.Sum(s => s.Value)));
        if (core)
        {
            query.Plan.Core = true;
            query.Plan.CoreLanes = 1;
        }

        long rows = 0;
        await foreach (KeyTotal total in query.As<KeyTotal>().ToRecordsAsync(Ct))
        {
            rows += total.Count + total.Total;
        }

        return (rows, query.Plan);
    }

    private static async Task<(long, AggregationPlan)> StridedAsync(VortexFile file)
    {
        Vorticity.Aggregation query = file.Scan<Spread>().GroupBy(s => s.Strided).Select(g => (g.Key, g.Count(), g.Sum(s => s.Value)));
        long rows = 0;
        await foreach (LongTotal total in query.As<LongTotal>().ToRecordsAsync(Ct))
        {
            rows += total.Count + total.Total;
        }

        return (rows, query.Plan);
    }

    private static async Task<(long, AggregationPlan)> DistinctByKeyAsync(VortexFile file)
    {
        Vorticity.Aggregation query = file.Scan<Spread>().GroupBy(s => s.K5).Select(g => (g.Key, g.CountDistinct(s => s.Value)));
        long values = 0;
        await foreach (KeyCount count in query.As<KeyCount>().ToRecordsAsync(Ct))
        {
            values += count.Count;
        }

        return (values, query.Plan);
    }

    private static async Task<(long, AggregationPlan)> DistinctAsync(VortexFile file)
    {
        Aggregation<int> distinct = file.Scan<Spread>().Select(s => s.K6).Distinct();
        long values = 0;
        await foreach (int value in distinct.WithCancellation(Ct))
        {
            values += value;
        }

        return (values, ((DistinctQuery)distinct.Query).Plan);
    }

    private static async Task<(long, AggregationPlan)> UrlsAsync(VortexFile file)
    {
        Vorticity.Aggregation query = file.Scan<Page>().GroupBy(p => p.Url).Select(g => (g.Key, g.Count()));
        long rows = 0;
        await foreach (TextCount count in query.As<TextCount>().ToRecordsAsync(Ct))
        {
            rows += count.Count;
        }

        return (rows, query.Plan);
    }

    private static async Task<(long, AggregationPlan)> UrlSmallsAsync(VortexFile file)
    {
        Vorticity.Aggregation query = file.Scan<Page>().GroupBy(p => (p.Url, p.Small)).Select(g => (g.Key.Item1, g.Key.Item2, g.Count()));
        long rows = 0;
        await foreach (TextSmallCount count in query.As<TextSmallCount>().ToRecordsAsync(Ct))
        {
            rows += count.Count;
        }

        return (rows, query.Plan);
    }

    private static async Task<(long, AggregationPlan)> TextExtremesAsync(VortexFile file)
    {
        Vorticity.Aggregation query = file.Scan<Page>().GroupBy(p => p.Key).Select(g => (g.Key, g.Min(p => p.Url), g.Max(p => p.Url)));
        long length = 0;
        await foreach (KeyTexts texts in query.As<KeyTexts>().ToRecordsAsync(Ct))
        {
            length += (texts.Least?.Length ?? 0) + (texts.Greatest?.Length ?? 0);
        }

        return (length, query.Plan);
    }

    private static async Task<(long, AggregationPlan)> UsersByDayAsync(VortexFile file)
    {
        Vorticity.Aggregation query = file.Scan<Visit>().GroupBy(v => v.Day).Select(g => (g.Key, g.CountDistinct(v => v.User)));
        long users = 0;
        await foreach (KeyCount count in query.As<KeyCount>().ToRecordsAsync(Ct))
        {
            users += count.Count;
        }

        return (users, query.Plan);
    }

    /// <summary>A query's counts on a line: what it gave, then what its run did.</summary>
    private static string Line(string name, long result, AggregationPlan plan)
    {
        Counts counts = Counts.Of(plan);
        (long range, long code, long hashed) = plan.LastKeyBlocks;
        StringBuilder line = new StringBuilder();
        line.Append(CultureInfo.InvariantCulture, $"{name}: result {result}, rows {counts.Rows}, lanes {counts.Lanes}, lane groups {counts.LaneGroups}, ");
        line.Append(CultureInfo.InvariantCulture, $"arrays {counts.Arrays} of {counts.ArrayBytes} B, copied {counts.Copied} B, blocks {range}/{code}/{hashed}, ");
        line.Append(CultureInfo.InvariantCulture, $"merge {plan.LastRun?.MergeParts} parts {counts.MergeEntries} entries");

        // The core's parts are the top bits of a hash under a seed drawn once a process: how many
        // entries each part takes, so the batches the lanes fill, the bursts, the splits and the peak
        // they bring, change from one process to the next. What its lanes fold, empty and apply does not.
        if (plan.LastRun?.Core is { } core)
        {
            line.Append(CultureInfo.InvariantCulture, $", core: flushes {core.Flushes} of {core.FlushedGroups}, bypassed {core.BypassedRows}, ");
            line.Append(CultureInfo.InvariantCulture, $"applied {core.AppliedEntries} made {core.MadeGroups}");
        }
        else
        {
            line.Append(CultureInfo.InvariantCulture, $", peak {plan.LastPeakBytes} B");
        }

        return line.ToString();
    }

    /// <summary>A run's counts summed over its lanes.</summary>
    private readonly record struct Counts(long Rows, int Lanes, long LaneGroups, long Arrays, long ArrayBytes, long Copied, long MergeEntries)
    {
        internal static Counts Of(AggregationPlan plan)
        {
            if (plan.LastRun is not { } run)
            {
                return default;
            }

            (long rows, long groups, long arrays, long bytes, long copied) = (0, 0, 0, 0, 0);
            foreach (AggregationRun.Lane lane in run.Lanes)
            {
                rows += lane.Rows;
                groups += lane.Groups;
                arrays += lane.Arrays;
                bytes += lane.ArrayBytes;
                copied += lane.CopiedBytes;
            }

            // A lane on the core folds apart from its cache the rows it bypasses it with.
            return new Counts(rows + (run.Core?.BypassedRows ?? 0), run.Lanes.Length, groups, arrays, bytes, copied, run.MergeEntries);
        }
    }

    /// <summary>
    /// Keys of 10³, 10⁵ and 10⁶ values in no order, a key in the order of the rows four rows a value, a
    /// long at a stride of 2^22 over 10⁶ values, and a value: the bench's spread files, cut short.
    /// </summary>
    private static Task<string> SpreadAsync() => SharedFiles.GetAsync($"{nameof(WorkCounterTests)}-spread-{SpreadRows}", path => WriteAsync(path, SpreadRows, row =>
    {
        ulong a = Mix((ulong)row);
        ulong b = Mix(a);
        return new Spread((int)(a % 1_000), (int)((a >> 32) % 100_000), (int)(b % 1_000_000), row / 4, (long)((b >> 32) % 1_000_000) << 22, (long)(Mix(b) % 10_000));
    }));

    /// <summary>A text of about thirty bytes among fifty thousand, a small integer, and a key of 10³ values: the bench's pages, cut short.</summary>
    private static Task<string> PagesAsync() => SharedFiles.GetAsync($"{nameof(WorkCounterTests)}-pages-{PageRows}", path => WriteAsync(path, PageRows, row =>
    {
        ulong a = Mix((ulong)row);
        return new Page(string.Create(CultureInfo.InvariantCulture, $"https://example.org/p/{a % 50_000:D6}"), (int)((a >> 20) % 100), (int)((a >> 40) % 1_000));
    }));

    /// <summary>A hundred days in order, a user among a million drawn for each visit: the bench's visits, cut short.</summary>
    private static Task<string> VisitsAsync() => SharedFiles.GetAsync($"{nameof(WorkCounterTests)}-visits-{VisitRows}", path => WriteAsync(path, VisitRows, row =>
        new Visit((int)((long)row * 100 / VisitRows), (int)(Mix((ulong)row) % 1_000_000))));

    private static async Task WriteAsync<T>(string path, int rows, Func<int, T> make)
        where T : IVortexRecord<T>
    {
        T[] data = new T[rows];
        for (int row = 0; row < rows; row++)
        {
            data[row] = make(row);
        }

        await using VortexFileWriter writer = VortexSession.Default.CreateWriter<T>(path);
        await writer.WriteAsync<T>(data, Ct);
        await writer.CompleteAsync(Ct);
    }

    private static ulong Mix(ulong x)
    {
        x += 0x9E37_79B9_7F4A_7C15UL;
        x = (x ^ (x >> 30)) * 0xBF58_476D_1CE4_E5B9UL;
        x = (x ^ (x >> 27)) * 0x94D0_49BB_1331_11EBUL;
        return x ^ (x >> 31);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [VortexRecord]
    public partial record struct Spread(int K3, int K5, int K6, int Ordered, long Strided, long Value);

    [VortexRecord]
    public partial record struct Page(string Url, int Small, int Key);

    [VortexRecord]
    public partial record struct Visit(int Day, int User);

    [VortexRecord]
    public partial record struct KeyTotal(int Key, long Count, long Total);

    [VortexRecord]
    public partial record struct LongTotal(long Key, long Count, long Total);

    [VortexRecord]
    public partial record struct KeyCount(int Key, long Count);

    [VortexRecord]
    public partial record struct TextCount(string Url, long Count);

    [VortexRecord]
    public partial record struct TextSmallCount(string Url, int Small, long Count);

    [VortexRecord]
    public partial record struct KeyTexts(int Key, string? Least, string? Greatest);
}
