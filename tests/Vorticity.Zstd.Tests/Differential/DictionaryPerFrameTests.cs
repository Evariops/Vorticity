using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO.Compression;
using Vorticity.Zstd.Internal;
using Vorticity.Zstd.Tests.Support;
using Xunit;

namespace Vorticity.Zstd.Tests.Differential;

/// <summary>
/// A dictionary given with each frame rather than to the constructor: copied for the frame, its
/// tables kept for the frames that give the same and rebuilt in place for another, so that one
/// decompressor serves frames of every dictionary. libzstd writes the frames.
/// </summary>
public sealed class DictionaryPerFrameTests
{
    public static TheoryData<string, int> Cases() => DictionaryEdgeTests.Cases();

    [Theory]
    [MemberData(nameof(Cases))]
    public void Edges_of_a_dictionary_given_with_the_frame_decode_like_libzstd(string type, int level)
    {
        // Against a guard page, where a read past the dictionary would fault: the edges are the
        // matches that end on its last bytes, which the fast loops copy 32 bytes at a time. Then
        // blocks of short sequences, which the decoder takes two at a time.
        (byte[] dictionary, _) = Dictionaries.Get("json", type);
        byte[] data = [.. DictionaryEdgeTests.EdgeContent(dictionary), .. DataKinds.Generate("json", 320 * 1024, 5)];
        using ZstandardDictionary prepared = ZstandardDictionary.Create(dictionary, level);
        byte[] frame = NativeZstd.Compress(data, new ZstandardCompressionOptions { Quality = level, Dictionary = prepared });
        AssertDecodes(frame, data, dictionary, $"{type} L{level}");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(19)]
    public void A_small_dictionary_given_with_the_frame_decodes_like_libzstd(int level)
    {
        // Of the size Vortex writers train for the frames of a small column: a hundredth of its bytes.
        byte[] dictionary = DataKinds.Generate("json", 600, 21);
        byte[] data = [.. DictionaryEdgeTests.EdgeContent(dictionary), .. DataKinds.Generate("json", 64 * 1024, 6)];
        using ZstandardDictionary prepared = ZstandardDictionary.Create(dictionary, level);
        byte[] frame = NativeZstd.Compress(data, new ZstandardCompressionOptions { Quality = level, Dictionary = prepared });
        AssertDecodes(frame, data, dictionary, $"600 bytes L{level}");
    }

    [Fact]
    public void One_decompressor_decodes_frames_of_one_dictionary_after_another()
    {
        // Each dictionary's tables are rebuilt in place over the last one's, and the table sets told:
        // frames of several, in turn, back and again, single blocks and paired ones, must each decode
        // as with a decompressor of its own.
        List<Given> dictionaries = Givens();
        var frames = new List<(int Dictionary, byte[] Data, byte[] Frame)>();
        for (int d = 0; d < dictionaries.Count; d++)
        {
            foreach ((string kind, int size, int seed) in new[] { ("json", 1024, 31), ("text", 3000, 32), ("json", 300 * 1024, 33) })
            {
                byte[] data = DataKinds.Generate(kind, size, seed + d);
                var options = new ZstandardCompressionOptions { Quality = 3, Dictionary = dictionaries[d].Native };
                frames.Add((d, data, NativeZstd.Compress(data, options)));
            }
        }

        var random = new Random(5);
        var decoder = new ZstdDecompressor();
        for (int round = 0; round < 3; round++)
        {
            foreach (int f in Order(frames.Count, random))
            {
                (int d, byte[] data, byte[] frame) = frames[f];
                byte[] output = new byte[data.Length];
                OperationStatus status = decoder.Decompress(frame, output, dictionaries[d].Bytes, out int consumed, out int written);
                Assert.True(status == OperationStatus.Done, $"{dictionaries[d].Name}, {data.Length} bytes: {status} ({decoder.LastError})");
                Assert.Equal(frame.Length, consumed);
                Corpus.AssertSameBytes(data, output.AsSpan(0, written), $"{dictionaries[d].Name}, {data.Length} bytes, round {round}");
            }
        }
    }

