// The ranks of a sorted column of every primitive type, against the values counted one by one: each column ascending over
// several zones, each value held by a few rows, sought at each value, between two, below the least, above the greatest
// and at the type's ends; a bound is the count of the values below the key, or at most the key.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Vorticity.Expressions;
using Vorticity.Keys;
using Xunit;

namespace Vorticity.Tests.Keys;

public sealed partial class SortedColumnRankTests
{
    private const int Rows = 30_000;

    [VortexRecord]
    public partial record struct Typed(sbyte I8, short I16, int I32, long I64, byte U8, ushort U16, uint U32, ulong U64, float F32, double F64);

    [Fact]
    public async Task EachRankIsTheCountOfTheValuesBelowTheKey()
    {
        Typed[] rows = RowsOf(Rows);
        string path = await WriteAsync(rows, options: null);
        try
        {
            await using VortexFile file = await VortexFile.OpenAsync(path, cancellationToken: TestContext.Current.CancellationToken);
            await CheckEveryColumn(file, rows);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    /// <summary>
    /// A file of so few rows that one zone holds them all, written with no zone map, is its own zone: each rank is what
    /// the file with its map gives, the zone decoded as the one a map names is. A dataset's small objects keep no map. A
    /// file of one row more without a map is refused, since a seek would decode a column of any length.
    /// </summary>
    [Fact]
    public async Task AFileOfOneZoneWithoutAZoneMapRanksAsOneWithIt()
    {
        VortexWriteOptions unmapped = new VortexWriteOptions { Zones = false };
        Typed[] rows = RowsOf(Vorticity.Compute.ZoneColumn.LoneZoneRows);
        Typed[] more = RowsOf(Vorticity.Compute.ZoneColumn.LoneZoneRows + 1);
        string path = await WriteAsync(rows, unmapped);
        string longer = await WriteAsync(more, unmapped);
        try
        {
            await using VortexFile file = await VortexFile.OpenAsync(path, cancellationToken: TestContext.Current.CancellationToken);
            await CheckEveryColumn(file, rows);

            await using VortexFile refused = await VortexFile.OpenAsync(longer, cancellationToken: TestContext.Current.CancellationToken);
            (SortedColumnSource? source, string? reason) = await SortedColumnSource.OpenAsync(refused, nameof(Typed.I64), TestContext.Current.CancellationToken);
            Assert.Null(source);
            Assert.Contains("no zone map", reason, StringComparison.Ordinal);
        }
        finally
        {
            System.IO.File.Delete(path);
            System.IO.File.Delete(longer);
        }
    }

    /// <summary>Rows ascending in every column, a value every few rows, the signed ones through zero.</summary>
    private static Typed[] RowsOf(int count)
    {
        Typed[] rows = new Typed[count];
        for (int i = 0; i < count; i++)
        {
            int step = i / 7;
            rows[i] = new Typed(
                (sbyte)(-120 + (step * 240 / (count / 7))),
                (short)(-20_000 + (step * 9)),
                -2_000_000 + (step * 1_000),
                -5_000_000_000L + (step * 3_000_000L),
                (byte)(step * 250 / (count / 7)),
                (ushort)(step * 15),
                3_000_000_000u + (uint)(step * 11),
                ulong.MaxValue - 9_000_000_000UL + ((ulong)step * 2_000_000UL),
                -1_000.5f + (step * 0.75f),
                -2_000.25 + (step * 1.5));
        }

        return rows;
    }

    private static async Task<string> WriteAsync(Typed[] rows, VortexWriteOptions? options)
    {
        string path = Path.Combine(Path.GetTempPath(), $"vorticity-ranks-{Guid.NewGuid():N}.vortex");
        await using VortexFileWriter writer = VortexSession.Default.CreateWriter<Typed>(path, options);
        await writer.WriteAsync<Typed>(rows, TestContext.Current.CancellationToken);
        await writer.CompleteAsync(TestContext.Current.CancellationToken);
        return path;
    }

    private static async Task CheckEveryColumn(VortexFile file, Typed[] rows)
    {
        await Signed(file, nameof(Typed.I8), rows.Select(r => (long)r.I8).ToArray(), sbyte.MinValue, sbyte.MaxValue);
        await Signed(file, nameof(Typed.I16), rows.Select(r => (long)r.I16).ToArray(), short.MinValue, short.MaxValue);
        await Signed(file, nameof(Typed.I32), rows.Select(r => (long)r.I32).ToArray(), int.MinValue, int.MaxValue);
        await Signed(file, nameof(Typed.I64), rows.Select(r => r.I64).ToArray(), long.MinValue, long.MaxValue);
        await Unsigned(file, nameof(Typed.U8), rows.Select(r => (ulong)r.U8).ToArray(), byte.MaxValue);
        await Unsigned(file, nameof(Typed.U16), rows.Select(r => (ulong)r.U16).ToArray(), ushort.MaxValue);
        await Unsigned(file, nameof(Typed.U32), rows.Select(r => (ulong)r.U32).ToArray(), uint.MaxValue);
        await Unsigned(file, nameof(Typed.U64), rows.Select(r => r.U64).ToArray(), ulong.MaxValue);
        await Floats(file, nameof(Typed.F32), rows.Select(r => (double)r.F32).ToArray());
        await Floats(file, nameof(Typed.F64), rows.Select(r => r.F64).ToArray());
    }

    private static async Task Signed(VortexFile file, string column, long[] values, long least, long most)
    {
        List<long> keys = Keys(values, least, most);
        await Check(file, column, values, keys.Select(FilterLiteral.From));
    }

    private static async Task Unsigned(VortexFile file, string column, ulong[] values, ulong most)
    {
        List<ulong> keys = [0, 1, most, most - 1, values[0] - (values[0] > 0 ? 1UL : 0), values[^1] + (values[^1] < most ? 1UL : 0)];
        for (int i = 0; i < values.Length; i += 997)
        {
            keys.Add(values[i]);
            keys.Add(values[i] + (values[i] < most ? 1UL : 0));
        }

        await Check(file, column, values, keys.Select(FilterLiteral.From));
    }

    private static async Task Floats(VortexFile file, string column, double[] values)
    {
        List<double> keys = [double.NegativeInfinity, double.PositiveInfinity, values[0] - 1, values[^1] + 1, 0.0, -0.0];
        for (int i = 0; i < values.Length; i += 997)
        {
            keys.Add(values[i]);
            keys.Add(values[i] + 0.1);
        }

        await Check(file, column, values, keys.Select(FilterLiteral.From));
    }

    private static List<long> Keys(long[] values, long least, long most)
    {
        List<long> keys = [least, most, values[0] - 1, values[^1] + 1];
        for (int i = 0; i < values.Length; i += 997)
        {
            keys.Add(values[i]);
            keys.Add(values[i] + 1);
        }

        return keys;
    }

    private static async Task Check<T>(VortexFile file, string column, T[] values, IEnumerable<FilterLiteral> keys)
        where T : INumber<T>
    {
        (SortedColumnSource? source, string? reason) = await SortedColumnSource.OpenAsync(file, column, TestContext.Current.CancellationToken);
        Assert.True(source is not null, reason);
        foreach (FilterLiteral key in keys)
        {
            T sought = key.Kind switch
            {
                FilterLiteralKind.Signed => T.CreateTruncating(key.SignedValue),
                FilterLiteralKind.Unsigned => T.CreateTruncating(key.UnsignedValue),
                _ => T.CreateTruncating(key.FloatValue),
            };

            // A key past the column's type compares as the literal: counted in its own type here.
            long below = values.LongCount(v => Below(v, key, strictly: true));
            long atMost = values.LongCount(v => Below(v, key, strictly: false));
            Assert.Equal(below, await source!.LowerBoundAsync(key, TestContext.Current.CancellationToken));
            Assert.Equal(atMost, await source.UpperBoundAsync(key, TestContext.Current.CancellationToken));
            _ = sought;
        }
    }

    private static bool Below<T>(T value, FilterLiteral key, bool strictly)
        where T : INumber<T>
    {
        int order = key.Kind switch
        {
            FilterLiteralKind.Signed => long.CreateTruncating(value).CompareTo(key.SignedValue),
            FilterLiteralKind.Unsigned => ulong.CreateTruncating(value).CompareTo(key.UnsignedValue),
            _ => double.CreateTruncating(value) < key.FloatValue ? -1 : double.CreateTruncating(value) > key.FloatValue ? 1 : 0,
        };

        return strictly ? order < 0 : order <= 0;
    }
}
