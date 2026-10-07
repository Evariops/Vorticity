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
/// The budgets a host shares among sessions, the process's beneath them, and the pools left after a
/// query (PLAN-HIGH-CARDINALITY, H2). They reserve against the process's own budget and read its shelf,
/// which every query of the process shares: they run alone.
/// </summary>
[Collection(nameof(CorePressureCollection))]
public sealed partial class SharedBudgetTests
{
    [Fact]
    public void TwoBudgetsWhoseCeilingsPassTheProcesssHoldUnderIt()
    {
        // Each budget's ceiling is the whole of what the process leaves its queries: together they pass
        // it, and the process's own count refuses what would pass it, whichever budget asks.
        long process = QueryMemoryBudget.Process.CeilingBytes;
        QueryMemoryBudget first = new QueryMemoryBudget(process);
        QueryMemoryBudget second = new QueryMemoryBudget(process);
        QueryMemory one = new QueryMemory(first);
        QueryMemory other = new QueryMemory(second);
        try
        {
            Assert.True(one.TryGrow(process / 2));
            Assert.False(other.TryGrow(process / 2 + (process / 4)));
            Assert.Equal(0, second.ReservedBytes);
            Assert.True(other.TryGrow(process / 4));
        }
        finally
        {
            one.Dispose();
            other.Dispose();
        }

        Assert.Equal(0, first.ReservedBytes);
        Assert.Equal(0, second.ReservedBytes);
    }

    [Fact]
    public async Task ASessionDisposedLeavesTheBudgetToTheSessionsThatShareIt()
    {
        string path = await WriteAsync(100_000, 400_000);
        try
        {
            QueryMemoryBudget budget = new QueryMemoryBudget(256L << 20);
            await using VortexSession staying = VortexSession.Create(options =>
            {
                options.MaxDegreeOfParallelism = 4;
                options.MemoryBudget = budget;
            });

            VortexSession leaving = VortexSession.Create(options => options.MemoryBudget = budget);
            await using VortexFile kept = await staying.OpenAsync(path, cancellationToken: Ct);
            VortexFile gone = await leaving.OpenAsync(path, cancellationToken: Ct);
            Assert.Equal(100_000, (await gone.Scan<Row>().GroupBy(r => r.Key).Select(g => g.Count()).ToListAsync(Ct)).Count);

            // A query of the staying session in flight, its groups held, while the other is disposed:
            // delivered whole, its reservation stays as it is while its reader waits, where parts merged
            // and built in the background would change it (PLAN-HIGH-CARDINALITY, H14).
            Aggregation<long> sums = kept.Scan<Row>().GroupBy(r => r.Key).Select(g => g.Sum(x => x.Value));
            sums.Plan.CoreParted = false;
            await using IAsyncEnumerator<long> reading = sums.GetAsyncEnumerator(Ct);
            Assert.True(await reading.MoveNextAsync());
            long held = budget.ReservedBytes;
            Assert.True(held > 0);
            await gone.DisposeAsync();
            await leaving.DisposeAsync();
            Assert.Equal(256L << 20, budget.CeilingBytes);
            Assert.Equal(held, budget.ReservedBytes);
            int read = 1;
            while (await reading.MoveNextAsync())
            {
                read++;
            }

            Assert.Equal(100_000, read);
            await reading.DisposeAsync();
            Assert.Equal(0, budget.ReservedBytes);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public async Task ASmallQueryBesideABigOneUnderPressureEndsAndSoDoesTheBigOne()
    {
        // A big query whose lanes' tables its budget cannot hold, and a small one beside it under the
        // same budget: the small one keeps its share, the big one ends in the core.
        string big = await WriteAsync(100_000, 1_200_000);
        string small = await WriteAsync(1_000, 50_000);
        try
        {
            (List<long> expected, long peak) = await SumsAsync(big, new QueryMemoryBudget(1L << 30));
            (List<long> few, _) = await SumsAsync(small, new QueryMemoryBudget(1L << 30));
            QueryMemoryBudget shared = new QueryMemoryBudget(peak / 10 * 7);
            Task<(List<long> Sums, long Peak)> bigger = SumsAsync(big, shared);
            Task<(List<long> Sums, long Peak)> smaller = SumsAsync(small, shared);
            Assert.Equal(few, (await smaller).Sums);
            Assert.Equal(expected, (await bigger).Sums);
            Assert.Equal(0, shared.ReservedBytes);
            Assert.Equal(0, shared.ActiveQueries);
        }
        finally
        {
            System.IO.File.Delete(big);
            System.IO.File.Delete(small);
        }
    }

    [Fact]
    public async Task AfterABigQueryTheShelfKeepsItsBudgetAndCollectionsSweepIt()
    {
        string path = await WriteAsync(200_000, 1_200_000);
        try
        {
            await using VortexSession session = VortexSession.Create(options => options.MaxDegreeOfParallelism = 14);
            await using VortexFile file = await session.OpenAsync(path, cancellationToken: Ct);
            Aggregation<long> sums = file.Scan<Row>().GroupBy(r => r.Key).Select(g => g.Sum(x => x.Value));
            sums.Plan.Core = true;
            sums.Plan.CoreLanes = 1;
            Assert.Equal(200_000, (await sums.ToListAsync(Ct)).Count);
            Assert.NotNull(sums.Plan.LastRun?.Core);
            Assert.True(ArrayShelf.Retained.Held <= ArrayShelf.Retained.Budget);

            // The first collection finds the shelf used since the last; the next, idle, empties it.
            for (int collection = 0; collection < 3 && ArrayShelf.Retained.Held > 0; collection++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }

            Assert.Equal(0, ArrayShelf.Retained.Held);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    private static async Task<(List<long> Sums, long Peak)> SumsAsync(string path, QueryMemoryBudget budget)
    {
        await using VortexSession session = VortexSession.Create(options =>
        {
            options.MaxDegreeOfParallelism = 14;
            options.MemoryBudget = budget;
        });

        await using VortexFile file = await session.OpenAsync(path, cancellationToken: Ct);
        Aggregation<long> sums = file.Scan<Row>().With(new ScanOptions { BatchRows = 1_024 }).GroupBy(r => r.Key).OrderBy(g => g.Key).Select(g => g.Sum(x => x.Value));
        return (await sums.ToListAsync(Ct), budget.PeakBytes);
    }

    private static async Task<string> WriteAsync(int keys, int rows)
    {
        Row[] data = new Row[rows];
        for (int row = 0; row < rows; row++)
        {
            data[row] = new Row((int)(((ulong)row * 0x9E37_79B9_7F4A_7C15UL >> 24) % (ulong)keys), row % 100);
        }

        string path = Path.Combine(AppContext.BaseDirectory, "shared-budget", $"shared-{Environment.ProcessId}-{Guid.NewGuid():N}.vortex");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using VortexFileWriter writer = VortexSession.Default.CreateWriter<Row>(path);
        await writer.WriteAsync<Row>(data, Ct);
        await writer.CompleteAsync(Ct);
        return path;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [VortexRecord]
    public partial record struct Row(int Key, long Value);
}