    [Fact]
    public void The_dictionary_of_the_constructor_and_those_given_with_frames_take_turns()
    {
        (byte[] json, ZstandardDictionary jsonNative) = Dictionaries.Get("json", "trained");
        (byte[] text, ZstandardDictionary textNative) = Dictionaries.Get("text", "trained");
        byte[] jsonData = DataKinds.Generate("json", 8192, 41);
        byte[] textData = DataKinds.Generate("text", 8192, 42);
        byte[] jsonFrame = NativeZstd.Compress(jsonData, new ZstandardCompressionOptions { Quality = 3, Dictionary = jsonNative });
        byte[] textFrame = NativeZstd.Compress(textData, new ZstandardCompressionOptions { Quality = 3, Dictionary = textNative });

        var decoder = new ZstdDecompressor(json);
        for (int i = 0; i < 3; i++)
        {
            byte[] output = new byte[8192];
            Assert.Equal(OperationStatus.Done, decoder.Decompress(jsonFrame, output, out _, out int written));
            Corpus.AssertSameBytes(jsonData, output.AsSpan(0, written), $"the constructor's, turn {i}");
            Assert.Equal(OperationStatus.Done, decoder.Decompress(textFrame, output, text, out _, out written));
            Corpus.AssertSameBytes(textData, output.AsSpan(0, written), $"given with the frame, turn {i}");
        }
    }

    [Fact]
    public void A_dictionary_is_read_with_each_frame_though_its_tables_are_kept()
    {
        // Two dictionaries alike up to their content, which differs in its middle and in its last
        // bytes: the tables of the first serve the second, but its bytes are its own.
        (byte[] first, _) = Dictionaries.Get("json", "trained");
        byte[] second = (byte[])first.Clone();
        second[^3] ^= 0x5A;
        second[second.Length - 4000] ^= 0x5A;
        foreach (byte[] dictionary in new[] { first, second, first })
        {
            using ZstandardDictionary prepared = ZstandardDictionary.Create(dictionary, 3);
            byte[] data = [.. DictionaryEdgeTests.EdgeContent(dictionary), .. dictionary.AsSpan(dictionary.Length - 4100, 200), .. DataKinds.Generate("json", 4096, 7)];
            byte[] frame = NativeZstd.Compress(data, new ZstandardCompressionOptions { Quality = 3, Dictionary = prepared });
            byte[] output = new byte[data.Length];
            Assert.Equal(OperationStatus.Done, Shared.Decompress(frame, output, dictionary, out _, out int written));
            Corpus.AssertSameBytes(data, output.AsSpan(0, written), ReferenceEquals(dictionary, first) ? "first" : "second");
        }
    }

    [Fact]
    public void A_dictionary_with_invalid_tables_is_refused_and_not_kept()
    {
        (byte[] dictionary, ZstandardDictionary native) = Dictionaries.Get("json", "trained");
        byte[] data = DataKinds.Generate("json", 4096, 9);
        byte[] frame = NativeZstd.Compress(data, new ZstandardCompressionOptions { Quality = 3, Dictionary = native });
        byte[] corrupted = (byte[])dictionary.Clone();
        corrupted.AsSpan(8, 56).Fill(0xFF);

        var decoder = new ZstdDecompressor();
        byte[] output = new byte[data.Length];
        Assert.Equal(OperationStatus.Done, decoder.Decompress(frame, output, dictionary, out _, out _));
        for (int i = 0; i < 2; i++)
        {
            Assert.Equal(OperationStatus.InvalidData, decoder.Decompress(frame, output, corrupted, out int consumed, out int written));
            Assert.Equal(ZstdError.DictionaryCorrupted, decoder.LastError);
            Assert.Equal(0, consumed);
            Assert.Equal(0, written);
        }

        Array.Clear(output);
        Assert.Equal(OperationStatus.Done, decoder.Decompress(frame, output, dictionary, out _, out int again));
        Corpus.AssertSameBytes(data, output.AsSpan(0, again), "after the invalid dictionary");
    }

    [Fact]
    public void A_frame_that_names_another_dictionary_is_refused()
    {
        (byte[] json, ZstandardDictionary native) = Dictionaries.Get("json", "trained");
        (byte[] text, _) = Dictionaries.Get("text", "trained");
        byte[] data = DataKinds.Generate("json", 4096, 9);
        byte[] frame = NativeZstd.Compress(data, new ZstandardCompressionOptions { Quality = 3, Dictionary = native });

        var decoder = new ZstdDecompressor(json);
        byte[] output = new byte[data.Length];
        Assert.Equal(OperationStatus.InvalidData, decoder.Decompress(frame, output, text, out _, out _));
        Assert.Equal(ZstdError.DictionaryMismatch, decoder.LastError);
        Assert.Equal(OperationStatus.InvalidData, decoder.Decompress(frame, output, ReadOnlySpan<byte>.Empty, out _, out _));
        Assert.Equal(ZstdError.DictionaryMismatch, decoder.LastError);
        Assert.Equal(OperationStatus.Done, decoder.Decompress(frame, output, json, out _, out int written));
        Corpus.AssertSameBytes(data, output.AsSpan(0, written), "with its own");
    }

