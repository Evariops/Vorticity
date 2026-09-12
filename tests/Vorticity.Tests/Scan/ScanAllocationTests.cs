// docs/03-architecture.md §4 invariant 1 and contract §13.4: zero managed bytes per batch in
// steady state.
//
// THE BOUND IS MEASURED, NOT GUESSED. The only per-batch allocation the design permits is the
// RecordBatch object itself - §12.1 makes it a sealed class with readonly fields, so it cannot be
// recycled and the enumerator must make a new one per batch. So the test first measures exactly
// what one RecordBatch costs, then asserts the scan's steady-state per-batch figure is not one byte
// more. A round number like "under 200 bytes" would let a small per-batch List<T> or a boxed
// enumerator slip through.
//
// The second assertion is the one that catches growth WITH the data: the same per-batch figure over
// two different batch sizes and two different files. Anything proportional to rows, columns or
// segments moves it.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

using Vorticity.Arrays;
using Vorticity.Columns;
using Vorticity.File;
using Vorticity.Scan;
using Vorticity.Types;
using Xunit;

namespace Vorticity.Tests.Scan;

public sealed class ScanAllocationTests
{
    private const string Multi = "distributions/high_cardinality_i64_r8193";
    private const string Struct = "containers/uncompressed_canonical";

    [Fact]
    public async Task SteadyStateAllocatesNothingBeyondOneRecordBatch()
    {
        Decoders.EnsureRegistered();
        long batchObject = MeasureOneRecordBatch();
        Assert.True(batchObject > 0, "a RecordBatch must cost something, or the probe is wrong");

        long perBatch = await MeasurePerBatch(Multi, 500);

        // Not "under some threshold": exactly the RecordBatch and nothing else.
        Assert.True(
            perBatch <= batchObject,
            string.Create(
                CultureInfo.InvariantCulture,
                $"{perBatch} bytes per batch, but one RecordBatch is only {batchObject}"));
        Assert.Equal(batchObject, perBatch);
    }

    [Fact]
    public async Task ThePerBatchFigureDoesNotGrowWithTheData()
    {
        Decoders.EnsureRegistered();

        long small = await MeasurePerBatch(Multi, 500);
        long large = await MeasurePerBatch(Multi, 1000);
        long wide = await MeasurePerBatch(Struct, 500);

        Assert.Equal(small, large);
        Assert.Equal(small, wide);
    }

    [Fact]
    public async Task EveryBatchOfAMemoryMappedFileCompletesSynchronously()
    {
        // The zero-allocation claim only holds on the synchronous path; if a memory-mapped read
        // ever went asynchronous, the state machine would allocate and the measurement above would
        // be measuring the wrong thing.
        Decoders.EnsureRegistered();
        await using VortexFile file = await VortexFile.OpenAsync(Corpus.Path(Multi), CancellationToken.None);

        IAsyncEnumerator<RecordBatch> enumerator =
            file.Scan().WithMaxBatchRows(500).ExecuteAsync().GetAsyncEnumerator();

        // Next asserts IsCompletedSuccessfully on every step, which is the property under test.
        int batches = 0;
        while (Next(enumerator))
        {
            batches++;
        }

        await enumerator.DisposeAsync();
        Assert.True(batches > 10);
    }

    /// <summary>The exact cost of one <see cref="RecordBatch"/>, measured rather than assumed.</summary>
    private static long MeasureOneRecordBatch()
    {
        DTypeArena types = new DTypeArena();
        CanonicalArena arena = new CanonicalArena();
        DType dtype = types.Null(Nullability.Nullable);
        int root = arena.AddNull(dtype, 1);

        // Warm the JIT and the allocation context before measuring.
        for (int i = 0; i < 64; i++)
        {
            GC.KeepAlive(new RecordBatch(arena, root, 0));
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        RecordBatch probe = new RecordBatch(arena, root, 0);
        long after = GC.GetAllocatedBytesForCurrentThread();
        GC.KeepAlive(probe);
        return after - before;
    }

    private static async Task<long> MeasurePerBatch(string entry, int cap)
    {
        await using VortexFile file = await VortexFile.OpenAsync(Corpus.Path(entry), CancellationToken.None);

        // Two full warm-up scans: the first JITs everything, the second lets the arenas of a fresh
        // enumerator reach the size the file's shape needs.
        for (int warm = 0; warm < 2; warm++)
        {
            await Drain(file, cap);
        }

        IAsyncEnumerator<RecordBatch> enumerator =
            file.Scan().WithMaxBatchRows(cap).ExecuteAsync().GetAsyncEnumerator();
        try
        {
            // Skip the first batches of this enumerator: its own arenas grow on the way in, which
            // is the one-allocation-per-scan the design permits.
            for (int i = 0; i < 3; i++)
            {
                Assert.True(Next(enumerator));
            }

            List<long> perBatch = [];
            long terminal;
            while (true)
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                bool more = Next(enumerator);
                long delta = GC.GetAllocatedBytesForCurrentThread() - before;
                if (!more)
                {
                    // The final MoveNextAsync returns false and produces no batch. Asserted on its
                    // own rather than averaged in with the others, which is what the two claims
                    // actually are: a batch costs one RecordBatch, and ending costs nothing.
                    terminal = delta;
                    break;
                }

                perBatch.Add(delta);
            }

            Assert.Equal(0, terminal);
            Assert.True(perBatch.Count >= 5, "the measurement needs several steady-state batches");
            return SteadyState(perBatch);
        }
        finally
        {
            await enumerator.DisposeAsync();
        }
    }

    /// <summary>
    /// The figure every batch but at most one allocated.
    /// </summary>
    /// <remarks>
    /// This used to be the MEAN over the whole window, and the mean is the wrong statistic for a
    /// steady-state claim: a single one-off inside the window - tiered JIT promoting a method on
    /// its call-count threshold is the usual one - is divided across every batch and lands as a
    /// plausible-looking per-batch figure. 1.3 kB of rejit over fourteen batches reads as
    /// "174 bytes per batch, not 80", which is a real-looking regression that reproduces on
    /// roughly one run in twenty and on no particular commit.
    ///
    /// The floor is strictly stronger than the mean for what the test is FOR: anything allocated
    /// on every batch raises it, and anything allocated periodically shows up as more than one
    /// outlier. Only a true one-off is tolerated, and the message names it.
    /// </remarks>
    private static long SteadyState(List<long> perBatch)
    {
        long floor = long.MaxValue;
        for (int i = 0; i < perBatch.Count; i++)
        {
            floor = Math.Min(floor, perBatch[i]);
        }

        int outliers = 0;
        for (int i = 0; i < perBatch.Count; i++)
        {
            if (perBatch[i] != floor)
            {
                outliers++;
            }
        }

        Assert.True(
            outliers <= 1,
            string.Create(
                CultureInfo.InvariantCulture,
                $"{outliers} of {perBatch.Count} batches allocated more than the {floor}-byte " +
                $"floor: [{string.Join(", ", perBatch)}]"));

        return floor;
    }

    private static bool Next(IAsyncEnumerator<RecordBatch> enumerator)
    {
        ValueTask<bool> move = enumerator.MoveNextAsync();
        Assert.True(move.IsCompletedSuccessfully);
        return move.Result;
    }

    private static async Task Drain(VortexFile file, int cap)
    {
        await foreach (RecordBatch batch in file.Scan().WithMaxBatchRows(cap).ExecuteAsync())
        {
            Assert.True(batch.RowCount > 0);
        }
    }
}
