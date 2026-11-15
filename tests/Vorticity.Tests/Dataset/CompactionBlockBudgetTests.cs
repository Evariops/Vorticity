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
    /// A budget of 16 MiB and a handle that learned an input and a writer cost a byte each, so that its plan takes the whole
    /// class: the job is refused a block, given up, and a narrower one runs in its place within the budget; once it ends,
    /// the budget holds nothing, and the next job has it whole.
    /// </summary>
    [Fact]
    public async Task AJobTheBudgetRefusesGivesWayToANarrowerOneAndLeavesTheBudgetWhole()
    {
        const long Small = 16L << 20;
        QueryMemoryBudget budget = new QueryMemoryBudget(Small);
        await using VortexSession session = VortexSession.Create(o => o.JobMemoryBudget = budget);
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Row.Schema, Options(session), Ct);
        List<Row> all = await AppendAsync(dataset, appends: 64, rows: 4_096);
        dataset.LearnDecodedRowBytes(1);
        dataset.LearnJobCosts(DatasetCompactor.InputFloorBytes + 1, 8_192, 1);
        int planned = (await dataset.PlanCompactionAsync(cancellationToken: Ct)).Job!.Inputs.Count;
        Assert.Equal(64, planned);

        CompactionResult? first = await dataset.CompactAsync(cancellationToken: Ct);

        Assert.NotNull(first);
        Assert.True(first.ObjectsIn < planned, $"the job took {first.ObjectsIn} objects of the {planned} first planned");
        Assert.InRange(first.MemoryReserved, 1, Small);
        Assert.Equal(0, budget.ReservedBytes);

        List<CompactionResult> rest = await CompactToTheEndAsync(dataset);
        Assert.All(rest, job => Assert.InRange(job.MemoryReserved, 0, Small));
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
