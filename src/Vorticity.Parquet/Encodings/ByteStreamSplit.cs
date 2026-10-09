using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

namespace Vorticity.Parquet.Encodings;

/// <summary>
/// BYTE_STREAM_SPLIT, read and written: <c>K</c> streams of <c>N</c> bytes for <c>N</c> values of
/// <c>K</c> bytes, the <c>k</c>-th byte of every value in the <c>k</c>-th stream, the streams back
/// to back.
/// </summary>
/// <remarks>
/// It changes no size: what it buys is a codec's ratio on floating point, whose exponent bytes
/// repeat where its mantissa's do not. Values of four and eight bytes go sixteen at a time through
/// byte interleaves where SSE2 or AdvSimd have them, a transpose of 16-byte blocks; any other width,
/// and what is left, a byte at a time.
/// </remarks>
internal static class ByteStreamSplit
{
    /// <summary>Gathers the streams of <paramref name="source"/> back into values of <paramref name="width"/> bytes.</summary>
    /// <exception cref="ParquetFormatException">The bytes are not a whole number of values.</exception>
    internal static void Decode(ReadOnlySpan<byte> source, int width, Span<byte> destination)
    {
        if (width <= 0 || source.Length % width != 0 || destination.Length < source.Length)
        {
            ParquetThrow.Format($"A BYTE_STREAM_SPLIT page of {source.Length} bytes is not a whole number of values of {width} bytes.");
        }

        int count = source.Length / width;
        int done = width switch
        {
            4 => Interleave4(source, destination, count),
            8 => Interleave8(source, destination, count),
            _ => 0,
        };

        for (int i = done; i < count; i++)
        {
            for (int k = 0; k < width; k++)
            {
                destination[(i * width) + k] = source[(k * count) + i];
            }
        }
    }

    /// <summary>Splits values of <paramref name="width"/> bytes into their streams.</summary>
    internal static void Encode(ReadOnlySpan<byte> values, int width, Span<byte> destination)
    {
        int count = values.Length / width;
        for (int i = 0; i < count; i++)
        {
            for (int k = 0; k < width; k++)
            {
                destination[(k * count) + i] = values[(i * width) + k];
            }
        }
    }

    /// <summary>Four streams into values of four bytes, sixteen values a step; the values done.</summary>
    private static int Interleave4(ReadOnlySpan<byte> source, Span<byte> destination, int count)
    {
        if (!Sse2.IsSupported && !AdvSimd.Arm64.IsSupported)
        {
            return 0;
        }

        ref byte from = ref MemoryMarshal.GetReference(source);
        ref byte into = ref MemoryMarshal.GetReference(destination);
        int i = 0;
        for (; i + 16 <= count; i += 16)
        {
            Vector128<byte> s0 = Vector128.LoadUnsafe(ref from, (nuint)i);
            Vector128<byte> s1 = Vector128.LoadUnsafe(ref from, (nuint)(count + i));
            Vector128<byte> s2 = Vector128.LoadUnsafe(ref from, (nuint)((2 * count) + i));
            Vector128<byte> s3 = Vector128.LoadUnsafe(ref from, (nuint)((3 * count) + i));

            // Bytes 0 and 1 of each value side by side, then 2 and 3, then the two pairs together.
            Vector128<byte> low01 = ZipLow(s0, s1);
            Vector128<byte> high01 = ZipHigh(s0, s1);
            Vector128<byte> low23 = ZipLow(s2, s3);
            Vector128<byte> high23 = ZipHigh(s2, s3);
            nuint at = (nuint)(i * 4);
            ZipLow(low01.AsUInt16(), low23.AsUInt16()).AsByte().StoreUnsafe(ref into, at);
            ZipHigh(low01.AsUInt16(), low23.AsUInt16()).AsByte().StoreUnsafe(ref into, at + 16);
            ZipLow(high01.AsUInt16(), high23.AsUInt16()).AsByte().StoreUnsafe(ref into, at + 32);
            ZipHigh(high01.AsUInt16(), high23.AsUInt16()).AsByte().StoreUnsafe(ref into, at + 48);
        }

        return i;
    }

