using System;
using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Microsoft.Win32.SafeHandles;
using Vorticity.Buffers;
using Vorticity.Serialization.Schemas;

namespace Vorticity.IO;

/// <summary>
/// The reads of a batch of segments planned as coalesced runs: the pending slots sorted by offset,
/// each run read into one block and its segments published as views into it, or copied out of it
/// when the run is mostly gaps. Shared by the sources that read a file positionally.
/// </summary>
/// <remarks>
/// <para>
/// <b>How a coalesced run is delivered.</b> One 64-byte-aligned native buffer is taken per run,
/// the run is read into it with a single positional read, and every segment in the run is
/// published as a <em>view</em> into that buffer, with no per-segment copy. The alternative, a
/// vectored read scattering into one buffer per segment plus throwaways for the gaps, transfers
/// the same bytes off the disk and would additionally need a <see cref="Memory{T}"/> (and so a
/// <see cref="MemoryManager{T}"/>) per scatter entry. The run buffer is the cheaper shape with the
/// same zero-copy result.
/// </para>
/// <para>
/// <b>Why the run buffer is safe to slice.</b> Its base is 64-aligned and its start is the run's
/// start rounded down to 64, so a segment at file offset <c>o</c> lands at buffer offset
/// <c>o - start</c>, which keeps every factor of two up to 64 that <c>o</c> had.
/// </para>
/// </remarks>
internal static class CoalescedReads
{
    /// <summary>
    /// A run at or below this size is always published by slicing, however sparse it is: holding
    /// 64 KiB is never worth a copy.
    /// </summary>
    private const int AlwaysSliceRunBytes = 64 * 1024;

    /// <summary>
    /// Reads every slot of <paramref name="requests"/> not yet filled, run after run, with
    /// positional reads on the caller's thread; the caller completes the set, or abandons it when
    /// this throws.
    /// </summary>
    /// <param name="requests">The batch.</param>
    /// <param name="handle">A readable handle on the file.</param>
    /// <param name="fileLength">The file's length, which every segment must lie within.</param>
    /// <param name="options">The gap and size budgets the runs are planned with.</param>
    internal static void ReadMany(SegmentRequestSet requests, SafeFileHandle handle, long fileLength, SegmentReadOptions options)
    {
        int registered = requests.Count;
        if (registered == 0)
        {
            return;
        }

        int[] order = ArrayPool<int>.Shared.Rent(registered);
        ulong[] keys = ArrayPool<ulong>.Shared.Rent(registered);
        SegmentSpec[] sorted = ArrayPool<SegmentSpec>.Shared.Rent(registered);
        CoalescedRun[] runs = ArrayPool<CoalescedRun>.Shared.Rent(registered);
        try
        {
            int pending = CollectPending(requests, order, keys);
            int runCount = pending == 0 ? 0 : PlanRuns(requests, order, keys, sorted, runs, pending, fileLength, options);
            for (int r = 0; r < runCount; r++)
            {
                CoalescedRun run = runs[r];
                NativeSegmentOwner block = RentBlock(run.Length, options);
                try
                {
                    ReadExact(handle, block.WritableSpan[..run.Length], run.Start);
                    Publish(requests, block, in run, sorted, order, options);
                }
                finally
                {
                    // Gives back this method's own reference: each published slot took its own.
                    block.Release();
                }
            }
        }
        finally
        {
            ArrayPool<CoalescedRun>.Shared.Return(runs);
            ArrayPool<SegmentSpec>.Shared.Return(sorted);
            ArrayPool<ulong>.Shared.Return(keys);
            ArrayPool<int>.Shared.Return(order);
        }
    }

    /// <summary>Collects the slots that still need bytes, keyed by offset for the sort.</summary>
    internal static int CollectPending(SegmentRequestSet requests, int[] order, ulong[] keys)
    {
        int pending = 0;

        for (int slot = 0; slot < requests.Count; slot++)
        {
            if (requests.IsFilled(slot))
            {
                continue;
            }

            order[pending] = slot;
            keys[pending] = requests.GetSpec(slot).Offset;
            pending++;
        }

        return pending;
    }

    /// <summary>Sorts the pending specs by offset and plans the coalesced reads.</summary>
    internal static int PlanRuns(
        SegmentRequestSet requests,
        int[] order,
        ulong[] keys,
        SegmentSpec[] sorted,
        CoalescedRun[] runs,
        int pending,
        long fileLength,
        SegmentReadOptions options)
    {
        // In-place introsort over the (offset, slot) pairs: no allocation, and it is what lets the
        // coalescer assume a sorted list.
        keys.AsSpan(0, pending).Sort(order.AsSpan(0, pending));

        for (int i = 0; i < pending; i++)
        {
            sorted[i] = requests.GetSpec(order[i]);

            // Fail before any I/O: a spec that escapes the file must not cause a partial read that
            // then has to be unwound.
            SegmentIo.ValidateSpec(in sorted[i], out long offset, out int length);
            SegmentIo.CheckInFile(offset, length, fileLength);
        }

        return SegmentCoalescer.Plan(sorted.AsSpan(0, pending), runs.AsSpan(0, pending), options);
    }

    /// <summary>
    /// Publishes the segments of a run read into <paramref name="block"/>: as views into it, or
    /// copied into buffers of their own when the run is mostly gaps.
    /// </summary>
    /// <returns>Whether the segments were published as views.</returns>
    internal static bool Publish(
        SegmentRequestSet requests,
        NativeSegmentOwner block,
        in CoalescedRun run,
        SegmentSpec[] sorted,
        int[] order,
        SegmentReadOptions options)
    {
        if (ShouldSlice(in run, sorted))
        {
            PublishRun(requests, block, in run, sorted, order);
            return true;
        }

        PublishRunByCopy(requests, block, in run, sorted, order, options);
        return false;
    }

