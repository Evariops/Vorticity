using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Vorticity.Serialization.Schemas;

namespace Vorticity.IO;

/// <summary>
/// Groups segment specs into the smallest set of contiguous file reads that still respects the
/// gap and size budgets in <see cref="SegmentReadOptions"/>.
/// </summary>
/// <remarks>
/// <para>
/// A reader registers every segment of a split before reading any of it, and this is what that
/// buys: nearby ranges become one read. On object storage it decides the performance of the whole
/// read path.
/// </para>
/// <para>
/// A class of its own because <see cref="FileSegmentSource"/> and the dataset's object source both
/// plan their reads with it, and because the alignment rule it enforces has to be testable on its
/// own rather than only through a file.
/// </para>
/// </remarks>
internal static class SegmentCoalescer
{
    /// <summary>
    /// Plans the reads for <paramref name="specs"/>.
    /// </summary>
    /// <param name="specs">
    /// The segments to cover, <b>sorted non-decreasing by <c>Offset</c></b>. The caller sorts;
    /// this method verifies, because an unsorted list would silently produce runs that do not
    /// contain the segments they claim to.
    /// </param>
    /// <param name="runs">
    /// Receives the plan. Must hold at least <c>specs.Length</c> entries: in the worst case every
    /// spec is its own run.
    /// </param>
    /// <param name="options">The gap and size budgets.</param>
    /// <returns>The number of runs written to <paramref name="runs"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="specs"/> is not sorted by offset, or <paramref name="runs"/> is too short.
    /// </exception>
    /// <exception cref="VortexFormatException">
    /// A spec is malformed: its alignment exponent exceeds the cap, its length exceeds
    /// <see cref="int"/> addressability, or its range overflows.
    /// </exception>
    public static int Plan(
        ReadOnlySpan<SegmentSpec> specs,
        Span<CoalescedRun> runs,
        in SegmentReadOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (specs.IsEmpty)
        {
            return 0;
        }

        if (runs.Length < specs.Length)
        {
            ThrowRunsTooShort(runs.Length, specs.Length);
        }

        int gapBudget = options.CoalesceGapBytes;
        long sizeBudget = options.MaxCoalescedReadBytes;

        int runCount = 0;
        int i = 0;

        while (i < specs.Length)
        {
            SegmentIo.ValidateSpec(in specs[i], out long firstOffset, out int firstLength);

            // The rule, and the only place it is written: round the run's start down to 64, so a
            // 64-aligned buffer base keeps every segment's own alignment.
            long start = firstOffset & ~((long)VortexLimits.MaxAlignment - 1);
            long end = firstOffset + firstLength;
            long prevOffset = firstOffset;

            int j = i + 1;
            while (j < specs.Length)
            {
                SegmentIo.ValidateSpec(in specs[j], out long nextOffset, out int nextLength);

                // Checked before the gap test, so the pair that ends this run is checked too and
                // every adjacent pair in `specs` is covered exactly once.
                if (nextOffset < prevOffset)
                {
                    ThrowUnsorted(j);
                }

                // A negative gap means the ranges overlap, which is legal and always coalesces.
                long gap = nextOffset - end;
                if (gap > gapBudget)
                {
                    break;
                }

                long nextEnd = nextOffset + nextLength;
                long candidateEnd = nextEnd > end ? nextEnd : end;

                // Checked before the merge, not after: `MaxCoalescedReadBytes` bounds an
                // allocation whose size comes from file content.
                if (candidateEnd - start > sizeBudget)
                {
                    break;
                }

                end = candidateEnd;
                prevOffset = nextOffset;
                j++;
            }

            long runLength = end - start;
            if (runLength > int.MaxValue)
            {
                ThrowRunTooLong(runLength);
            }

            runs[runCount++] = new CoalescedRun(start, (int)runLength, i, j - i);
            i = j;
        }

        return runCount;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    [DoesNotReturn]
    private static void ThrowUnsorted(int index) =>
        throw new ArgumentException(
            $"Segment specs must be sorted non-decreasing by offset; entry {index} goes backwards.",
            "specs");

    [MethodImpl(MethodImplOptions.NoInlining)]
    [DoesNotReturn]
    private static void ThrowRunsTooShort(int given, int needed) =>
        throw new ArgumentException(
            $"The run buffer holds {given} entries but the plan may need {needed}.",
            "runs");

    [MethodImpl(MethodImplOptions.NoInlining)]
    [DoesNotReturn]
    private static void ThrowRunTooLong(long length) =>
        throw new VortexFormatException(
            $"A coalesced read of {length} bytes exceeds the addressable buffer size.");
}
