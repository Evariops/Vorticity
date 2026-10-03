using System;
using System.Buffers;
using System.IO.Compression;
using Vorticity.Zstd.Tests.Support;
using Xunit;

namespace Vorticity.Zstd.Tests.Differential;

/// <summary>
/// Matches at the edges of a dictionary's content, which the fast sequence loop copies 16 bytes at a
/// time: ending on its last byte or a few before, running on from it into the frame, starting on its
/// first byte. libzstd writes the frames; Vorticity.Zstd must give the content back, into a destination of
/// its exact size and into a larger one.
/// </summary>
public sealed class DictionaryEdgeTests
{
    public static TheoryData<string, int> Cases()
    {
        var cases = new TheoryData<string, int>();
        foreach (string type in new[] { "raw", "trained" })
        {
            foreach (int level in new[] { -1, 1, 3, 9, 19 })
            {
                cases.Add(type, level);
            }
        }

        return cases;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Matches_at_the_edges_of_the_dictionary_decode_like_libzstd(string type, int level)
    {
        (byte[] dictionary, _) = Dictionaries.Get("json", type);
        byte[] content = EdgeContent(dictionary);
        using ZstandardDictionary prepared = ZstandardDictionary.Create(dictionary, level);
        byte[] frame = NativeZstd.Compress(content, new ZstandardCompressionOptions { Quality = level, Dictionary = prepared });

        var decoder = new ZstdDecompressor(dictionary);
        foreach (int extra in new[] { 0, 64 })
        {
            byte[] output = new byte[content.Length + extra];
            OperationStatus status = decoder.Decompress(frame, output, out int consumed, out int written);
            Assert.Equal(OperationStatus.Done, status);
            Assert.Equal(frame.Length, consumed);
            Assert.Equal(content.Length, written);
            Corpus.AssertSameBytes(content, output.AsSpan(0, written), $"{type} L{level} +{extra}");
        }
    }

    /// <summary>
    /// The tail of the dictionary at every length up to 40, each followed by bytes it shares nothing
    /// with or by the start of the frame (a match that runs on from the dictionary into the frame);
    /// then the dictionary's first bytes.
    /// </summary>
    private static byte[] EdgeContent(byte[] dictionary)
    {
        var random = new Random(11);
        byte[] start = new byte[24];
        random.NextBytes(start);
        var content = new System.Collections.Generic.List<byte>(start);
        for (int k = 1; k <= 40; k++)
        {
            content.AddRange(dictionary.AsSpan(dictionary.Length - k).ToArray());
            if ((k & 1) == 0)
            {
                content.AddRange(start.AsSpan(0, 4 + (k % 20)).ToArray());
            }

            byte[] noise = new byte[3 + (k % 5)];
            random.NextBytes(noise);
            content.AddRange(noise);
        }

        content.AddRange(dictionary.AsSpan(0, 40).ToArray());
        byte[] tail = new byte[64];
        random.NextBytes(tail);
        content.AddRange(tail);
        return content.ToArray();
    }
}
