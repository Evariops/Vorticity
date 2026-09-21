// The typed views, their exact-type rule, and the four validity
// kinds at row 0, a middle row and the last row.
using System;
using System.Buffers.Binary;
using System.Text;
using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Columns;
using Vorticity.Types;
using Vorticity.Types.Numerics;
using Xunit;

namespace Vorticity.Tests.Columns;

public sealed class ColumnAccessorTests
{
    [Fact]
    public void NonNullableValidityNeverTouchesMemory()
    {
        using ColumnFixture f = new ColumnFixture();
        RecordBatch batch = f.Batch(f.Int32Node([1, 2, 3], Validity.NonNullable));

        Assert.Equal(ValidityKind.NonNullable, batch.Root.ValidityKind);
        Assert.True(batch.Root.IsAllValid);
        Assert.Equal(0, batch.Root.NullCount);
        Assert.True(batch.Root.IsValid(0));
        Assert.True(batch.Root.IsValid(1));
        Assert.True(batch.Root.IsValid(2));
    }

    [Fact]
    public void AllValidAndAllInvalidAnswerWithoutABitmap()
    {
        using ColumnFixture f = new ColumnFixture();
        RecordBatch allValid = f.Batch(f.Int32Node([1, 2, 3], Validity.AllValid, Nullability.Nullable));
        Assert.Equal(ValidityKind.AllValid, allValid.Root.ValidityKind);
        Assert.True(allValid.Root.IsAllValid);
        Assert.Equal(0, allValid.Root.NullCount);
        Assert.True(allValid.Root.IsValid(0));
        Assert.True(allValid.Root.IsValid(2));

        RecordBatch allInvalid = f.Batch(f.Int32Node([1, 2, 3], Validity.AllInvalid, Nullability.Nullable));
        Assert.Equal(ValidityKind.AllInvalid, allInvalid.Root.ValidityKind);
        Assert.False(allInvalid.Root.IsAllValid);
        Assert.Equal(3, allInvalid.Root.NullCount);
        Assert.False(allInvalid.Root.IsValid(0));
        Assert.False(allInvalid.Root.IsValid(1));
        Assert.False(allInvalid.Root.IsValid(2));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(7)]
    public void BitmapValidityAppliesTheBitOffset(int bitOffset)
    {
        using ColumnFixture f = new ColumnFixture();

        // 1023 rows so the last bit lands in a partial byte for every offset.
        const int Rows = 1023;
        bool[] valid = new bool[Rows];
        int[] values = new int[Rows];
        int expectedNulls = 0;
        for (int i = 0; i < Rows; i++)
        {
            valid[i] = i % 3 != 0;
            values[i] = i;
            if (!valid[i])
            {
                expectedNulls++;
            }
        }

        Validity validity = f.BitmapValidity(valid, bitOffset);
        RecordBatch batch = f.Batch(f.Int32Node(values, validity, Nullability.Nullable));

        Assert.Equal(ValidityKind.Bitmap, batch.Root.ValidityKind);
        Assert.False(batch.Root.IsAllValid);
        for (int i = 0; i < Rows; i++)
        {
            Assert.Equal(valid[i], batch.Root.IsValid(i));
        }

        Assert.Equal(expectedNulls, batch.Root.NullCount);

        // Cached: the second call must agree with the first.
        Assert.Equal(expectedNulls, batch.Root.NullCount);
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
    public void NullCountOverTheBoundaryRowCounts(int rows)
    {
        using ColumnFixture f = new ColumnFixture();
        bool[] valid = new bool[rows];
        int[] values = new int[rows];
        int expected = 0;
        for (int i = 0; i < rows; i++)
        {
            valid[i] = (i & 1) == 0;
            values[i] = i;
            if (!valid[i])
            {
                expected++;
            }
        }

        Validity validity = rows == 0 ? Validity.AllValid : f.BitmapValidity(valid, 5);
        RecordBatch batch = f.Batch(f.Int32Node(values, validity, Nullability.Nullable));

        Assert.Equal(rows, batch.RowCount);
        Assert.Equal(expected, batch.Root.NullCount);
        if (rows > 0)
        {
            Assert.True(batch.Root.IsValid(0));
            Assert.Equal(valid[rows / 2], batch.Root.IsValid(rows / 2));
            Assert.Equal(valid[rows - 1], batch.Root.IsValid(rows - 1));
        }
    }

    [Fact]
    public void RowIndexOutOfRangeIsACallerError()
    {
        using ColumnFixture f = new ColumnFixture();
        RecordBatch batch = f.Batch(f.Int32Node([1, 2, 3], Validity.NonNullable));

        Assert.Throws<ArgumentOutOfRangeException>(() => { _ = batch.Root.IsValid(-1); });
        Assert.Throws<ArgumentOutOfRangeException>(() => { _ = batch.Root.IsValid(3); });
        Assert.Throws<ArgumentOutOfRangeException>(() => { _ = batch.Root.AsPrimitive<int>()[3]; });
        Assert.Throws<ArgumentOutOfRangeException>(() => { _ = batch.Root.AsPrimitive<int>()[-1]; });
    }

    [Fact]
    public void EmptyColumnHasNoValidRow()
    {
        using ColumnFixture f = new ColumnFixture();
        RecordBatch batch = f.Batch(f.Int32Node([], Validity.NonNullable));

        Assert.Equal(0, batch.RowCount);
        Assert.Equal(0, batch.Root.AsPrimitive<int>().Values.Length);
        Assert.Equal(0, batch.Root.NullCount);
        Assert.Throws<ArgumentOutOfRangeException>(() => { _ = batch.Root.IsValid(0); });
    }

    [Fact]
    public void AsPrimitiveDemandsTheExactElementType()
    {
        using ColumnFixture f = new ColumnFixture();
        RecordBatch batch = f.Batch(f.Int32Node([7], Validity.NonNullable));

        Assert.Equal(7, batch.Root.AsPrimitive<int>()[0]);

        // Same width, different signedness: still refused.
        Assert.Throws<InvalidOperationException>(() => { _ = batch.Root.AsPrimitive<uint>().Length; });
        Assert.Throws<InvalidOperationException>(() => { _ = batch.Root.AsPrimitive<long>().Length; });
        Assert.Throws<InvalidOperationException>(() => { _ = batch.Root.AsPrimitive<float>().Length; });
        Assert.Throws<InvalidOperationException>(() => { _ = batch.Root.AsPrimitive<byte>().Length; });
    }

    [Fact]
    public void EveryPTypeMapsToItsExactDotNetType()
    {
        using ColumnFixture f = new ColumnFixture();

        AssertMapping<byte>(f, PType.U8, [1, 2]);
        AssertMapping<ushort>(f, PType.U16, [1, 2]);
        AssertMapping<uint>(f, PType.U32, [1, 2]);
        AssertMapping<ulong>(f, PType.U64, [1, ulong.MaxValue]);
        AssertMapping<sbyte>(f, PType.I8, [-1, 2]);
        AssertMapping<short>(f, PType.I16, [-1, 2]);
        AssertMapping<int>(f, PType.I32, [-1, 2]);
        AssertMapping<long>(f, PType.I64, [long.MinValue, 2]);
        AssertMapping<float>(f, PType.F32, [float.NegativeInfinity, 1.5f]);
        AssertMapping<double>(f, PType.F64, [double.NaN, -0.0]);
        AssertMapping<Half>(f, PType.F16, [Half.NegativeZero, Half.PositiveInfinity]);
    }

    [Fact]
    public void F16RoundTripsBitForBit()
    {
        using ColumnFixture f = new ColumnFixture();

        // The sidecar's `bits` is normative, so -0.0 and a NaN payload must survive exactly.
        Half[] values = [Half.NegativeZero, Half.Zero, BitConverter.UInt16BitsToHalf(0x7E00), BitConverter.UInt16BitsToHalf(0x7C00)];
        byte[] raw = new byte[values.Length * 2];
        for (int i = 0; i < values.Length; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(i * 2), BitConverter.HalfToUInt16Bits(values[i]));
        }

        int node = f.Arena.AddPrimitive(
            f.Types.Primitive(PType.F16, Nullability.NonNullable), values.Length, Validity.NonNullable,
            PType.F16, f.Bytes(raw));
        RecordBatch batch = f.Batch(node);

        ReadOnlySpan<Half> read = batch.Root.AsPrimitive<Half>().Values;
        Assert.Equal((ushort)0x8000, BitConverter.HalfToUInt16Bits(read[0]));
        Assert.Equal((ushort)0x0000, BitConverter.HalfToUInt16Bits(read[1]));
        Assert.Equal((ushort)0x7E00, BitConverter.HalfToUInt16Bits(read[2]));
        Assert.Equal((ushort)0x7C00, BitConverter.HalfToUInt16Bits(read[3]));
    }

