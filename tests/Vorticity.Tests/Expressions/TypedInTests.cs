// `VortexExpr.In`: a set of typed values for the untyped scan, bound to its column when the scan
// takes the filter, as a hole of an interpolated filter is. Over the type matrix it keeps the rows
// the typed scan's `In` keeps, column by column; it refuses what a hole refuses; and it holds its
// own copy of the values, so a caller that reuses its buffer does not change the filter.
using System;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Tests.Matrix;
using Vorticity.Tests.Scan;
using Xunit;

namespace Vorticity.Tests.Expressions;

public sealed class TypedInTests
{
    [Fact]
    public async Task EachTypeKeepsTheRowsTheTypedInKeeps()
    {
        Decoders.EnsureRegistered();
        CancellationToken ct = TestContext.Current.CancellationToken;
        AllTypes[] rows = MatrixRows.Shared(Shape.Spread);
        (string path, _) = await TypeEncodingMatrixTests.WrittenAsync(Shape.Spread, EncodingHint.Auto);
        await using VortexFile file = await VortexFile.OpenAsync(path, ct);
        AllTypes[] probes = [rows[3], rows[4_321], rows[^1]];

        int[] ints = [.. probes.Select(r => r.I32), int.MaxValue];
        Assert.Equal(await TypedAsync(file, r => r.I32.In(ints), ct), await CountAsync(file, VortexExpr.In("I32", ints), ct));
        Assert.Equal(rows.Count(r => ints.Contains(r.I32)), await CountAsync(file, VortexExpr.In("I32", ints), ct));

        // A number goes to any numeric column, placed against its width: a value past it matches no row.
        long[] wide = [.. probes.Select(r => (long)r.I32), long.MaxValue];
        Assert.Equal(rows.Count(r => ints.Contains(r.I32)), await CountAsync(file, VortexExpr.In("I32", wide), ct));
        int[] shorts = [.. probes.Select(r => (int)r.I16), 1 << 20];
        Assert.Equal(rows.Count(r => shorts.Contains(r.I16)), await CountAsync(file, VortexExpr.In("I16", shorts), ct));
        long[] unsigned = [.. probes.Select(r => (long)r.U32), -1];
        Assert.Equal(rows.Count(r => unsigned.Contains(r.U32)), await CountAsync(file, VortexExpr.In("U32", unsigned), ct));
        double[] doubles = [.. probes.Select(r => r.F64), 0.5];
        Assert.Equal(await TypedAsync(file, r => r.F64.In(doubles), ct), await CountAsync(file, VortexExpr.In("F64", doubles), ct));

        decimal[] decimals = [.. probes.Select(r => r.Dec64), 1.23456m];
        Assert.Equal(await TypedAsync(file, r => r.Dec64.In(decimals), ct), await CountAsync(file, VortexExpr.In("Dec64", decimals), ct));
        string?[] texts = [.. probes.Select(r => r.Text), null, "absent"];
        Assert.Equal(rows.Count(r => texts.Contains(r.Text)), await CountAsync(file, VortexExpr.In("Text", texts), ct));
        DateOnly[] dates = [.. probes.Select(r => r.Date)];
        Assert.Equal(await TypedAsync(file, r => r.Date.In(dates), ct), await CountAsync(file, VortexExpr.In("Date", dates), ct));
        DateTime[] stamps = [.. probes.Select(r => r.Stamp)];
        Assert.Equal(await TypedAsync(file, r => r.Stamp.In(stamps), ct), await CountAsync(file, VortexExpr.In("Stamp", stamps), ct));
        DateTimeOffset[] zoned = [.. probes.Select(r => r.Zoned)];
        Assert.Equal(await TypedAsync(file, r => r.Zoned.In(zoned), ct), await CountAsync(file, VortexExpr.In("Zoned", zoned), ct));
        Guid[] uuids = [.. probes.Select(r => r.Uuid), Guid.Empty];
        Assert.Equal(await TypedAsync(file, r => r.Uuid.In(uuids), ct), await CountAsync(file, VortexExpr.In("Uuid", uuids), ct));
        char[] chars = [.. probes.Select(r => r.Char)];
        Assert.Equal(rows.Count(r => chars.Contains(r.Char)), await CountAsync(file, VortexExpr.In("Char", chars), ct));
        Int128[] wides = [.. probes.Select(r => r.I128), Int128.MaxValue];
        Assert.Equal(await TypedAsync(file, r => r.I128.In(wides), ct), await CountAsync(file, VortexExpr.In("I128", wides), ct));
        BigInteger[] bigs = [.. probes.Select(r => r.Big)];
        Assert.Equal(await TypedAsync(file, r => r.Big.In(bigs), ct), await CountAsync(file, VortexExpr.In("Big", bigs), ct));
    }

    [Fact]
    public async Task AColumnThatDoesNotTakeTheValuesIsRefusedWhereTheScanTakesTheFilter()
    {
        Decoders.EnsureRegistered();
        CancellationToken ct = TestContext.Current.CancellationToken;
        (string path, _) = await TypeEncodingMatrixTests.WrittenAsync(Shape.Spread, EncodingHint.Auto);
        await using VortexFile file = await VortexFile.OpenAsync(path, ct);

        // Built without a schema, so nothing is refused until a scan resolves the column.
        VortexExpr numbers = VortexExpr.In("Text", 1, 2);
        Assert.Throws<VortexSchemaException>(() => file.Scan("Text").Where(numbers));
        Assert.Throws<VortexSchemaException>(() => file.Scan("I32").Where(VortexExpr.In("I32", "1")));
        Assert.Throws<VortexSchemaException>(() => file.Scan("I32").Where(VortexExpr.In("Missing", 1)));
        Assert.Throws<ArgumentException>(() => VortexExpr.In("", 1));
    }

    [Fact]
    public async Task TheFilterHoldsItsOwnValuesAndCombinesAsAnyOther()
    {
        Decoders.EnsureRegistered();
        CancellationToken ct = TestContext.Current.CancellationToken;
        AllTypes[] rows = MatrixRows.Shared(Shape.Spread);
        (string path, _) = await TypeEncodingMatrixTests.WrittenAsync(Shape.Spread, EncodingHint.Auto);
        await using VortexFile file = await VortexFile.OpenAsync(path, ct);

        int[] buffer = [rows[3].I32, rows[4_321].I32];
        VortexExpr filter = VortexExpr.In("I32", buffer);
        long kept = rows.Count(r => r.I32 == rows[3].I32 || r.I32 == rows[4_321].I32);
        buffer[0] = int.MinValue;
        buffer[1] = int.MinValue;
        Assert.Equal(kept, await CountAsync(file, filter, ct));

        Assert.Equal(
            rows.Count(r => (r.I32 == rows[3].I32 || r.I32 == rows[4_321].I32) && r.I64 > 0),
            await CountAsync(file, filter & VortexExpr.Parse("I64 > 0"), ct));
        Assert.Equal(rows.Length - kept, await CountAsync(file, !filter, ct));
        Assert.Equal("I32 in (1, -2, 3)", VortexExpr.In("I32", 1, -2, 3).ToString());
        Assert.Equal("Text in ('a', null)", VortexExpr.In("Text", "a", null).ToString());
    }

    private static async Task<long> CountAsync(VortexFile file, VortexExpr filter, CancellationToken ct) =>
        await file.Scan("I32").Where(filter).CountAsync(ct);

    private static async Task<long> TypedAsync(VortexFile file, Func<Probe<AllTypes>, Predicate> filter, CancellationToken ct) =>
        await file.Scan<AllTypes>().Where(filter).CountAsync(ct);
}
