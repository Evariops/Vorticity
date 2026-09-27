using System;
using System.IO.Hashing;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Vorticity.Indexes;

/// <summary>
/// Split-block Bloom filter primitives over a span of 32-bit words: one 256-bit block of eight words
/// per value, chosen by the high half of the hash, one bit set per word from the low half, with
/// Parquet's eight salts in Parquet's order. Layout, hash and block choice match the reference
/// implementation exactly, so a filter written here is the one the reference would write for the
/// same values, and a reader of either can trust the other's bits.
/// </summary>
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
    /// <remarks>
    /// A float is hashed by bit pattern, so negative and positive zero are two values here; a probe
    /// answering an equality has to ask for both.
    /// </remarks>
    /// <param name="value">
    /// The value's bytes: an integer or a float as its little-endian bytes at the column's own width,
    /// a string or a binary as its bytes, an extension as its storage. A null is never hashed.
    /// </param>
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
        if (Avx2.IsSupported)
        {
            ref uint first = ref MemoryMarshal.GetReference(lanes);
            (Vector256.LoadUnsafe(ref first) | Bits(key)).StoreUnsafe(ref first);
            return;
        }

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
        if (Avx2.IsSupported)
        {
            return (Bits(key) & ~Vector256.LoadUnsafe(ref MemoryMarshal.GetReference(lanes))) == Vector256<uint>.Zero;
        }

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
    /// refuses a big-endian host before it reads a file (<c>VortexRuntimeChecks</c>), so this is a cast.
    /// </summary>
    /// <param name="bytes">The payload; a trailing partial block is ignored.</param>
    /// <returns>The words.</returns>
    internal static ReadOnlySpan<uint> Words(ReadOnlySpan<byte> bytes) =>
        MemoryMarshal.Cast<byte, uint>(bytes[..(bytes.Length / BytesPerBlock * BytesPerBlock)]);

    /// <summary>
    /// The bit <paramref name="key"/> sets in each word of a block, all eight at once: the key times
    /// each word's salt, whose top five bits index the bit, shifted in by a lane each.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<uint> Bits(uint key) =>
        Avx2.ShiftLeftLogicalVariable(
            Vector256<uint>.One,
            (Vector256.Create(key) * Vector256.Create(0x47b6137bu, 0x44974d91, 0x8824ad5b, 0xa2b7289d, 0x705495c7, 0x2df1424b, 0x9efc4947, 0x5c6bfb31)) >> 27);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int BlockIndex(ulong hash, int blocks) =>
        (int)(((hash >> 32) * (ulong)(uint)blocks) >> 32);
}
