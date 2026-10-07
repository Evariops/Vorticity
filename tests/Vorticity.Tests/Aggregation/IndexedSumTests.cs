using System;
using System.Linq;
using System.Numerics;
using Vorticity.Aggregating;
using Xunit;

namespace Vorticity.Tests.Aggregation;

/// <summary>
/// The indexed sum against the exact sum: the same bits in every order, under every cut, by every
/// path a value takes in, and those of the exact total rounded once wherever every value's bits
/// lie within its bins.
/// </summary>
public sealed class IndexedSumTests
{
    // The rounding in two words, which a sum's value takes, against the rounding of an Int128: the same
    // bits for totals of every length, both signs, ties to even, and exponents down past the subnormals.
    [Fact]
    public void TheRoundingInWordsIsTheRoundingOf128Bits()
    {
        Random random = new Random(20261007);
        for (int i = 0; i < 200_000; i++)
        {
            int bits = random.Next(1, 119);
            UInt128 magnitude = ((UInt128)(ulong)random.NextInt64() << 64 | (ulong)random.NextInt64()) >> (128 - bits);
            if (i % 7 == 0)
            {
                // A tie: the bit below the kept ones set alone, which rounds to the even.
                int drop = Math.Max(1, bits - 53);
                magnitude = (magnitude >> drop << drop) | ((UInt128)1 << (drop - 1));
            }

            Int128 total = random.Next(2) == 0 ? (Int128)magnitude : -(Int128)magnitude;
            int exponent = random.Next(-1200, 900);
            double expected = IndexedSum.Round(total, exponent);
            double actual = IndexedSum.Round((ulong)(total >> 64), (ulong)total, exponent);
            Assert.True(
                BitConverter.DoubleToInt64Bits(expected) == BitConverter.DoubleToInt64Bits(actual),
                $"{total} units of 2^{exponent}: {expected:R} rounded in 128 bits, {actual:R} in words");
        }
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(2, 12)]
    [InlineData(3, 40)]
    [InlineData(4, 600)]
    public void TheSumIsTheExactTotalRoundedOnce(int seed, int spread)
    {
        double[] values = Values(seed, 50_000, spread);
        IndexedSum sum = default;
        foreach (double value in values)
        {
            sum.Add(value);
        }

        Assert.Equal(values.Length, sum.Count);
        double exact = Exact(values);
        if (spread <= 40)
        {
            // The bits the bins leave out, 53 below the largest value and more, lie far below the
            // total's last bit: the rounding is the exact total's.
            Assert.Equal(BitConverter.DoubleToInt64Bits(exact), BitConverter.DoubleToInt64Bits(sum.Value));
        }
        else
        {
            Assert.Equal(exact, sum.Value, Math.Abs(exact) * 1e-15);
        }
    }

    [Theory]
    [InlineData(5, 0)]
    [InlineData(6, 30)]
    [InlineData(7, 900)]
    public void TheBitsDoNotDependOnTheOrderTheCutOrThePath(int seed, int spread)
    {
        double[] values = Values(seed, 40_000, spread);
        IndexedSum whole = default;
        foreach (double value in values)
        {
            whole.Add(value);
        }

        long expected = BitConverter.DoubleToInt64Bits(whole.Value);
        Random random = new Random(seed);
        for (int round = 0; round < 6; round++)
        {
            double[] shuffled = (double[])values.Clone();
            random.Shuffle(shuffled);

            // Cut into ranges summed apart, by spans or one value at a time, merged in any order.
            int parts = random.Next(2, 40);
            IndexedSum[] partial = new IndexedSum[parts];
            for (int p = 0; p < parts; p++)
            {
                int start = (int)((long)shuffled.Length * p / parts);
                int end = (int)((long)shuffled.Length * (p + 1) / parts);
                if (p % 2 == 0)
                {
                    partial[p].AddSpan(shuffled.AsSpan(start, end - start));
                }
                else
                {
                    for (int i = start; i < end; i++)
                    {
                        partial[p].Add(shuffled[i]);
                    }
                }
            }

            IndexedSum merged = default;
            foreach (int p in Enumerable.Range(0, parts).OrderBy(_ => random.Next()))
            {
                merged.Merge(in partial[p]);
            }

            Assert.Equal(expected, BitConverter.DoubleToInt64Bits(merged.Value));
            Assert.Equal(values.Length, merged.Count);
        }

        // A value repeated is a weighted value.
        IndexedSum repeated = default;
        IndexedSum weighted = default;
        foreach (double value in values.Take(500))
        {
            for (int i = 0; i < 7; i++)
            {
                repeated.Add(value);
            }

            weighted.AddWeighted(value, 7);
        }

        Assert.Equal(BitConverter.DoubleToInt64Bits(repeated.Value), BitConverter.DoubleToInt64Bits(weighted.Value));
        Assert.Equal(repeated.Count, weighted.Count);
    }

    [Fact]
    public void ANaNIsSkippedAndAnInfinityMarksTheSum()
    {
        IndexedSum sum = default;
        sum.AddSpan([1.5, double.NaN, 2.5]);
        sum.Add(double.NaN);
        Assert.Equal(4.0, sum.Value);
        Assert.Equal(2, sum.Count);

        sum.Add(double.PositiveInfinity);
        Assert.Equal(double.PositiveInfinity, sum.Value);
        Assert.Equal(3, sum.Count);

        IndexedSum negative = default;
        negative.AddWeighted(double.NegativeInfinity, 3);
        Assert.Equal(double.NegativeInfinity, negative.Value);
        sum.Merge(in negative);
        Assert.True(double.IsNaN(sum.Value));
        Assert.Equal(6, sum.Count);

        IndexedSum none = default;
        Assert.Equal(0.0, none.Value);
        none.Merge(in none);
        Assert.Equal(0, none.Count);
    }

