// Hand-written FlatBuffers primitive reads. Zero dependency is a founding constraint
// (docs/03-architecture.md §1), and FlatBuffers reading is pure offset arithmetic, so the whole
// runtime is this file plus two accessors.
//
// Parser-safety rule (docs/03-architecture.md §6): "Every offset/length read from the file is
// validated against the real size BEFORE any access." Every method here therefore checks the
// requested range against the span it was handed and throws VortexFormatException rather than
// letting the CLR raise IndexOutOfRangeException or returning a wrong value.
//
// Little-endianness is guaranteed by the module initializer in VortexRuntimeChecks, so the reads
// below are direct unaligned loads with no byte swapping (docs/09-contracts.md §7).
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Vorticity.Serialization.FlatBuffers;

/// <summary>
/// Bounds-checked little-endian primitive reads over a FlatBuffer.
/// </summary>
/// <remarks>
/// Every method throws <see cref="VortexFormatException"/> rather than returning a wrong value
/// when the read escapes the span. The bounds test is written as
/// <c>pos &lt; 0 || pos &gt; length - width</c> and never as <c>pos + width &gt; length</c>:
/// the latter wraps for a hostile <c>pos</c> close to <see cref="int.MaxValue"/>.
/// </remarks>
internal static class FlatBufferAccess
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static byte ReadUInt8(ReadOnlySpan<byte> b, int pos)
    {
        if (pos < 0 || pos > b.Length - sizeof(byte))
        {
            ThrowRead(pos, sizeof(byte), b.Length);
        }

        return Unsafe.Add(ref MemoryMarshal.GetReference(b), (nint)(uint)pos);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ushort ReadUInt16(ReadOnlySpan<byte> b, int pos)
    {
        if (pos < 0 || pos > b.Length - sizeof(ushort))
        {
            ThrowRead(pos, sizeof(ushort), b.Length);
        }

        return Unsafe.ReadUnaligned<ushort>(
            ref Unsafe.Add(ref MemoryMarshal.GetReference(b), (nint)(uint)pos));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static uint ReadUInt32(ReadOnlySpan<byte> b, int pos)
    {
        if (pos < 0 || pos > b.Length - sizeof(uint))
        {
            ThrowRead(pos, sizeof(uint), b.Length);
        }

        return Unsafe.ReadUnaligned<uint>(
            ref Unsafe.Add(ref MemoryMarshal.GetReference(b), (nint)(uint)pos));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ulong ReadUInt64(ReadOnlySpan<byte> b, int pos)
    {
        if (pos < 0 || pos > b.Length - sizeof(ulong))
        {
            ThrowRead(pos, sizeof(ulong), b.Length);
        }

        return Unsafe.ReadUnaligned<ulong>(
            ref Unsafe.Add(ref MemoryMarshal.GetReference(b), (nint)(uint)pos));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int ReadInt32(ReadOnlySpan<byte> b, int pos) => (int)ReadUInt32(b, pos);

    /// <summary>
    /// Validates that <paramref name="length"/> bytes starting at <paramref name="pos"/> lie inside
    /// <paramref name="b"/>. Both arguments are 64-bit so that a file-supplied <c>uint32</c> count
    /// multiplied by an element size cannot overflow before it is checked.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void CheckRange(ReadOnlySpan<byte> b, long pos, long length, string what)
    {
        if (pos < 0 || length < 0 || pos > b.Length - length)
        {
            ThrowOutOfBounds(what, pos, length, b.Length);
        }
    }

    /// <summary>
    /// Natural alignment of <typeparamref name="T"/>, computed without reflection.
    /// </summary>
    /// <remarks>
    /// <c>sizeof(struct { byte; T; }) - sizeof(T)</c> is exactly <c>alignof(T)</c>: the padded
    /// struct is <c>alignof(T) + sizeof(T)</c> whichever order the runtime chooses for the two
    /// fields, because <c>sizeof(T)</c> is always a whole multiple of <c>alignof(T)</c>.
    /// <c>Unsafe.SizeOf</c> keeps this AOT- and trim-safe (docs/03-architecture.md §1: no reflection).
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int AlignmentOf<T>() where T : unmanaged => AlignmentCache<T>.Value;

    private static class AlignmentCache<T> where T : unmanaged
    {
        internal static readonly int Value =
            Unsafe.SizeOf<AlignmentProbe<T>>() - Unsafe.SizeOf<T>();
    }

    private struct AlignmentProbe<T> where T : unmanaged
    {
#pragma warning disable CS0649 // never assigned: the probe exists only for its size
        internal byte Pad;
        internal T Value;
#pragma warning restore CS0649
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowRead(int pos, int width, int length) =>
        throw new VortexFormatException(
            $"FlatBuffers: a {width}-byte read at offset {pos} escapes the {length}-byte buffer.");

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowOutOfBounds(string what, long pos, long length, long limit) =>
        ThrowHelper.ThrowOutOfBounds("FlatBuffers " + what, pos, length, limit);
}
