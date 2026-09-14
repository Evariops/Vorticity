// Whether repeated scans accumulate anything, which is a different question from what they allocate.
//
// ScanAllocationTests pins the ALLOCATION per batch at exactly one RecordBatch, and PathAllocationTests
// pins the total for six whole paths. Both count bytes handed out. Neither can see a byte that is
// handed out and never given back: an allocation ratchet is satisfied by a scan that allocates the
// same amount every time and keeps all of it.
//
// bench/ALLOCATIONS.md says so in as many words - "every figure here is bytes allocated, not bytes
// live... nothing in this repository measures the second" - and docs/05 §3 lists peak RSS as one of
// the five missing axes for the same reason. This is that axis, as a test rather than a benchmark,
// because "does it grow" is a yes-or-no question and a benchmark would answer it with a mean.
//
// TWO THINGS ARE WATCHED, because the interesting one is not the managed heap. A scan's buffers come
// from AlignedBufferPool, which allocates NATIVE memory the GC never sees, so a pool that retains
// more blocks on every pass would leak invisibly to every other measurement here. The parked count
// is checked alongside the live heap for that reason.
using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Columns;
using Vorticity.File;
using Vorticity.Scan;
using Xunit;

namespace Vorticity.Tests.Scan;

/// <summary>Repeated scans settle instead of growing.</summary>
/// <remarks>
/// Shares <see cref="AllocationCollection"/>: <see cref="AlignedBufferPool.Shared"/> is
/// process-global, so a neighbouring class renting from it would move the parked counts under this
/// one's feet.
/// </remarks>
[Collection(nameof(AllocationCollection))]
public sealed class LiveMemoryTests
{
    private const string Entry = "containers/zoned_many_zones_nulls";

    /// <summary>Scans before the first reading, to let statics and the JIT settle.</summary>
    private const int Settle = 20;

    /// <summary>Scans between the two readings.</summary>
    private const int Between = 60;

    /// <summary>
    /// Live managed bytes the second reading may exceed the first by.
    /// </summary>
    /// <remarks>
    /// Not zero, and not a ratchet on a measured value: <see cref="GC.GetTotalMemory"/> after a
    /// forced collection still includes whatever the runtime itself has settled into, and tiered
    /// recompilation continues well past twenty scans. What the number has to be small enough to
    /// catch is ACCUMULATION - anything retained per scan would add megabytes over sixty passes.
    ///
    /// LOWERED 256 KiB -> 64 KiB on 2026-09-14 (BENCH-AUDIT.md §3.3): three runs measured the
    /// growth at 0 B, 32 B and 0 B, so the old ceiling was eight thousand times the observed value
    /// and would have let a real leak of a kilobyte per scan through. 64 KiB is still two thousand
    /// times the worst reading, which is the margin this needs rather than the margin it had: the
    /// figures above come from ONE machine, and CI runs four operating systems where tiering
    /// settles differently.
    /// </remarks>
    private const long ManagedGrowthCeiling = 64 * 1024;

    /// <summary>Parked pool blocks the second reading may exceed the first by.</summary>
    /// <remarks>
    /// LOWERED 64 -> 16 on the same day and by the same reasoning: the pool parked 15 blocks after
    /// twenty scans and the same 15 after eighty, three runs out of three. Sixteen leaves room for
    /// a later scan to touch size classes it had never seen, which is what the assertion below is
    /// worded to allow; sixty-four was room for a leak.
    /// </remarks>
    private const int ParkedGrowthCeiling = 16;

    [Fact]
    public async Task SixtyMoreScansRetainNothing()
    {
        Decoders.EnsureRegistered();
        string path = Corpus.Path(Entry);

        for (int i = 0; i < Settle; i++)
        {
            await Scan(path);
        }

        long firstManaged = Live();
        int firstParked = Parked();

        for (int i = 0; i < Between; i++)
        {
            await Scan(path);
        }

        long secondManaged = Live();
        int secondParked = Parked();

        string report = string.Create(
            CultureInfo.InvariantCulture,
            $"LIVE MEMORY after {Settle} scans: {firstManaged} B managed, {firstParked} parked blocks; " +
            $"after {Settle + Between}: {secondManaged} B, {secondParked} blocks");
        Console.Out.WriteLine(report);

        Assert.True(
            secondManaged - firstManaged <= ManagedGrowthCeiling,
            $"live managed memory grew by {secondManaged - firstManaged} B over {Between} scans.\n{report}");

        // The pool's retention is bounded by its own maxPerBucket, so this cannot drift upward
        // without the pool being asked for a size class it had never seen. Equality would be too
        // strict - a later scan may touch one more class - but growth proportional to the scan count
        // is what a leak looks like, and sixty scans adding at most a handful of blocks is not that.
        Assert.True(
            secondParked - firstParked <= ParkedGrowthCeiling,
            $"the shared pool retained {secondParked - firstParked} more blocks over {Between} scans.\n{report}");
    }

    private static long Live()
    {
        GC.Collect(2, GCCollectionMode.Forced, blocking: true);
        GC.WaitForPendingFinalizers();
        return GC.GetTotalMemory(forceFullCollection: true);
    }

    /// <summary>Blocks the shared pool is holding across the size classes a scan touches.</summary>
    private static int Parked()
    {
        int total = 0;
        for (int size = 4096; size <= 1 << 22; size <<= 1)
        {
            total += AlignedBufferPool.Shared.ParkedCount(size);
        }

        return total;
    }

    private static async Task<long> Scan(string path)
    {
        await using VortexFile file = await VortexFile.OpenAsync(path, CancellationToken.None);
        long rows = 0;
        await foreach (RecordBatch batch in file.Scan().ExecuteAsync()
            .WithCancellation(CancellationToken.None))
        {
            rows += batch.RowCount;
        }

        return rows;
    }
}
