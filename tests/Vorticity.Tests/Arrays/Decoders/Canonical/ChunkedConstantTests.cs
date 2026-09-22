// vortex.chunked and vortex.constant: the two decoders whose failure modes are silent rather than
// loud. Chunked underflows an unchecked subtraction upstream if its offsets are not monotone, and
// constant is the encoding whose scalar the vendored spec puts in the wrong place.
using System;
using System.Buffers.Binary;
using System.Text;
using Vorticity.Arrays;
using Vorticity.Types;
using Vorticity.Types.Numerics;
using Xunit;

namespace Vorticity.Tests.Arrays.Decoders.Canonical;

public sealed class ChunkedConstantTests
{
    private static byte[] U64(params ulong[] values)
    {
        byte[] bytes = new byte[values.Length * 8];
        for (int i = 0; i < values.Length; i++)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(i * 8, 8), values[i]);
        }

        return bytes;
    }

    private static byte[] I32(params int[] values)
    {
        byte[] bytes = new byte[values.Length * 4];
        for (int i = 0; i < values.Length; i++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(i * 4, 4), values[i]);
        }

        return bytes;
    }

    // --------------------------------------------------------------------------- vortex.chunked

    private static BlobNode Chunked(BlobBuilder b, ulong[] offsets, params int[][] chunkValues)
    {
        BlobNode node = new BlobNode("vortex.chunked").WithChildren(
            new BlobNode("vortex.primitive").WithBuffers(b.AddBuffer(U64(offsets))));

        foreach (int[] values in chunkValues)
        {
            node.WithChildren(new BlobNode("vortex.primitive").WithBuffers(b.AddBuffer(I32(values))));
        }

        return node;
    }

    [Fact]
    public void ChunkedConcatenatesInOrder()
    {
        using DecodeHarness h = new DecodeHarness();
        BlobBuilder b = new BlobBuilder();
        BlobNode node = Chunked(b, [0, 2, 5], [1, 2], [3, 4, 5]);

        DType dtype = h.Types.Primitive(PType.I32, Nullability.NonNullable);
        int index = h.Decode(b, node, dtype, 5);
        CanonicalNode decoded = h.Node(index);

        Assert.Equal(CanonicalKind.Primitive, decoded.Kind);
        Assert.Equal(5, decoded.Length);
        for (int i = 0; i < 5; i++)
        {
            Assert.Equal(i + 1, BinaryPrimitives.ReadInt32LittleEndian(decoded.Values.Span.Slice(i * 4, 4)));
        }
    }

    [Fact]
    public void ChunkedToleratesZeroLengthChunksAnywhere()
    {
        // encodings/chunked_empty_chunks puts a zero-row chunk first, in the middle and last.
        using DecodeHarness h = new DecodeHarness();
        BlobBuilder b = new BlobBuilder();
        BlobNode node = Chunked(b, [0, 0, 2, 2, 4, 4], [], [1, 2], [], [3, 4], []);

        DType dtype = h.Types.Primitive(PType.I32, Nullability.NonNullable);
        int index = h.Decode(b, node, dtype, 4);
        CanonicalNode decoded = h.Node(index);

        Assert.Equal(4, decoded.Length);
        Assert.Equal(4, BinaryPrimitives.ReadInt32LittleEndian(decoded.Values.Span.Slice(12, 4)));
    }

    [Fact]
    public void ChunkedRejectsNonMonotoneOffsets()
    {
        using DecodeHarness h = new DecodeHarness();
        BlobBuilder b = new BlobBuilder();
        BlobNode node = Chunked(b, [0, 3, 1], [1, 2, 3], [4]);

        Assert.Throws<VortexFormatException>(() => h.Decode(
            b, node, h.Types.Primitive(PType.I32, Nullability.NonNullable), 4));
    }

    [Fact]
    public void ChunkedRejectsALastOffsetThatIsNotTheRowCount()
    {
        using DecodeHarness h = new DecodeHarness();
        BlobBuilder b = new BlobBuilder();
        BlobNode node = Chunked(b, [0, 2, 4], [1, 2], [3, 4]);

        Assert.Throws<VortexFormatException>(() => h.Decode(
            b, node, h.Types.Primitive(PType.I32, Nullability.NonNullable), 5));
    }

    [Fact]
    public void ChunkedRejectsAFirstOffsetThatIsNotZero()
    {
        using DecodeHarness h = new DecodeHarness();
        BlobBuilder b = new BlobBuilder();
        BlobNode node = Chunked(b, [1, 3], [1, 2]);

        Assert.Throws<VortexFormatException>(() => h.Decode(
            b, node, h.Types.Primitive(PType.I32, Nullability.NonNullable), 3));
    }

    [Fact]
    public void ChunkedRejectsAnOffsetAboveIntMax()
    {
        using DecodeHarness h = new DecodeHarness();
        BlobBuilder b = new BlobBuilder();
        BlobNode node = Chunked(b, [0, ulong.MaxValue], [1, 2]);

        Assert.Throws<VortexFormatException>(() => h.Decode(
            b, node, h.Types.Primitive(PType.I32, Nullability.NonNullable), 2));
    }

    [Fact]
    public void ChunkedNeedsAtLeastOneChild()
    {
        using DecodeHarness h = new DecodeHarness();
        Assert.Throws<VortexFormatException>(() => h.Decode(
            new BlobBuilder(),
            new BlobNode("vortex.chunked"),
            h.Types.Primitive(PType.I32, Nullability.NonNullable),
            0));
    }

    [Fact]
    public void ChunkedRejectsNonEmptyMetadata()
    {
        using DecodeHarness h = new DecodeHarness();
        BlobBuilder b = new BlobBuilder();
        BlobNode node = Chunked(b, [0, 2], [1, 2]).WithMetadata([0x08, 0x00]);

        Assert.Throws<VortexFormatException>(() => h.Decode(
            b, node, h.Types.Primitive(PType.I32, Nullability.NonNullable), 2));
    }

    [Fact]
    public void ChunkedMergesMixedValidityIntoOneBitmap()
    {
        using DecodeHarness h = new DecodeHarness();
        BlobBuilder b = new BlobBuilder();

        int offsets = b.AddBuffer(U64(0, 2, 4));
        int first = b.AddBuffer(I32(1, 2));
        int second = b.AddBuffer(I32(3, 4));
        int allNull = b.AddBuffer(TestMetadata.ScalarBool(false));

        BlobNode node = new BlobNode("vortex.chunked").WithChildren(
            new BlobNode("vortex.primitive").WithBuffers(offsets),
            new BlobNode("vortex.primitive").WithBuffers(first),
            new BlobNode("vortex.primitive")
                .WithBuffers(second)
                .WithChildren(new BlobNode("vortex.constant").WithBuffers(allNull)));

        DType dtype = h.Types.Primitive(PType.I32, Nullability.Nullable);
        int index = h.Decode(b, node, dtype, 4);
        CanonicalNode decoded = h.Node(index);

        Assert.Equal(ValidityKind.Bitmap, decoded.Validity.Kind);
        CanonicalNode bits = h.Node(decoded.Validity.CanonicalNodeIndex);
        Assert.True(CanonicalBits.Get(bits.Bits.Span, bits.BitOffset + 0));
        Assert.True(CanonicalBits.Get(bits.Bits.Span, bits.BitOffset + 1));
        Assert.False(CanonicalBits.Get(bits.Bits.Span, bits.BitOffset + 2));
        Assert.False(CanonicalBits.Get(bits.Bits.Span, bits.BitOffset + 3));
    }

    [Fact]
    public void ChunkedConcatenatesBoolChunksAcrossBitOffsets()
    {
        // The two chunks start at different bit offsets, so a byte-wise concat produces the wrong
        // answer for every row after the first chunk.
        using DecodeHarness h = new DecodeHarness();
        BlobBuilder b = new BlobBuilder();

        int offsets = b.AddBuffer(U64(0, 3, 6));
        int firstBits = b.AddBuffer([0b0010_1000]);   // offset 3: bits 3,4,5 = 1,0,1
        int secondBits = b.AddBuffer([0b0100_0000]);  // offset 5: bits 5,6,7 = 0,1,0

        BlobNode node = new BlobNode("vortex.chunked").WithChildren(
            new BlobNode("vortex.primitive").WithBuffers(offsets),
            new BlobNode("vortex.bool").WithMetadata(TestMetadata.Bool(3)).WithBuffers(firstBits),
            new BlobNode("vortex.bool").WithMetadata(TestMetadata.Bool(5)).WithBuffers(secondBits));

        DType dtype = h.Types.Bool(Nullability.NonNullable);
        int index = h.Decode(b, node, dtype, 6);
        CanonicalNode decoded = h.Node(index);

        bool[] expected = [true, false, true, false, true, false];
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i], CanonicalBits.Get(decoded.Bits.Span, decoded.BitOffset + i));
        }
    }

    [Fact]
    public void ChunkedConcatenatesStructsFieldByField()
    {
        using DecodeHarness h = new DecodeHarness();
        BlobBuilder b = new BlobBuilder();

        int offsets = b.AddBuffer(U64(0, 1, 3));
        int a1 = b.AddBuffer(I32(10));
        int a2 = b.AddBuffer(I32(20, 30));

        BlobNode Chunk(int buffer) => new BlobNode("vortex.struct").WithChildren(
            new BlobNode("vortex.primitive").WithBuffers(buffer));

        BlobNode node = new BlobNode("vortex.chunked").WithChildren(
            new BlobNode("vortex.primitive").WithBuffers(offsets),
            Chunk(a1),
            Chunk(a2));

        DType dtype = h.Types.Struct(
            ["a"],
            [h.Types.Primitive(PType.I32, Nullability.NonNullable)],
            Nullability.NonNullable);

        int index = h.Decode(b, node, dtype, 3);
        CanonicalNode decoded = h.Node(index);
        Assert.Equal(CanonicalKind.Struct, decoded.Kind);

        CanonicalNode field = h.Node(decoded.GetFieldIndex(0));
        Assert.Equal(3, field.Length);
        Assert.Equal(30, BinaryPrimitives.ReadInt32LittleEndian(field.Values.Span.Slice(8, 4)));
    }

    [Fact]
    public void ChunkedOfOneChunkReturnsTheChunkItself()
    {
        using DecodeHarness h = new DecodeHarness();
        BlobBuilder b = new BlobBuilder();
        BlobNode node = Chunked(b, [0, 3], [1, 2, 3]);

        DType dtype = h.Types.Primitive(PType.I32, Nullability.NonNullable);
        int index = h.Decode(b, node, dtype, 3);
        Assert.Equal(3, h.Node(index).Length);
        Assert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(h.Node(index).Values.Span[..4]));
    }

    // -------------------------------------------------------------------------- vortex.constant

    private static int Constant(DecodeHarness h, byte[] scalar, DType dtype, int length, byte[]? metadata = null)
    {
        BlobBuilder b = new BlobBuilder();
        BlobNode node = new BlobNode("vortex.constant").WithBuffers(b.AddBuffer(scalar));
        if (metadata is not null)
        {
            node.WithMetadata(metadata);
        }

        return h.Decode(b, node, dtype, length);
    }

    /// <summary>A constant decimal takes the form, and expanding it does not lose its storage.</summary>
    /// <remarks>
    /// THE TRAP THIS GUARDS. A constant record carries an element and a width and has no storage,
    /// precision or scale of its own -- one record shape serves every kind. So the expansion had to
    /// get those from somewhere, and reading them off the record would have handed `AddDecimal` a
    /// zeroed triple, which it refuses. They come from the element's width and from the dtype
    /// instead, and this asserts both halves: the arena holds the form, and a caller reading
    /// through it sees the same storage and the same bytes as a tiled column would have given.
    /// </remarks>
    [Theory]
    [InlineData((byte)4, 4, DecimalStorageType.I32)]
    [InlineData((byte)4, 16, DecimalStorageType.I128)]
    [InlineData((byte)18, 32, DecimalStorageType.I256)]
    internal void AConstantDecimalKeepsItsStorageThroughTheForm(
        byte precision, int scalarWidth, DecimalStorageType expected)
    {
        byte[] value = new byte[scalarWidth];
        value[0] = 7;

        using DecodeHarness h = new DecodeHarness();
        int index = Constant(
            h,
            TestMetadata.ScalarBytes(value),
            h.Types.Decimal(precision, 2, Nullability.NonNullable),
            5);

        CanonicalNode node = h.Node(index);

        // The arena holds the element, not five copies of it.
        Assert.Equal(CanonicalKind.Constant, node.Kind);

        // And answers for it without expanding, though the record is not a Decimal one.
        Assert.Equal(expected, node.Storage);
        Assert.Equal(precision, node.Precision);
        Assert.Equal(2, node.Scale);

        // Expanding is where the zeroed triple would have thrown.
        Assert.Equal(5 * DecimalStorage.ByteWidth(expected), node.Values.Length);
        Assert.Equal(7, node.Values.Span[0]);
    }

    [Fact]
    public void ConstantIgnoresItsMetadataEntirely()
    {
        // Upstream names the parameter `_metadata` and never reads it. Rejecting a stray byte
        // would reject a file upstream reads.
        using DecodeHarness h = new DecodeHarness();
        int index = Constant(
            h,
            TestMetadata.ScalarInt64(42),
            h.Types.Primitive(PType.I64, Nullability.NonNullable),
            3,
            metadata: [0x08, 0x01, 0x7F]);

        Assert.Equal(42, BinaryPrimitives.ReadInt64LittleEndian(h.Node(index).Values.Span[..8]));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void ConstantRequiresExactlyOneBuffer(int buffers)
    {
        using DecodeHarness h = new DecodeHarness();
        BlobBuilder b = new BlobBuilder();
        BlobNode node = new BlobNode("vortex.constant");
        for (int i = 0; i < buffers; i++)
        {
            node.WithBuffers(b.AddBuffer(TestMetadata.ScalarInt64(1)));
        }

        Assert.Throws<VortexFormatException>(() => h.Decode(
            b, node, h.Types.Primitive(PType.I64, Nullability.NonNullable), 2));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(8193)]
    public void ConstantFillsEveryRow(int length)
    {
        using DecodeHarness h = new DecodeHarness();
        int index = Constant(
            h, TestMetadata.ScalarInt64(-7), h.Types.Primitive(PType.I64, Nullability.NonNullable), length);

        CanonicalNode node = h.Node(index);
        Assert.Equal(length, node.Length);
        for (int i = 0; i < length; i++)
        {
            Assert.Equal(-7, BinaryPrimitives.ReadInt64LittleEndian(node.Values.Span.Slice(i * 8, 8)));
        }
    }

    [Fact]
    public void ANullConstantIsAllInvalid()
    {
        using DecodeHarness h = new DecodeHarness();
        int index = Constant(
            h, TestMetadata.ScalarNull(), h.Types.Primitive(PType.I64, Nullability.Nullable), 4);

        CanonicalNode node = h.Node(index);
        Assert.Equal(ValidityKind.AllInvalid, node.Validity.Kind);
        Assert.Equal(32, node.Values.Length);
    }

    [Fact]
    public void ANullConstantNeedsANullableDType()
    {
        using DecodeHarness h = new DecodeHarness();
        Assert.Throws<VortexFormatException>(() => Constant(
            h, TestMetadata.ScalarNull(), h.Types.Primitive(PType.I64, Nullability.NonNullable), 4));
    }

    [Fact]
    public void AConstantNullDTypeIsANullNode()
    {
        using DecodeHarness h = new DecodeHarness();
        int index = Constant(h, TestMetadata.ScalarNull(), h.Types.Null(Nullability.Nullable), 9);
        Assert.Equal(CanonicalKind.Null, h.Node(index).Kind);
    }

    [Theory]
    [InlineData(2, 1)]
    [InlineData(4, 2)]
    [InlineData(9, 4)]
    [InlineData(18, 8)]
    [InlineData(38, 16)]
    [InlineData(40, 32)]
    public void AConstantDecimalWorksAtEveryStorageWidth(byte precision, int scalarWidth)
    {
        byte[] value = new byte[scalarWidth];
        value[0] = 12;

        using DecodeHarness h = new DecodeHarness();
        int index = Constant(
            h,
            TestMetadata.ScalarBytes(value),
            h.Types.Decimal(precision, 1, Nullability.NonNullable),
            3);

        CanonicalNode node = h.Node(index);
        Assert.Equal(DecimalStorage.ForPrecision(precision), node.Storage);
        Assert.Equal(12, node.Values.Span[0]);
        Assert.Equal(3 * DecimalStorage.ByteWidth(node.Storage), node.Values.Length);
    }

    // Upstream's `constant_canonicalize` takes the DecimalArray's values_type from the SCALAR's own
    // DecimalValue variant (`match_each_decimal_value!`), which `bytes_from_proto` derives from the
    // serialized bytes_value length - `smallest_decimal_value_type` is used only in the all-null
    // arm. So a decimal(4,2) column written from Arrow, whose values_type is i128, keeps reporting
    // i128 for the chunk the compressor folded into a vortex.constant. Deriving the width from the
    // precision instead makes one batch of a scan disagree with the next about Storage, StorageBytes
    // and which narrowed accessor works.
    [Theory]
    [InlineData((byte)4, 4, DecimalStorageType.I32)]
    [InlineData((byte)4, 8, DecimalStorageType.I64)]
    [InlineData((byte)4, 16, DecimalStorageType.I128)]
    [InlineData((byte)2, 32, DecimalStorageType.I256)]
    internal void AConstantDecimalKeepsAScalarWiderThanItsPrecision(
        byte precision, int scalarWidth, DecimalStorageType expected)
    {
        byte[] value = new byte[scalarWidth];
        value[0] = 12;

        using DecodeHarness h = new DecodeHarness();
        int index = Constant(
            h,
            TestMetadata.ScalarBytes(value),
            h.Types.Decimal(precision, 1, Nullability.NonNullable),
            3);

        CanonicalNode node = h.Node(index);
        Assert.Equal(expected, node.Storage);
        Assert.Equal(3 * scalarWidth, node.Values.Length);
        Assert.Equal(12, node.Values.Span[0]);

        // Sign extension, not truncation: the bytes above the value are all zero.
        for (int i = 1; i < scalarWidth; i++)
        {
            Assert.Equal(0, node.Values.Span[i]);
        }
    }

    // The other direction is NOT honoured, deliberately: DecimalDecoder refuses a values_type
    // narrower than the precision on the vortex.decimal path, so a constant must not be the one
    // node in the arena that reports one.
    [Fact]
    public void AConstantDecimalWidensAScalarNarrowerThanItsPrecision()
    {
        using DecodeHarness h = new DecodeHarness();
        int index = Constant(
            h,
            TestMetadata.ScalarBytes([0xFB]),
            h.Types.Decimal(9, 1, Nullability.NonNullable),
            2);

        CanonicalNode node = h.Node(index);
        Assert.Equal(DecimalStorageType.I32, node.Storage);
        Assert.Equal(2 * 4, node.Values.Length);

        // -5, sign extended out of one byte into four.
        Assert.Equal(-5, BinaryPrimitives.ReadInt32LittleEndian(node.Values.Span[..4]));
    }

    [Fact]
    public void AConstantDecimalRejectsAThreeByteScalar()
    {
        using DecodeHarness h = new DecodeHarness();
        Assert.Throws<VortexFormatException>(() => Constant(
            h,
            TestMetadata.ScalarBytes([1, 2, 3]),
            h.Types.Decimal(9, 1, Nullability.NonNullable),
            1));
    }

    [Fact]
    public void AConstantDecimalRejectsAValueTooWideForItsPrecision()
    {
        // 16 bytes of 0x7F is far beyond what an i8 storage (precision 2) can hold.
        byte[] value = new byte[16];
        value.AsSpan().Fill(0x7F);
        value[15] = 0x00;

        using DecodeHarness h = new DecodeHarness();
        Assert.Throws<VortexFormatException>(() => Constant(
            h, TestMetadata.ScalarBytes(value), h.Types.Decimal(2, 0, Nullability.NonNullable), 1));
    }

    [Fact]
    public void AConstantStringLongerThanTwelveBytesGetsItsOwnDataBuffer()
    {
        byte[] value = Encoding.UTF8.GetBytes("a rather long constant string");

        using DecodeHarness h = new DecodeHarness();
        int index = Constant(
            h, TestMetadata.ScalarString(value), h.Types.Utf8(Nullability.NonNullable), 3);

        CanonicalNode node = h.Node(index);
        Assert.Equal(1, node.DataBufferCount);
        Assert.Equal(value.Length, node.GetDataBuffer(0).Length);
        Assert.Equal((uint)value.Length, BinaryPrimitives.ReadUInt32LittleEndian(node.Views.Span[..4]));
    }

    [Fact]
    public void AConstantStringOfTwelveBytesInlines()
    {
        byte[] value = Encoding.UTF8.GetBytes("abcdefghijkl");

        using DecodeHarness h = new DecodeHarness();
        int index = Constant(
            h, TestMetadata.ScalarString(value), h.Types.Utf8(Nullability.NonNullable), 2);

        CanonicalNode node = h.Node(index);
        Assert.Equal(0, node.DataBufferCount);
        Assert.Equal("abcdefghijkl", Encoding.UTF8.GetString(node.Views.Span.Slice(4, 12)));
    }

    [Fact]
    public void AConstantStructBuildsOneConstantPerField()
    {
        using DecodeHarness h = new DecodeHarness();
        DType dtype = h.Types.Struct(
            ["a", "b"],
            [
                h.Types.Primitive(PType.I32, Nullability.NonNullable),
                h.Types.Utf8(Nullability.NonNullable),
            ],
            Nullability.NonNullable);

        byte[] scalar = TestMetadata.ScalarList(
            TestMetadata.ScalarInt64(5),
            TestMetadata.ScalarString(Encoding.UTF8.GetBytes("hi")));

        int index = Constant(h, scalar, dtype, 3);
        CanonicalNode node = h.Node(index);

        Assert.Equal(CanonicalKind.Struct, node.Kind);
        Assert.Equal(5, BinaryPrimitives.ReadInt32LittleEndian(h.Node(node.GetFieldIndex(0)).Values.Span[..4]));
        Assert.Equal("hi", Encoding.UTF8.GetString(h.Node(node.GetFieldIndex(1)).Views.Span.Slice(4, 2)));
    }

    [Fact]
    public void AConstantStructRejectsTheWrongFieldCount()
    {
        using DecodeHarness h = new DecodeHarness();
        DType dtype = h.Types.Struct(
            ["a", "b"],
            [
                h.Types.Primitive(PType.I32, Nullability.NonNullable),
                h.Types.Utf8(Nullability.NonNullable),
            ],
            Nullability.NonNullable);

        byte[] scalar = TestMetadata.ScalarList(TestMetadata.ScalarInt64(5));
        Assert.Throws<VortexFormatException>(() => Constant(h, scalar, dtype, 1));
    }

    [Fact]
    public void AConstantListPointsEveryRowAtOneCopyOfTheElements()
    {
        using DecodeHarness h = new DecodeHarness();
        DType dtype = h.Types.List(
            h.Types.Primitive(PType.I32, Nullability.NonNullable), Nullability.NonNullable);

        byte[] scalar = TestMetadata.ScalarList(
            TestMetadata.ScalarInt64(7), TestMetadata.ScalarInt64(8), TestMetadata.ScalarInt64(9));

        int index = Constant(h, scalar, dtype, 100);
        CanonicalNode node = h.Node(index);

        Assert.Equal(CanonicalKind.ListView, node.Kind);

        // O(k), not O(n * k): the elements child holds three values, not three hundred.
        Assert.Equal(3, h.Node(node.ElementsIndex).Length);
        Assert.Equal(0UL, BinaryPrimitives.ReadUInt64LittleEndian(node.Offsets.Span[..8]));
        Assert.Equal(3UL, BinaryPrimitives.ReadUInt64LittleEndian(node.Sizes.Span[..8]));
        Assert.Equal(3UL, BinaryPrimitives.ReadUInt64LittleEndian(node.Sizes.Span.Slice(99 * 8, 8)));
    }

    [Fact]
    public void AConstantFixedSizeListTilesItsElements()
    {
        using DecodeHarness h = new DecodeHarness();
        DType dtype = h.Types.FixedSizeList(
            h.Types.Primitive(PType.I32, Nullability.NonNullable), 2, Nullability.NonNullable);

        byte[] scalar = TestMetadata.ScalarList(
            TestMetadata.ScalarInt64(4), TestMetadata.ScalarInt64(5));

        int index = Constant(h, scalar, dtype, 3);
        CanonicalNode node = h.Node(index);

        Assert.Equal(CanonicalKind.FixedSizeList, node.Kind);
        CanonicalNode elements = h.Node(node.ElementsIndex);
        Assert.Equal(6, elements.Length);

        int[] expected = [4, 5, 4, 5, 4, 5];
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.Equal(
                expected[i],
                BinaryPrimitives.ReadInt32LittleEndian(elements.Values.Span.Slice(i * 4, 4)));
        }
    }

    [Fact]
    public void AConstantFixedSizeListRejectsTheWrongElementCount()
    {
        using DecodeHarness h = new DecodeHarness();
        DType dtype = h.Types.FixedSizeList(
            h.Types.Primitive(PType.I32, Nullability.NonNullable), 3, Nullability.NonNullable);

        byte[] scalar = TestMetadata.ScalarList(
            TestMetadata.ScalarInt64(4), TestMetadata.ScalarInt64(5));

        Assert.Throws<VortexFormatException>(() => Constant(h, scalar, dtype, 2));
    }

    [Fact]
    public void AConstantBoolFillsTheBitmap()
    {
        using DecodeHarness h = new DecodeHarness();
        int index = Constant(h, TestMetadata.ScalarBool(true), h.Types.Bool(Nullability.NonNullable), 13);

        CanonicalNode node = h.Node(index);
        for (int i = 0; i < 13; i++)
        {
            Assert.True(CanonicalBits.Get(node.Bits.Span, i));
        }
    }

    [Fact]
    public void AnEmptyScalarBufferIsMalformed()
    {
        // An empty ScalarValue body is Absent, which is "no kind" and not the same as null_value.
        using DecodeHarness h = new DecodeHarness();
        Assert.Throws<VortexFormatException>(() => Constant(
            h, Array.Empty<byte>(), h.Types.Primitive(PType.I64, Nullability.Nullable), 2));
    }

    [Fact]
    public void AConstantExtensionWrapsItsStorage()
    {
        using DecodeHarness h = new DecodeHarness();
        DType storage = h.Types.Primitive(PType.I64, Nullability.NonNullable);
        DType dtype = h.Types.Extension(
            "vortex.timestamp", storage, [(byte)VortexTimeUnit.Milliseconds, 0x00, 0x00]);

        int index = Constant(h, TestMetadata.ScalarInt64(1700000000000L), dtype, 2);
        CanonicalNode node = h.Node(index);

        Assert.Equal(CanonicalKind.Extension, node.Kind);
        Assert.Equal(
            1700000000000L,
            BinaryPrimitives.ReadInt64LittleEndian(h.Node(node.StorageIndex).Values.Span[..8]));
    }
}
