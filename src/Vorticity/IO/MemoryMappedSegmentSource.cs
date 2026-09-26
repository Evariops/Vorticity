using System;
using System.IO;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using Vorticity.Buffers;
using Vorticity.Serialization.Schemas;

namespace Vorticity.IO;

/// <summary>
/// A zero-copy source over a memory-mapped local file: every lease is a pointer into the mapping.
/// </summary>
/// <remarks>
/// <para>
/// The whole file is mapped once at construction and every segment is a pointer into it. There is
/// no I/O on the read path at all, so a batch of ranges is not coalesced: a run is only ever a way
/// to turn several small reads into one, and there are no reads.
/// </para>
/// <para>
/// <b>Alignment.</b> A mapping base is page-aligned, so a segment at file offset <c>o</c> sits at
/// an address congruent to <c>o</c> modulo the page size; a writer that aligned <c>o</c> to
/// <c>2^k</c> with <c>k ≤ 6</c> therefore yields an address aligned to <c>2^k</c>. The tail read of
/// an open is the one request that can ask for an alignment the file offset does not satisfy, and
/// it copies in exactly that case.
/// </para>
/// </remarks>
public sealed class MemoryMappedSegmentSource : ISegmentSource, ISegmentReader
{
    private readonly MappedFileOwner? _mapping;
    private readonly SafeFileHandle? _emptyFileHandle;
    private int _disposed;

    private MemoryMappedSegmentSource(MappedFileOwner? mapping, SafeFileHandle? emptyFileHandle, long length)
    {
        _mapping = mapping;
        _emptyFileHandle = emptyFileHandle;
        Length = length;
    }

    /// <summary>Opens <paramref name="path"/> read-only and maps it whole.</summary>
    /// <param name="path">A local file path.</param>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
    /// <exception cref="IOException">The file could not be opened or mapped.</exception>
    public MemoryMappedSegmentSource(string path)
        : this(Open(path))
    {
    }

    private MemoryMappedSegmentSource(MemoryMappedSegmentSource opened)
        : this(opened._mapping, opened._emptyFileHandle, opened.Length)
    {
    }

    /// <summary>The file length in bytes.</summary>
    public long Length { get; }

    /// <summary>Opens <paramref name="path"/> read-only and maps it.</summary>
    /// <param name="path">A local file path.</param>
    /// <returns>A source over the whole file.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
    /// <exception cref="IOException">The file could not be opened or mapped.</exception>
    internal static MemoryMappedSegmentSource Open(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        // System.IO.File spelled out: this library has a `Vorticity.File` type of its own, and it
        // shadows a bare `File` everywhere in the assembly because namespace lookup beats a using.
        // Delete: on Windows as on Unix, a file being read can be deleted or replaced.
        SafeFileHandle handle = System.IO.File.OpenHandle(
            path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, FileOptions.None);

        try
        {
            return Create(handle, RandomAccess.GetLength(handle), ownsHandle: true);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    /// <inheritdoc/>
    public ValueTask<SegmentLease> ReadAsync(SegmentRange range, CancellationToken cancellationToken) =>
        SegmentLeases.ReadAsync(this, range, cancellationToken);

    /// <inheritdoc/>
    public ValueTask ReadAsync(ReadOnlyMemory<SegmentRange> ranges, Memory<SegmentLease> leases, CancellationToken cancellationToken) =>
        SegmentLeases.ReadAsync(this, ranges, leases, cancellationToken);

    /// <summary>Maps an already-open file handle.</summary>
    /// <param name="handle">A readable file handle.</param>
    /// <param name="length">The file length in bytes.</param>
    /// <param name="ownsHandle">
    /// When true, <see cref="DisposeAsync"/> disposes <paramref name="handle"/> — but only once
    /// the mapping's last reference is gone, so an outstanding batch can never be unmapped early.
    /// </param>
    /// <returns>A source over the whole file.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="handle"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="length"/> is negative.</exception>
    internal static MemoryMappedSegmentSource Create(SafeFileHandle handle, long length, bool ownsHandle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentOutOfRangeException.ThrowIfNegative(length);

        // A length larger than the file would map pages past the end, and touching those is a
        // SIGBUS rather than an exception. One extra stat at open is the price of never being
        // able to reach that.
        long actualLength = RandomAccess.GetLength(handle);
        if (length > actualLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(length), length, $"The file holds only {actualLength} bytes.");
        }

        if (length == 0)
        {
            // An empty file cannot be mapped on any platform, and there is nothing to map. The
            // source still answers GetLengthAsync and still rejects every non-empty read.
            return new MemoryMappedSegmentSource(null, ownsHandle ? handle : null, 0);
        }

        MappedFileOwner mapping = MappedFileOwner.Map(handle, length, ownsHandle);
        return new MemoryMappedSegmentSource(mapping, null, length);
    }

    ValueTask<long> ISegmentReader.GetLengthAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new ValueTask<long>(Length);
    }

