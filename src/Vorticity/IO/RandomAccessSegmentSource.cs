// docs/03-architecture.md §3.5: "RandomAccessSegmentSource - RandomAccess.ReadAsync with vectored
// reads into aligned native buffers." See the class remarks for why one 64-aligned buffer per
// coalesced run beats a scatter list here: it delivers the same zero-copy segments with fewer
// allocations, and the alignment proof of PHASE1-CONTRACTS.md §5.4 is exactly what makes it work.
using System;
using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using Vorticity.Buffers;
using Vorticity.Serialization.Schemas;

namespace Vorticity.IO;

/// <summary>
/// An <see cref="ISegmentSource"/> that reads a local file through
/// <see cref="RandomAccess"/> into aligned native buffers.
/// </summary>
/// <remarks>
/// <para>
/// <b>How a coalesced run is delivered.</b> One 64-byte-aligned native buffer is allocated per
/// run, the run is read into it with a single positional read, and every segment in the run is
/// published as a <em>view</em> into that buffer — no per-segment copy. The doc's alternative, a
/// vectored read scattering into one buffer per segment plus throwaways for the gaps, transfers
/// the same bytes off the disk and would additionally need a <see cref="Memory{T}"/> (and so a
/// <see cref="System.Buffers.MemoryManager{T}"/>) per scatter entry. The run buffer is the cheaper
/// shape with the same zero-copy result.
/// </para>
/// <para>
/// <b>Why the run buffer is safe to slice.</b> Its base is 64-aligned and its start is the run's
/// start rounded down to 64, so a segment at file offset <c>o</c> lands at buffer offset
/// <c>o - start</c>, which keeps every factor of two up to 64 that <c>o</c> had —
/// PHASE1-CONTRACTS.md §5.4.
/// </para>
/// <para>
/// <b>Thread safety.</b> Positional reads carry their own offsets and share no file position, so
/// concurrent calls on one instance are safe. The source itself holds no mutable state.
/// </para>
/// </remarks>
public sealed class RandomAccessSegmentSource : ISegmentSource
{
    private readonly SafeFileHandle _handle;
    private readonly bool _ownsHandle;
    private readonly SegmentReadOptions _options;
    private int _disposed;
    private int _slicedRuns;
    private int _copiedRuns;
    private int _unpooledBlocks;

    /// <summary>
    /// A run at or below this size is always published by slicing, however sparse it is: holding
    /// 64 KiB is never worth a copy.
    /// </summary>
    private const int AlwaysSliceRunBytes = 64 * 1024;

    /// <summary>Wraps an already-open file handle.</summary>
    /// <param name="handle">A readable file handle. Positional reads are used exclusively.</param>
    /// <param name="ownsHandle">Whether <see cref="DisposeAsync"/> disposes the handle.</param>
    /// <param name="options">Coalescing and pooling budgets.</param>
    /// <exception cref="ArgumentNullException"><paramref name="handle"/> or <paramref name="options"/> is null.</exception>
    public RandomAccessSegmentSource(SafeFileHandle handle, bool ownsHandle, SegmentReadOptions options)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentNullException.ThrowIfNull(options);

