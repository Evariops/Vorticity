// A range on the first column of a dataset's key, grown under ScanOptions.AlignWindow to the objects it cuts so that their
// counts answer them whole; and a text leading the key, which an object's keys bound where its file states no bound.
//
// WHAT IS HELD: on a dataset keyed by a text alone and cut by size, a range that cuts an object grows to the object's
// bound, the counts then answer every object the range reaches, and the answer is the rows' of the range applied, which
// the metrics and the plan give. Under a key of a text and an instant cut by size, whose objects share a text at their
// bounds, the range stays as asked: growing it would cut the next object in turn. On a dataset keyed by a text and an
// instant and cut by the text, a question filtered on one text and grouped by another column is answered by the counts of
// that text's objects, which their keys prove hold it alone.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.Expressions;
using Vorticity.Scanning;
using Vorticity.Tests.Writing;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed partial class DatasetAlignedWindowTests
{
    private static readonly DateTimeOffset Noon = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);

    private static readonly string[] Statuses = ["ok", "slow", "error"];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ARangeThatCutsAnObjectGrowsToItsBoundAndItsCountsAnswer()
    {
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await WriteAsync(store, [nameof(Ticket.Tenant)], tenants: 12, rowsPerTenant: 1_500);

        // An object of two tenants or more, past the first tenant of all, and a range from its last tenant on: it cuts the
        // object, which it then holds whole.
        (string first, string last) = await ObjectOfTwoTenantsAsync(dataset, after: "t00");
        VortexExpr asked = Expr.Ge(Expr.Field(nameof(Ticket.Tenant)), Expr.Literal(FilterLiteral.From(last)));
        Vorticity.Scan aligned = Grouped(dataset, asked, new ScanOptions { AlignWindow = true });
        ScanPlan plan = await aligned.ExplainAsync(Ct);
        VortexExpr window = Assert.IsAssignableFrom<VortexExpr>(plan.Window);
        Console.WriteLine($"ALIGNED {asked} grows to {window}, the counts of {plan.States.Count(step => step.Read == "states")} objects answering");

        // The range applied starts at the object's first tenant, and its answer is that range's rows, the counts answering.
        List<string> answered = await RowsAsync(aligned);
        Assert.Equal(window.ToString(), aligned.Metrics.Window?.ToString());
        VortexExpr applied = Expr.Ge(Expr.Field(nameof(Ticket.Tenant)), Expr.Literal(FilterLiteral.From(first)));
        Assert.Equal(await RowsAsync(Grouped(dataset, applied, new ScanOptions { UseSynopses = false })), answered);
        Assert.NotEqual(await RowsAsync(Grouped(dataset, asked, new ScanOptions())), answered);
        Assert.Contains(plan.States, step => step.Read == "states");
        ScanPlan exact = await Grouped(dataset, asked, new ScanOptions()).ExplainAsync(Ct);
        Assert.Null(exact.Window);
        int answeredAligned = plan.States.Count(step => step.Read == "states");
        int answeredAsked = exact.States.Count(step => step.Read == "states");
        Assert.True(answeredAligned > answeredAsked, $"the counts of {answeredAligned} objects answered aligned, {answeredAsked} as asked");
    }

    [Fact]
    public async Task ARangeStaysWhereGrowingWouldCutAnotherObject()
    {
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await WriteAsync(store, [nameof(Ticket.Tenant), nameof(Ticket.At)], tenants: 6, rowsPerTenant: 4_000);

        // Each tenant's rows run over objects cut by size, which share a tenant at their bounds: the first tenant of an object
        // of two, past the first tenant of all, is the last of an object before it, which a range grown to it would cut.
        (_, string last) = await ObjectOfTwoTenantsAsync(dataset, after: "t00");
        VortexExpr asked = Expr.Ge(Expr.Field(nameof(Ticket.Tenant)), Expr.Literal(FilterLiteral.From(last)));
        Vorticity.Scan aligned = Grouped(dataset, asked, new ScanOptions { AlignWindow = true });
        Assert.Null((await aligned.ExplainAsync(Ct)).Window);
        List<string> answered = await RowsAsync(aligned);
        Assert.Null(aligned.Metrics.Window);
        Assert.Equal(await RowsAsync(Grouped(dataset, asked, new ScanOptions { UseSynopses = false })), answered);
    }

    [Fact]
    public async Task AFilterOnTheLeadingTextIsProvenByTheObjectsKeys()
    {
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await WriteAsync(
            store, [nameof(Ticket.Tenant), nameof(Ticket.At)], tenants: 6, rowsPerTenant: 2_000, new ClusteringOptions { Value = ClusteringValue.Column });

        // Grouped by the status, filtered on a tenant: the status's counts of the tenant's objects, which hold it alone.
        foreach (string filter in (string[])["Tenant = 't03'", "Tenant in ('t01', 't04')", "Tenant >= 't02' and Tenant < 't05'"])
        {
            VortexExpr asked = VortexExpr.Parse(filter);
            ScanPlan plan = await Grouped(dataset, asked, new ScanOptions()).ExplainAsync(Ct);
            Console.WriteLine($"LEADING TEXT {filter}: the counts of {plan.States.Count(step => step.Read == "states")} objects answer");
            Assert.Contains(plan.States, step => step.Read == "states");
            Assert.Equal(
                await RowsAsync(Grouped(dataset, asked, new ScanOptions { UseSynopses = false })),
                await RowsAsync(Grouped(dataset, asked, new ScanOptions())));
        }
    }

    /// <summary>The count and the bytes by status of the rows <paramref name="filter"/> keeps, under <paramref name="options"/>.</summary>
    internal static Vorticity.Scan Grouped(VortexDataset dataset, VortexExpr filter, ScanOptions options) =>
        dataset.Scan().With(options).Where(filter).GroupBy([nameof(Ticket.Status)]).Select([nameof(Ticket.Status), "count()", "sum(Bytes)"]);

    /// <summary>The groups a scan answers, each a line of its values, sorted.</summary>
    internal static async Task<List<string>> RowsAsync(Vorticity.Scan scan)
    {
        List<string> rows = [];
        await foreach (RecordBatch batch in scan.ToBatchesAsync(Ct))
        {
            Values.DescribeRows(batch, rows);
        }

        rows.Sort(StringComparer.Ordinal);
        return rows;
    }

    /// <summary>
    /// The first and the last tenant of an object a job wrote that holds two or more, as its summaries bound the key's
    /// text by its keys; its first past <paramref name="after"/> when given.
    /// </summary>
    private static async Task<(string First, string Last)> ObjectOfTwoTenantsAsync(VortexDataset dataset, string? after = null)
    {
        ClusteringKey key = dataset.Snapshot.Schema.Key!;
        await foreach (PositionedObject held in dataset.ScanBuilder().ObjectsAsync(Ct))
        {
            if (held.Entry.Contents is { Counts.Count: > 0 } && held.Summaries(key).TryGet(nameof(Ticket.Tenant), out ColumnSummary tenant)
                && tenant.HasMin && tenant.HasMax && Encoding.UTF8.GetString(tenant.Min.BytesValue) is var first
                && Encoding.UTF8.GetString(tenant.Max.BytesValue) is var last && !string.Equals(first, last, StringComparison.Ordinal)
                && (after is null || string.CompareOrdinal(first, after) > 0))
            {
                return (first, last);
            }
        }

        throw new InvalidOperationException("No object holds two tenants.");
    }

    /// <summary>
    /// <paramref name="tenants"/> tenants' tickets, <paramref name="rowsPerTenant"/> each, appended in twelve parts that
    /// mix them and compacted to the end, cut by <paramref name="clustering"/>, or by 16 KiB over blocks of a few
    /// kilobytes; every object a job writes keeps its counts.
    /// </summary>
    private static async Task<VortexDataset> WriteAsync(
        MemoryObjectStore store, string[] key, int tenants, int rowsPerTenant, ClusteringOptions? clustering = null)
    {
        VortexDataset dataset = await VortexDataset.CreateAsync(store, Ticket.Schema, new DatasetOptions
        {
            ClusteringKey = key,
            Clustering = clustering ?? new ClusteringOptions { ObjectBytes = 16L << 10 },
            Write = new VortexWriteOptions { RowBlockSize = 512, DataBlockTargetBytes = 4 << 10 },
            SheetObjectBytes = 0,
            ContentsObjectBytes = 0,
        }, Ct);
        Random random = new Random(61);
        int rows = tenants * rowsPerTenant;
        int part = rows / 12;
        for (int from = 0; from < rows; from += part)
        {
            Ticket[] tickets = new Ticket[Math.Min(part, rows - from)];
            for (int i = 0; i < tickets.Length; i++)
            {
                int row = from + i;
                tickets[i] = new Ticket($"t{row % tenants:D2}", Noon.AddSeconds(row), Statuses[random.Next(Statuses.Length)], random.NextInt64(100, 100_000));
            }

            await using ObjectDraft draft = dataset.StartObject();
            await draft.Writer.WriteAsync<Ticket>(tickets, Ct);
            await dataset.AppendAsync(draft, Ct);
        }

        while (await dataset.CompactAsync(new CompactionOptions { LevelZeroCeiling = 1 }, Ct) is not null)
        {
        }

        return dataset;
    }

    [VortexRecord]
    public partial record struct Ticket(string Tenant, DateTimeOffset At, string Status, long Bytes);
}