    ValueTask<SegmentOwner> ISegmentReader.ReadAsync(SegmentSpec spec, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();

        SegmentIo.ValidateSpec(in spec, out long offset, out int length);
        SegmentIo.CheckInFile(offset, length, Length);

        if (length == 0)
        {
            return new ValueTask<SegmentOwner>(new EmptySegmentOwner());
        }

        MappedFileOwner mapping = AcquireMapping();
        try
        {
            VortexBuffer view = mapping.View(offset, length, spec.AlignmentExponent);
            return new ValueTask<SegmentOwner>(new SliceSegmentOwner(mapping, view));
        }
        finally
        {
            mapping.Release();
        }
    }

    ValueTask ISegmentReader.ReadManyAsync(SegmentRequestSet requests, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requests);
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();

        if (requests.IsPopulated)
        {
            return ValueTask.CompletedTask;
        }

        int count = requests.Count;
        if (count == 0)
        {
            requests.Complete();
            return ValueTask.CompletedTask;
        }

        MappedFileOwner? mapping = null;

        try
        {
            for (int slot = 0; slot < count; slot++)
            {
                if (requests.IsFilled(slot))
                {
                    continue;
                }

                SegmentSpec spec = requests.GetSpec(slot);
                SegmentIo.ValidateSpec(in spec, out long offset, out int length);
                SegmentIo.CheckInFile(offset, length, Length);

                // Taken lazily: a set of nothing but zero-length segments must not fail on a
                // source whose file is empty and therefore has no mapping at all.
                mapping ??= AcquireMapping();

                // One Retain per slot on the single mapping owner; no per-segment object, no copy.
                requests.SetSharedResult(
                    slot, mapping, mapping.View(offset, length, spec.AlignmentExponent));
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
            mapping?.Release();
        }

        return ValueTask.CompletedTask;
    }

    ValueTask<SegmentOwner> ISegmentReader.ReadRangeAsync(
        long offset, int length, int alignment, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();

        int actual = SegmentIo.ClampRange(offset, length, alignment, Length);
        if (actual == 0)
        {
            return new ValueTask<SegmentOwner>(new EmptySegmentOwner());
        }

        MappedFileOwner mapping = AcquireMapping();

        try
        {
            if (mapping.IsAlignedAt(offset, alignment))
            {
                VortexBuffer view = mapping.View(
                    offset, actual, BitOperations.TrailingZeroCount(alignment));
                return new ValueTask<SegmentOwner>(new SliceSegmentOwner(mapping, view));
            }

            // The tail read starts at `length - 65536`, which is aligned to nothing in particular.
            // One copy, once per file open, is the honest answer; the alternative is handing back
            // a buffer that lies about its base.
            NativeSegmentOwner copy = AlignedBufferPool.Shared.Rent(actual, alignment);
            try
            {
                mapping.CopyTo(offset, copy.WritableSpan);
            }
            catch
            {
                copy.Dispose();
                throw;
            }

            return new ValueTask<SegmentOwner>(copy);
        }
        finally
        {
            mapping.Release();
        }
    }

    bool ISegmentReader.ReadsInPlace => true;

    /// <summary>Drops the source's reference to the mapping; leases still held keep it alive until they are disposed.</summary>
    /// <returns>A completed task.</returns>
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return ValueTask.CompletedTask;
        }

        // Drops the creator's reference only. Slices handed to a live batch keep the mapping
        // alive until that batch releases them.
        _mapping?.Dispose();
        _emptyFileHandle?.Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Borrows the mapping for the duration of one operation.
    /// </summary>
    /// <remarks>
    /// <see cref="DisposeAsync"/> on another thread drops the source's reference, and
    /// <see cref="MappedFileOwner.CopyTo"/> dereferences the base pointer — so without a
    /// reference held across the operation a concurrent dispose is a use-after-unmap, which on
    /// every platform is a fault rather than an exception. <see cref="SegmentOwner.Retain"/> is
    /// atomic and refuses once the count has reached zero, so the race resolves as
    /// <see cref="ObjectDisposedException"/> instead.
    /// </remarks>
    private MappedFileOwner AcquireMapping()
    {
        MappedFileOwner? mapping = _mapping;
        if (mapping is null)
        {
            throw new ObjectDisposedException(
                nameof(MemoryMappedSegmentSource), "The file is empty and was never mapped.");
        }

        return (MappedFileOwner)mapping.Retain();
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(MemoryMappedSegmentSource));
        }
    }
}
