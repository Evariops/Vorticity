using System;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using Vorticity.Aggregating;
using Xunit;

namespace Vorticity.Tests.Aggregation;

/// <summary>
/// The indexed sum, narrow and wide, against the exact sum: the same bits in every order, under every
/// cut, by every path a value takes in, and those of the exact total rounded once wherever every
/// value's bits lie within its bins; the wide one past the narrow one's values, settled epoch after
/// epoch.
/// </summary>
public sealed class IndexedSumTests
{
    // A value whose top part is the largest a bin takes: just below its top's limit, 2^32, its part 2^26
    // units of 2^6.
    private static readonly double Largest = 1 - Math.ScaleB(1.0, 32);

    // The three high parts of a wide sum's high word, below its count of epochs.
    private const long HighParts = (1L << 54) - 1;

    // The top a magnitude calls for, its binade read off its bits, against Math.ILogB's: every binade of
    // the normal doubles at both ends of its mantissas, every bit a subnormal may hold highest, and zero.
    [Fact]
    public void TheTopReadOffTheBitsIsTheOneILogBGives()
    {
        static int ByILogB(double magnitude) =>
            magnitude == 0 ? 2 : Math.Max(2, (Math.ILogB(magnitude) + 1 - (-1074 + 27 - 1) + 27 - 1) / 27);

        Assert.Equal(ByILogB(0), IndexedSum.Needed(0));
        for (long biased = 1; biased < 2047; biased++)
        {
            foreach (long mantissa in (long[])[0, 1, 1L << 51, (1L << 52) - 1])
            {
                double magnitude = BitConverter.Int64BitsToDouble((biased << 52) | mantissa);
                Assert.Equal(ByILogB(magnitude), IndexedSum.Needed(magnitude));
            }
        }

        for (int bit = 0; bit < 52; bit++)
        {
            foreach (long below in (long[])[0, (1L << bit) - 1])
            {
                double subnormal = BitConverter.Int64BitsToDouble((1L << bit) | below);
                Assert.Equal(ByILogB(subnormal), IndexedSum.Needed(subnormal));
            }
        }
    }

    // A value split once and added to sums of every top, those whose top must rise, NaNs, infinities, values past the
    // extractors, zeros and subnormals among them: each sum holds the words adding the value itself gives.
    [Fact]
    public void AValueSplitOnceAddsWhatTheValueAdds()
    {
        Random random = new Random(20261024);
        double[] specials = [0, -0.0, double.NaN, double.PositiveInfinity, double.NegativeInfinity, double.Epsilon, -double.Epsilon,
            double.MaxValue, -double.MaxValue, 1e300, Largest, 1e-310];
        for (int trial = 0; trial < 2_000; trial++)
        {
            // Three sums started at different magnitudes, so that a value meets tops above, at and below its own.
            IndexedSum[] split = new IndexedSum[3];
            IndexedSum[] plain = new IndexedSum[3];
            for (int s = 0; s < 3; s++)
            {
                double seed = Math.ScaleB(random.NextDouble() + 0.5, random.Next(-40, 40) * (s + 1));
                split[s].Add(seed);
                plain[s].Add(seed);
            }

            for (int v = 0; v < 50; v++)
            {
                double value = random.Next(20) == 0
                    ? specials[random.Next(specials.Length)]
                    : (random.NextDouble() - 0.5) * Math.ScaleB(1.0, random.Next(-60, 80));
                IndexedSum.Split parts = IndexedSum.SplitOf(value);
                for (int s = 0; s < 3; s++)
                {
                    split[s].Add(in parts);
                    plain[s].Add(value);
                    Assert.Equal(
                        (plain[s].M0, plain[s].M1, plain[s].M2, plain[s].Meta),
                        (split[s].M0, split[s].M1, split[s].M2, split[s].Meta));
                }
            }
        }
    }

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

