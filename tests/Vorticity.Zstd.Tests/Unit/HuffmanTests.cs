using System;
using System.Buffers;
using System.Linq;
using Vorticity.Zstd.Internal;
using Vorticity.Zstd.Tests.Support;
using Xunit;

namespace Vorticity.Zstd.Tests.Unit;

/// <summary>
/// Literals compressed with Huffman codes built independently of the decoder (<see cref="HuffmanCode"/>),
/// in frames the platform's libzstd decodes too.
/// </summary>
public sealed class HuffmanTests
{
    public static TheoryData<int, int, int, bool> Codes()
    {
        var data = new TheoryData<int, int, int, bool>();
        int seed = 0;
        foreach (int symbols in new[] { 2, 3, 5, 17, 64, 100, 129 })
        {
            foreach (int maxLength in new[] { 1, 4, 8, 11, 12 })
            {
                if (symbols > 1 << maxLength)
                {
                    continue;
                }

                foreach (bool four in new[] { false, true })
                {
                    data.Add(seed++, symbols, maxLength, four);
                }
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Codes))]
    public void Decodes_random_codes(int seed, int symbols, int maxLength, bool fourStreams)
    {
        var random = new Random(seed);
        HuffmanCode code = HuffmanCode.Random(random, symbols, maxLength);
        int count = fourStreams ? 6 + random.Next(20000) : 1 + random.Next(1000);
        byte[] literals = RandomSymbols(random, code, count);

        // One block of literals only; then a second, treeless, block reusing the tree.
        byte[] second = RandomSymbols(random, code, fourStreams ? 6 + random.Next(2000) : 1 + random.Next(900));
        byte[] content = [.. literals, .. second];
        byte[] frame = new FrameBuilder { ContentSize = (ulong)content.Length }
            .Compressed([.. code.Literals(literals, fourStreams), .. FrameBuilder.NoSequences()])
            .Compressed([.. code.Literals(second, fourStreams, treeless: true), .. FrameBuilder.NoSequences()], last: true)
            .Build();

        FrameAssert.DecodesTo(frame, content);
    }

    public static TheoryData<int, int, int> DoubleSymbolCodes()
    {
        var data = new TheoryData<int, int, int>();
        int seed = 1000;
        foreach (int symbols in new[] { 2, 3, 7, 17, 40, 100, 129 })
        {
            foreach (int maxLength in new[] { 1, 2, 4, 6, 8, 10, 11 })
            {
                if (symbols <= 1 << maxLength)
                {
                    data.Add(seed++, symbols, maxLength);
                }
            }
        }

        return data;
    }

    /// <summary>
    /// The double-symbol table decodes four streams as the single-symbol one does, its own rounds
    /// then the single-symbol finish of each stream, whatever the code: built by runs, it must pair
    /// every two codes that fit in 11 bits, and only those.
    /// </summary>
    [Theory]
    [MemberData(nameof(DoubleSymbolCodes))]
    public void The_double_symbol_table_decodes_as_the_single_one(int seed, int symbols, int maxLength)
    {
        var random = new Random(seed);
        HuffmanCode code = HuffmanCode.Random(random, symbols, maxLength);
        var table = new HuffmanTable();
        table.Read(code.DescribeDirect());
        foreach (int count in new[] { 6, 64, 999, 20000 })
        {
            byte[] literals = RandomSymbols(random, code, count);
            byte[] streams = code.EncodeFourStreams(literals);
            byte[] single = new byte[count];
            byte[] pairs = new byte[count];
            table.DecodeFourStreams(streams, single, preferDouble: false);
            table.DecodeFourStreams(streams, pairs, preferDouble: true);
            Assert.Equal(literals, single);
            Assert.Equal(literals, pairs);
        }
    }

    [Fact]
    public void A_weight_above_12_is_refused()
    {
        // Weights 13 and below: a direct description cannot even state 13+ beyond 15, and libzstd
        // refuses any weight above HUF_TABLELOG_MAX.
        byte[] description = [127 + 2, 0xD1]; // weights 13, 1 for symbols 0 and 1
        AssertRefused(description, ZstdError.HuffmanTable);
    }

    [Fact]
    public void An_incomplete_tree_is_refused()
    {
        // Weights 2, 1, 1 (implied last: total 2+1+1 = 4, rest... ) are completed by a power of two;
        // weights 2, 2, 1 leave a rest of 3, which no single weight fills.
        AssertRefused([127 + 3, 0x22, 0x10], ZstdError.HuffmanTable);
    }

    [Fact]
    public void A_tree_without_weight_one_codes_is_refused()
    {
        // Weight 2 and an implied 2: a complete code, but stated one level too deep. The weights of
        // a tree always sum to a power of two, so the longest codes come in pairs, and libzstd
        // requires that pair at weight 1.
        AssertRefused([127 + 1, 0x20], ZstdError.HuffmanTable);
    }

    [Fact]
    public void A_twelve_bit_tree_is_accepted_like_libzstd()
    {
        // RFC 8878 caps codes at 11 bits; libzstd decodes up to 12, and so does Vorticity.Zstd.
        var random = new Random(12);
        HuffmanCode code;
        do
        {
            code = HuffmanCode.Random(random, 100, 12);
        }
        while (code.TableLog != 12);

        byte[] literals = RandomSymbols(random, code, 5000);
        byte[] frame = new FrameBuilder { ContentSize = (ulong)literals.Length }
            .Compressed([.. code.Literals(literals, fourStreams: true), .. FrameBuilder.NoSequences()], last: true)
            .Build();
        FrameAssert.DecodesTo(frame, literals);
    }

