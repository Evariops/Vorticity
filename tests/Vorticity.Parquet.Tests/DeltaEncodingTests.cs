using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Vorticity.Parquet;
using Vorticity.Parquet.Encodings;
using Xunit;

namespace Vorticity.Parquet.Tests;

/// <summary>
/// DELTA_BINARY_PACKED, the two byte array delta encodings, BYTE_STREAM_SPLIT and the legacy
/// BIT_PACKED: the standard's own examples, then the fast paths against plain transcriptions of the
/// standard, value by value, on random inputs at the edges of every block, every width and both types.
/// </summary>
/// <remarks>
/// The transcriptions follow Encodings.md of parquet-format 2.14.0 sentence by sentence and are never
/// changed to agree with the fast paths: a disagreement is the fast path's to fix.
/// </remarks>
public sealed class DeltaEncodingTests
{
    private static readonly int[] Counts = [0, 1, 2, 7, 8, 9, 31, 32, 33, 127, 128, 129, 130, 1_023, 1_024, 1_025, 8_191, 8_192, 8_193];

    [Fact]
    public void WritesTheStandardsFirstDeltaExample()
    {
        // Encodings.md, DELTA_BINARY_PACKED, Example 1: 1, 2, 3, 4, 5; deltas 1, minimum 1, every
        // relative delta 0, so a width of 0 and no data. The example's block of 8 values is invalid
        // in a file, so the block here is the writer's 128, four miniblocks of 32.
        int[] values = [1, 2, 3, 4, 5];
        byte[] expected = [0x80, 0x01, 0x04, 0x05, 0x02, 0x02, 0x00, 0x00, 0x00, 0x00];
        Assert.Equal(expected, Encode(values));
        Assert.Equal(values, Decode32(expected, values.Length));
    }

    [Fact]
    public void WritesTheStandardsSecondDeltaExample()
    {
        // Example 2: 7, 5, 3, 1, 2, 3, 4, 5; deltas -2, -2, -2, 1, 1, 1, 1; minimum -2; relative
        // deltas 0, 0, 0, 3, 3, 3, 3 on 2 bits. The first miniblock is padded to its 32 values.
        int[] values = [7, 5, 3, 1, 2, 3, 4, 5];
        byte[] expected =
        [
            0x80, 0x01, 0x04, 0x08, 0x0E, // block 128, 4 miniblocks, 8 values, first zigzag(7)
            0x03, 0x02, 0x00, 0x00, 0x00, // minimum zigzag(-2), widths 2, 0, 0, 0
            0xC0, 0x3F, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, // 0,0,0,3 | 3,3,3,0 | padding
        ];
        Assert.Equal(expected, Encode(values));
        Assert.Equal(values, Decode32(expected, values.Length));
    }

    [Fact]
    public void WritesTheStandardsLengthExample()
    {
        // DELTA_LENGTH_BYTE_ARRAY: "Hello", "World", "Foobar", "ABCDEF" are DeltaEncoding(5, 5, 6, 6)
        // then "HelloWorldFoobarABCDEF".
        byte[] data = "HelloWorldFoobarABCDEF"u8.ToArray();
        int[] lengths = [5, 5, 6, 6];
        byte[] encoded = new byte[DeltaByteArrays.SizeLengths(lengths, data.Length)];
        Assert.Equal(encoded.Length, DeltaByteArrays.EncodeLengths(lengths, data, encoded));
        Assert.Equal(Encode(lengths).Concat(data), encoded);

        int[] decoded = new int[4];
        int start = DeltaByteArrays.DecodeLengths(encoded, decoded);
        Assert.Equal(lengths, decoded);
        Assert.Equal(data, encoded[start..]);
    }

    [Fact]
    public void WritesTheStandardsPrefixExample()
    {
        // DELTA_BYTE_ARRAY: "axis", "axle", "babble", "babyhood" are DeltaEncoding(0, 2, 0, 3),
        // DeltaEncoding(4, 2, 6, 5), then "axislebabbleyhood".
        string[] words = ["axis", "axle", "babble", "babyhood"];
        byte[] data = Encoding.ASCII.GetBytes(string.Concat(words));
        int[] lengths = words.Select(w => w.Length).ToArray();
        int[] prefixes = new int[4];
        int[] suffixes = new int[4];
        int rest = DeltaByteArrays.Prefixes(data, lengths, prefixes, suffixes);
        Assert.Equal([0, 2, 0, 3], prefixes);
        Assert.Equal([4, 2, 6, 5], suffixes);
        Assert.Equal("axislebabbleyhood".Length, rest);

        byte[] encoded = new byte[DeltaByteArrays.SizePrefixes(prefixes, suffixes, rest)];
        Assert.Equal(encoded.Length, DeltaByteArrays.EncodePrefixes(data, lengths, prefixes, suffixes, encoded));
        Assert.Equal(Encode(prefixes).Concat(Encode(suffixes)).Concat("axislebabbleyhood"u8.ToArray()), encoded);

        int[] readPrefixes = new int[4];
        int[] readSuffixes = new int[4];
        long total = DeltaByteArrays.DecodePrefixes(encoded, readPrefixes, readSuffixes, out int suffixStart);
        byte[] heap = new byte[total];
        int[] readLengths = new int[4];
        DeltaByteArrays.Rebuild(encoded.AsSpan(suffixStart), readPrefixes, readSuffixes, heap, readLengths);
        Assert.Equal(lengths, readLengths);
        Assert.Equal(data, heap);
    }

