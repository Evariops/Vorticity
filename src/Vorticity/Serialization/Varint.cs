using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Vorticity.Serialization;

/// <summary>
/// How a format refuses a varint it cannot read, each with its own exception. A reader passes a
/// struct, so that it is compiled with its own throws in place and nothing stands between the loop
/// and its caller: no status to return and test again.
/// </summary>
internal interface IVarintErrors
{
    /// <summary>Throws for data that ends at <paramref name="position"/>, before the varint's last byte.</summary>
    static abstract ulong Truncated(int position);

    /// <summary>Throws for <paramref name="value"/>, the byte at <paramref name="position"/>, which carries bits past the type's.</summary>
    static abstract ulong Malformed(int position, byte value);
}

/// <summary>
/// Unsigned LEB128 varints and the ZigZag mapping, as Protobuf, Thrift's compact protocol, Parquet's
/// RLE/bit-packing hybrid and Snappy all write them: seven bits a byte from the least significant,
/// the top bit set on every byte but the last.
/// </summary>
/// <remarks>
/// Every read is bounded at ten bytes. An overlong encoding of a representable value, <c>80 00</c>
/// for zero, is accepted: it is legal on every wire that uses these.
/// </remarks>
internal static class Varint
{
    /// <summary>The most bytes a 64-bit varint takes: ceil(64 / 7).</summary>
    internal const int MaxLength64 = 10;

    /// <summary>The most bytes a 32-bit varint takes: ceil(32 / 7).</summary>
    internal const int MaxLength32 = 5;

    /// <summary>Reads a varint of up to 64 bits at <paramref name="position"/>, which it advances past it.</summary>
    /// <typeparam name="TErrors">The format's refusals, called with the position of the failure.</typeparam>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ulong Read64<TErrors>(ReadOnlySpan<byte> data, ref int position)
        where TErrors : struct, IVarintErrors
    {
        int pos = position;
        ulong result = 0;

        // Bytes 1 to 9 carry seven value bits each: bits 0 to 62.
        for (int shift = 0; shift <= 56; shift += 7)
        {
            if ((uint)pos >= (uint)data.Length)
            {
                return TErrors.Truncated(pos);
            }

            byte b = data[pos++];
            result |= (ulong)(b & 0x7Fu) << shift;
            if ((b & 0x80) == 0)
            {
                position = pos;
                return result;
            }
        }

        // Byte 10 carries exactly one value bit, bit 63: any other bit set is a continuation or a
        // value bit past 63, neither representable.
        if ((uint)pos >= (uint)data.Length)
        {
            return TErrors.Truncated(pos);
        }

        byte last = data[pos];
        if (last > 1)
        {
            return TErrors.Malformed(pos, last);
        }

        position = pos + 1;
        return result | ((ulong)last << 63);
    }

    /// <summary>Reads a varint that must fit 32 bits: a wider value is malformed at its last byte.</summary>
    /// <typeparam name="TErrors">The format's refusals, called with the position of the failure.</typeparam>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static uint Read32<TErrors>(ReadOnlySpan<byte> data, ref int position)
        where TErrors : struct, IVarintErrors
    {
        ulong value = Read64<TErrors>(data, ref position);
        if (value > uint.MaxValue)
        {
            return (uint)TErrors.Malformed(position - 1, data[position - 1]);
        }

        return (uint)value;
    }

    /// <summary>Writes <paramref name="value"/> at the start of <paramref name="destination"/> and returns the bytes it took.</summary>
    /// <exception cref="ArgumentException">The destination is shorter than the value's varint.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int Write(Span<byte> destination, ulong value)
    {
        if (destination.Length < MaxLength64 && destination.Length < Size(value))
        {
            ThrowTooShort();
        }

        return Write(ref MemoryMarshal.GetReference(destination), value);
    }

    /// <summary>
    /// Writes <paramref name="value"/> at <paramref name="destination"/>, unchecked, and returns the
    /// bytes it took: the caller has made room for <see cref="Size"/> bytes, or for
    /// <see cref="MaxLength64"/>.
    /// </summary>
    /// <remarks>
    /// The form a writer calls that checks its capacity once per value, as the Protobuf writer does:
    /// through a span, a message of 4,096 varint fields took that writer a quarter longer than its
    /// own loop over its array; through this, 7 % less.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int Write(ref byte destination, ulong value)
    {
        // One byte on its own, so that a constant below 128, a tag, folds to a store.
        if (value < 0x80)
        {
            destination = (byte)value;
            return 1;
        }

        // A native count, so the address needs no sign extension per byte.
        nuint count = 0;
        do
        {
            Unsafe.Add(ref destination, count++) = (byte)(value | 0x80);
            value >>= 7;
        }
        while (value >= 0x80);

        Unsafe.Add(ref destination, count) = (byte)value;
        return (int)count + 1;
    }

    /// <summary>The bytes <paramref name="value"/> takes as a varint, 1 to 10.</summary>
    /// <remarks><c>value | 1</c> gives zero one significant bit, so the logarithm is defined for every input.</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int Size(ulong value) => ((63 - BitOperations.LeadingZeroCount(value | 1)) / 7) + 1;

    /// <summary>
    /// ZigZag-maps a signed 64-bit value so that small magnitudes stay short.
    /// </summary>
    /// <remarks>
    /// <c>unchecked</c> is load-bearing: <c>long.MinValue &lt;&lt; 1</c> overflows to 0, which is
    /// exactly the arithmetic the mapping needs. The pair round-trips <see cref="long.MinValue"/>.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ulong ZigZagEncode64(long value) => unchecked((ulong)((value << 1) ^ (value >> 63)));

    /// <summary>Inverse of <see cref="ZigZagEncode64"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static long ZigZagDecode64(ulong value) => unchecked((long)(value >> 1) ^ -(long)(value & 1));

    /// <summary>ZigZag-maps a signed 32-bit value.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static uint ZigZagEncode32(int value) => unchecked((uint)((value << 1) ^ (value >> 31)));

    /// <summary>Inverse of <see cref="ZigZagEncode32"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int ZigZagDecode32(uint value) => unchecked((int)(value >> 1) ^ -(int)(value & 1));

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowTooShort() =>
        throw new ArgumentException("The destination is shorter than the varint.", "destination");
}
