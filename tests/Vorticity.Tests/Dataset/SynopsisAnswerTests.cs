// An aggregation over a dataset of periods answered from its objects' synopses: the tables' states folded in
// place of the rows they stand for, and the rows of the slices a window cuts read beside them.
//
// WHAT IS HELD: every question gives the bits its rows give, with the synopses on and off: counts, exact
// integer and float sums, extremes and means, grouped by synopsis columns, under a filter on them, over a
// window aligned on the hours and one that cuts slices, and by the instant's hour; the sealed hours answer
// from their tables, and the hours still open from their rows.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.Tests.Writing;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed partial class SynopsisAnswerTests
{
    private static readonly DateTimeOffset Noon = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);

    private static readonly string[] Services = ["api", "auth", "billing", "cart", "search"];

    private static readonly string[] Methods = ["GET", "POST", "PUT"];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string> Questions() =>
    [
        "count by service",
        "every measure by service and method",
        "count by service under a status",
        "an aligned window",
        "an unaligned window",
        "a window across hours",
        "a cut hour without runs",
        "by the hour",
    ];

    [Theory]
    [MemberData(nameof(Questions))]
    public async Task AQuestionGivesTheBitsOfItsRowsFromTheSynopses(string question)
    {
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await FilledAsync(store);
        (List<string> on, ScanMetrics metrics) = await AskAsync(dataset, question, synopses: true);
        (List<string> off, ScanMetrics without) = await AskAsync(dataset, question, synopses: false);
        Console.Out.Write($"SYNOPSIS {question}: {on.Count} groups, {metrics.SynopsisRows} table rows and {metrics.Rows} rows ({metrics.EdgeRows} at edges), against {without.Rows} rows.\n");
        Assert.Equal(off, on);
        Assert.NotEmpty(on);
        Assert.Equal(0, without.SynopsisRows);
        Assert.True(metrics.SynopsisRows > 0, "no state folded");
        Assert.True(metrics.Rows < without.Rows, $"{metrics.Rows} rows read, as many as without");
        // A window across hours reads its whole hours from their coarse tables, the slices of the hours it
        // cuts from their runs and the slices it cuts as rows; where no table of slices keys the service, the
        // hour it cuts is read as rows, its object's every row.
        Assert.Equal(question is "an unaligned window" or "a window across hours", metrics.EdgeRows > 0);
    }

    /// <summary>
    /// The plan of a question over a window across hours says, per object, what it reads: the coarse table of
    /// the whole hour, the runs and the edges of the hour cut, and the rows of an object of level 0 or of
    /// the window's last hour, cut where no table of slices keys the service.
    /// </summary>
    [Fact]
    public async Task ThePlanSaysWhatEachObjectReads()
    {
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await FilledAsync(store);
        DateTimeOffset from = Noon.AddMinutes(37.5);
        DateTimeOffset to = Noon.AddMinutes(125.5);
        ScanPlan plan = await dataset.Scan<Request>().Where(r => r.At >= from && r.At < to).GroupBy(r => r.Method)
            .Select(g => (g.Key, g.Count())).ExplainAsync(Ct);
        foreach (StatesStep step in plan.States)
        {
            Console.Out.Write($"STEP {step.Object}: {step.Read} {step.Detail} x{step.Count}\n");
        }

        Assert.Contains(plan.States, step => step is { Read: "table", Detail: "Coarse by Hour on (Method)" });
        Assert.Contains(plan.States, step => step is { Read: "table", Detail: "Runs by Slice on (Method)" });
        Assert.Contains(plan.States, step => step is { Read: "edges", Count: > 0 });

        ScanPlan rows = await dataset.Scan<Request>().With(new ScanOptions { UseSynopses = false }).GroupBy(r => r.Method).Select(g => (g.Key, g.Count())).ExplainAsync(Ct);
        Assert.Empty(rows.States);
    }

    /// <summary>A whole scan's sums, extremes and means, over every row and over a window that cuts slices, are the bits of its rows.</summary>
    [Fact]
    public async Task AWholeScanGivesTheBitsOfItsRowsFromTheSynopses()
    {
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await FilledAsync(store);
        DateTimeOffset from = Noon.AddMinutes(37.5);
        DateTimeOffset to = Noon.AddMinutes(101);
        foreach (bool windowed in new[] { false, true })
        {
            foreach (bool synopses in new[] { true, false })
            {
                Scan<Request> scan = dataset.Scan<Request>().With(new ScanOptions { UseSynopses = synopses });
                scan = windowed ? scan.Where(r => r.At >= from && r.At < to) : scan;
                Totals totals = await scan.AggregateAsync<Totals>(a => (a.Sum(r => r.Bytes), a.Sum(r => r.Duration), a.Average(r => r.Duration), a.Max(r => r.Bytes)), Ct);
                Console.Out.Write(FormattableString.Invariant($"WHOLE {(windowed ? "window" : "rows")} {(synopses ? "on" : "off")}: {totals}, {scan.Metrics.SynopsisRows} table rows, {scan.Metrics.Rows} rows\n"));
                Assert.Equal(synopses, scan.Metrics.SynopsisRows > 0);
                (windowed ? WindowTotals : RowTotals).Add(totals);
            }
        }

        Assert.Equal(RowTotals[1], RowTotals[0]);
        Assert.Equal(WindowTotals[1], WindowTotals[0]);
        Assert.Equal(BitConverter.DoubleToInt64Bits(RowTotals[1].Duration), BitConverter.DoubleToInt64Bits(RowTotals[0].Duration));
    }

    /// <summary>
    /// Rows deleted from a sealed hour: its synopsis still counts them, so its rows are read, and the other
    /// hour's states answer beside them, to the bits of the rows left.
    /// </summary>
    [Fact]
    public async Task AnObjectWithDeletedRowsIsReadAsRows()
    {
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await FilledAsync(store);
        RowChangeResult deleted = await dataset.DeleteAsync(VortexExpr.Parse("Service = 'cart' and Status = 404"), Ct);
        Assert.True(deleted.Rows > 0);
        (List<string> on, ScanMetrics metrics) = await AskAsync(dataset, "every measure by service and method", synopses: true);
        (List<string> off, ScanMetrics without) = await AskAsync(dataset, "every measure by service and method", synopses: false);
        Console.Out.Write($"DELETED: {deleted.Rows} rows marked, {metrics.SynopsisRows} table rows and {metrics.Rows} rows, against {without.Rows} rows.\n");
        Assert.Equal(off, on);
        Assert.True(metrics.Rows > 0, "an object with marked rows answered from its synopsis");
    }

    private List<Totals> RowTotals { get; } = [];

    private List<Totals> WindowTotals { get; } = [];

    /// <summary>The question's groups, each a line of its key and results, and what reading them did.</summary>
    private static async Task<(List<string> Groups, ScanMetrics Metrics)> AskAsync(VortexDataset dataset, string question, bool synopses)
    {
        Scan<Request> scan = dataset.Scan<Request>().With(new ScanOptions { UseSynopses = synopses });
        DateTimeOffset from = Noon.AddHours(1);
        DateTimeOffset to = Noon.AddHours(2);
        DateTimeOffset cutFrom = Noon.AddMinutes(37.5);
        DateTimeOffset cutTo = Noon.AddMinutes(101);
        Vorticity.Aggregation aggregation = question switch
        {
            "count by service" => scan.GroupBy(r => r.Service).Select(g => (g.Key, g.Count())),
            "every measure by service and method" => scan.GroupBy(r => (r.Service, r.Method)).Select(g => (
                g.Key.Service, g.Key.Method, g.Count(), g.Sum(r => r.Bytes), g.Min(r => r.Bytes), g.Max(r => r.Bytes),
                g.Sum(r => r.Duration), g.Min(r => r.Duration), g.Max(r => r.Duration), g.Average(r => r.Duration))),
            "count by service under a status" => scan.Where(r => r.Status == 200).GroupBy(r => r.Service).Select(g => (g.Key, g.Count(), g.Sum(r => r.Bytes))),
            "an aligned window" => scan.Where(r => r.At >= from && r.At < to).GroupBy(r => r.Service).Select(g => (g.Key, g.Count(), g.Sum(r => r.Duration))),
            "an unaligned window" => scan.Where(r => r.At >= cutFrom && r.At < cutTo).GroupBy(r => r.Method).Select(g => (g.Key, g.Count(), g.Max(r => r.Bytes))),
            "a window across hours" => scan.Where(r => r.At >= cutFrom && r.At < Noon.AddMinutes(125.5)).GroupBy(r => r.Method).Select(g => (g.Key, g.Count(), g.Sum(r => r.Duration))),
            "a cut hour without runs" => scan.Where(r => r.At >= cutFrom && r.At < Noon.AddMinutes(125.5)).GroupBy(r => r.Service).Select(g => (g.Key, g.Count(), g.Sum(r => r.Duration))),
            _ => scan.GroupBy(r => (r.At.Truncate(CalendarUnit.Hour), r.Service)).Select(g => (g.Key.Item1, g.Key.Service, g.Count(), g.Sum(r => r.Bytes))),
        };

        List<string> groups = [];
        IAsyncEnumerator<RecordBatch> batches = aggregation.Query.Batches(Ct);
        await using (batches)
        {
            while (await batches.MoveNextAsync())
            {
                Values.DescribeRows(batches.Current, groups);
            }
        }

        groups.Sort(StringComparer.Ordinal);
        return (groups, aggregation.Metrics);
    }

    /// <summary>
    /// Two hours and a quarter of requests from noon, a row every 1.25 s, appended in sixteen parts and
    /// compacted: the first two hours sealed with their synopses, the rest in the fresh levels, whose jobs
    /// write synopses too. Slices of a minute, and every table kept.
    /// </summary>
    private static async Task<VortexDataset> FilledAsync(MemoryObjectStore store)
    {
        // A budget of the data's bytes ten times over keeps every table, even on hours this small.
        VortexDataset dataset = await VortexDataset.CreateAsync(store, Request.Schema, new DatasetOptions
        {
            ClusteringKey = [nameof(Request.At)],
            Periods = new PeriodOptions { Lateness = TimeSpan.FromMinutes(5), Length = SliceWidth.Hour, SliceTargetRows = 100 },
            Write = new VortexWriteOptions { Indexes = IndexPolicy.None.WithBudgetPerMille(10_000) },
        }, Ct);
        Random random = new Random(41);
        for (int part = 0; part < 16; part++)
        {
            Request[] rows = [.. Enumerable.Range(0, 400).Select(i => new Request(
                Noon.AddSeconds((part * 400 + i) * 1.25),
                Services[random.Next(Services.Length)],
                Methods[random.Next(Methods.Length)],
                random.Next(4) switch { 0 => 200L, 1 => 201L, 2 => 404L, _ => 500L },
                random.NextDouble() * 1_000,
                random.NextInt64(1, 1L << 40)))];
            await using ObjectDraft draft = dataset.StartObject();
            await draft.Writer.WriteAsync<Request>(rows, Ct);
            await dataset.AppendAsync(draft, Ct);
        }

        while (await dataset.CompactAsync(new CompactionOptions { LevelZeroCeiling = 2 }, Ct) is not null)
        {
        }

        return dataset;
    }

    [VortexRecord]
    public partial record struct Request(DateTimeOffset At, string Service, string Method, long Status, double Duration, long Bytes);

    [VortexRecord]
    public partial record struct Totals(long Bytes, double Duration, double? Mean, long? Largest);
}