    [Fact]
    public void From_one_dictionary_to_another_a_warm_decompressor_allocates_nothing()
    {
#if DEBUG
        Assert.Skip("allocations are a property of the optimized build");
#endif
        List<Given> dictionaries = Givens();
        var frames = new (byte[] Dictionary, byte[] Frame, int Size)[dictionaries.Count];
        for (int d = 0; d < dictionaries.Count; d++)
        {
            byte[] data = DataKinds.Generate("json", 200 * 1024, 50 + d);
            var options = new ZstandardCompressionOptions { Quality = 3, Dictionary = dictionaries[d].Native };
            frames[d] = (dictionaries[d].Bytes, NativeZstd.Compress(data, options), data.Length);
        }

        var decoder = new ZstdDecompressor();
        byte[] output = new byte[200 * 1024];
        for (int i = 0; i < 3; i++)
        {
            foreach ((byte[] dictionary, byte[] frame, _) in frames)
            {
                Assert.Equal(OperationStatus.Done, decoder.Decompress(frame, output, dictionary, out _, out _));
            }
        }

        // The least of several rounds, each frame with another dictionary than the one before.
        long perFrame = long.MaxValue;
        for (int round = 0; round < 5; round++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 4; i++)
            {
                foreach ((byte[] dictionary, byte[] frame, _) in frames)
                {
                    decoder.Decompress(frame, output, dictionary, out _, out _);
                }
            }

            perFrame = Math.Min(perFrame, (GC.GetAllocatedBytesForCurrentThread() - before) / (4 * frames.Length));
        }

        Assert.True(perFrame == 0, $"{perFrame} bytes allocated per frame");
    }

    /// <summary>A decompressor kept from one call to the next of a test, as a reader keeps one.</summary>
    private static readonly ZstdDecompressor Shared = new ZstdDecompressor();

    private sealed record Given(string Name, byte[] Bytes, ZstandardDictionary? Native);

    /// <summary>Two trained dictionaries, raw content, a dictionary under the tail's size, and none.</summary>
    private static List<Given> Givens()
    {
        (byte[] json, ZstandardDictionary jsonNative) = Dictionaries.Get("json", "trained");
        (byte[] text, ZstandardDictionary textNative) = Dictionaries.Get("text", "trained");
        (byte[] raw, ZstandardDictionary rawNative) = Dictionaries.Get("json", "raw");
        byte[] small = DataKinds.Generate("text", 700, 23);
        return
        [
            new Given("json trained", json, jsonNative),
            new Given("text trained", text, textNative),
            new Given("json raw", raw, rawNative),
            new Given("700 bytes raw", small, ZstandardDictionary.Create(small)),
            new Given("none", [], null),
        ];
    }

    /// <summary>Every index once, shuffled: some dictionaries come twice in a row, others alternate.</summary>
    private static int[] Order(int count, Random random)
    {
        int[] order = new int[count];
        for (int i = 0; i < count; i++)
        {
            order[i] = i;
        }

        random.Shuffle(order);
        return order;
    }

    /// <summary>
    /// Decodes <paramref name="frame"/> with <paramref name="dictionary"/> against a guard page, into
    /// a destination of the exact size and into a larger one, with and without paired blocks.
    /// </summary>
    private static void AssertDecodes(byte[] frame, byte[] data, byte[] dictionary, string name)
    {
        using var guarded = new GuardedBuffer(dictionary.Length);
        Span<byte> placed = guarded.AtEnd(dictionary.Length);
        dictionary.CopyTo(placed);
        foreach (bool paired in new[] { true, false })
        {
            var decoder = new ZstdDecompressor { PairsBlocks = paired };
            foreach (int extra in new[] { 0, 64 })
            {
                byte[] output = new byte[data.Length + extra];
                OperationStatus status = decoder.Decompress(frame, output, placed, out int consumed, out int written);
                Assert.True(status == OperationStatus.Done, $"{name}, paired {paired}: {status} ({decoder.LastError})");
                Assert.Equal(frame.Length, consumed);
                Assert.Equal(data.Length, written);
                Corpus.AssertSameBytes(data, output.AsSpan(0, written), $"{name}, paired {paired}, +{extra}");
            }
        }
    }
}
