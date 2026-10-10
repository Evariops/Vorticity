using System;
using Vorticity.Parquet;
using Vorticity.Parquet.Codecs;
using Vorticity.Parquet.Metadata;
using Vorticity.Zstd;
using Xunit;

namespace Vorticity.Parquet.Tests;

/// <summary>
/// The page codecs: every codec this library writes read back over data of every shape and around
/// the block boundaries, hand-built Snappy and LZ4 streams with overlapping copies, corrupt streams
/// refused, and the codecs the standard gives no format for refused as unsupported.
/// </summary>
public sealed class PageCodecTests
{
    private static readonly CompressionCodec[] Written =
    [
        CompressionCodec.Uncompressed,
        CompressionCodec.Snappy,
        CompressionCodec.Lz4Raw,
        CompressionCodec.Zstd,
        CompressionCodec.Gzip,
        CompressionCodec.Brotli,
    ];

    private static byte[] Shaped(Random random, int length, int shape)
    {
        byte[] data = new byte[length];
        switch (shape)
        {
            case 0:
                random.NextBytes(data);
                break;
            case 1:
                for (int i = 0; i < length; i++)
                {
                    data[i] = (byte)(i / random.Next(1, 300 + 1));
                }

                break;
            case 2:
                byte[] words = "the quick brown fox jumps over the lazy dog, Paris 2026 "u8.ToArray();
                for (int i = 0; i < length; i++)
                {
                    data[i] = random.Next(10) == 0 ? (byte)random.Next(256) : words[(i * 7 / 3) % words.Length];
                }

                break;
            default:
                // Short periods: copies whose source overlaps what they write.
                int period = random.Next(1, 17);
                for (int i = 0; i < length; i++)
                {
                    data[i] = (byte)(i % period);
                }

                break;
        }

        return data;
    }

    [Fact]
    public void EveryWrittenCodecReadsBackEveryShapeAndLength()
    {
        Random random = new(5);
        ZstdCompressor zstdOut = new(3);
        ZstdDecompressor zstdIn = new();
        foreach (int length in new[] { 0, 1, 15, 16, 17, 100, 4096, 65_535, 65_536, 65_537, 300_000 })
        {
            for (int shape = 0; shape < 4; shape++)
            {
                byte[] data = Shaped(random, length, shape);
                foreach (CompressionCodec codec in Written)
                {
                    byte[] compressed = new byte[PageCodecs.MaxCompressedLength(codec, length)];
                    int size = PageCodecs.Compress(codec, 3, data, compressed, zstdOut);
                    byte[] read = new byte[length];
                    PageCodecs.Decompress(codec, compressed.AsSpan(0, size), read, zstdIn);
                    Assert.True(data.AsSpan().SequenceEqual(read), $"{codec}, length {length}, shape {shape}");
                }
            }
        }
    }

    [Fact]
    public void ACompressibleRunShrinks()
    {
        byte[] data = new byte[1 << 20];
        foreach (CompressionCodec codec in new[] { CompressionCodec.Snappy, CompressionCodec.Lz4Raw })
        {
            byte[] compressed = new byte[PageCodecs.MaxCompressedLength(codec, data.Length)];
            int size = PageCodecs.Compress(codec, 0, data, compressed, null);
            Assert.True(size < data.Length / 20, $"{codec} took a megabyte of zeros to {size} bytes.");
        }
    }

    [Fact]
    public void AHandBuiltSnappyStreamWithAnOverlappingCopyDecodes()
    {
        // 12 bytes; the literal "abc"; a copy of 9 bytes from 3 back, its source overlapping itself.
        byte[] stream = [0x0C, 0x08, 0x61, 0x62, 0x63, 0x15, 0x03];
        byte[] read = new byte[12];
        Snappy.Decompress(stream, read);
        Assert.Equal("abcabcabcabc"u8.ToArray(), read);
    }

    [Fact]
    public void AHandBuiltLz4BlockWithAnOverlappingMatchDecodes()
    {
        // A token of three literals and a match of nine, "abc", the offset 3, then the last
        // sequence, which holds no literal.
        byte[] block = [0x35, 0x61, 0x62, 0x63, 0x03, 0x00, 0x00];
        byte[] read = new byte[12];
        Lz4Block.Decompress(block, read);
        Assert.Equal("abcabcabcabc"u8.ToArray(), read);
    }

