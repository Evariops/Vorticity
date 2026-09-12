// The promise is absolute: malformed input yields VortexFormatException - never an out-of-bounds
// read, an unbounded allocation, a hang, or a wrong value passed off as right.
//
// A column view sits behind the decoders, so the shapes it must survive are the ones a decoder bug
// (or a future decoder) could hand it: a buffer shorter than the row count claims, a validity
// bitmap that does not cover the array, a validity index pointing at something that is not a Bool.
// CanonicalArena.AddBare is the way to build them - it sets only the four common fields and skips
// every buffer check the typed builders make.
using System;
using System.Buffers.Binary;
using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Columns;
using Vorticity.Types;
using Vorticity.Types.Numerics;
using Xunit;

namespace Vorticity.Tests.Columns;

public sealed class MalformedNodeTests
{
    [Fact]
    public void ABoolNodeWithNoBitmapThrows()
    {
        using ColumnFixture f = new ColumnFixture();
        int node = f.Arena.AddBare(
            CanonicalKind.Bool, f.Types.Bool(Nullability.NonNullable), 8, Validity.NonNullable);
        RecordBatch batch = f.Batch(node);

        Assert.Equal(0, batch.Root.AsBool().Bits.Length);
        Assert.Throws<VortexFormatException>(() => { _ = batch.Root.AsBool()[0]; });
        Assert.Throws<VortexFormatException>(() => { _ = batch.Root.AsBool()[7]; });
    }

    [Fact]
    public void AVarBinViewWithNoViewsThrows()
    {
        using ColumnFixture f = new ColumnFixture();
        int node = f.Arena.AddBare(
            CanonicalKind.VarBinView, f.Types.Utf8(Nullability.NonNullable), 3, Validity.NonNullable);
        RecordBatch batch = f.Batch(node);

        Assert.Throws<VortexFormatException>(() => { _ = batch.Root.AsBinary().GetSpan(0).Length; });
        Assert.Throws<VortexFormatException>(() => batch.Root.AsBinary().GetLength(2));
        Assert.Throws<VortexFormatException>(() => batch.Root.AsBinary().GetString(1));
    }

    [Fact]
    public void ADecimalWithATruncatedStorageThrows()
    {
        using ColumnFixture f = new ColumnFixture();
        int node = f.Arena.AddBare(
            CanonicalKind.Decimal, f.Types.Decimal(9, 2, Nullability.NonNullable), 5, Validity.NonNullable);
        RecordBatch batch = f.Batch(node);

        Assert.Throws<VortexFormatException>(() => { _ = batch.Root.AsDecimal().StorageBytes.Length; });
        Assert.Throws<VortexFormatException>(() => { _ = batch.Root.AsDecimal()[0]; });
    }

    [Fact]
    public void AListViewWithNoOffsetsThrows()
    {
        using ColumnFixture f = new ColumnFixture();
        int elements = f.Int32Node([1, 2, 3], Validity.NonNullable);
        DType listType = f.Types.List(
            f.Types.Primitive(PType.I32, Nullability.NonNullable), Nullability.NonNullable);

        // AddListView enforces the buffer lengths, so build the same shape through AddBare and
        // attach only the elements child.
        int node = f.Arena.AddListView(
            listType, 3, Validity.NonNullable, elements,
            f.Int32s([0, 1, 2]), PType.I32, f.Int32s([1, 1, 1]), PType.I32);
        RecordBatch batch = f.Batch(node);

        // The well-formed node reads; the forged one below does not.
        Assert.Equal(1, batch.Root.AsList().GetLength(0));

        int bare = f.Arena.AddBare(CanonicalKind.ListView, listType, 3, Validity.NonNullable);
        RecordBatch broken = f.Batch(bare);
        Assert.Throws<VortexFormatException>(() => { _ = broken.Root.AsList().GetOffset(0); });
        Assert.Throws<VortexFormatException>(() => { _ = broken.Root.AsList().GetLength(0); });
    }

    [Fact]
    public void AListViewWhoseOffsetPTypeIsAFloatIsMalformed()
    {
        using ColumnFixture f = new ColumnFixture();
        int elements = f.Int32Node([1], Validity.NonNullable);
        DType listType = f.Types.List(
            f.Types.Primitive(PType.I32, Nullability.NonNullable), Nullability.NonNullable);

        byte[] eight = new byte[8];
        int node = f.Arena.AddListView(
            listType, 1, Validity.NonNullable, elements,
            f.Bytes(eight), PType.F64, f.Int32s([1]), PType.I32);
        RecordBatch batch = f.Batch(node);

        Assert.Throws<VortexFormatException>(() => { _ = batch.Root.AsList().GetOffset(0); });
    }

