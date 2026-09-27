// vortex.alprd. Six corpus files cover it, which is enough to prove the arithmetic and not enough
// to prove anything about the edges, so this file is where the edges live.
//
// The fixtures are built by SPLITTING a real double rather than by writing plausible-looking
// integers: a test that invents its own left and right parts proves the decoder is self-consistent,
// not that it reconstructs the value the encoder cut up.
using System;
using System.Buffers.Binary;
using Vorticity.Arrays;
using Vorticity.Arrays.Metadata;
using Vorticity.Types;
using Xunit;

namespace Vorticity.Tests.Arrays.Decoders.Compressed;

public sealed class AlpRdDecoderTests
{
    /// <summary>The width a real encoder picks for f64: 48 low bits stored, 16 high bits shared.</summary>
    private const int RightBitWidth = 48;

    [Fact]
    public void ReconstructsTheDoublesItWasCutFrom()
    {
        double[] originals = [3.141592653589793, 2.718281828459045, 1.4142135623730951];
        Assert.Equal(originals, RoundTrip(originals));
    }

    [Fact]
    public void ReconstructsBitPatternsThatArithmeticWouldDestroy()
    {
        // The reason the combine is a reinterpretation and not a conversion. -0.0 compares equal to
        // 0.0 and a NaN compares equal to nothing, so both are checked by their raw bits.
        double negativeZero = -0.0;
        double nan = BitConverter.UInt64BitsToDouble(0x7FF8_0000_DEAD_BEEF);

        double[] decoded = RoundTrip([negativeZero, nan, double.PositiveInfinity]);

        Assert.Equal(
            BitConverter.DoubleToUInt64Bits(negativeZero), BitConverter.DoubleToUInt64Bits(decoded[0]));
        Assert.Equal(
            BitConverter.DoubleToUInt64Bits(nan), BitConverter.DoubleToUInt64Bits(decoded[1]));
        Assert.Equal(double.PositiveInfinity, decoded[2]);
    }

    [Fact]
    public void ReconstructsFloats()
    {
        // f32 cuts a 32-bit pattern; the right parts child is u32 rather than u64.
        const int SingleRightBitWidth = 16;
        float[] originals = [3.14159f, -2.5f, float.Epsilon];

        uint[] dictionary = new uint[originals.Length];
        byte[] right = new byte[originals.Length * sizeof(uint)];
        for (int i = 0; i < originals.Length; i++)
        {
            uint word = BitConverter.SingleToUInt32Bits(originals[i]);
            dictionary[i] = word >> SingleRightBitWidth;
            BinaryPrimitives.WriteUInt32LittleEndian(
                right.AsSpan(i * sizeof(uint)), word & ((1u << SingleRightBitWidth) - 1));
        }

        byte[] codes = TestBuffers.UInt16(0, 1, 2);
        TestNode root = new TestNode("vortex.alprd")
            .WithMetadata(TestMetadata.AlpRd(SingleRightBitWidth, PType.U16, dictionary))
            .WithChild(new TestNode("vortex.primitive").WithBuffer(0))
            .WithChild(new TestNode("vortex.primitive").WithBuffer(1));

        using DecodeHarness harness = DecodeHarness.Load(root, codes, right);
        DType f32 = harness.Types.Primitive(PType.F32, Nullability.NonNullable);
        CanonicalNode node = harness.Node(harness.DecodeRoot(f32, 3));

        for (int i = 0; i < originals.Length; i++)
        {
            Assert.Equal(
                originals[i],
                BinaryPrimitives.ReadSingleLittleEndian(node.Values.Span.Slice(i * 4, 4)));
        }
    }

