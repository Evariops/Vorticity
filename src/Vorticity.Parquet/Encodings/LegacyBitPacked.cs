using System;

namespace Vorticity.Parquet.Encodings;

/// <summary>
/// BIT_PACKED, the deprecated encoding of the levels of older files' v1 pages: values back to back
/// at a fixed width, packed from each byte's most significant bit, the reverse of the hybrid's order.
/// </summary>
internal static class LegacyBitPacked
{
    /// <summary>The bytes <paramref name="count"/> values of <paramref name="width"/> bits take.</summary>
    internal static int Bytes(int count, int width) => (int)(((long)count * width + 7) >> 3);

    /// <summary>
    /// Decodes <paramref name="count"/> levels of one bit, MSB first, into a least-significant-first
    /// bitmap; the levels set.
    /// </summary>
    /// <exception cref="ParquetFormatException">The data holds fewer than <paramref name="count"/> levels.</exception>
    internal static int ReadBits(ReadOnlySpan<byte> data, int count, Span<byte> bits)
    {
        int bytes = Bytes(count, 1);
        if (data.Length < bytes)
        {
            ParquetThrow.Truncated("BIT_PACKED levels");
        }

        int set = 0;
        for (int i = 0; i < bytes; i++)
        {
            // Reversing a byte's bits turns most-significant-first into least-significant-first.
            byte reversed = (byte)(((data[i] * 0x0202020202UL) & 0x010884422010UL) % 1023);
            if (i == bytes - 1 && (count & 7) != 0)
            {
                reversed &= (byte)((1 << (count & 7)) - 1);
            }

            bits[i] = reversed;
            set += System.Numerics.BitOperations.PopCount(reversed);
        }

        return set;
    }
}
