using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Expressions;
using Xunit;

namespace Vorticity.Parquet.Tests;

/// <summary>
/// A scan of enough fields on several lanes decodes each batch's fields side by side, each into an
/// arena of its own that the batch references: the rows are the ones a scan on one lane reads, with
/// nulls, dictionaries, text past a view's inline bytes, lists and decimals, and batches that span
/// pages and row groups; a filter and a take that step over rows read the same.
/// </summary>
public sealed partial class FieldDecodeTests : IDisposable
{
    private const int Rows = 30_000;

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"vorticity-fields-{Guid.NewGuid():N}.parquet");

    [VortexRecord]
    public partial record struct Visit(
        long Id, double? Price, string? Label, bool? Flag, string Page, int[] Tags, decimal Amount, long? Sparse, string Note, int Day);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => System.IO.File.Delete(_path);

    [Theory]
    [InlineData(ParquetCompression.Snappy, DataPageVersion.V2)]
    [InlineData(ParquetCompression.Zstd, DataPageVersion.V1)]
    public async Task ReadsSideBySideTheRowsOneLaneReads(ParquetCompression compression, DataPageVersion version)
    {
        Visit[] visits = Visits();
        ParquetWriteOptions options = new() { Compression = compression, DataPageVersion = version, BlockRows = 1_000, RowGroupRows = 12_000 };
        await using (ParquetFileWriter writer = VortexSession.Default.CreateParquetWriter<Visit>(_path, options))
        {
            await writer.WriteAsync<Visit>(visits, Ct);
            await writer.CompleteAsync(Ct);
        }

        List<string> one = await RowsAsync(1, scan => scan);
        Assert.Equal(Rows, one.Count);
        Assert.Equal(one, await RowsAsync(4, scan => scan));

        VortexExpr band = Expr.And(Expr.Ge(Expr.Field("Id"), Expr.Literal(FilterLiteral.From(9_000L))), Expr.Lt(Expr.Field("Id"), Expr.Literal(FilterLiteral.From(14_500L))));
        Assert.Equal(await RowsAsync(1, scan => scan.Where(band)), await RowsAsync(4, scan => scan.Where(band)));
        long[] spread = [.. Enumerable.Range(0, 60).Select(i => (long)i * 499)];
        Assert.Equal(await RowsAsync(1, scan => scan.Rows(spread)), await RowsAsync(4, scan => scan.Rows(spread)));
    }

    [Fact]
    public async Task AScanStoppedWhileBatchesDecodeAheadLeavesNothingBehind()
    {
        Visit[] visits = Visits();
        await using (ParquetFileWriter writer = VortexSession.Default.CreateParquetWriter<Visit>(_path, new ParquetWriteOptions { BlockRows = 1_000, RowGroupRows = 12_000 }))
        {
            await writer.WriteAsync<Visit>(visits, Ct);
            await writer.CompleteAsync(Ct);
        }

        // Stopped at a batch while the fields decode those after it, in the first row group and in
        // the second: the pages they hold go back, and the file then reads whole.
        await using (VortexSession session = VortexSession.Create(options => options.MaxDegreeOfParallelism = 4))
        {
            foreach (int stop in (int[])[1, 2, 3, 14])
            {
                await using ParquetFile file = await session.OpenParquetAsync(_path, null, Ct);
                int seen = 0;
                await foreach (RecordBatch batch in file.Scan().With(new ScanOptions { BatchRows = 1_000 }).ToBatchesAsync(Ct))
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
        }

        Assert.Equal(await RowsAsync(1, scan => scan), await RowsAsync(4, scan => scan));
    }

    /// <summary>Every row the scan <paramref name="shape"/> makes of the file, on <paramref name="degree"/> lanes, each value rendered.</summary>
    private async Task<List<string>> RowsAsync(int degree, Func<Scan, Scan> shape)
    {
        await using VortexSession session = VortexSession.Create(options => options.MaxDegreeOfParallelism = degree);
        await using ParquetFile file = await session.OpenParquetAsync(_path, null, Ct);
        List<string> rows = [];
        await foreach (RecordBatch batch in shape(file.Scan()).ToBatchesAsync(Ct))
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

    private static Visit[] Visits()
    {
        Random random = new(41);
        Visit[] visits = new Visit[Rows];
        for (int i = 0; i < Rows; i++)
        {
            visits[i] = new Visit(
                i,
                i % 7 == 0 ? null : Math.Round(random.NextDouble() * 500, 2),
                i % 11 == 0 ? null : $"label-{i % 23}",
                i % 5 == 0 ? null : i % 3 == 0,
                $"https://example.com/{random.Next(400)}/a/page/of/many/more/bytes/than/a/view/holds",
                [.. Enumerable.Range(0, random.Next(4)).Select(t => random.Next(100))],
                random.Next(-100_000, 100_000) / 100m,
                i % 97 == 0 ? i : null,
                i % 3 == 0 ? "short" : $"note {random.Next()}",
                i / 1_000);
        }

        return visits;
    }
}
