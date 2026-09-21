// The allocation invariant of a scan: zero managed bytes per batch in steady state.
//
// THE BOUND IS MEASURED, NOT GUESSED. The only per-batch allocation the design permits is the
// RecordBatch object itself - it is a sealed class with readonly fields, so it cannot be
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
using Vorticity.Buffers;
using Vorticity.Columns;
using Vorticity.Expressions;
using Vorticity.File;
using Vorticity.Scan;
using Vorticity.Types;
using Vorticity.Writing;
using Xunit;

namespace Vorticity.Tests.Scan;

// IN THE SERIALISED COLLECTION because the degree-2 axis counts every thread's bytes, so anything
// else the process allocates while it runs would land in its figure.
[Collection(nameof(AllocationCollection))]
public sealed class ScanAllocationTests
{
    private const string Multi = "distributions/high_cardinality_i64_r8193";

    /// <summary>What a batch of a degree-2 scan may cost across every thread.</summary>
    /// <remarks>
    /// <para>
    /// THE MARGIN IS THE MEASUREMENT, and it is wide because the quantity is. Inside the suite the
    /// axis reads 3 466 to 3 479 with intrinsics over four runs and 3 459 to 3 696 without them
    /// over four more -- a spread of 237 bytes on a path whose degree-1 control reads 1 632 in
    /// every one of the eight. The swing is the pool's, not the scan's: lanes decode on threads the
    /// caller never touches, and how many of those the runtime injects is not a property of this
    /// code.
    /// </para>
    /// <para>
    /// WHAT THAT COSTS THE AXIS, said plainly: at this width it catches a regression of a few
    /// hundred bytes a batch and not the eighty a per-split closure cost, which is what it was
    /// added to watch. The instruments for that size are the sequential axis above, which holds an
    /// equality against one <see cref="RecordBatch"/>, and the read-path ceilings, which see a
    /// per-scan delegate at eight bytes. This one is here for the order of magnitude.
    /// </para>
    /// <para>
    /// Set at 3 900 rather than at the worst seen, because a ceiling ON the worst seen is the
    /// coin flip this axis already turned the no-intrinsics suite red with, one run in ten.
    /// </para>
    /// </remarks>
    private const long ParallelPerBatchCeiling = 3_900;
    private const string Struct = "containers/uncompressed_canonical";

    /// <summary>65536 rows in 64 zones of 1024.</summary>
    private const string Zoned = "containers/zoned_many_zones_nulls";

    [Fact]
    public async Task ACountAllocatesNothingPerBlock()
    {
        // A count allocates nothing per block. The proof tier is off so that
        // every LIVE block is decoded and the filter evaluated, and the two filters differ only in
        // how many blocks the mask leaves live -- 17 against 64 -- over the same rows, the same
        // plan, the same zone map, the same context and evaluation window. So the two figures
        // differ by exactly what 47 decoded blocks cost, which has to be nothing. (The plan's
        // own boundary array grows with the ROWS a scan covers, which is why the comparison is
        // not between two ranges.)
        ReleaseOnlyCeilings.Require();
        Decoders.EnsureRegistered();
        await using VortexFile file = await VortexFile.OpenAsync(Corpus.Path(Zoned), CancellationToken.None);
        VortexExpr everything = Expr.Ge(Expr.Field("monotone"), Expr.Literal(FilterLiteral.From(0L)));
        VortexExpr seventeen = Expr.Lt(Expr.Field("monotone"), Expr.Literal(FilterLiteral.From(1_000_000L + (3 * 17_408L))));

        (long few, long all) = await MeasureCounts(file, seventeen, everything);

        // NOT AN EQUALITY, AND THE SLACK CANNOT HIDE THE DEFECT IT IS THERE TO CATCH. The two runs
        // differ by 47 decoded blocks; the smallest object the runtime allocates is 24 bytes, so a
        // per-block allocation of any kind would show as 1 128 bytes or more. What is left under
        // that is the shared pool's own state -- a rent that hits in one run and misses in the
        // other -- which the interleaving below already suppresses and which an exact equality
        // made this test fail about one full-suite run in three.
        long drift = Math.Abs(few - all);
        Assert.True(
            drift <= 256,
            string.Create(
                CultureInfo.InvariantCulture,
                $"47 extra decoded blocks moved the allocation by {drift} bytes ({few} against {all})"));
    }

