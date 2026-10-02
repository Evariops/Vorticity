using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;
using Vorticity.Buffers;
using Vorticity.Serialization.Schemas;

namespace Vorticity.IO;

/// <summary>
/// A whole file mapped read-only, as a <see cref="SegmentOwner"/> so that every slice can refcount
/// it: the mapping lives until the last lease on it is released.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="SegmentOwner.Buffer"/> stays <see cref="VortexBuffer.Empty"/>: a file may be larger
/// than <see cref="int.MaxValue"/> and a <see cref="VortexBuffer"/> cannot describe it. The base
/// pointer and the <see cref="long"/> length live here instead, and <see cref="View"/> is the only
/// way to obtain an addressable window.
/// </para>
/// <para>
/// Unix maps with <c>mmap</c> called directly, <see cref="NativeFile.TryMap"/>, and Windows through
/// a view accessor. A finalizer unmaps the first if its owner is dropped without its last release,
/// as the accessor's handle does the second.
/// </para>
/// </remarks>
internal sealed unsafe class MappedFileOwner : SegmentOwner
{
    // Null for a mapping made with mmap.
    private readonly MemoryMappedViewAccessor? _view;
    private readonly SafeFileHandle? _ownedHandle;
    private readonly byte* _base;
    private readonly long _length;

    // What mmap returned, zero once unmapped: one exchange takes it, for a release or the finalizer.
    private nint _mapped;

    private static long s_finalizedMappings;

    private MappedFileOwner(
        MemoryMappedViewAccessor? view,
        SafeFileHandle? ownedHandle,
        byte* basePointer,
        long length)
    {
        _view = view;
        _ownedHandle = ownedHandle;
        _base = basePointer;
        _length = length;
        if (view is null)
        {
            _mapped = (nint)basePointer;
        }
        else
        {
            GC.SuppressFinalize(this);
        }
    }

    /// <summary>Unmaps a mapping made with mmap whose owner was dropped without its last release.</summary>
    ~MappedFileOwner()
    {
        if (Unmap())
        {
            Interlocked.Increment(ref s_finalizedMappings);
        }
    }

    /// <summary>
    /// How many mappings this process has unmapped from the finalizer rather than from a last
    /// release: none for a reader used correctly, and one more for each owner a test drops.
    /// </summary>
    internal static long FinalizedMappingCount => Interlocked.Read(ref s_finalizedMappings);

    /// <summary>Maps the first <paramref name="length"/> bytes of the file behind <paramref name="handle"/>.</summary>
    /// <param name="handle">A readable file handle.</param>
    /// <param name="length">The bytes to map; positive, and no more than the file holds.</param>
    /// <param name="ownsHandle">Whether the handle is disposed with the mapping's last lease.</param>
    internal static MappedFileOwner Map(SafeFileHandle handle, long length, bool ownsHandle)
    {
        if (NativeFile.TryMap(handle, length, out byte* mapped))
        {
            return new MappedFileOwner(view: null, ownsHandle ? handle : null, mapped, length);
        }

        // leaveOpen: true — the handle's lifetime is ours to manage. The file object holds a
        // reference on the handle for as long as it lives, and on Unix the handle holds the lock
        // FileShare asked for, so it is dropped as soon as the view exists: the view does not need
        // it, and a mapping kept past its reader must not keep a writer out.
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

            MappedFileOwner owner = new MappedFileOwner(
                view,
                ownsHandle ? handle : null,
                pointer + view.PointerOffset,
                length);
            file.Dispose();
            return owner;
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
        if (_view is null)
        {
            Unmap();
            GC.SuppressFinalize(this);
        }
        else
        {
            _view.SafeMemoryMappedViewHandle.ReleasePointer();
            _view.Dispose();
        }

        _ownedHandle?.Dispose();
    }

    private bool Unmap()
    {
        nint mapped = Interlocked.Exchange(ref _mapped, 0);
        if (mapped == 0)
        {
            return false;
        }

        NativeFile.Unmap((byte*)mapped, _length);
        return true;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    [DoesNotReturn]
    private static void ThrowWindow(long offset, int length, long mappedLength) =>
        throw new VortexFormatException(
            $"Window [{offset}, {offset + (long)length}) escapes a mapping of {mappedLength} bytes.");
}