    [Fact]
    public void TheExtremesOfTheDoublesAreSummedExactly()
    {
        // Subnormals, whose bits reach 2^-1074, the lowest bin's unit.
        double[] tiny = [double.Epsilon, 3 * double.Epsilon, -double.Epsilon, 1e-310, 2.2250738585072014e-308];
        Assert.Equal(Exact(tiny), Sum(tiny));

        // Near the largest double, split scaled down: exact while the total fits, infinite past it.
        double[] large = [1e308, 7e307, -3.5e307, 1.25e300];
        Assert.Equal(Exact(large), Sum(large));
        Assert.Equal(double.PositiveInfinity, Sum([double.MaxValue, double.MaxValue]));
        Assert.Equal(double.MaxValue, Sum([double.MaxValue, 1.0]));
        Assert.Equal(0.0, Sum([double.MaxValue, -double.MaxValue]));

        // The large and the small together: the small fall below the bins, as they would below a double.
        Assert.Equal(1e300, Sum([1e300, 1e-300, -1e-300, 1.0]));
    }

    [Fact]
    public void ASumPastItsValuesThrowsRatherThanLoseItsExactness()
    {
        // 2^36 values, each the largest part a bin takes: still exact.
        double largest = 0.5 - Math.ScaleB(1.0, 26);
        IndexedSum full = default;
        full.AddWeighted(largest, 1L << 36);
        Assert.Equal(largest * Math.ScaleB(1.0, 36), full.Value);

        IndexedSum one = full;
        Assert.Throws<OverflowException>(() => one.Add(1.0));
        IndexedSum other = default;
        other.Add(1.0);
        Assert.Throws<OverflowException>(() => other.Merge(in full));
    }

    [Fact]
    public void ATotalIsRoundedToTheNearestDoubleTiesToEven()
    {
        Random random = new Random(19);
        for (int i = 0; i < 20_000; i++)
        {
            Int128 total = (Int128)random.NextInt64() << random.Next(0, 60);
            total += random.NextInt64(-1000, 1000);
            if (random.Next(2) == 0)
            {
                total = -total;
            }

            int exponent = random.Next(-1200, 900);
            Assert.Equal(Reference((BigInteger)total, exponent), IndexedSum.Round(total, exponent));
        }

        // Ties: halfway between two doubles goes to the even one.
        Assert.Equal(Math.ScaleB(1.0, 53), IndexedSum.Round((Int128.One << 53) + 1, 0));
        Assert.Equal(Math.ScaleB(1.0, 53) + 4, IndexedSum.Round((Int128.One << 53) + 3, 0));
        Assert.Equal(2 * double.Epsilon, IndexedSum.Round(3, -1075));
        Assert.Equal(double.Epsilon, IndexedSum.Round(3, -1076));
        Assert.Equal(0.0, IndexedSum.Round(1, -1075));
    }

    private static double Sum(double[] values)
    {
        IndexedSum sum = default;
        foreach (double value in values)
        {
            sum.Add(value);
        }

        return sum.Value;
    }

    /// <summary>Values of mixed signs, their magnitudes spread over <paramref name="spread"/> binades, a few cancelling.</summary>
    private static double[] Values(int seed, int count, int spread)
    {
        Random random = new Random(seed);
        double[] values = new double[count];
        for (int i = 0; i < count; i++)
        {
            double value = Math.ScaleB(random.NextDouble() + 0.5, random.Next(-spread / 2, (spread / 2) + 1)) * (random.Next(4) == 0 ? -1 : 1);
            values[i] = value;
        }

        for (int i = 0; i < count / 20; i++)
        {
            values[random.Next(count)] = -values[random.Next(count)];
        }

        return values;
    }

    /// <summary>The exact sum of finite values, rounded once to the nearest double.</summary>
    private static double Exact(double[] values)
    {
        BigInteger total = BigInteger.Zero;
        foreach (double value in values)
        {
            long bits = BitConverter.DoubleToInt64Bits(value);
            int biased = (int)((bits >> 52) & 0x7FF);
            long mantissa = bits & 0xF_FFFF_FFFF_FFFF;
            if (biased != 0)
            {
                mantissa |= 1L << 52;
            }

            BigInteger scaled = new BigInteger(mantissa) << Math.Max(biased - 1, 0);
            total += bits < 0 ? -scaled : scaled;
        }

        return Reference(total, -1074);
    }

    /// <summary><paramref name="total"/> times 2^<paramref name="exponent"/>, rounded to the nearest double, ties to even.</summary>
    private static double Reference(BigInteger total, int exponent)
    {
        if (total.IsZero)
        {
            return 0;
        }

        int sign = total.Sign;
        BigInteger magnitude = BigInteger.Abs(total);
        int length = (int)magnitude.GetBitLength();
        int lowest = Math.Max(length - 1 + exponent - 52, -1074);
        int drop = lowest - exponent;
        if (drop > 0)
        {
            BigInteger kept = magnitude >> drop;
            BigInteger rest = magnitude - (kept << drop);
            BigInteger half = BigInteger.One << (drop - 1);
            if (rest > half || (rest == half && !kept.IsEven))
            {
                kept++;
            }

            magnitude = kept;
            exponent += drop;
        }

        double result = Math.ScaleB((double)magnitude, exponent);
        return sign < 0 ? -result : result;
    }
}