    [Fact]
    public void WritesTheStandardsStreamSplitExample()
    {
        // BYTE_STREAM_SPLIT: three floats AA BB CC DD, 00 11 22 33, A3 B4 C5 D6 become
        // AA 00 A3 BB 11 B4 CC 22 C5 DD 33 D6.
        byte[] values = [0xAA, 0xBB, 0xCC, 0xDD, 0x00, 0x11, 0x22, 0x33, 0xA3, 0xB4, 0xC5, 0xD6];
        byte[] split = [0xAA, 0x00, 0xA3, 0xBB, 0x11, 0xB4, 0xCC, 0x22, 0xC5, 0xDD, 0x33, 0xD6];
        byte[] encoded = new byte[12];
        ByteStreamSplit.Encode(values, 4, encoded);
        Assert.Equal(split, encoded);
        byte[] decoded = new byte[12];
        ByteStreamSplit.Decode(split, 4, decoded);
        Assert.Equal(values, decoded);
    }

    [Fact]
    public void ReadsTheStandardsLegacyPackingExample()
    {
        // BIT_PACKED packs from each byte's most significant bit: 0 through 7 at width 3 are
        // 00000101 00111001 01110111. At width 1, the levels 1, 0, 1, 1, 0, 0, 0, 1, 1 are
        // 10110001 1-------.
        byte[] packed = [0b1011_0001, 0b1000_0000];
        byte[] bits = new byte[2];
        Assert.Equal(5, LegacyBitPacked.ReadBits(packed, 9, bits));
        Assert.Equal([0b1000_1101, 0b0000_0001], bits);
    }

    public static IEnumerable<object[]> Shapes() =>
        from count in Counts
        from shape in new[] { 0, 1, 2, 3, 4, 5 }
        select new object[] { count, shape };

    [Theory]
    [MemberData(nameof(Shapes))]
    public void EncodesAndDecodesInt32AsTheStandardSays(int count, int shape)
    {
        Random random = new((count * 7) + shape);
        int[] values = new int[count];
        for (int i = 0; i < count; i++)
        {
            values[i] = shape switch
            {
                0 => 42,                                            // constant: widths of 0
                1 => i * 3 + random.Next(4),                        // climbing: narrow deltas
                2 => random.Next(),                                 // random
                3 => i % 2 == 0 ? int.MinValue : int.MaxValue,      // wrapping at both ends
                4 => random.Next(-1_000, 1_000),                    // noise around zero
                _ => (int)(random.NextInt64() >> random.Next(1, 33)), // every width
            };
        }

        byte[] encoded = Encode(values);
        Assert.Equal(Reference.Encode(values.Select(v => (long)v).ToArray(), 32), encoded);
        Assert.Equal(values, Decode32(encoded, count));
        Assert.Equal(values.Select(v => (long)v), Reference.Decode(encoded, 32));
    }

    [Theory]
    [MemberData(nameof(Shapes))]
    public void EncodesAndDecodesInt64AsTheStandardSays(int count, int shape)
    {
        Random random = new((count * 11) + shape);
        long[] values = new long[count];
        for (int i = 0; i < count; i++)
        {
            values[i] = shape switch
            {
                0 => -7,
                1 => 1_700_000_000_000L + (i * 1_000L) + random.Next(10),
                2 => random.NextInt64(long.MinValue, long.MaxValue),
                3 => i % 2 == 0 ? long.MinValue : long.MaxValue,
                4 => random.Next(-1_000, 1_000),
                _ => random.NextInt64() >> random.Next(1, 64),
            };
        }

        byte[] encoded = new byte[DeltaBinaryPacked.Size64(values)];
        Assert.Equal(encoded.Length, DeltaBinaryPacked.Encode64(values, encoded));
        Assert.Equal(Reference.Encode(values, 64), encoded);
        long[] decoded = new long[count];
        Assert.Equal(encoded.Length, DeltaBinaryPacked.Decode64(encoded, decoded));
        Assert.Equal(values, decoded);
        Assert.Equal(values, Reference.Decode(encoded, 64));
    }