    [Theory]
    [InlineData(new byte[] { 0x04, 0x08, 0x61, 0x62, 0x63, 0x05, 0x00 })]
    [InlineData(new byte[] { 0x0C, 0x08, 0x61, 0x62, 0x63, 0x15, 0x04 })]
    [InlineData(new byte[] { 0x0C, 0x08, 0x61, 0x62 })]
    [InlineData(new byte[] { 0x05, 0x08, 0x61, 0x62, 0x63 })]
    public void ACorruptSnappyStreamIsRefused(byte[] stream)
    {
        byte[] read = new byte[Math.Max(Snappy.UncompressedLength(stream), 0)];
        Assert.Throws<ParquetFormatException>(() => Snappy.Decompress(stream, read));
    }

    [Fact]
    public void ASnappyStreamPastAnEmptyDestinationIsRefused()
    {
        // A stream that declares no byte, then eighty bytes of literals: the destination is empty, a
        // null pointer, whose room the decoder's fast elements must measure rather than take a limit off.
        byte[] stream = new byte[1 + (5 * 17)];
        for (int i = 1; i < stream.Length; i += 17)
        {
            stream[i] = 0x3C;
        }

        Assert.Throws<ParquetFormatException>(() => Snappy.Decompress(stream, Span<byte>.Empty));
    }

    [Theory]
    [InlineData(new byte[] { 0x35, 0x61, 0x62, 0x63, 0x00, 0x00, 0x00 })]
    [InlineData(new byte[] { 0x35, 0x61, 0x62, 0x63, 0x04, 0x00, 0x00 })]
    [InlineData(new byte[] { 0x35, 0x61, 0x62 })]
    [InlineData(new byte[] { 0x35, 0x61, 0x62, 0x63, 0x03, 0x00 })]
    public void ACorruptLz4BlockIsRefused(byte[] block) =>
        Assert.Throws<ParquetFormatException>(() => Lz4Block.Decompress(block, new byte[12]));

    [Theory]
    [InlineData(CompressionCodec.Snappy)]
    [InlineData(CompressionCodec.Lz4Raw)]
    [InlineData(CompressionCodec.Gzip)]
    internal void AMutatedStreamIsReadOrRefusedIntoADestinationOfAnySize(CompressionCodec codec)
    {
        // This package's own decoders, each stream mutated a few bytes and read into a destination
        // of the edge sizes, empty among them, as a page header that lies would give it.
        Random random = new((int)codec * 41);
        for (int trial = 0; trial < 2_000; trial++)
        {
            byte[] data = Shaped(random, random.Next(0, 3_000), random.Next(4));
            byte[] compressed = new byte[PageCodecs.MaxCompressedLength(codec, data.Length)];
            int size = PageCodecs.Compress(codec, 1, data, compressed, null);
            byte[] stream = compressed.AsSpan(0, size).ToArray();
            for (int e = random.Next(0, 3); e > 0; e--)
            {
                int at = random.Next(stream.Length);
                stream[at] = random.Next(2) == 0 ? (byte)random.Next(256) : (byte)(stream[at] ^ (1 << random.Next(8)));
            }

            int length = random.Next(4) == 0 ? random.Next(stream.Length + 1) : stream.Length;
            int[] sizes = [0, 1, Math.Max(0, data.Length - 1), data.Length, data.Length + 1, random.Next(data.Length + 200)];
            foreach (int target in sizes)
            {
                try
                {
                    PageCodecs.Decompress(codec, stream.AsSpan(0, length), new byte[target], null);
                }
                catch (ParquetFormatException)
                {
                }
            }
        }
    }

    [Theory]
    [InlineData((int)CompressionCodec.Lzo)]
    [InlineData((int)CompressionCodec.Lz4)]
    [InlineData(99)]
    public void ACodecWithoutAFormatIsUnsupported(int codec)
    {
        ParquetUnsupportedException refused = Assert.Throws<ParquetUnsupportedException>(
            () => PageCodecs.Decompress((CompressionCodec)codec, [1, 2, 3], new byte[3], null));
        Assert.Equal(ParquetComponentKind.Codec, refused.Kind);
    }