    [Fact]
    public void EveryDowncastRefusesTheWrongKind()
    {
        using ColumnFixture f = new ColumnFixture();
        RecordBatch batch = f.Batch(f.Int32Node([1], Validity.NonNullable));

        Assert.Throws<InvalidOperationException>(() => { _ = batch.Root.AsNull().Length; });
        Assert.Throws<InvalidOperationException>(() => { _ = batch.Root.AsBool().Length; });
        Assert.Throws<InvalidOperationException>(() => { _ = batch.Root.AsDecimal().Length; });
        Assert.Throws<InvalidOperationException>(() => { _ = batch.Root.AsBinary().Length; });
        Assert.Throws<InvalidOperationException>(() => { _ = batch.Root.AsStruct().Length; });
        Assert.Throws<InvalidOperationException>(() => { _ = batch.Root.AsList().Length; });
        Assert.Throws<InvalidOperationException>(() => { _ = batch.Root.AsFixedSizeList().Length; });
        Assert.Throws<InvalidOperationException>(() => { _ = batch.Root.AsExtension().Length; });
        Assert.Equal(CanonicalKind.Primitive, batch.Root.Kind);
    }

    [Fact]
    public void NullColumnIsAllInvalid()
    {
        using ColumnFixture f = new ColumnFixture();
        int node = f.Arena.AddNull(f.Types.Null(Nullability.Nullable), 5);
        RecordBatch batch = f.Batch(node);

        Assert.Equal(5, batch.Root.AsNull().Length);
        Assert.Equal(ValidityKind.AllInvalid, batch.Root.ValidityKind);
        Assert.Equal(5, batch.Root.NullCount);
        Assert.False(batch.Root.AsNull().IsValid(0));
        Assert.False(batch.Root.AsNull().IsValid(4));
        Assert.Throws<ArgumentOutOfRangeException>(() => { _ = batch.Root.AsNull().IsValid(5); });
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(7)]
    public void BoolColumnExposesItsBitmapAndOffset(int bitOffset)
    {
        using ColumnFixture f = new ColumnFixture();
        bool[] bits = [true, false, true, true, false, false, true, false, true];
        int node = f.Arena.AddBool(
            f.Types.Bool(Nullability.NonNullable), bits.Length, Validity.NonNullable,
            f.Bitmap(bits, bitOffset), bitOffset);
        RecordBatch batch = f.Batch(node);

        BoolColumn column = batch.Root.AsBool();
        Assert.Equal(bits.Length, column.Length);
        Assert.Equal(bitOffset, column.BitOffset);
        Assert.Equal((bitOffset + bits.Length + 7) / 8, column.Bits.Length);
        for (int i = 0; i < bits.Length; i++)
        {
            Assert.Equal(bits[i], batch.Root.AsBool()[i]);
        }

        Assert.Throws<ArgumentOutOfRangeException>(() => { _ = batch.Root.AsBool()[bits.Length]; });
    }