    /// <summary>
    /// A block of at least <paramref name="length"/> bytes, pooled up to the options' ceiling, and past
    /// the shared pool's classes by the one block it keeps beyond them.
    /// </summary>
    internal static NativeSegmentOwner RentBlock(int length, SegmentReadOptions options) =>
        length <= options.MaxPooledBytes || length > AlignedBufferPool.Shared.MaxPooledLength
            ? AlignedBufferPool.Shared.Rent(length, VortexLimits.MaxAlignment)
            : NativeSegmentOwner.Allocate(length, VortexLimits.MaxAlignment);

    /// <summary>
    /// Whether a run's segments should be published as views into its buffer, or copied out.
    /// </summary>
    /// <remarks>
    /// A view keeps the <em>whole</em> run buffer alive for as long as the batch holds any segment
    /// of it. That is free when the run is mostly useful bytes, and ruinous when it is not: runs
    /// are disjoint, so a split of one-byte segments spread just under
    /// <see cref="SegmentReadOptions.CoalesceGapBytes"/> apart would pin a run buffer for every
    /// gap it bridged and end up holding the whole file to deliver a few hundred bytes. Below
    /// half-useful the segments are copied into their own pooled buffers instead and the run
    /// buffer goes straight back: the same outcome a vectored scatter would give, reached with
    /// one memcpy instead of a scatter list.
    /// </remarks>
    private static bool ShouldSlice(in CoalescedRun run, SegmentSpec[] sorted)
    {
        if (run.Length <= AlwaysSliceRunBytes)
        {
            return true;
        }

        long useful = 0;
        for (int k = run.FirstIndex; k < run.FirstIndex + run.Count; k++)
        {
            useful += sorted[k].Length;
        }

        return useful * 2 >= run.Length;
    }

    /// <summary>Copies each segment of a sparse run into its own buffer, freeing the run buffer.</summary>
    private static void PublishRunByCopy(
        SegmentRequestSet requests,
        NativeSegmentOwner block,
        in CoalescedRun run,
        SegmentSpec[] sorted,
        int[] order,
        SegmentReadOptions options)
    {
        ReadOnlySpan<byte> bytes = block.Buffer.Span;

        for (int k = run.FirstIndex; k < run.FirstIndex + run.Count; k++)
        {
            SegmentSpec spec = sorted[k];
            long relative = (long)spec.Offset - run.Start;

            if (relative < 0 || relative + spec.Length > bytes.Length)
            {
                ThrowRunWindow(spec.Offset, spec.Length, run.Start, run.Length);
            }

            int length = (int)spec.Length;
            NativeSegmentOwner owner = RentBlock(length, options);

            try
            {
                bytes.Slice((int)relative, length).CopyTo(owner.WritableSpan);

                // Published with the segment's own declared exponent rather than the block's 64,
                // so the two publish paths describe an identical buffer.
                requests.SetSharedResult(
                    order[k],
                    owner,
                    VortexBuffer.FromPinned(owner.Buffer.Span, spec.AlignmentExponent));
            }
            finally
            {
                // SetSharedResult retained; this gives back the reference Rent handed us, leaving
                // the slot as the single owner.
                owner.Release();
            }
        }
    }

    /// <summary>Publishes every segment of a completed run as a zero-copy view into its block.</summary>
    private static void PublishRun(
        SegmentRequestSet requests,
        NativeSegmentOwner block,
        in CoalescedRun run,
        SegmentSpec[] sorted,
        int[] order)
    {
        ReadOnlySpan<byte> bytes = block.Buffer.Span;

        for (int k = run.FirstIndex; k < run.FirstIndex + run.Count; k++)
        {
            SegmentSpec spec = sorted[k];
            long relative = (long)spec.Offset - run.Start;

            // Both bounds were established by the planner; re-derived here because this is where
            // the pointer is formed.
            if (relative < 0 || relative + spec.Length > bytes.Length)
            {
                ThrowRunWindow(spec.Offset, spec.Length, run.Start, run.Length);
            }

            // FromPinned over native memory: nothing is pinned, and the segment keeps its own
            // declared exponent instead of inheriting the block's 64.
            VortexBuffer view = VortexBuffer.FromPinned(
                bytes.Slice((int)relative, (int)spec.Length), spec.AlignmentExponent);

            requests.SetSharedResult(order[k], block, view);
        }
    }

    /// <summary>Reads exactly the destination's bytes at <paramref name="offset"/>, on the caller's thread.</summary>
    private static void ReadExact(SafeFileHandle handle, Span<byte> destination, long offset)
    {
        int done = 0;
        while (done < destination.Length)
        {
            // A short count is legal and not the end; none at all is a file shorter than its
            // footer says.
            int read = NativeFile.Read(handle, destination[done..], offset + done);
            if (read <= 0)
            {
                SegmentIo.ThrowTruncatedRead(offset, destination.Length, done);
            }

            done += read;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    [DoesNotReturn]
    private static void ThrowRunWindow(ulong offset, uint length, long runStart, int runLength) =>
        throw new VortexFormatException(
            $"Segment [{offset}, {offset + length}) does not lie inside the coalesced run " +
            $"[{runStart}, {runStart + runLength}).");
}