    [Fact]
    public void ReadsBlocksAndMiniblocksOfOtherShapes()
    {
        // Other writers cut other blocks: 256 values in 2 miniblocks, 128 in 1. A reader takes any
        // shape the standard allows, the bytes of unused miniblocks' widths included.
        Random random = new(5);
        long[] values = Enumerable.Range(0, 1_000).Select(_ => random.NextInt64(-1_000_000, 1_000_000)).ToArray();
        foreach ((int block, int miniblocks) in new[] { (256, 2), (128, 1), (512, 16) })
        {
            byte[] encoded = Reference.Encode(values, 64, block, miniblocks);
            long[] decoded = new long[values.Length];
            Assert.Equal(encoded.Length, DeltaBinaryPacked.Decode64(encoded, decoded));
            Assert.Equal(values, decoded);
        }
    }

    [Fact]
    public void RefusesWhatTheStandardDoesNotAllow()
    {
        int[] values = new int[300];
        byte[] encoded = Encode(values);

        // A block of 100 values, miniblocks of 24, a width past the type, a count other than the page's, data cut short.
        Assert.Throws<ParquetFormatException>(() => Decode32([100, 1, 1, 0], 1));
        Assert.Throws<ParquetFormatException>(() => Decode32([0x80, 0x01, 0x05, 0x01, 0x00], 1));
        Assert.Throws<ParquetFormatException>(() => Decode32([0x80, 0x01, 0x04, 0x02, 0x00, 0x00, 33, 0, 0, 0], 2));
        Assert.Throws<ParquetFormatException>(() => Decode32(encoded, 299));
        Assert.Throws<ParquetFormatException>(() => Decode32(Reference.Encode(Enumerable.Range(0, 300).Select(i => (long)i * i).ToArray(), 32)[..20], 300));
    }

