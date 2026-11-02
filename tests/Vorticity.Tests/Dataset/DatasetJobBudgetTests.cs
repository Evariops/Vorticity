// The jobs' budget a session gives its background work (VortexSessionOptions.JobMemoryBudget): an append holds its rows
// within it, and writes its largest partition sooner when the budget refuses them, reading back the rows it was given; a
// consolidation of variants counts its paths and writes its ranges on as many lanes as the budget holds, one at least.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.Types.Variants;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed partial class DatasetJobBudgetTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// Six days of rows appended in ten writes, partitioned by day: under a budget that holds them, an object a day once the
    /// append ends; under a budget that holds none, every day written at each write, more objects, the same rows.
    /// </summary>
    [Fact]
    public async Task AnAppendTheJobsBudgetRefusesWritesItsPartsSoonerAndHoldsTheSameRows()
    {
        (long roomy, List<Row> wide) = await AppendAsync(jobs: null);
        (long tight, List<Row> narrow) = await AppendAsync(jobs: new QueryMemoryBudget(1));
        Assert.Equal(6, roomy);
        Assert.True(tight > roomy, $"{tight} objects under a budget of a byte, {roomy} under the process's jobs' share");
        Assert.Equal(wide.OrderBy(static row => row.Sequence), narrow.OrderBy(static row => row.Sequence));
    }

    /// <summary>
    /// Two days of documents consolidated on four lanes, under the process's jobs' share and under a budget that holds
    /// nothing: the census of their paths on one lane, the ranges one at a time, every row the same.
    /// </summary>
    [Fact]
    public async Task AConsolidationOfVariantsTheJobsBudgetRefusesCountsAndWritesOnOneLaneAndHoldsTheSameRows()
    {
        List<Doc> wide = await ConsolidateAsync(jobs: null);
        List<Doc> narrow = await ConsolidateAsync(jobs: new QueryMemoryBudget(1));
        Assert.Equal(wide.Count, narrow.Count);
        for (int i = 0; i < wide.Count; i++)
        {
            Assert.Equal(wide[i].Sequence, narrow[i].Sequence);
            Assert.True(wide[i].Attributes.Equals(narrow[i].Attributes), $"row {wide[i].Sequence}: {wide[i].Attributes.ToJson()} against {narrow[i].Attributes.ToJson()}");
        }
    }

    private static async Task<List<Doc>> ConsolidateAsync(QueryMemoryBudget? jobs)
    {
        Random random = new Random(13);
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexSession session = VortexSession.Create(options =>
        {
            options.MaxDegreeOfParallelism = 4;
            options.JobMemoryBudget = jobs;
        });
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Doc.Schema, new DatasetOptions
        {
            Session = session,
            Layout = new DatasetLayout
            {
                Partition = LayoutValue.Column(nameof(Doc.Day)),
                SortKey = [LayoutValue.Column(nameof(Doc.Service))],
                ObjectBytes = 1 << 18,
                CalmAfter = null,
            },
        }, Ct);
        for (int a = 0; a < 6; a++)
        {
            Doc[] rows = [.. Enumerable.Range(a * 8_000, 8_000).Select(i => new Doc(random.Next(0, 2), $"svc-{random.Next(0, 60):D2}", i, Attributes(random)))];
            await using ObjectDraft draft = dataset.StartObject();
            await draft.Writer.WriteAsync<Doc>(rows, Ct);
            await dataset.AppendAsync(draft, Ct);
        }

        Assert.NotEmpty(await dataset.ConsolidateAsync(Ct));
        return [.. (await dataset.Scan<Doc>().ToRecordsAsync(Ct).ToListAsync(Ct)).OrderBy(static row => row.Sequence)];
    }

    /// <summary>Attributes of a service's records: a route, a status, a duration, a user, and now and then a key of its own.</summary>
    private static Variant Attributes(Random random)
    {
        List<(string, Variant)> fields =
        [
            ("route", Variant.From($"/api/r{random.Next(20)}")),
            ("status", Variant.From(200 + (random.Next(4) * 100))),
            ("duration", Variant.From(random.NextDouble() * 100)),
            ("user", Variant.From($"user-{random.Next(500)}")),
        ];
        if (random.Next(5) == 0)
        {
            fields.Add(($"k{random.Next(40)}", Variant.From(random.Next(1_000))));
        }

        return Variant.Object([.. fields]);
    }

    private static async Task<(long Objects, List<Row> Rows)> AppendAsync(QueryMemoryBudget? jobs)
    {
        Random random = new Random(5);
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexSession session = VortexSession.Create(options => options.JobMemoryBudget = jobs);
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Row.Schema, new DatasetOptions
        {
            Session = session,
            Layout = new DatasetLayout { Partition = LayoutValue.Column(nameof(Row.Day)), SortKey = [LayoutValue.Column(nameof(Row.Service))] },
        }, Ct);
        await using (ObjectDraft draft = dataset.StartObject())
        {
            for (int write = 0; write < 10; write++)
            {
                Row[] rows = [.. Enumerable.Range(write * 6_000, 6_000).Select(i => new Row(random.Next(0, 6), $"svc-{random.Next(0, 40):D2}", i, random.NextDouble()))];
                await draft.Writer.WriteAsync<Row>(rows, Ct);
            }

            await dataset.AppendAsync(draft, Ct);
        }

        long objects = await dataset.ScanBuilder().ObjectsAsync(Ct).CountAsync(Ct);
        return (objects, await dataset.Scan<Row>().ToRecordsAsync(Ct).ToListAsync(Ct));
    }

    [VortexRecord]
    public partial record struct Row(int Day, string Service, long Sequence, double Value);

    [VortexRecord]
    public partial record struct Doc(int Day, string Service, long Sequence, Variant Attributes);
}
