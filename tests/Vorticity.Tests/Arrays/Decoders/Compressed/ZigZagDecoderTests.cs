using System;
using System.Buffers.Binary;
using Vorticity.Arrays;
using Vorticity.Types;
using Xunit;

namespace Vorticity.Tests.Arrays.Decoders.Compressed;

public sealed class ZigZagDecoderTests
{
    [Fact]
    public void DecodesTheSignedBoundaryValues()
    {
        // zigzag(n) = (n << 1) ^ (n >> 63): 0, -1, 1 and i64::MIN encode as 0, 1, 2 and u64::MAX.
        byte[] encoded = TestBuffers.UInt64(0, 1, 2, 3, ulong.MaxValue, ulong.MaxValue - 1);
        TestNode root = new TestNode("vortex.zigzag")
            .WithChild(new TestNode("vortex.primitive").WithBuffer(0));

        using DecodeHarness harness = DecodeHarness.Load(root, encoded);
        DType i64 = harness.Types.Primitive(PType.I64, Nullability.NonNullable);
        CanonicalNode node = harness.Node(harness.DecodeRoot(i64, 6));

        long[] values = new long[6];
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = BinaryPrimitives.ReadInt64LittleEndian(node.Values.Span.Slice(i * 8, 8));
        }

        Assert.Equal([0L, -1L, 1L, -2L, long.MinValue, long.MaxValue], values);
    }

    [Theory]
    [InlineData(PType.I8, PType.U8)]
    [InlineData(PType.I16, PType.U16)]
    [InlineData(PType.I32, PType.U32)]
    internal void DecodesEveryNarrowWidth(PType signed, PType unsigned)
    {
        int width = signed.ByteWidth();
        byte[] encoded = new byte[3 * width];
        // The encoded 0, 1 and 2 decode to 0, -1 and 1.
        encoded[width] = 1;
        encoded[2 * width] = 2;

        TestNode root = new TestNode("vortex.zigzag")
            .WithChild(new TestNode("vortex.primitive").WithBuffer(0));

        using DecodeHarness harness = DecodeHarness.Load(root, encoded);
        DType dtype = harness.Types.Primitive(signed, Nullability.NonNullable);
        CanonicalNode node = harness.Node(harness.DecodeRoot(dtype, 3));

        Assert.Equal(signed, node.PType);
        Assert.Equal(unsigned, CompressedTestReflection.UnsignedOf(signed));
        Assert.Equal(0L, ReadSigned(node, 0, width));
        Assert.Equal(-1L, ReadSigned(node, 1, width));
        Assert.Equal(1L, ReadSigned(node, 2, width));
    }

    [Fact]
    public void AnUnsignedDTypeIsRejected()
    {
        byte[] encoded = TestBuffers.UInt32(1, 2);
        TestNode root = new TestNode("vortex.zigzag")
            .WithChild(new TestNode("vortex.primitive").WithBuffer(0));

        using DecodeHarness harness = DecodeHarness.Load(root, encoded);
        DType u32 = harness.Types.Primitive(PType.U32, Nullability.NonNullable);
        Assert.Throws<VortexFormatException>(() => harness.DecodeRoot(u32, 2));
    }

    [Fact]
    public void NonEmptyMetadataIsRejected()
    {
        byte[] encoded = TestBuffers.Int32(1, 2);
        TestNode root = new TestNode("vortex.zigzag")
            .WithMetadata([0x08, 0x01])
            .WithChild(new TestNode("vortex.primitive").WithBuffer(0));

        using DecodeHarness harness = DecodeHarness.Load(root, encoded);
        DType i32 = harness.Types.Primitive(PType.I32, Nullability.NonNullable);
        Assert.Throws<VortexFormatException>(() => harness.DecodeRoot(i32, 2));
    }

    [Fact]
    public void NullabilityIsCarriedThroughToTheEncodedChild()
    {
        byte[] encoded = TestBuffers.UInt32(0, 2, 4);
        byte[] validity = TestBuffers.Bitmap(true, false, true);
        TestNode root = new TestNode("vortex.zigzag")
            .WithChild(new TestNode("vortex.primitive")
                .WithBuffer(0)
                .WithChild(new TestNode("vortex.bool").WithBuffer(1)));

        using DecodeHarness harness = DecodeHarness.Load(root, encoded, validity);
        DType i32 = harness.Types.Primitive(PType.I32, Nullability.Nullable);
        CanonicalNode node = harness.Node(harness.DecodeRoot(i32, 3));

        Assert.Equal(ValidityKind.Bitmap, node.Validity.Kind);
        Assert.Equal([0, 1, 2], ForDecoderTests.ReadInt32(node));
    }

    [Fact]
    public void ZeroRowsDecodeToAnEmptyArray()
    {
        TestNode root = new TestNode("vortex.zigzag")
            .WithChild(new TestNode("vortex.primitive").WithBuffer(0));

        using DecodeHarness harness = DecodeHarness.Load(root, Array.Empty<byte>());
        DType i32 = harness.Types.Primitive(PType.I32, Nullability.NonNullable);
        Assert.Equal(0, harness.Node(harness.DecodeRoot(i32, 0)).Length);
    }

    private static long ReadSigned(CanonicalNode node, int index, int width) => width switch
    {
        1 => (sbyte)node.Values.Span[index],
        2 => BinaryPrimitives.ReadInt16LittleEndian(node.Values.Span.Slice(index * 2, 2)),
        4 => BinaryPrimitives.ReadInt32LittleEndian(node.Values.Span.Slice(index * 4, 4)),
        _ => BinaryPrimitives.ReadInt64LittleEndian(node.Values.Span.Slice(index * 8, 8)),
    };
}

internal static class CompressedTestReflection
{
    internal static PType UnsignedOf(PType signed) => signed switch
    {
        PType.I8 => PType.U8,
        PType.I16 => PType.U16,
        PType.I32 => PType.U32,
        _ => PType.U64,
    };
}
