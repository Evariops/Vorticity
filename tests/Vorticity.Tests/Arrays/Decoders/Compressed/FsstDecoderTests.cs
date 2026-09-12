// vortex.fsst. The corpus covers it on 55 files, so what is here is the kernel's own contract --
// escapes, symbol widths, the 12-byte view boundary -- and the failure surface, none of which a
// conformant writer can produce.
//
// The fixtures build the symbol table by hand rather than compressing anything, because the point
// is to pin what the DECODER does with a given table, and a compressor in the test would let a
// symmetric misunderstanding pass unnoticed in both directions.
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;
using Vorticity.Arrays;
using Vorticity.Arrays.Decoders.Compressed;
using Vorticity.Types;
using Xunit;

namespace Vorticity.Tests.Arrays.Decoders.Compressed;

public sealed class FsstDecoderTests
{
    [Fact]
    public void ExpandsSymbolsAndEscapes()
    {
        // Symbol 0 is "hello ", symbol 1 is "world". Code 255 escapes the byte after it.
        string[] symbols = ["hello ", "world"];
        byte[] codes = [0, 1, FsstSymbolTable.EscapeCode, (byte)'!'];

        Assert.Equal(["hello world!"], Decode(symbols, codes, [12]));
    }

    [Fact]
    public void SymbolsSpanEveryLegalWidth()
    {
        // 1 through 8 bytes: 8 is the u64 the symbol is stored in, and 1 is the degenerate case a
        // wide-store kernel has to handle without running past the value.
        string[] symbols = ["a", "bb", "ccc", "dddd", "eeeee", "ffffff", "ggggggg", "hhhhhhhh"];
        byte[] codes = [0, 1, 2, 3, 4, 5, 6, 7];

        Assert.Equal(["abbcccddddeeeeeffffffggggggghhhhhhhh"], Decode(symbols, codes, [36]));
    }

    [Fact]
    public void RowBoundariesComeFromTheUncompressedLengthsNotTheCodes()
    {
        // The defining property: one code stream, cut into rows by the DECODED lengths. Row 1 ends
        // in the middle of what symbol 0 produced.
        string[] symbols = ["abcdef"];
        byte[] codes = [0, 0];

        Assert.Equal(["abc", "defabc", "def"], Decode(symbols, codes, [3, 6, 3]));
    }

    [Fact]
    public void ValuesStraddleTheInlineViewBoundary()
    {
        // 12 bytes inline, 13 by reference. A symbol is at most 8 bytes, so a 12-byte value is two
        // of them -- which is the normal case and worth having the fixture reflect.
        string[] symbols = ["01234567", "89ab", "c"];
        byte[] codes = [0, 1, 0, 1, 2];

        Assert.Equal(["0123456789ab", "0123456789abc"], Decode(symbols, codes, [12, 13]));
    }

    [Fact]
    public void NullRowsConsumeNothing()
    {
        // Upstream stores a zero uncompressed length for a null row, so the heap holds only the
        // values of the valid ones.
        string[] symbols = ["yes"];
        byte[] codes = [0, 0];

        TestNode root = Root(symbols, codes, [3, 0, 3], PType.U32, PType.U32, validityBuffer: 5);
        using DecodeHarness harness = DecodeHarness.Load(
            root,
            Symbols(symbols),
            Lengths(symbols),
            codes,
            TestBuffers.UInt32(3, 0, 3),
            Offsets(codes.Length, 3),
            TestBuffers.Bitmap(true, false, true));

        DType utf8 = harness.Types.Utf8(Nullability.Nullable);
        CanonicalNode node = harness.Node(harness.DecodeRoot(utf8, 3));

        Assert.True(harness.IsValid(node, 0));
        Assert.False(harness.IsValid(node, 1));
        Assert.Equal("yes", Text(node, 0));
        Assert.Equal("yes", Text(node, 2));
    }

    [Fact]
    public void AnEscapeAtTheEndOfTheStreamIsRejected()
    {
        // The reference asserts "truncated compressed string: escape code at end of input".
        Assert.Throws<VortexFormatException>(
            () => Decode(["a"], [0, FsstSymbolTable.EscapeCode], [2]));
    }

    [Fact]
    public void ACodePastTheSymbolTableIsRejected()
    {
        Assert.Throws<VortexFormatException>(() => Decode(["a"], [0, 4], [2]));
    }

    [Fact]
    public void LengthsThatDoNotAccountForTheDecodedBytesAreRejected()
    {
        // build_views asserts that the lengths describe the heap exactly; a short sum would
        // silently truncate the last value and a long one would read past the heap.
        Assert.Throws<VortexFormatException>(() => Decode(["abc"], [0, 0], [3]));
        Assert.Throws<VortexFormatException>(() => Decode(["abc"], [0, 0], [3, 3, 3]));
    }

    [Fact]
    public void ASymbolLengthOutsideOneToEightIsRejected()
    {
        byte[] symbols = new byte[8];
        Assert.Throws<VortexFormatException>(() => DecodeRaw(symbols, [0], [0], [0]));
        Assert.Throws<VortexFormatException>(() => DecodeRaw(symbols, [9], [0], [9]));
    }

    [Fact]
    public void ASymbolBufferThatIsNotAWholeNumberOfSymbolsIsRejected()
    {
        Assert.Throws<VortexFormatException>(() => DecodeRaw(new byte[7], [1], [0], [1]));
    }

