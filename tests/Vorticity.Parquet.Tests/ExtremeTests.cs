using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Expressions;
using Vorticity.Parquet.Schema;
using Vorticity.Scanning;
using Xunit;

namespace Vorticity.Parquet.Tests;

/// <summary>
/// A column's least and greatest values answered from the footer where every row group bounds them
/// exactly, with no read, and from the rows otherwise; the two answers the same on every real file.
/// </summary>
public sealed partial class ExtremeTests : IDisposable
{
    private const int Rows = 40_000;

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"vorticity-extreme-{Guid.NewGuid():N}.parquet");

    [VortexRecord]
    public partial record struct Measure(long Id, string Name, double Score, decimal Price, int? Sparse, string Long);

    private static string? Root => Environment.GetEnvironmentVariable("VORTICITY_PARQUET_DATA");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => System.IO.File.Delete(_path);

    [Fact]
    public async Task AnswersFromTheFooterWhereItsBoundsAreExact()
    {
        Measure[] rows = await WriteAsync(withZero: false);
        await using ParquetFile file = await ParquetFile.OpenAsync(_path, Ct);

        // Every row group bounds these exactly: the footer answers, and nothing is read.
        Assert.Equal(rows.Min(r => r.Id), await FooterAsync(file, p => p.Id, min: true));
        Assert.Equal(rows.Max(r => r.Id), await FooterAsync(file, p => p.Id, min: false));
        Assert.Equal(rows.Select(r => r.Name).Min(StringComparer.Ordinal), await FooterAsync(file, p => p.Name, min: true));
        Assert.Equal(rows.Select(r => r.Name).Max(StringComparer.Ordinal), await FooterAsync(file, p => p.Name, min: false));
        Assert.Equal(rows.Min(r => r.Score), await FooterAsync(file, p => p.Score, min: true));
        Assert.Equal(rows.Max(r => r.Price), await FooterAsync(file, p => p.Price, min: false));

        // A row group of nulls alone adds nothing to the column's bounds.
        Assert.Equal(rows.Min(r => r.Sparse), await FooterAsync(file, p => p.Sparse, min: true));
        Assert.Equal(rows.Max(r => r.Sparse), await FooterAsync(file, p => p.Sparse, min: false));

        // Bounds cut at 64 bytes are not the extremes: the rows are read.
        Scan<Measure> cut = file.Scan<Measure>();
        Assert.Equal(rows.Select(r => r.Long).Max(StringComparer.Ordinal), await cut.MaxAsync(p => p.Long, Ct));
        Assert.True(cut.Metrics.Requests > 0);

        // A filter, or a scan that prunes nothing, reads the rows.
        Scan<Measure> filtered = file.Scan<Measure>().Where(p => p.Id < 1_000);
        Assert.Equal(rows.Where(r => r.Id < 1_000).Max(r => r.Score), await filtered.MaxAsync(p => p.Score, Ct));
        Assert.True(filtered.Metrics.Requests > 0);
        Scan<Measure> whole = file.Scan<Measure>().With(new ScanOptions { UseStatistics = false });
        Assert.Equal(rows.Max(r => r.Id), await whole.MaxAsync(p => p.Id, Ct));
        Assert.True(whole.Metrics.Requests > 0);
    }

    [Fact]
    public async Task ReadsTheRowsForAFloatsZero()
    {
        // Which zero comes first is the rows' to say: -0 sorts below +0 in the bounds, and ties in the data.
        Measure[] rows = await WriteAsync(withZero: true);
        await using ParquetFile file = await ParquetFile.OpenAsync(_path, Ct);
        Scan<Measure> scan = file.Scan<Measure>();
        double min = await scan.MinAsync(p => p.Score, Ct);
        Assert.Equal(0.0, min);
        Assert.False(double.IsNegative(min));
        Assert.True(scan.Metrics.Requests > 0);
    }

    [Fact]
    public async Task AnswersAsTheRowsDoOnRealFiles()
    {
        Assert.SkipWhen(Root is null || !Directory.Exists(Root), "VORTICITY_PARQUET_DATA names no directory of Parquet files.");
        List<string> failures = [];
        int answered = 0;
        foreach (string path in Directory.EnumerateFiles(Root!, "*.parquet", SearchOption.AllDirectories))
        {
            ParquetFile file;
            try
            {
                file = await ParquetFile.OpenAsync(path, Ct);
            }
            catch (Exception e) when (e is ParquetUnsupportedException or ParquetFormatException)
            {
                continue;
            }

            await using (file)
            {
                foreach (ParquetColumn column in file.Compiled.Columns)
                {
                    if (column.MaxRepetitionLevel > 0 || column.Form == LeafForm.Null)
                    {
                        continue;
                    }

                    FieldExpr field = new(column.DottedPath);
                    foreach (bool min in (bool[])[true, false])
                    {
                        FilterLiteral footer;
                        FilterLiteral data;
                        ScanCounters metrics = new();
                        try
                        {
                            footer = await file.Source.ExtremeAsync(new ScanSpec(), field, min, metrics, Ct);
                            data = await file.Source.ExtremeAsync(new ScanSpec { Options = new ScanOptions { UseStatistics = false } }, field, min, new ScanCounters(), Ct);
                        }
                        catch (Exception e) when (e is ArgumentException or NotSupportedException or VortexSchemaException or ParquetUnsupportedException or ParquetFormatException)
                        {
                            break;
                        }

                        answered += metrics.SegmentRequests == 0 ? 1 : 0;
                        if (!footer.Equals(data))
                        {
                            failures.Add($"{Path.GetRelativePath(Root!, path)}: {(min ? "min" : "max")}({column.DottedPath}) is {footer} from the footer and {data} from the rows");
                        }
                    }
                }
            }
        }

        Assert.True(answered > 0, "no real file's footer answered an extreme");
        Assert.Empty(failures);
    }

    /// <summary>The column's least or greatest value, which the footer answers with no request.</summary>
    private static async Task<T?> FooterAsync<T>(ParquetFile file, Func<Probe<Measure>, Sym<T>> column, bool min)
    {
        Scan<Measure> scan = file.Scan<Measure>();
        T? value = min ? await scan.MinAsync(column, Ct) : await scan.MaxAsync(column, Ct);
        Assert.Equal(0, scan.Metrics.Requests);
        return value;
    }

    private async Task<Measure[]> WriteAsync(bool withZero)
    {
        Random random = new(5);
        Measure[] rows = new Measure[Rows];
        for (int i = 0; i < Rows; i++)
        {
            double score = withZero && i == 12_345 ? 0.0 : withZero && i == 30_000 ? -0.0 : 1 + random.NextDouble();
            int? sparse = i < 16_384 ? null : i % 7 == 0 ? null : random.Next(-500, 500);
            rows[i] = new Measure(random.NextInt64(-1_000_000, 1_000_000), $"name-{random.Next(100_000):D6}", score, random.Next(-99_999, 99_999) / 100m, sparse, new string('x', 70) + random.Next(1_000));
        }

        await using ParquetFileWriter writer = VortexSession.Default.CreateParquetWriter<Measure>(
            _path, new ParquetWriteOptions { RowGroupRows = 8_192, BlockRows = 1_024 });
        await writer.WriteAsync<Measure>(rows, Ct);
        await writer.CompleteAsync(Ct);
        return rows;
    }
}
