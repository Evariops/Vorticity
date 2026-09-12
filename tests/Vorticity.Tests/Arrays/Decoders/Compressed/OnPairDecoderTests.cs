// vortex.onpair. 49 corpus files cover the decode; what is here is the dictionary's validation
// rules, which no conformant writer can violate, and the two shapes the encoding shares with FSST
// -- decoded-side row boundaries and the inline/reference view boundary -- pinned separately so a
// regression in one does not hide behind the other's coverage.
using System;
using System.Buffers.Binary;
using System.Text;
using Vorticity.Arrays;
using Vorticity.Types;
using Xunit;

namespace Vorticity.Tests.Arrays.Decoders.Compressed;

public sealed class OnPairDecoderTests
{
    [Fact]
    public void ConcatenatesTheTokensTheCodesName()
    {
        string[] tokens = ["on", "pair", "!"];
        Assert.Equal(["onpair!"], Decode(tokens, [0, 1, 2], [7]));
    }

    [Fact]
    public void RowBoundariesComeFromTheUncompressedLengthsNotTheCodes()
    {
        // As in FSST: one code stream, cut by the DECODED lengths. Row 0 ends inside token 0.
        string[] tokens = ["abcdef"];
        Assert.Equal(["abc", "defabc", "def"], Decode(tokens, [0, 0], [3, 6, 3]));
    }

    [Fact]
    public void ValuesStraddleTheInlineViewBoundary()
    {
        string[] tokens = ["0123456789ab", "c"];
        Assert.Equal(
            ["0123456789ab", "0123456789abc"],
            Decode(tokens, [0, 0, 1], [12, 13]));
    }

    [Fact]
    public void TokensMayBeTheFullSixteenBytes()
    {
        // MAX_TOKEN_SIZE is 16, twice FSST's symbol width; the 16-byte token is the boundary case
        // the reference's fixed-width copy is built around.
        string[] tokens = ["0123456789abcdef"];
        Assert.Equal(["0123456789abcdef"], Decode(tokens, [0], [16]));
    }

    [Fact]
    public void NonAsciiValuesSurviveIntact()
    {
        // Multi-byte sequences split across tokens: the decode is byte-wise, so a token boundary in
        // the middle of a code point must not disturb the value.
        byte[] utf8 = Encoding.UTF8.GetBytes("héllo wörld");
        string[] tokens = [Encoding.Latin1.GetString(utf8[..4]), Encoding.Latin1.GetString(utf8[4..])];

        // The fixture is built over raw bytes, so the tokens are carried as Latin1 round-trips.
        byte[] dictionary = utf8;
        uint[] offsets = [0, 4, (uint)utf8.Length];
        Assert.Equal(
            "héllo wörld",
            DecodeRaw(dictionary, offsets, [0, 1], [utf8.Length])[0]);
        Assert.Equal(2, tokens.Length);
    }

    [Fact]
    public void NullRowsConsumeNothing()
    {
        string[] tokens = ["yes"];
        byte[] dictionary = Encoding.UTF8.GetBytes(string.Concat(tokens));

        TestNode root = Root(dictionarySize: 1, codesLength: 2, validityBuffer: 5);
        using DecodeHarness harness = DecodeHarness.Load(
            root,
            dictionary,
            TestBuffers.UInt32(0, 3),
            TestBuffers.UInt16(0, 0),
            TestBuffers.UInt32(0, 0, 0, 2),
            TestBuffers.UInt32(3, 0, 3),
            TestBuffers.Bitmap(true, false, true));

        DType utf8 = harness.Types.Utf8(Nullability.Nullable);
        CanonicalNode node = harness.Node(harness.DecodeRoot(utf8, 3));

        Assert.True(harness.IsValid(node, 0));
        Assert.False(harness.IsValid(node, 1));
        Assert.Equal("yes", Text(node, 0));
        Assert.Equal("yes", Text(node, 2));
    }

    [Fact]
    public void ACodePastTheDictionaryIsRejected()
    {
        Assert.Throws<VortexFormatException>(() => Decode(["a"], [0, 3], [2]));
    }

    [Fact]
    public void OffsetsThatDoNotStartAtZeroAreRejected()
    {
        Assert.Throws<VortexFormatException>(
            () => DecodeRaw(Encoding.UTF8.GetBytes("ab"), [1, 2], [0], [1]));
    }

    [Fact]
    public void AnEmptyTokenIsRejected()
    {
        // validate_safety refuses these outright: an empty token makes the search tokenizer fail to
        // advance, and it is never something a trainer produces.
        Assert.Throws<VortexFormatException>(
            () => DecodeRaw(Encoding.UTF8.GetBytes("ab"), [0, 0, 2], [1], [2]));
    }

    [Fact]
    public void DecreasingOffsetsAreRejected()
    {
        Assert.Throws<VortexFormatException>(
            () => DecodeRaw(Encoding.UTF8.GetBytes("abc"), [0, 3, 1], [0], [3]));
    }