    [Fact]
    public void DecimalExposesRawStorageAndWidensExactly()
    {
        using ColumnFixture f = new ColumnFixture();

        // decimal(4,2): i16 storage. 12345 does not fit; 1234 and -1234 do.
        Int256[] unscaled = [new Int256(1234), new Int256(-1234), Int256.Zero];
        RecordBatch batch = f.Batch(f.DecimalNode(unscaled, 4, 2, Nullability.NonNullable));

        DecimalColumn column = batch.Root.AsDecimal();
        Assert.Equal(DecimalStorageType.I16, column.Storage);
        Assert.Equal((byte)4, column.Precision);
        Assert.Equal((sbyte)2, column.Scale);
        Assert.Equal(6, column.StorageBytes.Length);
        Assert.Equal(3, column.AsInt16.Length);
        Assert.Equal((short)1234, column.AsInt16[0]);
        Assert.Equal((short)-1234, column.AsInt16[1]);

        Assert.Equal("12.34", batch.Root.AsDecimal()[0].ToString());
        Assert.Equal("-12.34", batch.Root.AsDecimal()[1].ToString());
        Assert.Equal("0.00", batch.Root.AsDecimal()[2].ToString());

        Assert.Throws<InvalidOperationException>(() => { _ = batch.Root.AsDecimal().AsInt32.Length; });
        Assert.Throws<InvalidOperationException>(() => { _ = batch.Root.AsDecimal().AsInt8.Length; });
        Assert.Throws<InvalidOperationException>(() => { _ = batch.Root.AsDecimal().AsInt64.Length; });
        Assert.Throws<InvalidOperationException>(() => { _ = batch.Root.AsDecimal().AsInt128.Length; });
    }

