// Reading a bitmap sixty-four rows at a time - the kernel WRITE-AUDIT.md W-33 introduced, extracted
// so that the fused pass and the run-end scan share one copy of it.
//
// It existed inside `ColumnCompressor` because the run scan was the only thing that walked a bitmap
// by words. Since docs/11-write-strategy.md §8 stage 2b the ingest pass counts run boundaries too,
// and the measurement that made the trick necessary is the same one: the bit-at-a-time form of it
// cost **1,52 against a 0,78 reference** on the 1M `bool` file, which is what a per-row `BitAt`
// costs when a whole word could have been one xor and a popcount.
using System;
using System.Buffers.Binary;

namespace Vorticity.Writing;

/// <summary>Word-at-a-time reads over a bitmap that need not be byte aligned.</summary>
internal static class BitWords
{
    /// <summary>The low <paramref name="count"/> bits, with 64 meaning all of them.</summary>
    /// <param name="count">1 to 64.</param>
    internal static ulong Mask(int count) => count == 64 ? ulong.MaxValue : (1UL << count) - 1;

    /// <summary>
    /// Sixty-four bits starting at bit <paramref name="bitIndex"/>, reading past the end as zeroes.
    /// </summary>
    /// <param name="bits">The bitmap.</param>
    /// <param name="bitIndex">The first bit, LSB-first within each byte.</param>
    internal static ulong Load(ReadOnlySpan<byte> bits, int bitIndex) =>
        Load(bits, bitIndex >> 3, bitIndex & 7);

    /// <summary>
    /// Sixty-four bits starting <paramref name="shift"/> bits into byte <paramref name="index"/>.
    /// </summary>
    /// <param name="bits">The bitmap.</param>
    /// <param name="index">The first byte.</param>
    /// <param name="shift">0 to 7.</param>
    internal static ulong Load(ReadOnlySpan<byte> bits, int index, int shift)
    {
        ulong low = Eight(bits, index);
        return shift == 0 ? low : (low >> shift) | ((ulong)Byte(bits, index + 8) << (64 - shift));
    }

    private static ulong Eight(ReadOnlySpan<byte> bits, int index)
    {
        if ((uint)index + 8 <= (uint)bits.Length)
        {
            return BinaryPrimitives.ReadUInt64LittleEndian(bits.Slice(index, 8));
        }

        // THE TAIL IS READ BYTE BY BYTE rather than over-read: the bitmap's last word is a partial
        // one whenever the row count is not a multiple of 64, and the buffer is a window into a
        // segment that owes nothing past its own length.
        ulong value = 0;
        for (int i = 0; i < 8; i++)
        {
            value |= (ulong)Byte(bits, index + i) << (i * 8);
        }

        return value;
    }

    private static byte Byte(ReadOnlySpan<byte> bits, int index) =>
        (uint)index < (uint)bits.Length ? bits[index] : (byte)0;
}
