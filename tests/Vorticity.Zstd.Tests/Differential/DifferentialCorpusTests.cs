using System;
using System.Buffers;
using System.Collections.Generic;
using Vorticity.Zstd.Tests.Support;
using Xunit;

namespace Vorticity.Zstd.Tests.Differential;

/// <summary>
/// Every frame of the corpus is decoded by Vorticity.Zstd and by the platform's libzstd, and both must
/// give back the original bytes. The corpus is split across classes so that xUnit runs them in parallel.
/// Frames across the block boundaries are the compression corpus's, which makes libzstd's own and
/// decodes them; the frames of several blocks here are the large ones and the encoder options'.
/// </summary>
public static class Corpus
{
    private static readonly int[] SmallSizes = [0, 1, 2, 127, 4096];
    private static readonly int[] AllLevels = [-5, -1, 1, 3, 9, 19, 22];

    public static TheoryData<string> Small() => Cases(SmallSizes, AllLevels);

    /// <summary>Several megabytes: many blocks, long offsets, every level that stays quick.</summary>
    public static TheoryData<string> Large()
    {
        var data = new TheoryData<string>();
        foreach (string kind in DataKinds.All)
        {
            foreach (int level in new[] { -1, 1, 3, 9 })
            {
                data.Add(CorpusCase.Name(kind, 3_000_000, level));
            }
        }

        data.Add(CorpusCase.Name("walk64", 3_000_000, 19));
        data.Add(CorpusCase.Name("text", 3_000_000, 19));
        return data;
    }

    public static TheoryData<string> Options()
    {
        var data = new TheoryData<string>();
        foreach (string kind in DataKinds.All)
        {
            // Content checksum.
            foreach (int size in new[] { 0, 127, 131073 })
            {
                data.Add(CorpusCase.Name(kind, size, 1, "chk"));
                data.Add(CorpusCase.Name(kind, size, 19, "chk"));
            }

            // No content size: the frame is written as a stream.
            foreach (int size in new[] { 0, 1, 131073, 400_000 })
            {
                data.Add(CorpusCase.Name(kind, size, 3, "nofcs"));
                data.Add(CorpusCase.Name(kind, size, 19, "nofcs", "chk"));
            }

            // Small target blocks: many compressed blocks, and small windows.
            data.Add(CorpusCase.Name(kind, 131073, 3, "block=1340"));
            data.Add(CorpusCase.Name(kind, 131073, 19, "block=4096"));
            data.Add(CorpusCase.Name(kind, 70_000, 3, "wlog=10"));
            data.Add(CorpusCase.Name(kind, 70_000, 19, "wlog=10"));
        }

        // Long windows, long-distance matching: offsets far back.
        foreach (string kind in new[] { "repeats", "mixed", "text" })
        {
            data.Add(CorpusCase.Name(kind, 3_000_000, 3, "wlog=24"));
            data.Add(CorpusCase.Name(kind, 3_000_000, 3, "wlog=27", "ldm"));
            data.Add(CorpusCase.Name(kind, 3_000_000, 19, "wlog=27", "ldm"));
            data.Add(CorpusCase.Name(kind, 3_000_000, -1, "wlog=22", "ldm", "nofcs"));
        }

        return data;
    }

