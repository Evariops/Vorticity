using System;
using System.Buffers.Binary;
using System.Text;
using Vorticity.Arrays;
using Vorticity.Types;
using Xunit;

namespace Vorticity.Tests.Arrays.Decoders.Compressed;

public sealed class DictDecoderTests
{
    [Fact]
    public void GathersValuesThroughTheCodes()
    {
        TestNode root = Node(TestMetadata.Dict(3, PType.U8, false));
        using DecodeHarness harness = DecodeHarness.Load(
            root, TestBuffers.Bytes(0, 2, 1, 2, 0), TestBuffers.Int32(10, 20, 30));

        DType i32 = harness.Types.Primitive(PType.I32, Nullability.NonNullable);
        CanonicalNode node = harness.Node(harness.DecodeRoot(i32, 5));
        Assert.Equal([10, 30, 20, 30, 10], ForDecoderTests.ReadInt32(node));
    }

    [Fact]
    public void ACodeEqualToValuesLengthIsRejected()
    {
        TestNode root = Node(TestMetadata.Dict(3, PType.U8, false));
        using DecodeHarness harness = DecodeHarness.Load(
            root, TestBuffers.Bytes(0, 3), TestBuffers.Int32(10, 20, 30));

        DType i32 = harness.Types.Primitive(PType.I32, Nullability.NonNullable);
        Assert.Throws<VortexFormatException>(() => harness.DecodeRoot(i32, 2));
    }

    [Fact]
    public void ANegativeCodeIsRejected()
    {
        TestNode root = Node(TestMetadata.Dict(3, PType.I8, false));
        using DecodeHarness harness = DecodeHarness.Load(
            root, TestBuffers.Bytes(0, 0xFF), TestBuffers.Int32(10, 20, 30));

        DType i32 = harness.Types.Primitive(PType.I32, Nullability.NonNullable);
        Assert.Throws<VortexFormatException>(() => harness.DecodeRoot(i32, 2));
    }

    [Theory]
    [InlineData(null, Nullability.Nullable, Nullability.Nullable)]
    [InlineData(false, Nullability.Nullable, Nullability.NonNullable)]
    [InlineData(true, Nullability.Nullable, Nullability.Nullable)]
    [InlineData(null, Nullability.NonNullable, Nullability.NonNullable)]
    [InlineData(false, Nullability.NonNullable, Nullability.NonNullable)]
    [InlineData(true, Nullability.NonNullable, Nullability.Nullable)]
    public void IsNullableCodesDecidesTheCodesChildDType(
        bool? isNullableCodes, Nullability arrayNullability, Nullability expectedCodesNullability)
    {
        // Absent is a back-compat fallback to the ARRAY's nullability and is not
        // the same as `false`. Getting it wrong changes the codes child's dtype, which changes how
        // its own validity child is interpreted, which silently changes values.
        TestNode root = Node(TestMetadata.Dict(2, PType.U8, isNullableCodes));
        using DecodeHarness harness = DecodeHarness.Load(
            root, TestBuffers.Bytes(0, 1), TestBuffers.Int32(10, 20));

        DType dtype = harness.Types.Primitive(PType.I32, arrayNullability);
        harness.DecodeRoot(dtype, 2);

        Assert.Equal(expectedCodesNullability, FindCodesNode(harness).DType.Nullability);
    }

    [Fact]
    public void NullableCodesOverANonNullableArrayRejectTheNullRow()
    {
        TestNode root = new TestNode("vortex.dict")
            .WithMetadata(TestMetadata.Dict(2, PType.U8, true))
            .WithChild(new TestNode("vortex.primitive")
                .WithBuffer(0)
                .WithChild(new TestNode("vortex.bool").WithBuffer(2)))
            .WithChild(new TestNode("vortex.primitive").WithBuffer(1));

        using DecodeHarness harness = DecodeHarness.Load(
            root, TestBuffers.Bytes(0, 1), TestBuffers.Int32(10, 20),
            TestBuffers.Bitmap(true, false));

        DType nonNullable = harness.Types.Primitive(PType.I32, Nullability.NonNullable);
        Assert.Throws<VortexFormatException>(() => harness.DecodeRoot(nonNullable, 2));
    }

    private static CanonicalNode FindCodesNode(DecodeHarness harness)
    {
        for (int i = 0; i < harness.Scan.Canonical.NodeCount; i++)
        {
            CanonicalNode node = harness.Node(i);
            if (node.Kind == CanonicalKind.Primitive && node.PType == PType.U8)
            {
                return node;
            }
        }

        Assert.Fail("no codes node was decoded");
        return default;
    }

