using System;
using System.Numerics;

namespace Vorticity.RowEncoding;

/// <summary>
/// Maps one type's raw little-endian bits to the unsigned integer whose big-endian bytes sort like
/// the original values, so that a single kernel serves every fixed-width type.
/// </summary>
internal interface IRowOrdering<T>
    where T : unmanaged, IBinaryInteger<T>
{
    static abstract T ToOrdered(T raw);
}

/// <summary>Unsigned integers already sort by their big-endian bytes.</summary>
internal readonly struct OrderU8 : IRowOrdering<byte>
{
    public static byte ToOrdered(byte raw) => raw;
}

internal readonly struct OrderU16 : IRowOrdering<ushort>
{
    public static ushort ToOrdered(ushort raw) => raw;
}

internal readonly struct OrderU32 : IRowOrdering<uint>
{
    public static uint ToOrdered(uint raw) => raw;
}

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

internal readonly struct OrderI16 : IRowOrdering<ushort>
{
    public static ushort ToOrdered(ushort raw) => (ushort)(raw ^ 0x8000);
}

internal readonly struct OrderI32 : IRowOrdering<uint>
{
    public static uint ToOrdered(uint raw) => raw ^ 0x8000_0000u;
}

internal readonly struct OrderI64 : IRowOrdering<ulong>
{
    public static ulong ToOrdered(ulong raw) => raw ^ 0x8000_0000_0000_0000ul;
}

/// <summary>
/// IEEE 754, made totally ordered: a non-negative gets its sign bit set, a negative gets every bit
/// flipped. Negatives then descend as their magnitude grows, <c>-0.0</c> lands just below
/// <c>+0.0</c>, and NaNs are not canonicalized, so two NaNs with different payloads encode to
/// different keys.
/// </summary>
internal readonly struct OrderF16 : IRowOrdering<ushort>
{
    public static ushort ToOrdered(ushort raw) => (ushort)(raw ^ ((raw >> 15) == 0 ? 0x8000 : 0xFFFF));
}

internal readonly struct OrderF32 : IRowOrdering<uint>
{
    public static uint ToOrdered(uint raw) => raw ^ ((raw >> 31) == 0 ? 0x8000_0000u : 0xFFFF_FFFFu);
}

internal readonly struct OrderF64 : IRowOrdering<ulong>
{
    public static ulong ToOrdered(ulong raw) =>
        raw ^ ((raw >> 63) == 0 ? 0x8000_0000_0000_0000ul : 0xFFFF_FFFF_FFFF_FFFFul);
}