    [Theory]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(2)]
    [InlineData(12)]
    [InlineData(16)]
    public void SplitsAndGathersStreamsAtEveryWidth(int width)
    {
        foreach (int count in Counts)
        {
            Random random = new(count + width);
            byte[] values = new byte[count * width];
            random.NextBytes(values);
            byte[] split = new byte[values.Length];
            ByteStreamSplit.Encode(values, width, split);
            for (int i = 0; i < count; i++)
            {
                for (int k = 0; k < width; k++)
                {
                    Assert.Equal(values[(i * width) + k], split[(k * count) + i]);
                }
            }

            byte[] gathered = new byte[values.Length];
            ByteStreamSplit.Decode(split, width, gathered);
            Assert.Equal(values, gathered);
        }
    }

    [Fact]
    public void RebuildsFrontCodedValuesOfEveryShape()
    {
        Random random = new(9);
        foreach (int count in Counts)
        {
            // Sorted words share prefixes; random bytes share few; empty values share none.
            List<byte[]> words = [];
            for (int i = 0; i < count; i++)
            {
                byte[] word = new byte[random.Next(0, 20)];
                random.NextBytes(word);
                words.Add(i % 5 == 0 ? [] : word);
            }

            words.Sort((a, b) => a.AsSpan().SequenceCompareTo(b));
            byte[] data = words.SelectMany(w => w).ToArray();
            int[] lengths = words.Select(w => w.Length).ToArray();
            int[] prefixes = new int[count];
            int[] suffixes = new int[count];
            int rest = DeltaByteArrays.Prefixes(data, lengths, prefixes, suffixes);
            byte[] encoded = new byte[DeltaByteArrays.SizePrefixes(prefixes, suffixes, rest)];
            DeltaByteArrays.EncodePrefixes(data, lengths, prefixes, suffixes, encoded);

            int[] readPrefixes = new int[count];
            int[] readSuffixes = new int[count];
            long total = DeltaByteArrays.DecodePrefixes(encoded, readPrefixes, readSuffixes, out int start);
            byte[] heap = new byte[total];
            int[] readLengths = new int[count];
            DeltaByteArrays.Rebuild(encoded.AsSpan(start), readPrefixes, readSuffixes, heap, readLengths);
            Assert.Equal(lengths, readLengths);
            Assert.Equal(data, heap);
        }
    }

    private static byte[] Encode(int[] values)
    {
        byte[] encoded = new byte[DeltaBinaryPacked.Size32(values)];
        Assert.Equal(encoded.Length, DeltaBinaryPacked.Encode32(values, encoded));
        return encoded;
    }

    private static int[] Decode32(byte[] encoded, int count)
    {
        int[] values = new int[count];
        Assert.Equal(encoded.Length, DeltaBinaryPacked.Decode32(encoded, values));
        return values;
    }

    /// <summary>DELTA_BINARY_PACKED as Encodings.md states it, a value and a bit at a time.</summary>
    private static class Reference
    {
        internal static byte[] Encode(long[] values, int bits, int block = 128, int miniblocks = 4)
        {
            List<byte> output = [];
            Uleb(output, (ulong)block);
            Uleb(output, (ulong)miniblocks);
            Uleb(output, (ulong)values.Length);
            Uleb(output, ZigZag(values.Length == 0 ? 0 : values[0], bits));
            int perMiniblock = block / miniblocks;
            for (int start = 1; start < values.Length; start += block)
            {
                int count = Math.Min(block, values.Length - start);

                // 1. the differences between consecutive elements, wrapping in the type;
                long[] deltas = new long[count];
                for (int i = 0; i < count; i++)
                {
                    deltas[i] = Wrap(values[start + i] - values[start + i - 1], bits);
                }

                // 2. the minimum, subtracted from every delta;
                long minimum = deltas.Min();
                ulong[] relative = deltas.Select(d => Unsigned(d - minimum, bits)).ToArray();

                // 3. the minimum, the widths, then the deltas bit-packed per miniblock.
                Uleb(output, ZigZag(minimum, bits));
                int[] widths = new int[miniblocks];
                for (int m = 0; m < miniblocks; m++)
                {
                    ulong max = 0;
                    for (int i = m * perMiniblock; i < Math.Min(count, (m + 1) * perMiniblock); i++)
                    {
                        max = Math.Max(max, relative[i]);
                    }

                    widths[m] = max == 0 ? 0 : 64 - System.Numerics.BitOperations.LeadingZeroCount(max);
                    output.Add((byte)widths[m]);
                }

                for (int m = 0; m * perMiniblock < count; m++)
                {
                    ulong accumulator = 0;
                    int filled = 0;
                    for (int i = 0; i < perMiniblock; i++)
                    {
                        int index = (m * perMiniblock) + i;
                        ulong value = index < count ? relative[index] : 0;
                        for (int b = 0; b < widths[m]; b++)
                        {
                            accumulator |= ((value >> b) & 1) << filled;
                            if (++filled == 8)
                            {
                                output.Add((byte)accumulator);
                                accumulator = 0;
                                filled = 0;
                            }
                        }
                    }
                }
            }

            return [.. output];
        }

        internal static long[] Decode(byte[] data, int bits)
        {
            int position = 0;
            int block = (int)ReadUleb(data, ref position);
            int miniblocks = (int)ReadUleb(data, ref position);
            int count = (int)ReadUleb(data, ref position);
            long previous = UnZigZag(ReadUleb(data, ref position));
            List<long> values = count > 0 ? [Wrap(previous, bits)] : [];
            int perMiniblock = block / miniblocks;
            while (values.Count < count)
            {
                long minimum = UnZigZag(ReadUleb(data, ref position));
                int[] widths = new int[miniblocks];
                for (int m = 0; m < miniblocks; m++)
                {
                    widths[m] = data[position++];
                }

                for (int m = 0; m < miniblocks && values.Count < count; m++)
                {
                    long bitPosition = (long)position * 8;
                    for (int i = 0; i < perMiniblock; i++)
                    {
                        ulong delta = 0;
                        for (int b = 0; b < widths[m]; b++, bitPosition++)
                        {
                            delta |= (ulong)((data[bitPosition >> 3] >> (int)(bitPosition & 7)) & 1) << b;
                        }

                        if (values.Count < count)
                        {
                            previous = Wrap(previous + minimum + (long)delta, bits);
                            values.Add(previous);
                        }
                    }

                    position += perMiniblock * widths[m] / 8;
                }
            }

            return [.. values];
        }

        private static long Wrap(long value, int bits) => bits == 32 ? (int)value : value;

        private static ulong Unsigned(long value, int bits) => bits == 32 ? (uint)(int)value : (ulong)value;

        private static ulong ZigZag(long value, int bits) => bits == 32
            ? (uint)(((int)value << 1) ^ ((int)value >> 31))
            : (ulong)((value << 1) ^ (value >> 63));

        private static long UnZigZag(ulong value) => (long)(value >> 1) ^ -(long)(value & 1);

        private static void Uleb(List<byte> output, ulong value)
        {
            while (value >= 0x80)
            {
                output.Add((byte)(value | 0x80));
                value >>= 7;
            }

            output.Add((byte)value);
        }

        private static ulong ReadUleb(byte[] data, ref int position)
        {
            ulong value = 0;
            for (int shift = 0; ; shift += 7)
            {
                byte b = data[position++];
                value |= (ulong)(b & 0x7F) << shift;
                if ((b & 0x80) == 0)
                {
                    return value;
                }
            }
        }
    }
}
