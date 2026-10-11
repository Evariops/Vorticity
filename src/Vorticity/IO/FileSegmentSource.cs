using System;
using System.Buffers;
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
/// A source that reads a local file through <see cref="RandomAccess"/> into aligned native buffers,
/// coalescing the ranges of a batch into as few positional reads as their gaps allow.
/// </summary>
/// <remarks>
/// <para>
/// A run is read into one 64-byte-aligned native buffer and its segments are published as views
/// into it, as <see cref="CoalescedReads"/> describes.
/// </para>
/// <para>
/// <b>Thread safety.</b> Positional reads carry their own offsets and share no file position, so
/// concurrent calls on one instance are safe. The source itself holds no mutable state.
/// </para>
/// </remarks>
public sealed class FileSegmentSource : ISegmentSource, ISegmentReader
{
    private readonly SafeFileHandle _handle;
    private readonly bool _ownsHandle;
    private readonly SegmentReadOptions _options;
    private int _disposed;
    private int _slicedRuns;
    private int _copiedRuns;

    /// <summary>Opens <paramref name="path"/> read-only for positional reads.</summary>
    /// <param name="path">A local file path.</param>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
    /// <exception cref="IOException">The file could not be opened.</exception>
    public FileSegmentSource(string path)
    {
        SafeFileHandle handle = OpenHandle(path);
        try
        {
            Length = RandomAccess.GetLength(handle);
        }
        catch
        {
            handle.Dispose();
            throw;
        }

        _handle = handle;
        _ownsHandle = true;
        _options = SegmentReadOptions.Default;
    }

    /// <summary>Reads through an already-open handle.</summary>
    /// <param name="handle">A readable file handle; positional reads are used exclusively.</param>
    /// <param name="ownsHandle">Whether <see cref="DisposeAsync"/> disposes the handle.</param>
    /// <exception cref="ArgumentNullException"><paramref name="handle"/> is null.</exception>
    public FileSegmentSource(SafeFileHandle handle, bool ownsHandle = false)
        : this(handle, ownsHandle, SegmentReadOptions.Default)
    {
    }

    /// <summary>Wraps an already-open file handle.</summary>
    /// <param name="handle">A readable file handle. Positional reads are used exclusively.</param>
    /// <param name="ownsHandle">Whether <see cref="DisposeAsync"/> disposes the handle.</param>
    /// <param name="options">Coalescing and pooling budgets.</param>
    /// <exception cref="ArgumentNullException"><paramref name="handle"/> or <paramref name="options"/> is null.</exception>
    internal FileSegmentSource(SafeFileHandle handle, bool ownsHandle, SegmentReadOptions options)
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

    /// <summary>Opens <paramref name="path"/> read-only for asynchronous positional reads.</summary>
    /// <param name="path">A local file path.</param>
    /// <returns>A source over the whole file, with <see cref="SegmentReadOptions.Default"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
    internal static FileSegmentSource Open(string path) => new FileSegmentSource(path);

    /// <summary>Wraps an already-open handle with the default options.</summary>
    /// <param name="handle">A readable file handle.</param>
    /// <param name="ownsHandle">Whether <see cref="DisposeAsync"/> disposes the handle.</param>
    /// <returns>A source over the whole file.</returns>
    internal static FileSegmentSource Create(SafeFileHandle handle, bool ownsHandle) =>
        new FileSegmentSource(handle, ownsHandle, SegmentReadOptions.Default);

    private static SafeFileHandle OpenHandle(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        // System.IO.File spelled out: this library has a `Vorticity.File` namespace of its own,
        // and it shadows a bare `File` everywhere in the assembly because namespace lookup beats a using.
        return System.IO.File.OpenHandle(
            path,
            FileMode.Open,
            FileAccess.Read,
            // Delete: on Windows as on Unix, a file being read can be deleted or replaced.
            FileShare.Read | FileShare.Delete,
            // Asynchronous is what makes RandomAccess.ReadAsync genuinely overlapped on Windows;
            // RandomAccess is what makes it a positional, thread-safe read everywhere.
            FileOptions.Asynchronous | FileOptions.RandomAccess);
    }

    /// <inheritdoc/>
    public ValueTask<SegmentLease> ReadAsync(SegmentRange range, CancellationToken cancellationToken) =>
        SegmentLeases.ReadAsync(this, range, cancellationToken);

    /// <inheritdoc/>
    public ValueTask ReadAsync(ReadOnlyMemory<SegmentRange> ranges, Memory<SegmentLease> leases, CancellationToken cancellationToken) =>
        SegmentLeases.ReadAsync(this, ranges, leases, cancellationToken);

    ValueTask<long> ISegmentReader.GetLengthAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new ValueTask<long>(Length);
    }

    async ValueTask<SegmentOwner> ISegmentReader.ReadAsync(SegmentSpec spec, CancellationToken cancellationToken)
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
        NativeSegmentOwner owner = CoalescedReads.RentBlock(length, _options);
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
    /// <para>
    /// The runs are read one after another, rather than several in flight behind a bounded depth
    /// as <see cref="ObjectStoreSource"/> does. A depth pays when a call issues dozens of small
    /// positional reads, and it buys nothing at the scale a scan works at, because a wide file's
    /// columns are adjacent: the coalescer hands this method one run for a whole split of
    /// registered segments, and a depth has nothing to hold.
    /// </para>
    /// <para>
    /// The case that reasoning does not cover is a genuinely cold file, whose pages cannot be
    /// evicted per file without privileges.
    /// </para>
    /// </remarks>
    async ValueTask ISegmentReader.ReadManyAsync(SegmentRequestSet requests, CancellationToken cancellationToken)
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
            int pending = CoalescedReads.CollectPending(requests, order, keys);
            if (pending == 0)
            {
                requests.Complete();
                return;
            }

            int runCount = CoalescedReads.PlanRuns(requests, order, keys, sorted, runs, pending, Length, _options);

            for (int r = 0; r < runCount; r++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                CoalescedRun run = runs[r];
                NativeSegmentOwner block = CoalescedReads.RentBlock(run.Length, _options);

                try
                {
                    await ReadExactAsync(manager, block, 0, run.Length, run.Start, cancellationToken)
                        .ConfigureAwait(false);

                    if (CoalescedReads.Publish(requests, block, in run, sorted, order, _options))
                    {
                        Interlocked.Increment(ref _slicedRuns);
                    }
                    else
                    {
                        Interlocked.Increment(ref _copiedRuns);
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

    async ValueTask<SegmentOwner> ISegmentReader.ReadRangeAsync(
        long offset, int length, int alignment, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();

        int actual = SegmentIo.ClampRange(offset, length, alignment, Length);
        if (actual == 0)
        {
            return new EmptySegmentOwner();
        }

        NativeSegmentOwner owner = CoalescedReads.RentBlock(actual, _options);
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

    /// <summary>Closes the handle when the source owns it; leases already handed out stay valid.</summary>
    /// <returns>A completed task.</returns>
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

    // ---- I/O ----------------------------------------------------------------------------------

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
            throw new ObjectDisposedException(nameof(FileSegmentSource));
        }
    }

}
