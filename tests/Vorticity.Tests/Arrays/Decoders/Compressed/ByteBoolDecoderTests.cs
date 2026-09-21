using System;
using Vorticity.Arrays;
using Vorticity.Types;
using Xunit;

namespace Vorticity.Tests.Arrays.Decoders.Compressed;

public sealed class ByteBoolDecoderTests
{
    [Fact]
    public void AnyNonZeroByteIsTrue()
    {
        // Upstream canonicalizes with `bytes.iter().map(|&b| b != 0)`, so 2 and 0xFF are both true.
        byte[] values = [0, 1, 2, 0xFF, 0, 0x80, 0, 0, 3];
        TestNode root = new TestNode("vortex.bytebool").WithBuffer(0);

        using DecodeHarness harness = DecodeHarness.Load(root, values);
        DType boolType = harness.Types.Bool(Nullability.NonNullable);
        CanonicalNode node = harness.Node(harness.DecodeRoot(boolType, 9));

        Assert.Equal(CanonicalKind.Bool, node.Kind);
        Assert.Equal(0, node.BitOffset);
        for (int i = 0; i < values.Length; i++)
        {
            bool expected = values[i] != 0;
            bool actual = (node.Bits.Span[i >> 3] & (1 << (i & 7))) != 0;
            Assert.Equal(expected, actual);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(1023)]
    [InlineData(1024)]
    [InlineData(1025)]
    [InlineData(8191)]
    [InlineData(8192)]
    [InlineData(8193)]
    public void DecodesTheBoundaryRowCounts(int rows)
    {
        byte[] values = new byte[rows];
        for (int i = 0; i < rows; i++)
        {
            values[i] = (byte)(i % 3 == 0 ? 1 : 0);
        }

        TestNode root = new TestNode("vortex.bytebool").WithBuffer(0);
        using DecodeHarness harness = DecodeHarness.Load(root, values);
        DType boolType = harness.Types.Bool(Nullability.NonNullable);
        CanonicalNode node = harness.Node(harness.DecodeRoot(boolType, rows));

        Assert.Equal(rows, node.Length);
        for (int i = 0; i < rows; i++)
        {
            bool actual = (node.Bits.Span[i >> 3] & (1 << (i & 7))) != 0;
            Assert.Equal(i % 3 == 0, actual);
        }
    }

    [Theory]
    [InlineData(3)]
    [InlineData(5)]
    public void AValuesBufferOfTheWrongLengthIsRejected(int bufferLength)
    {
        byte[] values = new byte[bufferLength];
        TestNode root = new TestNode("vortex.bytebool").WithBuffer(0);

        using DecodeHarness harness = DecodeHarness.Load(root, values);
        DType boolType = harness.Types.Bool(Nullability.NonNullable);
        Assert.Throws<VortexFormatException>(() => harness.DecodeRoot(boolType, 4));
    }

    [Fact]
    public void NonEmptyMetadataIsRejected()
    {
        TestNode root = new TestNode("vortex.bytebool").WithMetadata([0x08, 0x00]).WithBuffer(0);
        using DecodeHarness harness = DecodeHarness.Load(root, new byte[2]);
        DType boolType = harness.Types.Bool(Nullability.NonNullable);
        Assert.Throws<VortexFormatException>(() => harness.DecodeRoot(boolType, 2));
    }

    [Fact]
    public void ANonBoolDTypeIsRejected()
    {
        TestNode root = new TestNode("vortex.bytebool").WithBuffer(0);
        using DecodeHarness harness = DecodeHarness.Load(root, new byte[2]);
        DType i32 = harness.Types.Primitive(PType.I32, Nullability.NonNullable);
        Assert.Throws<VortexFormatException>(() => harness.DecodeRoot(i32, 2));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void AWrongBufferCountIsRejected(int buffers)
    {
        TestNode root = new TestNode("vortex.bytebool");
        for (int i = 0; i < buffers; i++)
        {
            root.WithBuffer(i);
        }

        using DecodeHarness harness = DecodeHarness.Load(root, new byte[2], new byte[2]);
        DType boolType = harness.Types.Bool(Nullability.NonNullable);
        Assert.Throws<VortexFormatException>(() => harness.DecodeRoot(boolType, 2));
    }

    [Fact]
    public void AValidityChildIsHonoured()
    {
        byte[] values = [1, 1, 0, 1];
        byte[] validity = TestBuffers.Bitmap(true, false, true, false);
        TestNode root = new TestNode("vortex.bytebool")
            .WithBuffer(0)
            .WithChild(new TestNode("vortex.bool").WithBuffer(1));

        using DecodeHarness harness = DecodeHarness.Load(root, values, validity);
        DType boolType = harness.Types.Bool(Nullability.Nullable);
        CanonicalNode node = harness.Node(harness.DecodeRoot(boolType, 4));

        Assert.Equal(ValidityKind.Bitmap, node.Validity.Kind);
        CanonicalNode bits = harness.Node(node.Validity.CanonicalNodeIndex);
        Assert.Equal(0b0000_0101, bits.Bits.Span[0] & 0b0000_1111);
    }

    [Fact]
    public void AnAllFalseConstantValidityChildCollapsesToAllInvalid()
    {
        // AllInvalid reaches the wire as vortex.constant(false).
        ScalarStore store = new();
        byte[] falseScalar = TestMetadata.Scalar(store.Bool(false));
        TestNode root = new TestNode("vortex.bytebool")
            .WithBuffer(0)
            .WithChild(new TestNode("vortex.constant").WithBuffer(1));

        using DecodeHarness harness = DecodeHarness.Load(root, new byte[4], falseScalar);
        DType boolType = harness.Types.Bool(Nullability.Nullable);
        CanonicalNode node = harness.Node(harness.DecodeRoot(boolType, 4));
        Assert.Equal(ValidityKind.AllInvalid, node.Validity.Kind);
    }

    [Fact]
    public void ZeroRowsDecodeToAnEmptyArray()
    {
        TestNode root = new TestNode("vortex.bytebool").WithBuffer(0);
        using DecodeHarness harness = DecodeHarness.Load(root, Array.Empty<byte>());
        DType boolType = harness.Types.Bool(Nullability.NonNullable);
        Assert.Equal(0, harness.Node(harness.DecodeRoot(boolType, 0)).Length);
    }
}
