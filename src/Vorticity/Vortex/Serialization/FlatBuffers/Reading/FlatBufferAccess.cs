using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Vorticity.Serialization.FlatBuffers;

/// <summary>
/// Bounds-checked little-endian primitive reads over a FlatBuffer. Reading a FlatBuffer is pure
/// offset arithmetic, so it is hand-written here rather than taken as a dependency.
/// </summary>
/// <remarks>
/// Every offset and length read from a file is validated against the real size before any access,
/// so every method throws <see cref="VortexFormatException"/> rather than returning a wrong value
/// or letting the runtime raise an indexing error. The bounds test is written as
/// <c>pos &lt; 0 || pos &gt; length - width</c> and never as <c>pos + width &gt; length</c>:
/// the latter wraps for a hostile <c>pos</c> close to <see cref="int.MaxValue"/>. A little-endian
/// host is asserted once at startup, so the loads are direct and unaligned with no byte swapping.
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
    /// <c>Unsafe.SizeOf</c> keeps this AOT- and trim-safe, where reflection would not be.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int AlignmentOf<T>() where T : unmanaged => AlignmentCache<T>.Value;

    private static class AlignmentCache<T> where T : unmanaged
    {
        internal static readonly int Value =
            Unsafe.SizeOf<AlignmentProbe<T>>() - Unsafe.SizeOf<T>();
    }

    // Sequential and not auto: the packing of T has to survive into the probe, or the padding it
    // exposes is the natural alignment rather than the declared one. A Pack = 1 struct is exactly
    // the case the callers have -- the inline structs of the format are Pack = 1 -- and an
    // auto-laid-out probe reports 8 for them where the format requires 1.
    [StructLayout(LayoutKind.Sequential)]
    private struct AlignmentProbe<T> where T : unmanaged
    {
        internal byte Pad;
        internal T Value;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowRead(int pos, int width, int length) =>
        throw new VortexFormatException(
            $"FlatBuffers: a {width}-byte read at offset {pos} escapes the {length}-byte buffer.");

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowOutOfBounds(string what, long pos, long length, long limit) =>
        ThrowHelper.ThrowOutOfBounds("FlatBuffers " + what, pos, length, limit);
}
