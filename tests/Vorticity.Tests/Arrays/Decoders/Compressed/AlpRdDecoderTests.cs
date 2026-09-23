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
