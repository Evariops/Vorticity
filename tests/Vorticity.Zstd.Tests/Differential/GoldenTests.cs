using System;
using System.Buffers;
using System.IO;
using System.IO.Compression;
using System.Linq;
using Vorticity.Zstd.Internal;
using Vorticity.Zstd.Tests.Support;
using Xunit;

namespace Vorticity.Zstd.Tests.Differential;

/// <summary>
/// zstd's own golden files (tests/golden-* at v1.5.7, written by tools/native-ref/testdata.sh): frames
/// that must decode, frames that must be refused, and inputs whose compression once broke a decoder.
/// </summary>
public sealed class GoldenTests
{
    public static TheoryData<string> Valid() => Files("golden-decompression");

    public static TheoryData<string> Invalid() => Files("golden-decompression-errors");

    public static TheoryData<string> CompressionInputs() => Files("golden-compression");

    [Theory]
    [MemberData(nameof(Valid))]
    public void Decodes_like_libzstd(string file)
    {
        TestData.Require("golden-decompression");
        byte[] frame = TestData.Read(Path.Combine("golden-decompression", file));
        byte[] expected = new byte[1 << 20];
        Assert.Equal(OperationStatus.Done, NativeZstd.Decompress(frame, expected, null, out int nativeConsumed, out int nativeWritten));

        var decoder = new ZstdDecompressor();
        byte[] output = new byte[Math.Max(1, nativeWritten)];
        OperationStatus status = decoder.Decompress(frame, output, out int consumed, out int written);
        Assert.True(status == OperationStatus.Done, $"{file}: {status} ({decoder.LastError})");
        Assert.Equal(nativeConsumed, consumed);
        Assert.Equal(nativeWritten, written);
        Corpus.AssertSameBytes(expected.AsSpan(0, nativeWritten), output.AsSpan(0, written), file);
    }

    [Theory]
    [MemberData(nameof(Invalid))]
    public void Refuses_like_libzstd(string file)
    {
        TestData.Require("golden-decompression-errors");
        byte[] frame = TestData.Read(Path.Combine("golden-decompression-errors", file));
        byte[] output = new byte[1 << 20];
        Assert.NotEqual(OperationStatus.Done, NativeZstd.Decompress(frame, output, null, out _, out _));

        var decoder = new ZstdDecompressor();
        OperationStatus status = decoder.Decompress(frame, output, out int consumed, out int written);
        Assert.Equal(OperationStatus.InvalidData, status);
        Assert.Equal(0, consumed);
        Assert.Equal(0, written);

        // Refused for the reason the file was written for.
        ZstdError expected = file switch
        {
            "off0.bin.zst" => ZstdError.OffsetTooLarge,                 // a repeat offset of 0
            "truncated_huff_state.zst" => ZstdError.HuffmanTable,      // the weights' FSE stream ends early
            "zeroSeq_extraneous.zst" => ZstdError.SequencesHeader,     // bytes after a 0-sequence header
            _ => throw new InvalidOperationException("no expectation for " + file),
        };
        Assert.Equal(expected, decoder.LastError);
    }

    [Theory]
    [MemberData(nameof(CompressionInputs))]
    public void Decodes_golden_inputs_at_every_level(string file)
    {
        TestData.Require("golden-compression");
        byte[] data = TestData.Read(Path.Combine("golden-compression", file));
        foreach (int level in new[] { -7, -1, 1, 2, 3, 5, 7, 9, 12, 16, 19, 22 })
        {
            byte[] frame = NativeZstd.Compress(data, new ZstandardCompressionOptions { Quality = level });
            AssertDecodes(new ZstdDecompressor(), frame, data, $"{file} L{level}");
        }
    }

    /// <summary>
    /// A dictionary whose entropy tables lack symbols that the data needs: the encoder must fall back
    /// on other tables, and the decoder must load the dictionary all the same.
    /// </summary>
    [Fact]
    public void Decodes_with_a_dictionary_missing_symbols()
    {
        TestData.Require("golden-dictionaries");
        TestData.Require("golden-compression");
        byte[] dictionary = TestData.Read(Path.Combine("golden-dictionaries", "http-dict-missing-symbols"));
        byte[] data = TestData.Read(Path.Combine("golden-compression", "http"));
        using var native = ZstandardDictionary.Create(dictionary);
        foreach (int level in new[] { 1, 3, 9, 19 })
        {
            byte[] frame = NativeZstd.Compress(data, new ZstandardCompressionOptions { Quality = level, Dictionary = native });
            AssertDecodes(new ZstdDecompressor(dictionary), frame, data, $"http L{level}");
        }
    }

    internal static void AssertDecodes(ZstdDecompressor decoder, byte[] frame, byte[] data, string name)
    {
        byte[] output = new byte[data.Length];
        OperationStatus status = decoder.Decompress(frame, output, out int consumed, out int written);
        Assert.True(status == OperationStatus.Done, $"{name}: {status} ({decoder.LastError})");
        Assert.Equal(frame.Length, consumed);
        Assert.Equal(data.Length, written);
        Corpus.AssertSameBytes(data, output, name);
    }

    private static TheoryData<string> Files(string directory)
    {
        var data = new TheoryData<string>();
        foreach (string file in TestData.Files(directory))
        {
            data.Add(file);
        }

        return data;
    }
}
