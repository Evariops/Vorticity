// Tables over declared groupings of the synopsis columns (DatasetOptions.SynopsisGroupings): columns questions filter and
// group by together, whatever columns the order places between them, kept by hour and by object, their chunks bounded by
// the first column's values.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.Indexes;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed partial class SynopsisAnswerTests
{
    private static readonly string[][] Groupings = [[nameof(Spanned.Route), nameof(Spanned.Pod)], [nameof(Spanned.Service), nameof(Spanned.HttpStatus), nameof(Spanned.Route)]];

    /// <summary>
    /// Spans of three hours from 22:00, whose order places the route last and the pod before it, with the hosts and the
    /// services between the status and them: a sealed day of two hours keeps a table of each grouping by hour and by
    /// object, and the fresh hour by hour. The pods of one route, and one service's routes under 5xx by hour, are
    /// answered from those tables, the bits of the rows; the pods of one route read the chunks that may hold it, fewer
    /// bytes than the pods of every route.
    /// </summary>
    [Fact]
    public async Task QuestionsOverAGroupingAreAnsweredFromItsTables()
    {
        await using MemoryObjectStore inner = new MemoryObjectStore();
        await using CountingObjectStore store = new CountingObjectStore(inner);
        await using VortexDataset dataset = await SpannedAsync(store, Groupings);

        // The sealed day's tables: each grouping by hour and by object, in its own order.
        DateTimeOffset start = new DateTimeOffset(2026, 10, 19, 22, 0, 0, TimeSpan.Zero);
        int sealedObjects = 0;
        await foreach (DataObject held in dataset.ListObjectsAsync(Ct))
        {
            if (held.Entry!.Contents is not { } contents)
            {
                continue;
            }

            Vorticity.Types.DType schema = contents.SchemaType;
            string[][] keyed = [.. contents.Tables.Where(t => t.Table.Kind == SynopsisTableKind.Coarse)
                .Select(t => t.Table.Keys.Select(schema.GetFieldName).Prepend(t.Table.Bucket.ToString()).ToArray())];
            foreach (string[] grouping in Groupings)
            {
                Assert.Contains(keyed, k => k[0] == nameof(SynopsisBucket.Hour) && k[1..].SequenceEqual(grouping));
                if (held.Rows > SpannedRowsAnHour * 3 / 2)
                {
                    Assert.Contains(keyed, k => k[0] == nameof(SynopsisBucket.Object) && k[1..].SequenceEqual(grouping));
                }
            }

            sealedObjects += held.Rows > SpannedRowsAnHour * 3 / 2 ? 1 : 0;
        }

        Assert.Equal(1, sealedObjects);

        Func<Scan<Spanned>, Vorticity.Aggregation>[] questions =
        [
            scan => scan.Where(s => s.Route == "/s3/r7").GroupBy(s => s.Pod).Select(g => (g.Key, g.Count(), g.Sum(s => s.DurationNs))),
            scan => scan.Where(s => s.Service == "svc-3" && s.HttpStatus >= 500).GroupBy(s => (s.Route, s.At.Truncate(CalendarUnit.Hour))).Select(g => (g.Key.Route, g.Key.Item2, g.Count())),
            scan => scan.GroupBy(s => (s.Route, s.Pod)).Select(g => (g.Key.Route, g.Key.Pod, g.Count(), g.Sum(s => s.DurationNs))),
        ];
        string[] tables = ["on (Route, Pod)", "on (Service, HttpStatus, Route)", "on (Route, Pod)"];
        long[] bytes = new long[questions.Length];
        for (int q = 0; q < questions.Length; q++)
        {
            (List<string> on, ScanMetrics metrics) = await GroupsAsync(questions[q](dataset.Scan<Spanned>().With(new ScanOptions { UseSynopses = true })));
            store.Reset();
            (List<string> again, _) = await GroupsAsync(questions[q](dataset.Scan<Spanned>().With(new ScanOptions { UseSynopses = true })));
            bytes[q] = store.BytesRead;
            (List<string> off, _) = await GroupsAsync(questions[q](dataset.Scan<Spanned>().With(new ScanOptions { UseSynopses = false })));
            ScanPlan plan = await questions[q](dataset.Scan<Spanned>().With(new ScanOptions { UseSynopses = true })).ExplainAsync(Ct);
            Console.Out.Write(
                $"GROUPING {q}: {on.Count} groups, {metrics.SynopsisRows} table rows and {metrics.Rows} rows, {bytes[q]} bytes read: " +
                $"{string.Join(" | ", plan.States.Select(step => $"{step.Read} {step.Detail} x{step.Count}"))}\n");
            Assert.NotEmpty(on);
            Assert.Equal(off, on);
            Assert.Equal(on, again);
            Assert.True(metrics.SynopsisRows > 0, "no state folded");
            Assert.Equal(0, metrics.Rows);
            Assert.All(plan.States, step => Assert.Contains(tables[q], step.Detail, StringComparison.Ordinal));
        }

        Assert.True(bytes[0] * 2 < bytes[2], $"one route read {bytes[0]} bytes, every route {bytes[2]}");
    }

    /// <summary>
    /// A grouping of one column, the service, over spans of 800 rows a minute, whose slices' sketches take fewer bytes
    /// than their columns: its tables of sketches by slice, by hour and by object answer a quantile by service over a
    /// window that cuts its hours from the slices it keeps whole, and read as rows only the two slices it cuts, where
    /// without the grouping no table of slices holds a sketch and every slice of the hours cut is read as rows. Both give
    /// the bits of the rows.
    /// </summary>
    [Fact]
    public async Task AQuantileOverAWindowThatCutsHoursReadsTheSlicesOfItsGrouping()
    {
        DateTimeOffset start = new DateTimeOffset(2026, 10, 19, 22, 0, 0, TimeSpan.Zero);
        DateTimeOffset from = start.AddMinutes(10.5);
        DateTimeOffset to = start.AddMinutes(101.5);
        Func<Scan<Spanned>, Vorticity.Aggregation> question = scan => scan.Where(s => s.At >= from && s.At < to)
            .GroupBy(s => s.Service).Select(g => (g.Key, g.Count(), g.Quantile(s => s.DurationNs, 0.99), g.Quantile(s => s.DurationNs, 0.5)));
        long[] rows = new long[2];
        List<string>? first = null;
        foreach (bool declared in new[] { true, false })
        {
            await using MemoryObjectStore store = new MemoryObjectStore();
            await using VortexDataset dataset = await SpannedAsync(store, declared ? [[nameof(Spanned.Service)]] : [], rowsAnHour: 48_000);
            (List<string> on, ScanMetrics metrics) = await GroupsAsync(question(dataset.Scan<Spanned>().With(new ScanOptions { UseSynopses = true })));
            (List<string> off, _) = await GroupsAsync(question(dataset.Scan<Spanned>().With(new ScanOptions { UseSynopses = false })));
            ScanPlan plan = await question(dataset.Scan<Spanned>().With(new ScanOptions { UseSynopses = true })).ExplainAsync(Ct);
            Console.Out.Write(
                $"GROUPED QUANTILE declared {declared}: {on.Count} groups, {metrics.SynopsisRows} table rows and {metrics.Rows} rows ({metrics.EdgeRows} at edges): " +
                $"{string.Join(" | ", plan.States.Select(step => $"{step.Read} {step.Detail} x{step.Count}"))}\n");
            Assert.NotEmpty(on);
            Assert.Equal(off, on);
            Assert.Equal(first ??= on, on);
            rows[declared ? 0 : 1] = metrics.Rows + metrics.EdgeRows;
            if (declared)
            {
                Assert.True(metrics.SynopsisRows > 0, "no sketch folded");
                Assert.Contains(plan.States, step => step.Detail.StartsWith("Sketches by Slice on (Service)", StringComparison.Ordinal));
            }
        }

        Assert.True(rows[0] * 10 < rows[1], $"{rows[0]} rows read with the grouping, {rows[1]} without");
    }

    /// <summary>
    /// A grouping that names a column no object's order holds, the instant, or a column twice, writes no table, and the
    /// questions over its columns read what they read without it; a grouping of one column writes no table of states,
    /// whose values the order's counts hold.
    /// </summary>
    [Fact]
    public async Task AGroupingTheOrderDoesNotHoldWritesNoTable()
    {
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await SpannedAsync(
            store, [[nameof(Spanned.Route), nameof(Spanned.At)], [nameof(Spanned.Pod), nameof(Spanned.Pod)], [nameof(Spanned.Route), "Nope"], [nameof(Spanned.Route)]]);
        await foreach (DataObject held in dataset.ListObjectsAsync(Ct))
        {
            if (held.Entry!.Contents is { } contents)
            {
                Vorticity.Types.DType schema = contents.SchemaType;
                int route = schema.IndexOfField(nameof(Spanned.Route));
                Assert.DoesNotContain(contents.Tables, t => t.Table.Kind == SynopsisTableKind.Coarse && t.Table.Keys is [int first, ..] && first == route);
            }
        }
    }

    private const int SpannedRowsAnHour = 8_000;

    /// <summary>
    /// Three hours of spans from 22:00, <paramref name="rowsAnHour"/> an hour, appended in parts and compacted under periods
    /// of a day, every table kept and cut in chunks of 64 rows: the day's two hours sealed, the next hour in the fresh levels;
    /// slices of a minute. Six services of ten routes and three pods each, six hosts, two states and four HTTP statuses.
    /// </summary>
    private static async Task<VortexDataset> SpannedAsync(Vorticity.IO.IObjectStore store, IReadOnlyList<IReadOnlyList<string>> groupings, int rowsAnHour = SpannedRowsAnHour)
    {
        VortexDataset dataset = await VortexDataset.CreateAsync(store, Spanned.Schema, new DatasetOptions
        {
            ClusteringKey = [nameof(Spanned.At)],
            Periods = new PeriodOptions { Lateness = TimeSpan.FromMinutes(5), Length = SliceWidth.Day, SliceTargetRows = 500 },
            Write = new VortexWriteOptions { Indexes = IndexPolicy.None.WithBudgetPerMille(10_000) },
            SynopsisChunking = (64, 1 << 12),
            SynopsisGroupings = groupings,
        }, Ct);
        DateTimeOffset start = new DateTimeOffset(2026, 10, 19, 22, 0, 0, TimeSpan.Zero);
        Random random = new Random(71);
        int rows = rowsAnHour * 3;
        int parts = 24;
        for (int part = 0; part < parts; part++)
        {
            Spanned[] drawn = [.. Enumerable.Range(0, rows / parts).Select(i =>
            {
                int service = random.Next(6);
                long status = random.Next(10) switch { < 6 => 200L, < 8 => 404L, 8 => 500L, _ => 503L };
                return new Spanned(
                    start.AddSeconds((((long)part * (rows / parts)) + i) * 3_600.0 / rowsAnHour),
                    status >= 500 && random.Next(2) == 0 ? "error" : "ok",
                    $"svc-{service}",
                    status,
                    $"host-{random.Next(6)}",
                    $"svc-{service}-pod-{random.Next(3)}",
                    $"/s{service}/r{random.Next(10)}",
                    random.NextInt64(1_000, 1_000_000));
            })];
            await using ObjectDraft draft = dataset.StartObject();
            await draft.Writer.WriteAsync<Spanned>(drawn, Ct);
            await dataset.AppendAsync(draft, Ct);
        }

        while (await dataset.CompactAsync(new CompactionOptions { LevelZeroCeiling = 2 }, Ct) is not null)
        {
        }

        return dataset;
    }

    [VortexRecord]
    public partial record struct Spanned(
        DateTimeOffset At, string Status, string Service, long HttpStatus, string Host, string Pod, string Route, long DurationNs);
}
