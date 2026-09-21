// Exact decimal rendering of a 256-bit two's-complement value, checked against BigInteger.
// BigInteger is banned in src/ (the library takes no dependencies) and is exactly the
// right oracle here, because it is an independent implementation of the same function.
using System;
using System.Globalization;
using System.Numerics;
using Vorticity.Types.Numerics;
using Xunit;

namespace Vorticity.Tests.Types.Numerics;

public sealed class Int256FormattingTests
{
    [Fact]
    public void MinValueRendersExactlyAndNeverNegates()
    {
        string text = Int256.MinValue.ToString();
        Assert.Equal(Int256Tests.MinValueText, text);
        Assert.Equal(78, text.Length);
        Assert.Equal(Int256.MaxFormattedLength, text.Length);
    }

    [Fact]
    public void MaxValueRendersExactly()
    {
        string text = Int256.MaxValue.ToString();
        Assert.Equal(Int256Tests.MaxValueText, text);
        Assert.Equal(77, text.Length);
        Assert.Equal(Int256.MaxDigitCount, text.Length);
    }

    [Theory]
    [InlineData(0L, "0")]
    [InlineData(1L, "1")]
    [InlineData(-1L, "-1")]
    [InlineData(9L, "9")]
    [InlineData(10L, "10")]
    [InlineData(-10L, "-10")]
    [InlineData(long.MaxValue, "9223372036854775807")]
    [InlineData(long.MinValue, "-9223372036854775808")]
    public void SmallValuesRender(long value, string expected) =>
        Assert.Equal(expected, new Int256(value).ToString());

    [Fact]
    public void ChunkBoundariesRender()
    {
        // The renderer divides by 10^19; the interesting values are around that radix, where a
        // missing zero-pad on a non-final chunk would show up.
        (BigInteger Value, string Text)[] cases =
        {
            (BigInteger.Pow(10, 19) - 1, "9999999999999999999"),
            (BigInteger.Pow(10, 19), "10000000000000000000"),
            (BigInteger.Pow(10, 19) + 1, "10000000000000000001"),
            (BigInteger.Pow(10, 38), "1" + new string('0', 38)),
            (BigInteger.Pow(10, 38) + 1, "1" + new string('0', 37) + "1"),
            (BigInteger.Pow(10, 19) * BigInteger.Pow(10, 19) + 5, "1" + new string('0', 37) + "5"),
            (BigInteger.Pow(2, 64), "18446744073709551616"),
            (BigInteger.Pow(2, 128), "340282366920938463463374607431768211456"),
            (BigInteger.Pow(2, 192), "6277101735386680763835789423207666416102355444464034512896"),
        };

        foreach ((BigInteger value, string text) in cases)
        {
            Int256 positive = Int256Tests.FromBig(value);
            Assert.Equal(text, positive.ToString());
            Assert.Equal("-" + text, Int256Tests.FromBig(-value).ToString());
        }
    }

    [Fact]
    public void TryFormatRejectsAShortDestinationWithoutWriting()
    {
        char[] destination = new char[77];
        Assert.False(Int256.MinValue.TryFormat(destination, out int written));
        Assert.Equal(0, written);
        Assert.Equal('\0', destination[0]);

        char[] exact = new char[78];
        Assert.True(Int256.MinValue.TryFormat(exact, out written));
        Assert.Equal(78, written);
        Assert.Equal(Int256Tests.MinValueText, new string(exact, 0, written));
    }

    [Fact]
    public void TryFormatOfZeroNeedsOneChar()
    {
        char[] one = new char[1];
        Assert.True(Int256.Zero.TryFormat(one, out int written));
        Assert.Equal(1, written);
        Assert.Equal("0", new string(one, 0, written));

        Assert.False(Int256.Zero.TryFormat(Array.Empty<char>(), out written));
        Assert.Equal(0, written);
    }

    [Fact]
    public void TryFormatWritesNothingPastCharsWritten()
    {
        char[] destination = new char[80];
        Array.Fill(destination, '#');
        Assert.True(new Int256(-12345L).TryFormat(destination, out int written));
        Assert.Equal(6, written);
        Assert.Equal("-12345", new string(destination, 0, written));
        Assert.Equal('#', destination[written]);
    }

    [Fact]
    public void RenderingMatchesBigIntegerOnStructuredPatterns()
    {
        foreach (Int256 value in StructuredValues())
        {
            Assert.Equal(
                Int256Tests.ToBig(value).ToString(CultureInfo.InvariantCulture),
                value.ToString());
        }
    }