        _handle = handle;
        _ownsHandle = ownsHandle;
        _options = options;
        Length = RandomAccess.GetLength(handle);
    }

    /// <summary>The file length in bytes, read once at construction.</summary>
    public long Length { get; }

    /// <summary>Diagnostics: runs published as zero-copy views into their run buffer.</summary>
    internal int SlicedRunCount => Volatile.Read(ref _slicedRuns);

    /// <summary>Diagnostics: runs whose segments were copied out so the run buffer could go back.</summary>
    internal int CopiedRunCount => Volatile.Read(ref _copiedRuns);

    /// <summary>Diagnostics: blocks too large for the pool, allocated and freed per read.</summary>
    /// <remarks>
    /// PERF-AUDIT-v2.md W-10. Two ceilings disagree by a factor of two --
    /// <see cref="SegmentReadOptions.DefaultMaxCoalescedReadBytes"/> lets a run reach 16 MiB while
    /// <see cref="SegmentReadOptions.DefaultMaxPooledBytes"/> and
    /// <see cref="AlignedBufferPool.Shared"/> both stop at 8 -- so a run in that band is a fresh
    /// native allocation and a free on every read. This counts how often that actually happens,
    /// which is the thing the point could not be decided without.
    /// </remarks>
    internal int UnpooledBlockCount => Volatile.Read(ref _unpooledBlocks);

    /// <summary>Opens <paramref name="path"/> read-only for asynchronous positional reads.</summary>
    /// <param name="path">A local file path.</param>
    /// <returns>A source over the whole file, with <see cref="SegmentReadOptions.Default"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
    public static RandomAccessSegmentSource Open(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        // System.IO.File spelled out: `Vorticity.File` (PHASE1-CONTRACTS.md §7) shadows a bare `File`
        // for every file in this assembly, because namespace lookup beats a using directive.
        SafeFileHandle handle = System.IO.File.OpenHandle(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            // Asynchronous is what makes RandomAccess.ReadAsync genuinely overlapped on Windows;
            // RandomAccess is what makes it a positional, thread-safe read everywhere.
            FileOptions.Asynchronous | FileOptions.RandomAccess);

        try
        {
            return new RandomAccessSegmentSource(handle, ownsHandle: true, SegmentReadOptions.Default);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    /// <summary>Wraps an already-open handle with the default options.</summary>
    /// <param name="handle">A readable file handle.</param>
    /// <param name="ownsHandle">Whether <see cref="DisposeAsync"/> disposes the handle.</param>
    /// <returns>A source over the whole file.</returns>
    public static RandomAccessSegmentSource Create(SafeFileHandle handle, bool ownsHandle) =>
        new RandomAccessSegmentSource(handle, ownsHandle, SegmentReadOptions.Default);

    /// <inheritdoc/>
    public ValueTask<long> GetLengthAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new ValueTask<long>(Length);
    }

    /// <inheritdoc/>
    public async ValueTask<SegmentOwner> ReadAsync(SegmentSpec spec, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();

        SegmentIo.ValidateSpec(in spec, out long offset, out int length);
        SegmentIo.CheckInFile(offset, length, Length);

        if (length == 0)
        {
            return new EmptySegmentOwner();
        }

        // A single segment starts at the buffer base, so no rounding is needed: a 64-aligned base
        // satisfies every legal exponent outright.
        NativeSegmentOwner owner = RentBlock(length);
        NativeMemoryManager manager = new NativeMemoryManager();

        try
        {
            await ReadExactAsync(manager, owner, 0, length, offset, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            owner.Dispose();
            throw;
        }
        finally
        {
            manager.Clear();
        }

        return owner;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// THE RUNS ARE READ IN TURN, AND ISSUING THEM TOGETHER WAS MEASURED AND DROPPED. The shape is
    /// the one <c>ObjectSegmentSource</c> uses, bounded by a depth, with a buffer projection per
    /// read in flight. Against sixty-four positional reads of 128 kB the depth is worth two and a
    /// half times -- 0,32 ms at one in flight against 0,13 at eight -- so the call-level physics
    /// says yes. At the scan level it says nothing: a full scan of a wide file reads
    /// 1 273, 1 284, 1 280 and 1 275 microseconds at depths of one, two, four and eight, and a
    /// projection of four columns spread across that file reads 352, 352, 347 and 351.
    /// <para>
    /// The reason is in the run count. A wide file's columns are adjacent, so the coalescer hands
    /// this method ONE run for fifty registered segments, and a depth has nothing to hold. What is
    /// left unmeasured is a genuinely cold read: the pages here are resident, and there is no way
    /// to evict them per file without being root.
    /// </para>
    /// </remarks>
    public async ValueTask ReadManyAsync(SegmentRequestSet requests, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requests);
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();

        if (requests.IsPopulated)
        {
            return;
        }

        int registered = requests.Count;
        if (registered == 0)
        {
            requests.Complete();
            return;
        }

        int[] order = ArrayPool<int>.Shared.Rent(registered);
        ulong[] keys = ArrayPool<ulong>.Shared.Rent(registered);
        SegmentSpec[] sorted = ArrayPool<SegmentSpec>.Shared.Rent(registered);
        CoalescedRun[] runs = ArrayPool<CoalescedRun>.Shared.Rent(registered);
        NativeMemoryManager manager = new NativeMemoryManager();

        try
        {
            int pending = CollectPending(requests, order, keys);
            if (pending == 0)
            {
                requests.Complete();
                return;
            }

            int runCount = PlanRuns(requests, order, keys, sorted, runs, pending, Length, _options);

            for (int r = 0; r < runCount; r++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                CoalescedRun run = runs[r];
                NativeSegmentOwner block = RentBlock(run.Length);

                try
                {
                    await ReadExactAsync(manager, block, 0, run.Length, run.Start, cancellationToken)
                        .ConfigureAwait(false);

                    if (ShouldSlice(in run, sorted))
                    {
                        Interlocked.Increment(ref _slicedRuns);
                        PublishRun(requests, block, in run, sorted, order);
                    }
                    else
                    {
                        Interlocked.Increment(ref _copiedRuns);
                        PublishRunByCopy(requests, block, in run, sorted, order, _options);
                    }
                }
                finally
                {
                    // Gives back this method's own reference. Each published slot took its own
                    // through SetSharedResult, so the block survives exactly as long as the batch.
                    block.Release();
                }
            }

            requests.Complete();
        }
        catch
        {
            requests.AbandonPending();
            throw;
        }
        finally
        {
            manager.Clear();
            ArrayPool<CoalescedRun>.Shared.Return(runs);
            ArrayPool<SegmentSpec>.Shared.Return(sorted);
            ArrayPool<ulong>.Shared.Return(keys);
            ArrayPool<int>.Shared.Return(order);
        }
    }

    /// <inheritdoc/>
    public async ValueTask<SegmentOwner> ReadRangeAsync(
        long offset, int length, int alignment, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();

        int actual = SegmentIo.ClampRange(offset, length, alignment, Length);
        if (actual == 0)
        {
            return new EmptySegmentOwner();
        }

        NativeSegmentOwner owner = RentBlock(actual);
        NativeMemoryManager manager = new NativeMemoryManager();

        try
        {
            await ReadExactAsync(manager, owner, 0, actual, offset, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            owner.Dispose();
            throw;
        }
        finally
        {
            manager.Clear();
        }

        return owner;
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return ValueTask.CompletedTask;
        }

        if (_ownsHandle)
        {
            _handle.Dispose();
        }

        return ValueTask.CompletedTask;
    }

    // ---- plan / publish, factored out so no ref struct local crosses an await ------------------

    /// <summary>Collects the slots that still need bytes, keyed by offset for the sort.</summary>
    private static int CollectPending(SegmentRequestSet requests, int[] order, ulong[] keys)
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
    private static int PlanRuns(
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
    /// Whether a run's segments should be published as views into its buffer, or copied out.
    /// </summary>
    /// <remarks>
    /// A view keeps the <em>whole</em> run buffer alive for as long as the batch holds any segment
    /// of it. That is free when the run is mostly useful bytes, and ruinous when it is not: runs
    /// are disjoint, so a split of one-byte segments spread just under
    /// <see cref="SegmentReadOptions.CoalesceGapBytes"/> apart would pin a run buffer for every
    /// gap it bridged and end up holding the whole file to deliver a few hundred bytes. Below
    /// half-useful the segments are copied into their own pooled buffers instead and the run
    /// buffer goes straight back — the same outcome as the vectored scatter that
    /// docs/03-architecture.md §3.5 describes, reached with one memcpy instead of a scatter list.
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
            NativeSegmentOwner owner = length <= options.MaxPooledBytes
                ? AlignedBufferPool.Shared.Rent(length, VortexLimits.MaxAlignment)
                : NativeSegmentOwner.Allocate(length, VortexLimits.MaxAlignment);

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

    // ---- I/O ----------------------------------------------------------------------------------

    private NativeSegmentOwner RentBlock(int length)
    {
        if (length <= _options.MaxPooledBytes)
        {
            return AlignedBufferPool.Shared.Rent(length, VortexLimits.MaxAlignment);
        }

        Interlocked.Increment(ref _unpooledBlocks);
        return NativeSegmentOwner.Allocate(length, VortexLimits.MaxAlignment);
    }

    /// <summary>
    /// Reads exactly <paramref name="length"/> bytes into <paramref name="owner"/> at
    /// <paramref name="destinationOffset"/>.
    /// </summary>
    /// <remarks>
    /// <see cref="RandomAccess.ReadAsync(SafeFileHandle, Memory{byte}, long, CancellationToken)"/>
    /// may legitimately return a short count for a reason other than end of file, so the loop
    /// continues until the buffer is full; a zero-byte return is the end of file and means the
    /// file is shorter than its own footer claims — a format error, not something to retry.
    /// </remarks>
    private async ValueTask ReadExactAsync(
        NativeMemoryManager manager,
        NativeSegmentOwner owner,
        int destinationOffset,
        int length,
        long fileOffset,
        CancellationToken cancellationToken)
    {
        PointBufferAt(manager, owner, destinationOffset, length);

        Memory<byte> destination = manager.Memory;
        int done = 0;

        while (done < length)
        {
            int read = await RandomAccess
                .ReadAsync(_handle, destination.Slice(done), fileOffset + done, cancellationToken)
                .ConfigureAwait(false);

            if (read <= 0)
            {
                SegmentIo.ThrowTruncatedRead(fileOffset, length, done);
            }

            done += read;
        }
    }

    /// <summary>Re-points <paramref name="manager"/> at a window of the owner's native block.</summary>
    private static unsafe void PointBufferAt(
        NativeMemoryManager manager, NativeSegmentOwner owner, int offset, int length)
    {
        Span<byte> block = owner.WritableSpan;
        if ((uint)offset > (uint)block.Length || (uint)length > (uint)(block.Length - offset))
        {
            throw new ArgumentOutOfRangeException(nameof(length), length, null);
        }

        manager.Reset(
            (byte*)Unsafe.AsPointer(ref MemoryMarshal.GetReference(block)) + offset,
            length);
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(RandomAccessSegmentSource));
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    [DoesNotReturn]
    private static void ThrowRunWindow(ulong offset, uint length, long runStart, int runLength) =>
        throw new VortexFormatException(
            $"Segment [{offset}, {offset + length}) does not lie inside the coalesced run " +
            $"[{runStart}, {runStart + runLength}).");
}
