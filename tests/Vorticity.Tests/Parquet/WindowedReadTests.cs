using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Expressions;
using Vorticity.Parquet;
using Xunit;

namespace Vorticity.Tests.Parquet;

/// <summary>
/// A large row group read from a source that does not read in place, in windows of batches each read
/// while the one before it is decoded: the same rows as the mapped file, whole, filtered and over a
/// range, the plan counting the requests the scan makes, and the first batch waiting for its own
/// pages rather than its group's.
/// </summary>
public sealed class WindowedReadTests : IDisposable
{
    // A row group of a million rows in blocks of 8 192 passed the 4 MiB past which a read is cut into windows: the same
    // cuts on a group 64 times smaller, in blocks a 128th of it as before, under thresholds 64 times smaller. The large
    // group is four times the threshold and the small one a quarter of it, and the windows double from a batch of a block
    // until they hold half the largest window, at either size.
    private const int Rows = 1 << 14;

    private const int BlockRows = Rows / 128;

    private static readonly ParquetOpenOptions Windowed = new ParquetOpenOptions
    {
        WindowedBytes = (4L << 20) / 64,
        MaxWindowBytes = (8L << 20) / 64,
    };

    // A batch of a block, as a scan's 8 192 rows were of a group in blocks of 8 192.
    private static readonly ScanOptions Batches = new ScanOptions { BatchRows = BlockRows };

