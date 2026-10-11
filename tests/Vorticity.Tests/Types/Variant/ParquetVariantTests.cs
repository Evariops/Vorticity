using Vorticity.Arrays;
using Vorticity.Types;
using Vorticity.Types.Variant;
using Xunit;

namespace Vorticity.Tests.Types.Variant;

public sealed class ParquetVariantTests
{
    private readonly DTypeArena _types = new DTypeArena();
    private readonly ScalarStore _store = new ScalarStore();

    // The Parquet Variant has no unsigned integer: an unsigned value is written as a signed one wide
    // enough to hold it, never reinterpreted.
    [Theory]
    [InlineData(PType.U8, 0UL)]
    [InlineData(PType.U8, 127UL)]
    [InlineData(PType.U8, 128UL)]
    [InlineData(PType.U8, 200UL)]
    [InlineData(PType.U8, 255UL)]
    [InlineData(PType.U16, 32_767UL)]
    [InlineData(PType.U16, 40_000UL)]
    [InlineData(PType.U16, 65_535UL)]
    [InlineData(PType.U32, 2_147_483_647UL)]
    [InlineData(PType.U32, 3_000_000_000UL)]
    [InlineData(PType.U32, 4_294_967_295UL)]
    [InlineData(PType.U64, 9_223_372_036_854_775_807UL)]
    internal void AnUnsignedValueKeepsItsValue(PType ptype, ulong value)
    {
        DType dtype = _types.Primitive(ptype, Nullability.NonNullable);
        TypedScalar scalar = new TypedScalar(_store.UInt64(value), dtype);
        byte[] bytes = new byte[ParquetVariant.MeasureValue(in scalar, dtype)];
        ParquetVariant.WriteValue(in scalar, dtype, bytes);

        VariantValue read = ParquetVariant.Read(ParquetVariant.EmptyMetadata, bytes);
        Assert.Equal((long)value, read.Integer);
    }

    [Fact]
    public void AnUnsignedValueNoSignedIntegerHoldsHasNoEncoding()
    {
        DType dtype = _types.Primitive(PType.U64, Nullability.NonNullable);
        TypedScalar scalar = new TypedScalar(_store.UInt64(ulong.MaxValue), dtype);
        Assert.Equal(-1, ParquetVariant.MeasureValue(in scalar, dtype));
    }

