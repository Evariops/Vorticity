using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Compute;
using Vorticity.Expressions;
using Vorticity.Parquet.Metadata;
using Vorticity.Parquet.Reading;
using Vorticity.Parquet.Schema;
using Vorticity.Parquet.Writing;
using Xunit;

namespace Vorticity.Parquet.Tests;

/// <summary>
/// The statistics the writer gives each column, read back as the reader's bounds: every type with
/// an order, its exactness, its null and NaN counts, a float's in IEEE 754's total order, a long
/// text's cut and raised; and every page's, in the column index.
/// </summary>
public sealed class StatisticsTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"vorticity-statistics-{Guid.NewGuid():N}.parquet");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => System.IO.File.Delete(_path);

    [Fact]
    public async Task BoundsEveryTypeThatHasAnOrder()
    {
        VortexSchema schema =
        [
            ("i8", VortexType.Int8),
            ("u16", VortexType.UInt16.Nullable),
            ("i64", VortexType.Int64),
            ("u64", VortexType.UInt64),
            ("f32", VortexType.Float32.Nullable),
            ("f64", VortexType.Float64),
            ("half", VortexType.Float16),
            ("flag", VortexType.Bool),
            ("text", VortexType.Utf8.Nullable),
            ("raw", VortexType.Binary),
            ("d9", VortexType.Decimal(9, 2)),
            ("d28", VortexType.Decimal(28, 5)),
            ("day", VortexType.Date),
            ("id", VortexType.Uuid),
        ];
        const int rows = 1_000;
        await using (ParquetFileWriter writer = VortexSession.Default.CreateParquetWriter(_path, schema, new ParquetWriteOptions { BlockRows = 256, RowGroupRows = 1_024 }))
        {
            ColumnsBuilder builder = writer.Builder();
            for (int i = 0; i < rows; i++)
            {
                builder.Column<sbyte>(0).Append((sbyte)(i % 200 - 100));
                if (i % 10 == 0)
                {
                    builder.Column<ushort?>(1).AppendNull();
                    builder.Column<float?>(4).AppendNull();
                    builder.Column<string?>(8).AppendNull();
                }
                else
                {
                    builder.Column<ushort?>(1).Append((ushort)(i * 50));
                    builder.Column<float?>(4).Append(i % 7 == 0 ? float.NaN : i - 400.5f);
                    builder.Column<string?>(8).Append(i == 1 ? new string('é', 40) : $"text {i:D4}");
                }

                builder.Column<long>(2).Append(-1_000_000L + i * 3);
                builder.Column<ulong>(3).Append(ulong.MaxValue - (ulong)i);
                builder.Column<double>(5).Append(i == 3 ? -0.0 : i == 4 ? double.NaN : i * 0.5);
                builder.Column<Half>(6).Append((Half)(i - 500));
                builder.Column<bool>(7).Append(i % 2 == 0);
                builder.Column<ReadOnlyMemory<byte>>(9).Append(new byte[] { (byte)(i % 251), 0xFF, 7 });
                builder.Column<decimal>(10).Append((i - 500) * 1.25m);
                builder.Column<decimal>(11).Append(i * -123_456_789.12345m);
                builder.Column<DateOnly>(12).Append(DateOnly.FromDayNumber(700_000 + i));
                builder.Column<Guid>(13).Append(new Guid(i, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0));
            }

            await writer.WriteAsync(builder, Ct);
            await writer.CompleteAsync(Ct);
        }

        await using ParquetFile file = await ParquetFile.OpenAsync(_path, Ct);
        ParquetFooter footer = file.Footer;
        ZoneBounds Bounds(int column) => ColumnBounds.Of(file.Compiled.Columns[column], footer.Chunk(0, column).Statistics, footer.Bytes);

        AssertBounds(Bounds(0), FilterLiteral.From(-100L), FilterLiteral.From(99L), nulls: 0);
        AssertBounds(Bounds(1), FilterLiteral.From(50UL), FilterLiteral.From(999UL * 50), nulls: 100);
        AssertBounds(Bounds(2), FilterLiteral.From(-1_000_000L), FilterLiteral.From(-1_000_000L + 999 * 3), nulls: 0);
        AssertBounds(Bounds(3), FilterLiteral.From(ulong.MaxValue - 999), FilterLiteral.From(ulong.MaxValue), nulls: 0);

        // Floats: the NaNs counted and left out of the bounds, the null rows counted apart.
        ZoneBounds f32 = Bounds(4);
        AssertBounds(f32, FilterLiteral.From(1 - 400.5f), FilterLiteral.From(999 - 400.5f), nulls: 100);
        Assert.True(f32.HasNanCount);
        Assert.Equal(CountNans(rows), f32.NanCount);
        ZoneBounds f64 = Bounds(5);
        AssertBounds(f64, FilterLiteral.From(-0.0), FilterLiteral.From(999 * 0.5), nulls: 0);
        Assert.Equal(1, f64.NanCount);
        Assert.True(double.IsNegative(f64.Min.FloatValue));
        AssertBounds(Bounds(6), FilterLiteral.From(-500.0), FilterLiteral.From(499.0), nulls: 0);
        AssertBounds(Bounds(7), FilterLiteral.From(false), FilterLiteral.From(true), nulls: 0);

        // The longest text, 80 bytes of é, is cut to 64 at a code point and stays UTF-8.
        ZoneBounds text = Bounds(8);
        Assert.Equal(100, text.NullCount);
        Assert.False(text.IsExact);
        Assert.Equal(Encoding.UTF8.GetBytes("text 0002"), text.Min.BytesValue.ToArray());
        Assert.Equal(Encoding.UTF8.GetBytes(new string('é', 31) + "ê"), text.Max.BytesValue.ToArray());
        AssertBounds(Bounds(9), FilterLiteral.From(new byte[] { 0, 0xFF, 7 }), FilterLiteral.From(new byte[] { 250, 0xFF, 7 }), nulls: 0);
        AssertBounds(Bounds(10), FilterLiteral.From(-500 * 125L), FilterLiteral.From(499 * 125L), nulls: 0);
        Assert.True(ColumnBounds.IsDecimal(file.Compiled.Columns[11]));
        int epoch = DateOnly.FromDayNumber(700_000).DayNumber - DateOnly.FromDateTime(DateTime.UnixEpoch).DayNumber;
        AssertBounds(Bounds(12), FilterLiteral.From((long)epoch), FilterLiteral.From(epoch + 999L), nulls: 0);
        ColumnStatistics uuid = footer.Chunk(0, 13).Statistics;
        Assert.True(uuid.MinValue.IsPresent && uuid.MaxValue.IsPresent && uuid.MinValue.Length == 16);

        // A float's order is IEEE 754's total order, every other's its type's.
        Assert.Equal(ColumnOrderKind.Ieee754TotalOrder, file.Compiled.Columns[4].Order);
        Assert.Equal(ColumnOrderKind.Ieee754TotalOrder, file.Compiled.Columns[6].Order);
        Assert.Equal(ColumnOrderKind.TypeDefined, file.Compiled.Columns[8].Order);
    }

    [Fact]
    public async Task IndexesEveryPageOfABoundedColumn()
    {
        VortexSchema schema = [("id", VortexType.Int64), ("name", VortexType.Utf8.Nullable)];
        await using (ParquetFileWriter writer = VortexSession.Default.CreateParquetWriter(_path, schema, new ParquetWriteOptions { BlockRows = 100, RowGroupRows = 1_000 }))
        {
            ColumnsBuilder builder = writer.Builder();
            for (int i = 0; i < 1_000; i++)
            {
                builder.Column<long>(0).Append(i);
                if (i is >= 300 and < 400)
                {
                    builder.Column<string?>(1).AppendNull();
                }
                else
                {
                    builder.Column<string?>(1).Append($"n{999 - i:D3}");
                }
            }

            await writer.WriteAsync(builder, Ct);
            await writer.CompleteAsync(Ct);
        }

        await using ParquetFile file = await ParquetFile.OpenAsync(_path, Ct);
        byte[] bytes = await System.IO.File.ReadAllBytesAsync(_path, Ct);
        ColumnChunkMetadata ids = file.Footer.Chunk(0, 0);
        ColumnIndex index = ColumnIndex.Read(bytes.AsMemory((int)ids.ColumnIndexOffset, ids.ColumnIndexLength), 10);
        Assert.Equal(BoundaryOrder.Ascending, index.Order);
        for (int page = 0; page < 10; page++)
        {
            Assert.False(index.NullPages[page]);
            Assert.Equal(page * 100L, BitConverter.ToInt64(index.Min(page)));
            Assert.Equal(page * 100L + 99, BitConverter.ToInt64(index.Max(page)));
            Assert.Equal(0, index.NullCounts![page]);
        }

        // The names fall, their fourth page all null.
        ColumnChunkMetadata names = file.Footer.Chunk(0, 1);
        ColumnIndex named = ColumnIndex.Read(bytes.AsMemory((int)names.ColumnIndexOffset, names.ColumnIndexLength), 10);
        Assert.Equal(BoundaryOrder.Descending, named.Order);
        Assert.True(named.NullPages[3]);
        Assert.Equal(100, named.NullCounts![3]);
        Assert.Equal("n999", Encoding.UTF8.GetString(named.Max(0)));
        Assert.Equal("n900", Encoding.UTF8.GetString(named.Min(0)));
    }

    [Fact]
    public async Task BoundsAPageOfCodesAsItsValues()
    {
        // Pages of codes into a dictionary of few entries: some rows null, a page all null, words
        // longer than a bound among them, and new words in the later pages and row groups.
        string[] words = new string[40];
        for (int w = 0; w < words.Length; w++)
        {
            words[w] = w % 7 == 0 ? new string((char)('a' + w % 26), 70) + w : $"w{w * 37 % 100:D2}";
        }

        const int Rows = 3_000;
        string?[] names = new string?[Rows];
        for (int i = 0; i < Rows; i++)
        {
            names[i] = i is >= 600 and < 700 || i % 11 == 0 ? null : words[i * 7 % Math.Min(words.Length, 5 + (i % 1_500 / 100))];
        }

        VortexSchema schema = [("name", VortexType.Utf8.Nullable)];
        await using (ParquetFileWriter writer = VortexSession.Default.CreateParquetWriter(_path, schema, new ParquetWriteOptions { BlockRows = 100, RowGroupRows = 1_500 }))
        {
            ColumnsBuilder builder = writer.Builder();
            foreach (string? name in names)
            {
                if (name is null)
                {
                    builder.Column<string?>(0).AppendNull();
                }
                else
                {
                    builder.Column<string?>(0).Append(name);
                }
            }

            await writer.WriteAsync(builder, Ct);
            await writer.CompleteAsync(Ct);
        }

        byte[] bytes = await System.IO.File.ReadAllBytesAsync(_path, Ct);
        await using ParquetFile file = await ParquetFile.OpenAsync(_path, Ct);
        for (int group = 0; group < 2; group++)
        {
            ColumnChunkMetadata chunk = file.Footer.Chunk(group, 0);
            Assert.True(chunk.DictionaryPageOffset >= 0, "the names are coded");
            ColumnIndex index = ColumnIndex.Read(bytes.AsMemory((int)chunk.ColumnIndexOffset, chunk.ColumnIndexLength), 15);
            for (int page = 0; page < 15; page++)
            {
                int first = (group * 1_500) + (page * 100);
                byte[][] values = [.. names.AsSpan(first, 100).ToArray().OfType<string>().Select(Encoding.UTF8.GetBytes)];
                Assert.Equal(100 - values.Length, index.NullCounts![page]);
                Assert.Equal(values.Length == 0, index.NullPages[page]);
                if (values.Length > 0)
                {
                    (byte[] min, byte[] max) = Bounds(values, StatisticsDomain.Utf8);
                    Assert.Equal(min, index.Min(page));
                    Assert.Equal(max, index.Max(page));
                }
            }
        }
    }

    [Fact]
    public void CutsALongBoundAndRaisesItWhereItCan()
    {
        // Bytes: the last unit short of 0xFF is raised, those after it dropped.
        byte[] climbing = new byte[80];
        climbing.AsSpan(0, 63).Fill(0x41);
        climbing.AsSpan(63).Fill(0xFF);
        (byte[] min, byte[] max, bool minExact, bool maxExact) = Bound(StatisticsDomain.Binary, climbing);
        Assert.Equal(climbing[..64], min);
        Assert.False(minExact);
        Assert.Equal([.. climbing[..62], (byte)0x42], max);
        Assert.False(maxExact);

        // Every unit 0xFF: nothing in the prefix can be raised, and the value is the bound.
        byte[] full = new byte[80];
        full.AsSpan().Fill(0xFF);
        (_, max, _, maxExact) = Bound(StatisticsDomain.Binary, full);
        Assert.Equal(full, max);
        Assert.True(maxExact);

        // Text: a code point is never cut, and one below the surrogates is raised past them.
        byte[] text = Encoding.UTF8.GetBytes(new string('a', 61) + char.ConvertFromUtf32(0xD7FF) + new string('z', 20));
        (min, max, _, _) = Bound(StatisticsDomain.Utf8, text);
        Assert.Equal(new string('a', 61) + char.ConvertFromUtf32(0xD7FF), Encoding.UTF8.GetString(min));
        Assert.Equal(new string('a', 61) + char.ConvertFromUtf32(0xE000), Encoding.UTF8.GetString(max));

        // The greatest code point throughout: written whole.
        byte[] top = Encoding.UTF8.GetBytes(string.Concat(System.Linq.Enumerable.Repeat(char.ConvertFromUtf32(0x10FFFF), 20)));
        (_, max, _, maxExact) = Bound(StatisticsDomain.Utf8, top);
        Assert.Equal(top, max);
        Assert.True(maxExact);
    }

    [Fact]
    public void BoundsByteArraysInTheOrderOfTheirBytes()
    {
        // Values whose first eight bytes tie, zeros past a shorter one's end, values of eight bytes
        // and of nine: what a prefix decides and what it leaves to the bytes.
        byte[][] values =
        [
            [], [0], [0, 0], "a"u8.ToArray(), [0x61, 0], [0x61, 0, 0, 0, 0, 0, 0, 0], [0x61, 0, 0, 0, 0, 0, 0, 0, 0],
            "ab"u8.ToArray(), "abcdefgh"u8.ToArray(), [.. "abcdefgh"u8, 0], "abcdefghi"u8.ToArray(), [.. "abcdefgg"u8, 0xFF],
            [0xFF], [0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF], [0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0], "b"u8.ToArray(),
        ];
        Comparison<byte[]> order = (a, b) => a.AsSpan().SequenceCompareTo(b);
        Random random = new(17);
        for (int trial = 0; trial < 500; trial++)
        {
            byte[][] page = new byte[random.Next(1, 12)][];
            for (int i = 0; i < page.Length; i++)
            {
                page[i] = values[random.Next(values.Length)];
            }

            byte[][] sorted = [.. page];
            Array.Sort(sorted, order);
            (byte[] min, byte[] max) = Bounds(page, StatisticsDomain.Binary);
            Assert.Equal(sorted[0], min);
            Assert.Equal(sorted[^1], max);
        }
    }

    /// <summary>The bounds of a page of byte arrays, PLAIN as the writer stages them.</summary>
    private static (byte[] Min, byte[] Max) Bounds(byte[][] page, StatisticsDomain domain)
    {
        WriteColumn column = new() { Name = "v", Path = ["v"], Physical = PhysicalType.ByteArray, Conversion = ValueConversion.ByteArray, Domain = domain };
        ChunkStatistics statistics = new(column);
        using MemoryStream plain = new();
        foreach (byte[] value in page)
        {
            plain.Write(BitConverter.GetBytes(value.Length));
            plain.Write(value);
        }

        statistics.AddPage(plain.ToArray(), page.Length, 0);
        WrittenStatistics written = statistics.Close();
        return (written.Min, written.Max);
    }

    /// <summary>The bounds a page of one byte array value gets.</summary>
    private static (byte[] Min, byte[] Max, bool MinExact, bool MaxExact) Bound(StatisticsDomain domain, byte[] value)
    {
        WriteColumn column = new() { Name = "v", Path = ["v"], Physical = PhysicalType.ByteArray, Conversion = ValueConversion.ByteArray, Domain = domain };
        ChunkStatistics statistics = new(column);
        byte[] plain = new byte[4 + value.Length];
        BitConverter.TryWriteBytes(plain, value.Length);
        value.CopyTo(plain, 4);
        statistics.AddPage(plain, 1, 0);
        WrittenStatistics written = statistics.Close();
        return (written.Min, written.Max, written.MinExact, written.MaxExact);
    }

    private static long CountNans(int rows)
    {
        long nans = 0;
        for (int i = 0; i < rows; i++)
        {
            nans += i % 10 != 0 && i % 7 == 0 ? 1 : 0;
        }

        return nans;
    }

    private static void AssertBounds(ZoneBounds bounds, FilterLiteral min, FilterLiteral max, long nulls)
    {
        Assert.True(bounds.HasMin && bounds.HasMax);
        Assert.True(ZonePrunerCompare(bounds.Min, min) == 0, $"min {Describe(bounds.Min)} is not {Describe(min)}");
        Assert.True(ZonePrunerCompare(bounds.Max, max) == 0, $"max {Describe(bounds.Max)} is not {Describe(max)}");
        Assert.True(bounds.HasNullCount);
        Assert.Equal(nulls, bounds.NullCount);
    }

    private static string Describe(FilterLiteral literal) => literal.Kind switch
    {
        FilterLiteralKind.Signed => $"signed {literal.SignedValue}",
        FilterLiteralKind.Unsigned => $"unsigned {literal.UnsignedValue}",
        FilterLiteralKind.Float => $"float {literal.FloatValue}",
        FilterLiteralKind.Bool => $"bool {literal.BoolValue}",
        FilterLiteralKind.Bytes => $"bytes {Convert.ToHexString(literal.BytesValue)}",
        _ => "null",
    };

    private static int ZonePrunerCompare(FilterLiteral a, FilterLiteral b) =>
        Vorticity.Compute.ZonePruner.TryCompare(a, b, out int order) ? order : int.MinValue;
}
