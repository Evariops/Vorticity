using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace Vorticity.Buffers;

/// <summary>
/// A <see cref="SegmentOwner"/> over a <see cref="GC.AllocateArray{T}(int, bool)"/> pinned array,
/// for tests and small in-memory cases where a native allocation is not worth its bookkeeping.
/// </summary>
/// <remarks>
/// <para>
/// The pinned object heap guarantees the array never moves, so a raw pointer into it stays valid,
/// but it guarantees only pointer-size alignment. To honour an arbitrary
/// <c>alignment_exponent</c> the array is over-allocated by <c>alignment - 1</c> bytes and the
/// buffer starts at the first suitably aligned byte inside it.
/// </para>
/// <para>
/// Releasing drops the reference to the array; the GC reclaims it like any other object. There is
/// no finalizer because there is nothing native to leak.
/// </para>
/// </remarks>
internal sealed class PinnedArraySegmentOwner : SegmentOwner
{
    private byte[]? _array;
    private readonly int _offset;

    private unsafe PinnedArraySegmentOwner(byte[] array, int offset, int length, int alignmentExponent)
    {
        _array = array;
        _offset = offset;
        Buffer = VortexBuffer.FromPointer(
            (byte*)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(array)) + offset,
            length,
            alignmentExponent);
    }

    /// <summary>
    /// Allocates <paramref name="length"/> bytes aligned to <paramref name="alignment"/>.
    /// </summary>
    /// <param name="length">Length in bytes, non-negative.</param>
    /// <param name="alignment">A power of two in
    /// <c>[1, <see cref="VortexLimits.MaxAlignment"/>]</c>.</param>
    /// <returns>An owner with a reference count of 1.</returns>
    /// <exception cref="VortexFormatException">
    /// <paramref name="length"/> is negative or <paramref name="alignment"/> is out of range.
    /// </exception>
    /// <remarks>
    /// The bytes arrive zeroed because <see cref="GC.AllocateArray{T}(int, bool)"/> zeroes what it
    /// hands out. That is the allocator's behaviour and not this type's promise: the other two
    /// origins of a <see cref="SegmentOwner"/> hand back whatever the memory last held, the three
    /// are interchangeable to a caller, and reading before writing is a defect against all of them.
    /// </remarks>
    public static unsafe PinnedArraySegmentOwner Allocate(int length, int alignment)
    {
        int exponent = NativeSegmentOwner.CheckLengthAndAlignment(length, alignment);

        // The padded size is formed in long because length + alignment - 1 can exceed
        // int.MaxValue, and a wrapped size would be allocated instead of refused.
        long padded = (long)length + alignment - 1;
        if (padded > int.MaxValue)
        {
            ThrowTooLarge(length, alignment);
        }

        // A zero-length array has no addressable first element on every runtime; one spare byte
        // keeps the base address real so IsAligned and pointer identity remain meaningful.
        byte[] array = GC.AllocateArray<byte>(Math.Max((int)padded, 1), pinned: true);

        nint baseAddress = (nint)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(array));
        int offset = (int)(Alignment.AlignUp(baseAddress, alignment) - baseAddress);

        return new PinnedArraySegmentOwner(array, offset, length, exponent);
    }

    /// <summary>
    /// Allocates an aligned block and copies <paramref name="data"/> into it.
    /// </summary>
    /// <param name="data">The bytes to copy.</param>
    /// <param name="alignment">A power of two in
    /// <c>[1, <see cref="VortexLimits.MaxAlignment"/>]</c>.</param>
    /// <returns>An owner with a reference count of 1.</returns>
    /// <exception cref="VortexFormatException"><paramref name="alignment"/> is out of range.</exception>
    public static PinnedArraySegmentOwner CopyOf(ReadOnlySpan<byte> data, int alignment)
    {
        PinnedArraySegmentOwner owner = Allocate(data.Length, alignment);
        data.CopyTo(owner.WritableSpan);
        return owner;
    }

    /// <summary>
    /// A writable view over the owned bytes. Valid until the owner is released.
    /// </summary>
    /// <exception cref="ObjectDisposedException">The owner has already been released.</exception>
    public Span<byte> WritableSpan
    {
        get
        {
            byte[]? array = Volatile.Read(ref _array);
            if (array is null)
            {
                ThrowFreed();
            }

            return new Span<byte>(array, _offset, Length);
        }
    }

    /// <inheritdoc/>
    protected override void FreeCore() => Volatile.Write(ref _array, null);

    [MethodImpl(MethodImplOptions.NoInlining)]
    [DoesNotReturn]
    private static void ThrowTooLarge(int length, int alignment) =>
        throw new VortexFormatException(
            $"A {length}-byte segment aligned to {alignment} does not fit in a single managed " +
            "array.");

    [MethodImpl(MethodImplOptions.NoInlining)]
    [DoesNotReturn]
    private static void ThrowFreed() =>
        throw new ObjectDisposedException(
            nameof(PinnedArraySegmentOwner),
            "The pinned array has been released; its memory is no longer owned.");
}
