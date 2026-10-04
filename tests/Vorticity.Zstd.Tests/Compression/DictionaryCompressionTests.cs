using System;
using System.Buffers;
using System.IO.Compression;
using Vorticity.Zstd.Tests.Differential;
using Vorticity.Zstd.Tests.Support;
using Xunit;

namespace Vorticity.Zstd.Tests.Compression;

/// <summary>
/// Frames compressed with a dictionary, trained (the zstd format) or raw, against the platform's
/// libzstd given the same dictionary prepared at the same level (<c>ZSTD_createCDict</c> and
/// <c>ZSTD_CCtx_refCDict</c>): byte for byte, then decoded with Vorticity.Zstd and with libzstd. The sizes
/// cross the three ways libzstd uses a prepared dictionary: attached up to 8 to 32 KiB, copied, then
/// loaded from 128 KiB and six times the dictionary.
/// </summary>
public sealed class DictionaryCompressionTests
{
    private static readonly int[] Sizes = [0, 1, 100, 1000, 4096, 8192, 8193, 16384, 16385, 50_000, 98_000, 131_071, 131_072, 200_000, 400_000];

    private static readonly int[] Levels = [-5, -1, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22];

    public static TheoryData<string> Cases()
    {
        var data = new TheoryData<string>();
        foreach (string kind in new[] { "text", "json", "walk64", "mixed", "zeros", "urls" })
        {
            foreach (string type in new[] { "trained", "raw" })
            {
                foreach (int level in Levels)
                {
                    foreach (int size in Sizes)
                    {
                        data.Add(CorpusCase.Name(kind, size, level, "dict=" + type));
                    }
                }
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Compresses_like_libzstd(string name)
    {
        CorpusCase @case = CorpusCase.Parse(name);
        byte[] data = @case.Data;
        (byte[] dictionary, _) = @case.GetDictionary()!.Value;

        using ZstandardDictionary native = ZstandardDictionary.Create(dictionary, @case.Level);
        byte[] expected = NativeZstd.Compress(data, new ZstandardCompressionOptions { Dictionary = native });

        var compressor = new ZstdCompressor(@case.Level, dictionary);
        byte[] output = new byte[ZstdCompressor.GetMaxCompressedLength(data.Length)];
        Assert.Equal(OperationStatus.Done, compressor.Compress(data, output, out _, out int written));
        Corpus.AssertSameBytes(expected, output.AsSpan(0, written), name);

        // Decoded with the dictionary by Vorticity.Zstd: the frame is libzstd's own, so libzstd decodes it.
        byte[] decoded = new byte[data.Length];
        var decoder = new ZstdDecompressor(dictionary);
        Assert.Equal(OperationStatus.Done, decoder.Decompress(output.AsSpan(0, written), decoded, out _, out int decodedSize));
        Corpus.AssertSameBytes(data, decoded.AsSpan(0, decodedSize), name);

        // Again on the same compressor, whose tables carry over from frame to frame.
        Assert.Equal(OperationStatus.Done, compressor.Compress(data, output, out _, out int again));
        Corpus.AssertSameBytes(expected, output.AsSpan(0, again), name);
    }
}
