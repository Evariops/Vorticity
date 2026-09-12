using System;
using Vorticity.Arrays;
using Vorticity.Arrays.Decoders.Compressed;
using Vorticity.Types;
using Xunit;

namespace Vorticity.Tests.Arrays.Decoders.Compressed;

public sealed class FastLanesRleDecoderTests
{
    private const int Chunk = FastLanes.BlockSize;

    [Fact]
    public void GathersOneChunkThroughItsIndices()
    {
        ushort[] indices = new ushort[Chunk];
        for (int i = 0; i < Chunk; i++)
        {
            indices[i] = (ushort)(i % 3);
        }

        using DecodeHarness harness = Load(
            valuesLength: 3, indicesLength: Chunk, offsetsLength: 1, offset: 0,
            values: TestBuffers.UInt32(10, 20, 30),
            indices: TestBuffers.UInt16(indices),
            offsets: TestBuffers.UInt64(0));

        DType u32 = harness.Types.Primitive(PType.U32, Nullability.NonNullable);
        CanonicalNode node = harness.Node(harness.DecodeRoot(u32, Chunk));

        for (int i = 0; i < Chunk; i++)
        {
            Assert.Equal((ulong)(10 * ((i % 3) + 1)), BitPackedDecoderTests.ReadUnsigned(node, i, 4));
        }
    }

    [Fact]
    public void EachChunkHasItsOwnValueWindow()
    {
        ushort[] indices = new ushort[2 * Chunk];
        for (int i = 0; i < Chunk; i++)
        {
            indices[i] = (ushort)(i % 2);              // chunk 0 selects from values[0..2)
            indices[Chunk + i] = (ushort)(i % 3);      // chunk 1 selects from values[2..5)
        }

        using DecodeHarness harness = Load(
            valuesLength: 5, indicesLength: 2 * Chunk, offsetsLength: 2, offset: 0,
            values: TestBuffers.UInt32(10, 20, 30, 40, 50),
            indices: TestBuffers.UInt16(indices),
            offsets: TestBuffers.UInt64(0, 2));

        DType u32 = harness.Types.Primitive(PType.U32, Nullability.NonNullable);
        CanonicalNode node = harness.Node(harness.DecodeRoot(u32, 2 * Chunk));

        Assert.Equal(10UL, BitPackedDecoderTests.ReadUnsigned(node, 0, 4));
        Assert.Equal(20UL, BitPackedDecoderTests.ReadUnsigned(node, 1, 4));
        Assert.Equal(30UL, BitPackedDecoderTests.ReadUnsigned(node, Chunk, 4));
        Assert.Equal(40UL, BitPackedDecoderTests.ReadUnsigned(node, Chunk + 1, 4));
        Assert.Equal(50UL, BitPackedDecoderTests.ReadUnsigned(node, Chunk + 2, 4));
    }

    [Fact]
    public void AnIndexIntoTheNextChunkIsRejected()
    {
        // Upstream's own test: an index of 2 in chunk 0 is inside `values` but out of bounds for
        // the chunk, and must be rejected rather than silently reading chunk 1's values.
        ushort[] indices = new ushort[2 * Chunk];
        indices[100] = 2;

        using DecodeHarness harness = Load(
            valuesLength: 4, indicesLength: 2 * Chunk, offsetsLength: 2, offset: 0,
            values: TestBuffers.UInt32(10, 20, 30, 40),
            indices: TestBuffers.UInt16(indices),
            offsets: TestBuffers.UInt64(0, 2));

        DType u32 = harness.Types.Primitive(PType.U32, Nullability.NonNullable);
        Assert.Throws<VortexFormatException>(() => harness.DecodeRoot(u32, 2 * Chunk));
    }

    [Fact]
    public void ASingleValueChunkIgnoresItsIndicesEntirely()
    {
        // "Single-value chunk: fill directly to avoid out-of-bounds index access. The indices may
        // contain values other than 0 when they have been further compressed."
        ushort[] indices = new ushort[Chunk];
        indices[100] = 999;

        using DecodeHarness harness = Load(
            valuesLength: 1, indicesLength: Chunk, offsetsLength: 1, offset: 0,
            values: TestBuffers.UInt32(77),
            indices: TestBuffers.UInt16(indices),
            offsets: TestBuffers.UInt64(0));

        DType u32 = harness.Types.Primitive(PType.U32, Nullability.NonNullable);
        CanonicalNode node = harness.Node(harness.DecodeRoot(u32, Chunk));
        Assert.Equal(77UL, BitPackedDecoderTests.ReadUnsigned(node, 100, 4));
    }

