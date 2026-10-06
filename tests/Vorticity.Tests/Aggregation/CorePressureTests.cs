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
        // the keys, so that the lanes' tables hold eight times the groups the core holds once. At 85 %
        // of what they reserve at most, their pass fits and their merge in parts does not: they merge in
        // series, each let go once merged. At 70 %, the pass does not fit either, near its end: a lane or
        // two turn to the core, the others merge in series, the largest last. At 50 %, most turn.
        const int rows = 1_200_000;
        Row[] data = new Row[rows];
        for (int row = 0; row < rows; row++)
        {
            data[row] = new Row((int)(((ulong)row * 0x9E37_79B9_7F4A_7C15UL >> 24) % 100_000), row % 100);
        }

        string path = Path.Combine(AppContext.BaseDirectory, "core-pressure", $"pressed-{Environment.ProcessId}-{Guid.NewGuid():N}.vortex");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using (VortexFileWriter writer = VortexSession.Default.CreateWriter<Row>(path))
        {
            await writer.WriteAsync<Row>(data, Ct);
            await writer.CompleteAsync(Ct);
        }

        try
        {
            (List<long> expected, long tables, _) = await SumsAsync(path, new QueryMemoryBudget(1L << 30), turn: false);
            QueryMemoryBudget under = new QueryMemoryBudget(tables / 100 * percent);
            await Assert.ThrowsAsync<VortexMemoryException>(async () => await SumsAsync(path, under, turn: false));
            Assert.Equal(0, under.ReservedBytes);

            (List<long> pressed, _, CoreRun? core) = await SumsAsync(path, under, turn: true);
            Assert.Equal(expected, pressed);
            if (percent >= 85)
            {
                Assert.Null(core);
            }
            else if (percent <= 50)
            {
                Assert.NotNull(core);
            }

            Assert.Equal(0, under.ReservedBytes);
        }
        finally
        {
            System.IO.File.Delete(path);
        }

        static async Task<(List<long> Sums, long Peak, CoreRun? Core)> SumsAsync(string path, QueryMemoryBudget budget, bool turn)
        {
            await using VortexSession session = VortexSession.Create(options =>
            {
                options.MaxDegreeOfParallelism = 14;
                options.MemoryBudget = budget;
            });

            await using VortexFile file = await session.OpenAsync(path, cancellationToken: Ct);
            Aggregation<long> sums = file.Scan<Row>().With(new ScanOptions { BatchRows = 1_024 }).GroupBy(r => r.Key).OrderBy(g => g.Key).Select(g => g.Sum(x => x.Value));
            AggregationPlan plan = ((AggregationQuery)sums.Query).Plan;
            plan.CoreUnderPressure = turn;
            return (await sums.ToListAsync(Ct), budget.PeakBytes, plan.LastRun?.Core);
        }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [VortexRecord]
    public partial record struct Row(int Key, long Value);
}