    [Fact]
    public void AValidityBitmapShorterThanTheArrayThrows()
    {
        using ColumnFixture f = new ColumnFixture();

        // A Bool node of 8 rows, used as validity for a 64-row column: the bitmap covers 1 byte.
        int bits = f.BoolNode([true, true, true, true, true, true, true, true]);
        int[] values = new int[64];
        int node = f.Arena.AddPrimitive(
            f.Types.Primitive(PType.I32, Nullability.Nullable), 64, Validity.Bitmap(bits),
            PType.I32, f.Int32s(values));
        RecordBatch batch = f.Batch(node);

        Assert.True(batch.Root.IsValid(0));
        Assert.Throws<VortexFormatException>(() => { _ = batch.Root.IsValid(63); });
        Assert.Throws<VortexFormatException>(() => { _ = batch.Root.NullCount; });
    }

    [Fact]
    public void AValidityIndexPointingAtANonBoolNodeThrows()
    {
        using ColumnFixture f = new ColumnFixture();
        int notABitmap = f.Int32Node([1, 2, 3], Validity.NonNullable);
        int node = f.Arena.AddPrimitive(
            f.Types.Primitive(PType.I32, Nullability.Nullable), 3, Validity.Bitmap(notABitmap),
            PType.I32, f.Int32s([4, 5, 6]));
        RecordBatch batch = f.Batch(node);

        Assert.Throws<VortexFormatException>(() => { _ = batch.Root.IsValid(0); });
        Assert.Throws<VortexFormatException>(() => { _ = batch.Root.NullCount; });
    }

    [Fact]
    public void AValidityIndexOutsideTheArenaThrows()
    {
        using ColumnFixture f = new ColumnFixture();
        int node = f.Arena.AddPrimitive(
            f.Types.Primitive(PType.I32, Nullability.Nullable), 1, Validity.Bitmap(9999),
            PType.I32, f.Int32s([1]));
        RecordBatch batch = f.Batch(node);

        Assert.Throws<VortexFormatException>(() => { _ = batch.Root.IsValid(0); });
    }

    [Fact]
    public void AStructDTypeOverANonStructNodeThrows()
    {
        using ColumnFixture f = new ColumnFixture();

        // The dtype says struct; the decoded node is a primitive. FieldCount comes from the dtype,
        // so the failure has to happen at the field access, and it has to be a format error.
        DType schema = f.Types.Struct(
            ["a"], [f.Types.Primitive(PType.I32, Nullability.NonNullable)], Nullability.NonNullable);
        int node = f.Arena.AddPrimitive(schema, 2, Validity.NonNullable, PType.I32, f.Int32s([1, 2]));
        RecordBatch batch = f.Batch(node);

        Assert.True(batch.IsTabular);
        Assert.Equal(1, batch.FieldCount);
        Assert.Throws<VortexFormatException>(() => { _ = batch.Column(0).Length; });
        Assert.Throws<VortexFormatException>(() => { _ = batch.Column("a"u8).Length; });
    }

