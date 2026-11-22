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
/// The budgets a host shares among sessions, and the process's beneath them. They reserve against the
/// process's own budget, which every query of the process shares: they run alone.
/// </summary>
[Collection(nameof(CorePressureCollection))]
[Trait("Category", "RunsAlone")]
public sealed partial class SharedBudgetTests
{
    [Fact]
    public void TwoBudgetsWhoseCeilingsPassTheProcesssHoldUnderIt()
    {
        // Each budget's ceiling is the whole of what the process leaves its queries: together they pass
        // it, and the process's own count refuses what would pass it, whichever budget asks. A parent of
        // a fixed ceiling stands for the process: the process's own moves with what the rest of it
        // holds, and under the full suite, read while the heap held other tests' garbage, the collection
        // the process's budget runs before it refuses gave back more than a quarter of it, and the
        // second budget's reservation passed (2026-10-09).
        long ceiling = 64L << 20;
        QueryMemoryBudget process = new QueryMemoryBudget(ceiling);
        QueryMemoryBudget first = new QueryMemoryBudget(ceiling, process);
        QueryMemoryBudget second = new QueryMemoryBudget(ceiling, process);
        QueryMemory one = new QueryMemory(first);
        QueryMemory other = new QueryMemory(second);
        try
        {
            Assert.True(one.TryGrow(ceiling / 2));
            Assert.False(other.TryGrow(ceiling / 2 + (ceiling / 4)));
            Assert.Equal(0, second.ReservedBytes);
            Assert.True(other.TryGrow(ceiling / 4));
        }
        finally
        {
            one.Dispose();
            other.Dispose();
        }

        Assert.Equal(0, first.ReservedBytes);
        Assert.Equal(0, second.ReservedBytes);
        Assert.Equal(0, process.ReservedBytes);
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
            // and built in the background would change it.
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

    /// <summary>
    /// What the queries let go between two full collections counts with the rest of the process until the next one, never
    /// past what the heap holds: a query's working memory admitted and given back again and again, which the heap never
    /// held, left the process's budget nothing to grant until a full collection, and refused a lane its 64 KiB under load
    /// (2026-11-08). After a collection of the youngest generation, the budget grants what it granted before, 128 GiB let go
    /// whatever the machine and whatever the process holds: the ceiling is judged against itself, read before, half of it at
    /// least, what the collector keeps committed between two readings moving it by some hundreds of megabytes, where the
    /// let-gos counted whole would take it all.
    /// </summary>
    [Fact]
    public void WhatTheQueriesLetGoNeverCountsPastWhatTheHeapHolds()
    {
        QueryMemoryBudget process = new QueryMemoryBudget(64L << 30, process: null);
        GC.Collect(0, GCCollectionMode.Forced, blocking: true);
        Assert.True(process.CanReserve(1));
        long before = process.CeilingBytes;
        using QueryMemory memory = new QueryMemory(process);
        for (int i = 0; i < 128; i++)
        {
            memory.Measure(1L << 30);
            memory.Measure(-(1L << 30));
        }

        GC.Collect(0, GCCollectionMode.Forced, blocking: true);
        Assert.True(process.CanReserve(64 * 1024), $"{process.CeilingBytes:N0} bytes left to the queries, {before:N0} before");
        Assert.True(process.CeilingBytes >= before / 2, $"{process.CeilingBytes:N0} bytes left to the queries, {before:N0} before");
    }

    /// <summary>
    /// The rest read from a collection is the heap less what the work measured when the collector ran. Blocks the work gave
    /// back to the pool and took again before the collection, a merge's windows from one of its slices to the next, are the
    /// work's when it runs: read less the fewest bytes measured since the reading before, they counted as the rest's too,
    /// 201 MiB of them under 2 GiB, and a lane of a compaction's writer was refused a block by a process that held less
    /// than its ceiling (2026-11-22). Whether the collection is noted by the reading itself, nothing having moved since,
    /// or by the first move of the measure after it.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WhatTheWorkGaveBackAndTookAgainBeforeACollectionIsNotReadAsTheRests(bool movesAfter)
    {
        const int held = 256 << 20;

        // The block, measured, then a full blocking collection read with it: the collector knows the block from then on,
        // and its allocation asks for no collection of its own, a background one whose numbers are those of its end.
        QueryMemoryBudget process = new QueryMemoryBudget(64L << 30, process: null);
        byte[] block = GC.AllocateUninitializedArray<byte>(held, pinned: true);
        process.Measure(held);
        Assert.True(process.CanReserve(1));
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true);
        Assert.True(process.CanReserve(1));

        // Another collection of the process between the two readings leaves the second nothing to tell them apart by,
        // and it falls back on the fewest bytes measured: the low is run again.
        for (int attempt = 0; attempt < 8; attempt++)
        {
            GC.Collect(0, GCCollectionMode.Forced, blocking: true);
            Assert.True(process.CanReserve(1));
            long before = process.CeilingBytes;
            int collections = GC.CollectionCount(0);

            // The low: given back for the pool to keep, taken again from what it keeps; then the collection.
            process.Returned(held);
            process.Reuse(-held);
            process.Measure(held);
            process.Reuse(held);
            GC.Collect(0, GCCollectionMode.Forced, blocking: true);
            if (movesAfter)
            {
                process.Measure(4096);
            }

            Assert.True(process.CanReserve(1));
            long after = process.CeilingBytes;
            if (GC.CollectionCount(0) == collections + 1)
            {
                Assert.True(after >= before - (held / 2), $"{after:N0} bytes left to the work after the low, {before:N0} before it; the rest's parts {process.RestParts}");
                GC.KeepAlive(block);
                return;
            }
        }

        Assert.Fail("Another collection ran beside each of eight runs of the low.");
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [VortexRecord]
    public partial record struct Row(int Key, long Value);
}
