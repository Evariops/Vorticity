// Several compaction loops on one tiered dataset of many partitions, each dealt the partitions of its value's hash: their
// plans never share a partition, so no two loops run one job, and together they drain every partition.
//
// THE ORACLE IS THE PLANS AND THE ROWS: the partitions of each loop's jobs, the jobs no commit applied, and every row read
// back once the loops are done.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed partial class LoopPartitionsTests
{
    private const int Days = 12;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The plans of three loops over twelve days due a merge each: disjoint partitions, every day in one of them.</summary>
    [Fact]
    public async Task ThePlansOfSeveralLoopsShareNoPartition()
    {
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await CreateAsync(store);
        await AppendAsync(dataset, []);

        HashSet<string> seen = [];
        int planned = 0;
        for (int loop = 0; loop < 3; loop++)
        {
            CompactionPlan plan = await CompactionPolicy.PlanAsync(dataset, null, int.MaxValue, Ct, loops: 3, loop: loop);
            Assert.True(plan.Sharded);
            foreach (CompactionJob job in plan.Jobs)
            {
                Assert.True(seen.Add(CompactionPolicy.PartitionOf(dataset, job)), "two loops planned one partition");
                planned++;
            }
        }

        Assert.Equal(Days, planned);
    }

    /// <summary>Three loops on their own handles drain the twelve days with no job done for nothing.</summary>
    [Fact]
    public async Task SeveralLoopsDrainADatasetWithNoJobDoneTwice()
    {
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await CreateAsync(store);
        List<Row> all = [];
        await AppendAsync(dataset, all);

        ConcurrentQueue<CompactionResult> results = new ConcurrentQueue<CompactionResult>();
        Progress progress = new Progress(results);
        List<VortexDataset> handles = [];
        using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        List<Task> loops = [];
        for (int loop = 0; loop < 3; loop++)
        {
            VortexDataset handle = await VortexDataset.OpenAsync(store, new DatasetOptions(), Ct);
            handles.Add(handle);
            loops.Add(handle.RunCompactionAsync(new CompactionSchedule { Loops = 3, Loop = loop, Idle = TimeSpan.FromMilliseconds(5), Progress = progress }, stop.Token));
        }

        try
        {
            for (int wait = 0; wait < 2_000; wait++)
            {
                await dataset.RefreshAsync(Ct);
                if ((await dataset.PlanCompactionAsync(cancellationToken: Ct)).Job is null)
                {
                    break;
                }

                await Task.Delay(5, Ct);
            }
        }
        finally
        {
            await stop.CancelAsync();
            foreach (Task loop in loops)
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => loop);
            }

            foreach (VortexDataset handle in handles)
            {
                await handle.DisposeAsync();
            }
        }

        Assert.Null((await dataset.PlanCompactionAsync(cancellationToken: Ct)).Job);
        Assert.DoesNotContain(results, result => result.Outcome != OperationOutcome.Applied);
        Assert.Equal(all.OrderBy(r => r.Sequence), (await dataset.Scan<Row>().ToRecordsAsync(Ct).ToListAsync(Ct)).OrderBy(r => r.Sequence));
    }

    private sealed class Progress(ConcurrentQueue<CompactionResult> results) : IProgress<CompactionResult>
    {
        public void Report(CompactionResult value) => results.Enqueue(value);
    }

    private static ValueTask<VortexDataset> CreateAsync(MemoryObjectStore store) => VortexDataset.CreateAsync(store, Row.Schema, new DatasetOptions
    {
        Layout = new DatasetLayout
        {
            Partition = LayoutValue.Column(nameof(Row.Day)),
            SortKey = [LayoutValue.Column(nameof(Row.Service))],
            ObjectBytes = 64 << 20,
            MergeFanout = 4,
            CalmAfter = null,
        },
        ContentsObjectBytes = 0,
    }, Ct);

    /// <summary>Four appends of each of the days, a size class full in each.</summary>
    private static async Task AppendAsync(VortexDataset dataset, List<Row> all)
    {
        Random random = new Random(37);
        for (int a = 0; a < 4; a++)
        {
            for (int day = 0; day < Days; day++)
            {
                Row[] rows = [.. Enumerable.Range(0, 10).Select(i => new Row(day, $"svc-{random.Next(0, 6)}", all.Count + i))];
                all.AddRange(rows);
                await using ObjectDraft draft = dataset.StartObject();
                await draft.Writer.WriteAsync<Row>(rows, Ct);
                await dataset.AppendAsync(draft, Ct);
            }
        }
    }

    [VortexRecord]
    public partial record struct Row(int Day, string Service, long Sequence);
}
