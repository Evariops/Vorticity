using System;
using System.Buffers;
using System.IO.Compression;
using Vorticity.Zstd.Internal;
using Vorticity.Zstd.Tests.Differential;
using Vorticity.Zstd.Tests.Support;
using Xunit;

namespace Vorticity.Zstd.Tests.Compression;

/// <summary>
/// Every case of the corpus is compressed by Vorticity.Zstd and by the platform's libzstd. Wherever Vorticity.Zstd
/// implements the level's strategy, both frames must be the same bytes; whatever the level, Vorticity.Zstd's
/// frame must decode, with Vorticity.Zstd and with libzstd, to the original. The corpus is split across
/// classes so that xUnit runs them in parallel.
/// </summary>
public static class CompressionCorpus
{
    private static readonly int[] SmallSizes = [0, 1, 2, 6, 7, 8, 31, 32, 33, 64, 100, 255, 256, 1023, 1024, 4095, 4096, 16384, 16385];
    private static readonly int[] BlockSizes = [70_000, 131_071, 131_072, 131_073, 262_145, 400_000];
    private static readonly int[] ExactLevels = [-131072, -50, -5, -1, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15];
    private static readonly int[] StrongLevels = [16, 19, 22];

    public static TheoryData<string> Small() => Cases(SmallSizes, ExactLevels);

    public static TheoryData<string> Blocks() => Cases(BlockSizes, ExactLevels);

    /// <summary>The levels whose strategies cascade for now: round trips only, on smaller sources.</summary>
    public static TheoryData<string> Strong() => Cases([0, 100, 4096, 70_000, 131_073], StrongLevels);

    /// <summary>Several megabytes: many blocks, split before their match finding, and windows that slide.</summary>
    public static TheoryData<string> Large()
    {
        var data = new TheoryData<string>();
        foreach (string kind in DataKinds.All)
        {
            foreach (int level in new[] { -1, 1, 3, 4, 5, 8, 12, 15 })
            {
                data.Add(CorpusCase.Name(kind, 3_000_000, level));
            }
        }

        return data;
    }

    public static TheoryData<string> Checksums()
    {
        var data = new TheoryData<string>();
        foreach (string kind in DataKinds.All)
        {
            foreach (int size in new[] { 0, 127, 131_073 })
            {
                data.Add(CorpusCase.Name(kind, size, 1, "chk"));
                data.Add(CorpusCase.Name(kind, size, 3, "chk"));
            }
        }

        return data;
    }

    private static TheoryData<string> Cases(int[] sizes, int[] levels)
    {
        var data = new TheoryData<string>();
        foreach (string kind in DataKinds.All)
        {
            foreach (int size in sizes)
            {
                foreach (int level in levels)
                {
                    data.Add(CorpusCase.Name(kind, size, level));
                }
            }
        }

        return data;
    }

    /// <summary>Whether Vorticity.Zstd implements the strategy libzstd uses for this level and size.</summary>
    public static bool IsByteExact(int level, int size) =>
        CompressionParameters.LibzstdStrategy(level, size) <= CompressionParameters.StrongestImplemented;

    /// <summary>The platform's frame: libzstd's <c>ZSTD_compress</c> at the level, with or without a checksum.</summary>
    public static byte[] Libzstd(ReadOnlySpan<byte> data, int level, bool checksum)
    {
        byte[] output = new byte[ZstdCompressor.GetMaxCompressedLength(data.Length)];
        using var encoder = new ZstandardEncoder(new ZstandardCompressionOptions { Quality = level, AppendChecksum = checksum });
        OperationStatus status = encoder.Compress(data, output, out int consumed, out int written, isFinalBlock: true);
        Assert.Equal(OperationStatus.Done, status);
        Assert.Equal(data.Length, consumed);
        return output.AsSpan(0, written).ToArray();
    }

    /// <summary>Compresses the case both ways and checks everything the API promises about it.</summary>
    public static void Check(string name)
    {
        CorpusCase @case = CorpusCase.Parse(name);
        byte[] data = @case.Data;
        var compressor = new ZstdCompressor(@case.Level) { AppendChecksum = @case.Checksum };
        byte[] output = new byte[ZstdCompressor.GetMaxCompressedLength(data.Length)];
        OperationStatus status = compressor.Compress(data, output, out int consumed, out int written);
        Assert.Equal(OperationStatus.Done, status);
        Assert.Equal(data.Length, consumed);
        ReadOnlySpan<byte> frame = output.AsSpan(0, written);

        if (IsByteExact(@case.Level, data.Length))
        {
            Corpus.AssertSameBytes(Libzstd(data, @case.Level, @case.Checksum), frame, name);
        }

        AssertDecodes(frame, data, name);

        // An exact destination, on the same compressor, which carries its tables over.
        byte[] exact = new byte[written];
        Assert.Equal(OperationStatus.Done, compressor.Compress(data, exact, out consumed, out int again));
        Assert.Equal(written, again);
        Corpus.AssertSameBytes(frame, exact, name);

        // One byte short.
        status = compressor.Compress(data, new byte[written - 1], out consumed, out again);
        Assert.Equal(OperationStatus.DestinationTooSmall, status);
        Assert.Equal(0, consumed);
        Assert.Equal(0, again);
    }

    /// <summary>The frame decodes to <paramref name="data"/>, with Vorticity.Zstd and with the platform's libzstd.</summary>
    public static void AssertDecodes(ReadOnlySpan<byte> frame, ReadOnlySpan<byte> data, string name)
    {
        byte[] decoded = new byte[data.Length];
        var decoder = new ZstdDecompressor();
        OperationStatus status = decoder.Decompress(frame, decoded, out int consumed, out int written);
        Assert.True(status == OperationStatus.Done, $"{name}: Vorticity.Zstd's decoder says {status} ({decoder.LastError})");
        Assert.Equal(frame.Length, consumed);
        Corpus.AssertSameBytes(data, decoded.AsSpan(0, written), name);

        byte[] native = new byte[Math.Max(1, data.Length)];
        status = NativeZstd.Decompress(frame, native, null, out consumed, out written);
        Assert.True(status == OperationStatus.Done, $"{name}: libzstd says {status}");
        Assert.Equal(frame.Length, consumed);
        Corpus.AssertSameBytes(data, native.AsSpan(0, written), name);
    }
}

public sealed class SmallCompressionTests
{
    [Theory]
    [MemberData(nameof(CompressionCorpus.Small), MemberType = typeof(CompressionCorpus))]
    public void Compresses_like_libzstd(string name) => CompressionCorpus.Check(name);
}

public sealed class BlockCompressionTests
{
    [Theory]
    [MemberData(nameof(CompressionCorpus.Blocks), MemberType = typeof(CompressionCorpus))]
    public void Compresses_like_libzstd(string name) => CompressionCorpus.Check(name);
}

public sealed class LargeCompressionTests
{
    [Theory]
    [MemberData(nameof(CompressionCorpus.Large), MemberType = typeof(CompressionCorpus))]
    public void Compresses_like_libzstd(string name) => CompressionCorpus.Check(name);
}

public sealed class StrongLevelTests
{
    [Theory]
    [MemberData(nameof(CompressionCorpus.Strong), MemberType = typeof(CompressionCorpus))]
    public void Round_trips(string name) => CompressionCorpus.Check(name);
}

public sealed class ChecksumCompressionTests
{
    [Theory]
    [MemberData(nameof(CompressionCorpus.Checksums), MemberType = typeof(CompressionCorpus))]
    public void Compresses_like_libzstd(string name) => CompressionCorpus.Check(name);
}
