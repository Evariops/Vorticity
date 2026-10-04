// The .NET types a record maps beyond the dtypes' own: a char as its UTF-16 code unit, a native
// integer as its 64 bits, a TimeSpan as its ticks, and the integers of 128 bits and of any size as
// decimals of scale 0 -- filtered, aggregated and grouped by as the values they are.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Tests.Scan;
using Xunit;

namespace Vorticity.Tests.Matrix;

public sealed class NativeTypeSurfaceTests
{
    [Fact]
    public async Task EachTypeFiltersAsItsValuesCompare()
    {
        Decoders.EnsureRegistered();
        CancellationToken ct = TestContext.Current.CancellationToken;
        AllTypes[] rows = MatrixRows.Shared(Shape.Spread);
        (string path, _) = await TypeEncodingMatrixTests.WrittenAsync(Shape.Spread, EncodingHint.Auto);
        await using VortexFile file = await VortexFile.OpenAsync(path, ct);
        AllTypes probe = rows[4_321];

        Assert.Equal(rows.Count(r => r.I128 > probe.I128), await file.Scan<AllTypes>().Where(r => r.I128 > probe.I128).CountAsync(ct));
        Assert.Equal(rows.Count(r => r.U128 == probe.U128), await file.Scan<AllTypes>().Where(r => r.U128 == probe.U128).CountAsync(ct));
        Assert.Equal(rows.Count(r => r.I128Wide <= probe.I128Wide), await file.Scan<AllTypes>().Where(r => r.I128Wide <= probe.I128Wide).CountAsync(ct));
        Assert.Equal(rows.Count(r => r.Big < probe.Big), await file.Scan<AllTypes>().Where(r => r.Big < probe.Big).CountAsync(ct));
        Assert.Equal(rows.Count(r => r.BigN is { } big && big >= probe.Big), await file.Scan<AllTypes>().Where(r => r.BigN >= probe.Big).CountAsync(ct));
        Assert.Equal(rows.Count(r => r.Char == probe.Char), await file.Scan<AllTypes>().Where(r => r.Char == probe.Char).CountAsync(ct));
        Assert.Equal(rows.Count(r => r.Char > 'z'), await file.Scan<AllTypes>().Where(r => r.Char > 'z').CountAsync(ct));
        Assert.Equal(rows.Count(r => r.Span >= TimeSpan.Zero), await file.Scan<AllTypes>().Where(r => r.Span >= TimeSpan.Zero).CountAsync(ct));
        Assert.Equal(rows.Count(r => r.SpanN is { } span && span < probe.Span), await file.Scan<AllTypes>().Where(r => r.SpanN < probe.Span).CountAsync(ct));
        Assert.Equal(rows.Count(r => r.NInt < probe.NInt), await file.Scan<AllTypes>().Where(r => r.NInt < probe.NInt).CountAsync(ct));
        Assert.Equal(rows.Count(r => r.NUInt != probe.NUInt), await file.Scan<AllTypes>().Where(r => r.NUInt != probe.NUInt).CountAsync(ct));
        Assert.Equal(
            rows.Count(r => r.I128 == probe.I128 || r.I128 == rows[7].I128),
            await file.Scan<AllTypes>().Where(r => r.I128.In(probe.I128, rows[7].I128, Int128.MaxValue)).CountAsync(ct));

        // The holes of an untyped filter take the same values, checked against the column they meet.
        Assert.Equal(rows.Count(r => r.I128 > probe.I128), await file.Scan("I128").Where($"I128 > {probe.I128}").CountAsync(ct));
        Assert.Equal(rows.Count(r => r.U128Wide >= probe.U128Wide), await file.Scan("U128Wide").Where($"U128Wide >= {probe.U128Wide}").CountAsync(ct));
        Assert.Equal(rows.Count(r => r.Big < probe.Big), await file.Scan("Big").Where($"Big < {probe.Big}").CountAsync(ct));
        Assert.Equal(rows.Count(r => r.Char == probe.Char), await file.Scan("Char").Where($"Char = {probe.Char}").CountAsync(ct));
        Assert.Equal(rows.Count(r => r.Span < probe.Span), await file.Scan("Span").Where($"Span < {probe.Span}").CountAsync(ct));
        Assert.Equal(rows.Count(r => r.NInt >= probe.NInt), await file.Scan("NInt").Where($"NInt >= {probe.NInt}").CountAsync(ct));
    }

