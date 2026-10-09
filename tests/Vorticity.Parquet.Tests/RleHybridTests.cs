using System;
using Vorticity.Parquet;
using Vorticity.Parquet.Encodings;
using Xunit;

namespace Vorticity.Parquet.Tests;

/// <summary>
/// The RLE/bit-packing hybrid: the grammar's own bytes, round trips over data with runs of every
/// length at every width, decoding resumed in batches of any size, the plan's size equal to the
/// bytes written, and malformed runs refused.
/// </summary>
public sealed class RleHybridTests
{
    private static uint[] Runs(Random random, int count, int width)
    {
        uint[] values = new uint[count];
        uint limit = width == 32 ? uint.MaxValue : (1u << width) - 1;
        int i = 0;
        while (i < count)
        {
            int run = random.Next(4) switch
            {
                0 => 1,
                1 => random.Next(1, 9),
                2 => random.Next(8, 40),
                _ => random.Next(40, 400),
            };
            uint value = width == 0 ? 0 : (uint)random.NextInt64(0, (long)limit + 1);
            for (int k = 0; k < run && i < count; k++)
            {
                values[i++] = random.Next(3) == 0 && width > 0 ? (uint)random.NextInt64(0, (long)limit + 1) : value;
            }
        }

        return values;
    }

    [Fact]
    public void TheGrammarsBytesDecode()
    {
        // A bit-packed run of one group, 0 to 7 at width 3 (the encodings page's example), then a
        // repeated run of 1000 fives: a header of 2000, then the value in one byte.
        byte[] data = [0x03, 0x88, 0xC6, 0xFA, 0xD0, 0x0F, 0x05];
        RleHybridDecoder decoder = new(3);
        uint[] values = new uint[1008];
        decoder.Read(data, values);
        for (int i = 0; i < 8; i++)
        {
            Assert.Equal((uint)i, values[i]);
        }

        for (int i = 8; i < 1008; i++)
        {
            Assert.Equal(5u, values[i]);
        }
    }

    [Fact]
    public void EveryWidthRoundTripsAndThePlanIsTheBytesWritten()
    {
        Random random = new(3);
        foreach (int count in new[] { 0, 1, 7, 8, 9, 100, 1023, 8192 })
        {
            for (int width = 0; width <= 32; width++)
            {
                uint[] values = Runs(random, count, width);
                int size = RleHybridEncoder.Size(values, width);
                byte[] data = new byte[size + 8];
                int written = RleHybridEncoder.Encode(values, width, data);
                Assert.Equal(size, written);

                uint[] read = new uint[count];
                RleHybridDecoder decoder = new(width);
                decoder.Read(data.AsSpan(0, written), read);
                Assert.Equal(values, read);
            }
        }
    }

    [Fact]
    public void ByteValuesRoundTripAsLevels()
    {
        Random random = new(8);
        for (int width = 0; width <= 8; width++)
        {
            uint[] wide = Runs(random, 5000, width);
            byte[] values = Array.ConvertAll(wide, v => (byte)v);
            int size = RleHybridEncoder.Size(values, width);
            byte[] data = new byte[size];
            Assert.Equal(size, RleHybridEncoder.Encode(values, width, data));
            Assert.Equal(size, RleHybridEncoder.Size(wide, width));

            byte[] read = new byte[values.Length];
            RleHybridDecoder decoder = new(width);
            decoder.Read(data, read);
            Assert.Equal(values, read);
        }
    }

    [Fact]
    public void WidthOneReadsStraightIntoABitmapAtAnyOffset()
    {
        Random random = new(1);
        uint[] values = Runs(random, 3000, 1);
        byte[] data = new byte[RleHybridEncoder.Size(values, 1)];
        RleHybridEncoder.Encode(values, 1, data);

        foreach (int offset in new[] { 0, 1, 5, 7, 8, 13 })
        {
            byte[] bits = new byte[(offset + values.Length + 7) / 8 + 1];
            RleHybridDecoder decoder = new(1);
            int set = 0;
            int done = 0;
            foreach (int batch in new[] { 1, 7, 64, 1000, 3000 })
            {
                int take = Math.Min(batch, values.Length - done);
                set += decoder.ReadBits(data, bits, offset + done, take);
                done += take;
            }

            int expected = 0;
            for (int i = 0; i < values.Length; i++)
            {
                bool bit = (bits[(offset + i) >> 3] & (1 << ((offset + i) & 7))) != 0;
                Assert.Equal(values[i] == 1, bit);
                expected += (int)values[i];
            }

            Assert.Equal(expected, set);
        }
    }

    [Fact]
    public void DecodingResumesAcrossBatchesOfAnySize()
    {
        Random random = new(9);
        foreach (int width in new[] { 1, 3, 8, 13, 25, 32 })
        {
            uint[] values = Runs(random, 10_000, width);
            byte[] data = new byte[RleHybridEncoder.Size(values, width)];
            RleHybridEncoder.Encode(values, width, data);

            RleHybridDecoder decoder = new(width);
            uint[] read = new uint[values.Length];
            int done = 0;
            while (done < values.Length)
            {
                int take = Math.Min(random.Next(1, 700), values.Length - done);
                if (random.Next(5) == 0)
                {
                    decoder.Skip(data, take);
                    Array.Copy(values, done, read, done, take);
                }
                else
                {
                    decoder.Read(data, read.AsSpan(done, take));
                }

                done += take;
            }

            Assert.Equal(values, read);
        }
    }

    [Fact]
    public void ALongRunOfOneValueIsOneRepeatedRun()
    {
        uint[] values = new uint[100_000];
        values.AsSpan().Fill(6);
        Assert.Equal(4, RleHybridEncoder.Size(values, 3));
    }

    [Theory]
    [InlineData(new byte[] { 0x01 })]
    [InlineData(new byte[] { 0x00, 0x05 })]
    [InlineData(new byte[] { 0x05, 0x88 })]
    [InlineData(new byte[] { 0x10 })]
    [InlineData(new byte[] { 0x80 })]
    public void AMalformedRunIsRefused(byte[] data)
    {
        RleHybridDecoder decoder = new(3);
        uint[] values = new uint[8];
        try
        {
            decoder.Read(data, values);
            Assert.Fail("A malformed run decoded.");
        }
        catch (ParquetFormatException)
        {
        }
    }

    [Fact]
    public void DataEndingBeforeItsValuesIsRefused()
    {
        uint[] values = new uint[16];
        values.AsSpan().Fill(1);
        byte[] data = new byte[RleHybridEncoder.Size(values, 2)];
        RleHybridEncoder.Encode(values, 2, data);
        RleHybridDecoder decoder = new(2);
        uint[] read = new uint[17];
        try
        {
            decoder.Read(data, read);
            Assert.Fail("Seventeen values decoded from sixteen.");
        }
        catch (ParquetFormatException)
        {
        }
    }
}