    [Fact]
    public void Four_streams_need_six_literals()
    {
        HuffmanCode code = HuffmanCode.FromWeights(1, 1);
        byte[] literals = [0, 1, 0, 1, 1];
        byte[] section = [.. HuffmanCode.CompressedLiteralsHeader(2, 5, 2 + 6 + 4, fourStreams: true), .. code.DescribeDirect(), .. new byte[6 + 4]];
        byte[] frame = new FrameBuilder { ContentSize = 5 }.Compressed([.. section, 0], last: true).Build();
        FrameAssert.Refused(frame, ZstdError.LiteralsHeader);
        _ = literals;
    }

    [Fact]
    public void Treeless_literals_need_a_previous_tree()
    {
        HuffmanCode code = HuffmanCode.FromWeights(1, 1);
        byte[] frame = new FrameBuilder { ContentSize = 4 }
            .Compressed([.. code.Literals([0, 1, 1, 0], fourStreams: false, treeless: true), .. FrameBuilder.NoSequences()], last: true)
            .Build();
        FrameAssert.Refused(frame, ZstdError.TreelessWithoutTable);
    }

    [Fact]
    public void A_stream_with_bits_left_over_is_refused()
    {
        HuffmanCode code = HuffmanCode.FromWeights(1, 1);
        byte[] stream = code.EncodeStream([0, 1, 1, 0, 1, 0, 0]);
        byte[] payload = [.. code.DescribeDirect(), .. stream];

        // The header claims six literals: the stream holds seven.
        byte[] frame = new FrameBuilder { ContentSize = 6 }
            .Compressed([.. HuffmanCode.CompressedLiteralsHeader(2, 6, payload.Length, fourStreams: false), .. payload, .. FrameBuilder.NoSequences()], last: true)
            .Build();
        FrameAssert.Refused(frame, ZstdError.HuffmanStream);
    }

    private static void AssertRefused(byte[] description, ZstdError error)
    {
        byte[] payload = [.. description, 0x01, 0x80];
        byte[] frame = new FrameBuilder { ContentSize = 4 }
            .Compressed([.. HuffmanCode.CompressedLiteralsHeader(2, 4, payload.Length, fourStreams: false), .. payload, .. FrameBuilder.NoSequences()], last: true)
            .Build();
        FrameAssert.Refused(frame, error);
    }

    private static byte[] RandomSymbols(Random random, HuffmanCode code, int count)
    {
        int[] present = Enumerable.Range(0, code.Weights.Length).Where(s => code.Weights[s] > 0).ToArray();
        byte[] symbols = new byte[count];
        for (int i = 0; i < count; i++)
        {
            // Skewed, as literals are: low symbols far more often.
            symbols[i] = (byte)present[(int)(Math.Pow(random.NextDouble(), 3) * present.Length)];
        }

        return symbols;
    }
}

/// <summary>Assertions on hand-built frames, which the platform's libzstd must judge alike.</summary>
internal static class FrameAssert
{
    public static void DecodesTo(byte[] frame, byte[] expected, byte[]? dictionary = null)
    {
        var decoder = dictionary is null ? new ZstdDecompressor() : new ZstdDecompressor(dictionary);
        byte[] output = new byte[Math.Max(1, expected.Length)];
        OperationStatus status = decoder.Decompress(frame, output, out int consumed, out int written);
        Assert.True(status == OperationStatus.Done, $"{status} ({decoder.LastError})");
        Assert.Equal(frame.Length, consumed);
        Assert.Equal(expected.Length, written);
        Differential.Corpus.AssertSameBytes(expected, output.AsSpan(0, written), "frame");

        using var native = dictionary is null ? null : System.IO.Compression.ZstandardDictionary.Create(dictionary);
        byte[] nativeOutput = new byte[Math.Max(1, expected.Length)];
        Assert.Equal(OperationStatus.Done, NativeZstd.Decompress(frame, nativeOutput, native, out _, out int nativeWritten));
        Differential.Corpus.AssertSameBytes(expected, nativeOutput.AsSpan(0, nativeWritten), "frame (libzstd)");
    }

    public static void Refused(byte[] frame, ZstdError error, byte[]? dictionary = null, int capacity = 1 << 16)
    {
        var decoder = dictionary is null ? new ZstdDecompressor() : new ZstdDecompressor(dictionary);
        OperationStatus status = decoder.Decompress(frame, new byte[capacity], out int consumed, out int written);
        Assert.NotEqual(OperationStatus.Done, status);
        Assert.Equal(0, consumed);
        Assert.Equal(0, written);
        Assert.Equal(error, decoder.LastError);

        // The platform's decoder refuses too, by a status or, for some errors, an exception.
        using var native = dictionary is null ? null : System.IO.Compression.ZstandardDictionary.Create(dictionary);
        OperationStatus nativeStatus;
        try
        {
            nativeStatus = NativeZstd.Decompress(frame, new byte[capacity], native, out _, out _);
        }
        catch (Exception e) when (e is System.IO.IOException or System.IO.InvalidDataException)
        {
            nativeStatus = OperationStatus.InvalidData;
        }

        Assert.NotEqual(OperationStatus.Done, nativeStatus);
    }
}