    [Fact]
    public void MismatchedSymbolAndLengthCountsAreRejected()
    {
        Assert.Throws<VortexFormatException>(() => DecodeRaw(new byte[16], [1], [0], [1]));
    }

    [Fact]
    public void ATableLargerThanTheCodeSpaceIsRejected()
    {
        // 255 is the escape, so 255 symbols is the most that can be addressed.
        byte[] symbols = new byte[256 * 8];
        byte[] lengths = new byte[256];
        Array.Fill(lengths, (byte)1);
        Assert.Throws<VortexFormatException>(() => DecodeRaw(symbols, lengths, [0], [1]));
    }

    [Fact]
    public void TheLegacyTwoBufferShapeIsRefusedByName()
    {
        // Upstream's deserialize_legacy keeps the codes in a nested vortex.varbin child. This build
        // reads only the three-buffer shape, and says so rather than misreading the node.
        TestNode root = new TestNode("vortex.fsst")
            .WithMetadata(TestMetadata.Fsst(PType.U32, PType.U32))
            .WithBuffer(0)
            .WithBuffer(1)
            .WithChild(new TestNode("vortex.primitive").WithBuffer(2))
            .WithChild(new TestNode("vortex.primitive").WithBuffer(3));

        using DecodeHarness harness = DecodeHarness.Load(
            root, Symbols(["a"]), Lengths(["a"]), TestBuffers.UInt32(0), TestBuffers.UInt32(0, 1));
        DType utf8 = harness.Types.Utf8(Nullability.NonNullable);
        VortexFormatException error = Assert.Throws<VortexFormatException>(
            () => harness.DecodeRoot(utf8, 1));
        Assert.Contains("two-buffer", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void InvalidUtf8IsRejectedButIsALegalBinaryValue()
    {
        byte[] symbols = new byte[8];
        symbols[0] = 0xFF;
        symbols[1] = 0xFE;

        Assert.Throws<VortexFormatException>(() => DecodeRaw(symbols, [2], [0], [2]));
        Assert.Equal(2, DecodeRaw(symbols, [2], [0], [2], binary: true)[0].Length);
    }

    [Fact]
    public void AnEmptyArrayDecodesToAnEmptyColumn()
    {
        string[] values = Decode(["a"], [], []);
        Assert.Empty(values);
    }

    // ------------------------------------------------------------------------------- fixtures

    private static string[] Decode(string[] symbols, byte[] codes, int[] uncompressedLengths) =>
        DecodeRaw(Symbols(symbols), Lengths(symbols), codes, uncompressedLengths);

    private static string[] DecodeRaw(
        byte[] symbols, byte[] symbolLengths, byte[] codes, int[] uncompressedLengths,
        bool binary = false)
    {
        int rows = uncompressedLengths.Length;
        TestNode root = new TestNode("vortex.fsst")
            .WithMetadata(TestMetadata.Fsst(PType.U32, PType.U32))
            .WithBuffer(0)
            .WithBuffer(1)
            .WithBuffer(2)
            .WithChild(new TestNode("vortex.primitive").WithBuffer(3))
            .WithChild(new TestNode("vortex.primitive").WithBuffer(4));

        uint[] lengths = new uint[rows];
        for (int i = 0; i < rows; i++)
        {
            lengths[i] = (uint)uncompressedLengths[i];
        }

        using DecodeHarness harness = DecodeHarness.Load(
            root, symbols, symbolLengths, codes, TestBuffers.UInt32(lengths), Offsets(codes.Length, rows));

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

    private static TestNode Root(
        string[] symbols, byte[] codes, int[] lengths, PType lengthsPType, PType offsetsPType,
        int validityBuffer)
    {
        TestNode root = new TestNode("vortex.fsst")
            .WithMetadata(TestMetadata.Fsst(lengthsPType, offsetsPType))
            .WithBuffer(0)
            .WithBuffer(1)
            .WithBuffer(2)
            .WithChild(new TestNode("vortex.primitive").WithBuffer(3))
            .WithChild(new TestNode("vortex.primitive").WithBuffer(4));

        return validityBuffer < 0
            ? root
            : root.WithChild(new TestNode("vortex.bool").WithBuffer(validityBuffer));
    }

    /// <summary>One <c>u64</c> per symbol, its bytes in the low positions.</summary>
    private static byte[] Symbols(string[] symbols)
    {
        byte[] bytes = new byte[symbols.Length * FsstSymbolTable.SymbolSize];
        for (int i = 0; i < symbols.Length; i++)
        {
            Encoding.UTF8.GetBytes(symbols[i], bytes.AsSpan(i * FsstSymbolTable.SymbolSize));
        }

        return bytes;
    }

    private static byte[] Lengths(string[] symbols)
    {
        byte[] lengths = new byte[symbols.Length];
        for (int i = 0; i < symbols.Length; i++)
        {
            lengths[i] = (byte)Encoding.UTF8.GetByteCount(symbols[i]);
        }

        return lengths;
    }

    /// <summary>
    /// VarBin offsets over the code stream: only <c>[0]</c> and <c>[rows]</c> are read, so the
    /// interior is filled with the total rather than being made meaningful.
    /// </summary>
    private static byte[] Offsets(int codeBytes, int rows)
    {
        uint[] offsets = new uint[rows + 1];
        for (int i = 1; i <= rows; i++)
        {
            offsets[i] = (uint)codeBytes;
        }

        return TestBuffers.UInt32(offsets);
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
