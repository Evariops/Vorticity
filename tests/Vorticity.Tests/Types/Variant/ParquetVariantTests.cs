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
}