    // The narrow and the wide sum of the same values, by every path: the same bits.
    [Theory]
    [InlineData(21, 0)]
    [InlineData(22, 40)]
    [InlineData(23, 900)]
    public void TheWideSumIsTheNarrowOnesBits(int seed, int spread)
    {
        double[] values = Values(seed, 30_000, spread);
        IndexedSum narrow = default;
        narrow.AddSpan(values);
        WideIndexedSum byValue = default;
        foreach (double value in values)
        {
            byValue.Add(value);
        }

        WideIndexedSum bySpan = default;
        bySpan.AddSpan(values);
        WideIndexedSum merged = default;
        for (int start = 0; start < values.Length; start += 7_000)
        {
            WideIndexedSum part = default;
            part.AddSpan(values.AsSpan(start, Math.Min(7_000, values.Length - start)));
            merged.Merge(in part);
        }

        foreach (WideIndexedSum wide in (WideIndexedSum[])[byValue, bySpan, merged])
        {
            Assert.Equal(narrow.Count, wide.Count);
            Assert.Equal(BitConverter.DoubleToInt64Bits(narrow.Value), BitConverter.DoubleToInt64Bits(wide.Value));
        }
    }

    // Weights of 2^30 to 2^40 a value, tens of epochs in all: the bins settle many times over, and the sum
    // is the exact total's bits, whole or cut and merged in any order.
    [Theory]
    [InlineData(31, 0)]
    [InlineData(32, 12)]
    [InlineData(33, 40)]
    public void ASumPastItsEpochsIsItsExactTotalInEveryOrderOfMerges(int seed, int spread)
    {
        Random random = new Random(seed);
        double[] values = Values(seed, 40, spread);
        values[0] = Largest;
        values[1] = -Largest / 3;
        long[] weights = Array.ConvertAll(values, _ => random.NextInt64(1L << 30, 1L << 40));
        WideIndexedSum whole = default;
        for (int i = 0; i < values.Length; i++)
        {
            whole.AddWeighted(values[i], weights[i]);
        }

        Assert.Equal(weights.Sum(), whole.Count);
        Assert.NotEqual(0, whole.High & HighParts);
        Assert.Equal(BitConverter.DoubleToInt64Bits(ExactWeighted(values, weights)), BitConverter.DoubleToInt64Bits(whole.Value));

        for (int round = 0; round < 6; round++)
        {
            int parts = random.Next(2, 12);
            WideIndexedSum[] partial = new WideIndexedSum[parts];
            foreach (int i in Enumerable.Range(0, values.Length).OrderBy(_ => random.Next()))
            {
                partial[random.Next(parts)].AddWeighted(values[i], weights[i]);
            }

            WideIndexedSum merged = default;
            foreach (int p in Enumerable.Range(0, parts).OrderBy(_ => random.Next()))
            {
                merged.Merge(in partial[p]);
            }

            Assert.Equal(BitConverter.DoubleToInt64Bits(whole.Value), BitConverter.DoubleToInt64Bits(merged.Value));
            Assert.Equal(whole.Count, merged.Count);
        }
    }

