using System;
using System.Buffers;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Buffers;
using Vorticity.Serialization.Schemas;

namespace Vorticity.IO;

/// <summary>A source over bytes the caller already holds: a file read into memory, a buffer received whole.</summary>
/// <remarks>
/// <para>
/// Every lease is a view, and a view honours the alignment its file declares for the segment, which a
/// decoder that reads in place relies on. So the source keeps its bytes on a 64-byte boundary, the
/// widest a segment declares: bytes that already lie on one are pinned where they are, for the
/// source's life; bytes that do not, which is where a <c>byte[]</c> puts them, are copied once, when
/// the source is made, into memory it owns. A copy per read instead would copy the file on every
/// scan of it.
/// </para>
/// <para>
/// The caller keeps the memory valid until the source and every lease from it are disposed. To spare
/// the one copy, hand the source memory on a 64-byte boundary, as <see cref="AlignedMemoryPool"/>
/// rents it.
/// </para>
/// </remarks>
public sealed class MemorySegmentSource : ISegmentSource, ISegmentReader
{
    private readonly long _length;
    private readonly SegmentOwner _owner;
    private readonly unsafe byte* _base;
    private int _disposed;

    /// <summary>A source over <paramref name="bytes"/>.</summary>
    /// <param name="bytes">The whole file.</param>
    public unsafe MemorySegmentSource(ReadOnlyMemory<byte> bytes)
    {
        _length = bytes.Length;
        MemoryHandle pin = bytes.Pin();
        if (((nuint)pin.Pointer & (VortexLimits.MaxAlignment - 1)) == 0)
        {
            _owner = new PinnedOwner(pin);
            _base = (byte*)pin.Pointer;
            return;
        }

        try
        {
            PinnedArraySegmentOwner copy = PinnedArraySegmentOwner.CopyOf(bytes.Span, VortexLimits.MaxAlignment);
            _owner = copy;
            _base = (byte*)Unsafe.AsPointer(ref MemoryMarshal.GetReference(copy.WritableSpan));
        }
        finally
        {
            pin.Dispose();
        }
    }

    /// <inheritdoc/>
    public long Length => _length;

    /// <inheritdoc/>
    public ValueTask<SegmentLease> ReadAsync(SegmentRange range, CancellationToken cancellationToken) =>
        SegmentLeases.ReadAsync(this, range, cancellationToken);

    /// <inheritdoc/>
    public ValueTask ReadAsync(ReadOnlyMemory<SegmentRange> ranges, Memory<SegmentLease> leases, CancellationToken cancellationToken) =>
        SegmentLeases.ReadAsync(this, ranges, leases, cancellationToken);

    ValueTask<long> ISegmentReader.GetLengthAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new ValueTask<long>(_length);
    }

    ValueTask<SegmentOwner> ISegmentReader.ReadAsync(SegmentSpec spec, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SegmentIo.ValidateSpec(in spec, out long offset, out int length);
        SegmentIo.CheckInFile(offset, length, _length);
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
                SegmentIo.CheckInFile(offset, length, _length);
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
        int available = SegmentIo.ClampRange(offset, length, alignment, _length);
        return new ValueTask<SegmentOwner>(View(offset, available, alignment));
    }

    /// <summary>Releases the bytes once the last lease is disposed.</summary>
    /// <returns>A completed task.</returns>
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _owner.Dispose();
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// A view of the segment where it lies, or a copy of it when the file itself lays it off the
    /// boundary it declares, which a well-formed file does only for the tail an open reads.
    /// </summary>
    private unsafe SegmentOwner View(long offset, int length, int alignment)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (length == 0)
        {
            return new EmptySegmentOwner();
        }

        byte* at = _base + offset;
        if (((nuint)at & (nuint)(alignment - 1)) != 0)
        {
            return PinnedArraySegmentOwner.CopyOf(new ReadOnlySpan<byte>(at, length), alignment);
        }

        return new SliceSegmentOwner(_owner, VortexBuffer.FromPointer(at, length, BitOperations.TrailingZeroCount(alignment)));
    }

    /// <summary>The pin on the caller's bytes, released when the source and every view of it are gone.</summary>
    private sealed class PinnedOwner(MemoryHandle handle) : SegmentOwner
    {
        private MemoryHandle _handle = handle;

        protected override void FreeCore() => _handle.Dispose();
    }
}
