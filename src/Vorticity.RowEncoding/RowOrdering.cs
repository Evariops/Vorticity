// The value transforms: raw little-endian storage bits -> an unsigned integer whose big-endian
// bytes sort like the original values.
//
// Every fixed-width type reduces to the SAME three steps - map to an ordered unsigned integer,
// complement it when the column is descending, write it big-endian - so there is exactly one loop
// (RowFixedKernel) and these twelve one-line structs choose the mapping. The alternative, a switch
// with twelve copies of the loop, is where a transcription error hides: eleven correct cases and
// one that forgot the sign flip reads as symmetric code and fails only on that type's negatives.
//
// Transcribed from the `RowEncode` trait in vortex-row/src/codec.rs at 0.86.1.
using System;
using System.Numerics;

namespace Vorticity.RowEncoding;

/// <summary>Maps one type's raw bits to the unsigned integer whose big-endian bytes sort alike.</summary>
/// <typeparam name="T">The unsigned integer holding the raw little-endian bits.</typeparam>
internal interface IRowOrdering<T>
    where T : unmanaged, IBinaryInteger<T>
{
    /// <summary>Transforms raw bits into order-preserving bits.</summary>
    /// <param name="raw">The value as stored.</param>
    /// <returns>The value to write big-endian.</returns>
    static abstract T ToOrdered(T raw);
}

/// <summary>Unsigned integers already sort by their big-endian bytes.</summary>
internal readonly struct OrderU8 : IRowOrdering<byte>
{
    public static byte ToOrdered(byte raw) => raw;
}

/// <inheritdoc cref="OrderU8"/>
internal readonly struct OrderU16 : IRowOrdering<ushort>
{
    public static ushort ToOrdered(ushort raw) => raw;
}

/// <inheritdoc cref="OrderU8"/>
internal readonly struct OrderU32 : IRowOrdering<uint>
{
    public static uint ToOrdered(uint raw) => raw;
}

/// <inheritdoc cref="OrderU8"/>
internal readonly struct OrderU64 : IRowOrdering<ulong>
{
    public static ulong ToOrdered(ulong raw) => raw;
}

/// <summary>
/// Two's complement puts negatives above positives in unsigned order; flipping the sign bit moves
/// them back below, which is the whole of the signed transform.
/// </summary>
internal readonly struct OrderI8 : IRowOrdering<byte>
{
    public static byte ToOrdered(byte raw) => (byte)(raw ^ 0x80);
}

/// <inheritdoc cref="OrderI8"/>
internal readonly struct OrderI16 : IRowOrdering<ushort>
{
    public static ushort ToOrdered(ushort raw) => (ushort)(raw ^ 0x8000);
}

/// <inheritdoc cref="OrderI8"/>
internal readonly struct OrderI32 : IRowOrdering<uint>
{
    public static uint ToOrdered(uint raw) => raw ^ 0x8000_0000u;
}

/// <inheritdoc cref="OrderI8"/>
internal readonly struct OrderI64 : IRowOrdering<ulong>
{
    public static ulong ToOrdered(ulong raw) => raw ^ 0x8000_0000_0000_0000ul;
}

/// <summary>
/// IEEE 754, made totally ordered: a non-negative gets its sign bit set, a negative gets every bit
/// flipped. Negatives then descend as their magnitude grows, <c>-0.0</c> lands just below
/// <c>+0.0</c>, and NaNs order by raw bit pattern - they are NOT canonicalized, so two NaNs with
/// different payloads encode to different keys.
/// </summary>
internal readonly struct OrderF16 : IRowOrdering<ushort>
{
    public static ushort ToOrdered(ushort raw) => (ushort)(raw ^ ((raw >> 15) == 0 ? 0x8000 : 0xFFFF));
}

/// <inheritdoc cref="OrderF16"/>
internal readonly struct OrderF32 : IRowOrdering<uint>
{
    public static uint ToOrdered(uint raw) => raw ^ ((raw >> 31) == 0 ? 0x8000_0000u : 0xFFFF_FFFFu);
}

/// <inheritdoc cref="OrderF16"/>
internal readonly struct OrderF64 : IRowOrdering<ulong>
{
    public static ulong ToOrdered(ulong raw) =>
        raw ^ ((raw >> 63) == 0 ? 0x8000_0000_0000_0000ul : 0xFFFF_FFFF_FFFF_FFFFul);
}