    /// <summary>
    /// The two counts, measured ALTERNATELY so that the shared pool is in the same state for both,
    /// then floored over several rounds.
    /// </summary>
    private static async Task<(long Few, long All)> MeasureCounts(
        VortexFile file, VortexExpr seventeen, VortexExpr everything)
    {
        const long fewRows = 17 * 1024L;
        const long allRows = 64 * 1024L;
        for (int warm = 0; warm < 3; warm++)
        {
            Assert.Equal(fewRows, await Count(file, seventeen));
            Assert.Equal(allRows, await Count(file, everything));
        }

        List<long> few = [];
        List<long> all = [];
        for (int i = 0; i < 5; i++)
        {
            few.Add(await Measure(file, seventeen, fewRows));
            all.Add(await Measure(file, everything, allRows));
        }

        few.Sort();
        all.Sort();
        return (few[0], all[0]);
    }

    private static async Task<long> Measure(VortexFile file, VortexExpr filter, long expected)
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        long counted = await Count(file, filter);
        long delta = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(expected, counted);
        return delta;
    }

    private static ValueTask<long> Count(VortexFile file, VortexExpr filter) =>
        file.Scan()
            .Where(filter)
            .WithTiers(TerminalTiers.All & ~TerminalTiers.FullBlock)
            .CountAsync();

    [Fact]
    public async Task SteadyStateAllocatesNothingBeyondOneRecordBatch()
    {
        ReleaseOnlyCeilings.Require();
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

    /// <summary>What a scan at a degree above one allocates per batch, across every thread.</summary>
    /// <remarks>
    /// MEASURED ACROSS THE PROCESS AND NOT THE CALLER'S THREAD, unlike every other axis here, and
    /// the parallel path is why: a lane decodes on the pool, so the bytes it allocates belong to a
    /// thread the caller never touches, and `GetAllocatedBytesForCurrentThread` would report a
    /// figure that leaves out most of what a degree buys or costs. The per-thread instrument also
    /// cannot span an await, which the first batch of a parallel scan always is.
    /// <para>
    /// So the claim is coarser than the sequential one -- a ceiling, not an equality with one
    /// <see cref="RecordBatch"/> -- and it covers what the sequential axis cannot see at all: a
    /// per-split closure, a per-split delegate, anything the pump allocates to start a lane.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ADegreeAboveOneAllocatesWithinItsPerBatchCeiling()
    {
        ReleaseOnlyCeilings.Require();
        Decoders.EnsureRegistered();

        long sequential = await MeasureWholeScan(Multi, 500, degree: 1);
        long parallel = await MeasureWholeScan(Multi, 500, degree: 2);
        long extra = parallel - sequential;

        // Reported whether or not it passes, as the other allocation axes report theirs: a ceiling
        // nobody can read the distance to is a ceiling nobody can maintain.
        Console.Out.Write(
            string.Create(
                CultureInfo.InvariantCulture,
                $"PARALLEL SCAN: {parallel} B per batch at degree 2 against {sequential} at degree 1, " +
                $"{extra} more, ceiling {ParallelPerBatchCeiling}\n"));

        Assert.True(
            parallel <= ParallelPerBatchCeiling,
            string.Create(
                CultureInfo.InvariantCulture,
                $"{parallel} B per batch at degree 2 against a ceiling of {ParallelPerBatchCeiling}"));
    }

    /// <summary>
    /// A descending key-ordered scan costs the two <see cref="RecordBatch"/> objects a reversal
    /// needs, and nothing else.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every key is null, so every row comes out of the reversing tail and nothing out of the key
    /// cursor: the two phases allocate differently and averaging them would measure neither.
    /// </para>
    /// <para>
    /// Two and not one, because a reversal cannot make fewer. The scan underneath yields a batch
    /// over the rows in file order, the reversed rows are a different root, and a
    /// <see cref="RecordBatch"/> is sealed, with readonly fields, precisely so that neither is
    /// recycled. Nothing else may be rebuilt per batch: not a plan, an enumerable or a filter for
    /// every split, a split being a batch here.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ADescendingScanCostsTheTwoBatchesAReversalNeeds()
    {
        ReleaseOnlyCeilings.Require();
        Decoders.EnsureRegistered();
        long batchObject = MeasureOneRecordBatch();

        string path = WriteNullKeys();
        try
        {
            long perBatch = await MeasureDescendingPerBatch(path, 64);
            Assert.Equal(2 * batchObject, perBatch);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
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

    /// <summary>Every thread's bytes over one whole scan, divided by the batches it produced.</summary>
    private static async Task<long> MeasureWholeScan(string entry, int cap, int degree)
    {
        await using VortexFile file = await VortexFile.OpenAsync(
            Corpus.Path(entry), CancellationToken.None);

        // Warm-ups for the same reason the per-thread probe has them: the first scan JITs, the
        // second lets a fresh enumerator's arenas reach the size the file needs.
        for (int warm = 0; warm < 2; warm++)
        {
            await DrainAt(file, cap, degree);
        }

        long before = GC.GetTotalAllocatedBytes(precise: true);
        int batches = await DrainAt(file, cap, degree);
        long after = GC.GetTotalAllocatedBytes(precise: true);

        Assert.True(batches >= 5, "the measurement needs several batches");
        return (after - before) / batches;
    }

    private static async Task<int> DrainAt(VortexFile file, int cap, int degree)
    {
        int batches = 0;
        await foreach (RecordBatch batch in file.Scan()
            .WithMaxBatchRows(cap)
            .WithDegreeOfParallelism(degree)
            .ExecuteAsync())
        {
            Assert.True(batch.RowCount > 0);
            batches++;
        }

        return batches;
    }

    private static async Task<long> MeasurePerBatch(string entry, int cap) =>
        await MeasurePerBatch(entry, cap, degree: 1);

    private static async Task<long> MeasurePerBatch(string entry, int cap, int degree)
    {
        await using VortexFile file = await VortexFile.OpenAsync(Corpus.Path(entry), CancellationToken.None);

        // Two full warm-up scans: the first JITs everything, the second lets the arenas of a fresh
        // enumerator reach the size the file's shape needs.
        for (int warm = 0; warm < 2; warm++)
        {
            await Drain(file, cap);
        }

        IAsyncEnumerator<RecordBatch> enumerator = file.Scan()
            .WithMaxBatchRows(cap)
            .WithDegreeOfParallelism(degree)
            .ExecuteAsync()
            .GetAsyncEnumerator();
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
    /// The figure the batches allocated in steady state: the floor, checked against the median.
    /// </summary>
    /// <remarks>
    /// Not the MEAN over the whole window, which is the wrong statistic for a steady-state claim:
    /// a single one-off inside the window - tiered JIT promoting a method on its call-count
    /// threshold is the usual one - is divided across every batch and lands as a real-looking
    /// per-batch regression that reproduces on no particular commit.
    ///
    /// The floor is strictly stronger than the mean for what the test is FOR: anything allocated on
    /// EVERY batch raises the floor itself, which the caller's assertion then catches against the
    /// measured RecordBatch cost. What the floor alone cannot see is something allocated on SOME
    /// batches, so the median is required to equal it - that catches anything affecting a majority
    /// while staying indifferent to how many methods happen to tier up inside the window. Counting
    /// outliers instead needs a number, and any number there is arbitrary.
    /// </remarks>
    private static long SteadyState(List<long> perBatch)
    {
        List<long> sorted = [.. perBatch];
        sorted.Sort();
        long floor = sorted[0];
        long median = sorted[sorted.Count / 2];

        Assert.True(
            median == floor,
            string.Create(
                CultureInfo.InvariantCulture,
                $"the median batch allocated {median} bytes against a floor of {floor}: " +
                $"[{string.Join(", ", perBatch)}]"));

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

    private static IAsyncEnumerable<RecordBatch> Descending(VortexFile file, int cap) =>
        file.Scan().InKeyOrder(NullKeyField, descending: true).WithMaxBatchRows(cap).ExecuteAsync();

    private static async Task<long> MeasureDescendingPerBatch(string path, int cap)
    {
        await using VortexFile file = await VortexFile.OpenAsync(path, CancellationToken.None);
        for (int warm = 0; warm < 2; warm++)
        {
            await foreach (RecordBatch batch in Descending(file, cap))
            {
                Assert.True(batch.RowCount > 0);
            }
        }

        IAsyncEnumerator<RecordBatch> enumerator = Descending(file, cap).GetAsyncEnumerator();
        try
        {
            for (int i = 0; i < 3; i++)
            {
                Assert.True(Next(enumerator));
            }

            List<long> perBatch = [];
            while (true)
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                bool more = Next(enumerator);
                long delta = GC.GetAllocatedBytesForCurrentThread() - before;
                if (!more)
                {
                    break;
                }

                perBatch.Add(delta);
            }

            Assert.True(perBatch.Count >= 5, "the measurement needs several steady-state batches");
            return SteadyState(perBatch);
        }
        finally
        {
            await enumerator.DisposeAsync();
        }
    }

    /// <summary>The key column of the file <see cref="WriteNullKeys"/> writes.</summary>
    private const string NullKeyField = "k";

    /// <summary>Rows of that file, every one of them with a null key.</summary>
    private const int NullKeyRows = 8_192;

    private static string WriteNullKeys()
    {
        DTypeArena types = new DTypeArena();
        DType i32 = types.Primitive(PType.I32, Nullability.Nullable);
        DType i64 = types.Primitive(PType.I64, Nullability.NonNullable);
        DType schema = types.Struct([NullKeyField, "payload"], [i32, i64], Nullability.NonNullable);

        string path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"vorticity-descending-{Guid.NewGuid():N}.vortex");
        WriteNullKeysAsync(path, schema, i32, i64).GetAwaiter().GetResult();
        return path;
    }

    private static async Task WriteNullKeysAsync(string path, DType schema, DType i32, DType i64)
    {
        CanonicalArena arena = new CanonicalArena();
        await using VortexFileWriter writer = VortexFileWriter.Create(path, schema, new VortexWriteOptions());
        const int batch = 2_048;
        for (int start = 0; start < NullKeyRows; start += batch)
        {
            int count = Math.Min(batch, NullKeyRows - start);
            VortexBuffer keys = arena.Allocate(count * sizeof(int), sizeof(int), out _);
            int key = arena.AddPrimitive(i32, count, Validity.AllInvalid, PType.I32, keys);

            VortexBuffer values = arena.Allocate(count * sizeof(long), sizeof(long), out Span<byte> bytes);
            Span<long> longs = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, long>(bytes);
            for (int i = 0; i < count; i++)
            {
                longs[i] = start + i;
            }

            int payload = arena.AddPrimitive(i64, count, Validity.NonNullable, PType.I64, values);
            int root = arena.AddStruct(schema, count, Validity.NonNullable, [key, payload]);
            using (RecordBatch record = new RecordBatch(arena, root, start))
            {
                await writer.WriteAsync(record, CancellationToken.None);
            }
        }

        await writer.CompleteAsync(CancellationToken.None);
    }
}
