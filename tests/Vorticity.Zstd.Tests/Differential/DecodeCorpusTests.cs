using System;
using System.Buffers;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using Vorticity.Zstd.Tests.Support;
using Xunit;

namespace Vorticity.Zstd.Tests.Differential;

/// <summary>
/// Frames from zstd's decodecorpus (tools/native-ref/testdata.sh): random but valid, and built to reach
/// the modes an encoder seldom emits. Each must decode to the original its manifest describes, and to
/// what the platform's libzstd decodes.
/// </summary>
public sealed class DecodeCorpusTests
{
    public static TheoryData<string> Frames()
    {
        var data = new TheoryData<string>();
        if (!TestData.Has("decodecorpus"))
        {
            data.Add(TestData.Missing);
            return data;
        }

        foreach (string set in new[] { "small", "plain", "large", "dict" })
        {
            foreach (string line in File.ReadAllLines(TestData.PathOf(Path.Combine("decodecorpus", set, "manifest.txt"))))
            {
                data.Add(set + "/" + line);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Frames))]
    public void Decodes_to_the_original(string entry)
    {
        TestData.Require("decodecorpus");

        // "set/name size sha256"
        string[] parts = entry.Split(' ');
        string set = parts[0][..parts[0].IndexOf('/', StringComparison.Ordinal)];
        string name = parts[0][(set.Length + 1)..];
        int size = int.Parse(parts[1], CultureInfo.InvariantCulture);
        string sha256 = parts[2];

        byte[] frame = TestData.Read(Path.Combine("decodecorpus", set, name));
        byte[]? dictionary = set == "dict" ? TestData.Read(Path.Combine("decodecorpus", set, "dictionary")) : null;

        var decoder = dictionary is null ? new ZstdDecompressor() : new ZstdDecompressor(dictionary);
        byte[] output = new byte[Math.Max(1, size)];
        OperationStatus status = decoder.Decompress(frame, output, out int consumed, out int written);
        Assert.True(status == OperationStatus.Done, $"{entry}: {status} ({decoder.LastError})");
        Assert.Equal(frame.Length, consumed);
        Assert.Equal(size, written);
        Assert.Equal(sha256, Convert.ToHexStringLower(SHA256.HashData(output.AsSpan(0, written))));

        using ZstandardDictionary? native = dictionary is null ? null : ZstandardDictionary.Create(dictionary);
        byte[] expected = new byte[Math.Max(1, size)];
        Assert.Equal(OperationStatus.Done, NativeZstd.Decompress(frame, expected, native, out _, out int nativeWritten));
        Assert.Equal(size, nativeWritten);
        Corpus.AssertSameBytes(expected.AsSpan(0, size), output.AsSpan(0, size), entry);
    }
}