    [Fact]
    public void NonMonotonicValueIndexOffsetsAreRejected()
    {
        using DecodeHarness harness = Load(
            valuesLength: 4, indicesLength: 2 * Chunk, offsetsLength: 2, offset: 0,
            values: TestBuffers.UInt32(10, 20, 30, 40),
            indices: TestBuffers.UInt16(new ushort[2 * Chunk]),
            offsets: TestBuffers.UInt64(2, 0));

        DType u32 = harness.Types.Primitive(PType.U32, Nullability.NonNullable);
        Assert.Throws<VortexFormatException>(() => harness.DecodeRoot(u32, 2 * Chunk));
    }

    [Fact]
    public void ValueIndexOffsetsSpanningBeyondTheValuesAreRejected()
    {
        using DecodeHarness harness = Load(
            valuesLength: 4, indicesLength: 2 * Chunk, offsetsLength: 2, offset: 0,
            values: TestBuffers.UInt32(10, 20, 30, 40),
            indices: TestBuffers.UInt16(new ushort[2 * Chunk]),
            offsets: TestBuffers.UInt64(0, 100));

        DType u32 = harness.Types.Primitive(PType.U32, Nullability.NonNullable);
        Assert.Throws<VortexFormatException>(() => harness.DecodeRoot(u32, 2 * Chunk));
    }

    [Fact]
    public void AChunkThatReferencesNoValuesIsRejected()
    {
        using DecodeHarness harness = Load(
            valuesLength: 2, indicesLength: 2 * Chunk, offsetsLength: 2, offset: 0,
            values: TestBuffers.UInt32(10, 20),
            indices: TestBuffers.UInt16(new ushort[2 * Chunk]),
            offsets: TestBuffers.UInt64(0, 0));

        DType u32 = harness.Types.Primitive(PType.U32, Nullability.NonNullable);
        Assert.Throws<VortexFormatException>(() => harness.DecodeRoot(u32, 2 * Chunk));
    }

    [Fact]
    public void AnIndicesLengthThatIsNotAMultipleOf1024IsRejected()
    {
        using DecodeHarness harness = Load(
            valuesLength: 2, indicesLength: 1000, offsetsLength: 1, offset: 0,
            values: TestBuffers.UInt32(10, 20),
            indices: TestBuffers.UInt16(new ushort[1000]),
            offsets: TestBuffers.UInt64(0));

        DType u32 = harness.Types.Primitive(PType.U32, Nullability.NonNullable);
        Assert.Throws<VortexFormatException>(() => harness.DecodeRoot(u32, 1000));
    }

    [Fact]
    public void AnInt32IndicesPTypeIsRejected()
    {
        // "RLE indices must be u8 or u16".
        TestNode root = new TestNode("fastlanes.rle")
            .WithMetadata(TestMetadata.Rle(2, Chunk, PType.I32, 1, PType.U64, 0))
            .WithChild(new TestNode("vortex.primitive").WithBuffer(0))
            .WithChild(new TestNode("vortex.primitive").WithBuffer(1))
            .WithChild(new TestNode("vortex.primitive").WithBuffer(2));

        using DecodeHarness harness = DecodeHarness.Load(
            root, TestBuffers.UInt32(10, 20), new byte[Chunk * 4], TestBuffers.UInt64(0));

        DType u32 = harness.Types.Primitive(PType.U32, Nullability.NonNullable);
        Assert.Throws<VortexFormatException>(() => harness.DecodeRoot(u32, Chunk));
    }

    [Fact]
    public void AUInt32IndicesPTypeIsRejected()
    {
        TestNode root = new TestNode("fastlanes.rle")
            .WithMetadata(TestMetadata.Rle(2, Chunk, PType.U32, 1, PType.U64, 0))
            .WithChild(new TestNode("vortex.primitive").WithBuffer(0))
            .WithChild(new TestNode("vortex.primitive").WithBuffer(1))
            .WithChild(new TestNode("vortex.primitive").WithBuffer(2));

        using DecodeHarness harness = DecodeHarness.Load(
            root, TestBuffers.UInt32(10, 20), new byte[Chunk * 4], TestBuffers.UInt64(0));

        DType u32 = harness.Types.Primitive(PType.U32, Nullability.NonNullable);
        Assert.Throws<VortexFormatException>(() => harness.DecodeRoot(u32, Chunk));
    }

