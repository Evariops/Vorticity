// VortexDecimal: the pinned ToString table from the Phase 1 contract §3, the System.Decimal
// conversion boundaries (96-bit magnitude AND a scale limit of 28), numeric CompareTo across
// scales, and structural Equals.
using System;
using System.Globalization;
using System.Numerics;
using Vorticity;
using Vorticity.Types.Numerics;
using Xunit;

namespace Vorticity.Tests.Types.Numerics;

public sealed class VortexDecimalTests
{
    // ---- the pinned ToString table -------------------------------------------------------

    [Theory]
    [InlineData(12345L, 2, "123.45")]
    [InlineData(5L, 3, "0.005")]
    [InlineData(-5L, 3, "-0.005")]
    [InlineData(0L, 3, "0.000")]
    [InlineData(123L, -2, "12300")]
    [InlineData(0L, -2, "0")]
    [InlineData(12345L, 0, "12345")]
    public void PinnedToStringVectors(long unscaled, int scale, string expected)
    {
        VortexDecimal value = VortexDecimal.FromInt64(unscaled, 20, (sbyte)scale);
        Assert.Equal(expected, value.ToString());

        Span<char> buffer = stackalloc char[VortexDecimal.MaxFormattedLength];
        Assert.True(value.TryFormat(buffer, out int written));
        Assert.Equal(expected, new string(buffer.Slice(0, written)));
    }

    [Fact]
    public void MinValueAtScaleZeroRendersTheExactSeventyEightCharDecimal()
    {
        VortexDecimal value = new VortexDecimal(Int256.MinValue, 76, 0);
        string text = value.ToString();
        Assert.Equal(Int256Tests.MinValueText, text);
        Assert.Equal(78, text.Length);
    }

    [Fact]
    public void MinValueAtANegativeScaleAppendsZerosWithoutOverflowing()
    {
        VortexDecimal value = new VortexDecimal(Int256.MinValue, 76, -128);
        string text = value.ToString();
        Assert.Equal(Int256Tests.MinValueText + new string('0', 128), text);
        Assert.Equal(VortexDecimal.MaxFormattedLength, text.Length);
    }

    [Fact]
    public void MinValueAtTheLargestPositiveScale()
    {
        // 77 digits with a scale of 76: one digit before the point.
        VortexDecimal value = new VortexDecimal(Int256.MinValue, 76, 76);
        string text = value.ToString();
        Assert.Equal("-5.7896044618658097711785492504343953926634992332820282019728792003956564819968",
            text);
    }

    [Fact]
    public void ZeroKeepsThePointAtAPositiveScaleAndLosesItAtANegativeOne()
    {
        Assert.Equal("0", VortexDecimal.FromInt64(0, 10, 0).ToString());
        Assert.Equal("0.0", VortexDecimal.FromInt64(0, 10, 1).ToString());
        Assert.Equal("0.0000000000000000000000000000",
            VortexDecimal.FromInt64(0, 76, 28).ToString());
        Assert.Equal("0", VortexDecimal.FromInt64(0, 10, -1).ToString());
        Assert.Equal("0", VortexDecimal.FromInt64(0, 10, -128).ToString());
    }

    [Fact]
    public void DigitCountEqualToScaleStillGetsALeadingZero()
    {
        Assert.Equal("0.45", VortexDecimal.FromInt64(45, 10, 2).ToString());
        Assert.Equal("-0.45", VortexDecimal.FromInt64(-45, 10, 2).ToString());
        Assert.Equal("4.5", VortexDecimal.FromInt64(45, 10, 1).ToString());
    }