    [Theory]
    [InlineData((byte)2, DecimalStorageType.I8)]
    [InlineData((byte)4, DecimalStorageType.I16)]
    [InlineData((byte)9, DecimalStorageType.I32)]
    [InlineData((byte)18, DecimalStorageType.I64)]
    [InlineData((byte)38, DecimalStorageType.I128)]
    [InlineData((byte)40, DecimalStorageType.I256)]
    public void DecimalReadsEveryStorageWidth(byte precision, DecimalStorageType expected)
    {
        using ColumnFixture f = new ColumnFixture();
        Int256[] unscaled = [new Int256(-7), new Int256(7)];
        RecordBatch batch = f.Batch(f.DecimalNode(unscaled, precision, 1, Nullability.NonNullable));

        Assert.Equal(expected, batch.Root.AsDecimal().Storage);
        Assert.Equal(
            DecimalStorage.ByteWidth(expected) * 2,
            batch.Root.AsDecimal().StorageBytes.Length);
        Assert.Equal("-0.7", batch.Root.AsDecimal()[0].ToString());
        Assert.Equal("0.7", batch.Root.AsDecimal()[1].ToString());
    }

    [Fact]
    public void DecimalI256CarriesThePrecision76Range()
    {
        using ColumnFixture f = new ColumnFixture();

        // The exact value types/decimal40_10 stores at row 1.
        Int256 big = ParseDecimalDigits("-9999999999999999999999999999999999999999");
        RecordBatch batch = f.Batch(f.DecimalNode([big], 40, 10, Nullability.NonNullable));

        Assert.Equal(DecimalStorageType.I256, batch.Root.AsDecimal().Storage);
        Assert.Equal(big, batch.Root.AsDecimal()[0].Unscaled);
        Assert.Equal("-999999999999999999999999999999.9999999999", batch.Root.AsDecimal()[0].ToString());
    }

