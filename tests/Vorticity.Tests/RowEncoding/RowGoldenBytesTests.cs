// The bytes themselves, spelled out.
//
// WHY THESE EXIST WHEN THE ORDER TESTS ALREADY PASS. A sort-order property test cannot see a
// layout that is internally consistent but different from the reference's: swap the empty and
// non-empty variable-width sentinels for 0x11 and 0x12, use 16-byte blocks instead of 32, or
// encode bools as 0x00/0x01, and every order assertion still holds while no other implementation
// can compare against our keys. Byte-exactness with Rust IS the feature, so it needs assertions
// that fail when a byte changes.
//
// Every expectation below is derived from the row format's rules and cross-read against
// vortex-row/src/codec.rs at 0.86.1.
using System;
using Vorticity.Types;
using Vorticity.Types.Numerics;
using Vorticity.RowEncoding;
using Xunit;

namespace Vorticity.Tests.RowEncoding;

public sealed class RowGoldenBytesTests
{
    /// <summary>Signed integers: big-endian with the sign bit flipped, nulls zero-filled.</summary>
    [Fact]
    public void SignedIntegerAscending()
    {
        using RowFixture fixture = new RowFixture();
        // Row 2 is null and its backing value is deliberately not zero: the encoder must ignore it.
        int column = fixture.Primitive<int>(PType.I32, [1, -1, 0x7F7F_7F7F], [true, true, false]);
        byte[][] rows = RowTestHelp.Rows(fixture, column, RowSortField.Ascending);

        Assert.Equal(RowTestHelp.Hex("01 80 00 00 01"), rows[0]);
        Assert.Equal(RowTestHelp.Hex("01 7F FF FF FF"), rows[1]);
        Assert.Equal(RowTestHelp.Hex("00 00 00 00 00"), rows[2]);
    }

    /// <summary>
    /// Descending inverts the VALUE bytes and nothing else - not the null sentinel, and not a
    /// null's zero fill.
    /// </summary>
    [Fact]
    public void SignedIntegerDescendingInvertsOnlyValueBytes()
    {
        using RowFixture fixture = new RowFixture();
        int column = fixture.Primitive<int>(PType.I32, [1, -1, 0x7F7F_7F7F], [true, true, false]);
        byte[][] rows = RowTestHelp.Rows(fixture, column, new RowSortField(descending: true, nullsFirst: true));

        Assert.Equal(RowTestHelp.Hex("01 7F FF FF FE"), rows[0]);
        Assert.Equal(RowTestHelp.Hex("01 80 00 00 00"), rows[1]);
        Assert.Equal(RowTestHelp.Hex("00 00 00 00 00"), rows[2]);
    }

    /// <summary>Nulls last moves the fixed-width null sentinel from 0x00 to 0x02, in either direction.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NullsLastUsesTheHighFixedSentinel(bool descending)
    {
        using RowFixture fixture = new RowFixture();
        int column = fixture.Primitive<int>(PType.I32, [1, 2], [true, false]);
        byte[][] rows = RowTestHelp.Rows(
            fixture, column, new RowSortField(descending, nullsFirst: false));

        Assert.Equal(0x01, rows[0][0]);
        Assert.Equal(RowTestHelp.Hex("02 00 00 00 00"), rows[1]);
    }

    /// <summary>Unsigned integers are already ordered: plain big-endian, no sign flip.</summary>
    [Fact]
    public void UnsignedIntegerIsPlainBigEndian()
    {
        using RowFixture fixture = new RowFixture();
        int column = fixture.Primitive<uint>(PType.U32, [0u, 1u, uint.MaxValue]);
        byte[][] rows = RowTestHelp.Rows(fixture, column, RowSortField.Ascending);

        Assert.Equal(RowTestHelp.Hex("01 00 00 00 00"), rows[0]);
        Assert.Equal(RowTestHelp.Hex("01 00 00 00 01"), rows[1]);
        Assert.Equal(RowTestHelp.Hex("01 FF FF FF FF"), rows[2]);
    }

