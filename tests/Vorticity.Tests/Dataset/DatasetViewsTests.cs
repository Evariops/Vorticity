// The automatic views (spec H, H1): an object's counts keep a quantile sketch by value while a value holds enough rows on
// average. The partials of question families an earlier version learned and kept under learned/ are read by nothing now:
// a vacuum deletes them whatever their age.
//
// THE ORACLE IS THE ROWS: every answer a sketch gives equals the one the rows give, and the answers after the vacuum those
// before it.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.IO;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed partial class DatasetViewsTests
{
    private static readonly DateTime Start = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// Four appends of 25 000 rows, consolidated: a column of 300 values, 333 rows each, keeps a quantile sketch by value and
    /// answers a quantile by value from its counts, reading a few KiB; one of 1 000 values, 100 rows each, keeps none and
    /// reads its rows (H1).
    /// </summary>
    [Fact]
    public async Task AValueKeepsItsSketchWhileItHoldsEnoughRows()
    {
        Random random = new Random(41);
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Measure.Schema, new DatasetOptions
        {
            Layout = new DatasetLayout { SortKey = [LayoutValue.Column(nameof(Measure.Few))], ObjectBytes = 64 << 20, MergeFanout = 4, CalmAfter = null },
            ContentsObjectBytes = 0,
        }, Ct);
        for (int a = 0; a < 4; a++)
        {
            Measure[] rows = [.. Enumerable.Range(0, 25_000).Select(_ => new Measure($"few-{random.Next(300):D3}", $"many-{random.Next(1_000):D4}", Math.Round(random.NextDouble() * 1_000, 2)))];
            await using ObjectDraft draft = dataset.StartObject();
            await draft.Writer.WriteAsync<Measure>(rows, Ct);
            await dataset.AppendAsync(draft, Ct);
        }

        while (await dataset.CompactAsync(cancellationToken: Ct) is not null)
        {
        }

        await dataset.ConsolidateAsync(Ct);
        foreach (bool few in new[] { true, false })
        {
            Vorticity.Aggregation question = few
                ? dataset.Scan<Measure>().GroupBy(r => r.Few).Select(g => (g.Key, g.Quantile(r => r.Value, 0.9)))
                : dataset.Scan<Measure>().GroupBy(r => r.Many).Select(g => (g.Key, g.Quantile(r => r.Value, 0.9)));
            List<KeyQuantile> answers = await question.As<KeyQuantile>().ToRecordsAsync(Ct).ToListAsync(Ct);
            Console.WriteLine($"SKETCHES {(few ? "300 values" : "1,000 values")}: {answers.Count} groups, {question.Metrics.BytesRequested} bytes in {question.Metrics.Requests} requests, {question.Metrics.BlocksDecoded} blocks decoded");
            Assert.Equal(few ? 300 : 1_000, answers.Count);
            Assert.Equal(few, question.Metrics.BytesRequested < 32 << 10);
        }
    }

    /// <summary>
    /// A dataset of two appends, consolidated, whose store holds what an earlier version learned: partials of two families
    /// over its objects and over one gone, and the families' texts, written just now. A vacuum deletes each of them, whatever
    /// its age, and nothing else the version holds; the answers are those before it.
    /// </summary>
    [Fact]
    public async Task AVacuumDeletesWhatAnEarlierVersionLearned()
    {
        Random random = new Random(61);
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Call.Schema, new DatasetOptions
        {
            Layout = new DatasetLayout { SortKey = [LayoutValue.Column(nameof(Call.Service))], ObjectBytes = 1 << 20, MergeFanout = 4, CalmAfter = null },
            ContentsObjectBytes = 0,
        }, Ct);
        for (int a = 0; a < 2; a++)
        {
            Call[] rows = [.. Enumerable.Range(0, 20_000).Select(i => new Call(
                random.Next(7), $"svc-{random.Next(0, 30):D2}", Start.AddSeconds(random.Next(0, 86_400)), (a * 20_000L) + i, Math.Round(random.NextDouble() * 100, 2)))];
            await using ObjectDraft draft = dataset.StartObject();
            await draft.Writer.WriteAsync<Call>(rows, Ct);
            await dataset.AppendAsync(draft, Ct);
        }

        await dataset.ConsolidateAsync(Ct);
        List<string> before = await HoursAsync(Question(dataset));
        List<UInt128> live = await dataset.ScanBuilder().ObjectsAsync(Ct).Select(static held => held.Entry.Uid).ToListAsync(Ct);
        List<string> learned = [];
        foreach (UInt128 family in (UInt128[])[0x1234, 0xBEEF])
        {
            string prefix = $"{CommitKey.LearnedPrefix}{family:x32}/";
            foreach (UInt128 uid in live.Append(UInt128.MaxValue))
            {
                learned.Add($"{prefix}{uid:x32}.vxp");
            }

            learned.Add($"{CommitKey.LearnedPrefix}families/{family:x32}");
        }

        foreach (string key in learned)
        {
            await store.PutAsync(key, System.IO.Pipelines.PipeReader.Create(new System.Buffers.ReadOnlySequence<byte>(new byte[] { 1, 2, 3 })), 3, PutCondition.None, Ct);
        }

        List<string> data = [];
        await foreach (ObjectListing listed in store.ListAsync("data/", null, Ct))
        {
            data.Add(listed.Key);
        }

        VacuumResult swept = await dataset.VacuumAsync(cancellationToken: Ct);
        Assert.Equal(learned.Order(StringComparer.Ordinal), swept.Deleted.Where(static key => key.StartsWith(CommitKey.LearnedPrefix, StringComparison.Ordinal)).Order(StringComparer.Ordinal));
        await foreach (ObjectListing listed in store.ListAsync(CommitKey.LearnedPrefix, null, Ct))
        {
            Assert.Fail($"{listed.Key} is left");
        }

        foreach (string key in data)
        {
            Assert.DoesNotContain(key, swept.Deleted);
        }

        Assert.Equal(before, await HoursAsync(Question(dataset)));
    }

    private static Vorticity.Aggregation Question(VortexDataset dataset) =>
        dataset.Scan<Call>().Where(r => r.Day == 3).GroupBy(r => r.At.Truncate(CalendarUnit.Hour)).Select(g => (g.Key, g.Count(), g.Sum(r => r.Value)));

    private static async Task<List<string>> HoursAsync(Vorticity.Aggregation question) =>
        [.. (await question.As<HourSum>().ToRecordsAsync(Ct).ToListAsync(Ct)).OrderBy(static a => a.Hour).Select(static a => a.ToString())];

    [VortexRecord]
    public partial record struct Measure(string Few, string Many, double Value);

    [VortexRecord]
    public partial record struct KeyQuantile(string Key, double? Value90);

    [VortexRecord]
    public partial record struct Call(int Day, string Service, DateTime At, long Sequence, double Value);

    [VortexRecord]
    public partial record struct HourSum(DateTime Hour, long Count, double Sum);

}
