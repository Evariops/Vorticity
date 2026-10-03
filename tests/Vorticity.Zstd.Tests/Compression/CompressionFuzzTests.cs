using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Vorticity.Zstd.Tests.Differential;
using Vorticity.Zstd.Tests.Support;
using Xunit;

namespace Vorticity.Zstd.Tests.Compression;

/// <summary>
/// Random content, built to exercise the match finders (copies from every distance, runs, noise,
/// text), compressed at random levels by compressors that are reused from frame to frame, from
/// sources placed against inaccessible pages: any read past the source faults. Every frame must be
/// libzstd's wherever the level is implemented, and must decode to its content.
/// </summary>
/// <remarks><c>VORTICITY_ZSTD_FUZZ_ITERATIONS</c> sets the number of frames per seed (default 300).</remarks>
public sealed class CompressionFuzzTests
{
    private static int Iterations =>
        int.TryParse(Environment.GetEnvironmentVariable("VORTICITY_ZSTD_FUZZ_ITERATIONS"), CultureInfo.InvariantCulture, out int n) ? n : 300;

    public static TheoryData<int> Seeds() => [1, 2, 3, 4, 5, 6, 7, 8];

    [Theory]
    [MemberData(nameof(Seeds))]
    public void Random_content_compresses_like_libzstd(int seed)
    {
        var random = new Random(seed);
        var compressors = new Dictionary<int, ZstdCompressor>();
        using var source = new GuardedBuffer(1 << 20);
        for (int iteration = 0; iteration < Iterations; iteration++)
        {
            int size = random.Next(4) switch
            {
                0 => random.Next(64),
                1 => random.Next(4096),
                2 => random.Next(140_000),
                _ => random.Next(1 << 20),
            };
            int level = random.Next(6) switch
            {
                0 => -random.Next(1, 20),
                _ => random.Next(1, 5),
            };

            byte[] data = Generate(random, size);
            Span<byte> src = random.Next(2) == 0 ? source.AtEnd(size) : source.AtStart(size);
            data.CopyTo(src);

            if (!compressors.TryGetValue(level, out ZstdCompressor? compressor))
            {
                compressor = new ZstdCompressor(level);
                compressors.Add(level, compressor);
            }

            byte[] output = new byte[ZstdCompressor.GetMaxCompressedLength(size)];
            OperationStatus status = compressor.Compress(src, output, out int consumed, out int written);
            string name = $"seed {seed}, iteration {iteration}: {size} bytes at level {level}";
            Assert.True(status == OperationStatus.Done, name);
            Assert.Equal(size, consumed);
            if (CompressionCorpus.IsByteExact(level, size))
            {
                Corpus.AssertSameBytes(CompressionCorpus.Libzstd(data, level, checksum: false), output.AsSpan(0, written), name);
            }

            CompressionCorpus.AssertDecodes(output.AsSpan(0, written), data, name);
        }
    }

    /// <summary>
    /// Content made of segments: noise, runs of one byte, words, and copies of what precedes at short,
    /// medium and long distances, each sometimes altered by a byte or two.
    /// </summary>
    private static byte[] Generate(Random random, int size)
    {
        byte[] data = new byte[size];
        int i = 0;
        while (i < size)
        {
            int length = Math.Min(size - i, 1 + random.Next(random.Next(4) == 0 ? 2000 : 60));
            switch (random.Next(6))
            {
                case 0:
                    random.NextBytes(data.AsSpan(i, length));
                    break;
                case 1:
                    data.AsSpan(i, length).Fill((byte)random.Next(256));
                    break;
                case 2:
                    for (int k = 0; k < length; k++)
                    {
                        data[i + k] = (byte)"etaoin shrdlu"[random.Next(13)];
                    }

                    break;
                default:
                    if (i == 0)
                    {
                        goto case 0;
                    }

                    int distance = random.Next(3) switch
                    {
                        0 => 1 + random.Next(Math.Min(i, 16)),
                        1 => 1 + random.Next(Math.Min(i, 4096)),
                        _ => 1 + random.Next(i),
                    };
                    for (int k = 0; k < length; k++)
                    {
                        data[i + k] = data[i + k - distance];
                    }

                    if (random.Next(3) == 0)
                    {
                        data[i + random.Next(length)] ^= (byte)(1 + random.Next(255));
                    }

                    break;
            }

            i += length;
        }

        return data;
    }
}