    [Fact]
    public void RenderingMatchesBigIntegerOnRandomBitPatterns()
    {
        // Deterministic: a fixed seed, so a failure reproduces.
        Random random = new Random(0x5EED_1234);
        byte[] bytes = new byte[Int256.ByteCount];
        Span<byte> le = stackalloc byte[Int256.ByteCount];
        Span<byte> be = stackalloc byte[Int256.ByteCount];
        for (int iteration = 0; iteration < 20_000; iteration++)
        {
            random.NextBytes(bytes);

            // Bias towards short magnitudes half the time so small values are covered too.
            if ((iteration & 1) == 0)
            {
                int keep = 1 + (iteration % Int256.ByteCount);
                byte fill = (bytes[keep - 1] & 0x80) != 0 ? (byte)0xFF : (byte)0x00;
                for (int i = keep; i < bytes.Length; i++)
                {
                    bytes[i] = fill;
                }
            }

            Int256 value = Int256.FromLittleEndianBytes(bytes);
            BigInteger oracle = new BigInteger(bytes, isUnsigned: false, isBigEndian: false);

            Assert.Equal(oracle.ToString(CultureInfo.InvariantCulture), value.ToString());
            Assert.Equal(oracle.Sign, value.Sign);
            Assert.Equal(oracle.Sign < 0, value.IsNegative);
            Assert.Equal(oracle.IsZero, value.IsZero);

            // Byte round trip, both orders.
            value.WriteLittleEndianBytes(le);
            Assert.True(le.SequenceEqual(bytes));
            value.WriteBigEndianBytes(be);
            Assert.Equal(value, Int256.FromBigEndianBytes(be));

            // Narrowing agrees with the oracle.
            bool fitsInt128 = oracle >= (BigInteger)Int128.MinValue && oracle <= (BigInteger)Int128.MaxValue;
            Assert.Equal(fitsInt128, value.TryToInt128(out Int128 asInt128));
            if (fitsInt128)
            {
                Assert.Equal(oracle, (BigInteger)asInt128);
            }

            bool fitsInt64 = oracle >= long.MinValue && oracle <= long.MaxValue;
            Assert.Equal(fitsInt64, value.TryToInt64(out long asInt64));
            if (fitsInt64)
            {
                Assert.Equal(oracle, new BigInteger(asInt64));
            }

            bool fitsUInt64 = oracle >= BigInteger.Zero && oracle <= ulong.MaxValue;
            Assert.Equal(fitsUInt64, value.TryToUInt64(out ulong asUInt64));
            if (fitsUInt64)
            {
                Assert.Equal(oracle, new BigInteger(asUInt64));
            }

            bool fitsDecimal = BigInteger.Abs(oracle) <= new BigInteger(decimal.MaxValue);
            Assert.Equal(fitsDecimal, value.TryToDecimal(out decimal asDecimal));
            if (fitsDecimal)
            {
                Assert.Equal(oracle, new BigInteger(asDecimal));
            }
        }
    }

    [Fact]
    public void OrderingMatchesBigInteger()
    {
        Int256[] values = StructuredValues();
        for (int i = 0; i < values.Length; i++)
        {
            for (int j = 0; j < values.Length; j++)
            {
                int expected = BigInteger.Compare(
                    Int256Tests.ToBig(values[i]), Int256Tests.ToBig(values[j]));
                int actual = values[i].CompareTo(values[j]);
                Assert.Equal(Math.Sign(expected), Math.Sign(actual));
                Assert.Equal(expected < 0, values[i] < values[j]);
                Assert.Equal(expected > 0, values[i] > values[j]);
                Assert.Equal(expected <= 0, values[i] <= values[j]);
                Assert.Equal(expected >= 0, values[i] >= values[j]);
                Assert.Equal(expected == 0, values[i] == values[j]);
                Assert.Equal(expected != 0, values[i] != values[j]);
                if (expected == 0)
                {
                    Assert.Equal(values[i].GetHashCode(), values[j].GetHashCode());
                }
            }
        }
    }

    [Fact]
    public void OrderingMatchesBigIntegerOnRandomPairs()
    {
        Random random = new Random(0x0DDE_4EE7);
        byte[] leftBytes = new byte[Int256.ByteCount];
        byte[] rightBytes = new byte[Int256.ByteCount];
        for (int iteration = 0; iteration < 20_000; iteration++)
        {
            random.NextBytes(leftBytes);
            random.NextBytes(rightBytes);

            // Half the pairs share a prefix so the comparison has to reach the low limbs.
            if ((iteration & 1) == 0)
            {
                int shared = iteration % Int256.ByteCount;
                leftBytes.AsSpan(Int256.ByteCount - shared)
                    .CopyTo(rightBytes.AsSpan(Int256.ByteCount - shared));
            }

            Int256 left = Int256.FromLittleEndianBytes(leftBytes);
            Int256 right = Int256.FromLittleEndianBytes(rightBytes);
            int expected = BigInteger.Compare(
                new BigInteger(leftBytes, isUnsigned: false, isBigEndian: false),
                new BigInteger(rightBytes, isUnsigned: false, isBigEndian: false));

            Assert.Equal(Math.Sign(expected), Math.Sign(left.CompareTo(right)));
            Assert.Equal(Math.Sign(-expected), Math.Sign(right.CompareTo(left)));
            Assert.Equal(expected == 0, left.Equals(right));
            Assert.Equal(expected < 0, left < right);
            Assert.Equal(expected > 0, left > right);
        }
    }

    private static Int256[] StructuredValues()
    {
        return new[]
        {
            Int256.MinValue,
            Int256Tests.FromBig((BigInteger)Int128.MinValue - 1),
            new Int256(Int128.MinValue),
            new Int256(long.MinValue),
            new Int256(-1L),
            Int256.Zero,
            Int256.One,
            new Int256(long.MaxValue),
            Int256.FromUInt64(ulong.MaxValue),
            new Int256(Int128.MaxValue),
            Int256Tests.FromBig((BigInteger)Int128.MaxValue + 1),
            // Values that differ only in a middle limb: a CompareTo that compares the limbs in the
            // wrong order gets every one of these backwards.
            Int256.FromLimbs(0, 1, 0, 0),
            Int256.FromLimbs(0, 0, 1, 0),
            Int256.FromLimbs(ulong.MaxValue, 0, 0, 0),
            Int256.FromLimbs(0, ulong.MaxValue, 0, 0),
            Int256.FromLimbs(1, 0, ulong.MaxValue, 0),
            Int256Tests.FromBig(BigInteger.Pow(10, 19)),
            Int256Tests.FromBig(BigInteger.Pow(10, 38)),
            Int256Tests.FromBig(BigInteger.Pow(10, 57)),
            Int256Tests.FromBig(BigInteger.Pow(10, 76)),
            Int256Tests.FromBig(-BigInteger.Pow(10, 76)),
            Int256.MaxValue,
        };
    }
}
