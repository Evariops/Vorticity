using System;
using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Vorticity.Indexes;

/// <summary>
/// XxHash3-64, seed 0, for inputs of a known short length, inlined at the width a column decides.
/// These are the algorithm's own short-input paths with the default secret and a zero seed folded in
/// as constants, not a look-alike: a Bloom filter built on them has to stay bit for bit the one the
/// reference builds. What they buy over the general library call is the per-value dispatch on the
/// length; the paths themselves are a handful of 64-bit multiplies, which no lane width helps.
/// </summary>
internal static class XxHash3Fixed
{
    // The default secret's words, little-endian (kSecret, bytes 0 to 55).
    private const uint Secret0 = 0x396cfeb8;
    private const uint Secret4 = 0xbe4ba423;
    private const ulong Secret8 = 0x1cad21f72c81017c;
    private const ulong Secret16 = 0xdb979083e96dd4de;
    private const ulong Secret24 = 0x1f67b3b7a4a44072;
    private const ulong Secret32 = 0x78e5c0cc4ee679cb;
    private const ulong Secret40 = 0x2172ffcc7dd05a82;
    private const ulong Secret48 = 0x8e2443f7744608b8;

    private const ulong Prime64x2 = 0xC2B2AE3D27D4EB4F;
    private const ulong Prime64x3 = 0x165667B19E3779F9;
    private const ulong PrimeMx1 = 0x165667919E3779F9;
    private const ulong PrimeMx2 = 0x9FB21C651E98DF25;

    /// <summary>The hash of a one- to three-byte input (<c>len_1to3</c>).</summary>
    /// <param name="value">One to three bytes.</param>
    /// <returns>The hash.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ulong Hash1To3(ReadOnlySpan<byte> value)
    {
        int length = value.Length;
        uint combined = ((uint)value[0] << 16) | ((uint)value[length >> 1] << 24)
            | value[length - 1] | ((uint)length << 8);
        ulong keyed = combined ^ (ulong)(Secret0 ^ Secret4);
        return Avalanche64(keyed);
    }

    /// <summary>The hash of a four-byte input (<c>len_4to8</c> at 4).</summary>
    /// <param name="value">The four bytes, as their little-endian word.</param>
    /// <returns>The hash.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ulong Hash4(uint value)
    {
        // Both reads of `len_4to8` are the same word at this length.
        ulong input = value + ((ulong)value << 32);
        return Rrmxmx(input ^ (Secret8 ^ Secret16), 4);
    }

    /// <summary>The hash of an eight-byte input (<c>len_4to8</c> at 8).</summary>
    /// <param name="value">The eight bytes, as their little-endian word.</param>
    /// <returns>The hash.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ulong Hash8(ulong value)
    {
        uint low = (uint)value;
        uint high = (uint)(value >> 32);
        ulong input = high + ((ulong)low << 32);
        return Rrmxmx(input ^ (Secret8 ^ Secret16), 8);
    }

    /// <summary>The hash of a nine- to sixteen-byte input (<c>len_9to16</c>).</summary>
    /// <param name="value">Nine to sixteen bytes.</param>
    /// <returns>The hash.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ulong Hash9To16(ReadOnlySpan<byte> value)
    {
        int length = value.Length;
        ulong low = BinaryPrimitives.ReadUInt64LittleEndian(value) ^ (Secret24 ^ Secret32);
        ulong high = BinaryPrimitives.ReadUInt64LittleEndian(value[(length - 8)..]) ^ (Secret40 ^ Secret48);
        ulong product = Math.BigMul(low, high, out ulong lower);
        ulong acc = (ulong)length + BinaryPrimitives.ReverseEndianness(low) + high + (product ^ lower);
        return Avalanche3(acc);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong Rrmxmx(ulong h, int length)
    {
        h ^= BitOperations.RotateLeft(h, 49) ^ BitOperations.RotateLeft(h, 24);
        h *= PrimeMx2;
        h ^= (h >> 35) + (ulong)length;
        h *= PrimeMx2;
        return h ^ (h >> 28);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong Avalanche64(ulong h)
    {
        h ^= h >> 33;
        h *= Prime64x2;
        h ^= h >> 29;
        h *= Prime64x3;
        return h ^ (h >> 32);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong Avalanche3(ulong h)
    {
        h ^= h >> 37;
        h *= PrimeMx1;
        return h ^ (h >> 32);
    }
}
