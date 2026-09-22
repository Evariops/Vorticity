// The precision -> storage-width table is transcribed from the reference implementation's
// smallest_decimal_value_type. Every boundary pair
// is asserted on both sides, because a guessed boundary reads a legal file at the wrong width.
using System;
using Vorticity;
using Vorticity.Types.Numerics;
using Xunit;

namespace Vorticity.Tests.Types.Numerics;

public sealed class DecimalStorageTests
{
    [Theory]
    [InlineData(1, DecimalStorageType.I8)]
    [InlineData(2, DecimalStorageType.I8)]
    [InlineData(3, DecimalStorageType.I16)]
    [InlineData(4, DecimalStorageType.I16)]
    [InlineData(5, DecimalStorageType.I32)]
    [InlineData(9, DecimalStorageType.I32)]
    [InlineData(10, DecimalStorageType.I64)]
    [InlineData(18, DecimalStorageType.I64)]
    [InlineData(19, DecimalStorageType.I128)]
    [InlineData(38, DecimalStorageType.I128)]
    [InlineData(39, DecimalStorageType.I256)]
    [InlineData(76, DecimalStorageType.I256)]
    internal void BoundaryPairs(int precision, DecimalStorageType expected) =>
        Assert.Equal(expected, DecimalStorage.ForPrecision((byte)precision));

    [Fact]
    public void EveryPrecisionInRangeIsMonotonic()
    {
        DecimalStorageType previous = DecimalStorageType.I8;
        for (byte precision = 1; precision <= 76; precision++)
        {
            DecimalStorageType storage = DecimalStorage.ForPrecision(precision);
            Assert.True(storage >= previous);
            previous = storage;
            Assert.True(DecimalStorage.IsDefined(storage));
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(77)]
    [InlineData(78)]
    [InlineData(100)]
    [InlineData(255)]
    public void PrecisionOutsideOneToSeventySixIsRejected(int precision) =>
        Assert.Throws<VortexFormatException>(
            () => { _ = DecimalStorage.ForPrecision((byte)precision); });

    [Theory]
    [InlineData(DecimalStorageType.I8, 1)]
    [InlineData(DecimalStorageType.I16, 2)]
    [InlineData(DecimalStorageType.I32, 4)]
    [InlineData(DecimalStorageType.I64, 8)]
    [InlineData(DecimalStorageType.I128, 16)]
    [InlineData(DecimalStorageType.I256, 32)]
    internal void ByteWidths(DecimalStorageType storage, int expected) =>
        Assert.Equal(expected, DecimalStorage.ByteWidth(storage));

    [Theory]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(255)]
    public void UndefinedStorageValueIsRejected(int raw)
    {
        DecimalStorageType storage = (DecimalStorageType)(byte)raw;
        Assert.False(DecimalStorage.IsDefined(storage));
        Assert.Throws<VortexFormatException>(() => { _ = DecimalStorage.ByteWidth(storage); });
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void DefinedStorageValuesAreAccepted(int raw)
    {
        DecimalStorageType storage = (DecimalStorageType)(byte)raw;
        Assert.True(DecimalStorage.IsDefined(storage));
        Assert.True(DecimalStorage.ByteWidth(storage) > 0);
    }

    [Theory]
    [InlineData(0u, DecimalStorageType.I8)]
    [InlineData(5u, DecimalStorageType.I256)]
    internal void WireValuesInRangeNarrow(uint raw, DecimalStorageType expected)
    {
        Assert.True(DecimalStorage.IsDefinedWireValue(raw, out DecimalStorageType storage));
        Assert.Equal(expected, storage);
    }

    [Theory]
    [InlineData(6u)]
    [InlineData(255u)]
    [InlineData(256u)]           // truncates to I8 if cast to byte first - the trap this closes
    [InlineData(261u)]           // truncates to I256
    [InlineData(uint.MaxValue)]
    public void WireValuesOutOfRangeAreRejectedBeforeTruncation(uint raw)
    {
        Assert.False(DecimalStorage.IsDefinedWireValue(raw, out DecimalStorageType storage));
        Assert.Equal(DecimalStorageType.I8, storage);

        // The narrowed guard cannot see the same problem, which is why the wire guard exists:
        // 256 and 261 both survive IsDefined once they have been cast to a byte.
        if ((raw & 0xFF) <= 5)
        {
            Assert.True(DecimalStorage.IsDefined((DecimalStorageType)(byte)raw));
        }
    }

    [Theory]
    [InlineData(DecimalStorageType.I8)]
    [InlineData(DecimalStorageType.I16)]
    [InlineData(DecimalStorageType.I32)]
    [InlineData(DecimalStorageType.I64)]
    [InlineData(DecimalStorageType.I128)]
    [InlineData(DecimalStorageType.I256)]
    internal void FromByteWidthInvertsByteWidth(DecimalStorageType storage) =>
        Assert.Equal(storage, DecimalStorage.FromByteWidth(DecimalStorage.ByteWidth(storage)));

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(-1)]
    [InlineData(12)]
    [InlineData(33)]
    public void FromByteWidthRejectsAWidthOutsideTheSix(int width) =>
        Assert.Throws<VortexFormatException>(() => DecimalStorage.FromByteWidth(width));

    [Fact]
    public void WireEnumValuesAreFrozen()
    {
        // The wire enum DecimalType is I8=0, I16=1, I32=2, I64=3, I128=4, I256=5.
        // These are protobuf values; renumbering them would silently misread every decimal column.
        Assert.Equal((byte)0, (byte)DecimalStorageType.I8);
        Assert.Equal((byte)1, (byte)DecimalStorageType.I16);
        Assert.Equal((byte)2, (byte)DecimalStorageType.I32);
        Assert.Equal((byte)3, (byte)DecimalStorageType.I64);
        Assert.Equal((byte)4, (byte)DecimalStorageType.I128);
        Assert.Equal((byte)5, (byte)DecimalStorageType.I256);
    }
}
