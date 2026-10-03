using System;
using System.Buffers;
using System.IO.Compression;
using Vorticity.Zstd.Internal;
using Vorticity.Zstd.Tests.Differential;
using Vorticity.Zstd.Tests.Support;
using Xunit;

namespace Vorticity.Zstd.Tests.Compression;

/// <summary>
/// libzstd turns its long-distance matcher on by itself only at level 22 for sources over 64 MiB; asked
/// for explicitly, it runs at any size. The matcher is compared that way, on sources of a few megabytes,
/// at the optimal parsers' levels.
/// </summary>
public sealed class LongDistanceCompressionTests
{
    private static readonly int[] Levels = [16, 17, 18, 19, 22];

    public static TheoryData<string> Cases()
    {
        var data = new TheoryData<string>();
        foreach (string kind in DataKinds.All)
        {
            foreach (int level in Levels)
            {
                data.Add(CorpusCase.Name(kind, 1_500_000, level));
            }
        }

        foreach (string kind in FarRepeats.Kinds)
        {
            foreach (int level in Levels)
            {
                data.Add(CorpusCase.Name(kind, 3_000_000, level));
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Compresses_like_libzstd(string name)
    {
        CorpusCase @case = CorpusCase.Parse(name);
        byte[] data = FarRepeats.Generate(@case.Kind, @case.Size);
        byte[] expected = Libzstd(data, @case.Level, longDistance: true);

        var compressor = new ZstdCompressor(@case.Level) { LongDistanceMatching = true };
        byte[] output = new byte[ZstdCompressor.GetMaxCompressedLength(data.Length)];
        Assert.Equal(OperationStatus.Done, compressor.Compress(data, output, out _, out int written));
        Corpus.AssertSameBytes(expected, output.AsSpan(0, written), name);
        CompressionCorpus.AssertDecodes(output.AsSpan(0, written), data, name);

        // A second frame on the same compressor, whose tables carry over but whose matcher starts afresh.
        Assert.Equal(OperationStatus.Done, compressor.Compress(data, output, out _, out int again));
        Corpus.AssertSameBytes(expected, output.AsSpan(0, again), name);
    }

    /// <summary>The cases must exercise the matcher: libzstd's frames change with it, for most of them.</summary>
    [Fact]
    public void The_matcher_changes_the_frames()
    {
        int changed = 0;
        foreach (string kind in FarRepeats.Kinds)
        {
            byte[] data = FarRepeats.Generate(kind, 3_000_000);
            foreach (int level in Levels)
            {
                changed += Libzstd(data, level, longDistance: true).AsSpan().SequenceEqual(Libzstd(data, level, longDistance: false)) ? 0 : 1;
            }
        }

        Assert.True(changed >= FarRepeats.Kinds.Length * Levels.Length / 2, $"only {changed} frames changed");
    }

    /// <summary>
    /// Level 22 over 64 MiB: libzstd's window reaches 128 MiB and its matcher turns itself on; the
    /// frames are the same with no switch on either side.
    /// </summary>
    [Fact]
    public void Turns_itself_on_like_libzstd()
    {
        const int Size = (64 << 20) + 300_000;
        Assert.True(LongDistanceMatcher.EnabledFor(CompressionParameters.ForFrame(22, Size)));
        Assert.False(LongDistanceMatcher.EnabledFor(CompressionParameters.ForFrame(22, 64 << 20)));
        Assert.False(LongDistanceMatcher.EnabledFor(CompressionParameters.ForFrame(21, Size)));

        byte[] data = FarRepeats.Generate("far-walk64", Size);
        byte[] expected = Libzstd(data, 22, longDistance: false);
        byte[] output = new byte[ZstdCompressor.GetMaxCompressedLength(data.Length)];
        Assert.Equal(OperationStatus.Done, new ZstdCompressor(22).Compress(data, output, out _, out int written));
        Corpus.AssertSameBytes(expected, output.AsSpan(0, written), "far-walk64/" + Size + "/L22");
        CompressionCorpus.AssertDecodes(output.AsSpan(0, written), data, "far-walk64/" + Size + "/L22");
    }

    /// <summary>The platform's frame, with or without its long-distance matcher.</summary>
    private static byte[] Libzstd(ReadOnlySpan<byte> data, int level, bool longDistance)
    {
        byte[] output = new byte[ZstdCompressor.GetMaxCompressedLength(data.Length)];
        using var encoder = new ZstandardEncoder(new ZstandardCompressionOptions { Quality = level, EnableLongDistanceMatching = longDistance });
        Assert.Equal(OperationStatus.Done, encoder.Compress(data, output, out int consumed, out int written, isFinalBlock: true));
        Assert.Equal(data.Length, consumed);
        return output.AsSpan(0, written).ToArray();
    }

    /// <summary>
    /// Sources whose matches lie far back and run long: copies of a stretch of content, slightly edited,
    /// between other content. The binary trees skip positions inside long matches, which the
    /// long-distance matcher's table still holds.
    /// </summary>
    private static class FarRepeats
    {
        public static readonly string[] Kinds = ["far-text", "far-json", "far-random", "far-walk64"];

        public static byte[] Generate(string kind, int size)
        {
            if (!kind.StartsWith("far-", StringComparison.Ordinal))
            {
                return DataKinds.Generate(kind, size);
            }

            string content = kind.Substring(4) == "random" ? "random64" : kind.Substring(4);
            var random = new Random(size ^ kind.Length);
            byte[] stretch = DataKinds.Generate(content, size / 6, seed: 2);
            byte[] data = new byte[size];
            int position = 0;
            int copy = 0;
            while (position < size)
            {
                // Other content, then a copy of the stretch with an edit every few kilobytes.
                byte[] filler = DataKinds.Generate("text", random.Next(1_000, 100_000), seed: 3 + copy);
                int length = Math.Min(filler.Length, size - position);
                filler.AsSpan(0, length).CopyTo(data.AsSpan(position));
                position += length;

                length = Math.Min(stretch.Length, size - position);
                stretch.AsSpan(0, length).CopyTo(data.AsSpan(position));
                for (int edit = random.Next(500, 5_000); edit < length; edit += random.Next(500, 5_000))
                {
                    data[position + edit] = (byte)random.Next(256);
                }

                position += length;
                copy++;
            }

            return data;
        }
    }
}
