using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO.Compression;
using System.Linq;
using Vorticity.Zstd.Tests.Support;
using Xunit;

namespace Vorticity.Zstd.Tests.Differential;

/// <summary>
/// The start of a frame's content, decoded alone: as many bytes as the destination holds, the same
/// as the whole frame's first ones, and not one written past them. libzstd writes the frames.
/// </summary>
public sealed class PrefixDecodeTests
{
    private static readonly int[] Levels = [1, 3, 9, 19];

    private static readonly int[] Sizes = [100, 131_072, 400_000];

    /// <summary>The kinds of content, a theory row each: raw blocks, runs and short sequences among them.</summary>
    public static TheoryData<string> Kinds() => [.. DataKinds.All];

    [Theory]
    [MemberData(nameof(Kinds))]
    public void The_start_of_a_frame_is_its_content_s_start(string kind) =>
        Cases.CheckAll(
            from size in Sizes from level in Levels select (Size: size, Level: level),
            @case => CheckPrefixes(
                DataKinds.Generate(kind, @case.Size), new ZstandardCompressionOptions { Quality = @case.Level }, default),
            @case => $"{kind}, {@case.Size} bytes, level {@case.Level}");

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(19)]
    public void The_start_of_a_frame_with_a_dictionary_is_its_content_s_start(int level)
    {
        // The edges of the dictionary first, so that a prefix ends inside a match that copies from it.
        (byte[] dictionary, _) = Dictionaries.Get("json", "raw");
        byte[] data = [.. DictionaryEdgeTests.EdgeContent(dictionary), .. DataKinds.Generate("json", 200 * 1024, 9)];
        using ZstandardDictionary prepared = ZstandardDictionary.Create(dictionary, level);
        CheckPrefixes(data, new ZstandardCompressionOptions { Quality = level, Dictionary = prepared }, dictionary);
    }

    [Fact]
    public void A_frame_with_a_checksum_is_not_checked_past_the_prefix()
    {
        // The checksum covers the whole content: a prefix stops before it, a whole decode checks it.
        byte[] data = DataKinds.Generate("text", 300_000);
        byte[] frame = NativeZstd.Compress(data, new ZstandardCompressionOptions { Quality = 3, AppendChecksum = true });
        frame[^1] ^= 0xFF;
        var decompressor = new ZstdDecompressor();
        byte[] prefix = new byte[200_000];
        Assert.Equal(OperationStatus.Done, decompressor.DecompressPrefix(frame, prefix, default, out int written));
        Assert.Equal(prefix.Length, written);
        Assert.True(data.AsSpan(0, prefix.Length).SequenceEqual(prefix));

        Assert.Equal(OperationStatus.InvalidData, decompressor.DecompressPrefix(frame, new byte[data.Length], default, out _));
    }

    /// <summary>
    /// Every prefix the boundaries make interesting, and random ones, against a guard page that
    /// faults on the first byte written past the destination; each followed, on the same
    /// decompressor, by a decode of the whole frame.
    /// </summary>
    private static void CheckPrefixes(byte[] data, ZstandardCompressionOptions options, ReadOnlySpan<byte> dictionary)
    {
        byte[] frame = NativeZstd.Compress(data, options);
        var decompressor = new ZstdDecompressor();
        using var guarded = new GuardedBuffer(Math.Max(data.Length + 1, 1));
        byte[] whole = new byte[data.Length];
        foreach (int length in Lengths(data.Length))
        {
            Span<byte> destination = guarded.AtEnd(length);
            OperationStatus status = decompressor.DecompressPrefix(frame, destination, dictionary, out int written);
            Assert.True(status == OperationStatus.Done, $"a prefix of {length} bytes: {status} ({decompressor.LastError})");
            int expected = Math.Min(length, data.Length);
            Assert.Equal(expected, written);
            int differs = data.AsSpan(0, expected).CommonPrefixLength(destination[..expected]);
            Assert.True(differs == expected, $"a prefix of {length} bytes differs at byte {differs}");

            status = decompressor.Decompress(frame, whole, dictionary, out _, out written);
            Assert.True(status == OperationStatus.Done && written == data.Length && whole.AsSpan().SequenceEqual(data),
                $"the whole frame after a prefix of {length} bytes: {status} ({decompressor.LastError})");
        }
    }

    /// <summary>The prefix lengths of a content: its edges, the block's, the fast loop's margin, and some at random.</summary>
    private static IEnumerable<int> Lengths(int size)
    {
        var lengths = new SortedSet<int> { 0, 1, 7, 8, 31, 32, 33, 64, 131_071, 131_072, 131_073, size - 33, size - 32, size - 1, size, size + 1 };
        var random = new Random(size);
        for (int i = 0; i < 24; i++)
        {
            lengths.Add(random.Next(size + 1));
        }

        return lengths.Where(length => length >= 0 && length <= size + 1);
    }
}