    [Fact]
    public void RenderingMatchesAnIndependentStringOracle()
    {
        Random random = new Random(0x0D_EC_1_A1);
        byte[] bytes = new byte[Int256.ByteCount];
        for (int iteration = 0; iteration < 4_000; iteration++)
        {
            random.NextBytes(bytes);
            int keep = 1 + (iteration % Int256.ByteCount);
            byte fill = (bytes[keep - 1] & 0x80) != 0 ? (byte)0xFF : (byte)0x00;
            for (int i = keep; i < bytes.Length; i++)
            {
                bytes[i] = fill;
            }

            Int256 unscaled = Int256.FromLittleEndianBytes(bytes);
            sbyte scale = (sbyte)(random.Next(-128, 128));
            VortexDecimal value = new VortexDecimal(unscaled, 76, scale);
            Assert.Equal(Oracle(Int256Tests.ToBig(unscaled), scale), value.ToString());
        }
    }

    /// <summary>
    /// An independent rendering written with plain string operations - deliberately not the span
    /// algorithm under test.
    /// </summary>
    private static string Oracle(BigInteger unscaled, int scale)
    {
        bool negative = unscaled.Sign < 0;
        string digits = BigInteger.Abs(unscaled).ToString(CultureInfo.InvariantCulture);
        string body;
        if (scale <= 0)
        {
            body = unscaled.IsZero ? "0" : digits + new string('0', -scale);
        }
        else if (digits.Length > scale)
        {
            body = digits.Substring(0, digits.Length - scale) + "." +
                   digits.Substring(digits.Length - scale);
        }
        else
        {
            body = "0." + new string('0', scale - digits.Length) + digits;
        }

        return negative ? "-" + body : body;
    }

    // ---- the shapes the corpus actually contains -----------------------------------------

    [Theory]
    [InlineData(2, 1, DecimalStorageType.I8)]
    [InlineData(4, 2, DecimalStorageType.I16)]
    [InlineData(9, 2, DecimalStorageType.I32)]
    [InlineData(18, 4, DecimalStorageType.I64)]
    [InlineData(38, 10, DecimalStorageType.I128)]
    [InlineData(40, 10, DecimalStorageType.I256)]
    public void CorpusShapesReportTheRightStorage(int precision, int scale, DecimalStorageType expected)
    {
        VortexDecimal value = VortexDecimal.FromInt64(1, (byte)precision, (sbyte)scale);
        Assert.Equal(expected, value.Storage);
        Assert.Equal((byte)precision, value.Precision);
        Assert.Equal((sbyte)scale, value.Scale);
    }

    [Fact]
    public void CorpusDecimalTwoOneValuesRender()
    {
        // types/decimal2_1_nonnull_r1025.jsonl rows: 0, -99, 99, -36, 37, -38 at scale 1.
        Assert.Equal("0.0", VortexDecimal.FromInt64(0, 2, 1).ToString());
        Assert.Equal("-9.9", VortexDecimal.FromInt64(-99, 2, 1).ToString());
        Assert.Equal("9.9", VortexDecimal.FromInt64(99, 2, 1).ToString());
        Assert.Equal("-3.6", VortexDecimal.FromInt64(-36, 2, 1).ToString());
        Assert.Equal("3.7", VortexDecimal.FromInt64(37, 2, 1).ToString());
    }

    [Fact]
    public void CorpusDecimalThirtyEightTenValuesRender()
    {
        // types/decimal38_10_nonnull_r1025.jsonl, i128 storage.
        VortexDecimal max = VortexDecimal.FromInt128(
            Int128.Parse("99999999999999999999999999999999999999", CultureInfo.InvariantCulture),
            38, 10);
        Assert.Equal("9999999999999999999999999999.9999999999", max.ToString());
        Assert.Equal(DecimalStorageType.I128, max.Storage);

        VortexDecimal min = VortexDecimal.FromInt128(
            Int128.Parse("-99999999999999999999999999999999999999", CultureInfo.InvariantCulture),
            38, 10);
        Assert.Equal("-9999999999999999999999999999.9999999999", min.ToString());
    }

    // ---- System.Decimal conversion --------------------------------------------------------

