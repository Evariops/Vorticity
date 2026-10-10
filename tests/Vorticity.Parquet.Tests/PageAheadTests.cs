using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Expressions;
using Xunit;

namespace Vorticity.Parquet.Tests;

/// <summary>
/// A scan given lanes decompresses each column's next pages on them while it reads the pages before:
/// the rows are the ones a scan on one lane reads, under every codec that compresses and both page
/// versions, with nulls, dictionaries and text; a scan that stops early leaves no page decompressing
/// behind it, and a skip over pages decompressed ahead drops them.
/// </summary>
public sealed class PageAheadTests : IDisposable
{
    private const int Rows = 40_000;

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"vorticity-ahead-{Guid.NewGuid():N}.parquet");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static VortexSchema Schema =>
    [
        ("id", VortexType.Int64),
        ("price", VortexType.Float64.Nullable),
        ("label", VortexType.Utf8.Nullable),
        ("flag", VortexType.Bool.Nullable),
    ];

    public void Dispose() => System.IO.File.Delete(_path);

    [Theory]
    [InlineData(ParquetCompression.Gzip, DataPageVersion.V1)]
    [InlineData(ParquetCompression.Gzip, DataPageVersion.V2)]
    [InlineData(ParquetCompression.Zstd, DataPageVersion.V1)]
    [InlineData(ParquetCompression.Zstd, DataPageVersion.V2)]
    [InlineData(ParquetCompression.Snappy, DataPageVersion.V2)]
    [InlineData(ParquetCompression.Lz4Raw, DataPageVersion.V1)]
    public async Task ReadsOnLanesTheRowsOneLaneReads(ParquetCompression compression, DataPageVersion version)
    {
        await WriteAsync(compression, version);
        (List<string> one, long aheadOnOne) = await CountedRowsAsync(1);
        (List<string> four, long aheadOnFour) = await CountedRowsAsync(4);
        Assert.Equal(Rows, one.Count);
        Assert.Equal(one, four);

        // On one lane no page is taken ahead; on four, pages are.
        Assert.Equal(0, aheadOnOne);
        Assert.True(aheadOnFour > 0, "No page was taken ahead on four lanes.");

        // A filter and a take step over pages a lane may have decompressed ahead.
        VortexExpr band = Expr.And(Expr.Ge(Expr.Field("id"), Expr.Literal(FilterLiteral.From(12_000L))), Expr.Lt(Expr.Field("id"), Expr.Literal(FilterLiteral.From(13_500L))));
        Assert.Equal(await RowsAsync(1, scan => scan.Where(band)), await RowsAsync(4, scan => scan.Where(band)));
        long[] spread = [.. Enumerable.Range(0, 40).Select(i => (long)i * 997)];
        Assert.Equal(await RowsAsync(1, scan => scan.Rows(spread)), await RowsAsync(4, scan => scan.Rows(spread)));
    }

    [Fact]
    public async Task AScanStoppedEarlyLeavesNoPageBehind()
    {
        await WriteAsync(ParquetCompression.Gzip, DataPageVersion.V2);
        await using VortexSession session = VortexSession.Create(options => options.MaxDegreeOfParallelism = 4);
        for (int stop = 1; stop <= 3; stop++)
        {
            await using ParquetFile file = await session.OpenParquetAsync(_path, null, Ct);
            int seen = 0;
            await foreach (RecordBatch batch in file.Scan().ToBatchesAsync(Ct))
            {
                using (batch)
                {
                    if (++seen == stop)
                    {
                        break;
                    }
                }
            }

            Assert.Equal(stop, seen);
        }

        // The pages a stopped scan decompressed ahead went back: the file reads whole after it.
        Assert.Equal(await RowsAsync(1, scan => scan), await RowsAsync(4, scan => scan));
    }

    /// <summary>Every row of the file on <paramref name="degree"/> lanes, and the pages its columns took ahead.</summary>
    private async Task<(List<string> Rows, long Ahead)> CountedRowsAsync(int degree)
    {
        await using VortexSession session = VortexSession.Create(options => options.MaxDegreeOfParallelism = degree);
        await using ParquetFile file = await session.OpenParquetAsync(_path, null, Ct);
        List<string> rows = await RowsAsync(file.Scan().ToBatchesAsync(Ct));
        return (rows, file.Counters.Ahead);
    }

    /// <summary>Every row the scan <paramref name="shape"/> makes of the file, on <paramref name="degree"/> lanes, each value rendered.</summary>
    private async Task<List<string>> RowsAsync(int degree, Func<Scan, Scan> shape)
    {
        await using VortexSession session = VortexSession.Create(options => options.MaxDegreeOfParallelism = degree);
        await using ParquetFile file = await session.OpenParquetAsync(_path, null, Ct);
        return await RowsAsync(shape(file.Scan()).ToBatchesAsync(Ct));
    }

    /// <summary>Every row of the batches, each value rendered.</summary>
    private static async Task<List<string>> RowsAsync(IAsyncEnumerable<RecordBatch> batches)
    {
        List<string> rows = [];
        await foreach (RecordBatch batch in batches)
        {
            using (batch)
            {
                for (int r = 0; r < batch.RowCount; r++)
                {
                    rows.Add(string.Join(" | ", Enumerable.Range(0, batch.Schema.Count).Select(c => Render.Row(batch, c, r))));
                }
            }
        }

        return rows;
    }

    /// <summary>The rows, pages of a thousand of them in a row group of them all, so that every column has forty pages to decompress ahead.</summary>
    private async Task WriteAsync(ParquetCompression compression, DataPageVersion version)
    {
        ParquetWriteOptions options = new() { Compression = compression, DataPageVersion = version, BlockRows = 1_000, RowGroupRows = Rows };
        await using ParquetFileWriter writer = VortexSession.Default.CreateParquetWriter(_path, Schema, options);
        ColumnsBuilder builder = writer.Builder();
        for (int row = 0; row < Rows; row++)
        {
            builder.Column<long>(0).Append(row);
            if (row % 7 == 0)
            {
                builder.Column<double?>(1).AppendNull();
            }
            else
            {
                builder.Column<double?>(1).Append((row * 7919 % 10_007) / 4.0);
            }

            if (row % 11 == 0)
            {
                builder.Column<string?>(2).AppendNull();
            }
            else
            {
                builder.Column<string?>(2).Append(string.Create(CultureInfo.InvariantCulture, $"label-{row % 37}"));
            }

            if (row % 5 == 0)
            {
                builder.Column<bool?>(3).AppendNull();
            }
            else
            {
                builder.Column<bool?>(3).Append(row % 3 == 0);
            }
        }

        await writer.WriteAsync(builder, Ct);
        await writer.CompleteAsync(Ct);
    }
}