    [Fact]
    public void BinaryValuesInlineAndSpill()
    {
        using ColumnFixture f = new ColumnFixture();

        // 0, 12 and 13 bytes bracket the inline/spill boundary; the NUL and the non-ASCII value
        // are what utf8_embedded_nul and utf8_non_ascii exist to catch.
        byte[] empty = [];
        byte[] twelve = Encoding.UTF8.GetBytes("123456789012");
        byte[] thirteen = Encoding.UTF8.GetBytes("1234567890123");
        byte[] withNul = [0x61, 0x00, 0x62];
        byte[] nonAscii = Encoding.UTF8.GetBytes("élan 名前 😀");

        byte[]?[] values = [empty, twelve, thirteen, withNul, nonAscii, null];
        RecordBatch batch = f.Batch(f.Utf8Node(values, Nullability.Nullable));

        BinaryColumn column = batch.Root.AsBinary();
        Assert.True(column.IsUtf8);
        Assert.Equal(6, column.Length);

        Assert.Equal(0, batch.Root.AsBinary().GetLength(0));
        Assert.True(batch.Root.AsBinary().GetSpan(0).IsEmpty);
        Assert.Equal(12, batch.Root.AsBinary().GetLength(1));
        Assert.True(batch.Root.AsBinary().GetSpan(1).SequenceEqual(twelve));
        Assert.Equal(13, batch.Root.AsBinary().GetLength(2));
        Assert.True(batch.Root.AsBinary().GetSpan(2).SequenceEqual(thirteen));
        Assert.True(batch.Root.AsBinary().GetSpan(3).SequenceEqual(withNul));
        Assert.Equal("a\0b", batch.Root.AsBinary().GetString(3));
        Assert.True(batch.Root.AsBinary().GetSpan(4).SequenceEqual(nonAscii));
        Assert.Equal("élan 名前 😀", batch.Root.AsBinary().GetString(4));
        Assert.Equal(nonAscii.Length, batch.Root.AsBinary().GetLength(4));

        // The null row: an empty span, a zero length and a null string - never a dereferenced view.
        Assert.False(batch.Root.AsBinary().IsValid(5));
        Assert.True(batch.Root.AsBinary().GetSpan(5).IsEmpty);
        Assert.Equal(0, batch.Root.AsBinary().GetLength(5));
        Assert.Null(batch.Root.AsBinary().GetString(5));

        Assert.Throws<ArgumentOutOfRangeException>(() => { _ = batch.Root.AsBinary().GetLength(6); });
        Assert.Throws<ArgumentOutOfRangeException>(() => batch.Root.AsBinary().GetString(6));
    }

    [Fact]
    public void BinaryDTypeIsNotUtf8()
    {
        using ColumnFixture f = new ColumnFixture();
        byte[] raw = [0xFF, 0xFE];
        RecordBatch batch = f.Batch(f.Utf8Node([raw], Nullability.NonNullable, DTypeKind.Binary));

        Assert.False(batch.Root.AsBinary().IsUtf8);
        Assert.True(batch.Root.AsBinary().GetSpan(0).SequenceEqual(raw));
    }