    /// <summary>Eight streams into values of eight bytes, sixteen values a step; the values done.</summary>
    private static int Interleave8(ReadOnlySpan<byte> source, Span<byte> destination, int count)
    {
        if (!Sse2.IsSupported && !AdvSimd.Arm64.IsSupported)
        {
            return 0;
        }

        ref byte from = ref MemoryMarshal.GetReference(source);
        ref byte into = ref MemoryMarshal.GetReference(destination);
        int i = 0;
        for (; i + 16 <= count; i += 16)
        {
            Vector128<byte> s0 = Vector128.LoadUnsafe(ref from, (nuint)i);
            Vector128<byte> s1 = Vector128.LoadUnsafe(ref from, (nuint)(count + i));
            Vector128<byte> s2 = Vector128.LoadUnsafe(ref from, (nuint)((2 * count) + i));
            Vector128<byte> s3 = Vector128.LoadUnsafe(ref from, (nuint)((3 * count) + i));
            Vector128<byte> s4 = Vector128.LoadUnsafe(ref from, (nuint)((4 * count) + i));
            Vector128<byte> s5 = Vector128.LoadUnsafe(ref from, (nuint)((5 * count) + i));
            Vector128<byte> s6 = Vector128.LoadUnsafe(ref from, (nuint)((6 * count) + i));
            Vector128<byte> s7 = Vector128.LoadUnsafe(ref from, (nuint)((7 * count) + i));

            // Pairs of bytes, quartets of bytes, then the eight bytes of each value.
            Vector128<ushort> a0 = ZipLow(s0, s1).AsUInt16();
            Vector128<ushort> a1 = ZipHigh(s0, s1).AsUInt16();
            Vector128<ushort> b0 = ZipLow(s2, s3).AsUInt16();
            Vector128<ushort> b1 = ZipHigh(s2, s3).AsUInt16();
            Vector128<ushort> c0 = ZipLow(s4, s5).AsUInt16();
            Vector128<ushort> c1 = ZipHigh(s4, s5).AsUInt16();
            Vector128<ushort> d0 = ZipLow(s6, s7).AsUInt16();
            Vector128<ushort> d1 = ZipHigh(s6, s7).AsUInt16();
            Vector128<uint> ab0 = ZipLow(a0, b0).AsUInt32();
            Vector128<uint> ab1 = ZipHigh(a0, b0).AsUInt32();
            Vector128<uint> ab2 = ZipLow(a1, b1).AsUInt32();
            Vector128<uint> ab3 = ZipHigh(a1, b1).AsUInt32();
            Vector128<uint> cd0 = ZipLow(c0, d0).AsUInt32();
            Vector128<uint> cd1 = ZipHigh(c0, d0).AsUInt32();
            Vector128<uint> cd2 = ZipLow(c1, d1).AsUInt32();
            Vector128<uint> cd3 = ZipHigh(c1, d1).AsUInt32();
            nuint at = (nuint)(i * 8);
            ZipLow(ab0, cd0).AsByte().StoreUnsafe(ref into, at);
            ZipHigh(ab0, cd0).AsByte().StoreUnsafe(ref into, at + 16);
            ZipLow(ab1, cd1).AsByte().StoreUnsafe(ref into, at + 32);
            ZipHigh(ab1, cd1).AsByte().StoreUnsafe(ref into, at + 48);
            ZipLow(ab2, cd2).AsByte().StoreUnsafe(ref into, at + 64);
            ZipHigh(ab2, cd2).AsByte().StoreUnsafe(ref into, at + 80);
            ZipLow(ab3, cd3).AsByte().StoreUnsafe(ref into, at + 96);
            ZipHigh(ab3, cd3).AsByte().StoreUnsafe(ref into, at + 112);
        }

        return i;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> ZipLow(Vector128<byte> left, Vector128<byte> right) =>
        Sse2.IsSupported ? Sse2.UnpackLow(left, right) : AdvSimd.Arm64.ZipLow(left, right);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> ZipHigh(Vector128<byte> left, Vector128<byte> right) =>
        Sse2.IsSupported ? Sse2.UnpackHigh(left, right) : AdvSimd.Arm64.ZipHigh(left, right);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<ushort> ZipLow(Vector128<ushort> left, Vector128<ushort> right) =>
        Sse2.IsSupported ? Sse2.UnpackLow(left, right) : AdvSimd.Arm64.ZipLow(left, right);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<ushort> ZipHigh(Vector128<ushort> left, Vector128<ushort> right) =>
        Sse2.IsSupported ? Sse2.UnpackHigh(left, right) : AdvSimd.Arm64.ZipHigh(left, right);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<uint> ZipLow(Vector128<uint> left, Vector128<uint> right) =>
        Sse2.IsSupported ? Sse2.UnpackLow(left, right) : AdvSimd.Arm64.ZipLow(left, right);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<uint> ZipHigh(Vector128<uint> left, Vector128<uint> right) =>
        Sse2.IsSupported ? Sse2.UnpackHigh(left, right) : AdvSimd.Arm64.ZipHigh(left, right);
}