    // Every primitive's size by its type id, the bytes after it ignored: a value is self-delimiting.
    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(2, 1)]
    [InlineData(3, 2)]
    [InlineData(4, 3)]
    [InlineData(5, 5)]
    [InlineData(6, 9)]
    [InlineData(7, 9)]
    [InlineData(8, 6)]
    [InlineData(9, 10)]
    [InlineData(10, 18)]
    [InlineData(11, 5)]
    [InlineData(12, 9)]
    [InlineData(13, 9)]
    [InlineData(14, 5)]
    [InlineData(17, 9)]
    [InlineData(18, 9)]
    [InlineData(19, 9)]
    [InlineData(20, 17)]
    public void APrimitiveTakesTheBytesItsTypeSays(int typeId, int size)
    {
        byte[] value = new byte[32];
        value[0] = (byte)(typeId << 2);
        Assert.Equal(size, ParquetVariant.SizeOf(value));
    }

    [Fact]
    public void AStringOrBinaryTakesItsLengthPastItsHeader()
    {
        Assert.Equal(1 + 7, ParquetVariant.SizeOf([(7 << 2) | 1, .. "iceberg"u8, 0xFF]));
        Assert.Equal(5 + 3, ParquetVariant.SizeOf([16 << 2, 3, 0, 0, 0, .. "abc"u8, 0xFF]));
        Assert.Equal(5 + 2, ParquetVariant.SizeOf([15 << 2, 2, 0, 0, 0, 0x0A, 0x0B]));
        Assert.Throws<VortexFormatException>(() => ParquetVariant.SizeOf([15 << 2, 9, 0, 0, 0, 0x0A, 0x0B]));
        Assert.Throws<VortexUnsupportedException>(() => ParquetVariant.SizeOf([21 << 2]));
    }

    // An object's offsets follow its names, not its values: {a: "bb", b: 1} with b's value first,
    // so a's offset is past b's and a value's end is its own encoding's, not the next offset.
    [Fact]
    public void AnObjectsValuesEndWhereTheirEncodingsSayWhateverTheOrderOfTheirOffsets()
    {
        byte[] value = [0x02, 2, 0, 1, 5, 0, 8, (5 << 2), 1, 0, 0, 0, (2 << 2) | 1, (byte)'b', (byte)'b', 0xEE];
        VariantNested nested = VariantNested.Read(value);
        Assert.Equal(2, nested.Count);
        Assert.Equal(15, nested.Size);
        Assert.Equal(15, ParquetVariant.SizeOf(value));
        Assert.Equal(0, nested.IdAt(0));
        Assert.Equal(1, nested.IdAt(1));
        Assert.Equal([(2 << 2) | 1, (byte)'b', (byte)'b'], nested.ValueAt(0).ToArray());
        Assert.Equal([5 << 2, 1, 0, 0, 0], nested.ValueAt(1).ToArray());
    }

    [Fact]
    public void AnArrayOrObjectCutShortIsRefused()
    {
        Assert.Throws<VortexFormatException>(() => VariantNested.Read([0x03, 2, 0, 1]));
        Assert.Throws<VortexFormatException>(() => VariantNested.Read([0x03, 1, 0, 9, 0]));
        Assert.Throws<VortexFormatException>(() => VariantNested.Read([0x02, 1, 0, 0, 5, 0x00]).ValueAt(0).Length);
    }

    // Four bytes of count where is_large is set: an array's flag is bit 2 of its header, an object's bit 4.
    [Fact]
    public void ALargeCountTakesFourBytes()
    {
        VariantNested array = VariantNested.Read([0x03 | (1 << 4), 1, 0, 0, 0, 0, 1, 0x00]);
        Assert.Equal(1, array.Count);
        Assert.Equal(8, array.Size);
        VariantNested empty = VariantNested.Read([0x02 | (1 << 6), 0, 0, 0, 0, 0]);
        Assert.Equal(0, empty.Count);
        Assert.Equal(6, empty.Size);
    }

    [Fact]
    public void ASortedDictionaryIsSearchedByHalvesAndAnUnsortedOneFromItsStart()
    {
        // Version 1, sorted, offsets of one byte: "a", "bb", "c".
        byte[] sorted = [0x11, 3, 0, 1, 3, 4, (byte)'a', (byte)'b', (byte)'b', (byte)'c'];
        VariantDictionary dictionary = VariantDictionary.Read(sorted);
        Assert.True(dictionary.Sorted);
        Assert.Equal(3, dictionary.Count);
        Assert.Equal(0, dictionary.Find("a"u8));
        Assert.Equal(1, dictionary.Find("bb"u8));
        Assert.Equal(2, dictionary.Find("c"u8));
        Assert.Equal(-1, dictionary.Find("b"u8));
        Assert.Equal("bb"u8.ToArray(), dictionary[1].ToArray());

        // Unsorted, with a name twice: the first is found.
        byte[] unsorted = [0x01, 3, 0, 1, 2, 3, (byte)'z', (byte)'a', (byte)'z'];
        VariantDictionary twice = VariantDictionary.Read(unsorted);
        Assert.False(twice.Sorted);
        Assert.Equal(0, twice.Find("z"u8));
        Assert.Equal(1, twice.Find("a"u8));
    }

    [Fact]
    public void ADictionaryWhoseOffsetsDoNotFitIsRefused()
    {
        Assert.Throws<VortexFormatException>(() => VariantDictionary.Read([0x01, 4, 0, 1]));
        Assert.Throws<VortexUnsupportedException>(() => VariantDictionary.Read([0x02, 0, 0]));
        Assert.Throws<VortexFormatException>(() => VariantDictionary.Read([0x01, 1, 0, 3, (byte)'a'])[0].Length);
        Assert.Throws<VortexFormatException>(() => VariantDictionary.Read([0x01, 1, 0, 1, (byte)'a'])[1].Length);
    }
}
