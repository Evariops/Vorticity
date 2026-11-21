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
    /// A merge of four appends of rows that name 512 bytes of text each, one of twelve services, 61 MB of them, under a jobs'
    /// budget of 24 MiB, whose writer's share, two fifths of it (<see cref="JobShares"/>), the rows pass six times and its
    /// columns' chunks, aimed at 64 MiB, ten times, while the inputs keep the services in a dictionary and hold little, the
    /// writer's blocks of 1 024 rows, its objects streamed from a mebibyte: its columns write their chunks early, past what
    /// the share leaves them to gather, and the job runs to its end, refused nothing, the handle narrowed in nothing, and
    /// every row reads back.
    /// </summary>
    [Fact]
    public async Task AWriterThatWouldGatherTenTimesItsShareWritesEarlyAndIsRefusedNothing()
    {
        const long Budget = 24L << 20;
        await using VortexSession session = VortexSession.Create(o => o.JobMemoryBudget = new QueryMemoryBudget(Budget));
        await using MemoryObjectStore store = new MemoryObjectStore();
        DatasetOptions options = Options(session, fanout: 4);
        options = options with { Layout = options.Layout! with { ObjectBytes = 64 << 20 }, Write = options.Write with { BlockRows = 1_024 }, StreamedObjectBytes = 1 << 20 };
        Random random = new Random(17);
        List<Row> all = [];
        await using (VortexDataset appending = await VortexDataset.CreateAsync(store, Row.Schema, options, Ct))
        {
            for (int a = 0; a < 4; a++)
            {
                await AppendAsync(appending, all, [.. Enumerable.Range(0, 30_000).Select(i => new Row(0, Services[random.Next(Services.Length)], all.Count + i, random.Next(0, 100), random.NextDouble()))]);
            }
        }

        // The job's handle aims its columns' chunks at 64 MiB, which they would gather were the share not there.
        await using VortexDataset dataset = await VortexDataset.OpenAsync(store, options with { ChunkBytes = 64 << 20 }, Ct);
        const int Columns = 5;
        Assert.True(Columns * (64L << 20) >= 10 * JobShares.Of(Budget).Writer, "the columns' chunks weigh less than ten writer's shares");
        Assert.True(all.Sum(static r => (long)r.Service.Length) >= 4 * JobShares.Of(Budget).Writer, "the rows weigh less than four writer's shares");
        CompactionResult merged = Assert.IsType<CompactionResult>(await dataset.CompactAsync(cancellationToken: Ct));

        Assert.Equal(CompactionTrigger.Tier, merged.Trigger);
        Assert.Equal(4, merged.ObjectsIn);
        Assert.Equal(0, dataset.JobWidthCap);
        Assert.Equal(0, dataset.JobGatherHalvings);
        Assert.InRange(merged.MemoryReserved, 1, Budget);
        Assert.Equal(all.OrderBy(r => r.Sequence), (await dataset.Scan<Row>().ToRecordsAsync(Ct).ToListAsync(Ct)).OrderBy(r => r.Sequence));
    }

    /// <summary>
    /// Twelve services named in 512 bytes each: few values, which the inputs keep in a dictionary. A block of the writer's
    /// 8 192 rows of a kibibyte weighed 8 MiB, the whole budget of the first try, before any share was dealt; and inputs of
    /// 140 000 rows, decoded a chunk at a time, held 2 MiB each, which four did not fit.
    /// </summary>
    private static readonly string[] Services = [.. Enumerable.Range(0, 12).Select(static k => $"svc-{k:D2}-".PadRight(512, (char)('a' + k)))];

    /// <summary>
    /// A partition of 35 small objects under a budget that holds some 14 of them open at once, its fan-out 64, so that no
    /// merge of its size classes is ever due: its consolidation is preceded by merges of its smallest neighbours, each
    /// committed, until what is left fits, and runs refused nothing, the handle narrowed in nothing. Level 1 then holds the
    /// rows a consolidation of the same appends under a large budget holds. Without the merges, it consolidated the 35 at
    /// once, which its price said the budget does not hold.
    /// </summary>
    [Fact]
    public async Task AConsolidationWiderThanAMergeMergesItsSmallestNeighboursFirst()
    {
        const int Objects = 35;
        long budget = (14 * DatasetCompactor.InputFloorBytes) + (256 << 10);
        await using VortexSession session = VortexSession.Create(o => o.JobMemoryBudget = new QueryMemoryBudget(budget));
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Row.Schema, Options(session, fanout: 64), Ct);
        Random random = new Random(23);
        List<Row> all = [];
        for (int a = 0; a < Objects; a++)
        {
            await AppendAsync(dataset, all, Rows(random, 20 + (a % 7), all.Count));
        }

        IReadOnlyList<CompactionResult> results = await dataset.ConsolidateAsync(Ct);

        string ran = string.Join(", ", results.Select(static r => $"{r.Trigger} {r.ObjectsIn}"));
        Assert.True(results.Count >= 2 && results[0].Trigger == CompactionTrigger.Tier, ran);
        Assert.Equal(CompactionTrigger.Consolidation, results[^1].Trigger);
        Assert.True(results[^1].ObjectsIn < Objects, ran);
        Assert.Equal(0, dataset.JobWidthCap);
        Assert.Equal(0, dataset.Levels[0].Entries);
        List<Row> read = (await dataset.Scan<Row>().ToRecordsAsync(Ct).ToListAsync(Ct)).OrderBy(r => r.Sequence).ToList();
        Assert.Equal(all.OrderBy(r => r.Sequence), read);

        // The same appends consolidated at once under a large budget hold the same rows in level 1.
        await using MemoryObjectStore wide = new MemoryObjectStore();
        await using VortexDataset direct = await VortexDataset.CreateAsync(wide, Row.Schema, Options(VortexSession.Default, fanout: 64), Ct);
        List<Row> again = [];
        int at = 0;
        for (int a = 0; a < Objects; a++)
        {
            int count = 20 + (a % 7);
            await AppendAsync(direct, again, [.. all.Skip(at).Take(count)]);
            at += count;
        }

        IReadOnlyList<CompactionResult> once = await direct.ConsolidateAsync(Ct);
        Assert.Equal(CompactionTrigger.Consolidation, Assert.Single(once).Trigger);
        Assert.Equal(read, (await direct.Scan<Row>().ToRecordsAsync(Ct).ToListAsync(Ct)).OrderBy(r => r.Sequence));
    }

    /// <summary>
    /// A size class of 72 small objects of one weight, due under a fan-out of 64, whose merges a budget holds to two inputs
    /// open at once: each merge takes the lightest neighbours of the class, two objects of one weight, and no merge takes the
    /// object the merge before wrote. Taking the first of the class, that object, a merge took it again with the next, and
    /// its rows were written once more each merge: under 2 GiB, 240 appends of log records went through 179 merges of two,
    /// of 12.3, 21.0, 28.5, 33.5 and 38.9 MB in turn (2026-11-21).
    /// </summary>
    [Fact]
    public async Task AClassMergedTwoAtATimeMergesObjectsOfOneWeight()
    {
        const int Objects = 72;
        const int RowsEach = 20;
        long budget = (2 * DatasetCompactor.InputFloorBytes) + (256 << 10);
        await using VortexSession session = VortexSession.Create(o => o.JobMemoryBudget = new QueryMemoryBudget(budget));
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Row.Schema, Options(session, fanout: 64), Ct);
        Random random = new Random(29);
        List<Row> all = [];
        for (int a = 0; a < Objects; a++)
        {
            await AppendAsync(dataset, all, Rows(random, RowsEach, all.Count));
        }

        List<CompactionResult> merges = [];
        while (merges.Count < 200 && await dataset.CompactAsync(cancellationToken: Ct) is { } result)
        {
            merges.Add(result);
        }

        string ran = string.Join(", ", merges.Select(static r => $"{r.Trigger} {r.ObjectsIn}->{r.ObjectsOut} {r.Rows}"));
        List<CompactionResult> tiers = [.. merges.Where(static r => r.Trigger == CompactionTrigger.Tier)];
        Assert.True(tiers.Count >= 4 && tiers.All(static r => r.ObjectsIn <= 3), ran);

        // Objects of one weight a merge, appends each, none an object a merge before wrote.
        Assert.All(tiers, r => Assert.True(r.Rows == r.ObjectsIn * RowsEach, ran));
        List<Row> read = (await dataset.Scan<Row>().ToRecordsAsync(Ct).ToListAsync(Ct)).OrderBy(r => r.Sequence).ToList();
        Assert.Equal(all.OrderBy(r => r.Sequence), read);
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
    /// its batches, as many inputs open at once at most. A refusal that narrowed none of those of the one before it halves
    /// what the next job's writer gathers, down to a mebibyte, then fails, one given up on its price alone excepted; and a
    /// job that ran to its end lifts the caps, its writer's among them.
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
        long whole = DatasetCompactor.GatherCeiling(dataset);
        dataset.Narrowed(null, first);
        dataset.Narrowed(first, shorter);
        Assert.Equal(whole, DatasetCompactor.GatherCeiling(dataset));

        // Nothing narrower of its inputs or its batches: its writer gathers half as much, refusal after refusal, down to a
        // mebibyte, where a refusal fails.
        Assert.True(whole > DatasetCompactor.LeastGatherCeiling, $"a writer that gathers {whole} bytes has nothing to halve");
        for (long halved = whole / 2; halved >= DatasetCompactor.LeastGatherCeiling; halved /= 2)
        {
            dataset.Narrowed(shorter, same);
            Assert.Equal(Math.Max(halved, DatasetCompactor.LeastGatherCeiling), DatasetCompactor.GatherCeiling(dataset));
        }

        if (DatasetCompactor.GatherCeiling(dataset) > DatasetCompactor.LeastGatherCeiling)
        {
            dataset.Narrowed(shorter, same);
        }

        Assert.Equal(DatasetCompactor.LeastGatherCeiling, DatasetCompactor.GatherCeiling(dataset));
        Assert.Throws<VortexMemoryException>(() => dataset.Narrowed(shorter, same));
        dataset.Narrowed(new JobTooWideException(job, 1, 8L << 20), same);

        dataset.LearnJobCosts(1L << 20, 8_192, writerBytes: 0, width: int.MaxValue);
        Assert.Equal(0, dataset.JobWidthCap);
        Assert.Equal((0, 0), dataset.JobSizingCap);
        Assert.Equal(whole, DatasetCompactor.GatherCeiling(dataset));
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
