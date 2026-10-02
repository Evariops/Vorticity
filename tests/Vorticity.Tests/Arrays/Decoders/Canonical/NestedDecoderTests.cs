// vortex.struct, vortex.list, vortex.listview, vortex.fixed_size_list, vortex.ext and
// vortex.masked: child order, each child's derived dtype and length, and the range checks that
// keep a bad offset from becoming an out-of-bounds read.
using System;
using System.Buffers.Binary;
using System.Text;
using Vorticity.Arrays;
using Vorticity.Types;
using Xunit;

namespace Vorticity.Tests.Arrays.Decoders.Canonical;

public sealed class NestedDecoderTests
{
    private static byte[] I32(params int[] values)
    {
        byte[] bytes = new byte[values.Length * 4];
        for (int i = 0; i < values.Length; i++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(i * 4, 4), values[i]);
        }

        return bytes;
    }

    private static byte[] I64(params long[] values)
    {
        byte[] bytes = new byte[values.Length * 8];
        for (int i = 0; i < values.Length; i++)
        {
            BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(i * 8, 8), values[i]);
        }

        return bytes;
    }

    private static long ReadOffset(ReadOnlySpan<byte> bytes, PType ptype, int index) => ptype switch
    {
        PType.I32 => BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(index * 4, 4)),
        PType.U32 => BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(index * 4, 4)),
        _ => BinaryPrimitives.ReadInt64LittleEndian(bytes.Slice(index * 8, 8)),
    };

    // ---------------------------------------------------------------------------- vortex.struct

    [Fact]
    public void StructPutsValidityFirst()
    {
        using DecodeHarness h = new DecodeHarness();
        BlobBuilder b = new BlobBuilder();
        int bits = b.AddBuffer([0b0000_0101]);
        int a = b.AddBuffer(I32(1, 2, 3));
        int c = b.AddBuffer(I64(10, 20, 30));

        BlobNode node = new BlobNode("vortex.struct").WithChildren(
            new BlobNode("vortex.bool").WithMetadata(TestMetadata.Bool(0)).WithBuffers(bits),
            new BlobNode("vortex.primitive").WithBuffers(a),
            new BlobNode("vortex.primitive").WithBuffers(c));

        DType dtype = h.Types.Struct(
            ["a", "b"],
            [
                h.Types.Primitive(PType.I32, Nullability.NonNullable),
                h.Types.Primitive(PType.I64, Nullability.NonNullable),
            ],
            Nullability.Nullable);

        int index = h.Decode(b, node, dtype, 3);
        CanonicalNode decoded = h.Node(index);

        Assert.Equal(CanonicalKind.Struct, decoded.Kind);
        Assert.Equal(2, decoded.FieldCount);
        Assert.Equal(ValidityKind.Bitmap, decoded.Validity.Kind);
        Assert.Equal(PType.I32, h.Node(decoded.GetFieldIndex(0)).PType);
        Assert.Equal(PType.I64, h.Node(decoded.GetFieldIndex(1)).PType);
    }

    [Fact]
    public void StructWithoutAValidityChildUsesTheDTypeNullability()
    {
        using DecodeHarness h = new DecodeHarness();
        BlobBuilder b = new BlobBuilder();
        int a = b.AddBuffer(I32(1, 2, 3));

        BlobNode node = new BlobNode("vortex.struct").WithChildren(
            new BlobNode("vortex.primitive").WithBuffers(a));

        DType dtype = h.Types.Struct(
            ["a"],
            [h.Types.Primitive(PType.I32, Nullability.NonNullable)],
            Nullability.NonNullable);

        int index = h.Decode(b, node, dtype, 3);
        Assert.Equal(ValidityKind.NonNullable, h.Node(index).Validity.Kind);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public void StructRejectsTheWrongChildCount(int children)
    {
        using DecodeHarness h = new DecodeHarness();
        BlobBuilder b = new BlobBuilder();
        int a = b.AddBuffer(I32(1, 2, 3));

        BlobNode node = new BlobNode("vortex.struct");
        for (int i = 0; i < children; i++)
        {
            node.WithChildren(new BlobNode("vortex.primitive").WithBuffers(a));
        }

        DType dtype = h.Types.Struct(
            ["a"],
            [h.Types.Primitive(PType.I32, Nullability.NonNullable)],
            Nullability.NonNullable);

        Assert.Throws<VortexFormatException>(() => h.Decode(b, node, dtype, 3));
    }

    [Fact]
    public void AConstantFalseStructValidityCollapsesToAllInvalid()
    {
        using DecodeHarness h = new DecodeHarness();
        BlobBuilder b = new BlobBuilder();
        int scalar = b.AddBuffer(TestMetadata.ScalarBool(false));
        int a = b.AddBuffer(I32(1, 2, 3));

        BlobNode node = new BlobNode("vortex.struct").WithChildren(
            new BlobNode("vortex.constant").WithBuffers(scalar),
            new BlobNode("vortex.primitive").WithBuffers(a));

        DType dtype = h.Types.Struct(
            ["a"],
            [h.Types.Primitive(PType.I32, Nullability.NonNullable)],
            Nullability.Nullable);

        int index = h.Decode(b, node, dtype, 3);
        Assert.Equal(ValidityKind.AllInvalid, h.Node(index).Validity.Kind);
    }

    // ------------------------------------------------------------------------------ vortex.list

    private static BlobNode List(BlobBuilder b, byte[] elements, byte[] offsets, ulong elementsLength)
    {
        int elementsBuffer = b.AddBuffer(elements);
        int offsetsBuffer = b.AddBuffer(offsets);
        return new BlobNode("vortex.list")
            .WithMetadata(TestMetadata.List(elementsLength, PType.I32))
            .WithChildren(
                new BlobNode("vortex.primitive").WithBuffers(elementsBuffer),
                new BlobNode("vortex.primitive").WithBuffers(offsetsBuffer));
    }

    [Fact]
    public void ListCanonicalizesToListViewWithComputedSizes()
    {
        using DecodeHarness h = new DecodeHarness();
        BlobBuilder b = new BlobBuilder();
        BlobNode node = List(b, I32(1, 2, 3, 4, 5), I32(0, 2, 2, 5), 5);

        DType dtype = h.Types.List(h.Types.Primitive(PType.I32, Nullability.NonNullable), Nullability.NonNullable);
        int index = h.Decode(b, node, dtype, 3);
        CanonicalNode decoded = h.Node(index);

        Assert.Equal(CanonicalKind.ListView, decoded.Kind);
        Assert.Equal(3, decoded.Length);
        Assert.Equal(2, ReadOffset(decoded.Sizes.Span, decoded.SizePType, 0));
        Assert.Equal(0, ReadOffset(decoded.Sizes.Span, decoded.SizePType, 1));
        Assert.Equal(3, ReadOffset(decoded.Sizes.Span, decoded.SizePType, 2));
        Assert.Equal(0, ReadOffset(decoded.Offsets.Span, decoded.OffsetPType, 0));
        Assert.Equal(2, ReadOffset(decoded.Offsets.Span, decoded.OffsetPType, 2));
    }

    [Fact]
    public void ListRejectsANonMonotoneOffsetsArray()
    {
        using DecodeHarness h = new DecodeHarness();
        BlobBuilder b = new BlobBuilder();
        BlobNode node = List(b, I32(1, 2, 3, 4, 5), I32(0, 3, 1, 5), 5);

        DType dtype = h.Types.List(h.Types.Primitive(PType.I32, Nullability.NonNullable), Nullability.NonNullable);
        Assert.Throws<VortexFormatException>(() => h.Decode(b, node, dtype, 3));
    }

    [Fact]
    public void ListRejectsOffsetsPastTheElementsChild()
    {
        using DecodeHarness h = new DecodeHarness();
        BlobBuilder b = new BlobBuilder();
        BlobNode node = List(b, I32(1, 2, 3, 4, 5), I32(0, 2, 6), 5);

        DType dtype = h.Types.List(h.Types.Primitive(PType.I32, Nullability.NonNullable), Nullability.NonNullable);
        Assert.Throws<VortexFormatException>(() => h.Decode(b, node, dtype, 2));
    }

    [Fact]
    public void AListSelectionReadsTheWantedRowsOffsetsAlone()
    {
        // Rows 0 and 1 share an offset, row 3 is the last: each row's span is its offset and the next.
        using DecodeHarness h = new DecodeHarness();
        BlobBuilder b = new BlobBuilder();
        BlobNode node = List(b, I32(1, 2, 3, 4, 5, 6, 7), I32(0, 2, 2, 5, 7), 7);

        DType dtype = h.Types.List(h.Types.Primitive(PType.I32, Nullability.NonNullable), Nullability.NonNullable);
        CanonicalNode decoded = h.Node(h.DecodeSelected(b, node, dtype, 4, [0, 1, 3]));

        Assert.Equal(CanonicalKind.ListView, decoded.Kind);
        Assert.Equal(3, decoded.Length);
        Assert.Equal([0L, 2, 5], [ReadOffset(decoded.Offsets.Span, decoded.OffsetPType, 0), ReadOffset(decoded.Offsets.Span, decoded.OffsetPType, 1), ReadOffset(decoded.Offsets.Span, decoded.OffsetPType, 2)]);
        Assert.Equal([2L, 0, 2], [ReadOffset(decoded.Sizes.Span, decoded.SizePType, 0), ReadOffset(decoded.Sizes.Span, decoded.SizePType, 1), ReadOffset(decoded.Sizes.Span, decoded.SizePType, 2)]);
    }

    [Fact]
    public void AListSelectionRejectsAWantedRowRunningPastTheElementsChild()
    {
        using DecodeHarness h = new DecodeHarness();
        BlobBuilder b = new BlobBuilder();
        BlobNode node = List(b, I32(1, 2, 3, 4, 5), I32(0, 2, 6), 5);

        DType dtype = h.Types.List(h.Types.Primitive(PType.I32, Nullability.NonNullable), Nullability.NonNullable);
        Assert.Throws<VortexFormatException>(() => h.DecodeSelected(b, node, dtype, 2, [1]));
    }

    // -------------------------------------------------------------------------- vortex.listview

    private static BlobNode ListView(
        BlobBuilder b, byte[] elements, byte[] offsets, byte[] sizes, ulong elementsLength)
    {
        int e = b.AddBuffer(elements);
        int o = b.AddBuffer(offsets);
        int s = b.AddBuffer(sizes);
        return new BlobNode("vortex.listview")
            .WithMetadata(TestMetadata.ListView(elementsLength, PType.I32, PType.I32))
            .WithChildren(
                new BlobNode("vortex.primitive").WithBuffers(e),
                new BlobNode("vortex.primitive").WithBuffers(o),
                new BlobNode("vortex.primitive").WithBuffers(s));
    }

    [Fact]
    public void ListViewAcceptsUnorderedOffsets()
    {
        using DecodeHarness h = new DecodeHarness();
        BlobBuilder b = new BlobBuilder();
        BlobNode node = ListView(b, I32(1, 2, 3, 4, 5), I32(3, 0, 2), I32(2, 1, 0), 5);

        DType dtype = h.Types.List(h.Types.Primitive(PType.I32, Nullability.NonNullable), Nullability.NonNullable);
        int index = h.Decode(b, node, dtype, 3);
        CanonicalNode decoded = h.Node(index);

        Assert.Equal(CanonicalKind.ListView, decoded.Kind);
        Assert.Equal(3, ReadOffset(decoded.Offsets.Span, decoded.OffsetPType, 0));
    }

    [Fact]
    public void ListViewRejectsARowRunningPastTheElementsChild()
    {
        using DecodeHarness h = new DecodeHarness();
        BlobBuilder b = new BlobBuilder();
        BlobNode node = ListView(b, I32(1, 2, 3, 4, 5), I32(3, 0, 2), I32(2, 1, 4), 5);

        DType dtype = h.Types.List(h.Types.Primitive(PType.I32, Nullability.NonNullable), Nullability.NonNullable);
        Assert.Throws<VortexFormatException>(() => h.Decode(b, node, dtype, 3));
    }

    [Fact]
    public void ListViewRejectsANegativeOffset()
    {
        using DecodeHarness h = new DecodeHarness();
        BlobBuilder b = new BlobBuilder();
        BlobNode node = ListView(b, I32(1, 2, 3, 4, 5), I32(-1, 0, 2), I32(1, 1, 1), 5);

        DType dtype = h.Types.List(h.Types.Primitive(PType.I32, Nullability.NonNullable), Nullability.NonNullable);
        Assert.Throws<VortexFormatException>(() => h.Decode(b, node, dtype, 3));
    }

    // A 64-bit offset and a 64-bit size, each individually legal, whose SIGNED sum wraps negative
    // and therefore compares below elements_len. Upstream widens to u64 and uses checked_add
    // (validate_offsets_and_sizes), so it rejects these bytes; a signed add silently accepts them
    // and publishes a row whose [offset, offset+size) is nowhere near the elements child.
    [Theory]
    [InlineData(0x4000000000000000L, 0x4000000000000000L)]
    [InlineData(long.MaxValue, 1L)]
    [InlineData(1L, long.MaxValue)]
    public void ListViewRejectsAnOffsetPlusSizeThatWrapsSigned64(long offset, long size)
    {
        using DecodeHarness h = new DecodeHarness();
        BlobBuilder b = new BlobBuilder();
        BlobNode node = ListView64(b, I32(1, 2, 3, 4, 5), I64(offset), I64(size), 5);

        DType dtype = h.Types.List(h.Types.Primitive(PType.I32, Nullability.NonNullable), Nullability.NonNullable);
        VortexFormatException error = Assert.Throws<VortexFormatException>(() => h.Decode(b, node, dtype, 1));

        // The wrapped sum must never reach the message either.
        Assert.DoesNotContain("-", error.Message, StringComparison.Ordinal);
    }

    // The boundary the unsigned add must keep accepting: an empty row starting exactly at the end.
    [Fact]
    public void ListViewAcceptsAnEmptyRowAtTheEndOfTheElementsChild()
    {
        using DecodeHarness h = new DecodeHarness();
        BlobBuilder b = new BlobBuilder();
        BlobNode node = ListView64(b, I32(1, 2, 3, 4, 5), I64(5), I64(0), 5);

        DType dtype = h.Types.List(h.Types.Primitive(PType.I32, Nullability.NonNullable), Nullability.NonNullable);
        int index = h.Decode(b, node, dtype, 1);

        Assert.Equal(CanonicalKind.ListView, h.Node(index).Kind);
    }

    // Rows enough for the validation's vectors, eight or sixteen rows at a time, with one bad row
    // at either end of a vector and past the last whole one: every place it can hide.
    [Theory]
    [InlineData(false, -1, 0)]
    [InlineData(true, -1, 0)]
    [InlineData(false, 0, 1)]
    [InlineData(false, 15, 1)]
    [InlineData(false, 16, 2)]
    [InlineData(false, 39, 2)]
    [InlineData(true, 0, 1)]
    [InlineData(true, 7, 2)]
    [InlineData(true, 8, 3)]
    [InlineData(true, 16, 1)]
    [InlineData(true, 39, 3)]
    public void ListViewChecksEveryRowOfALongColumn(bool wide, int badRow, int badKind)
    {
        const int Rows = 40;
        const int Elements = 100;
        Random random = new Random((badRow * 7) + badKind + (wide ? 100 : 0));
        long[] offsets = new long[Rows];
        long[] sizes = new long[Rows];
        for (int i = 0; i < Rows; i++)
        {
            offsets[i] = random.Next(Elements + 1);
            sizes[i] = random.Next((int)(Elements - offsets[i]) + 1);
        }

        if (badRow >= 0)
        {
            (offsets[badRow], sizes[badRow]) = badKind switch
            {
                1 => (-1L, 1L),
                2 => (Elements - 2L, 3L),
                _ => (long.MaxValue, 1L),
            };
        }

        using DecodeHarness h = new DecodeHarness();
        BlobBuilder b = new BlobBuilder();
        byte[] elements = I32(new int[Elements]);
        BlobNode node = wide
            ? ListView64(b, elements, I64(offsets), I64(sizes), Elements)
            : ListView(b, elements, I32(Array.ConvertAll(offsets, v => (int)v)), I32(Array.ConvertAll(sizes, v => (int)v)), Elements);
        DType dtype = h.Types.List(h.Types.Primitive(PType.I32, Nullability.NonNullable), Nullability.NonNullable);
        if (badRow < 0)
        {
            Assert.Equal(CanonicalKind.ListView, h.Node(h.Decode(b, node, dtype, Rows)).Kind);
        }
        else
        {
            Assert.Throws<VortexFormatException>(() => h.Decode(b, node, dtype, Rows));
        }
    }

    private static BlobNode ListView64(
        BlobBuilder b, byte[] elements, byte[] offsets, byte[] sizes, ulong elementsLength)
    {
        int e = b.AddBuffer(elements);
        int o = b.AddBuffer(offsets);
        int s = b.AddBuffer(sizes);
        return new BlobNode("vortex.listview")
            .WithMetadata(TestMetadata.ListView(elementsLength, PType.I64, PType.I64))
            .WithChildren(
                new BlobNode("vortex.primitive").WithBuffers(e),
                new BlobNode("vortex.primitive").WithBuffers(o),
                new BlobNode("vortex.primitive").WithBuffers(s));
    }

    // ------------------------------------------------------------------- vortex.fixed_size_list

    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    [InlineData(3u)]
    public void FixedSizeListDerivesTheElementCount(uint size)
    {
        const int Rows = 4;
        using DecodeHarness h = new DecodeHarness();
        BlobBuilder b = new BlobBuilder();
        int elements = b.AddBuffer(new byte[Rows * (int)size * 4]);

        BlobNode node = new BlobNode("vortex.fixed_size_list").WithChildren(
            new BlobNode("vortex.primitive").WithBuffers(elements));

        DType dtype = h.Types.FixedSizeList(
            h.Types.Primitive(PType.I32, Nullability.NonNullable), size, Nullability.NonNullable);

        int index = h.Decode(b, node, dtype, Rows);
        CanonicalNode decoded = h.Node(index);

        Assert.Equal(size, decoded.FixedSize);
        Assert.Equal((int)(Rows * size), h.Node(decoded.ElementsIndex).Length);
    }

    [Fact]
    public void FixedSizeListRejectsNonEmptyMetadata()
    {
        using DecodeHarness h = new DecodeHarness();
        BlobBuilder b = new BlobBuilder();
        int elements = b.AddBuffer(new byte[12]);

        BlobNode node = new BlobNode("vortex.fixed_size_list")
            .WithMetadata([0x08, 0x03])
            .WithChildren(new BlobNode("vortex.primitive").WithBuffers(elements));

        DType dtype = h.Types.FixedSizeList(
            h.Types.Primitive(PType.I32, Nullability.NonNullable), 3, Nullability.NonNullable);

        Assert.Throws<VortexFormatException>(() => h.Decode(b, node, dtype, 1));
    }

    // ------------------------------------------------------------------------------- vortex.ext

    private static DType Timestamp(DecodeHarness h, byte unit, string? zone)
    {
        byte[] zoneBytes = zone is null ? [] : Encoding.UTF8.GetBytes(zone);
        byte[] metadata = new byte[3 + zoneBytes.Length];
        metadata[0] = unit;
        BinaryPrimitives.WriteUInt16LittleEndian(metadata.AsSpan(1, 2), (ushort)zoneBytes.Length);
        zoneBytes.CopyTo(metadata.AsSpan(3));
        return h.Types.Extension(
            "vortex.timestamp", h.Types.Primitive(PType.I64, Nullability.NonNullable), metadata);
    }

    private static int DecodeExtension(DecodeHarness h, DType dtype, DType storage, int length)
    {
        BlobBuilder b = new BlobBuilder();
        int values = b.AddBuffer(new byte[length * StorageWidth(storage)]);
        BlobNode node = new BlobNode("vortex.ext").WithChildren(
            StorageNode(storage, values));
        return h.Decode(b, node, dtype, length);
    }

    private static int StorageWidth(DType storage) =>
        storage.Kind == DTypeKind.Primitive ? storage.PType.ByteWidth() : 1;

    private static BlobNode StorageNode(DType storage, int buffer) =>
        storage.Kind == DTypeKind.Primitive
            ? new BlobNode("vortex.primitive").WithBuffers(buffer)
            : new BlobNode("vortex.fixed_size_list").WithChildren(
                new BlobNode("vortex.primitive").WithBuffers(buffer));

    [Fact]
    public void TimestampWithNoTimezoneIsThreeBytes()
    {
        using DecodeHarness h = new DecodeHarness();
        DType storage = h.Types.Primitive(PType.I64, Nullability.NonNullable);
        DType dtype = Timestamp(h, (byte)VortexTimeUnit.Milliseconds, null);

        int index = DecodeExtension(h, dtype, storage, 4);
        Assert.Equal(CanonicalKind.Extension, h.Node(index).Kind);
    }

    [Fact]
    public void TimestampWithATimezoneRoundTrips()
    {
        using DecodeHarness h = new DecodeHarness();
        DType storage = h.Types.Primitive(PType.I64, Nullability.NonNullable);
        DType dtype = Timestamp(h, (byte)VortexTimeUnit.Microseconds, "Europe/Paris");

        int index = DecodeExtension(h, dtype, storage, 2);
        Assert.Equal(CanonicalKind.Extension, h.Node(index).Kind);

        TimestampOptions options = ExtensionDTypeRegistry.ReadTimestamp(dtype.ExtensionMetadata, storage);
        Assert.True(options.HasTimeZone);
        Assert.Equal("Europe/Paris", Encoding.UTF8.GetString(options.TimeZoneUtf8));
    }

    [Fact]
    public void TimestampRejectsAOneByteMetadata()
    {
        using DecodeHarness h = new DecodeHarness();
        DType storage = h.Types.Primitive(PType.I64, Nullability.NonNullable);
        DType dtype = h.Types.Extension("vortex.timestamp", storage, [(byte)VortexTimeUnit.Milliseconds]);

        Assert.Throws<VortexFormatException>(() => DecodeExtension(h, dtype, storage, 1));
    }

    [Fact]
    public void TimestampRejectsATimezoneLengthThatOverruns()
    {
        using DecodeHarness h = new DecodeHarness();
        DType storage = h.Types.Primitive(PType.I64, Nullability.NonNullable);
        byte[] metadata = [(byte)VortexTimeUnit.Milliseconds, 0x10, 0x00, (byte)'X'];
        DType dtype = h.Types.Extension("vortex.timestamp", storage, metadata);

        Assert.Throws<VortexFormatException>(() => DecodeExtension(h, dtype, storage, 1));
    }

    [Fact]
    public void DateRejectsMicroseconds()
    {
        using DecodeHarness h = new DecodeHarness();
        DType storage = h.Types.Primitive(PType.I64, Nullability.NonNullable);
        DType dtype = h.Types.Extension("vortex.date", storage, [(byte)VortexTimeUnit.Microseconds]);

        Assert.Throws<VortexFormatException>(() => DecodeExtension(h, dtype, storage, 1));
    }

    [Fact]
    public void DateDaysNeedsI32Storage()
    {
        using DecodeHarness h = new DecodeHarness();
        DType i32 = h.Types.Primitive(PType.I32, Nullability.NonNullable);
        DType good = h.Types.Extension("vortex.date", i32, [(byte)VortexTimeUnit.Days]);
        Assert.Equal(CanonicalKind.Extension, h.Node(DecodeExtension(h, good, i32, 3)).Kind);

        DType i64 = h.Types.Primitive(PType.I64, Nullability.NonNullable);
        DType bad = h.Types.Extension("vortex.date", i64, [(byte)VortexTimeUnit.Days]);
        Assert.Throws<VortexFormatException>(() => DecodeExtension(h, bad, i64, 3));
    }

    [Theory]
    [InlineData(0x0F)]
    [InlineData(0xFF)]
    public void UuidAcceptsBothSpellingsOfMax(byte version)
    {
        using DecodeHarness h = new DecodeHarness();
        DType storage = h.Types.FixedSizeList(
            h.Types.Primitive(PType.U8, Nullability.NonNullable), 16, Nullability.NonNullable);
        DType dtype = h.Types.Extension("vortex.uuid", storage, [version]);

        BlobBuilder b = new BlobBuilder();
        int bytes = b.AddBuffer(new byte[2 * 16]);
        BlobNode node = new BlobNode("vortex.ext").WithChildren(
            new BlobNode("vortex.fixed_size_list").WithChildren(
                new BlobNode("vortex.primitive").WithBuffers(bytes)));

        int index = h.Decode(b, node, dtype, 2);
        Assert.Equal(CanonicalKind.Extension, h.Node(index).Kind);
        Assert.Equal(
            ExtensionDTypeRegistry.UuidVersionMax,
            ExtensionDTypeRegistry.ReadUuid(dtype.ExtensionMetadata, storage).Version);
    }

    [Fact]
    public void UuidRejectsTwoMetadataBytes()
    {
        using DecodeHarness h = new DecodeHarness();
        DType storage = h.Types.FixedSizeList(
            h.Types.Primitive(PType.U8, Nullability.NonNullable), 16, Nullability.NonNullable);
        DType dtype = h.Types.Extension("vortex.uuid", storage, [0x04, 0x00]);

        BlobBuilder b = new BlobBuilder();
        int bytes = b.AddBuffer(new byte[16]);
        BlobNode node = new BlobNode("vortex.ext").WithChildren(
            new BlobNode("vortex.fixed_size_list").WithChildren(
                new BlobNode("vortex.primitive").WithBuffers(bytes)));

        Assert.Throws<VortexFormatException>(() => h.Decode(b, node, dtype, 1));
    }

    [Fact]
    public void AnUnknownExtensionIdIsUnsupportedNotMalformed()
    {
        using DecodeHarness h = new DecodeHarness();
        DType storage = h.Types.Primitive(PType.I64, Nullability.NonNullable);
        DType dtype = h.Types.Extension("acme.duration", storage, [0x00]);

        VortexUnsupportedException error =
            Assert.Throws<VortexUnsupportedException>(() => DecodeExtension(h, dtype, storage, 1));
        Assert.Contains("acme.duration", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ExtensionRejectsAValidityChildOfItsOwn()
    {
        using DecodeHarness h = new DecodeHarness();
        DType storage = h.Types.Primitive(PType.I64, Nullability.NonNullable);
        DType dtype = Timestamp(h, (byte)VortexTimeUnit.Milliseconds, null);

        BlobBuilder b = new BlobBuilder();
        int values = b.AddBuffer(new byte[16]);
        int bits = b.AddBuffer([0xFF]);
        BlobNode node = new BlobNode("vortex.ext").WithChildren(
            new BlobNode("vortex.primitive").WithBuffers(values),
            new BlobNode("vortex.bool").WithMetadata(TestMetadata.Bool(0)).WithBuffers(bits));

        Assert.Throws<VortexFormatException>(() => h.Decode(b, node, dtype, 2));
        Assert.Equal(DTypeKind.Primitive, storage.Kind);
    }

    [Fact]
    public void ExtensionTakesItsValidityFromTheStorage()
    {
        using DecodeHarness h = new DecodeHarness();
        DType storage = h.Types.Primitive(PType.I64, Nullability.Nullable);
        DType dtype = h.Types.Extension(
            "vortex.timestamp", storage, [(byte)VortexTimeUnit.Milliseconds, 0x00, 0x00]);

        BlobBuilder b = new BlobBuilder();
        int values = b.AddBuffer(new byte[16]);
        int scalar = b.AddBuffer(TestMetadata.ScalarBool(false));
        BlobNode node = new BlobNode("vortex.ext").WithChildren(
            new BlobNode("vortex.primitive")
                .WithBuffers(values)
                .WithChildren(new BlobNode("vortex.constant").WithBuffers(scalar)));

        int index = h.Decode(b, node, dtype, 2);
        Assert.Equal(ValidityKind.AllInvalid, h.Node(index).Validity.Kind);
    }

    // ---------------------------------------------------------------------------- vortex.masked

    [Fact]
    public void MaskedAppliesTheMaskToItsChild()
    {
        using DecodeHarness h = new DecodeHarness();
        BlobBuilder b = new BlobBuilder();
        int values = b.AddBuffer(I32(7, 8, 9, 10));
        int bits = b.AddBuffer([0b0000_1001]);

        BlobNode node = new BlobNode("vortex.masked").WithChildren(
            new BlobNode("vortex.primitive").WithBuffers(values),
            new BlobNode("vortex.bool").WithMetadata(TestMetadata.Bool(0)).WithBuffers(bits));

        DType dtype = h.Types.Primitive(PType.I32, Nullability.Nullable);
        int index = h.Decode(b, node, dtype, 4);
        CanonicalNode decoded = h.Node(index);

        Assert.Equal(CanonicalKind.Primitive, decoded.Kind);
        Assert.Equal(dtype, decoded.DType);
        Assert.Equal(ValidityKind.Bitmap, decoded.Validity.Kind);
        Assert.Equal(7, BinaryPrimitives.ReadInt32LittleEndian(decoded.Values.Span[..4]));
    }

    [Fact]
    public void MaskedRejectsANonNullableDType()
    {
        using DecodeHarness h = new DecodeHarness();
        BlobBuilder b = new BlobBuilder();
        int values = b.AddBuffer(I32(7, 8));

        BlobNode node = new BlobNode("vortex.masked").WithChildren(
            new BlobNode("vortex.primitive").WithBuffers(values));

        Assert.Throws<VortexFormatException>(() => h.Decode(
            b, node, h.Types.Primitive(PType.I32, Nullability.NonNullable), 2));
    }

    [Fact]
    public void MaskedWithOneChildIsAllValid()
    {
        using DecodeHarness h = new DecodeHarness();
        BlobBuilder b = new BlobBuilder();
        int values = b.AddBuffer(I32(7, 8));

        BlobNode node = new BlobNode("vortex.masked").WithChildren(
            new BlobNode("vortex.primitive").WithBuffers(values));

        int index = h.Decode(b, node, h.Types.Primitive(PType.I32, Nullability.Nullable), 2);
        Assert.Equal(ValidityKind.AllValid, h.Node(index).Validity.Kind);
    }

    [Fact]
    public void MaskedRejectsThreeChildren()
    {
        using DecodeHarness h = new DecodeHarness();
        BlobBuilder b = new BlobBuilder();
        int values = b.AddBuffer(I32(7, 8));
        int bits = b.AddBuffer([0b0000_0011]);

        BlobNode node = new BlobNode("vortex.masked").WithChildren(
            new BlobNode("vortex.primitive").WithBuffers(values),
            new BlobNode("vortex.bool").WithMetadata(TestMetadata.Bool(0)).WithBuffers(bits),
            new BlobNode("vortex.bool").WithMetadata(TestMetadata.Bool(0)).WithBuffers(bits));

        Assert.Throws<VortexFormatException>(() => h.Decode(
            b, node, h.Types.Primitive(PType.I32, Nullability.Nullable), 2));
    }
}