    [Fact]
    public void ToDecimalAppliesTheScaleExactly()
    {
        Assert.Equal(123.45m, VortexDecimal.FromInt64(12345, 10, 2).ToDecimal());
        Assert.Equal(-123.45m, VortexDecimal.FromInt64(-12345, 10, 2).ToDecimal());
        Assert.Equal(12300m, VortexDecimal.FromInt64(123, 10, -2).ToDecimal());
        Assert.Equal(0.005m, VortexDecimal.FromInt64(5, 10, 3).ToDecimal());

        // The scale is preserved, not normalised: 0.005 has scale 3.
        Assert.Equal(3, decimal.GetBits(VortexDecimal.FromInt64(5, 10, 3).ToDecimal())[3] >> 16 & 0xFF);
    }

    [Fact]
    public void ScaleBeyondTwentyEightIsRejectedBeforeTheMagnitude()
    {
        // Even zero, which is trivially representable, is rejected: the scale is out of decimal's
        // domain, and the contract requires the check to come first.
        Assert.False(VortexDecimal.FromInt64(0, 76, 29).TryToDecimal(out decimal high));
        Assert.Equal(0m, high);
        Assert.False(VortexDecimal.FromInt64(0, 76, -29).TryToDecimal(out _));
        Assert.True(VortexDecimal.FromInt64(0, 76, 28).TryToDecimal(out _));
        Assert.True(VortexDecimal.FromInt64(0, 76, -28).TryToDecimal(out _));

        Assert.Throws<OverflowException>(
            () => { _ = VortexDecimal.FromInt64(1, 76, 29).ToDecimal(); });
    }

    [Fact]
    public void MagnitudeBeyondNinetySixBitsIsRejected()
    {
        Int256 maxUnscaled = Int256Tests.FromBig(new BigInteger(decimal.MaxValue));
        Assert.True(new VortexDecimal(maxUnscaled, 76, 0).TryToDecimal(out decimal max));
        Assert.Equal(decimal.MaxValue, max);

        Int256 justOver = Int256Tests.FromBig(new BigInteger(decimal.MaxValue) + 1);
        Assert.False(new VortexDecimal(justOver, 76, 0).TryToDecimal(out _));
        Assert.Throws<OverflowException>(
            () => { _ = new VortexDecimal(justOver, 76, 0).ToDecimal(); });

        Assert.False(new VortexDecimal(Int256.MinValue, 76, 0).TryToDecimal(out _));
    }

    [Fact]
    public void NegativeScaleMultipliesAndCanOverflowDecimal()
    {
        // 10^28 fits; 10^28 * 10 does not.
        Int256 one = Int256.One;
        Assert.True(new VortexDecimal(one, 76, -28).TryToDecimal(out decimal value));
        Assert.Equal(BigInteger.Pow(10, 28), new BigInteger(value));

        Int256 tenPow69 = Int256Tests.FromBig(BigInteger.Pow(10, 69));
        Assert.False(new VortexDecimal(tenPow69, 76, -28).TryToDecimal(out _));

        // decimal.MaxValue / 10 scaled by -1 still fits, one more digit does not.
        Int256 nearMax = Int256Tests.FromBig(new BigInteger(decimal.MaxValue) / 10);
        Assert.True(new VortexDecimal(nearMax, 76, -1).TryToDecimal(out decimal scaled));
        Assert.Equal(new BigInteger(decimal.MaxValue) / 10 * 10, new BigInteger(scaled));

        Int256 overMax = Int256Tests.FromBig(new BigInteger(decimal.MaxValue) / 10 + 1);
        Assert.False(new VortexDecimal(overMax, 76, -1).TryToDecimal(out _));
    }

