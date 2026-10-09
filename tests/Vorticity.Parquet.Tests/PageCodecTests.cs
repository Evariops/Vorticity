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

    [Theory]
    [InlineData(new byte[] { 0x35, 0x61, 0x62, 0x63, 0x00, 0x00, 0x00 })]
    [InlineData(new byte[] { 0x35, 0x61, 0x62, 0x63, 0x04, 0x00, 0x00 })]
    [InlineData(new byte[] { 0x35, 0x61, 0x62 })]
    [InlineData(new byte[] { 0x35, 0x61, 0x62, 0x63, 0x03, 0x00 })]
    public void ACorruptLz4BlockIsRefused(byte[] block) =>
        Assert.Throws<ParquetFormatException>(() => Lz4Block.Decompress(block, new byte[12]));

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