    [Fact]
    public void AStructNodeWithFewerChildrenThanItsDTypeRefusesTheMissingField()
    {
        using ColumnFixture f = new ColumnFixture();

        // The batch constructor rejects this at the root; a NESTED struct is not checked there, so
        // StructColumn has to refuse the out-of-range field itself rather than read a sibling's
        // child slot.
        DType i32 = f.Types.Primitive(PType.I32, Nullability.NonNullable);
        DType inner = f.Types.Struct(["x", "y"], [i32, i32], Nullability.NonNullable);
        int x = f.Int32Node([1], Validity.NonNullable);
        int innerNode = f.Arena.AddStruct(inner, 1, Validity.NonNullable, [x]);

        DType outer = f.Types.Struct(["inner"], [inner], Nullability.NonNullable);
        int outerNode = f.Arena.AddStruct(outer, 1, Validity.NonNullable, [innerNode]);
        RecordBatch batch = f.Batch(outerNode);

        StructColumn column = batch.Column(0).AsStruct();
        Assert.Equal(1, column.FieldCount);
        Assert.Equal("x", column.GetFieldName(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => { _ = batch.Column(0).AsStruct().GetField(1).Length; });
        Assert.False(batch.Column(0).AsStruct().TryGetFieldIndex("y"u8, out int missing));
        Assert.Equal(-1, missing);
    }

    [Fact]
    public void AnExtensionOverATruncatedStorageThrows()
    {
        using ColumnFixture f = new ColumnFixture();
        DType storage = f.Types.Primitive(PType.I32, Nullability.NonNullable);
        int values = f.Arena.AddPrimitive(storage, 1, Validity.NonNullable, PType.I32, f.Int32s([0]));
        DType ext = f.Types.Extension("vortex.date", storage, [(byte)VortexTimeUnit.Days]);

        // The extension claims 4 rows; its storage holds 1.
        int node = f.Arena.AddExtension(ext, 4, values);
        RecordBatch batch = f.Batch(node);

        Assert.Equal(new DateOnly(1970, 1, 1), batch.Root.AsExtension().ToDateOnly(0));
        Assert.Throws<VortexFormatException>(() => batch.Root.AsExtension().ToDateOnly(3));
    }

    [Fact]
    public void AnExtensionOverANonPrimitiveStorageThrows()
    {
        using ColumnFixture f = new ColumnFixture();
        DType storage = f.Types.Primitive(PType.I32, Nullability.NonNullable);
        int bogus = f.Arena.AddNull(f.Types.Null(Nullability.Nullable), 1);
        DType ext = f.Types.Extension("vortex.date", storage, [(byte)VortexTimeUnit.Days]);
        int node = f.Arena.AddExtension(ext, 1, bogus);
        RecordBatch batch = f.Batch(node);

        // AddExtension copies the storage's validity, and a Null node is AllInvalid, so the row
        // reads as null before the storage kind is even consulted.
        Assert.False(batch.Root.AsExtension().IsValid(0));
        Assert.Throws<InvalidOperationException>(() => batch.Root.AsExtension().ToDateOnly(0));
    }

    [Fact]
    public void AnExtensionWithMalformedMetadataIsAFormatError()
    {
        using ColumnFixture f = new ColumnFixture();
        DType storage = f.Types.Primitive(PType.I32, Nullability.NonNullable);
        int values = f.Arena.AddPrimitive(storage, 1, Validity.NonNullable, PType.I32, f.Int32s([0]));

        // Empty metadata: vortex.date requires exactly one TimeUnit byte.
        DType ext = f.Types.Extension("vortex.date", storage, default);
        int node = f.Arena.AddExtension(ext, 1, values);
        RecordBatch batch = f.Batch(node);

        Assert.Throws<VortexFormatException>(() => { _ = batch.Root.AsExtension().TimeUnit; });
        Assert.Throws<VortexFormatException>(() => batch.Root.AsExtension().ToDateOnly(0));

        // A unit vortex.date does not admit at all.
        DType seconds = f.Types.Extension("vortex.date", storage, [(byte)VortexTimeUnit.Seconds]);
        RecordBatch bad = f.Batch(f.Arena.AddExtension(seconds, 1, values));
        Assert.Throws<VortexFormatException>(() => { _ = bad.Root.AsExtension().TimeUnit; });

        // A unit byte outside the enum.
        DType undefined = f.Types.Extension("vortex.date", storage, [(byte)99]);
        RecordBatch worse = f.Batch(f.Arena.AddExtension(undefined, 1, values));
        Assert.Throws<VortexFormatException>(() => { _ = worse.Root.AsExtension().TimeUnit; });
    }

    [Fact]
    public void AViewWhoseSizeOverflowsAnIntIsRejected()
    {
        using ColumnFixture f = new ColumnFixture();

        // size = 0xFFFFFFFF: the inline test (size <= 12) fails, so it takes the buffer path, and
        // `offset + size` must be evaluated in 64 bits or it wraps back inside the buffer.
        byte[] view = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(view, uint.MaxValue);
        BinaryPrimitives.WriteUInt32LittleEndian(view.AsSpan(8), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(view.AsSpan(12), 8);

        VortexBuffer data = f.Bytes(new byte[16]);
        int node = f.Arena.AddVarBinView(
            f.Types.Binary(Nullability.NonNullable), 1, Validity.NonNullable, f.Bytes(view), [data]);
        RecordBatch batch = f.Batch(node);

        Assert.Throws<VortexFormatException>(() => { _ = batch.Root.AsBinary().GetSpan(0).Length; });
        Assert.Throws<VortexFormatException>(() => batch.Root.AsBinary().GetLength(0));
    }

    [Fact]
    public void ABareDecimalNodeRefusesEveryRow()
    {
        using ColumnFixture f = new ColumnFixture();

        // AddBare sets only the four common fields, so the storage width, precision and scale are
        // all zero and the values buffer is empty - the shape a decoder would leave behind if it
        // allocated a node and then failed. Every accessor must refuse it rather than read the
        // empty buffer as I8 values.
        int node = f.Arena.AddBare(
            CanonicalKind.Decimal, f.Types.Decimal(9, 2, Nullability.NonNullable), 3, Validity.NonNullable);
        RecordBatch batch = f.Batch(node);

        Assert.Equal(DecimalStorageType.I8, batch.Root.AsDecimal().Storage);
        Assert.Throws<VortexFormatException>(() => { _ = batch.Root.AsDecimal().StorageBytes.Length; });
        Assert.Throws<VortexFormatException>(() => { _ = batch.Root.AsDecimal()[0]; });
        Assert.Throws<VortexFormatException>(() => { _ = batch.Root.AsDecimal().AsInt8.Length; });
        Assert.Throws<ArgumentOutOfRangeException>(() => { _ = batch.Root.AsDecimal()[3]; });
    }
}
