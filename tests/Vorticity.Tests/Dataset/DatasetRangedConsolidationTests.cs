// The consolidation of a partition on the lanes of a session (spec A, 6.4; A6): one stream of objects, its merge cut in
// slices of the key past a slice's rows and its writer sending several blocks through at once, as one lane consolidates it.
// A merge ran in ranges of the key side by side, as many as the lanes, each writing objects of its own; it runs as one
// stream now, the same objects at any degree, and only a piece of a consolidation merges the ranges below its cut side by
// side.
//
// THE ORACLE IS THE ONE LANE AND THE INVARIANTS: the same appends and the same delete into a dataset of four lanes and one
// of one; both consolidate a day each, hold every live row and no deleted one, at level 1 alone, and verify (each object one
// day's, sorted, the level of a day disjoint).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed partial class DatasetRangedConsolidationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// Eight appends of 15 000 rows, most of a first day, the rest of a second, merged, a status of the first day deleted,
    /// then each day consolidated, on four lanes and on one. Both run two consolidations, keep the live rows alone at level
    /// 1, and hold the layout's invariants.
    /// </summary>
    [Fact]
    public async Task ADayConsolidatedOnFourLanesHoldsWhatOneLaneHolds()
    {
        (List<Row> appended, List<Row> ranged, _) = await ConsolidateAsync(lanes: 4);
        (List<Row> appendedAgain, List<Row> single, _) = await ConsolidateAsync(lanes: 1);
        Assert.Equal(appended, appendedAgain);
        List<Row> live = [.. appended.Where(static row => !(row.Day == 0 && row.Status == 7))];
        Assert.Equal(live, ranged.OrderBy(static row => row.Sequence));
        Assert.Equal(live, single.OrderBy(static row => row.Sequence));
    }

    /// <summary>
    /// The same days consolidated on four lanes under a jobs' budget of 10 MiB: every job's blocks within the budget, and
    /// every live row held as under a roomy one; a job's merge is one stream whatever the budget.
    /// </summary>
    [Fact]
    public async Task UnderABudgetAConsolidationHoldsItsBlocksWithinItAndTheSameRows()
    {
        // 10 MiB: one range at a time held 7.98 MB, then 8.77 MB once an input's batch stays until the writers copied the
        // runs they keep of it (2026-11-17); a consolidation's merge is one stream since.
        const long Budget = 10L << 20;
        (List<Row> appended, List<Row> roomy, IReadOnlyList<CompactionResult> wide) = await ConsolidateAsync(lanes: 4);
        (_, List<Row> tight, IReadOnlyList<CompactionResult> narrow) = await ConsolidateAsync(lanes: 4, new QueryMemoryBudget(Budget));
        List<Row> live = [.. appended.Where(static row => !(row.Day == 0 && row.Status == 7))];
        Assert.Equal(live, roomy.OrderBy(static row => row.Sequence));
        Assert.Equal(live, tight.OrderBy(static row => row.Sequence));
        Assert.True(wide.Any(static job => job.MemoryReserved > 0), string.Join("; ", wide.Select(static job => $"{job.ObjectsIn} in, {job.BytesIn} B, {job.MemoryReserved} B reserved")));
        Assert.All(narrow, static job => Assert.InRange(job.MemoryReserved, 0, Budget));
        Assert.All(wide.Concat(narrow).Where(static job => !job.Piece), static job => Assert.Equal(1, job.RangesAtOnce));
    }

    /// <summary>
    /// A job's writer holds at most the rows it writes, whatever the chunks its columns gather: columns that gather chunks
    /// of 64 MiB, sixteen times that each at most, priced a writer at several gigabytes, though a day of some 100 000 rows
    /// holds a few megabytes. Priced by its rows, a job reserves what it holds, under a budget of 3 GiB as under one of 64 GiB.
    /// </summary>
    /// <remarks>
    /// The roomy budget is one of its own, under a parent of a fixed ceiling standing for the process (2026-11-02).
    /// </remarks>
    [Fact]
    public async Task AJobsWriterIsPricedByTheRowsItWrites()
    {
        QueryMemoryBudget roomyBudget = new QueryMemoryBudget(64L << 30, new QueryMemoryBudget(64L << 30, process: null));
        (List<Row> appended, List<Row> roomy, _) = await ConsolidateAsync(lanes: 4, roomyBudget, chunkBytes: 64 << 20);
        (_, List<Row> tight, IReadOnlyList<CompactionResult> narrow) = await ConsolidateAsync(lanes: 4, new QueryMemoryBudget(3L << 30), chunkBytes: 64 << 20);
        List<Row> live = [.. appended.Where(static row => !(row.Day == 0 && row.Status == 7))];
        Assert.Equal(live, roomy.OrderBy(static row => row.Sequence));
        Assert.Equal(live, tight.OrderBy(static row => row.Sequence));
        Assert.All(narrow.Where(static job => job.ObjectsMoved == 0 && job.Rows > 0), static job => Assert.InRange(job.MemoryReserved, 1, 3L << 30));
    }

    /// <summary>
    /// What a consolidation's input holds open of its object, which a range's memory counts, is a chunk of each column, its
    /// bytes over its chunks as the object's contents count them: nearly the data of objects whose columns are one chunk
    /// each, a few chunks of 16 KiB of objects cut in those.
    /// </summary>
    [Fact]
    public async Task AnInputHoldsAChunkOfEachColumnOpen()
    {
        (long whole, long wholeBytes) = await OpenChunkAsync(64 << 20);
        (long chunked, long chunkedBytes) = await OpenChunkAsync(16 << 10);
        Assert.InRange(whole, wholeBytes / 4, wholeBytes);
        Assert.InRange(chunked, 1, 4L * Row.Schema.Count * (16 << 10));
        Assert.True(chunked * 2 < whole, $"{chunked} bytes open of inputs of {chunkedBytes} in chunks of 16 KiB, {whole} of inputs of {wholeBytes} in one chunk a column");
    }

    /// <summary>The bytes an input of a day's consolidation holds open, and its inputs' bytes, both averaged over them.</summary>
    private static async Task<(long Open, long Bytes)> OpenChunkAsync(int chunkBytes)
    {
        Random random = new Random(47);
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Row.Schema, new DatasetOptions
        {
            Layout = new DatasetLayout
            {
                Partition = LayoutValue.Column(nameof(Row.Day)),
                SortKey = [LayoutValue.Column(nameof(Row.Service)), LayoutValue.Column(nameof(Row.Status))],
                ObjectBytes = 1 << 20,
                MergeFanout = 4,
                CalmAfter = null,
            },
            ContentsObjectBytes = 0,
            ChunkBytes = chunkBytes,
        }, Ct);
        for (int a = 0; a < 8; a++)
        {
            Row[] rows = [.. Enumerable.Range(0, 30_000).Select(i => new Row(0, $"svc-{random.Next(0, 400):D3}", (a * 30_000) + i, random.Next(0, 100), random.NextDouble()))];
            await using ObjectDraft draft = dataset.StartObject();
            await draft.Writer.WriteAsync<Row>(rows, Ct);
            await dataset.AppendAsync(draft, Ct);
        }

        while (await dataset.CompactAsync(cancellationToken: Ct) is not null)
        {
        }

        CompactionPlan plan = await CompactionPolicy.PlanConsolidationsAsync(dataset, Ct);
        CompactionJob job = Assert.Single(plan.Jobs, static job => job.Trigger == CompactionTrigger.Consolidation);
        Assert.All(job.Inputs, static input => Assert.NotNull(input.Entry.Contents));
        return (DatasetCompactor.OpenChunkBytes(dataset, dataset.Snapshot.Schema, job), (long)job.Inputs.Average(static input => input.Entry.Bytes));
    }

    /// <summary>
    /// Sixteen appends of overlapping keys, with no partition, merged level by level on four lanes and on one: a merge of
    /// several objects' worth is one stream on four as on one, the jobs write the same objects, and the levels hold the rows,
    /// sorted and disjoint, as a verification finds them.
    /// </summary>
    [Fact]
    public async Task ALevelMergedOnFourLanesWritesWhatOneLaneWrites()
    {
        (List<Row> appended, List<Row> four, IReadOnlyList<CompactionResult> fourJobs) = await MergeLevelsAsync(lanes: 4);
        (_, List<Row> one, IReadOnlyList<CompactionResult> oneJobs) = await MergeLevelsAsync(lanes: 1);
        List<Row> all = [.. appended.OrderBy(static row => row.Sequence)];
        Assert.Equal(all, four.OrderBy(static row => row.Sequence));
        Assert.Equal(all, one.OrderBy(static row => row.Sequence));
        Assert.Equal(oneJobs.Select(static job => (job.ObjectsIn, job.ObjectsOut, job.BytesOut)), fourJobs.Select(static job => (job.ObjectsIn, job.ObjectsOut, job.BytesOut)));
        Assert.All(fourJobs, static job => Assert.Equal(1, job.RangesAtOnce));
    }

    private static async Task<(List<Row> Appended, List<Row> Read, IReadOnlyList<CompactionResult> Merged)> MergeLevelsAsync(int lanes)
    {
        Random random = new Random(53);
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexSession session = VortexSession.Create(options => options.MaxDegreeOfParallelism = lanes);
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Row.Schema, new DatasetOptions
        {
            Session = session,
            Layout = new DatasetLayout
            {
                SortKey = [LayoutValue.Column(nameof(Row.Service)), LayoutValue.Column(nameof(Row.Sequence))],
                ObjectBytes = 1 << 18,
                MergeFanout = 16,
                CalmAfter = null,
            },
            ContentsObjectBytes = 0,
        }, Ct);
        List<Row> all = [];
        for (int a = 0; a < 16; a++)
        {
            Row[] rows = [.. Enumerable.Range(0, 15_000).Select(i => new Row(0, $"svc-{random.Next(0, 400):D3}", all.Count + i, random.Next(0, 100), random.NextDouble()))];
            all.AddRange(rows);
            await using ObjectDraft draft = dataset.StartObject();
            await draft.Writer.WriteAsync<Row>(rows, Ct);
            await dataset.AppendAsync(draft, Ct);
        }

        List<CompactionResult> merged = [];
        while (await dataset.CompactAsync(cancellationToken: Ct) is { } result)
        {
            merged.Add(result);
        }

        DatasetVerification verified = await dataset.VerifyAsync(cancellationToken: Ct);
        Assert.True(verified.Holds, $"{lanes} lanes:\n{string.Join('\n', verified.Problems)}");
        return (all, await dataset.Scan<Row>().ToRecordsAsync(Ct).ToListAsync(Ct), merged);
    }

    private static async Task<(List<Row> Appended, List<Row> Read, IReadOnlyList<CompactionResult> Consolidated)> ConsolidateAsync(
        int lanes, QueryMemoryBudget? jobs = null, int chunkBytes = 1 << 20)
    {
        Random random = new Random(47);
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexSession session = VortexSession.Create(options =>
        {
            options.MaxDegreeOfParallelism = lanes;
            options.JobMemoryBudget = jobs;
        });
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Row.Schema, new DatasetOptions
        {
            Session = session,
            Layout = new DatasetLayout
            {
                Partition = LayoutValue.Column(nameof(Row.Day)),
                SortKey = [LayoutValue.Column(nameof(Row.Service)), LayoutValue.Column(nameof(Row.Status))],
                ObjectBytes = 1 << 18,
                MergeFanout = 4,
                CalmAfter = null,
            },
            ContentsObjectBytes = 0,
            ChunkBytes = chunkBytes,
        }, Ct);
        List<Row> all = [];
        for (int a = 0; a < 8; a++)
        {
            Row[] rows = [.. Enumerable.Range(0, 15_000).Select(i => new Row(random.Next(0, 8) == 0 ? 1 : 0, $"svc-{random.Next(0, 400):D3}", all.Count + i, random.Next(0, 100), random.NextDouble()))];
            all.AddRange(rows);
            await using ObjectDraft draft = dataset.StartObject();
            await draft.Writer.WriteAsync<Row>(rows, Ct);
            await dataset.AppendAsync(draft, Ct);
        }

        while (await dataset.CompactAsync(cancellationToken: Ct) is not null)
        {
        }

        await dataset.DeleteAsync<Row>(static row => row.Day == 0 & row.Status == 7, Ct);
        // A day larger than its lanes' objects is consolidated in pieces, each a result: the last of each day is its own.
        IReadOnlyList<CompactionResult> consolidated = await dataset.ConsolidateAsync(Ct);
        Assert.Equal(2, consolidated.Count(static job => !job.Piece));
        await foreach (PositionedObject held in dataset.ScanBuilder().ObjectsAsync(Ct))
        {
            Assert.True(held.Level == 1 && !held.Entry.HasDeletions, $"{lanes} lanes: '{held.Entry.Key}' at level {held.Level}, deletions {held.Entry.HasDeletions}");
        }

        DatasetVerification verified = await dataset.VerifyAsync(cancellationToken: Ct);
        Assert.True(verified.Holds, $"{lanes} lanes:\n{string.Join('\n', verified.Problems)}");
        return (all, await dataset.Scan<Row>().ToRecordsAsync(Ct).ToListAsync(Ct), consolidated);
    }

    [VortexRecord]
    public partial record struct Row(int Day, string Service, long Sequence, int Status, double Value);
}