    [Fact]
    public void ASignedValueIndexOffsetsPTypeIsRejected()
    {
        TestNode root = new TestNode("fastlanes.rle")
            .WithMetadata(TestMetadata.Rle(2, Chunk, PType.U16, 1, PType.I64, 0))
            .WithChild(new TestNode("vortex.primitive").WithBuffer(0))
            .WithChild(new TestNode("vortex.primitive").WithBuffer(1))
            .WithChild(new TestNode("vortex.primitive").WithBuffer(2));

        using DecodeHarness harness = DecodeHarness.Load(
            root, TestBuffers.UInt32(10, 20), new byte[Chunk * 2], TestBuffers.Int64(0));

        DType u32 = harness.Types.Primitive(PType.U32, Nullability.NonNullable);
        Assert.Throws<VortexFormatException>(() => harness.DecodeRoot(u32, Chunk));
    }

    [Fact]
    public void AWrongOffsetCountIsRejected()
    {
        using DecodeHarness harness = Load(
            valuesLength: 2, indicesLength: 2 * Chunk, offsetsLength: 1, offset: 0,
            values: TestBuffers.UInt32(10, 20),
            indices: TestBuffers.UInt16(new ushort[2 * Chunk]),
            offsets: TestBuffers.UInt64(0));

        DType u32 = harness.Types.Primitive(PType.U32, Nullability.NonNullable);
        Assert.Throws<VortexFormatException>(() => harness.DecodeRoot(u32, 2 * Chunk));
    }

    [Fact]
    public void OffsetPlusLengthBeyondTheIndicesIsRejected()
    {
        using DecodeHarness harness = Load(
            valuesLength: 2, indicesLength: Chunk, offsetsLength: 1, offset: 10,
            values: TestBuffers.UInt32(10, 20),
            indices: TestBuffers.UInt16(new ushort[Chunk]),
            offsets: TestBuffers.UInt64(0));

        DType u32 = harness.Types.Primitive(PType.U32, Nullability.NonNullable);
        Assert.Throws<VortexFormatException>(() => harness.DecodeRoot(u32, Chunk));
    }

    [Fact]
    public void AnOffsetWindowsBothTheValuesAndTheValidity()
    {
        ushort[] indices = new ushort[Chunk];
        for (int i = 0; i < Chunk; i++)
        {
            indices[i] = (ushort)(i % 2);
        }

        bool[] validity = new bool[Chunk];
        for (int i = 0; i < Chunk; i++)
        {
            validity[i] = i % 4 != 0;
        }

        TestNode root = new TestNode("fastlanes.rle")
            .WithMetadata(TestMetadata.Rle(2, (ulong)Chunk, PType.U16, 1, PType.U64, 5))
            .WithChild(new TestNode("vortex.primitive").WithBuffer(0))
            .WithChild(new TestNode("vortex.primitive")
                .WithBuffer(1)
                .WithChild(new TestNode("vortex.bool").WithBuffer(3)))
            .WithChild(new TestNode("vortex.primitive").WithBuffer(2));

        using DecodeHarness harness = DecodeHarness.Load(
            root,
            TestBuffers.UInt32(10, 20),
            TestBuffers.UInt16(indices),
            TestBuffers.UInt64(0),
            TestBuffers.Bitmap(validity));

        DType u32 = harness.Types.Primitive(PType.U32, Nullability.Nullable);
        int rows = Chunk - 5;
        CanonicalNode node = harness.Node(harness.DecodeRoot(u32, rows));

        Assert.Equal(ValidityKind.Bitmap, node.Validity.Kind);
        CanonicalNode bits = harness.Node(node.Validity.CanonicalNodeIndex);

        for (int i = 0; i < rows; i++)
        {
            int encoded = i + 5;
            int bit = bits.BitOffset + i;
            bool valid = (bits.Bits.Span[bit >> 3] & (1 << (bit & 7))) != 0;
            Assert.Equal(validity[encoded], valid);
            if (valid)
            {
                Assert.Equal(
                    (ulong)(10 * ((encoded % 2) + 1)),
                    BitPackedDecoderTests.ReadUnsigned(node, i, 4));
            }
        }
    }

