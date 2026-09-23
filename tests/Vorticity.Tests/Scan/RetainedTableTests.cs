using System;

using Vorticity.Arrays;
using Xunit;

namespace Vorticity.Tests.Scan;

/// <summary>
/// The chunks a scan holds across its batches are claimed, published, evicted and given back in a
/// table the scans of the process take in turn.
/// </summary>
public sealed class RetainedTableTests
{
    private const int Warmup = 20;
    private const int Iterations = 200;

    /// <summary>
    /// A scan that retains chunks allocates nothing for its table beyond the table object: the
    /// entries, their table and their arenas are the process's.
    /// </summary>
    /// <remarks>
    /// The table also leaves its monitor alone when no context waits on it, which this cannot see:
    /// the runtime the tests run on keeps a monitor's state in native memory, where Native AOT
    /// allocates a lock and a condition on the heap, and the AOT runner's count per round shows it.
    /// </remarks>
    [Fact]
    public void AScanThatRetainsChunksAllocatesNothingForItsTable()
    {
        using ScanContext claimant = new ScanContext(["vortex.primitive"]);

        long empty = Measure(() => Cycle(claimant, claims: false));
        long retaining = Measure(() => Cycle(claimant, claims: true));

        Assert.Equal(empty, retaining);
    }

    /// <summary>
    /// The pool keeps every entry a wide scan gives back, past the room it started with, and hands
    /// the same entries to the next scan; past its bound it keeps none.
    /// </summary>
    [Fact]
    public void ThePoolKeepsWhatAWideScanGivesBackUpToItsBound()
    {
        RetainedChunkPool pool = new RetainedChunkPool(initialCapacity: 4, maxCapacity: 24);
        RetainedChunk[] given = new RetainedChunk[20];
        for (int i = 0; i < given.Length; i++)
        {
            given[i] = new RetainedChunk();
            pool.Return(given[i]);
        }

        Assert.Equal(20, pool.Count);
        for (int i = given.Length - 1; i >= 0; i--)
        {
            Assert.Same(given[i], pool.Rent());
        }

        Assert.Equal(0, pool.Count);
        for (int i = 0; i < 30; i++)
        {
            pool.Return(new RetainedChunk());
        }

        Assert.Equal(24, pool.Count);
    }

    [Fact]
    public void AnEntryIsFoundUntilItsBatchesAreDeadAndThenEvicted()
    {
        using ScanContext claimant = new ScanContext(["vortex.primitive"]);
        using RetainedChunks table = new RetainedChunks(columns: 2, lanes: 1);

        // Twelve keys, past the table's first size of six: it grows and keeps every entry.
        for (long key = 0; key < 12; key++)
        {
            Assert.False(table.TryGet(key, claimant, batch: 1, out RetainedChunk? claim, out _, out _));
            table.Publish(claim!, nodeIndex: (int)key, batch: 1);
        }

        for (long key = 0; key < 12; key++)
        {
            Assert.True(table.Peek(key, batch: 2, out _, out int node));
            Assert.Equal((int)key, node);
        }

        // Batch 2 touched every entry: releasing it evicts none; batch 3 touched only the odd keys,
        // so releasing it evicts the even ones, whichever order the removals leave the table in.
        table.Release(2);
        for (long key = 1; key < 12; key += 2)
        {
            Assert.True(table.Peek(key, batch: 3, out _, out _));
        }

        table.Release(3);
        for (long key = 0; key < 12; key++)
        {
            Assert.Equal(key % 2 == 1, table.Peek(key, batch: 3, out _, out _));
        }
    }

    private static void Cycle(ScanContext claimant, bool claims)
    {
        RetainedChunks table = new RetainedChunks(columns: 4, lanes: 1);
        if (claims)
        {
            for (long batch = 1; batch <= 3; batch++)
            {
                for (long column = 0; column < 4; column++)
                {
                    if (!table.TryGet((batch * 10) + column, claimant, batch, out RetainedChunk? claim, out _, out _))
                    {
                        table.Publish(claim!, nodeIndex: 0, batch);
                    }
                }

                table.Release(batch);
            }
        }

        table.Dispose();
    }

    private static long Measure(Action body)
    {
        for (int i = 0; i < Warmup; i++)
        {
            body();
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < Iterations; i++)
        {
            body();
        }

        return GC.GetAllocatedBytesForCurrentThread() - before;
    }
}