    private readonly List<string> _paths = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        foreach (string path in _paths)
        {
            System.IO.File.Delete(path);
        }
    }

    /// <remarks>
    /// One page format: the windows are cut on the pages' places and take each page whole, so a page decodes the same
    /// from a window as from the mapped file, whatever its version and its codec, which the other tests of the format read.
    /// </remarks>
    [Fact]
    public async Task AGroupReadInWindowsIsTheMappedFilesRows()
    {
        string path = await WriteAsync(new ParquetWriteOptions { Compression = ParquetCompression.Zstd, DataPageVersion = DataPageVersion.V2, RowGroupRows = Rows, BlockRows = BlockRows });
        VortexExpr filter = Expr.Or(
            Expr.Lt(Expr.Field("id"), Expr.Literal(FilterLiteral.From(312L))),
            Expr.And(Expr.Ge(Expr.Field("id"), Expr.Literal(FilterLiteral.From(10_937L))), Expr.Lt(Expr.Field("id"), Expr.Literal(FilterLiteral.From(11_422L)))));
        foreach (Func<Vorticity.Scan, Vorticity.Scan> shape in (Func<Vorticity.Scan, Vorticity.Scan>[])[scan => scan, scan => scan.Where(filter), scan => scan.Rows(new RowRange(4_690, 8_005))])
        {
            (UInt128[] mapped, ScanMetrics whole) = await ScanAsync(path, mapFiles: true, shape);
            (UInt128[] windowed, ScanMetrics read) = await ScanAsync(path, mapFiles: false, shape);
            Assert.Equal(mapped.Length, windowed.Length);
            Assert.Equal(mapped, windowed);

            // The pages the batches need, in windows: a request per window and chunk, past the one the
            // mapped file asks of each chunk. The bytes past the mapped file's are the offset indexes the
            // windows are cut by, a few bytes a page, 128 pages a chunk at either size.
            Assert.True(read.Requests > whole.Requests, $"{read.Requests} requests against {whole.Requests}");
            Assert.True(read.BytesRequested <= whole.BytesRequested + (8 << 10), $"{read.BytesRequested} bytes against {whole.BytesRequested}");
        }
    }

    /// <summary>
    /// A group sixteen times another's: its first batch asks for no more than the smaller group does
    /// whole, which a read in one request would have made it wait for sixteen times over.
    /// </summary>
    [Fact]
    public async Task TheFirstBatchWaitsForItsPagesNotItsGroup()
    {
        string small = await WriteAsync(new ParquetWriteOptions { RowGroupRows = Rows / 16, BlockRows = BlockRows });
        string large = await WriteAsync(new ParquetWriteOptions { RowGroupRows = Rows, BlockRows = BlockRows });
        (long smallFirst, long smallGroup) = await FirstBatchAsync(small);
        (long largeFirst, long largeGroup) = await FirstBatchAsync(large);
        Assert.True(largeGroup > 8 * smallGroup, $"groups of {largeGroup} and {smallGroup} bytes");
        Assert.True(largeFirst <= smallFirst, $"the first batch of the large group asked for {largeFirst} bytes, of the small one {smallFirst}");
        Assert.True(largeFirst * 8 < largeGroup, $"the first batch asked for {largeFirst} bytes of its group's {largeGroup}");
    }

    /// <summary>The bytes a scan from a source that does not read in place has asked for once its first batch is out, and the bytes of the file's first group's chunks.</summary>
    private static async Task<(long First, long Group)> FirstBatchAsync(string path)
    {
        await using VortexSession session = VortexSession.Create(options => options.MapFiles = false);
        await using ParquetFile file = await ParquetFile.OpenAsync(path, Windowed, session, Ct);
        long group = 0;
        for (int column = 0; column < file.Compiled.Columns.Length; column++)
        {
            group += file.ChunkRange(file.Footer.Chunk(0, column)).Length;
        }

        Vorticity.Scan scan = file.Scan().With(Batches);
        await using IAsyncEnumerator<RecordBatch> batches = scan.ToBatchesAsync(Ct).GetAsyncEnumerator(Ct);
        Assert.True(await batches.MoveNextAsync());
        long first = scan.Metrics.BytesRequested;
        batches.Current.Dispose();
        return (first, group);
    }

    /// <summary>The rows a scan of the file gives, each by the digest of its values, and what the scan asked of its source.</summary>
    private static async Task<(UInt128[] Rows, ScanMetrics Metrics)> ScanAsync(string path, bool mapFiles, Func<Vorticity.Scan, Vorticity.Scan> shape)
    {
        await using VortexSession session = VortexSession.Create(options => options.MapFiles = mapFiles);
        await using ParquetFile file = await ParquetFile.OpenAsync(path, Windowed, session, Ct);
        ScanPlan plan = await shape(file.Scan().With(Batches)).ExplainAsync(analyze: true, Ct);
        Vorticity.Scan scan = shape(file.Scan().With(Batches));
        List<UInt128> rows = [];
        List<UInt128> digests = [];
        await foreach (RecordBatch batch in scan.ToBatchesAsync(Ct))
        {
            using (batch)
            {
                digests.Clear();
                Writing.Values.DigestRows(batch, digests);
                for (int r = 0; r < batch.RowCount; r++)
                {
                    if (batch.SelectionWords.IsEmpty || ((batch.SelectionWords[r >> 6] >> (r & 63)) & 1) != 0)
                    {
                        rows.Add(digests[r]);
                    }
                }
            }
        }

        // The plan counts what the scan asks of its source, window for window.
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
        string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"vx-windows-{Guid.NewGuid():N}.parquet");
        _paths.Add(path);
        await using ParquetFileWriter writer = VortexSession.Default.CreateParquetWriter(path, schema, options);
        ColumnsBuilder builder = writer.Builder();
        Random random = new(43);
        for (int i = 0; i < Rows; i++)
        {
            builder.Column<long>(0).Append(i);
            if (i % 11 == 0)
            {
                builder.Column<double?>(1).AppendNull();
            }
            else
            {
                builder.Column<double?>(1).Append(random.NextDouble());
            }

            builder.Column<string>(2).Append($"label-{random.Next(500)}");
            ColumnBuilder<ReadOnlyMemory<int>> tags = builder.Column<ReadOnlyMemory<int>>(3);
            tags.BeginList();
            for (int t = 0; t < i % 4; t++)
            {
                tags.Elements.Append(random.Next());
            }

            tags.EndList();
            if ((i + 1) % 1_024 == 0)
            {
                await writer.WriteAsync(builder, Ct);
            }
        }

        await writer.WriteAsync(builder, Ct);
        await writer.CompleteAsync(Ct);
        return path;
    }
}