    [Theory]
    [InlineData(PType.U8, false)]
    [InlineData(PType.U16, false)]
    [InlineData(PType.U32, false)]
    [InlineData(PType.U64, false)]
    [InlineData(PType.U8, true)]
    [InlineData(PType.U16, true)]
    [InlineData(PType.U32, true)]
    [InlineData(PType.U64, true)]
    internal void EveryCodeWidthCombinesAsTheScalarLineDoes(PType codeType, bool single)
    {
        // Thirty-seven rows over a full dictionary: whole vector steps where a machine has them, a
        // tail after them, and every entry looked up, each row checked by its bits.
        const int rows = 37;
        int rightBitWidth = single ? 20 : 48;
        int width = single ? sizeof(uint) : sizeof(ulong);
        Random random = new Random(20260926 + (int)codeType);
        uint[] dictionary = new uint[8];
        for (int i = 0; i < dictionary.Length; i++)
        {
            dictionary[i] = (uint)random.Next(1 << (single ? 12 : 16));
        }

        int[] code = new int[rows];
        ulong[] low = new ulong[rows];
        byte[] codes = new byte[rows * codeType.ByteWidth()];
        byte[] right = new byte[rows * width];
        for (int i = 0; i < rows; i++)
        {
            code[i] = random.Next(dictionary.Length);
            low[i] =(ulong)random.NextInt64() & ((1UL << rightBitWidth) - 1);
            if (single)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(right.AsSpan(i * 4), (uint)low[i]);
            }
            else
            {
                BinaryPrimitives.WriteUInt64LittleEndian(right.AsSpan(i * 8), low[i]);
            }

            Span<byte> at = codes.AsSpan(i * codeType.ByteWidth());
            switch (codeType)
            {
                case PType.U8: at[0] = (byte)code[i]; break;
                case PType.U16: BinaryPrimitives.WriteUInt16LittleEndian(at, (ushort)code[i]); break;
                case PType.U32: BinaryPrimitives.WriteUInt32LittleEndian(at, (uint)code[i]); break;
                default: BinaryPrimitives.WriteUInt64LittleEndian(at, (ulong)code[i]); break;
            }
        }

        TestNode root = new TestNode("vortex.alprd")
            .WithMetadata(TestMetadata.AlpRd((uint)rightBitWidth, codeType, dictionary))
            .WithChild(new TestNode("vortex.primitive").WithBuffer(0))
            .WithChild(new TestNode("vortex.primitive").WithBuffer(1));

        using DecodeHarness harness = DecodeHarness.Load(root, codes, right);
        DType type = harness.Types.Primitive(single ? PType.F32 : PType.F64, Nullability.NonNullable);
        CanonicalNode node = harness.Node(harness.DecodeRoot(type, rows));

