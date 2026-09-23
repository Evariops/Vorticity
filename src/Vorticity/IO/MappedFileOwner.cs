using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Numerics;
using System.Runtime.CompilerServices;
using Microsoft.Win32.SafeHandles;
using Vorticity.Buffers;
using Vorticity.Serialization.Schemas;

namespace Vorticity.IO;

/// <summary>
/// A whole file mapped read-only, as a <see cref="SegmentOwner"/> so that every slice can refcount
/// it: the mapping lives until the last lease on it is released.
/// </summary>
/// <remarks>
/// <see cref="SegmentOwner.Buffer"/> stays <see cref="VortexBuffer.Empty"/>: a file may be larger
/// than <see cref="int.MaxValue"/> and a <see cref="VortexBuffer"/> cannot describe it. The base
/// pointer and the <see cref="long"/> length live here instead, and <see cref="View"/> is the only
/// way to obtain an addressable window.
/// </remarks>
internal sealed unsafe class MappedFileOwner : SegmentOwner
{
    private readonly MemoryMappedFile _file;
    private readonly MemoryMappedViewAccessor _view;
    private readonly SafeFileHandle? _ownedHandle;
    private readonly byte* _base;
    private readonly long _length;

    private MappedFileOwner(
        MemoryMappedFile file,
        MemoryMappedViewAccessor view,
        SafeFileHandle? ownedHandle,
        byte* basePointer,
        long length)
    {
        _file = file;
        _view = view;
        _ownedHandle = ownedHandle;
        _base = basePointer;
        _length = length;
    }

    /// <summary>Maps the first <paramref name="length"/> bytes of the file behind <paramref name="handle"/>.</summary>
    /// <param name="handle">A readable file handle.</param>
    /// <param name="length">The bytes to map; positive, and no more than the file holds.</param>
    /// <param name="ownsHandle">Whether the handle is disposed with the mapping's last lease.</param>
    internal static MappedFileOwner Map(SafeFileHandle handle, long length, bool ownsHandle)
    {
        // leaveOpen: true — the handle's lifetime is ours to manage, and it must outlive the
        // mapping on platforms where the mapping keeps a reference to it.
        MemoryMappedFile file = MemoryMappedFile.CreateFromFile(
            handle,
            mapName: null,
            capacity: 0,
            MemoryMappedFileAccess.Read,
            HandleInheritability.None,
            leaveOpen: true);

        MemoryMappedViewAccessor? view = null;
        bool acquired = false;

        try
        {
            view = file.CreateViewAccessor(0, length, MemoryMappedFileAccess.Read);

            byte* pointer = null;
            view.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);
            acquired = true;

            if (pointer is null)
            {
                throw new IOException("The memory mapping produced a null base address.");
            }

            return new MappedFileOwner(
                file,
                view,
                ownsHandle ? handle : null,
                pointer + view.PointerOffset,
                length);
        }
        catch
        {
            if (acquired)
            {
                view!.SafeMemoryMappedViewHandle.ReleasePointer();
            }

            view?.Dispose();
            file.Dispose();
            throw;
        }
    }

    /// <summary>One segment, as a slice of the mapping; the caller holds a reference across the call.</summary>
    /// <param name="spec">The segment.</param>
    /// <param name="fileLength">The file's length, which the segment must fit.</param>
    internal SegmentOwner Read(in SegmentSpec spec, long fileLength)
    {
        SegmentIo.ValidateSpec(in spec, out long offset, out int length);
        SegmentIo.CheckInFile(offset, length, fileLength);
        return length == 0
            ? new EmptySegmentOwner()
            : new SliceSegmentOwner(this, View(offset, length, spec.AlignmentExponent));
    }

    /// <summary>
    /// Every slot of <paramref name="requests"/> not yet filled, as a slice of the mapping: one
    /// reference per slot on this owner, no per-segment object and no copy. The caller holds a
    /// reference across the call and completes the set.
    /// </summary>
    /// <param name="requests">The batch's registered segments.</param>
    /// <param name="fileLength">The file's length, which every segment must fit.</param>
    internal void ReadMany(SegmentRequestSet requests, long fileLength)
    {
        int count = requests.Count;
        for (int slot = 0; slot < count; slot++)
        {
            if (requests.IsFilled(slot))
            {
                continue;
            }

            SegmentSpec spec = requests.GetSpec(slot);
            SegmentIo.ValidateSpec(in spec, out long offset, out int length);
            SegmentIo.CheckInFile(offset, length, fileLength);
            requests.SetSharedResult(slot, this, View(offset, length, spec.AlignmentExponent));
        }
    }

    /// <summary>
    /// <paramref name="length"/> bytes at <paramref name="offset"/>, clamped to the file, as a slice
    /// when the mapping lends the alignment asked and as an aligned copy otherwise; the caller holds
    /// a reference across the call.
    /// </summary>
    internal SegmentOwner ReadRange(long offset, int length, int alignment, long fileLength)
    {
        int actual = SegmentIo.ClampRange(offset, length, alignment, fileLength);
        if (actual == 0)
        {
            return new EmptySegmentOwner();
        }

        if (IsAlignedAt(offset, alignment))
        {
            return new SliceSegmentOwner(this, View(offset, actual, BitOperations.TrailingZeroCount(alignment)));
        }

        // The tail read starts at `length - 65536`, which is aligned to nothing in particular.
        // One copy, once per file open, is the honest answer; the alternative is handing back a
        // buffer that lies about its base.
        NativeSegmentOwner copy = AlignedBufferPool.Shared.Rent(actual, alignment);
        try
        {
            CopyTo(offset, copy.WritableSpan);
        }
        catch
        {
            copy.Dispose();
            throw;
        }

        return copy;
    }

    /// <summary>An addressable window over <c>[offset, offset + length)</c>.</summary>
    /// <param name="offset">A validated, in-file offset.</param>
    /// <param name="length">A validated length that does not escape the file.</param>
    /// <param name="alignmentExponent">The segment's declared alignment exponent.</param>
    internal VortexBuffer View(long offset, int length, int alignmentExponent)
    {
        // Re-checked here even though every caller validated: this is the one method that turns a
        // file-supplied number into an address, so it carries the memory-safety check.
        if ((ulong)offset > (ulong)_length || (ulong)length > (ulong)(_length - offset))
        {
            ThrowWindow(offset, length, _length);
        }

        return VortexBuffer.FromPointer(_base + offset, length, alignmentExponent);
    }

    internal bool IsAlignedAt(long offset, int alignment) =>
        ((nuint)(_base + offset) & (nuint)(alignment - 1)) == 0;

    internal void CopyTo(long offset, Span<byte> destination)
    {
        if ((ulong)offset > (ulong)_length ||
            (ulong)destination.Length > (ulong)(_length - offset))
        {
            ThrowWindow(offset, destination.Length, _length);
        }

        new ReadOnlySpan<byte>(_base + offset, destination.Length).CopyTo(destination);
    }

    protected override void FreeCore()
    {
        _view.SafeMemoryMappedViewHandle.ReleasePointer();
        _view.Dispose();
        _file.Dispose();
        _ownedHandle?.Dispose();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    [DoesNotReturn]
    private static void ThrowWindow(long offset, int length, long mappedLength) =>
        throw new VortexFormatException(
            $"Window [{offset}, {offset + (long)length}) escapes a mapping of {mappedLength} bytes.");
}
