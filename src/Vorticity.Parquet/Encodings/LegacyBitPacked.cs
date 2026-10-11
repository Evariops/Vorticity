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

    /// <summary>
    /// Decodes <c>levels.Length</c> levels of <paramref name="width"/> bits, at most 8, each read from
    /// its most significant bit, into a byte each.
    /// </summary>
    /// <exception cref="ParquetFormatException">The data holds fewer levels.</exception>
    internal static void ReadLevels(ReadOnlySpan<byte> data, int width, Span<byte> levels)
    {
        if ((uint)width > 8)
        {
            ParquetThrow.Format($"BIT_PACKED levels of {width} bits do not fit a byte.");
        }

        if (data.Length < Bytes(levels.Length, width))
        {
            ParquetThrow.Truncated("BIT_PACKED levels");
        }

        // A window of the stream's next bits, its oldest at the top: a byte enters at the bottom
        // when fewer bits than a level are left in it.
        uint window = 0;
        int held = 0;
        int next = 0;
        uint mask = (1u << width) - 1;
        for (int i = 0; i < levels.Length; i++)
        {
            if (held < width)
            {
                window = (window << 8) | data[next++];
                held += 8;
            }

            held -= width;
            levels[i] = (byte)((window >> held) & mask);
        }
    }
}
