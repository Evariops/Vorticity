// The automatic views (spec H; H1, H2): an object's counts keep a quantile sketch by value while a value holds enough rows
// on average; the partials a question learns are kept in the store, where another node finds them and a coarser question
// is served by a finer family's; the vacuum sweeps those of objects gone, and a budget keeps the newest families.
//
// THE ORACLE IS THE ROWS: every answer a sketch or a partial gives equals the one the rows give; a node that answers from
// the store's partials reads no data object.
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
    /// Three days of six appends of 20 000 rows, partitioned by day and consolidated (H2): a node learns a question's family
    /// from the rows; another, opened after, answers it from the store's partials with no read of a data object; a node that
    /// learned by hour and service serves a question by hour to another, which keeps the partials it read; the vacuum, a month
    /// on, sweeps the partials of an object a compaction replaced, and under a budget of the newest family's bytes keeps that
    /// family alone, its text with it.
    /// </summary>
    [Fact]
    public async Task AFamilyLearnedOnOneNodeIsServedByAnother()
    {
        Random random = new Random(61);
        await using MemoryObjectStore memory = new MemoryObjectStore();
        LoggingStore store = new LoggingStore(memory);
        DatasetOptions options = new DatasetOptions
        {
            Layout = new DatasetLayout { Partition = LayoutValue.Column(nameof(Call.At)).Truncate(CalendarUnit.Day), SortKey = [LayoutValue.Column(nameof(Call.Service))], ObjectBytes = 1 << 20, MergeFanout = 4, CalmAfter = null },
            ContentsObjectBytes = 1,
            SheetObjectBytes = 0,
        };
        await using (VortexDataset writer = await VortexDataset.CreateAsync(store, Call.Schema, options, Ct))
        {
            for (int a = 0; a < 6; a++)
            {
                Call[] rows = [.. Enumerable.Range(0, 20_000).Select(i => new Call(
                    random.Next(7), $"svc-{random.Next(0, 30):D2}", Start.AddSeconds(random.Next(0, 3 * 86_400)), (a * 20_000L) + i, Math.Round(random.NextDouble() * 100, 2)))];
                await using ObjectDraft draft = writer.StartObject();
                await draft.Writer.WriteAsync<Call>(rows, Ct);
                await writer.AppendAsync(draft, Ct);
            }

            while (await writer.CompactAsync(cancellationToken: Ct) is not null)
            {
            }

            await writer.ConsolidateAsync(Ct);
        }

        // Node a learns from the rows; node b answers from the store's partials.
        List<string> first = await OnNodeAsync(store, options, static d => HoursAsync(Question(d)));
        store.Reads.Clear();
        Assert.Equal(first, await OnNodeAsync(store, options, static d => HoursAsync(Question(d))));
        Assert.True(Reads(store, "learned/") > 0 && Reads(store, "data/") == 0, $"{Reads(store, "learned/")} reads of partials, {Reads(store, "data/")} of data objects");

        // Node c learns by hour and service; node d's question by hour is served by those partials, then by its own.
        List<string> reference = await OnNodeAsync(store, options, static d => TotalsAsync(Coarse(d, synopses: false)));
        await OnNodeAsync(store, options, static async d => (await Fine(d).As<HourServiceTotals>().ToRecordsAsync(Ct).ToListAsync(Ct)).Count);
        await using (VortexSession session = VortexSession.Create(_ => { }))
        await using (VortexDataset d = await VortexDataset.OpenAsync(store, options with { Session = session }, Ct))
        {
            store.Reads.Clear();
            Assert.Equal(reference, await TotalsAsync(Coarse(d, synopses: true)));
            Assert.True(Reads(store, "learned/") > 0 && Reads(store, "data/") == 0, $"{Reads(store, "learned/")} reads of partials, {Reads(store, "data/")} of data objects");
            store.Reads.Clear();
            Assert.Equal(reference, await TotalsAsync(Coarse(d, synopses: true)));
            Assert.Empty(store.Reads);
        }

        // The vacuum a month on: the partials of an object a compaction replaced go; a budget keeps the newest family alone.
        string[] before = await PartialsAsync(memory);
        await using (VortexDataset writer = await VortexDataset.OpenAsync(store, options, Ct))
        {
            await using (ObjectDraft draft = writer.StartObject())
            {
                await draft.Writer.WriteAsync<Call>([.. Enumerable.Range(0, 1_000).Select(i => new Call(3, "svc-00", Start.AddSeconds(i), 1_000_000 + i, 1.5))], Ct);
                await writer.AppendAsync(draft, Ct);
            }

            while (await writer.CompactAsync(cancellationToken: Ct) is not null)
            {
            }

            await writer.ConsolidateAsync(Ct);
            HashSet<UInt128> live = [.. await writer.ScanBuilder().ObjectsAsync(Ct).Select(static held => held.Entry.Uid).ToListAsync(Ct)];
            VacuumResult swept = await writer.VacuumAsync(new VacuumOptions { TimeProvider = new Shifted(TimeSpan.FromDays(30)) }, Ct);
            string[] after = await PartialsAsync(memory);
            Assert.All(after, static key => Assert.EndsWith(".vxp", key, StringComparison.Ordinal));
            Assert.All(after, key => Assert.Contains(UInt128.Parse(key[(key.LastIndexOf('/') + 1)..^4], System.Globalization.NumberStyles.HexNumber), live));
            Assert.True(after.Length < before.Length, $"{before.Length} partials, {after.Length} after");
            Assert.Equal(before.Length - after.Length, swept.Deleted.Count(static key => key.StartsWith("learned/", StringComparison.Ordinal) && key.EndsWith(".vxp", StringComparison.Ordinal)));
        }

        Dictionary<string, long> families = [];
        await foreach (ObjectListing listed in memory.ListAsync("learned/", null, Ct))
        {
            string family = listed.Key.Split('/')[1];
            families[family] = family == "families" ? 0 : families.GetValueOrDefault(family) + listed.Head.Length;
        }

        families.Remove("families");
        Assert.True(families.Count > 1, $"{families.Count} families");
        string newest = await NewestFamilyAsync(memory);
        await using (VortexDataset tight = await VortexDataset.OpenAsync(store, options with { LearnedBudget = families[newest] }, Ct))
        {
            await tight.VacuumAsync(new VacuumOptions { TimeProvider = new Shifted(TimeSpan.FromDays(30)) }, Ct);
        }

        List<string> kept = [];
        await foreach (ObjectListing listed in memory.ListAsync("learned/", null, Ct))
        {
            kept.Add(listed.Key);
        }

        Assert.All(kept, key => Assert.True(key.StartsWith($"learned/{newest}/", StringComparison.Ordinal) || key == $"learned/families/{newest}", key));
        Assert.Contains($"learned/families/{newest}", kept);
    }

    private static Vorticity.Aggregation Question(VortexDataset dataset) =>
        dataset.Scan<Call>().With(new ScanOptions { UseSynopses = true }).Where(r => r.Day == 3).GroupBy(r => r.At.Truncate(CalendarUnit.Hour)).Select(g => (g.Key, g.Count(), g.Sum(r => r.Value)));

    private static Vorticity.Aggregation Coarse(VortexDataset dataset, bool synopses) =>
        dataset.Scan<Call>().With(new ScanOptions { UseSynopses = synopses }).Where(r => r.Day == 3).GroupBy(r => r.At.Truncate(CalendarUnit.Hour))
            .Select(g => (g.Key, g.Count(), g.Sum(r => r.Value), g.Max(r => r.Value)));

    private static Vorticity.Aggregation Fine(VortexDataset dataset) =>
        dataset.Scan<Call>().With(new ScanOptions { UseSynopses = true }).Where(r => r.Day == 3).GroupBy(r => (Hour: r.At.Truncate(CalendarUnit.Hour), r.Service))
            .Select(g => (g.Key.Hour, g.Key.Service, g.Count(), g.Sum(r => r.Value), g.Max(r => r.Value), g.Min(r => r.Value)));

    /// <summary>What <paramref name="ask"/> gives on a node of its own: a handle and a session opened for it, closed after.</summary>
    private static async Task<T> OnNodeAsync<T>(IObjectStore store, DatasetOptions options, Func<VortexDataset, Task<T>> ask)
    {
        await using VortexSession session = VortexSession.Create(_ => { });
        await using VortexDataset dataset = await VortexDataset.OpenAsync(store, options with { Session = session }, Ct);
        return await ask(dataset);
    }

    private static async Task<List<string>> HoursAsync(Vorticity.Aggregation question) =>
        [.. (await question.As<HourSum>().ToRecordsAsync(Ct).ToListAsync(Ct)).OrderBy(static a => a.Hour).Select(static a => a.ToString())];

    private static async Task<List<string>> TotalsAsync(Vorticity.Aggregation question) =>
        [.. (await question.As<HourTotals>().ToRecordsAsync(Ct).ToListAsync(Ct)).OrderBy(static a => a.Hour).Select(static a => a.ToString())];

    private static int Reads(LoggingStore store, string prefix)
    {
        lock (store.Reads)
        {
            return store.Reads.Count(read => read.Key.StartsWith(prefix, StringComparison.Ordinal));
        }
    }

    /// <summary>The partials the store keeps, by key.</summary>
    private static async Task<string[]> PartialsAsync(MemoryObjectStore store)
    {
        List<string> keys = [];
        await foreach (ObjectListing listed in store.ListAsync("learned/", null, Ct))
        {
            if (listed.Key.EndsWith(".vxp", StringComparison.Ordinal))
            {
                keys.Add(listed.Key);
            }
        }

        return [.. keys];
    }

    /// <summary>The family whose partial was written last.</summary>
    private static async Task<string> NewestFamilyAsync(MemoryObjectStore store)
    {
        (string Family, DateTimeOffset At) newest = (string.Empty, DateTimeOffset.MinValue);
        await foreach (ObjectListing listed in store.ListAsync("learned/", null, Ct))
        {
            if (listed.Key.EndsWith(".vxp", StringComparison.Ordinal) && listed.Head.LastModified >= newest.At)
            {
                newest = (listed.Key.Split('/')[1], listed.Head.LastModified);
            }
        }

        return newest.Family;
    }

    private sealed class Shifted(TimeSpan by) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => base.GetUtcNow() + by;
    }

    [VortexRecord]
    public partial record struct Measure(string Few, string Many, double Value);

    [VortexRecord]
    public partial record struct KeyQuantile(string Key, double? Value90);

    [VortexRecord]
    public partial record struct Call(int Day, string Service, DateTime At, long Sequence, double Value);

    [VortexRecord]
    public partial record struct HourSum(DateTime Hour, long Count, double Sum);

    [VortexRecord]
    public partial record struct HourTotals(DateTime Hour, long Count, double Sum, double Max);

    [VortexRecord]
    public partial record struct HourServiceTotals(DateTime Hour, string Service, long Count, double Sum, double Max, double Min);
}
