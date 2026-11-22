// A compaction's blocks under the jobs' budget: a backlog of appends that all overlap compacts to its end under a budget
// that holds a fraction of it, no job's blocks ever past the budget; a job the budget refuses gives way to a narrower one
// and leaves the next its whole budget; many small appends cost what they hold.
//
// THE ORACLE IS THE BUDGET AND THE ROWS: what each job's blocks held at most, against the budget, and every row read back.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed partial class CompactionBlockBudgetTests
{
    private const long Budget = 48L << 20;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// Sixty-four appends of 4 096 rows that all span the same keys, as an ingestion's flushes do: the maintainer's loop
    /// compacts them to its end under a budget of 48 MiB, and no job's blocks hold more than the budget.
    /// </summary>
    [Fact]
    public async Task ABacklogCompactsToItsEndWithinTheBudget()
    {
        await using VortexSession session = VortexSession.Create(o => o.JobMemoryBudget = new QueryMemoryBudget(Budget));
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Row.Schema, Options(session), Ct);
        List<Row> all = await AppendAsync(dataset, appends: 64, rows: 4_096);

        List<CompactionResult> jobs = await CompactToTheEndAsync(dataset);

        Assert.NotEmpty(jobs);
        Assert.All(jobs, job => Assert.InRange(job.MemoryReserved, 0, Budget));
        Assert.Null((await dataset.PlanCompactionAsync(cancellationToken: Ct)).Job);
        Assert.Equal(all.OrderBy(r => r.Sequence), (await dataset.Scan<Row>().ToRecordsAsync(Ct).ToListAsync(Ct)).OrderBy(r => r.Sequence));
    }

    /// <summary>Eight such appends, the regime of a maintainer that keeps up: one job, within the budget.</summary>
    [Fact]
    public async Task AFewAppendsCompactInOneJobWithinTheBudget()
    {
        await using VortexSession session = VortexSession.Create(o => o.JobMemoryBudget = new QueryMemoryBudget(Budget));
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Row.Schema, Options(session), Ct);
        List<Row> all = await AppendAsync(dataset, appends: 8, rows: 4_096);

        CompactionResult? job = await dataset.CompactAsync(cancellationToken: Ct);

        Assert.NotNull(job);
        Assert.Equal(8, job.ObjectsIn);
        Assert.InRange(job.MemoryReserved, 1, Budget);
        Assert.Equal(all.OrderBy(r => r.Sequence), (await dataset.Scan<Row>().ToRecordsAsync(Ct).ToListAsync(Ct)).OrderBy(r => r.Sequence));
    }

    /// <summary>
    /// A handle under a budget of 18 MiB runs a job planned for the whole class, sixty-four objects, by a handle under a
    /// large one, which its own plan would not take. Its inputs reserve what they hold as they read it, within the budget;
    /// the job runs to its end when the budget holds it, and otherwise ends short of room and commits what it merged, or
    /// commits nothing and gives way to the job the handle plans itself. Whichever, its inputs hold within the budget,
    /// the budget holds nothing once it ends, and the backlog compacts to its end, every row read back.
    /// </summary>
    /// <remarks>
    /// The job was refused a block and given up every time, and a narrower one ran in its place: its inputs and its writer
    /// were counted before it ran, and a block past the count gave it up. What a job holds in all is no longer within the
    /// budget whatever its writer holds: a writer cannot wait in the middle of a chunk, reserves nothing yet, and is
    /// counted past its share rather than refused. The sixty-four inputs of the first job take 13 MiB as they open and
    /// its writer 13 after them, 25 MiB under 18; the last job of the backlog, four objects of the whole class into one,
    /// holds 18.2, its writer 13 of a share of 7.2 (2026-11-21). A plan of the handle's own counts its writer at what
    /// the job before it held, and takes fewer inputs for it.
    /// </remarks>
    [Fact]
    public async Task AJobPlannedTooWideForItsBudgetRunsOrGivesWayAndLeavesTheBudgetWhole()
    {
        const long Small = 18L << 20;
        QueryMemoryBudget budget = new QueryMemoryBudget(Small);
        await using VortexSession session = VortexSession.Create(o => o.JobMemoryBudget = budget);
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Row.Schema, Options(session), Ct);
        List<Row> all = await AppendAsync(dataset, appends: 64, rows: 4_096);
        await using VortexSession large = VortexSession.Create(static o => o.JobMemoryBudget = new QueryMemoryBudget(4L << 30));
        await using VortexDataset wide = await VortexDataset.OpenAsync(store, Options(large), Ct);
        CompactionJob whole = (await wide.PlanCompactionAsync(cancellationToken: Ct)).Job!;
        int planned = whole.Inputs.Count;
        Assert.Equal(64, planned);
        Assert.True((await dataset.PlanCompactionAsync(cancellationToken: Ct)).Job!.Inputs.Count < planned, "the handle's own plan takes the whole class");

        CompactionResult? first = await dataset.RunWithinBudgetAsync(
            whole, async token => (await dataset.PlanCompactionAsync(cancellationToken: token)).Job, Ct);

        // Its inputs within the budget, which is never passed for them; and what it held in all, within the budget but for
        // what its writer held, counted past rather than refused.
        static void HoldsWithin(CompactionResult job) => Assert.True(
            job.MemoryReservedBy.Inputs <= Small && job.MemoryReserved - Small <= job.MemoryReservedBy.Writer,
            $"a job held {job.MemoryReserved} bytes under {Small}, its inputs {job.MemoryReservedBy.Inputs} and its writer {job.MemoryReservedBy.Writer}");

        Assert.NotNull(first);
        Assert.InRange(first.ObjectsIn, 2, planned);
        HoldsWithin(first);
        Assert.Equal(0, budget.ReservedBytes);

        List<CompactionResult> rest = await CompactToTheEndAsync(dataset);
        Assert.All(rest, HoldsWithin);
        Assert.Equal(0, budget.ReservedBytes);
        Assert.Equal(all.OrderBy(r => r.Sequence), (await dataset.Scan<Row>().ToRecordsAsync(Ct).ToListAsync(Ct)).OrderBy(r => r.Sequence));
    }

    /// <summary>A thousand appends of four rows: each input costs what it holds, and the backlog compacts within the budget.</summary>
    [Fact]
    public async Task ManySmallAppendsCostWhatTheyHold()
    {
        await using VortexSession session = VortexSession.Create(o => o.JobMemoryBudget = new QueryMemoryBudget(Budget));
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Row.Schema, Options(session), Ct);
        List<Row> all = await AppendAsync(dataset, appends: 1_024, rows: 4);

        List<CompactionResult> jobs = await CompactToTheEndAsync(dataset);

        Assert.All(jobs, job => Assert.InRange(job.MemoryReserved, 0, Budget));
        Assert.Equal(all.Count, await dataset.ScanBuilder().CountAsync(Ct));
    }

    private static async Task<List<CompactionResult>> CompactToTheEndAsync(VortexDataset dataset)
    {
        List<CompactionResult> jobs = [];
        while (await dataset.CompactAsync(cancellationToken: Ct) is { } job)
        {
            jobs.Add(job);
        }

        return jobs;
    }

    private static DatasetOptions Options(VortexSession session) => new DatasetOptions
    {
        Session = session,
        Layout = new DatasetLayout
        {
            Partition = LayoutValue.Column(nameof(Row.Day)),
            SortKey = [LayoutValue.Column(nameof(Row.Service)), LayoutValue.Column(nameof(Row.Series)), LayoutValue.Column(nameof(Row.Sequence))],
            ObjectBytes = 64 << 20,
            MergeFanout = 4,
            CalmAfter = null,
        },
        ContentsObjectBytes = 0,
    };

    /// <summary>
    /// <paramref name="appends"/> appends of <paramref name="rows"/> rows of one day, each a point of as many series of 512,
    /// so that every append spans the keys of the others, none merged as they came.
    /// </summary>
    private static async Task<List<Row>> AppendAsync(VortexDataset dataset, int appends, int rows)
    {
        List<Row> all = [];
        for (int a = 0; a < appends; a++)
        {
            Row[] batch = [.. Enumerable.Range(0, rows).Select(i =>
            {
                int series = (i + a) % 512;
                return new Row(0, $"svc-{series % 12:D2}", series, all.Count + i, $"/api/v{series % 3}/route-{series % 97}/{{id}}", $"pod-{series % 31}-{series:x4}");
            })];
            all.AddRange(batch);
            await using ObjectDraft draft = dataset.StartObject();
            await draft.Writer.WriteAsync<Row>(batch, Ct);
            await dataset.AppendAsync(draft, Ct);
        }

        return all;
    }

    [VortexRecord]
    public partial record struct Row(int Day, string Service, int Series, long Sequence, string Route, string Pod);
}
