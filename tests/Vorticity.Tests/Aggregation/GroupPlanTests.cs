using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Aggregating;
using Xunit;

namespace Vorticity.Tests.Aggregation;

/// <summary>
/// What a group by says of itself (docs/design/16-queries.md §10): its plan before a row is read, from
/// the query and the statistics alone, and its statistics once it ran.
/// </summary>
public sealed partial class GroupPlanTests
{
    private const int Rows = 200_000;

    [Fact]
    public async Task APlanSaysTheKeyItsBoundsAndHowTheGroupsAreOrdered()
    {
        string path = await WriteAsync(sorted: false);
        try
        {
            await using VortexSession session = VortexSession.Create(options => options.MaxDegreeOfParallelism = 4);
            await using VortexFile file = await session.OpenAsync(path, cancellationToken: Ct);

            GroupPlan plan = (await file.Scan<Row>().GroupBy(r => r.Key).Select(g => (g.Key, g.Count(), g.Sum(x => x.Value))).ExplainAsync(Ct)).Grouping!;
            GroupKeyPlan key = Assert.Single(plan.Keys);
            Assert.Equal("Key", key.Column);
            Assert.False(key.Sorted);
            Assert.Equal(10_000, key.ValueRange);
            Assert.Equal(10_000, plan.MostGroups);
            Assert.Null(plan.Streaming);
            Assert.NotNull(plan.NotStreaming);
            Assert.Equal(2, plan.Aggregates);
            Assert.Equal(GroupOrdering.None, plan.Order);
            Assert.Equal(4, plan.Degree);
            Assert.False(plan.Core);

            GroupPlan top = (await file.Scan<Row>().GroupBy(r => r.Key).OrderByDescending(g => g.Count()).Take(10).Select(g => g.Key).ExplainAsync(Ct)).Grouping!;
            Assert.Equal(GroupOrdering.Top, top.Order);
            Assert.Equal(10, top.Kept);
            GroupPlan sorted = (await file.Scan<Row>().GroupBy(r => r.Key).OrderBy(g => g.Sum(x => x.Value)).Select(g => g.Key).ExplainAsync(Ct)).Grouping!;
            Assert.Equal(GroupOrdering.Sort, sorted.Order);

            // Without a group by, nothing to say.
            Assert.Null((await file.Scan<Row>().Select(r => r.Value).Distinct().ExplainAsync(Ct)).Grouping);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public async Task OnASortedKeyThePlanSaysTheGroupsStream()
    {
        string path = await WriteAsync(sorted: true);
        try
        {
            await using VortexFile file = await VortexFile.OpenAsync(path, Ct);
            GroupPlan plan = (await file.Scan<Row>().GroupBy(r => r.Key).OrderBy(g => g.Key).Select(g => g.Count()).ExplainAsync(Ct)).Grouping!;
            Assert.True(plan.Keys[0].Sorted);
            Assert.Equal(0, plan.Streaming);
            Assert.Null(plan.NotStreaming);
            Assert.Equal(GroupOrdering.Streamed, plan.Order);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public async Task TheStatisticsSayWhatTheGroupByDid()
    {
        string path = await WriteAsync(sorted: false);
        try
        {
            QueryMemoryBudget budget = new QueryMemoryBudget(1L << 30);
            await using VortexSession session = VortexSession.Create(options =>
            {
                options.MaxDegreeOfParallelism = 4;
                options.MemoryBudget = budget;
            });

            await using VortexFile file = await session.OpenAsync(path, cancellationToken: Ct);
            Aggregation<long> counts = file.Scan<Row>().GroupBy(r => r.Key).Select(g => g.Count());
            Assert.Null(counts.Metrics.Grouping);
            Assert.Equal(10_000, (await counts.ToListAsync(Ct)).Count);
            GroupMetrics grouping = counts.Metrics.Grouping!;
            Assert.Equal(10_000, grouping.Groups);
            Assert.Equal(10_000, grouping.PeakGroups);
            Assert.True(grouping.PeakBytes > 0);
            Assert.True(grouping.Lanes >= 1);
            Assert.False(grouping.Core);
            Assert.Equal(0, grouping.Bursts);
            Assert.True(grouping.KeyBlocksByRange + grouping.KeyBlocksByCode + grouping.KeyBlocksHashed > 0);
            Assert.True(grouping.TimeToFirstBatch > TimeSpan.Zero);
            Assert.Equal(0, grouping.SpilledParts);

            // The core, asked for at four lanes: the plan says its cache and α, the statistics its work.
            Aggregation<long> held = file.Scan<Row>().GroupBy(r => r.Key).Select(g => g.Count());
            held.Plan.Core = true;
            held.Plan.CoreLanes = 1;
            held.Plan.CoreCapacity = 256;
            GroupPlan plan = (await held.ExplainAsync(Ct)).Grouping!;
            Assert.True(plan.Core);
            Assert.Equal(256, plan.CacheCapacity);
            Assert.True(plan.Alpha >= 1);
            Assert.Equal(10_000, (await held.ToListAsync(Ct)).Count);
            GroupMetrics core = held.Metrics.Grouping!;
            Assert.True(core.Core);
            Assert.True(core.CacheEvictions > 0);
            Assert.True(core.Tables > 0);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    private static async Task<string> WriteAsync(bool sorted)
    {
        // Ten thousand keys, 0 to 9 999, twenty rows each: in a row, or scattered.
        Row[] rows = new Row[Rows];
        for (int row = 0; row < Rows; row++)
        {
            int key = sorted ? row / (Rows / 10_000) : (int)(((ulong)row * 0x9E37_79B9_7F4A_7C15UL >> 24) % 10_000);
            rows[row] = new Row(key, row % 100);
        }

        string path = Path.Combine(AppContext.BaseDirectory, "group-plan", $"plan-{Environment.ProcessId}-{Guid.NewGuid():N}.vortex");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using VortexFileWriter writer = VortexSession.Default.CreateWriter<Row>(path);
        await writer.WriteAsync<Row>(rows, Ct);
        await writer.CompleteAsync(Ct);
        return path;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [VortexRecord]
    public partial record struct Row(int Key, long Value);
}