    [Fact]
    public void AGarbageIndexAtANullPositionIsTolerated()
    {
        // Upstream sanitizes null positions to index 0, because further compression of the indices
        // can leave anything there. A file the reference reads must not fail for us.
        ushort[] indices = new ushort[Chunk];
        for (int i = 0; i < Chunk; i++)
        {
            indices[i] = (ushort)(i % 2);
        }

        indices[100] = 999;

        bool[] validity = new bool[Chunk];
        Array.Fill(validity, true);
        validity[100] = false;

        TestNode root = new TestNode("fastlanes.rle")
            .WithMetadata(TestMetadata.Rle(2, (ulong)Chunk, PType.U16, 1, PType.U64, 0))
            .WithChild(new TestNode("vortex.primitive").WithBuffer(0))
            .WithChild(new TestNode("vortex.primitive")
                .WithBuffer(1)
                .WithChild(new TestNode("vortex.bool").WithBuffer(3)))
            .WithChild(new TestNode("vortex.primitive").WithBuffer(2));

        using DecodeHarness harness = DecodeHarness.Load(
            root,
            TestBuffers.UInt32(10, 20),
            TestBuffers.UInt16(indices),
            TestBuffers.UInt64(0),
            TestBuffers.Bitmap(validity));

        DType u32 = harness.Types.Primitive(PType.U32, Nullability.Nullable);
        CanonicalNode node = harness.Node(harness.DecodeRoot(u32, Chunk));
        Assert.Equal(Chunk, node.Length);
        Assert.Equal(10UL, BitPackedDecoderTests.ReadUnsigned(node, 0, 4));
    }

    [Fact]
    public void AnOutOfBoundsIndexAtAValidPositionIsRejected()
    {
        ushort[] indices = new ushort[Chunk];
        indices[100] = 999;

        bool[] validity = new bool[Chunk];
        Array.Fill(validity, true);
        validity[200] = false;

        TestNode root = new TestNode("fastlanes.rle")
            .WithMetadata(TestMetadata.Rle(2, (ulong)Chunk, PType.U16, 1, PType.U64, 0))
            .WithChild(new TestNode("vortex.primitive").WithBuffer(0))
            .WithChild(new TestNode("vortex.primitive")
                .WithBuffer(1)
                .WithChild(new TestNode("vortex.bool").WithBuffer(3)))
            .WithChild(new TestNode("vortex.primitive").WithBuffer(2));

        using DecodeHarness harness = DecodeHarness.Load(
            root,
            TestBuffers.UInt32(10, 20),
            TestBuffers.UInt16(indices),
            TestBuffers.UInt64(0),
            TestBuffers.Bitmap(validity));

        DType u32 = harness.Types.Primitive(PType.U32, Nullability.Nullable);
        Assert.Throws<VortexFormatException>(() => harness.DecodeRoot(u32, Chunk));
    }

    [Fact]
    public void MoreValuesThanIndicesIsRejected()
    {
        using DecodeHarness harness = Load(
            valuesLength: 2048, indicesLength: Chunk, offsetsLength: 1, offset: 0,
            values: new byte[2048 * 4],
            indices: TestBuffers.UInt16(new ushort[Chunk]),
            offsets: TestBuffers.UInt64(0));

        DType u32 = harness.Types.Primitive(PType.U32, Nullability.NonNullable);
        Assert.Throws<VortexFormatException>(() => harness.DecodeRoot(u32, Chunk));
    }

