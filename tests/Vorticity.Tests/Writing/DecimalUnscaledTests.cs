// A decimal written to a column of another scale: its unscaled value at the column's scale, exact or
// refused. Held against the definition, computed in BigInteger: the value times ten to the scale,
// an integer under ten to the precision. One division by the power between the scales and one
// multiplication replace a division by ten a digit, so every distance between scales is covered,
// down and up, a negative scale and a distance past any power a decimal's 96 bits reach included.
using System;
using System.Numerics;
using Vorticity.Types.Numerics;
using Vorticity.Writing;
using Xunit;

namespace Vorticity.Tests.Writing;

public sealed class DecimalUnscaledTests
{
    private static readonly int[] Precisions = [1, 2, 9, 18, 19, 28, 38];

    private static readonly int[] Scales = [-30, -3, 0, 1, 2, 4, 9, 10, 18, 19, 27, 28, 38];

    [Fact]
    public void ADecimalIsItsValueAtTheColumnsScaleOrRefused()
    {
        Random random = new Random(20261105);
        for (int i = 0; i < 4_000; i++)
        {
            decimal value = RandomDecimal(random);
            foreach (int precision in Precisions)
            {
                foreach (int scale in Scales)
                {
                    if (scale <= precision)
                    {
                        Check(value, scale, precision);
                    }
                }
            }
        }
    }

    [Theory]
    [InlineData("0", 4, 1)]
    [InlineData("3", 4, 1)]
    [InlineData("-0.000", 0, 1)]
    [InlineData("12.5", 4, 18)]
    [InlineData("12.5000", 1, 18)]
    [InlineData("12.5001", 1, 18)]
    [InlineData("9999.99", 2, 6)]
    [InlineData("9999.99", 3, 6)]
    [InlineData("79228162514264337593543950335", 0, 29)]
    [InlineData("79228162514264337593543950335", 9, 38)]
    [InlineData("79228162514264337593543950335", 10, 38)]
    [InlineData("-7.9228162514264337593543950335", 28, 29)]
    [InlineData("0.0000000000000000000000000001", 0, 1)]
    [InlineData("0.0000000000000000000000000001", 28, 1)]
    [InlineData("100", -2, 1)]
    [InlineData("150", -2, 2)]
    public void TheEdgesOfTheScalesAndOfThePrecision(string text, int scale, int precision) =>
        Check(decimal.Parse(text, System.Globalization.CultureInfo.InvariantCulture), scale, precision);

    [Fact]
    public void AWideDecimalIsItsValueAtTheColumnsScaleOrRefused()
    {
        Random random = new Random(1105);
        for (int i = 0; i < 2_000; i++)
        {
            int digits = random.Next(1, 60);
            BigInteger unscaled = RandomInteger(random, digits) * Pow10(random.Next(0, 5));
            if (random.Next(2) == 0)
            {
                unscaled = -unscaled;
            }

            int precision = Math.Min(76, digits + 5);
            sbyte from = (sbyte)random.Next(-5, Math.Min(precision, 60) + 1);
            VortexDecimal value = new VortexDecimal(ToInt256(unscaled), (byte)precision, from);
            foreach (int target in new[] { 76, 60, 40, 39, 38, 20, 19, 1 })
            {
                foreach (int scale in new[] { -10, 0, 3, 19, 20, 38, 39, 57, 76 })
                {
                    if (scale <= target)
                    {
                        CheckWide(value, unscaled, from, scale, target);
                    }
                }
            }
        }
    }

    private static void Check(decimal value, int scale, int precision)
    {
        int[] bits = decimal.GetBits(value);
        BigInteger mantissa = ((BigInteger)(uint)bits[2] << 64) | ((BigInteger)(uint)bits[1] << 32) | (uint)bits[0];
        BigInteger signed = bits[3] < 0 ? -mantissa : mantissa;
        int from = (bits[3] >> 16) & 0xFF;
        (BigInteger? expected, string? refusal) = Expected(signed, from, scale, precision);
        if (expected is { } exact)
        {
            Assert.Equal(exact, (BigInteger)BuilderValues.Unscaled(value, scale, precision));
        }
        else
        {
            VortexSchemaException thrown = Assert.Throws<VortexSchemaException>(() => BuilderValues.Unscaled(value, scale, precision));
            Assert.Contains(refusal!, thrown.Message, StringComparison.Ordinal);
        }
    }

    private static void CheckWide(VortexDecimal value, BigInteger signed, int from, int scale, int precision)
    {
        // Ten to the 76th is under 2^256, so a value within the precision fits the 256 bits it is built in.
        (BigInteger? expected, string? refusal) = Expected(signed, from, scale, precision);
        if (expected is { } exact)
        {
            Assert.Equal(exact, ToBigInteger(BuilderValues.Unscaled(value, scale, precision)));
        }
        else
        {
            VortexSchemaException thrown = Assert.Throws<VortexSchemaException>(() => BuilderValues.Unscaled(value, scale, precision));
            Assert.Contains(refusal!, thrown.Message, StringComparison.Ordinal);
        }
    }

    /// <summary>The value at <paramref name="scale"/>, or the reason it is refused: digits lost first, then too many digits.</summary>
    private static (BigInteger? Value, string? Refusal) Expected(BigInteger signed, int from, int scale, int precision)
    {
        BigInteger at;
        if (scale >= from)
        {
            at = signed * Pow10(scale - from);
        }
        else
        {
            at = BigInteger.DivRem(signed, Pow10(from - scale), out BigInteger remainder);
            if (!remainder.IsZero)
            {
                return (null, "more fractional digits");
            }
        }

        return BigInteger.Abs(at) < Pow10(precision) ? (at, null) : (null, "does not fit");
    }

    private static decimal RandomDecimal(Random random)
    {
        // Values of every size and scale, trailing zeros included, which a scale down drops exactly.
        int lo = random.Next(), mid = random.Next(4) switch { 0 => 0, 1 => random.Next(16), _ => random.Next() }, hi = random.Next(3) switch { 0 => 0, 1 => random.Next(4), _ => random.Next() };
        decimal value = new decimal(lo, mid, hi, random.Next(2) == 0, (byte)random.Next(0, 29));
        int zeros = random.Next(0, 6);
        for (int i = 0; i < zeros && value.Scale < 28; i++)
        {
            try
            {
                value *= 1.0m;
            }
            catch (OverflowException)
            {
                break;
            }
        }

        return value;
    }

    private static BigInteger RandomInteger(Random random, int digits)
    {
        BigInteger value = BigInteger.Zero;
        for (int i = 0; i < digits; i++)
        {
            value = (value * 10) + random.Next(10);
        }

        return value;
    }

    private static BigInteger Pow10(int exponent) => BigInteger.Pow(10, exponent);

    private static Int256 ToInt256(BigInteger value)
    {
        Span<byte> bytes = stackalloc byte[Int256.ByteCount];
        bytes.Fill(value.Sign < 0 ? (byte)0xFF : (byte)0);
        Assert.True(value.TryWriteBytes(bytes, out _, isUnsigned: false, isBigEndian: false));
        return Int256.FromLittleEndianBytes(bytes);
    }

    private static BigInteger ToBigInteger(Int256 value)
    {
        Span<byte> bytes = stackalloc byte[Int256.ByteCount];
        value.WriteLittleEndianBytes(bytes);
        return new BigInteger(bytes, isUnsigned: false, isBigEndian: false);
    }
}
