// The split-block Bloom filter of docs/10-indexes.md §5.1: blocks of 256 bits held as eight 32-bit
// words, one block chosen per value by the high half of its hash, one bit set per word by the low
// half.
//
// BIT-IDENTICAL TO THE REFERENCE'S `BloomPartial` (vortex-layout-0.86.1
// layouts/zoned/aggregates/bloom_filter/partial/mod.rs), line for line: XxHash3-64 with seed 0 over
// the value's bytes; block = ((h >> 32) * n) >> 32, Lemire's fast range; bit i of the block's word i
// at ((uint)h * SALT[i]) >> 27, with Parquet's eight salts in Parquet's order. So a filter written
// here is the filter the reference would write for the same values, for as long as upstream keeps
// that layout -- and the day it freezes one, ours can become theirs by a change of id.
//
// THE HASHED BYTES are the reference's too (canonical/primitive.rs, canonical/varbin.rs,
// partial/scalar.rs): an integer or a float as its little-endian bytes at the column's own width,
// a string or a binary as its bytes, an extension as its storage, a null never. A float is hashed by
// bit pattern, so -0.0 and +0.0 are two values here; the probe, which answers an IEEE equality, asks
// for both (see `BloomProbe`).
using System;
using System.IO.Hashing;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Vorticity.Indexes;

/// <summary>Split-block Bloom filter primitives over a span of 32-bit words.</summary>
internal static class SplitBlockBloom
{
    /// <summary>Words per block.</summary>
    internal const int WordsPerBlock = 8;

    /// <summary>Bytes per block: eight 32-bit words, 256 bits.</summary>
    internal const int BytesPerBlock = WordsPerBlock * sizeof(uint);

    private static ReadOnlySpan<uint> Salts =>
    [
        0x47b6137b, 0x44974d91, 0x8824ad5b, 0xa2b7289d, 0x705495c7, 0x2df1424b, 0x9efc4947, 0x5c6bfb31,
    ];

    /// <summary>The hash a filter stores for <paramref name="value"/>.</summary>
    /// <param name="value">The value's bytes, as §5.1's table spells them.</param>
    /// <param name="hash">XxHash3-64 by default; xxHash64 for the Parquet-compatible variant.</param>
    /// <returns>The 64-bit hash.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ulong Hash(ReadOnlySpan<byte> value, BloomHash hash) =>
        hash == BloomHash.XxHash64
            ? XxHash64.HashToUInt64(value)
            : XxHash3.HashToUInt64(value);

    /// <summary>
    /// The block count for <paramref name="distinct"/> values at a false-positive rate of
    /// <paramref name="fppPpm"/> parts per million: Parquet's sizing formula, rounded up to a
    /// power of two and clamped to <c>[1, maxBlocks]</c>.
    /// </summary>
    /// <remarks>
    /// Parquet's <c>optimal_num_of_bytes</c>: <c>m = -8 n / ln(1 - p^(1/8))</c> bits, then bytes,
    /// then a power of two. The clamp comes after, so a column past the ceiling gets a filter whose
    /// rate is worse than asked rather than none at all; the report states the ceiling.
    /// </remarks>
    /// <param name="distinct">Distinct values the filter will hold; at least 1.</param>
    /// <param name="fppPpm">The target false-positive rate, in parts per million.</param>
    /// <param name="maxBlocks">The ceiling.</param>
    /// <returns>A power of two in <c>[1, maxBlocks]</c>, or <paramref name="maxBlocks"/> itself when that is not one.</returns>
    internal static int BlocksFor(long distinct, int fppPpm, int maxBlocks)
    {
        double p = Math.Clamp(fppPpm / 1_000_000.0, 1e-9, 0.5);
        double bits = -8.0 * Math.Max(distinct, 1) / Math.Log(1.0 - Math.Pow(p, 1.0 / 8.0));
        double blocks = Math.Ceiling(bits / 8.0 / BytesPerBlock);
        if (!(blocks >= 1))
        {
            return 1;
        }

        if (blocks >= maxBlocks)
        {
            return maxBlocks;
        }

        int rounded = (int)BitOperations.RoundUpToPowerOf2((uint)blocks);
        return Math.Min(rounded, maxBlocks);
    }

    /// <summary>Adds <paramref name="hash"/> to the filter held by <paramref name="words"/>.</summary>
    /// <param name="words">The filter: a whole number of blocks.</param>
    /// <param name="hash">The value's hash.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void Insert(Span<uint> words, ulong hash)
    {
        int block = BlockIndex(hash, words.Length / WordsPerBlock);
        Span<uint> lanes = words.Slice(block * WordsPerBlock, WordsPerBlock);
        uint key = unchecked((uint)hash);
        ReadOnlySpan<uint> salts = Salts;
        for (int i = 0; i < WordsPerBlock; i++)
        {
            lanes[i] |= 1u << (int)((key * salts[i]) >> 27);
        }
    }

    /// <summary>Whether <paramref name="hash"/> may be in the filter.</summary>
    /// <param name="words">The filter.</param>
    /// <param name="hash">The value's hash.</param>
    /// <returns><see langword="false"/> proves the value absent.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool Contains(ReadOnlySpan<uint> words, ulong hash)
    {
        int block = BlockIndex(hash, words.Length / WordsPerBlock);
        ReadOnlySpan<uint> lanes = words.Slice(block * WordsPerBlock, WordsPerBlock);
        uint key = unchecked((uint)hash);
        ReadOnlySpan<uint> salts = Salts;
        uint missing = 0;
        for (int i = 0; i < WordsPerBlock; i++)
        {
            missing |= ~lanes[i] & (1u << (int)((key * salts[i]) >> 27));
        }

        return missing == 0;
    }

    /// <summary>
    /// The filter's words over little-endian bytes, which is how a payload stores them. The library
    /// refuses a big-endian host at startup (<c>VortexRuntimeChecks</c>), so this is a cast.
    /// </summary>
    /// <param name="bytes">The payload; a trailing partial block is ignored.</param>
    /// <returns>The words.</returns>
    internal static ReadOnlySpan<uint> Words(ReadOnlySpan<byte> bytes) =>
        MemoryMarshal.Cast<byte, uint>(bytes[..(bytes.Length / BytesPerBlock * BytesPerBlock)]);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int BlockIndex(ulong hash, int blocks) =>
        (int)(((hash >> 32) * (ulong)(uint)blocks) >> 32);
}