    [Fact]
    public void AWrongChildCountIsRejected()
    {
        TestNode root = new TestNode("fastlanes.rle")
            .WithMetadata(TestMetadata.Rle(2, (ulong)Chunk, PType.U16, 1, PType.U64, 0))
            .WithChild(new TestNode("vortex.primitive").WithBuffer(0))
            .WithChild(new TestNode("vortex.primitive").WithBuffer(1));

        using DecodeHarness harness = DecodeHarness.Load(
            root, TestBuffers.UInt32(10, 20), new byte[Chunk * 2]);

        DType u32 = harness.Types.Primitive(PType.U32, Nullability.NonNullable);
        Assert.Throws<VortexFormatException>(() => harness.DecodeRoot(u32, Chunk));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(1023)]
    [InlineData(1024)]
    [InlineData(1025)]
    public void DecodesTheBoundaryRowCounts(int rows)
    {
        int chunks = (rows + Chunk - 1) / Chunk;
        ushort[] indices = new ushort[chunks * Chunk];
        for (int i = 0; i < indices.Length; i++)
        {
            indices[i] = (ushort)(i % 3);
        }

        // Each chunk owns its own three-value window: the offsets are absolute positions into a
        // shared, contiguous values array, so two chunks cannot share one window.
        uint[] values = new uint[chunks * 3];
        ulong[] offsets = new ulong[chunks];
        for (int chunk = 0; chunk < chunks; chunk++)
        {
            offsets[chunk] = (ulong)(chunk * 3);
            values[(chunk * 3) + 0] = 10;
            values[(chunk * 3) + 1] = 20;
            values[(chunk * 3) + 2] = 30;
        }

        using DecodeHarness harness = Load(
            valuesLength: (ulong)values.Length, indicesLength: chunks * Chunk,
            offsetsLength: chunks, offset: 0,
            values: TestBuffers.UInt32(values),
            indices: TestBuffers.UInt16(indices),
            offsets: TestBuffers.UInt64(offsets));

        DType u32 = harness.Types.Primitive(PType.U32, Nullability.NonNullable);
        CanonicalNode node = harness.Node(harness.DecodeRoot(u32, rows));

        Assert.Equal(rows, node.Length);
        for (int i = 0; i < rows; i++)
        {
            Assert.Equal((ulong)(10 * ((i % 3) + 1)), BitPackedDecoderTests.ReadUnsigned(node, i, 4));
        }
    }

    // `RLEData::try_new` is `vortex_ensure!(offset < 1024, "Offset must be smaller than 1024")`
    // and `deserialize` calls it with `metadata.offset as usize`, so no node the reference accepts
    // can declare this. It matters beyond strictness: rle_decompress always starts its chunk walk
    // at index 0 and slices `offset..offset+len` out of the result, so its chunk-to-offsets mapping
    // and this decoder's `(offset + row) / 1024` agree ONLY while offset < 1024. Without the bound
    // the decoder silently defines a reading of bytes the format does not define - here it would
    // hand row 0 chunk 1's value window (chunkBase = 2, i.e. 30) instead of chunk 0's.
    [Fact]
    public void AnOffsetOfAFullBlockIsRejected()
    {
        // {values_len: 8, indices_len: 4096, indices_ptype: U16, values_idx_offsets_len: 4,
        //  values_idx_offsets_ptype: U64, offset: 1024}, hand-written: RleMetadata's constructor
        // now refuses to build it.
        byte[] metadata =
        [
            0x08, 0x08,
            0x10, 0x80, 0x20,
            0x18, 0x01,
            0x20, 0x04,
            0x28, 0x03,
            0x30, 0x80, 0x08,
        ];

        ushort[] indices = new ushort[4 * Chunk];
        for (int i = 0; i < indices.Length; i++)
        {
            indices[i] = (ushort)(i % 2);
        }

        TestNode root = new TestNode("fastlanes.rle")
            .WithMetadata(metadata)
            .WithChild(new TestNode("vortex.primitive").WithBuffer(0))
            .WithChild(new TestNode("vortex.primitive").WithBuffer(1))
            .WithChild(new TestNode("vortex.primitive").WithBuffer(2));

        using DecodeHarness harness = DecodeHarness.Load(
            root,
            TestBuffers.UInt32(10, 20, 30, 40, 50, 60, 70, 80),
            TestBuffers.UInt16(indices),
            TestBuffers.UInt64(0, 2, 4, 6));

        DType u32 = harness.Types.Primitive(PType.U32, Nullability.NonNullable);
        Assert.Throws<VortexFormatException>(() => harness.DecodeRoot(u32, Chunk));
    }

    private static DecodeHarness Load(
        ulong valuesLength,
        int indicesLength,
        int offsetsLength,
        ulong offset,
        byte[] values,
        byte[] indices,
        byte[] offsets)
    {
        TestNode root = new TestNode("fastlanes.rle")
            .WithMetadata(TestMetadata.Rle(
                valuesLength, (ulong)indicesLength, PType.U16, (ulong)offsetsLength, PType.U64, offset))
            .WithChild(new TestNode("vortex.primitive").WithBuffer(0))
            .WithChild(new TestNode("vortex.primitive").WithBuffer(1))
            .WithChild(new TestNode("vortex.primitive").WithBuffer(2));

        return DecodeHarness.Load(root, values, indices, offsets);
    }
}
