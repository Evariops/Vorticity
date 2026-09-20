using System.Numerics;
using System.Runtime.CompilerServices;

namespace Vorticity.Serialization.Protobuf;

/// <summary>
/// Wire-level constants and pure conversions shared by <see cref="ProtoReader"/> and
/// <see cref="ProtoWriter"/>. Nothing here touches a buffer, so nothing here can throw.
/// </summary>
internal static class ProtoWire
{
    /// <summary>
    /// Maximum bytes in a well-formed varint. A 64-bit value needs ceil(64/7) = 10 groups of
    /// seven bits; an eleventh byte can only ever be an encoding error.
    /// </summary>
    internal const int MaxVarintLength = 10;

    /// <summary>
    /// Largest legal field number. A tag is a <c>uint32</c> holding
    /// <c>(field_number &lt;&lt; 3) | wire_type</c>, which leaves 29 bits for the number.
    /// </summary>
    internal const int MaxFieldNumber = (1 << 29) - 1;

    /// <summary>Bit count the field number is shifted by inside a tag.</summary>
    internal const int TagTypeBits = 3;

    /// <summary>Mask selecting the wire type out of a tag.</summary>
    internal const uint WireTypeMask = 7;

    /// <summary>
    /// ZigZag-encodes a signed 64-bit value so small magnitudes stay short on the wire
    /// (<c>sint64</c>; the scalar schema's <c>ScalarValue.int64_value</c> field is encoded this way).
    /// </summary>
    /// <remarks>
    /// <c>unchecked</c> is load-bearing: <c>long.MinValue &lt;&lt; 1</c> overflows to 0, which is
    /// exactly the arithmetic the encoding requires. The pair round-trips
    /// <see cref="long.MinValue"/>.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ulong ZigZagEncode64(long value) => unchecked((ulong)((value << 1) ^ (value >> 63)));

    /// <summary>Inverse of <see cref="ZigZagEncode64"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static long ZigZagDecode64(ulong value) => unchecked((long)(value >> 1) ^ -(long)(value & 1));

    /// <summary>ZigZag-encodes a signed 32-bit value (<c>sint32</c>).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static uint ZigZagEncode32(int value) => unchecked((uint)((value << 1) ^ (value >> 31)));

    /// <summary>Inverse of <see cref="ZigZagEncode32"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int ZigZagDecode32(uint value) => unchecked((int)(value >> 1) ^ -(int)(value & 1));

    /// <summary>
    /// Number of bytes <paramref name="value"/> occupies as a varint, in the range 1..10.
    /// </summary>
    /// <remarks>
    /// Branch-free: <c>value | 1</c> makes zero report a single significant bit, so the
    /// <c>Log2</c> below is defined for every input.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int VarintSize(ulong value) =>
        ((63 - BitOperations.LeadingZeroCount(value | 1)) / 7) + 1;
}