    [Fact]
    public void AForgedViewCannotEscapeItsDataBuffer()
    {
        using ColumnFixture f = new ColumnFixture();

        // One row, 100 bytes claimed at offset 0 of a 4-byte data buffer. The decoders reject this
        // shape; a column accessor that trusted them would read 96 bytes past the segment.
        byte[] view = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(view, 100);
        BinaryPrimitives.WriteUInt32LittleEndian(view.AsSpan(8), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(view.AsSpan(12), 0);

        VortexBuffer data = f.Bytes([1, 2, 3, 4]);
        int node = f.Arena.AddVarBinView(
            f.Types.Binary(Nullability.NonNullable), 1, Validity.NonNullable, f.Bytes(view), [data]);
        RecordBatch batch = f.Batch(node);

        Assert.Throws<VortexFormatException>(() => { _ = batch.Root.AsBinary().GetSpan(0).Length; });
    }

    [Fact]
    public void AForgedViewCannotNameAMissingDataBuffer()
    {
        using ColumnFixture f = new ColumnFixture();
        byte[] view = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(view, 13);
        BinaryPrimitives.WriteUInt32LittleEndian(view.AsSpan(8), 9);
        BinaryPrimitives.WriteUInt32LittleEndian(view.AsSpan(12), 0);

        int node = f.Arena.AddVarBinView(
            f.Types.Binary(Nullability.NonNullable), 1, Validity.NonNullable, f.Bytes(view), []);
        RecordBatch batch = f.Batch(node);

        Assert.Throws<VortexFormatException>(() => { _ = batch.Root.AsBinary().GetSpan(0).Length; });
    }

    [Fact]
    public void ListRowsAreOffsetPlusSize()
    {
        using ColumnFixture f = new ColumnFixture();

        // The shape of types/list_i32: rows of 0, 1, 2 and 3 elements, plus a null row.
        int elements = f.Int32Node([10, 20, 21, 30, 31, 32], Validity.NonNullable);
        DType listType = f.Types.List(
            f.Types.Primitive(PType.I32, Nullability.NonNullable), Nullability.Nullable);

        int[] offsets = [0, 0, 1, 3, 0];
        int[] sizes = [0, 1, 2, 3, 0];
        Validity validity = f.BitmapValidity([true, true, true, true, false]);
        int node = f.Arena.AddListView(
            listType, 5, validity, elements, f.Int32s(offsets), PType.I32, f.Int32s(sizes), PType.I32);
        RecordBatch batch = f.Batch(node);

        ListColumn column = batch.Root.AsList();
        Assert.Equal(5, column.Length);
        Assert.Equal(PType.I32, column.OffsetPType);
        Assert.Equal(PType.I32, column.SizePType);
        Assert.Equal(6, column.Elements.Length);

        Assert.Equal(0, batch.Root.AsList().GetLength(0));
        Assert.Equal(1, batch.Root.AsList().GetLength(1));
        Assert.Equal(0L, batch.Root.AsList().GetOffset(1));
        Assert.Equal(2, batch.Root.AsList().GetLength(2));
        Assert.Equal(1L, batch.Root.AsList().GetOffset(2));
        Assert.Equal(3, batch.Root.AsList().GetLength(3));
        Assert.Equal(3L, batch.Root.AsList().GetOffset(3));

        // A null row reports nothing rather than surfacing an unspecified offset.
        Assert.False(batch.Root.AsList().IsValid(4));
        Assert.Equal(0, batch.Root.AsList().GetLength(4));
        Assert.Equal(0L, batch.Root.AsList().GetOffset(4));

        ReadOnlySpan<int> values = batch.Root.AsList().Elements.AsPrimitive<int>().Values;
        Assert.Equal(30, values[(int)batch.Root.AsList().GetOffset(3)]);

        Assert.Throws<ArgumentOutOfRangeException>(() => { _ = batch.Root.AsList().GetOffset(5); });
        Assert.Throws<ArgumentOutOfRangeException>(() => { _ = batch.Root.AsList().GetLength(-1); });
    }

    [Fact]
    public void ListOffsetsWiderThanLongAreMalformed()
    {
        using ColumnFixture f = new ColumnFixture();
        int elements = f.Int32Node([1], Validity.NonNullable);
        DType listType = f.Types.List(
            f.Types.Primitive(PType.I32, Nullability.NonNullable), Nullability.NonNullable);

        byte[] offsets = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(offsets, ulong.MaxValue);
        int node = f.Arena.AddListView(
            listType, 1, Validity.NonNullable, elements,
            f.Bytes(offsets), PType.U64, f.Int32s([1]), PType.I32);
        RecordBatch batch = f.Batch(node);

        Assert.Throws<VortexFormatException>(() => { _ = batch.Root.AsList().GetOffset(0); });
    }

    [Fact]
    public void FixedSizeListStridesByItsSize()
    {
        using ColumnFixture f = new ColumnFixture();

        // types/fsl_i32_3: three i32 per row.
        int elements = f.Int32Node([0, 1, 2, 3, 4, 5, 6, 7, 8], Validity.NonNullable);
        DType fslType = f.Types.FixedSizeList(
            f.Types.Primitive(PType.I32, Nullability.NonNullable), 3, Nullability.NonNullable);
        int node = f.Arena.AddFixedSizeList(fslType, 3, Validity.NonNullable, elements, 3);
        RecordBatch batch = f.Batch(node);

        FixedSizeListColumn column = batch.Root.AsFixedSizeList();
        Assert.Equal(3, column.Length);
        Assert.Equal(3u, column.Size);
        Assert.Equal(9, column.Elements.Length);

        batch.Root.AsFixedSizeList().GetRange(2, out int start, out int count);
        Assert.Equal(6, start);
        Assert.Equal(3, count);

        ReadOnlySpan<int> values = batch.Root.AsFixedSizeList().Elements.AsPrimitive<int>().Values;
        Assert.Equal(6, values[start]);
        Assert.Equal(8, values[start + count - 1]);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            batch.Root.AsFixedSizeList().GetRange(3, out _, out _));
    }