    [Fact]
    public void TryToDecimalMatchesTheOracleOnRandomValues()
    {
        Random random = new Random(0x00_DE_C1_A2);
        byte[] bytes = new byte[Int256.ByteCount];
        for (int iteration = 0; iteration < 4_000; iteration++)
        {
            random.NextBytes(bytes);
            int keep = 1 + (iteration % 14);
            byte fill = (bytes[keep - 1] & 0x80) != 0 ? (byte)0xFF : (byte)0x00;
            for (int i = keep; i < bytes.Length; i++)
            {
                bytes[i] = fill;
            }

            Int256 unscaled = Int256.FromLittleEndianBytes(bytes);
            sbyte scale = (sbyte)random.Next(-30, 31);
            VortexDecimal value = new VortexDecimal(unscaled, 76, scale);

            BigInteger oracle = Int256Tests.ToBig(unscaled);
            bool expected = scale >= -28 && scale <= 28;
            if (expected)
            {
                BigInteger magnitude = BigInteger.Abs(oracle);
                if (scale < 0)
                {
                    magnitude *= BigInteger.Pow(10, -scale);
                }

                expected = magnitude <= new BigInteger(decimal.MaxValue);
            }

            Assert.Equal(expected, value.TryToDecimal(out decimal actual));
            if (expected)
            {
                BigInteger scaledOracle = oracle;
                if (scale < 0)
                {
                    scaledOracle *= BigInteger.Pow(10, -scale);
                }

                Assert.Equal(scaledOracle, new BigInteger(actual * Pow10(scale)));
            }
        }
    }

    private static decimal Pow10(int scale)
    {
        decimal result = 1m;
        for (int i = 0; i < scale; i++)
        {
            result *= 10m;
        }

        return result;
    }

    [Fact]
    public void TryToInt128NarrowsTheUnscaledValue()
    {
        Assert.True(VortexDecimal.FromInt128(Int128.MaxValue, 76, 5).TryToInt128(out Int128 max));
        Assert.Equal(Int128.MaxValue, max);
        Assert.False(new VortexDecimal(Int256.MaxValue, 76, 5).TryToInt128(out Int128 over));
        Assert.Equal(Int128.Zero, over);
    }

    // ---- construction guard ---------------------------------------------------------------

    [Theory]
    [InlineData(0)]
    [InlineData(77)]
    [InlineData(255)]
    public void OutOfRangePrecisionIsRejectedAtConstruction(int precision)
    {
        Assert.Throws<VortexFormatException>(
            () => { _ = new VortexDecimal(Int256.One, (byte)precision, 0); });
        Assert.Throws<VortexFormatException>(
            () => { _ = VortexDecimal.FromInt64(1, (byte)precision, 0); });
        Assert.Throws<VortexFormatException>(
            () => { _ = VortexDecimal.FromInt128(Int128.One, (byte)precision, 0); });
    }

    [Fact]
    public void DefaultValueDoesNotSilentlyReportAStorageWidth()
    {
        VortexDecimal zero = default;
        Assert.Equal((byte)0, zero.Precision);
        Assert.Throws<VortexFormatException>(() => { _ = zero.Storage; });
    }

    // ---- equality and ordering ------------------------------------------------------------

    [Fact]
    public void EqualsIsStructuralNotNumeric()
    {
        VortexDecimal a = VortexDecimal.FromInt64(100, 4, 2);   // 1.00
        VortexDecimal b = VortexDecimal.FromInt64(10, 4, 1);    // 1.0
        Assert.False(a.Equals(b));
        Assert.Equal(0, a.CompareTo(b));

        VortexDecimal c = VortexDecimal.FromInt64(100, 4, 2);
        Assert.True(a.Equals(c));
        Assert.True(a.Equals((object)c));
        Assert.False(a.Equals((object)b));
        Assert.False(a.Equals(null));
        Assert.Equal(a.GetHashCode(), c.GetHashCode());

        // Precision participates too.
        VortexDecimal d = VortexDecimal.FromInt64(100, 9, 2);
        Assert.False(a.Equals(d));
        Assert.Equal(0, a.CompareTo(d));
    }

