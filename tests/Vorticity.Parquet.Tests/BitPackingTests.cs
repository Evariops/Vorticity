using System;
using Vorticity.Parquet;
using Vorticity.Parquet.Encodings;
using Xunit;

namespace Vorticity.Parquet.Tests;

/// <summary>
/// Parquet's bit packing against a transcription of the standard written a bit at a time: every
/// width, the lengths around a group and around the vector paths' steps, and sources exactly as long
/// as the values, so that a path reading past them fails here.
/// </summary>
public sealed class BitPackingTests
{
    private static readonly int[] Counts = [0, 1, 7, 8, 9, 15, 16, 17, 31, 32, 33, 63, 64, 65, 100, 1000, 4097];

    /// <summary>The standard's own words: value i's bit k is bit i × width + k of the stream, least significant first.</summary>
    private static byte[] ReferencePack(ReadOnlySpan<ulong> values, int width)
    {
        byte[] bytes = new byte[(values.Length * (long)width + 7) / 8];
        for (int i = 0; i < values.Length; i++)
        {
            for (int k = 0; k < width; k++)
            {
                if (((values[i] >> k) & 1) != 0)
                {
                    long bit = (long)i * width + k;
                    bytes[bit >> 3] |= (byte)(1 << (int)(bit & 7));
                }
            }
        }

        return bytes;
    }

    private static ulong[] RandomValues(Random random, int count, int width)
    {
        ulong[] values = new ulong[count];
        ulong mask = width == 64 ? ulong.MaxValue : (1UL << width) - 1;
        for (int i = 0; i < count; i++)
        {
            values[i] = (ulong)random.NextInt64() ^ ((ulong)random.NextInt64() << 1);
            values[i] &= mask;
        }

        return values;
    }

    [Fact]
    public void TheStandardsExampleIsTheBytesItShows()
    {
        // Encodings.md: 0 to 7 at width 3, packed from the least significant bit, is 10001000
        // 11000110 11111010.
        uint[] values = [0, 1, 2, 3, 4, 5, 6, 7];
        byte[] packed = new byte[3];
        BitPacking.Pack32(values, 3, packed);
        Assert.Equal(new byte[] { 0x88, 0xC6, 0xFA }, packed);

        byte[] small = new byte[3];
        BitPacking.Pack8([0, 1, 2, 3, 4, 5, 6, 7], 3, small);
        Assert.Equal(packed, small);

        uint[] read = new uint[8];
        BitPacking.Unpack32(packed, 3, read);
        Assert.Equal(values, read);
    }

    [Fact]
    public void Unpack32ReadsEveryWidthAsTheStandardPacksIt()
    {
        Random random = new(32);
        for (int width = 0; width <= 32; width++)
        {
            foreach (int count in Counts)
            {
                ulong[] values = RandomValues(random, count, width);
                byte[] packed = ReferencePack(values, width);
                uint[] read = new uint[count];
                BitPacking.Unpack32(packed, width, read);
                for (int i = 0; i < count; i++)
                {
                    Assert.True(values[i] == read[i], $"width {width}, count {count}, value {i}: {values[i]} read as {read[i]}");
                }
            }
        }
    }

    [Fact]
    public void Unpack8ReadsEveryWidthAsTheStandardPacksIt()
    {
        Random random = new(8);
        for (int width = 0; width <= 8; width++)
        {
            foreach (int count in Counts)
            {
                ulong[] values = RandomValues(random, count, width);
                byte[] packed = ReferencePack(values, width);
                byte[] read = new byte[count];
                BitPacking.Unpack8(packed, width, read);
                for (int i = 0; i < count; i++)
                {
                    Assert.True(values[i] == read[i], $"width {width}, count {count}, value {i}");
                }
            }
        }
    }

    [Fact]
    public void Unpack64ReadsEveryWidthAsTheStandardPacksIt()
    {
        Random random = new(64);
        for (int width = 0; width <= 64; width++)
        {
            foreach (int count in Counts)
            {
                ulong[] values = RandomValues(random, count, width);
                byte[] packed = ReferencePack(values, width);
                ulong[] read = new ulong[count];
                BitPacking.Unpack64(packed, width, read);
                Assert.Equal(values, read);
            }
        }
    }

    [Fact]
    public void PackWritesTheBytesTheStandardDescribes()
    {
        Random random = new(7);
        for (int width = 0; width <= 64; width++)
        {
            foreach (int count in Counts)
            {
                ulong[] values = RandomValues(random, count, width);
                byte[] expected = ReferencePack(values, width);

                byte[] packed64 = new byte[expected.Length];
                BitPacking.Pack64(values, width, packed64);
                Assert.Equal(expected, packed64);

                if (width <= 32)
                {
                    uint[] narrow = Array.ConvertAll(values, v => (uint)v);
                    byte[] packed32 = new byte[expected.Length];
                    BitPacking.Pack32(narrow, width, packed32);
                    Assert.Equal(expected, packed32);
                }

                if (width <= 8)
                {
                    byte[] bytes = Array.ConvertAll(values, v => (byte)v);
                    byte[] packed8 = new byte[expected.Length];
                    BitPacking.Pack8(bytes, width, packed8);
                    Assert.Equal(expected, packed8);
                }
            }
        }
    }

    [Fact]
    public void ASourceShorterThanItsValuesIsRefused()
    {
        byte[] packed = new byte[11];
        Assert.Throws<ParquetFormatException>(() => BitPacking.Unpack32(packed, 3, new uint[30]));
        Assert.Throws<ParquetFormatException>(() => BitPacking.Unpack8(packed, 3, new byte[30]));
        Assert.Throws<ParquetFormatException>(() => BitPacking.Unpack64(packed, 3, new ulong[30]));
    }

    [Fact]
    public void AWidthPastTheValuesIsRefused()
    {
        byte[] packed = new byte[64];
        Assert.Throws<ParquetFormatException>(() => BitPacking.Unpack32(packed, 33, new uint[1]));
        Assert.Throws<ParquetFormatException>(() => BitPacking.Unpack8(packed, 9, new byte[1]));
        Assert.Throws<ParquetFormatException>(() => BitPacking.Unpack64(packed, 65, new ulong[1]));
    }
}
