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
    /// rows, a power of two, never fewer than 1 024 however tight the budget, and never more than a window however large
    /// the share, nor than a million rows over the inputs together.
    /// </summary>
    [Theory]
    [InlineData(8, 1_000, 8L << 30, 1 << 17)]
    [InlineData(8, 1_000, 256L << 20, 1 << 15)]
    [InlineData(8, 1_000, 1L << 20, DatasetCompactor.LeastBatchRows)]
    [InlineData(8, 1_000, 32L << 20, 4_096)]
    [InlineData(8, 1_000, -1, DatasetCompactor.LeastBatchRows)]
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

    /// <summary>
    /// A job of 300 inputs refused once they held 4 MiB each at batches of 8 192 rows: the handle keeps what an input held at
    /// the rows of its batch, which prices the next job, and takes half as many inputs; a refusal before the batches were
    /// sized narrows the jobs and keeps what the handle knew; a refusal that held less a row than the handle knew keeps
    /// what it knew.
    /// </summary>
    [Fact]
    public async Task AJobRefusedKeepsWhatAnInputHeldAtTheRowsOfItsBatch()
    {
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Row.Schema, Options(VortexSession.Default, fanout: 4), Ct);

        dataset.NarrowJobs(300, 300L * (4L << 20), (0, 0), writerBytes: 0);
        Assert.Equal(150, dataset.JobWidthCap);
        Assert.Equal(0, dataset.InputJobBytes);

        dataset.NarrowJobs(300, 300L * (4L << 20), (8_192, 300), writerBytes: 0);
        Assert.Equal(150, dataset.JobWidthCap);
        Assert.Equal(4L << 20, dataset.InputJobBytes);
        Assert.Equal(8_192, dataset.JobBatchRows);

        dataset.NarrowJobs(150, 150L * (1L << 20), (8_192, 150), writerBytes: 0);
        Assert.Equal(75, dataset.JobWidthCap);
        Assert.Equal(4L << 20, dataset.InputJobBytes);
    }

    /// <summary>
    /// A handle that has run no job measures one input before its first merge, through a merge of that input alone: what an
    /// input holds at a batch of a block and what a row weighs, read from one object, which the next plan prices its inputs
    /// by; nothing of the dataset is written.
    /// </summary>
    [Fact]
    public async Task AHandlesFirstMergeMeasuresAnInputOfItsOwnFirst()
    {
        await using MemoryObjectStore inner = new MemoryObjectStore();
        CountingObjectStore store = new CountingObjectStore(inner);
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Row.Schema, Options(VortexSession.Default, fanout: 4), Ct);
        Random random = new Random(3);
        List<Row> all = [];
        for (int a = 0; a < 8; a++)
        {
            await AppendAsync(dataset, all, Rows(random, 2_000, all.Count));
        }

        CompactionPlan plan = await dataset.PlanCompactionAsync(cancellationToken: Ct);
        Assert.NotNull(plan.Job);
        Assert.Equal(0, dataset.InputJobBytes);
        Assert.Equal(0, dataset.DecodedRowBytes);
        ulong version = dataset.Version;
        store.Reset();

        Assert.True(await DatasetCompactor.MeasureInputAsync(dataset, plan.Job, Ct));
        Assert.InRange(dataset.InputJobBytes, 1, 64L << 20);
        Assert.InRange(dataset.DecodedRowBytes, 1, 64L << 10);
        Assert.InRange(store.CountOf(ObjectOperation.Read), 1, 2);
        Assert.Equal(0, store.CountOf(ObjectOperation.Put));
        Assert.Equal(version, dataset.Version);
    }

    /// <summary>
    /// A job refused its blocks narrows the next: half its inputs for a merge by size class, and for any job half the rows of
    /// its batches, as many inputs open at once at most. A refusal that narrowed nothing of the one before it fails, one
    /// given up on its price alone excepted, and a job that ran to its end lifts the caps.
    /// </summary>
    [Fact]
    public async Task AJobRefusedNarrowsTheNextUntilNothingIsLeftToNarrow()
    {
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Row.Schema, Options(VortexSession.Default, fanout: 4), Ct);
        dataset.NarrowJobs(8, 8L << 20, (16_384, 8), writerBytes: 0);
        Assert.Equal(4, dataset.JobWidthCap);
        Assert.Equal((8_192, 8), dataset.JobSizingCap);

        CompactionJob job = new CompactionJob(0, 1, CompactionStyle.Leveled, CompactionTrigger.Consolidation, [], 0, 0);
        JobTooWideException first = new JobTooWideException(job, 8L << 20, 8L << 20, new VortexMemoryException("refused")) { Sizing = (16_384, 8) };
        JobTooWideException shorter = new JobTooWideException(job, 8L << 20, 8L << 20, new VortexMemoryException("refused")) { Sizing = (8_192, 8) };
        JobTooWideException same = new JobTooWideException(job, 8L << 20, 8L << 20, new VortexMemoryException("refused")) { Sizing = (8_192, 8) };
        VortexDataset.Narrowed(null, first);
        VortexDataset.Narrowed(first, shorter);
        Assert.Throws<VortexMemoryException>(() => VortexDataset.Narrowed(shorter, same));
        VortexDataset.Narrowed(new JobTooWideException(job, 1, 8L << 20), same);

        dataset.LearnJobCosts(1L << 20, 8_192, writerBytes: 0, width: int.MaxValue);
        Assert.Equal(0, dataset.JobWidthCap);
        Assert.Equal((0, 0), dataset.JobSizingCap);
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
