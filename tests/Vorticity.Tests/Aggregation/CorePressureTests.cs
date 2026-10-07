using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Aggregating;
using Xunit;

namespace Vorticity.Tests.Aggregation;

/// <summary>
/// The tests that measure what a query reserves at most, and set a budget under it: they run alone.
/// Beside other tests, the lanes a query starts on and the order they reserve in follow the machine's
/// load, and the peaks with them.
/// </summary>
[CollectionDefinition(nameof(CorePressureCollection), DisableParallelization = true)]
public sealed class CorePressureCollection
{
}

/// <summary>
/// The core under pressure (PLAN-HIGH-CARDINALITY, H4, milestone 2): a query whose lanes' tables its
/// budget cannot hold, during the pass or for their merge, turns to the core, which holds each group
/// once, and ends exact under that budget, giving everything back.
/// </summary>
[Collection(nameof(CorePressureCollection))]
public sealed partial class CorePressureTests
{
    [Theory]
    [InlineData(85)]
    [InlineData(70)]
    [InlineData(50)]
    public async Task AQueryItsLanesTablesOutgrowFinishesUnderItsBudget(int percent)
    {
        // A hundred thousand keys over 1.2 million rows, fourteen lanes: each lane meets more than half
        // the keys, so that the lanes' tables hold eight times the groups the core holds once. A lane
        // turns once the budget could not take every growing lane's next doubling and the entries it
        // would empty into: at 85 and 70 % of what the lanes reserve at most, some turn, the others
        // merge in series; at 50 %, most turn.
        string path = await WriteAsync();
        try
        {
            (List<long> expected, long tables, _) = await SumsAsync(path, new QueryMemoryBudget(1L << 30), turn: false, degree: 14);
            QueryMemoryBudget under = new QueryMemoryBudget(tables / 100 * percent);

            // The lanes' tables alone are refused well under their peak; near it, the ranges each lane
            // takes from the queue move what they hold by a tenth, and the refusal with them.
            if (percent <= 50)
            {
                await Assert.ThrowsAsync<VortexMemoryException>(async () => await SumsAsync(path, under, turn: false, degree: 14));
                Assert.Equal(0, under.ReservedBytes);
            }

            (List<long> pressed, _, CoreRun? core) = await SumsAsync(path, under, turn: true, degree: 14);
            Assert.Equal(expected, pressed);
            if (percent <= 50)
            {
                Assert.NotNull(core);
            }

            Assert.Equal(0, under.ReservedBytes);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public async Task ItsLanesWaitOnTasksWhenThePoolHasNoThreadToSpare()
    {
        // Twice as many lanes as the pool may run threads, under a budget their tables outgrow: a lane
        // that waited for its turn, or for room, on a thread would hold it from the lane it waits for.
        string path = await WriteAsync();
        int threads = Environment.ProcessorCount;
        ThreadPool.GetMaxThreads(out int workers, out int ports);
        try
        {
            (List<long> expected, long tables, _) = await SumsAsync(path, new QueryMemoryBudget(1L << 30), turn: false, degree: 2 * threads);
            QueryMemoryBudget under = new QueryMemoryBudget(tables / 2);
            Assert.True(ThreadPool.SetMaxThreads(threads, ports));
            (List<long> pressed, _, _) = await SumsAsync(path, under, turn: true, degree: 2 * threads).WaitAsync(TimeSpan.FromMinutes(2), Ct);
            Assert.Equal(expected, pressed);
            Assert.Equal(0, under.ReservedBytes);
        }
        finally
        {
            ThreadPool.SetMaxThreads(workers, ports);
            System.IO.File.Delete(path);
        }
    }

    private static async Task<string> WriteAsync()
    {
        const int rows = 1_200_000;
        Row[] data = new Row[rows];
        for (int row = 0; row < rows; row++)
        {
            data[row] = new Row((int)(((ulong)row * 0x9E37_79B9_7F4A_7C15UL >> 24) % 100_000), row % 100);
        }

        string path = Path.Combine(AppContext.BaseDirectory, "core-pressure", $"pressed-{Environment.ProcessId}-{Guid.NewGuid():N}.vortex");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using VortexFileWriter writer = VortexSession.Default.CreateWriter<Row>(path);
        await writer.WriteAsync<Row>(data, Ct);
        await writer.CompleteAsync(Ct);
        return path;
    }

    private static async Task<(List<long> Sums, long Peak, CoreRun? Core)> SumsAsync(string path, QueryMemoryBudget budget, bool turn, int degree)
    {
        await using VortexSession session = VortexSession.Create(options =>
        {
            options.MaxDegreeOfParallelism = degree;
            options.MemoryBudget = budget;
        });

        await using VortexFile file = await session.OpenAsync(path, cancellationToken: Ct);
        Aggregation<long> sums = file.Scan<Row>().With(new ScanOptions { BatchRows = 1_024 }).GroupBy(r => r.Key).OrderBy(g => g.Key).Select(g => g.Sum(x => x.Value));
        AggregationPlan plan = ((AggregationQuery)sums.Query).Plan;
        plan.CoreUnderPressure = turn;
        return (await sums.ToListAsync(Ct), budget.PeakBytes, plan.LastRun?.Core);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [VortexRecord]
    public partial record struct Row(int Key, long Value);
}
