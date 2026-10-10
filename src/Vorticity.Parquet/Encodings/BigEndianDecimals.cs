using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace Vorticity.Parquet.Encodings;

/// <summary>
/// DECIMAL values stored as big-endian two's complement, in fixed-length or length-prefixed byte
/// arrays, sign-extended into the little-endian storage of their precision: 4, 8, 16 or 32 bytes.
/// </summary>
/// <remarks>
/// A value is read where its bytes start, as many bytes as its storage holds, big-endian, then
/// shifted right arithmetically by the bytes past its own: the load reverses the bytes and the shift
/// extends the sign, in a few instructions for a storage of 4 or 8 bytes, and for 16 in two loads and
/// a 128-bit shift. A fixed-length column stored in 16 bytes is widened a value per 128-bit register:
/// a byte shuffle reverses the value's bytes and repeats its first, whose sign a comparison spreads
/// over the bytes past it. A value whose storage's worth of bytes runs past the source, the last few,
/// is widened a byte at a time, and so is a storage of 32 bytes.
/// </remarks>
internal static class BigEndianDecimals
{
    /// <summary>
    /// Widens <paramref name="count"/> values of <paramref name="width"/> bytes each, back to back in
    /// <paramref name="source"/>, into <paramref name="slot"/> bytes each of <paramref name="into"/>.
    /// The caller has checked that <paramref name="width"/> is at most <paramref name="slot"/> and that
    /// <paramref name="source"/> holds the values.
    /// </summary>
    internal static void WidenFixed(ReadOnlySpan<byte> source, int width, Span<byte> into, int slot, int count)
    {
        int i = slot == 16 && width > 0 && Vector128.IsHardwareAccelerated ? Registers(source, width, into, count) : 0;
        for (; i < count; i++)
        {
            Widen(source, i * width, width, into.Slice(i * slot, slot));
        }
    }

    /// <summary>
    /// Widens the value of <paramref name="width"/> bytes at <paramref name="position"/> of
    /// <paramref name="source"/> into <paramref name="into"/>, its storage: by a big-endian load and an
    /// arithmetic shift where the storage's worth of bytes lies in the source, a byte at a time otherwise.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void Widen(ReadOnlySpan<byte> source, int position, int width, Span<byte> into)
    {
        int slot = into.Length;
        if (width == 0)
        {
            into.Clear();
            return;
        }

        if (source.Length - position >= slot)
        {
            ReadOnlySpan<byte> at = source[position..];
            int shift = (slot - width) * 8;
            switch (slot)
            {
                case 4:
                    BinaryPrimitives.WriteInt32LittleEndian(into, BinaryPrimitives.ReadInt32BigEndian(at) >> shift);
                    return;
                case 8:
                    BinaryPrimitives.WriteInt64LittleEndian(into, BinaryPrimitives.ReadInt64BigEndian(at) >> shift);
                    return;
                case 16:
                    BinaryPrimitives.WriteInt128LittleEndian(into, BinaryPrimitives.ReadInt128BigEndian(at) >> shift);
                    return;
            }
        }

        Bytes(source.Slice(position, width), into);
    }

    /// <summary>A value a byte at a time: its bytes reversed, and its sign repeated over the storage's bytes past them.</summary>
    private static void Bytes(ReadOnlySpan<byte> bigEndian, Span<byte> into)
    {
        byte sign = (bigEndian[0] & 0x80) != 0 ? (byte)0xFF : (byte)0;
        into.Fill(sign);
        for (int b = 0; b < bigEndian.Length; b++)
        {
            into[b] = bigEndian[bigEndian.Length - 1 - b];
        }
    }

    /// <summary>
    /// The values a 128-bit register widens, each from a load of 16 bytes at its start: every value
    /// but those whose 16 bytes run past the source; how many.
    /// </summary>
    private static int Registers(ReadOnlySpan<byte> source, int width, Span<byte> into, int count)
    {
        // The value's bytes last first, then its first byte again in every place past them, which
        // the sign comparison turns into its sign.
        Span<byte> order = stackalloc byte[16];
        Span<byte> past = stackalloc byte[16];
        for (int k = 0; k < 16; k++)
        {
            order[k] = (byte)(k < width ? width - 1 - k : 0);
            past[k] = (byte)(k < width ? 0 : 0xFF);
        }

        Vector128<byte> shuffle = Vector128.Create<byte>(order);
        Vector128<byte> upper = Vector128.Create<byte>(past);
        int fits = source.Length < 16 ? 0 : Math.Min(count, ((source.Length - 16) / width) + 1);
        ref byte from = ref MemoryMarshal.GetReference(source);
        ref byte to = ref MemoryMarshal.GetReference(into);
        for (int i = 0; i < fits; i++)
        {
            Vector128<byte> reversed = Vector128.ShuffleNative(Vector128.LoadUnsafe(ref from, (nuint)(i * width)), shuffle);
            Vector128<byte> sign = Vector128.LessThan(reversed.AsSByte(), Vector128<sbyte>.Zero).AsByte();
            Vector128.ConditionalSelect(upper, sign, reversed).StoreUnsafe(ref to, (nuint)(i * 16));
        }

        return fits;
    }
}