    /// <summary>
    /// The float transform: a non-negative gets its sign bit set, a negative gets every bit
    /// flipped - which puts -0.0 immediately below +0.0 and leaves NaN ordered by its payload.
    /// </summary>
    [Fact]
    public void FloatTransformIsSignAware()
    {
        using RowFixture fixture = new RowFixture();
        int column = fixture.Primitive<double>(PType.F64, [-1.0, 0.0, -0.0]);
        byte[][] rows = RowTestHelp.Rows(fixture, column, RowSortField.Ascending);

        // -1.0 is 0xBFF0000000000000; negative, so every bit flips.
        Assert.Equal(RowTestHelp.Hex("01 40 0F FF FF FF FF FF FF"), rows[0]);
        // +0.0 is 0; non-negative, so only the top bit is set.
        Assert.Equal(RowTestHelp.Hex("01 80 00 00 00 00 00 00 00"), rows[1]);
        // -0.0 is 0x8000000000000000; negative, so every bit flips - landing just below +0.0.
        Assert.Equal(RowTestHelp.Hex("01 7F FF FF FF FF FF FF FF"), rows[2]);
    }

    /// <summary>
    /// NaNs are ordered by their raw bit pattern and are NOT canonicalized - which has a
    /// consequence sharp enough to deserve its own test: <see cref="double.NaN"/> in .NET is the
    /// NEGATIVE quiet NaN (0xFFF8000000000000, what 0.0/0.0 produces on x86 and ARM), so it sorts
    /// BELOW every finite value, while a positive NaN sorts above every one of them. Two values
    /// that are both "NaN" to a .NET programmer land at opposite ends of the key space.
    /// </summary>
    [Fact]
    public void NaNsAreOrderedByBitPatternAndNotCanonicalized()
    {
        using RowFixture fixture = new RowFixture();
        double positiveNaN = BitConverter.UInt64BitsToDouble(0x7FF8_0000_0000_0000);
        Assert.Equal(0xFFF8_0000_0000_0000, BitConverter.DoubleToUInt64Bits(double.NaN));

        int column = fixture.Primitive<double>(
            PType.F64, [double.NaN, positiveNaN, 0.0, double.NegativeInfinity, double.PositiveInfinity]);
        byte[][] rows = RowTestHelp.Rows(fixture, column, RowSortField.Ascending);

        // 0xFFF8... is negative, so every bit flips: 0x0007FFFFFFFFFFFF, below everything.
        Assert.Equal(RowTestHelp.Hex("01 00 07 FF FF FF FF FF FF"), rows[0]);
        // 0x7FF8... is non-negative, so only the top bit is set: above everything.
        Assert.Equal(RowTestHelp.Hex("01 FF F8 00 00 00 00 00 00"), rows[1]);

        int[] order = RowTestHelp.ByteOrder(rows);
        Assert.Equal([0, 3, 2, 4, 1], order);
    }

    /// <summary>
    /// Bool is 0x01/0x02, not 0x00/0x01: a false must stay distinguishable from a null's
    /// zero-filled body.
    /// </summary>
    [Theory]
    [InlineData(false, "01 01", "01 02")]
    [InlineData(true, "01 FE", "01 FD")]
    public void BoolUsesOneAndTwo(bool descending, string expectedFalse, string expectedTrue)
    {
        using RowFixture fixture = new RowFixture();
        int column = fixture.Bool([false, true, true], [true, true, false]);
        byte[][] rows = RowTestHelp.Rows(
            fixture, column, new RowSortField(descending, nullsFirst: true));

        Assert.Equal(RowTestHelp.Hex(expectedFalse), rows[0]);
        Assert.Equal(RowTestHelp.Hex(expectedTrue), rows[1]);
        Assert.Equal(RowTestHelp.Hex("00 00"), rows[2]);
    }

    /// <summary>The Null dtype has no body at all: one sentinel and nothing to order by.</summary>
    [Fact]
    public void NullDTypeIsASentinelAlone()
    {
        using RowFixture fixture = new RowFixture();
        int column = fixture.Nulls(2);
        byte[][] rows = RowTestHelp.Rows(fixture, column, RowSortField.Ascending);

        Assert.Equal([0x00], rows[0]);
        Assert.Equal([0x00], rows[1]);
    }

    /// <summary>Three variable-width sentinels, so byte 0 alone says null, empty or non-empty.</summary>
    [Fact]
    public void VariableWidthSentinelsAreThree()
    {
        using RowFixture fixture = new RowFixture();
        int column = fixture.VarBin([null, [], RowTestHelp.Utf8("a")]);
        byte[][] rows = RowTestHelp.Rows(fixture, column, RowSortField.Ascending);

        Assert.Equal([0x00], rows[0]);
        Assert.Equal([0x01], rows[1]);
        Assert.Equal(0x02, rows[2][0]);
    }