        for (int i = 0; i < rows; i++)
        {
            ulong expected = ((ulong)dictionary[code[i]] << rightBitWidth) | low[i];
            ulong actual = single
                ? BinaryPrimitives.ReadUInt32LittleEndian(node.Values.Span.Slice(i * 4, 4))
                : BinaryPrimitives.ReadUInt64LittleEndian(node.Values.Span.Slice(i * 8, 8));
            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public void SharedHighBitsCollapseIntoOneDictionaryEntry()
    {
        // The whole point of the encoding: values close enough to share their top 16 bits share a
        // code. 1.5/1.25/1.125 do NOT -- their top 4 mantissa bits differ -- which is why the
        // assertion below is in the test rather than assumed.
        double[] originals = [1.0, 1.01, 1.02];
        ulong shared = BitConverter.DoubleToUInt64Bits(originals[0]) >> RightBitWidth;
        foreach (double value in originals)
        {
            Assert.Equal(shared, BitConverter.DoubleToUInt64Bits(value) >> RightBitWidth);
        }

        byte[] codes = TestBuffers.UInt16(0, 0, 0);
        byte[] right = RightParts(originals);

        TestNode root = new TestNode("vortex.alprd")
            .WithMetadata(TestMetadata.AlpRd(RightBitWidth, PType.U16, (uint)shared))
            .WithChild(new TestNode("vortex.primitive").WithBuffer(0))
            .WithChild(new TestNode("vortex.primitive").WithBuffer(1));

        using DecodeHarness harness = DecodeHarness.Load(root, codes, right);
        DType f64 = harness.Types.Primitive(PType.F64, Nullability.NonNullable);
        CanonicalNode node = harness.Node(harness.DecodeRoot(f64, 3));

        Assert.Equal(originals, Values(node, 3));
    }

    [Fact]
    public void PatchesReplaceTheLeftPartRatherThanTheValue()
    {
        // Unlike vortex.alp, a patch here carries raw HIGH BITS, and the row is recombined with the
        // right part it already had. Row 1's high bits are not in the dictionary.
        double[] originals = [1.0, 1e300, 1.02];
        ulong shared = BitConverter.DoubleToUInt64Bits(1.0) >> RightBitWidth;
        ulong exceptional = BitConverter.DoubleToUInt64Bits(1e300) >> RightBitWidth;

        byte[] codes = TestBuffers.UInt16(0, 0, 0);
        byte[] right = RightParts(originals);
        byte[] indices = TestBuffers.UInt32(1);
        byte[] patchValues = TestBuffers.UInt16((ushort)exceptional);

        TestNode root = new TestNode("vortex.alprd")
            .WithMetadata(TestMetadata.AlpRd(
                RightBitWidth, PType.U16, PatchesMetadata.Create(1, 0, PType.U32), (uint)shared))
            .WithChild(new TestNode("vortex.primitive").WithBuffer(0))
            .WithChild(new TestNode("vortex.primitive").WithBuffer(1))
            .WithChild(new TestNode("vortex.primitive").WithBuffer(2))
            .WithChild(new TestNode("vortex.primitive").WithBuffer(3));

        using DecodeHarness harness = DecodeHarness.Load(root, codes, right, indices, patchValues);
        DType f64 = harness.Types.Primitive(PType.F64, Nullability.NonNullable);
        CanonicalNode node = harness.Node(harness.DecodeRoot(f64, 3));

        Assert.Equal(originals, Values(node, 3));
    }

    [Fact]
    public void ARangeDecodesAsTheWholeArraySliced()
    {
        // Patches at the first row, the last, and on both sides of the ranges' edges: a range must
        // hold exactly the patches of its own rows, rebased to its first.
        int[] exceptions = [0, 5, 17, 18, 40, 63];
        double[] originals = new double[64];
        for (int i = 0; i < originals.Length; i++)
        {
            originals[i] = Array.IndexOf(exceptions, i) >= 0 ? 1e300 * (i + 1) : 1.0 + (i * 1e-9);
        }

        ulong shared = BitConverter.DoubleToUInt64Bits(1.0) >> RightBitWidth;
        byte[] codes = new byte[originals.Length * sizeof(ushort)];
        uint[] positions = new uint[exceptions.Length];
        ushort[] highs = new ushort[exceptions.Length];
        for (int p = 0; p < exceptions.Length; p++)
        {
            positions[p] = (uint)exceptions[p];
            highs[p] = (ushort)(BitConverter.DoubleToUInt64Bits(originals[exceptions[p]]) >> RightBitWidth);
        }

        TestNode root = new TestNode("vortex.alprd")
            .WithMetadata(TestMetadata.AlpRd(
                RightBitWidth, PType.U16, PatchesMetadata.Create((ulong)exceptions.Length, 0, PType.U32), (uint)shared))
            .WithChild(new TestNode("vortex.primitive").WithBuffer(0))
            .WithChild(new TestNode("vortex.primitive").WithBuffer(1))
            .WithChild(new TestNode("vortex.primitive").WithBuffer(2))
            .WithChild(new TestNode("vortex.primitive").WithBuffer(3));

        using DecodeHarness harness = DecodeHarness.Load(
            root, codes, RightParts(originals), TestBuffers.UInt32(positions), TestBuffers.UInt16(highs));
        DType f64 = harness.Types.Primitive(PType.F64, Nullability.NonNullable);
        ArrayNode node = harness.Scan.Nodes.Root;
        Assert.True(harness.Scan.Decode.DecodesRange(in node));
        Assert.Equal(originals, Values(harness.Node(harness.DecodeRoot(f64, originals.Length)), originals.Length));

        foreach ((int start, int count) in new[] { (0, 64), (0, 1), (5, 1), (4, 13), (17, 24), (41, 23), (63, 1), (19, 21) })
        {
            CanonicalNode range = harness.Node(
                harness.Scan.Decode.DecodeRootRange(in node, f64, originals.Length, start, count, keepEncoding: false));
            Assert.Equal(originals.AsSpan(start, count).ToArray(), Values(range, count));
        }
    }

    [Fact]
    public void ASelectionTakesThePatchesOfItsRowsAndNoOthers()
    {
        // A patch every fourth row and two more: a selection of a few rows looks each of them up
        // among the patches, a larger one walks them, and both put every patch where its row landed.
        const int rows = 256;
        System.Collections.Generic.List<int> exceptions = [5, 255];
        for (int i = 0; i < rows; i += 4)
        {
            exceptions.Add(i);
        }

        exceptions.Sort();
        double[] originals = new double[rows];
        for (int i = 0; i < rows; i++)
        {
            originals[i] = exceptions.Contains(i) ? 1e300 * (i + 1) : 1.0 + (i * 1e-9);
        }

        ulong shared = BitConverter.DoubleToUInt64Bits(1.0) >> RightBitWidth;
        uint[] positions = new uint[exceptions.Count];
        ushort[] highs = new ushort[exceptions.Count];
        for (int p = 0; p < exceptions.Count; p++)
        {
            positions[p] = (uint)exceptions[p];
            highs[p] = (ushort)(BitConverter.DoubleToUInt64Bits(originals[exceptions[p]]) >> RightBitWidth);
        }

        TestNode root = new TestNode("vortex.alprd")
            .WithMetadata(TestMetadata.AlpRd(
                RightBitWidth, PType.U16, PatchesMetadata.Create((ulong)exceptions.Count, 0, PType.U32), (uint)shared))
            .WithChild(new TestNode("vortex.primitive").WithBuffer(0))
            .WithChild(new TestNode("vortex.primitive").WithBuffer(1))
            .WithChild(new TestNode("vortex.primitive").WithBuffer(2))
            .WithChild(new TestNode("vortex.primitive").WithBuffer(3));

        using DecodeHarness harness = DecodeHarness.Load(
            root, new byte[rows * sizeof(ushort)], RightParts(originals), TestBuffers.UInt32(positions), TestBuffers.UInt16(highs));
        DType f64 = harness.Types.Primitive(PType.F64, Nullability.NonNullable);
        ArrayNode node = harness.Scan.Nodes.Root;

        int[] every3 = new int[86];
        for (int i = 0; i < every3.Length; i++)
        {
            every3[i] = i * 3;
        }

        foreach (int[] wanted in new[] { new[] { 0 }, [3], [4, 5], [0, 17, 128, 255], [1, 2, 3, 6, 7], [254, 255], every3 })
        {
            CanonicalNode taken = harness.Node(harness.Scan.Decode.DecodeRootSelected(in node, f64, rows, wanted));
            double[] expected = new double[wanted.Length];
            for (int i = 0; i < wanted.Length; i++)
            {
                expected[i] = originals[wanted[i]];
            }

            Assert.Equal(expected, Values(taken, wanted.Length));
        }
    }

    [Fact]
    public void ValidityComesFromTheLeftPartsChild()
    {
        byte[] codes = TestBuffers.UInt16(0, 0, 0);
        byte[] right = RightParts([1.0, 1.01, 1.02]);
        byte[] validity = TestBuffers.Bitmap(true, false, true);
        ulong shared = BitConverter.DoubleToUInt64Bits(1.0) >> RightBitWidth;

        TestNode root = new TestNode("vortex.alprd")
            .WithMetadata(TestMetadata.AlpRd(RightBitWidth, PType.U16, (uint)shared))
            .WithChild(new TestNode("vortex.primitive")
                .WithBuffer(0)
                .WithChild(new TestNode("vortex.bool").WithBuffer(2)))
            .WithChild(new TestNode("vortex.primitive").WithBuffer(1));

        using DecodeHarness harness = DecodeHarness.Load(root, codes, right, validity);
        DType f64 = harness.Types.Primitive(PType.F64, Nullability.Nullable);
        CanonicalNode node = harness.Node(harness.DecodeRoot(f64, 3));

        Assert.True(harness.IsValid(node, 0));
        Assert.False(harness.IsValid(node, 1));
    }

    [Fact]
    public void ACodePastTheDictionaryIsRejected()
    {
        // Upstream masks the code into a zero-filled table on its fast path and panics on its
        // patched one. We refuse both, because a reader must not invent a value.
        byte[] codes = TestBuffers.UInt16(0, 5);
        byte[] right = RightParts([1.0, 1.01]);

        TestNode root = Root(TestMetadata.AlpRd(RightBitWidth, PType.U16, 1, 2));
        using DecodeHarness harness = DecodeHarness.Load(root, codes, right);
        DType f64 = harness.Types.Primitive(PType.F64, Nullability.NonNullable);
        Assert.Throws<VortexFormatException>(() => harness.DecodeRoot(f64, 2));
    }

    [Fact]
    public void ACodePastTheDictionaryIsRejectedAtItsRowAmongMany()
    {
        // Enough rows for the codes to be checked a vector at a time: the one past the dictionary
        // is still found, and named at its own row.
        ushort[] codes = new ushort[40];
        double[] values = new double[40];
        for (int i = 0; i < codes.Length; i++)
        {
            codes[i] = (ushort)(i % 2);
            values[i] = 1.0 + (i / 100.0);
        }

        codes[25] = 7;
        TestNode root = Root(TestMetadata.AlpRd(RightBitWidth, PType.U16, 1, 2));
        using DecodeHarness harness = DecodeHarness.Load(root, TestBuffers.UInt16(codes), RightParts(values));
        DType f64 = harness.Types.Primitive(PType.F64, Nullability.NonNullable);
        VortexFormatException error = Assert.Throws<VortexFormatException>(() => harness.DecodeRoot(f64, codes.Length));
        Assert.Contains("row 25 ", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(64u)]
    [InlineData(100u)]
    public void ARightBitWidthAtOrPastTheFloatsWidthIsRejected(uint rightBitWidth)
    {
        // `left << 64` on a u64 is undefined in C and wraps the shift amount in release Rust:
        // neither is something to reproduce.
        byte[] codes = TestBuffers.UInt16(0);
        byte[] right = TestBuffers.UInt64(0);

        TestNode root = Root(TestMetadata.AlpRd(rightBitWidth, PType.U16, 1));
        using DecodeHarness harness = DecodeHarness.Load(root, codes, right);
        DType f64 = harness.Types.Primitive(PType.F64, Nullability.NonNullable);
        Assert.Throws<VortexFormatException>(() => harness.DecodeRoot(f64, 1));
    }

    [Fact]
    public void AnEmptyDictionaryIsRejected()
    {
        byte[] codes = TestBuffers.UInt16(0);
        byte[] right = TestBuffers.UInt64(0);

        TestNode root = Root(TestMetadata.AlpRd(RightBitWidth, PType.U16));
        using DecodeHarness harness = DecodeHarness.Load(root, codes, right);
        DType f64 = harness.Types.Primitive(PType.F64, Nullability.NonNullable);
        Assert.Throws<VortexFormatException>(() => harness.DecodeRoot(f64, 1));
    }

    [Fact]
    public void ADictionaryLargerThanTheEncodingAllowsIsRejected()
    {
        // MAX_DICT_SIZE is 8. A message carrying more is refused rather than heap-allocated from.
        byte[] codes = TestBuffers.UInt16(0);
        byte[] right = TestBuffers.UInt64(0);

        TestNode root = Root(TestMetadata.AlpRd(RightBitWidth, PType.U16, 1, 2, 3, 4, 5, 6, 7, 8, 9));
        using DecodeHarness harness = DecodeHarness.Load(root, codes, right);
        DType f64 = harness.Types.Primitive(PType.F64, Nullability.NonNullable);
        Assert.Throws<VortexFormatException>(() => harness.DecodeRoot(f64, 1));
    }

    [Fact]
    public void ASignedLeftPartsPTypeIsRejected()
    {
        byte[] codes = TestBuffers.Int16(0);
        byte[] right = TestBuffers.UInt64(0);

        TestNode root = Root(TestMetadata.AlpRd(RightBitWidth, PType.I16, 1));
        using DecodeHarness harness = DecodeHarness.Load(root, codes, right);
        DType f64 = harness.Types.Primitive(PType.F64, Nullability.NonNullable);
        Assert.Throws<VortexFormatException>(() => harness.DecodeRoot(f64, 1));
    }

    [Fact]
    public void AnIntegerDTypeIsRejected()
    {
        byte[] codes = TestBuffers.UInt16(0);
        byte[] right = TestBuffers.UInt64(0);

        TestNode root = Root(TestMetadata.AlpRd(RightBitWidth, PType.U16, 1));
        using DecodeHarness harness = DecodeHarness.Load(root, codes, right);
        DType i64 = harness.Types.Primitive(PType.I64, Nullability.NonNullable);
        Assert.Throws<VortexFormatException>(() => harness.DecodeRoot(i64, 1));
    }

    [Fact]
    public void AnEmptyArrayDecodesToAnEmptyFloatColumn()
    {
        TestNode root = Root(TestMetadata.AlpRd(RightBitWidth, PType.U16, 1));
        using DecodeHarness harness = DecodeHarness.Load(root, Array.Empty<byte>(), Array.Empty<byte>());
        DType f64 = harness.Types.Primitive(PType.F64, Nullability.NonNullable);
        CanonicalNode node = harness.Node(harness.DecodeRoot(f64, 0));

        Assert.Equal(CanonicalKind.Primitive, node.Kind);
        Assert.Equal(0, node.Length);
    }

    // ------------------------------------------------------------------------------- fixtures

    /// <summary>Cuts each double at <see cref="RightBitWidth"/> and decodes the halves back.</summary>
    private static double[] RoundTrip(double[] originals)
    {
        uint[] dictionary = new uint[originals.Length];
        byte[] codes = new byte[originals.Length * sizeof(ushort)];
        for (int i = 0; i < originals.Length; i++)
        {
            dictionary[i] = (uint)(BitConverter.DoubleToUInt64Bits(originals[i]) >> RightBitWidth);
            BinaryPrimitives.WriteUInt16LittleEndian(codes.AsSpan(i * sizeof(ushort)), (ushort)i);
        }

        TestNode root = Root(TestMetadata.AlpRd(RightBitWidth, PType.U16, dictionary));
        using DecodeHarness harness = DecodeHarness.Load(root, codes, RightParts(originals));
        DType f64 = harness.Types.Primitive(PType.F64, Nullability.NonNullable);
        return Values(harness.Node(harness.DecodeRoot(f64, originals.Length)), originals.Length);
    }

    private static TestNode Root(byte[] metadata) =>
        new TestNode("vortex.alprd")
            .WithMetadata(metadata)
            .WithChild(new TestNode("vortex.primitive").WithBuffer(0))
            .WithChild(new TestNode("vortex.primitive").WithBuffer(1));

    private static byte[] RightParts(double[] originals)
    {
        byte[] right = new byte[originals.Length * sizeof(ulong)];
        ulong mask = (1UL << RightBitWidth) - 1;
        for (int i = 0; i < originals.Length; i++)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(
                right.AsSpan(i * sizeof(ulong)),
                BitConverter.DoubleToUInt64Bits(originals[i]) & mask);
        }

        return right;
    }

    private static double[] Values(CanonicalNode node, int length)
    {
        double[] values = new double[length];
        for (int i = 0; i < length; i++)
        {
            values[i] = BinaryPrimitives.ReadDoubleLittleEndian(node.Values.Span.Slice(i * 8, 8));
        }

        return values;
    }
}
