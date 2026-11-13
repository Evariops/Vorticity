// What a job holds, priced before it runs and kept within the jobs' budget: an input by the rows it holds and what an open
// input costs, a row by what the handle's last merge measured it to weigh decoded, the writer by the rows the job writes,
// and a partition whose level 0 the budget does not hold at once merged by size class first.
//
// THE ORACLE IS THE ROWS AND THE PRICES: every row reads back after the jobs, the jobs reserve what they priced, and the
// last consolidation of a partition over its budget takes no more than the budget holds.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed partial class JobMemoryTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// The rows of an input's batch: a share that holds a window of rows for each input gives a window, a smaller one fewer
    /// rows, a power of two, never fewer than a block of 8 192, and never more than a window however large the share.
    /// </summary>
    [Theory]
    [InlineData(8, 1_000, 8L << 30, 1 << 17)]
    [InlineData(8, 1_000, 256L << 20, 1 << 15)]
    [InlineData(8, 1_000, 1L << 20, 8_192)]
    [InlineData(8, 1_000, -1, 8_192)]
    [InlineData(1, 100, 8L << 30, 1 << 17)]
    [InlineData(64, 100, 8L << 30, 1 << 14)]
    public void AnInputsBatchHoldsItsShareOfTheBudget(int inputs, long rowBytes, long share, int expected) =>
        Assert.Equal(expected, DatasetCompactor.InputBatchRows(inputs, rowBytes, share));

    /// <summary>
    /// The least a row takes decoded, from the schema alone: its numbers' widths, a view a text, and a list's offset, size
    /// and one element.
    /// </summary>
    [Fact]
    public void ARowsFloorIsWhatItsColumnsTakeAtLeast()
    {
        VortexSchema schema = VortexSchema.Create([
            new VortexField("Id", VortexType.Int64),
            new VortexField("Ratio", VortexType.Float32),
            new VortexField("Name", VortexType.Utf8),
            new VortexField("Counts", VortexType.List(VortexType.Int64))]);

        Assert.Equal(8 + 4 + 16 + (8 + 8), DatasetCompactor.FloorBytes(VortexTypes.ToDType(schema, new Vorticity.Types.DTypeArena())));
    }

    /// <summary>
    /// A merge measures what a row weighs decoded, which the handle keeps for the next job's price, and reserves what it
    /// priced: no more than the jobs' budget once its batches are sized to it.
    /// </summary>
    [Fact]
    public async Task AMergeMeasuresItsRowsAndReservesWhatItPriced()
    {
        await using VortexSession session = VortexSession.Create(o => o.JobMemoryBudget = new QueryMemoryBudget(256L << 20));
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Row.Schema, Options(session, fanout: 4), Ct);
        Random random = new Random(7);
        List<Row> all = [];
        for (int a = 0; a < 4; a++)
        {
            await AppendAsync(dataset, all, Rows(random, 2_000, all.Count));
        }

        Assert.Equal(0, dataset.DecodedRowBytes);
        CompactionResult? merged = await dataset.CompactAsync(cancellationToken: Ct);

        Assert.NotNull(merged);
        Assert.Equal(CompactionTrigger.Tier, merged.Trigger);
        Assert.InRange(dataset.DecodedRowBytes, 1, 64L << 10);
        Assert.InRange(merged.MemoryReserved, 1, 256L << 20);
        Assert.Equal(all.OrderBy(r => r.Sequence), (await dataset.Scan<Row>().ToRecordsAsync(Ct).ToListAsync(Ct)).OrderBy(r => r.Sequence));
    }

    /// <summary>
    /// A partition of 120 small objects under a budget that holds some 40 of them open at once: its consolidation is
    /// preceded by merges of its size classes, each committed, until what is left fits; the consolidation then takes
    /// what the budget holds, and every row reads back from level 1 alone.
    /// </summary>
    [Fact]
    public async Task APartitionOverItsBudgetIsMergedInPassesBeforeItsConsolidation()
    {
        const int Objects = 120;
        long budget = 40 * DatasetCompactor.InputFloorBytes;
        await using VortexSession session = VortexSession.Create(o => o.JobMemoryBudget = new QueryMemoryBudget(budget));
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Row.Schema, Options(session, fanout: 4), Ct);
        Random random = new Random(11);
        List<Row> all = [];
        for (int a = 0; a < Objects; a++)
        {
            await AppendAsync(dataset, all, Rows(random, 20, all.Count));
        }

        IReadOnlyList<CompactionResult> results = await dataset.ConsolidateAsync(Ct);

        Assert.Contains(results, result => result.Trigger == CompactionTrigger.Tier);
        CompactionResult consolidation = Assert.Single(results, result => result.Trigger == CompactionTrigger.Consolidation);
        Assert.Equal(CompactionTrigger.Consolidation, results[^1].Trigger);
        Assert.True(consolidation.ObjectsIn < Objects, $"the consolidation took {consolidation.ObjectsIn} objects");
        Assert.True(consolidation.MemoryReserved <= budget, $"the consolidation reserved {consolidation.MemoryReserved} bytes under {budget}");
        Assert.Equal(0, dataset.Levels[0].Entries);
        Assert.Equal(all.OrderBy(r => r.Sequence), (await dataset.Scan<Row>().ToRecordsAsync(Ct).ToListAsync(Ct)).OrderBy(r => r.Sequence));
    }

    /// <summary>The same partition under the default budget, which holds it at once: one consolidation, no pass.</summary>
    [Fact]
    public async Task APartitionWithinItsBudgetIsConsolidatedAtOnce()
    {
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Row.Schema, Options(VortexSession.Default, fanout: 4), Ct);
        Random random = new Random(11);
        List<Row> all = [];
        for (int a = 0; a < 120; a++)
        {
            await AppendAsync(dataset, all, Rows(random, 20, all.Count));
        }

        IReadOnlyList<CompactionResult> results = await dataset.ConsolidateAsync(Ct);

        CompactionResult consolidation = Assert.Single(results);
        Assert.Equal(CompactionTrigger.Consolidation, consolidation.Trigger);
        Assert.Equal(120, consolidation.ObjectsIn);
    }

    private static DatasetOptions Options(VortexSession session, int fanout) => new DatasetOptions
    {
        Session = session,
        Layout = new DatasetLayout
        {
            Partition = LayoutValue.Column(nameof(Row.Day)),
            SortKey = [LayoutValue.Column(nameof(Row.Service)), LayoutValue.Column(nameof(Row.Status))],
            ObjectBytes = 2 << 20,
            MergeFanout = fanout,
            CalmAfter = null,
        },
        ContentsObjectBytes = 0,
    };

    private static Row[] Rows(Random random, int count, long first) =>
        [.. Enumerable.Range(0, count).Select(i => new Row(0, $"svc-{random.Next(0, 12)}", first + i, random.Next(0, 100), random.NextDouble()))];

    private static async Task AppendAsync(VortexDataset dataset, List<Row> all, Row[] rows)
    {
        all.AddRange(rows);
        await using ObjectDraft draft = dataset.StartObject();
        await draft.Writer.WriteAsync<Row>(rows, Ct);
        await dataset.AppendAsync(draft, Ct);
    }

    [VortexRecord]
    public partial record struct Row(int Day, string Service, long Sequence, int Status, double Measure);
}