    /// <summary>
    /// A one-byte value still costs a whole 32-byte block, because the marker's position must be
    /// the same for every value of the same length class.
    /// </summary>
    [Fact]
    public void ShortValueFillsAWholeBlock()
    {
        using RowFixture fixture = new RowFixture();
        int column = fixture.VarBin([RowTestHelp.Utf8("a")]);
        byte[][] rows = RowTestHelp.Rows(fixture, column, RowSortField.Ascending);

        byte[] expected = new byte[34];
        expected[0] = 0x02;
        expected[1] = (byte)'a';
        expected[33] = 1;
        Assert.Equal(expected, rows[0]);
    }

    /// <summary>
    /// A length that is an exact multiple of 32 emits ONE block whose marker is 32 - not a full
    /// block plus an empty one, which would sort above a 33-byte value that shares its prefix.
    /// </summary>
    [Fact]
    public void ExactMultipleOfThirtyTwoEndsWithMarkerThirtyTwo()
    {
        using RowFixture fixture = new RowFixture();
        byte[] value = new byte[32];
        Array.Fill(value, (byte)'x');
        int column = fixture.VarBin([value]);
        byte[][] rows = RowTestHelp.Rows(fixture, column, RowSortField.Ascending);

        Assert.Equal(34, rows[0].Length);
        Assert.Equal(0x02, rows[0][0]);
        Assert.Equal(32, rows[0][33]);
    }

    /// <summary>Thirty-three bytes: one continuation block, then a final block of one byte.</summary>
    [Fact]
    public void ThirtyThreeBytesSpansTwoBlocks()
    {
        using RowFixture fixture = new RowFixture();
        byte[] value = new byte[33];
        Array.Fill(value, (byte)'x');
        int column = fixture.VarBin([value]);
        byte[][] rows = RowTestHelp.Rows(fixture, column, RowSortField.Ascending);

        Assert.Equal(67, rows[0].Length);
        Assert.Equal(0x02, rows[0][0]);
        Assert.Equal(0xFF, rows[0][33]);        // continuation
        Assert.Equal((byte)'x', rows[0][34]);   // the 33rd data byte
        Assert.Equal(0, rows[0][35]);           // zero padding
        Assert.Equal(1, rows[0][66]);           // final marker: one real byte
    }

    /// <summary>Descending inverts data, padding and markers - but never the null sentinel.</summary>
    [Fact]
    public void DescendingVariableWidthInvertsEverythingButTheNullSentinel()
    {
        using RowFixture fixture = new RowFixture();
        int column = fixture.VarBin([RowTestHelp.Utf8("a"), [], null]);
        byte[][] rows = RowTestHelp.Rows(
            fixture, column, new RowSortField(descending: true, nullsFirst: true));

        byte[] expected = new byte[34];
        expected[0] = 0xFD;                          // non-empty sentinel, complemented
        expected[1] = unchecked((byte)~(byte)'a');
        for (int i = 2; i < 33; i++)
        {
            expected[i] = 0xFF;                      // zero padding, complemented
        }

        expected[33] = unchecked((byte)~1);          // the length marker, complemented
        Assert.Equal(expected, rows[0]);
        Assert.Equal([0xFE], rows[1]);               // empty sentinel, complemented
        Assert.Equal([0x00], rows[2]);               // null sentinel, NOT complemented
    }

    /// <summary>A decimal's key width comes from its precision, not from its physical storage.</summary>
    [Fact]
    public void DecimalKeyWidthFollowsPrecision()
    {
        using RowFixture fixture = new RowFixture();
        // Precision 7 implies an i32 key; the physical storage here is i64 and must be narrowed.
        int column = fixture.Decimal([484, -5], precision: 7, scale: 5, DecimalStorageType.I64);
        byte[][] rows = RowTestHelp.Rows(fixture, column, RowSortField.Ascending);

        Assert.Equal(RowTestHelp.Hex("01 80 00 01 E4"), rows[0]);
        Assert.Equal(RowTestHelp.Hex("01 7F FF FF FB"), rows[1]);
    }

    /// <summary>Columns are concatenated left to right, which is what makes column 0 the primary key.</summary>
    [Fact]
    public void ColumnsConcatenateInOrder()
    {
        using RowFixture fixture = new RowFixture();
        int first = fixture.Primitive<int>(PType.I32, [1]);
        int second = fixture.Primitive<byte>(PType.U8, [7]);
        byte[][] rows = RowTestHelp.Rows(
            fixture, [first, second], RowSortField.Ascending, RowSortField.Ascending);

        Assert.Equal(RowTestHelp.Hex("01 80 00 00 01 01 07"), rows[0]);
    }
}
