using System;
using System.Buffers.Binary;
using Vorticity.Arrays;
using Vorticity.Arrays.Decoders.Compressed;
using Vorticity.Arrays.Metadata;
using Vorticity.Types;
using Xunit;

namespace Vorticity.Tests.Arrays.Decoders.Compressed;

public sealed class BitPackedDecoderTests
{
    [Theory]
    [InlineData(PType.U8, 8)]
    [InlineData(PType.U16, 16)]
    [InlineData(PType.U32, 32)]
    [InlineData(PType.U64, 64)]
    public void RoundTripsEveryBitWidthThroughARealBlob(PType ptype, int elementBits)
    {
        for (int bitWidth = 0; bitWidth <= elementBits; bitWidth++)
        {
            ulong[] values = TestPacking.Ramp(1, bitWidth);
            byte[] packed = TestPacking.Pack(values, bitWidth, elementBits);

            TestNode root = new TestNode("fastlanes.bitpacked")
                .WithMetadata(TestMetadata.BitPacked((uint)bitWidth, 0))
                .WithBuffer(0);

            using DecodeHarness harness = DecodeHarness.Load(root, packed);
            DType dtype = harness.Types.Primitive(ptype, Nullability.NonNullable);
            CanonicalNode node = harness.Node(harness.DecodeRoot(dtype, FastLanes.BlockSize));

            for (int i = 0; i < FastLanes.BlockSize; i++)
            {
                Assert.Equal(values[i], ReadUnsigned(node, i, elementBits / 8));
            }
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(1023)]
    public void AnOffsetSkipsTheLeadingRowsOfTheFirstBlock(int offset)
    {
        const int bitWidth = 11;
        const int elementBits = 32;
        int rows = (2 * FastLanes.BlockSize) - offset;

        ulong[] values = TestPacking.Ramp(2, bitWidth);
        byte[] packed = TestPacking.Pack(values, bitWidth, elementBits);

        TestNode root = new TestNode("fastlanes.bitpacked")
            .WithMetadata(TestMetadata.BitPacked(bitWidth, (uint)offset))
            .WithBuffer(0);

        using DecodeHarness harness = DecodeHarness.Load(root, packed);
        DType u32 = harness.Types.Primitive(PType.U32, Nullability.NonNullable);
        CanonicalNode node = harness.Node(harness.DecodeRoot(u32, rows));

        for (int i = 0; i < rows; i++)
        {
            Assert.Equal(values[offset + i], ReadUnsigned(node, i, 4));
        }
    }

    [Fact]
    public void AnOffsetOf1024IsRejectedByTheMetadataCodec()
    {
        // BitPackedMetadata's own domain check: `offset` must be < 1024.
        Assert.Throws<ArgumentOutOfRangeException>(() => new BitPackedMetadata(4, 1024));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    public void APackedBufferOfTheWrongLengthIsRejected(int delta)
    {
        const int bitWidth = 5;
        byte[] packed = new byte[FastLanes.BlockByteLength(bitWidth) + delta];
        TestNode root = new TestNode("fastlanes.bitpacked")
            .WithMetadata(TestMetadata.BitPacked(bitWidth, 0))
            .WithBuffer(0);

        using DecodeHarness harness = DecodeHarness.Load(root, packed);
        DType u32 = harness.Types.Primitive(PType.U32, Nullability.NonNullable);
        Assert.Throws<VortexFormatException>(() => harness.DecodeRoot(u32, FastLanes.BlockSize));
    }

    [Fact]
    public void ABitWidthWiderThanTheDTypeIsRejected()
    {
        byte[] packed = new byte[FastLanes.BlockByteLength(9)];
        TestNode root = new TestNode("fastlanes.bitpacked")
            .WithMetadata(TestMetadata.BitPacked(9, 0))
            .WithBuffer(0);

        using DecodeHarness harness = DecodeHarness.Load(root, packed);
        DType u8 = harness.Types.Primitive(PType.U8, Nullability.NonNullable);
        Assert.Throws<VortexFormatException>(() => harness.DecodeRoot(u8, FastLanes.BlockSize));
    }

    [Fact]
    public void ANonIntegerDTypeIsRejected()
    {
        byte[] packed = new byte[FastLanes.BlockByteLength(8)];
        TestNode root = new TestNode("fastlanes.bitpacked")
            .WithMetadata(TestMetadata.BitPacked(8, 0))
            .WithBuffer(0);

        using DecodeHarness harness = DecodeHarness.Load(root, packed);
        DType f32 = harness.Types.Primitive(PType.F32, Nullability.NonNullable);
        Assert.Throws<VortexFormatException>(() => harness.DecodeRoot(f32, FastLanes.BlockSize));
    }

    // ---------------------------------------------------------------- the five child-count shapes

    [Fact]
    public void ShapeC0IsPackedOnly()
    {
        ulong[] values = TestPacking.Ramp(1, 4);
        byte[] packed = TestPacking.Pack(values, 4, 32);

        TestNode root = new TestNode("fastlanes.bitpacked")
            .WithMetadata(TestMetadata.BitPacked(4, 0))
            .WithBuffer(0);

        using DecodeHarness harness = DecodeHarness.Load(root, packed);
        DType u32 = harness.Types.Primitive(PType.U32, Nullability.NonNullable);
        CanonicalNode node = harness.Node(harness.DecodeRoot(u32, 16));
        Assert.Equal(ValidityKind.NonNullable, node.Validity.Kind);
        Assert.Equal(values[0], ReadUnsigned(node, 0, 4));
    }

    [Fact]
    public void ShapeC1IsPackedPlusValidity()
    {
        ulong[] values = TestPacking.Ramp(1, 4);
        byte[] packed = TestPacking.Pack(values, 4, 32);
        byte[] validity = TestBuffers.Bitmap(true, false, true, true);

        TestNode root = new TestNode("fastlanes.bitpacked")
            .WithMetadata(TestMetadata.BitPacked(4, 0))
            .WithBuffer(0)
            .WithChild(new TestNode("vortex.bool").WithBuffer(1));

        using DecodeHarness harness = DecodeHarness.Load(root, packed, validity);
        DType u32 = harness.Types.Primitive(PType.U32, Nullability.Nullable);
        CanonicalNode node = harness.Node(harness.DecodeRoot(u32, 4));
        Assert.Equal(ValidityKind.Bitmap, node.Validity.Kind);
    }

    [Fact]
    public void ShapeC2IsPatchesWithoutValidity()
    {
        ulong[] values = TestPacking.Ramp(1, 4);
        byte[] packed = TestPacking.Pack(values, 4, 32);

        TestNode root = new TestNode("fastlanes.bitpacked")
            .WithMetadata(TestMetadata.BitPacked(4, 0, PatchesMetadata.Create(2, 0, PType.U32)))
            .WithBuffer(0)
            .WithChild(new TestNode("vortex.primitive").WithBuffer(1))
            .WithChild(new TestNode("vortex.primitive").WithBuffer(2));

        using DecodeHarness harness = DecodeHarness.Load(
            root, packed, TestBuffers.UInt32(1, 3), TestBuffers.UInt32(7777, 8888));

        DType u32 = harness.Types.Primitive(PType.U32, Nullability.NonNullable);
        CanonicalNode node = harness.Node(harness.DecodeRoot(u32, 8));

        Assert.Equal(ValidityKind.NonNullable, node.Validity.Kind);
        Assert.Equal(values[0], ReadUnsigned(node, 0, 4));
        Assert.Equal(7777UL, ReadUnsigned(node, 1, 4));
        Assert.Equal(values[2], ReadUnsigned(node, 2, 4));
        Assert.Equal(8888UL, ReadUnsigned(node, 3, 4));
    }

    [Fact]
    public void ShapeC3MeaningPatchesPlusValidity()
    {
        ulong[] values = TestPacking.Ramp(1, 4);
        byte[] packed = TestPacking.Pack(values, 4, 32);

        TestNode root = new TestNode("fastlanes.bitpacked")
            .WithMetadata(TestMetadata.BitPacked(4, 0, PatchesMetadata.Create(1, 0, PType.U32)))
            .WithBuffer(0)
            .WithChild(new TestNode("vortex.primitive").WithBuffer(1))
            .WithChild(new TestNode("vortex.primitive").WithBuffer(2))
            .WithChild(new TestNode("vortex.bool").WithBuffer(3));

        using DecodeHarness harness = DecodeHarness.Load(
            root,
            packed,
            TestBuffers.UInt32(2),
            TestBuffers.UInt32(4242),
            TestBuffers.Bitmap(true, true, true, false));

        DType u32 = harness.Types.Primitive(PType.U32, Nullability.Nullable);
        CanonicalNode node = harness.Node(harness.DecodeRoot(u32, 4));

        Assert.Equal(ValidityKind.Bitmap, node.Validity.Kind);
        Assert.Equal(4242UL, ReadUnsigned(node, 2, 4));
    }

    [Fact]
    public void ShapeC3MeaningPatchesPlusChunkOffsetsWithoutValidity()
    {
        // The same child count as the test above, and only the metadata tells them apart:
        // `chunk_offsets_ptype` present moves the validity child to index 3, so with three
        // children there is no validity child at all.
        ulong[] values = TestPacking.Ramp(1, 4);
        byte[] packed = TestPacking.Pack(values, 4, 32);

        TestNode root = new TestNode("fastlanes.bitpacked")
            .WithMetadata(TestMetadata.BitPacked(
                4, 0, PatchesMetadata.CreateChunked(1, 0, PType.U32, 1, PType.U32, 0)))
            .WithBuffer(0)
            .WithChild(new TestNode("vortex.primitive").WithBuffer(1))
            .WithChild(new TestNode("vortex.primitive").WithBuffer(2))
            .WithChild(new TestNode("vortex.primitive").WithBuffer(3));

        using DecodeHarness harness = DecodeHarness.Load(
            root,
            packed,
            TestBuffers.UInt32(2),
            TestBuffers.UInt32(4242),
            TestBuffers.UInt32(0));

        DType u32 = harness.Types.Primitive(PType.U32, Nullability.NonNullable);
        CanonicalNode node = harness.Node(harness.DecodeRoot(u32, 4));

        Assert.Equal(ValidityKind.NonNullable, node.Validity.Kind);
        Assert.Equal(4242UL, ReadUnsigned(node, 2, 4));
    }

    [Fact]
    public void ShapeC4IsPatchesPlusChunkOffsetsPlusValidity()
    {
        ulong[] values = TestPacking.Ramp(1, 4);
        byte[] packed = TestPacking.Pack(values, 4, 32);

        TestNode root = new TestNode("fastlanes.bitpacked")
            .WithMetadata(TestMetadata.BitPacked(
                4, 0, PatchesMetadata.CreateChunked(1, 0, PType.U32, 1, PType.U32, 0)))
            .WithBuffer(0)
            .WithChild(new TestNode("vortex.primitive").WithBuffer(1))
            .WithChild(new TestNode("vortex.primitive").WithBuffer(2))
            .WithChild(new TestNode("vortex.primitive").WithBuffer(3))
            .WithChild(new TestNode("vortex.bool").WithBuffer(4));

        using DecodeHarness harness = DecodeHarness.Load(
            root,
            packed,
            TestBuffers.UInt32(1),
            TestBuffers.UInt32(555),
            TestBuffers.UInt32(0),
            TestBuffers.Bitmap(true, true, false, true));

        DType u32 = harness.Types.Primitive(PType.U32, Nullability.Nullable);
        CanonicalNode node = harness.Node(harness.DecodeRoot(u32, 4));

        Assert.Equal(ValidityKind.Bitmap, node.Validity.Kind);
        Assert.Equal(555UL, ReadUnsigned(node, 1, 4));
    }

    [Fact]
    public void TooManyChildrenForTheDeclaredShapeIsRejected()
    {
        ulong[] values = TestPacking.Ramp(1, 4);
        byte[] packed = TestPacking.Pack(values, 4, 32);

        TestNode root = new TestNode("fastlanes.bitpacked")
            .WithMetadata(TestMetadata.BitPacked(4, 0))
            .WithBuffer(0)
            .WithChild(new TestNode("vortex.bool").WithBuffer(1))
            .WithChild(new TestNode("vortex.bool").WithBuffer(1));

        using DecodeHarness harness = DecodeHarness.Load(root, packed, TestBuffers.Bitmap(true));
        DType u32 = harness.Types.Primitive(PType.U32, Nullability.Nullable);
        Assert.Throws<VortexFormatException>(() => harness.DecodeRoot(u32, 4));
    }

    [Fact]
    public void NullPatchValuesAreRejected()
    {
        ulong[] values = TestPacking.Ramp(1, 4);
        byte[] packed = TestPacking.Pack(values, 4, 32);

        TestNode root = new TestNode("fastlanes.bitpacked")
            .WithMetadata(TestMetadata.BitPacked(4, 0, PatchesMetadata.Create(2, 0, PType.U32)))
            .WithBuffer(0)
            .WithChild(new TestNode("vortex.primitive").WithBuffer(1))
            .WithChild(new TestNode("vortex.primitive")
                .WithBuffer(2)
                .WithChild(new TestNode("vortex.bool").WithBuffer(3)));

        using DecodeHarness harness = DecodeHarness.Load(
            root,
            packed,
            TestBuffers.UInt32(0, 1),
            TestBuffers.UInt32(1, 2),
            TestBuffers.Bitmap(true, false));

        DType u32 = harness.Types.Primitive(PType.U32, Nullability.Nullable);
        Assert.Throws<VortexFormatException>(() => harness.DecodeRoot(u32, 4));
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
        const int bitWidth = 13;
        const int elementBits = 32;
        int blocks = (rows + FastLanes.BlockSize - 1) / FastLanes.BlockSize;
        blocks = Math.Max(blocks, rows == 0 ? 0 : 1);

        ulong[] values = TestPacking.Ramp(blocks, bitWidth);
        byte[] packed = TestPacking.Pack(values, bitWidth, elementBits);

        TestNode root = new TestNode("fastlanes.bitpacked")
            .WithMetadata(TestMetadata.BitPacked(bitWidth, 0))
            .WithBuffer(0);

        using DecodeHarness harness = DecodeHarness.Load(root, packed);
        DType u32 = harness.Types.Primitive(PType.U32, Nullability.NonNullable);
        CanonicalNode node = harness.Node(harness.DecodeRoot(u32, rows));

        Assert.Equal(rows, node.Length);
        for (int i = 0; i < rows; i++)
        {
            Assert.Equal(values[i], ReadUnsigned(node, i, 4));
        }
    }

    internal static ulong ReadUnsigned(CanonicalNode node, int index, int width) => width switch
    {
        1 => node.Values.Span[index],
        2 => BinaryPrimitives.ReadUInt16LittleEndian(node.Values.Span.Slice(index * 2, 2)),
        4 => BinaryPrimitives.ReadUInt32LittleEndian(node.Values.Span.Slice(index * 4, 4)),
        _ => BinaryPrimitives.ReadUInt64LittleEndian(node.Values.Span.Slice(index * 8, 8)),
    };
}

internal static class BitPackedTestShapes
{
    /// <summary>
    /// A node whose metadata declares chunk offsets: they are read and validated as child 2, and
    /// the validity child moves to index 3.
    /// </summary>
    internal static void AssertChunkOffsetsShapeDecodes()
    {
        ulong[] values = TestPacking.Ramp(1, 6);
        byte[] packed = TestPacking.Pack(values, 6, 32);

        TestNode root = new TestNode("fastlanes.bitpacked")
            .WithMetadata(TestMetadata.BitPacked(
                6, 0, PatchesMetadata.CreateChunked(1, 0, PType.U32, 1, PType.U64, 0)))
            .WithBuffer(0)
            .WithChild(new TestNode("vortex.primitive").WithBuffer(1))
            .WithChild(new TestNode("vortex.primitive").WithBuffer(2))
            .WithChild(new TestNode("vortex.primitive").WithBuffer(3));

        using DecodeHarness harness = DecodeHarness.Load(
            root,
            packed,
            TestBuffers.UInt32(0),
            TestBuffers.UInt32(63),
            TestBuffers.UInt64(0));

        DType u32 = harness.Types.Primitive(PType.U32, Nullability.NonNullable);
        CanonicalNode node = harness.Node(harness.DecodeRoot(u32, 4));
        Assert.Equal(63UL, BitPackedDecoderTests.ReadUnsigned(node, 0, 4));
    }
}