    [Fact]
    public void ANullCodeProducesANullRow()
    {
        TestNode root = new TestNode("vortex.dict")
            .WithMetadata(TestMetadata.Dict(2, PType.U8, true))
            .WithChild(new TestNode("vortex.primitive")
                .WithBuffer(0)
                .WithChild(new TestNode("vortex.bool").WithBuffer(2)))
            .WithChild(new TestNode("vortex.primitive").WithBuffer(1));

        using DecodeHarness harness = DecodeHarness.Load(
            root,
            TestBuffers.Bytes(0, 1, 0),
            TestBuffers.Int32(10, 20),
            TestBuffers.Bitmap(true, false, true));

        DType i32 = harness.Types.Primitive(PType.I32, Nullability.Nullable);
        CanonicalNode node = harness.Node(harness.DecodeRoot(i32, 3));

        Assert.Equal(ValidityKind.Bitmap, node.Validity.Kind);
        CanonicalNode bits = harness.Node(node.Validity.CanonicalNodeIndex);
        Assert.Equal(0b101, bits.Bits.Span[0] & 0b111);
        Assert.Equal([10, 0, 10], ForDecoderTests.ReadInt32(node));
    }

    [Fact]
    public void ANullDictionaryValueMakesEveryReferencingRowNull()
    {
        TestNode root = new TestNode("vortex.dict")
            .WithMetadata(TestMetadata.Dict(2, PType.U8, false))
            .WithChild(new TestNode("vortex.primitive").WithBuffer(0))
            .WithChild(new TestNode("vortex.primitive")
                .WithBuffer(1)
                .WithChild(new TestNode("vortex.bool").WithBuffer(2)));

        using DecodeHarness harness = DecodeHarness.Load(
            root,
            TestBuffers.Bytes(0, 1, 1, 0),
            TestBuffers.Int32(10, 20),
            TestBuffers.Bitmap(true, false));

        DType i32 = harness.Types.Primitive(PType.I32, Nullability.Nullable);
        CanonicalNode node = harness.Node(harness.DecodeRoot(i32, 4));
        CanonicalNode bits = harness.Node(node.Validity.CanonicalNodeIndex);
        Assert.Equal(0b1001, bits.Bits.Span[0] & 0b1111);
    }

    [Fact]
    public void FloatKeysAreCopiedByBitPatternAndNeverCanonicalized()
    {
        // Corpus manifest caveat 2: distributions/float_specials_f64_* keep -0.0 and +0.0 and
        // several NaN payloads as distinct keys. A decoder that compares by IEEE equality, or that
        // normalizes a NaN, produces a different dictionary and a different answer.
        double[] keys = [0.0, -0.0, double.NaN, BitConverter.UInt64BitsToDouble(0x7FF8_0000_0000_0001UL)];
        byte[] values = new byte[keys.Length * 8];
        for (int i = 0; i < keys.Length; i++)
        {
            BinaryPrimitives.WriteDoubleLittleEndian(values.AsSpan(i * 8), keys[i]);
        }

        TestNode root = Node(TestMetadata.Dict((uint)keys.Length, PType.U8, false));
        using DecodeHarness harness = DecodeHarness.Load(root, TestBuffers.Bytes(3, 1, 0, 2), values);

        DType f64 = harness.Types.Primitive(PType.F64, Nullability.NonNullable);
        CanonicalNode node = harness.Node(harness.DecodeRoot(f64, 4));

        ulong[] expected =
        [
            0x7FF8_0000_0000_0001UL,
            BitConverter.DoubleToUInt64Bits(-0.0),
            BitConverter.DoubleToUInt64Bits(0.0),
            BitConverter.DoubleToUInt64Bits(double.NaN),
        ];

        for (int i = 0; i < expected.Length; i++)
        {
            Assert.Equal(
                expected[i],
                BinaryPrimitives.ReadUInt64LittleEndian(node.Values.Span.Slice(i * 8, 8)));
        }
    }

    [Fact]
    public void GathersVarBinViewValues()
    {
        // encodings/dict is exactly this shape: utf8 values under a varbinview child.
        byte[] data = Encoding.UTF8.GetBytes("alphabravocharlie-a-very-long-value-here");
        byte[] views = new byte[3 * 16];
        WriteView(views.AsSpan(0, 16), data, 0, 5);            // "alpha", inline
        WriteView(views.AsSpan(16, 16), data, 5, 5);           // "bravo", inline
        WriteView(views.AsSpan(32, 16), data, 10, data.Length - 10);  // long, referenced

        TestNode root = new TestNode("vortex.dict")
            .WithMetadata(TestMetadata.Dict(3, PType.U8, false))
            .WithChild(new TestNode("vortex.primitive").WithBuffer(0))
            .WithChild(new TestNode("vortex.varbinview").WithBuffer(1).WithBuffer(2));

        using DecodeHarness harness = DecodeHarness.Load(
            root, TestBuffers.Bytes(2, 0, 1, 2), data, views);

        DType utf8 = harness.Types.Utf8(Nullability.NonNullable);
        CanonicalNode node = harness.Node(harness.DecodeRoot(utf8, 4));

        Assert.Equal(CanonicalKind.VarBinView, node.Kind);
        Assert.Equal(1, node.DataBufferCount);
        Assert.Equal("charlie-a-very-long-value-here", ReadValue(node, 0));
        Assert.Equal("alpha", ReadValue(node, 1));
        Assert.Equal("bravo", ReadValue(node, 2));
        Assert.Equal("charlie-a-very-long-value-here", ReadValue(node, 3));
    }

