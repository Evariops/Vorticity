using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Expressions;
using Xunit;

namespace Vorticity.Parquet.Tests;

/// <summary>
/// A source that reads positionally reads, of each chunk, the pages the batches it keeps need and its
/// dictionary page, by the chunk's offset index: a page the page index or a row range rules out is
/// neither read nor decoded, and the rows are the mapped file's.
/// </summary>
public sealed class SparseReadTests : IDisposable
{
    private const int Rows = 200_000;

    private readonly List<string> _paths = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        foreach (string path in _paths)
        {
            System.IO.File.Delete(path);
        }
    }

    [Theory]
    [InlineData(ParquetCompression.Zstd, DataPageVersion.V2)]
    [InlineData(ParquetCompression.Uncompressed, DataPageVersion.V2)]
    [InlineData(ParquetCompression.Snappy, DataPageVersion.V1)]
    public async Task PagesTheFilterRulesOutAreNeverRead(ParquetCompression compression, DataPageVersion pages)
    {
        string path = await WriteAsync(new ParquetWriteOptions { Compression = compression, DataPageVersion = pages, RowGroupRows = 65_536 });
        VortexExpr filter = Expr.And(
            Expr.Ge(Expr.Field("id"), Expr.Literal(FilterLiteral.From(130_000L))),
            Expr.Lt(Expr.Field("id"), Expr.Literal(FilterLiteral.From(140_000L))));

        (string[][] mapped, ScanMetrics whole) = await ScanAsync(path, mapFiles: true, scan => scan.Where(filter));
        (string[][] positional, ScanMetrics sparse) = await ScanAsync(path, mapFiles: false, scan => scan.Where(filter));
        Assert.Equal(10_000, mapped.Length);
        Assert.Equal(mapped, positional);

        // The filter reaches two row groups of eight pages a column: of each column, their last and
        // first two pages and their dictionary pages, where the mapped file asked for both groups whole;
        // three pages of the file's twenty-five a column.
        long length = new System.IO.FileInfo(path).Length;
        Assert.True(sparse.BytesRequested * 4 < whole.BytesRequested, $"{sparse.BytesRequested} bytes asked against {whole.BytesRequested}");
        Assert.True(sparse.BytesRequested * 6 < length, $"{sparse.BytesRequested} bytes of {length}");
    }

    [Fact]
    public async Task ARangeOfRowsReadsItsPagesAlone()
    {
        string path = await WriteAsync(new ParquetWriteOptions { RowGroupRows = 65_536 });
        RowRange range = new(100_000, 110_000);
        (string[][] mapped, ScanMetrics whole) = await ScanAsync(path, mapFiles: true, scan => scan.Rows(range));
        (string[][] positional, ScanMetrics sparse) = await ScanAsync(path, mapFiles: false, scan => scan.Rows(range));
        Assert.Equal(10_000, mapped.Length);
        Assert.Equal(mapped, positional);

        // Pages 4 and 5 of the group's eight a column, and its dictionary pages.
        Assert.True(sparse.BytesRequested * 3 < whole.BytesRequested, $"{sparse.BytesRequested} bytes asked against {whole.BytesRequested}");
    }

    /// <summary>Every page the scan reads stays read once, whether pruning keeps few batches or all of them.</summary>
    [Fact]
    public async Task AScanThatKeepsEveryBatchReadsEveryPageOnce()
    {
        string path = await WriteAsync(new ParquetWriteOptions { RowGroupRows = 65_536 });
        VortexExpr everything = Expr.Ge(Expr.Field("id"), Expr.Literal(FilterLiteral.From(0L)));
        (string[][] mapped, ScanMetrics whole) = await ScanAsync(path, mapFiles: true, scan => scan.Where(everything));
        (string[][] positional, ScanMetrics read) = await ScanAsync(path, mapFiles: false, scan => scan.Where(everything));
        Assert.Equal(Rows, mapped.Length);
        Assert.Equal(mapped, positional);
        Assert.Equal(whole.BytesRequested, read.BytesRequested);
    }

    private async Task<(string[][] Rows, ScanMetrics Metrics)> ScanAsync(string path, bool mapFiles, Func<Scan, Scan> shape)
    {
        await using VortexSession session = VortexSession.Create(options => options.MapFiles = mapFiles);
        await using ParquetFile file = await ParquetFile.OpenAsync(path, ParquetOpenOptions.Default, session, Ct);
        ScanPlan plan = await shape(file.Scan()).ExplainAsync(Ct);
        Scan scan = shape(file.Scan());
        List<string[]> rows = [];
        await foreach (RecordBatch batch in scan.ToBatchesAsync(Ct))
        {
            using (batch)
            {
                // A range's first and last batches are trimmed to it by the selection.
                for (int r = 0; r < batch.RowCount; r++)
                {
                    if (batch.SelectionWords.IsEmpty || ((batch.SelectionWords[r >> 6] >> (r & 63)) & 1) != 0)
                    {
                        rows.Add([.. Enumerable.Range(0, batch.Schema.Count).Select(c => Render.Row(batch, c, r))]);
                    }
                }
            }
        }

        // The plan counts what the scan asks of its source, range for range.
        Assert.Equal(plan.Segments, scan.Metrics.Requests);
        Assert.Equal(plan.BytesToRead, scan.Metrics.BytesRequested);
        return ([.. rows], scan.Metrics);
    }

    private async Task<string> WriteAsync(ParquetWriteOptions options)
    {
        VortexSchema schema =
        [
            ("id", VortexType.Int64),
            ("value", VortexType.Float64.Nullable),
            ("label", VortexType.Utf8),
            ("tags", VortexType.List(VortexType.Int32)),
        ];
        string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"vx-sparse-{Guid.NewGuid():N}.parquet");
        _paths.Add(path);
        await using ParquetFileWriter writer = VortexSession.Default.CreateParquetWriter(path, schema, options);
        ColumnsBuilder builder = writer.Builder();
        Random random = new(37);
        for (int i = 0; i < Rows; i++)
        {
            builder.Column<long>(0).Append(i);
            if (i % 9 == 0)
            {
                builder.Column<double?>(1).AppendNull();
            }
            else
            {
                builder.Column<double?>(1).Append(random.NextDouble());
            }

            builder.Column<string>(2).Append($"label-{random.Next(50)}");
            ColumnBuilder<ReadOnlyMemory<int>> tags = builder.Column<ReadOnlyMemory<int>>(3);
            tags.BeginList();
            for (int t = 0; t < i % 3; t++)
            {
                tags.Elements.Append(i + t);
            }

            tags.EndList();
        }

        await writer.WriteAsync(builder, Ct);
        await writer.CompleteAsync(Ct);
        return path;
    }
}
