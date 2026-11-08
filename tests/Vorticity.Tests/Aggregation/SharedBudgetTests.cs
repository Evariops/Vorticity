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
/// query. They reserve against the process's own budget and read its shelf,
/// which every query of the process shares: they run alone.
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

    /// <summary>
    /// A shelf kept as the process's, a megabyte and a quarter of it for small arrays: a query gives back twice that
    /// quarter of them, and half goes; once it ends, the next keeps them all, up to the megabyte; idle, the shelf comes back
    /// to its quarter. Kept to their quarter, the small arrays of a quantile per service and hour over a week at 32 lanes,
    /// its lanes' sketches and its parts' at once, were made again at every ask, 34 MB of them.
    /// </summary>
    [Fact]
    public void TheProcesssShelfKeepsTheSmallArraysQueriesLatelyGaveBackPastItsQuarter()
    {
        const long Budget = 1L << 20;
        ArrayShelf shelf = ArrayShelf.Governed(Budget);
        const int Arrays = 64;
        long bytes = Arrays * 1024L * sizeof(long);
        Assert.Equal(Budget / 2, bytes);
        foreach (long[] array in Enumerable.Range(0, Arrays).Select(static _ => new long[1024]))
        {
            shelf.Give(array);
        }

        Assert.Equal(Budget / 4, shelf.Held);

        // The query ends: the next keeps what this one found no room for.
        shelf.Expect(0);
        foreach (long[] array in Enumerable.Range(0, Arrays / 2).Select(static _ => new long[1024]))
        {
            shelf.Give(array);
        }

        Assert.Equal(bytes, shelf.Held);

        // Idle: the first tick finds the shelf used since the last, the next two find it idle and bring it back.
        for (int tick = 0; tick < 3; tick++)
        {
            shelf.Tick();
        }

        Assert.Equal(Budget / 4, shelf.Held);
    }

    /// <summary>
    /// A shelf kept as the process's, a quarter of a megabyte for small arrays, given a thousand arrays of a thousand lengths:
    /// past its budget, each one given lets the oldest go, and the piles it empties leave the order an eviction walks, which
    /// holds those that keep an array alone. Left in it, every eviction walked past the emptied ones: a quantile within 1 %
    /// per service and hour (T7), asked after another's sketches filled the shelf, took 2.5 s where it takes 0.25.
    /// </summary>
    [Fact]
    public void AnEvictionWalksOnlyThePilesThatHoldAnArray()
    {
        ArrayShelf shelf = ArrayShelf.Governed(1L << 20);
        for (int length = 1_000; length < 2_000; length++)
        {
            shelf.Give(new long[length]);
        }

        long held = shelf.Held;
        Assert.InRange(held, 1L << 17, 1L << 18);
        Assert.InRange(shelf.OrderedSmallPiles, 1, (int)(held / (1_000 * sizeof(long))) + 1);
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

    /// <summary>
    /// What the queries let go between two full collections counts with the rest of the process until the next one, never
    /// past what the heap holds: a query's working memory admitted and given back again and again, which the heap never
    /// held, left the process's budget nothing to grant until a full collection, and refused a lane its 64 KiB under load
    /// (2026-11-08). After a collection of the youngest generation, the budget grants it again.
    /// </summary>
    [Fact]
    public void WhatTheQueriesLetGoNeverCountsPastWhatTheHeapHolds()
    {
        QueryMemoryBudget process = new QueryMemoryBudget(64L << 30, process: null);
        Assert.True(process.TryReserve(1));
        process.Release(1);
        using QueryMemory memory = new QueryMemory(process);
        for (int i = 0; i < 128; i++)
        {
            memory.Measure(1L << 30);
            memory.Measure(-(1L << 30));
        }

        GC.Collect(0, GCCollectionMode.Forced, blocking: true);
        Assert.True(process.CanReserve(64 * 1024), $"{process.CeilingBytes:N0} bytes left to the queries");
        Assert.True(process.CeilingBytes > 32L << 30, $"{process.CeilingBytes:N0} bytes left to the queries");
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [VortexRecord]
    public partial record struct Row(int Key, long Value);
}