    [Fact]
    public void AWrongChildCountIsRejected()
    {
        TestNode root = new TestNode("vortex.dict")
            .WithMetadata(TestMetadata.Dict(1, PType.U8, false))
            .WithChild(new TestNode("vortex.primitive").WithBuffer(0));

        using DecodeHarness harness = DecodeHarness.Load(root, TestBuffers.Bytes(0));
        DType i32 = harness.Types.Primitive(PType.I32, Nullability.NonNullable);
        Assert.Throws<VortexFormatException>(() => harness.DecodeRoot(i32, 1));
    }

    [Fact]
    public void ABufferOnTheNodeIsRejected()
    {
        TestNode root = new TestNode("vortex.dict")
            .WithMetadata(TestMetadata.Dict(1, PType.U8, false))
            .WithBuffer(0)
            .WithChild(new TestNode("vortex.primitive").WithBuffer(0))
            .WithChild(new TestNode("vortex.primitive").WithBuffer(1));

        using DecodeHarness harness = DecodeHarness.Load(
            root, TestBuffers.Bytes(0), TestBuffers.Int32(1));
        DType i32 = harness.Types.Primitive(PType.I32, Nullability.NonNullable);
        Assert.Throws<VortexFormatException>(() => harness.DecodeRoot(i32, 1));
    }

    [Fact]
    public void AFloatCodesPTypeIsRejected()
    {
        TestNode root = Node(TestMetadata.Dict(1, PType.F32, false));
        using DecodeHarness harness = DecodeHarness.Load(
            root, TestBuffers.UInt32(0), TestBuffers.Int32(1));
        DType i32 = harness.Types.Primitive(PType.I32, Nullability.NonNullable);
        Assert.Throws<VortexFormatException>(() => harness.DecodeRoot(i32, 1));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(1023)]
    [InlineData(1024)]
    [InlineData(1025)]
    public void DecodesTheBoundaryRowCounts(int rows)
    {
        byte[] codes = new byte[rows];
        for (int i = 0; i < rows; i++)
        {
            codes[i] = (byte)(i % 4);
        }

        TestNode root = Node(TestMetadata.Dict(4, PType.U8, false));
        using DecodeHarness harness = DecodeHarness.Load(
            root, codes, TestBuffers.Int32(100, 200, 300, 400));

        DType i32 = harness.Types.Primitive(PType.I32, Nullability.NonNullable);
        CanonicalNode node = harness.Node(harness.DecodeRoot(i32, rows));
        int[] decoded = ForDecoderTests.ReadInt32(node);
        for (int i = 0; i < rows; i++)
        {
            Assert.Equal(100 * ((i % 4) + 1), decoded[i]);
        }
    }

    internal static void WriteView(Span<byte> view, ReadOnlySpan<byte> data, int offset, int length)
    {
        view.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(view, (uint)length);
        if (length <= 12)
        {
            data.Slice(offset, length).CopyTo(view[4..]);
            return;
        }

        data.Slice(offset, 4).CopyTo(view[4..8]);
        BinaryPrimitives.WriteUInt32LittleEndian(view.Slice(8, 4), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(view.Slice(12, 4), (uint)offset);
    }

    internal static string ReadValue(CanonicalNode node, int row)
    {
        ReadOnlySpan<byte> view = node.Views.Span.Slice(row * 16, 16);
        int length = (int)BinaryPrimitives.ReadUInt32LittleEndian(view);
        if (length <= 12)
        {
            return Encoding.UTF8.GetString(view.Slice(4, length));
        }

        int buffer = (int)BinaryPrimitives.ReadUInt32LittleEndian(view.Slice(8, 4));
        int offset = (int)BinaryPrimitives.ReadUInt32LittleEndian(view.Slice(12, 4));
        return Encoding.UTF8.GetString(node.GetDataBuffer(buffer).Span.Slice(offset, length));
    }

    private static TestNode Node(byte[] metadata) =>
        new TestNode("vortex.dict")
            .WithMetadata(metadata)
            .WithChild(new TestNode("vortex.primitive").WithBuffer(0))
            .WithChild(new TestNode("vortex.primitive").WithBuffer(1));
}