    [Fact]
    public void ATokenLongerThanSixteenBytesIsRejected()
    {
        byte[] dictionary = new byte[17];
        Assert.Throws<VortexFormatException>(() => DecodeRaw(dictionary, [0, 17], [0], [17]));
    }

    [Fact]
    public void OffsetsRunningPastTheDictionaryBlobAreRejected()
    {
        Assert.Throws<VortexFormatException>(
            () => DecodeRaw(Encoding.UTF8.GetBytes("ab"), [0, 5], [0], [5]));
    }

    [Fact]
    public void LengthsThatDoNotAccountForTheDecodedBytesAreRejected()
    {
        Assert.Throws<VortexFormatException>(() => Decode(["abc"], [0, 0], [3]));
        Assert.Throws<VortexFormatException>(() => Decode(["abc"], [0, 0], [3, 3, 3]));
    }

    [Fact]
    public void InvalidUtf8IsRejectedButIsALegalBinaryValue()
    {
        byte[] dictionary = [0xFF, 0xFE];
        Assert.Throws<VortexFormatException>(() => DecodeRaw(dictionary, [0, 2], [0], [2]));
        Assert.Equal(2, DecodeRaw(dictionary, [0, 2], [0], [2], binary: true)[0].Length);
    }

    [Fact]
    public void AnEmptyArrayDecodesToAnEmptyColumn()
    {
        Assert.Empty(Decode(["a"], [], []));
    }

    // ------------------------------------------------------------------------------- fixtures

    private static string[] Decode(string[] tokens, ushort[] codes, int[] uncompressedLengths)
    {
        byte[] dictionary = Encoding.UTF8.GetBytes(string.Concat(tokens));
        uint[] offsets = new uint[tokens.Length + 1];
        for (int i = 0; i < tokens.Length; i++)
        {
            offsets[i + 1] = offsets[i] + (uint)Encoding.UTF8.GetByteCount(tokens[i]);
        }

        return DecodeRaw(dictionary, offsets, codes, uncompressedLengths);
    }

    private static string[] DecodeRaw(
        byte[] dictionary, uint[] dictOffsets, ushort[] codes, int[] uncompressedLengths,
        bool binary = false)
    {
        int rows = uncompressedLengths.Length;
        uint[] lengths = new uint[rows];
        for (int i = 0; i < rows; i++)
        {
            lengths[i] = (uint)uncompressedLengths[i];
        }

        // Only codes_offsets[0] and codes_offsets[rows] are read, so the interior is filled with
        // the total rather than being made meaningful.
        uint[] codeOffsets = new uint[rows + 1];
        for (int i = 1; i <= rows; i++)
        {
            codeOffsets[i] = (uint)codes.Length;
        }

        using DecodeHarness harness = DecodeHarness.Load(
            Root((uint)(dictOffsets.Length - 1), (ulong)codes.Length, validityBuffer: -1),
            dictionary,
            TestBuffers.UInt32(dictOffsets),
            TestBuffers.UInt16(codes),
            TestBuffers.UInt32(codeOffsets),
            TestBuffers.UInt32(lengths));

        DType dtype = binary
            ? harness.Types.Binary(Nullability.NonNullable)
            : harness.Types.Utf8(Nullability.NonNullable);
        CanonicalNode node = harness.Node(harness.DecodeRoot(dtype, rows));

        string[] values = new string[rows];
        for (int i = 0; i < rows; i++)
        {
            values[i] = Text(node, i);
        }

        return values;
    }

    /// <summary>
    /// Buffer 0 is the dictionary blob; children are dict_offsets, codes, codes_offsets,
    /// uncompressed_lengths and optionally validity. dict_size and codes_len ride in the metadata
    /// rather than on the wire, so the fixture states them explicitly.
    /// </summary>
    private static TestNode Root(uint dictionarySize, ulong codesLength, int validityBuffer)
    {
        TestNode root = new TestNode("vortex.onpair")
            .WithMetadata(TestMetadata.OnPair(dictionarySize, codesLength))
            .WithBuffer(0)
            .WithChild(new TestNode("vortex.primitive").WithBuffer(1))
            .WithChild(new TestNode("vortex.primitive").WithBuffer(2))
            .WithChild(new TestNode("vortex.primitive").WithBuffer(3))
            .WithChild(new TestNode("vortex.primitive").WithBuffer(4));

        return validityBuffer < 0
            ? root
            : root.WithChild(new TestNode("vortex.bool").WithBuffer(validityBuffer));
    }

    private static string Text(CanonicalNode node, int index)
    {
        ReadOnlySpan<byte> view = node.Views.Span.Slice(index * 16, 16);
        int size = BinaryPrimitives.ReadInt32LittleEndian(view);
        if (size <= 12)
        {
            return Encoding.UTF8.GetString(view.Slice(4, size));
        }

        int buffer = BinaryPrimitives.ReadInt32LittleEndian(view[8..12]);
        int offset = BinaryPrimitives.ReadInt32LittleEndian(view[12..16]);
        return Encoding.UTF8.GetString(node.GetDataBuffer(buffer).Span.Slice(offset, size));
    }
}