    [Fact]
    public void CompareToIsNumericAcrossScales()
    {
        Assert.True(VortexDecimal.FromInt64(1, 10, 0).CompareTo(VortexDecimal.FromInt64(10, 10, 1)) == 0);
        Assert.True(VortexDecimal.FromInt64(1, 10, 0).CompareTo(VortexDecimal.FromInt64(11, 10, 1)) < 0);
        Assert.True(VortexDecimal.FromInt64(2, 10, 0).CompareTo(VortexDecimal.FromInt64(11, 10, 1)) > 0);

        // Negatives invert the magnitude order.
        Assert.True(VortexDecimal.FromInt64(-1, 10, 0).CompareTo(VortexDecimal.FromInt64(-10, 10, 1)) == 0);
        Assert.True(VortexDecimal.FromInt64(-1, 10, 0).CompareTo(VortexDecimal.FromInt64(-11, 10, 1)) > 0);
        Assert.True(VortexDecimal.FromInt64(-2, 10, 0).CompareTo(VortexDecimal.FromInt64(-11, 10, 1)) < 0);

        // Across the sign boundary and against zero.
        Assert.True(VortexDecimal.FromInt64(-1, 10, 5).CompareTo(VortexDecimal.FromInt64(1, 10, 40)) < 0);
        Assert.True(VortexDecimal.FromInt64(0, 10, 0).CompareTo(VortexDecimal.FromInt64(0, 10, 20)) == 0);
        Assert.True(VortexDecimal.FromInt64(0, 10, -5).CompareTo(VortexDecimal.FromInt64(-1, 10, 76)) > 0);

        // Negative scale on one side only.
        Assert.True(VortexDecimal.FromInt64(1, 10, -3).CompareTo(VortexDecimal.FromInt64(1000, 10, 0)) == 0);
        Assert.True(VortexDecimal.FromInt64(1, 10, -3).CompareTo(VortexDecimal.FromInt64(999, 10, 0)) > 0);
    }

    [Fact]
    public void CompareToNeverThrowsWhenScalingWouldOverflow()
    {
        // Scaling MaxValue up by 10^127 cannot fit 256 bits; the answer is still exact, because a
        // value that overflows the scaling is necessarily the larger magnitude.
        VortexDecimal huge = new VortexDecimal(Int256.MaxValue, 76, -127);
        VortexDecimal small = VortexDecimal.FromInt64(1, 10, 0);
        Assert.True(huge.CompareTo(small) > 0);
        Assert.True(small.CompareTo(huge) < 0);

        VortexDecimal hugeNegative = new VortexDecimal(Int256.MinValue, 76, -127);
        Assert.True(hugeNegative.CompareTo(small) < 0);
        Assert.True(small.CompareTo(hugeNegative) > 0);
        Assert.True(hugeNegative.CompareTo(huge) < 0);
    }

    [Fact]
    public void CompareToMatchesTheOracleOnRandomValues()
    {
        Random random = new Random(0x0C_0A_0F_0E);
        byte[] left = new byte[Int256.ByteCount];
        byte[] right = new byte[Int256.ByteCount];
        for (int iteration = 0; iteration < 4_000; iteration++)
        {
            VortexDecimal a = NextDecimal(random, left);
            VortexDecimal b = NextDecimal(random, right);

            BigInteger scaledA = Scaled(a);
            BigInteger scaledB = Scaled(b);
            int expected = BigInteger.Compare(scaledA, scaledB);
            Assert.Equal(Math.Sign(expected), Math.Sign(a.CompareTo(b)));
            Assert.Equal(Math.Sign(-expected), Math.Sign(b.CompareTo(a)));
        }
    }

    private static VortexDecimal NextDecimal(Random random, byte[] bytes)
    {
        random.NextBytes(bytes);
        // Keep magnitudes modest so the shared-scale oracle stays cheap, but include big ones.
        int keep = 1 + random.Next(Int256.ByteCount);
        byte fill = (bytes[keep - 1] & 0x80) != 0 ? (byte)0xFF : (byte)0x00;
        for (int i = keep; i < bytes.Length; i++)
        {
            bytes[i] = fill;
        }

        return new VortexDecimal(
            Int256.FromLittleEndianBytes(bytes), 76, (sbyte)random.Next(-20, 21));
    }

    /// <summary>The value at a common scale of +20, so both operands are exact integers.</summary>
    private static BigInteger Scaled(VortexDecimal value) =>
        Int256Tests.ToBig(value.Unscaled) * BigInteger.Pow(10, 20 - value.Scale);
}