    [Fact]
    public async Task EachTypeAggregatesToItsOwnType()
    {
        Decoders.EnsureRegistered();
        CancellationToken ct = TestContext.Current.CancellationToken;
        AllTypes[] rows = MatrixRows.Shared(Shape.Spread);
        (string path, _) = await TypeEncodingMatrixTests.WrittenAsync(Shape.Spread, EncodingHint.Auto);
        await using VortexFile file = await VortexFile.OpenAsync(path, ct);

        Assert.Equal(rows.Aggregate(Int128.Zero, (sum, r) => sum + r.I128), await file.Scan<AllTypes>().SumAsync(r => r.I128, ct));
        Assert.Equal(rows.Aggregate(BigInteger.Zero, (sum, r) => sum + r.Big), await file.Scan<AllTypes>().SumAsync(r => r.Big, ct));
        Assert.Equal(rows.Where(r => r.BigN is not null).Aggregate(BigInteger.Zero, (sum, r) => sum + r.BigN!.Value), await file.Scan<AllTypes>().SumAsync(r => r.BigN, ct));
        Assert.Equal(rows.Aggregate((nint)0, (sum, r) => sum + r.NInt), await file.Scan<AllTypes>().SumAsync(r => r.NInt, ct));
        Assert.Equal(rows.Max(r => r.Char), await file.Scan<AllTypes>().MaxAsync(r => r.Char, ct));
        Assert.Equal(rows.Min(r => r.Span), await file.Scan<AllTypes>().MinAsync(r => r.Span, ct));
        Assert.Equal(rows.Max(r => r.U128), await file.Scan<AllTypes>().MaxAsync(r => r.U128, ct));
        Assert.Equal(rows.Min(r => r.I128Wide), await file.Scan<AllTypes>().MinAsync(r => r.I128Wide, ct));
        Assert.Equal(rows.Max(r => r.Big), await file.Scan<AllTypes>().MaxAsync(r => r.Big, ct));
        Assert.Equal(rows.Select(r => r.Char).Distinct().Count(), await file.Scan<AllTypes>().CountDistinctAsync(r => r.Char, ct));
        Assert.Equal(rows.Select(r => r.U128).Distinct().Count(), await file.Scan<AllTypes>().CountDistinctAsync(r => r.U128, ct));

        // A key of each: one group per distinct value, counted.
        Dictionary<char, int> chars = rows.GroupBy(r => r.Char).ToDictionary(g => g.Key, g => g.Count());
        int seen = 0;
        await foreach ((char key, long count) in file.Scan<AllTypes>().GroupBy(r => r.Char).AggAsync(g => (g.Key, g.Count())).WithCancellation(ct))
        {
            Assert.Equal(chars[key], count);
            seen++;
        }

        Assert.Equal(chars.Count, seen);
        Dictionary<Int128, int> wides = rows.GroupBy(r => r.I128Wide).ToDictionary(g => g.Key, g => g.Count());
        seen = 0;
        await foreach ((Int128 key, long count) in file.Scan<AllTypes>().GroupBy(r => r.I128Wide).AggAsync(g => (g.Key, g.Count())).WithCancellation(ct))
        {
            Assert.Equal(wides[key], count);
            seen++;
        }

        Assert.Equal(wides.Count, seen);
    }

    [Fact]
    public async Task An128BitIntegerPastThirtyEightDigitsIsRefusedWithTheWayOut()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        AllTypes[] rows = MatrixRows.Build(Shape.Constant, count: 4);
        rows[2] = rows[2] with { I128 = Int128.MaxValue };
        string path = TypeEncodingMatrixTests.Temp();
        try
        {
            VortexSchemaException refused = await Assert.ThrowsAsync<VortexSchemaException>(
                () => TypeEncodingMatrixTests.WriteAsync(path, rows, EncodingHint.Auto, ct));
            Assert.Contains("Precision = 39", refused.Message, StringComparison.Ordinal);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public async Task A128BitColumnWiderThan128BitsIsCopiedNotViewed()
    {
        Decoders.EnsureRegistered();
        CancellationToken ct = TestContext.Current.CancellationToken;
        AllTypes[] rows = MatrixRows.Shared(Shape.Runs);
        (string path, _) = await TypeEncodingMatrixTests.WrittenAsync(Shape.Runs, EncodingHint.Canonical);
        await using VortexFile file = await VortexFile.OpenAsync(path, ct);
        int offset = 0;
        await foreach (Columns<AllTypes> batch in file.Scan<AllTypes>().WithCancellation(ct))
        {
            Column<Int128> narrow = batch.I128;
            Column<Int128> wide = batch.I128Wide;
            Int128[] copied = new Int128[wide.Length];
            wide.CopyTo(copied);
            for (int i = 0; i < copied.Length; i++)
            {
                Assert.Equal(rows[offset + i].I128Wide, copied[i]);
                Assert.Equal(rows[offset + i].I128Wide, wide[i]);
                Assert.Equal(rows[offset + i].I128, narrow.Values[i]);
            }

            Assert.True(ThrowsOnValues(wide));
            offset += copied.Length;
        }

        Assert.Equal(rows.Length, offset);
    }

    private static bool ThrowsOnValues(Column<Int128> column)
    {
        try
        {
            _ = column.Values.Length;
            return false;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }
}