/// <summary>A compressor reused for frames of every kind and size writes each one as a fresh libzstd context would.</summary>
public sealed class CompressorReuseTests
{
    public static TheoryData<int> Levels() => [-5, 1, 2, 3, 4];

    [Theory]
    [MemberData(nameof(Levels))]
    public void A_reused_compressor_writes_libzstd_frames(int level)
    {
        var compressor = new ZstdCompressor(level);
        foreach (int size in new[] { 300_000, 0, 1000, 70_000, 5, 131_073, 4096, 1_000_000, 64 })
        {
            foreach (string kind in DataKinds.All)
            {
                byte[] data = DataKinds.Generate(kind, size, 3);
                byte[] output = new byte[ZstdCompressor.GetMaxCompressedLength(size)];
                Assert.Equal(OperationStatus.Done, compressor.Compress(data, output, out _, out int written));
                string name = $"{kind}/{size}/L{level}";
                if (CompressionCorpus.IsByteExact(level, size))
                {
                    Corpus.AssertSameBytes(CompressionCorpus.Libzstd(data, level, checksum: false), output.AsSpan(0, written), name);
                }

                CompressionCorpus.AssertDecodes(output.AsSpan(0, written), data, name);
            }
        }
    }

    [Fact]
    public void Levels_are_checked()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ZstdCompressor(ZstdCompressor.MinLevel - 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ZstdCompressor(ZstdCompressor.MaxLevel + 1));
        Assert.Equal(ZstdCompressor.DefaultLevel, new ZstdCompressor(0).Level);
        Assert.Equal(ZstdCompressor.DefaultLevel, new ZstdCompressor().Level);
    }

    [Fact]
    public void The_bound_is_libzstds()
    {
        Assert.Equal(64, ZstdCompressor.GetMaxCompressedLength(0));
        Assert.Equal(1 + 63, ZstdCompressor.GetMaxCompressedLength(1));
        Assert.Equal(131_072 + 512, ZstdCompressor.GetMaxCompressedLength(131_072));
        Assert.Throws<ArgumentOutOfRangeException>(() => ZstdCompressor.GetMaxCompressedLength(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => ZstdCompressor.GetMaxCompressedLength(int.MaxValue));
    }
}

/// <summary>zstd's own golden compression inputs (tests/golden-compression at v1.5.7), at every fast level.</summary>
public sealed class GoldenCompressionTests
{
    public static TheoryData<string, int> Inputs()
    {
        var data = new TheoryData<string, int>();
        foreach (string file in Directory.GetFiles(TestData.PathOf("golden-compression")))
        {
            foreach (int level in new[] { -5, -1, 1, 2, 3, 4 })
            {
                data.Add(Path.GetFileName(file), level);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Inputs))]
    public void Compresses_like_libzstd(string file, int level)
    {
        byte[] data = TestData.Read(Path.Combine("golden-compression", file));
        var compressor = new ZstdCompressor(level);
        byte[] output = new byte[ZstdCompressor.GetMaxCompressedLength(data.Length)];
        Assert.Equal(OperationStatus.Done, compressor.Compress(data, output, out _, out int written));
        string name = $"{file} at level {level}";
        if (CompressionCorpus.IsByteExact(level, data.Length))
        {
            Corpus.AssertSameBytes(CompressionCorpus.Libzstd(data, level, checksum: false), output.AsSpan(0, written), name);
        }

        CompressionCorpus.AssertDecodes(output.AsSpan(0, written), data, name);
    }
}
