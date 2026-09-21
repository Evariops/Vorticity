using System;
using System.Buffers;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Buffers;
using Vorticity.Serialization.Schemas;

namespace Vorticity.IO;

/// <summary>A source over bytes the caller already holds: a file read into memory, a buffer received whole.</summary>
/// <remarks>
/// The bytes are pinned once, for the source's life, and every lease is a view into them; a segment
/// whose address does not honour the alignment its file declares is the one case that is copied.
/// The caller keeps the memory valid until the source and every lease from it are disposed.
/// </remarks>
public sealed class MemorySegmentSource : ISegmentSource, ISegmentReader
{
    private readonly ReadOnlyMemory<byte> _bytes;
    private readonly PinnedOwner _pinned;
    private int _disposed;

    /// <summary>A source over <paramref name="bytes"/>.</summary>
    /// <param name="bytes">The whole file.</param>
    public MemorySegmentSource(ReadOnlyMemory<byte> bytes)
    {
        _bytes = bytes;
        _pinned = new PinnedOwner(bytes);
    }

    /// <inheritdoc/>
    public long Length => _bytes.Length;

    /// <inheritdoc/>
    public ValueTask<SegmentLease> ReadAsync(SegmentRange range, CancellationToken cancellationToken) =>
        SegmentLeases.ReadAsync(this, range, cancellationToken);

    /// <inheritdoc/>
    public ValueTask ReadAsync(ReadOnlyMemory<SegmentRange> ranges, Memory<SegmentLease> leases, CancellationToken cancellationToken) =>
        SegmentLeases.ReadAsync(this, ranges, leases, cancellationToken);

    ValueTask<long> ISegmentReader.GetLengthAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new ValueTask<long>(_bytes.Length);
    }

    ValueTask<SegmentOwner> ISegmentReader.ReadAsync(SegmentSpec spec, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SegmentIo.ValidateSpec(in spec, out long offset, out int length);
        SegmentIo.CheckInFile(offset, length, _bytes.Length);
        return new ValueTask<SegmentOwner>(View(offset, length, 1 << spec.AlignmentExponent));
    }

    ValueTask ISegmentReader.ReadManyAsync(SegmentRequestSet requests, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requests);
        cancellationToken.ThrowIfCancellationRequested();
        if (requests.IsPopulated)
        {
            return ValueTask.CompletedTask;
        }

        try
        {
            for (int slot = 0; slot < requests.Count; slot++)
            {
                if (requests.IsFilled(slot))
                {
                    continue;
                }

                SegmentSpec spec = requests.GetSpec(slot);
                SegmentIo.ValidateSpec(in spec, out long offset, out int length);
                SegmentIo.CheckInFile(offset, length, _bytes.Length);
                requests.SetResult(slot, View(offset, length, 1 << spec.AlignmentExponent));
            }

            requests.Complete();
        }
        catch
        {
            requests.AbandonPending();
            throw;
        }

        return ValueTask.CompletedTask;
    }

    ValueTask<SegmentOwner> ISegmentReader.ReadRangeAsync(long offset, int length, int alignment, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // A tail read asks for more than the file holds by design: the open path reads 64 KiB
        // whatever the file's size, so a short answer is the normal case and not an error.
        int available = SegmentIo.ClampRange(offset, length, alignment, _bytes.Length);
        return new ValueTask<SegmentOwner>(View(offset, available, alignment));
    }

    /// <summary>Unpins the bytes once the last lease is disposed.</summary>
    /// <returns>A completed task.</returns>
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _pinned.Dispose();
        }

        return ValueTask.CompletedTask;
    }

    private SegmentOwner View(long offset, int length, int alignment)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (length == 0)
        {
            return new EmptySegmentOwner();
        }

        ReadOnlySpan<byte> bytes = _bytes.Span.Slice((int)offset, length);
        if (!_pinned.IsAligned(offset, alignment))
        {
            return PinnedArraySegmentOwner.CopyOf(bytes, alignment);
        }

        return new SliceSegmentOwner(_pinned, _pinned.View(offset, length, alignment));
    }

    /// <summary>The pin, released when the source and every view of it are gone.</summary>
    private sealed unsafe class PinnedOwner : SegmentOwner
    {
        private MemoryHandle _handle;
        private readonly byte* _base;

        internal PinnedOwner(ReadOnlyMemory<byte> bytes)
        {
            _handle = bytes.Pin();
            _base = (byte*)_handle.Pointer;
        }

        internal bool IsAligned(long offset, int alignment) =>
            ((nuint)(_base + offset) & (nuint)(alignment - 1)) == 0;

        internal VortexBuffer View(long offset, int length, int alignment) =>
            VortexBuffer.FromPointer(_base + offset, length, System.Numerics.BitOperations.TrailingZeroCount(alignment));

        protected override void FreeCore() => _handle.Dispose();
    }
}