    [Fact]
    public void FixedSizeListOfSizeZeroDoesNotDivide()
    {
        using ColumnFixture f = new ColumnFixture();
        int elements = f.Int32Node([], Validity.NonNullable);
        DType fslType = f.Types.FixedSizeList(
            f.Types.Primitive(PType.I32, Nullability.NonNullable), 0, Nullability.NonNullable);
        int node = f.Arena.AddFixedSizeList(fslType, 4, Validity.NonNullable, elements, 0);
        RecordBatch batch = f.Batch(node);

        Assert.Equal(0u, batch.Root.AsFixedSizeList().Size);
        batch.Root.AsFixedSizeList().GetRange(3, out int start, out int count);
        Assert.Equal(0, start);
        Assert.Equal(0, count);
    }

    [Fact]
    public void ATruncatedPrimitiveBufferIsMalformedNotAnOverread()
    {
        using ColumnFixture f = new ColumnFixture();

        // AddPrimitive enforces the exact length, so reach past it with AddBare, which sets only
        // the four common fields and leaves the values buffer empty. A column claiming 4 rows over
        // 0 bytes must throw, not hand back a span it cannot back.
        int node = f.Arena.AddBare(
            CanonicalKind.Primitive, f.Types.Primitive(PType.U8, Nullability.NonNullable), 4,
            Validity.NonNullable);
        RecordBatch batch = f.Batch(node);

        Assert.Equal(PType.U8, batch.Root.AsPrimitive<byte>().PType);
        Assert.Throws<VortexFormatException>(() => { _ = batch.Root.AsPrimitive<byte>().Values.Length; });
        Assert.Throws<VortexFormatException>(() => { _ = batch.Root.AsPrimitive<byte>()[0]; });
    }

    private static void AssertMapping<T>(ColumnFixture f, PType ptype, T[] values)
        where T : unmanaged
    {
        ReadOnlySpan<byte> raw = System.Runtime.InteropServices.MemoryMarshal.AsBytes<T>(values);
        int node = f.Arena.AddPrimitive(
            f.Types.Primitive(ptype, Nullability.NonNullable), values.Length, Validity.NonNullable,
            ptype, f.Bytes(raw));
        RecordBatch batch = f.Batch(node);

        ReadOnlySpan<T> read = batch.Root.AsPrimitive<T>().Values;
        Assert.Equal(values.Length, read.Length);
        for (int i = 0; i < values.Length; i++)
        {
            Assert.True(read[i].Equals(values[i]) || IsBitEqual(read[i], values[i]));
        }
    }

    private static bool IsBitEqual<T>(T a, T b)
        where T : unmanaged
    {
        ReadOnlySpan<T> left = new ReadOnlySpan<T>(in a);
        ReadOnlySpan<T> right = new ReadOnlySpan<T>(in b);
        return System.Runtime.InteropServices.MemoryMarshal.AsBytes(left)
            .SequenceEqual(System.Runtime.InteropServices.MemoryMarshal.AsBytes(right));
    }

    /// <summary>Parses a signed decimal digit string into an <see cref="Int256"/>, test-side.</summary>
    internal static Int256 ParseDecimalDigits(string text)
    {
        bool negative = text.StartsWith('-');
        ReadOnlySpan<char> digits = negative ? text.AsSpan(1) : text.AsSpan();

        // Ten chunks of at most nine digits: 10^9 fits an int and Int256 has no multiply, so build
        // the magnitude with Int128 (precision 40 needs 133 bits; Int128 is not enough) - do it in
        // two halves and combine through the byte representation instead.
        System.Numerics.BigInteger magnitude = System.Numerics.BigInteger.Zero;
        foreach (char c in digits)
        {
            magnitude = (magnitude * 10) + (c - '0');
        }

        if (negative)
        {
            magnitude = -magnitude;
        }

        Span<byte> bytes = stackalloc byte[32];
        bytes.Fill(magnitude.Sign < 0 ? (byte)0xFF : (byte)0x00);
        magnitude.TryWriteBytes(bytes, out _, isUnsigned: false, isBigEndian: false);
        return Int256.FromLittleEndianBytes(bytes);
    }
}
