// The aggregates of every decimal width, 128-bit and 256-bit storage included, against the same
// arithmetic done on the rows in memory with BigInteger, which cannot overflow.
//
// A SUM THAT DOES NOT FIT ITS RESULT THROWS, and one whose running total passes the storage width
// on the way to a result that fits does not: a decimal(76, 10) column holding the largest value and
// its opposite in turn sums to a small number, and the answer must be that number.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Tests.Scan;
using Xunit;

namespace Vorticity.Tests.Matrix;

public sealed class DecimalAggregateTests
{
    public static TheoryData<Shape> Shapes() => [.. Enum.GetValues<Shape>()];

    [Theory]
    [MemberData(nameof(Shapes))]
    public async Task EveryDecimalWidthAggregatesExactly(Shape shape)
    {
        Decoders.EnsureRegistered();
        CancellationToken ct = TestContext.Current.CancellationToken;
        AllTypes[] rows = MatrixRows.Build(shape);
        string path = TypeEncodingMatrixTests.Temp();
        try
        {
            await TypeEncodingMatrixTests.WriteAsync(path, rows, EncodingHint.Auto, ct);
            await using VortexFile file = await VortexFile.OpenAsync(path, ct);
            Func<Scan<AllTypes>> scan = file.Scan<AllTypes>;

            await Check(rows.Select(r => (decimal?)r.Dec8), 1, () => scan().SumAsync(r => r.Dec8, ct), () => scan().AvgAsync(r => r.Dec8, ct), () => N(scan().MinAsync(r => r.Dec8, ct)), () => N(scan().MaxAsync(r => r.Dec8, ct)), () => scan().CountDistinctAsync(r => r.Dec8, ct));
            await Check(rows.Select(r => (decimal?)r.Dec16), 2, () => scan().SumAsync(r => r.Dec16, ct), () => scan().AvgAsync(r => r.Dec16, ct), () => N(scan().MinAsync(r => r.Dec16, ct)), () => N(scan().MaxAsync(r => r.Dec16, ct)), () => scan().CountDistinctAsync(r => r.Dec16, ct));
            await Check(rows.Select(r => (decimal?)r.Dec32), 2, () => scan().SumAsync(r => r.Dec32, ct), () => scan().AvgAsync(r => r.Dec32, ct), () => N(scan().MinAsync(r => r.Dec32, ct)), () => N(scan().MaxAsync(r => r.Dec32, ct)), () => scan().CountDistinctAsync(r => r.Dec32, ct));
            await Check(rows.Select(r => (decimal?)r.Dec64), 4, () => scan().SumAsync(r => r.Dec64, ct), () => scan().AvgAsync(r => r.Dec64, ct), () => N(scan().MinAsync(r => r.Dec64, ct)), () => N(scan().MaxAsync(r => r.Dec64, ct)), () => scan().CountDistinctAsync(r => r.Dec64, ct));
            await Check(rows.Select(r => (decimal?)r.Dec128), 10, () => scan().SumAsync(r => r.Dec128, ct), () => scan().AvgAsync(r => r.Dec128, ct), () => N(scan().MinAsync(r => r.Dec128, ct)), () => N(scan().MaxAsync(r => r.Dec128, ct)), () => scan().CountDistinctAsync(r => r.Dec128, ct));
            await Check(rows.Select(r => r.Dec128N), 10, () => scan().SumAsync(r => r.Dec128N, ct), () => scan().AvgAsync(r => r.Dec128N, ct), () => N(scan().MinAsync(r => r.Dec128N, ct)), () => N(scan().MaxAsync(r => r.Dec128N, ct)), () => scan().CountDistinctAsync(r => r.Dec128N, ct));

            await CheckWide(rows.Select(r => (VortexDecimal?)r.Wide128), 6, () => scan().SumAsync(r => r.Wide128, ct), () => scan().AvgAsync(r => r.Wide128, ct), () => N(scan().MinAsync(r => r.Wide128, ct)), () => N(scan().MaxAsync(r => r.Wide128, ct)), () => scan().CountDistinctAsync(r => r.Wide128, ct));
            await CheckWide(rows.Select(r => (VortexDecimal?)r.Wide256), 10, () => scan().SumAsync(r => r.Wide256, ct), () => scan().AvgAsync(r => r.Wide256, ct), () => N(scan().MinAsync(r => r.Wide256, ct)), () => N(scan().MaxAsync(r => r.Wide256, ct)), () => scan().CountDistinctAsync(r => r.Wide256, ct));
            await CheckWide(rows.Select(r => r.Wide256N), 10, () => scan().SumAsync(r => r.Wide256N, ct), () => scan().AvgAsync(r => r.Wide256N, ct), () => N(scan().MinAsync(r => r.Wide256N, ct)), () => N(scan().MaxAsync(r => r.Wide256N, ct)), () => scan().CountDistinctAsync(r => r.Wide256N, ct));
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Theory]
    [MemberData(nameof(Shapes))]
    public async Task EveryDecimalWidthAggregatesInOnePassAndPerGroup(Shape shape)
    {
        Decoders.EnsureRegistered();
        CancellationToken ct = TestContext.Current.CancellationToken;
        AllTypes[] rows = MatrixRows.Build(shape);
        string path = TypeEncodingMatrixTests.Temp();
        try
        {
            await TypeEncodingMatrixTests.WriteAsync(path, rows, EncodingHint.Auto, ct);
            await using VortexFile file = await VortexFile.OpenAsync(path, ct);

            // In one pass: the answers a single scan computes together are the ones computed apart.
            (VortexDecimal? min, VortexDecimal? max, long distinct, double? mean) = await file.Scan<AllTypes>()
                .AggAsync(a => (a.Min(r => r.Wide256), a.Max(r => r.Wide256), a.CountDistinct(r => r.Wide256), a.Avg(r => r.Wide256)), ct);
            List<BigInteger> wide = rows.Select(r => Unscaled(r.Wide256, 10)).ToList();
            Assert.Equal(wide.Min(), Unscaled(min!.Value, 10));
            Assert.Equal(wide.Max(), Unscaled(max!.Value, 10));
            Assert.Equal(wide.Distinct().Count(), distinct);
            AssertClose(Mean(wide, 10), mean);

            // Per group, keyed by the enum: every group's sum and maximum, or its overflow.
            Dictionary<Status, List<AllTypes>> groups = rows.GroupBy(r => r.State).ToDictionary(g => g.Key, g => g.ToList());
            var byState = file.Scan<AllTypes>()
                .GroupBy(r => r.State)
                .AggAsync(g => (g.Key, g.Max(r => r.Wide128), g.Avg(r => r.Wide128)));
            int seen = 0;
            await foreach ((Status state, VortexDecimal groupMax, double? groupMean) in byState.WithCancellation(ct))
            {
                List<BigInteger> values = groups[state].Select(r => Unscaled(r.Wide128, 6)).ToList();
                Assert.Equal(values.Max(), Unscaled(groupMax, 6));
                AssertClose(Mean(values, 6), groupMean);
                seen++;
            }

            Assert.Equal(groups.Count, seen);

            // A decimal of 76 digits as the key itself: one group per distinct value.
            int keys = 0;
            await foreach ((VortexDecimal key, long count) in file.Scan<AllTypes>()
                .GroupBy(r => r.Wide256)
                .AggAsync(g => (g.Key, g.Count())).WithCancellation(ct))
            {
                BigInteger unscaled = Unscaled(key, 10);
                Assert.Equal(wide.Count(v => v == unscaled), count);
                keys++;
            }

            Assert.Equal(wide.Distinct().Count(), keys);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    // ------------------------------------------------------------------------------ the checks

    private static async Task Check(
        IEnumerable<decimal?> column, int scale, Func<ValueTask<decimal>> sum, Func<ValueTask<double?>> mean,
        Func<Task<decimal?>> min, Func<Task<decimal?>> max, Func<ValueTask<long>> distinct)
    {
        List<BigInteger> values = column.Where(v => v is not null).Select(v => Unscaled(v!.Value, scale)).ToList();
        BigInteger total = values.Aggregate(BigInteger.Zero, (a, b) => a + b);
        if (FitsDecimal(total, scale))
        {
            Assert.Equal(total, Unscaled(await sum(), scale));
        }
        else
        {
            await Assert.ThrowsAsync<OverflowException>(async () => await sum());
        }

        AssertClose(Mean(values, scale), await mean());
        Assert.Equal(values.Count == 0 ? (BigInteger?)null : values.Min(), await min() is { } least ? Unscaled(least, scale) : null);
        Assert.Equal(values.Count == 0 ? (BigInteger?)null : values.Max(), await max() is { } most ? Unscaled(most, scale) : null);
        Assert.Equal(values.Distinct().Count(), await distinct());
    }

    private static async Task CheckWide(
        IEnumerable<VortexDecimal?> column, int scale, Func<ValueTask<VortexDecimal>> sum, Func<ValueTask<double?>> mean,
        Func<Task<VortexDecimal?>> min, Func<Task<VortexDecimal?>> max, Func<ValueTask<long>> distinct)
    {
        List<BigInteger> values = column.Where(v => v is not null).Select(v => Unscaled(v!.Value, scale)).ToList();
        BigInteger total = values.Aggregate(BigInteger.Zero, (a, b) => a + b);
        if (BigInteger.Abs(total) < BigInteger.Pow(10, 76))
        {
            VortexDecimal result = await sum();
            Assert.Equal(scale, result.Scale);
            Assert.Equal(total, Unscaled(result, scale));
        }
        else
        {
            await Assert.ThrowsAsync<OverflowException>(async () => await sum());
        }

        AssertClose(Mean(values, scale), await mean());
        Assert.Equal(values.Count == 0 ? (BigInteger?)null : values.Min(), await min() is { } least ? Unscaled(least, scale) : null);
        Assert.Equal(values.Count == 0 ? (BigInteger?)null : values.Max(), await max() is { } most ? Unscaled(most, scale) : null);
        Assert.Equal(values.Distinct().Count(), await distinct());
    }

    private static async Task<decimal?> N(ValueTask<decimal> value) => await value;

    private static async Task<decimal?> N(ValueTask<decimal?> value) => await value;

    private static async Task<VortexDecimal?> N(ValueTask<VortexDecimal> value) => await value;

    private static async Task<VortexDecimal?> N(ValueTask<VortexDecimal?> value) => await value;

    private static bool FitsDecimal(BigInteger unscaled, int scale) =>
        scale <= 28 && BigInteger.Abs(unscaled) < (BigInteger.One << 96);

    private static double? Mean(List<BigInteger> values, int scale) =>
        values.Count == 0 ? null : (double)values.Aggregate(BigInteger.Zero, (a, b) => a + b) / Math.Pow(10, scale) / values.Count;

    private static void AssertClose(double? expected, double? actual)
    {
        if (expected is null)
        {
            Assert.Null(actual);
            return;
        }

        Assert.NotNull(actual);
        double tolerance = Math.Max(Math.Abs(expected.Value) * 1e-12, 1e-300);
        Assert.True(Math.Abs(expected.Value - actual.Value) <= tolerance || (double.IsInfinity(expected.Value) && expected == actual),
            $"expected {expected:R}, got {actual:R}");
    }

    private static BigInteger Unscaled(decimal value, int scale)
    {
        Span<int> bits = stackalloc int[4];
        decimal.GetBits(value, bits);
        BigInteger magnitude = ((BigInteger)(uint)bits[2] << 64) | ((BigInteger)(uint)bits[1] << 32) | (uint)bits[0];
        int from = (bits[3] >> 16) & 0xFF;
        magnitude *= BigInteger.Pow(10, scale - from);
        return bits[3] < 0 ? -magnitude : magnitude;
    }

    private static BigInteger Unscaled(VortexDecimal value, int scale)
    {
        BigInteger unscaled = value.GetUnscaledValue();
        return value.Scale <= scale
            ? unscaled * BigInteger.Pow(10, scale - value.Scale)
            : unscaled / BigInteger.Pow(10, value.Scale - scale);
    }
}