    public static TheoryData<string> Dictionaries()
    {
        var data = new TheoryData<string>();
        foreach (string kind in DataKinds.All)
        {
            foreach (string type in new[] { "trained", "raw" })
            {
                foreach (int size in new[] { 1, 127, 4096, 131073 })
                {
                    foreach (int level in new[] { 1, 3, 19 })
                    {
                        data.Add(CorpusCase.Name(kind, size, level, "dict=" + type));
                    }
                }

                data.Add(CorpusCase.Name(kind, 4096, 3, "dict=" + type, "chk", "nofcs"));
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

    /// <summary>Decodes the case both ways and checks everything the API promises about it.</summary>
    public static void Check(string name)
    {
        CorpusCase @case = CorpusCase.Parse(name);
        byte[] data = @case.Data;
        byte[] frame = @case.Compress(data);
        (byte[] Bytes, System.IO.Compression.ZstandardDictionary Native)? dictionary = @case.GetDictionary();

        // The oracle first: a failure here is the corpus's, not the decoder's. Its destination is
        // never empty, which the platform's decoder refuses even for an empty frame.
        byte[] native = new byte[Math.Max(1, data.Length)];
        OperationStatus nativeStatus = NativeZstd.Decompress(frame, native, dictionary?.Native, out int nativeConsumed, out int nativeWritten);
        Assert.Equal(OperationStatus.Done, nativeStatus);
        Assert.Equal(frame.Length, nativeConsumed);
        Assert.Equal(data.Length, nativeWritten);
        Assert.True(data.AsSpan().SequenceEqual(native.AsSpan(0, nativeWritten)), "the platform's libzstd does not round-trip " + name);

        ZstdDecompressor decoder = dictionary is null ? new ZstdDecompressor() : new ZstdDecompressor(dictionary.Value.Bytes);

        // An exact destination.
        byte[] output = new byte[data.Length];
        OperationStatus status = decoder.Decompress(frame, output, out int consumed, out int written);
        Assert.True(status == OperationStatus.Done, $"{name}: {status} ({decoder.LastError})");
        Assert.Equal(frame.Length, consumed);
        Assert.Equal(data.Length, written);
        AssertSameBytes(data, output, name);

        // A larger one, with the frame followed by other data, on the same reused decoder.
        byte[] larger = new byte[data.Length + 100];
        byte[] followed = [.. frame, 0x28, 0xB5, 0x2F, 0xFD, 1, 2, 3];
        decoder.Reset();
        status = decoder.Decompress(followed, larger, out consumed, out written);
        Assert.Equal(OperationStatus.Done, status);
        Assert.Equal(frame.Length, consumed);
        Assert.Equal(data.Length, written);
        AssertSameBytes(data, larger.AsSpan(0, written), name);

        // One byte short of room.
        if (data.Length > 0)
        {
            status = decoder.Decompress(frame, new byte[data.Length - 1], out consumed, out written);
            Assert.Equal(OperationStatus.DestinationTooSmall, status);
            Assert.Equal(0, consumed);
            Assert.Equal(0, written);
        }

        // One byte short of frame.
        status = decoder.Decompress(frame.AsSpan(0, frame.Length - 1), output, out consumed, out written);
        Assert.Equal(OperationStatus.NeedMoreData, status);
        Assert.Equal(0, consumed);
        Assert.Equal(0, written);

        // The dictionary given with the frame instead, against a guard page, where a read past its end
        // faults.
        if (dictionary is { } given)
        {
            using var guarded = new GuardedBuffer(given.Bytes.Length);
            Span<byte> placed = guarded.AtEnd(given.Bytes.Length);
            given.Bytes.CopyTo(placed);
            var shared = new ZstdDecompressor();
            byte[] again = new byte[data.Length];
            status = shared.Decompress(frame, again, placed, out consumed, out written);
            Assert.True(status == OperationStatus.Done, $"{name}, the dictionary given with the frame: {status} ({shared.LastError})");
            Assert.Equal(frame.Length, consumed);
            Assert.Equal(data.Length, written);
            AssertSameBytes(data, again, name + ", the dictionary given with the frame");
        }
    }

    public static void AssertSameBytes(ReadOnlySpan<byte> expected, ReadOnlySpan<byte> actual, string name)
    {
        Assert.Equal(expected.Length, actual.Length);
        int mismatch = expected.CommonPrefixLength(actual);
        if (mismatch != expected.Length)
        {
            Assert.Fail($"{name}: first difference at byte {mismatch} of {expected.Length} " +
                $"(expected 0x{expected[mismatch]:X2}, got 0x{actual[mismatch]:X2})");
        }
    }
}

public sealed class SmallFrameTests
{
    [Theory]
    [MemberData(nameof(Corpus.Small), MemberType = typeof(Corpus))]
    public void Decodes_like_libzstd(string name) => Corpus.Check(name);
}

public sealed class LargeFrameTests
{
    [Theory]
    [MemberData(nameof(Corpus.Large), MemberType = typeof(Corpus))]
    public void Decodes_like_libzstd(string name) => Corpus.Check(name);
}

public sealed class EncoderOptionTests
{
    [Theory]
    [MemberData(nameof(Corpus.Options), MemberType = typeof(Corpus))]
    public void Decodes_like_libzstd(string name) => Corpus.Check(name);
}

public sealed class DictionaryTests
{
    [Theory]
    [MemberData(nameof(Corpus.Dictionaries), MemberType = typeof(Corpus))]
    public void Decodes_like_libzstd(string name) => Corpus.Check(name);
}