    // A sum a few values short of an epoch's end, its bins near 2^61, then values across that end: a value at
    // a time, as a span, a weight at a time and a run of words at a time, the same bits, the exact total's.
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(7)]
    public void AnEpochEndsOnEveryPathWithTheSameBits(int missing)
    {
        double[] values = Values(41 + missing, (2 * missing) + 5, 10);
        WideIndexedSum start = default;
        start.AddWeighted(Largest, WideIndexedSum.Epoch - missing);
        Assert.Equal(0, start.High);

        WideIndexedSum byValue = start;
        foreach (double value in values)
        {
            byValue.Add(value);
        }

        WideIndexedSum bySpan = start;
        bySpan.AddSpan(values);
        WideIndexedSum byWeight = start;
        foreach (double value in values)
        {
            byWeight.AddWeighted(value, 1);
        }

        // Every other row selected out of a block twice as long: the rows left out are not counted.
        double[] block = [.. values.SelectMany(value => (double[])[value, double.MaxValue])];
        ulong[] words = new ulong[(block.Length + 63) / 64];
        for (int row = 0; row < block.Length; row += 2)
        {
            words[row / 64] |= 1UL << (row % 64);
        }

        WideIndexedSum byWords = start;
        default(WideIndexedFloatSum<double>).AddWords(ref byWords, block, words);

        double[] all = [Largest, .. values];
        long[] weights = [WideIndexedSum.Epoch - missing, .. values.Select(_ => 1L)];
        long expected = BitConverter.DoubleToInt64Bits(ExactWeighted(all, weights));
        foreach (WideIndexedSum sum in (WideIndexedSum[])[byValue, bySpan, byWeight, byWords])
        {
            Assert.NotEqual(0, sum.High & HighParts);
            Assert.Equal(start.Count + values.Length, sum.Count);
            Assert.Equal(expected, BitConverter.DoubleToInt64Bits(sum.Value));
        }
    }

    // Bins settled into their high parts, then a value that moves the top up one, two or three bins, folded or
    // merged: the high parts move with their bins, the bits those of the large value folded first.
    [Theory]
    [InlineData(30)]
    [InlineData(57)]
    [InlineData(84)]
    public void AHighPartMovesWithItsBin(int exponent)
    {
        double large = Math.ScaleB(1.0, exponent);
        WideIndexedSum small = default;
        small.AddWeighted(1.5, 1L << 40);
        Assert.NotEqual(0, small.High & HighParts);
        WideIndexedSum smallFirst = small;
        smallFirst.Add(large);

        WideIndexedSum largeFirst = default;
        largeFirst.Add(large);
        largeFirst.AddWeighted(1.5, 1L << 40);

        WideIndexedSum alone = default;
        alone.Add(large);
        WideIndexedSum mergedUp = small;
        mergedUp.Merge(in alone);
        WideIndexedSum mergedDown = alone;
        mergedDown.Merge(in small);

        long expected = BitConverter.DoubleToInt64Bits(largeFirst.Value);
        foreach (WideIndexedSum sum in (WideIndexedSum[])[smallFirst, mergedUp, mergedDown])
        {
            Assert.Equal(expected, BitConverter.DoubleToInt64Bits(sum.Value));
            Assert.Equal((1L << 40) + 1, sum.Count);
        }

        if (exponent < 84)
        {
            Assert.Equal(BitConverter.DoubleToInt64Bits(ExactWeighted([1.5, large], [1L << 40, 1])), expected);
        }
    }

    [Fact]
    public void AWideSumPastItsCapacityThrowsRatherThanLoseItsExactness()
    {
        // The capacity's values, each the largest part a bin takes: still exact.
        WideIndexedSum full = default;
        full.AddWeighted(Largest, WideIndexedSum.Capacity);
        Assert.Equal(WideIndexedSum.Capacity, full.Count);
        Assert.Equal(BitConverter.DoubleToInt64Bits(ExactWeighted([Largest], [WideIndexedSum.Capacity])), BitConverter.DoubleToInt64Bits(full.Value));

        WideIndexedSum one = full;
        Assert.Throws<OverflowException>(() => one.Add(1.0));
        one = full;
        Assert.Throws<OverflowException>(() => one.AddSpan([1.0]));
        one = full;
        Assert.Throws<OverflowException>(() => one.AddWeighted(1.0, 1));
        WideIndexedSum other = default;
        other.Add(1.0);
        Assert.Throws<OverflowException>(() => other.Merge(in full));

        // One short, a value at a time: the last value the capacity takes, then the one past it.
        WideIndexedSum almost = default;
        almost.AddWeighted(Largest, WideIndexedSum.Capacity - 1);
        almost.Add(1.0);
        Assert.Equal(WideIndexedSum.Capacity, almost.Count);
        Assert.Throws<OverflowException>(() => almost.Add(1.0));
    }

    [Fact]
    public void ANarrowGroupHoldsThirtyTwoBytesAndAWideForty()
    {
        Assert.Equal(32, Unsafe.SizeOf<IndexedSum>());
        Assert.Equal(40, Unsafe.SizeOf<WideIndexedSum>());
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
            total += Units(value);
        }

        return Reference(total, -1074);
    }

    /// <summary>The exact sum of finite values, each times its weight, rounded once to the nearest double.</summary>
    private static double ExactWeighted(double[] values, long[] weights)
    {
        BigInteger total = BigInteger.Zero;
        for (int i = 0; i < values.Length; i++)
        {
            total += Units(values[i]) * weights[i];
        }

        return Reference(total, -1074);
    }

    /// <summary>A finite value in units of 2^-1074, exactly.</summary>
    private static BigInteger Units(double value)
    {
        long bits = BitConverter.DoubleToInt64Bits(value);
        int biased = (int)((bits >> 52) & 0x7FF);
        long mantissa = bits & 0xF_FFFF_FFFF_FFFF;
        if (biased != 0)
        {
            mantissa |= 1L << 52;
        }

        BigInteger scaled = new BigInteger(mantissa) << Math.Max(biased - 1, 0);
        return bits < 0 ? -scaled : scaled;
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