    [Fact]
    public void EveryKindOfSnappyElementDecodesAsItsFormatSays()
    {
        // Streams of every element another encoder writes: literals short and long, copies with a
        // one-byte offset and three bits in the tag, two-byte and four-byte offsets, offsets under
        // sixteen whose copies repeat themselves, read back as a byte at a time reads them; then
        // each broken a byte at a time, which only fails as a format.
        Random random = new(53);
        for (int round = 0; round < 400; round++)
        {
            (byte[] stream, byte[] expected) = SnappyStream(random, random.Next(1, 120));
            byte[] read = new byte[expected.Length];
            Snappy.Decompress(stream, read);
            Assert.True(expected.AsSpan().SequenceEqual(read), $"round {round}");

            for (int mutation = 0; mutation < 8; mutation++)
            {
                byte[] broken = (byte[])stream.Clone();
                broken[random.Next(broken.Length)] ^= (byte)random.Next(1, 256);
                try
                {
                    Snappy.Decompress(broken, new byte[expected.Length]);
                }
                catch (ParquetFormatException)
                {
                }
            }
        }
    }

    /// <summary>A valid stream of <paramref name="elements"/> elements of every kind, and the bytes it decodes to.</summary>
    private static (byte[] Stream, byte[] Expected) SnappyStream(Random random, int elements)
    {
        System.Collections.Generic.List<byte> body = [];
        System.Collections.Generic.List<byte> output = [];
        for (int e = 0; e < elements; e++)
        {
            int kind = output.Count == 0 ? 0 : random.Next(4);
            if (kind == 0)
            {
                int length = random.Next(4) == 0 ? random.Next(61, 300) : random.Next(1, 61);
                if (length <= 60)
                {
                    body.Add((byte)((length - 1) << 2));
                }
                else
                {
                    body.Add(61 << 2);
                    body.Add((byte)((length - 1) & 0xFF));
                    body.Add((byte)((length - 1) >> 8));
                }

                for (int i = 0; i < length; i++)
                {
                    byte value = (byte)random.Next(256);
                    body.Add(value);
                    output.Add(value);
                }

                continue;
            }

            // A copy: its offset short most of the time, so that it repeats what it writes.
            int most = kind == 1 ? Math.Min(output.Count, 2047) : output.Count;
            int offset = random.Next(3) == 0 ? random.Next(1, Math.Min(most, 15) + 1) : random.Next(1, most + 1);
            int count = kind == 1 ? random.Next(4, 12) : random.Next(1, 65);
            switch (kind)
            {
                case 1:
                    body.Add((byte)(1 | ((count - 4) << 2) | ((offset >> 8) << 5)));
                    body.Add((byte)offset);
                    break;
                case 2:
                    body.Add((byte)(2 | ((count - 1) << 2)));
                    body.Add((byte)offset);
                    body.Add((byte)(offset >> 8));
                    break;
                default:
                    body.Add((byte)(3 | ((count - 1) << 2)));
                    body.AddRange(BitConverter.GetBytes(offset));
                    break;
            }

            for (int i = 0; i < count; i++)
            {
                output.Add(output[output.Count - offset]);
            }
        }

        System.Collections.Generic.List<byte> stream = [];
        for (uint length = (uint)output.Count; ; length >>= 7)
        {
            if (length < 0x80)
            {
                stream.Add((byte)length);
                break;
            }

            stream.Add((byte)(length | 0x80));
        }

        stream.AddRange(body);
        return ([.. stream], [.. output]);
    }

    [Fact]
    public void RandomStreamsFailOnlyWithAFormatException()
    {
        Random random = new(11);
        ZstdDecompressor zstd = new();
        byte[] buffer = new byte[200];
        for (int round = 0; round < 10_000; round++)
        {
            int length = random.Next(buffer.Length);
            random.NextBytes(buffer.AsSpan(0, length));
            byte[] input = buffer.AsSpan(0, length).ToArray();
            CompressionCodec codec = Written[1 + (round % (Written.Length - 1))];
            try
            {
                PageCodecs.Decompress(codec, input, new byte[random.Next(0, 400)], zstd);
            }
            catch (ParquetFormatException)
            {
            }
        }
    }
}
