using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Vorticity.Parquet.Tests;

/// <summary>
/// A scan of enough flat fields on several lanes decodes a row group's batches ahead of its read, a
/// field at a time: the rows are the ones a scan on one lane reads, over row groups and batches that
/// span pages; a scan stopped while fields decode ahead leaves nothing behind it; and a field that
/// fails ahead fails the scan at the batch that reaches it.
/// </summary>
public sealed partial class FieldPipelineTests : IDisposable
{
    private const int Rows = 30_000;

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"vorticity-pipeline-{Guid.NewGuid():N}.parquet");

    [VortexRecord]
    public partial record struct Trip(
        long Id, double? Fare, string? Zone, bool? Paid, string Route, decimal Toll, long? Sparse, string Note, int Day);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        System.IO.File.Delete(_path);
        System.IO.File.Delete(_path + ".altered");
    }

    [Theory]
    [InlineData(ParquetCompression.Snappy, DataPageVersion.V2)]
    [InlineData(ParquetCompression.Zstd, DataPageVersion.V1)]
    [InlineData(ParquetCompression.Gzip, DataPageVersion.V2)]
    public async Task ReadsAheadTheRowsOneLaneReads(ParquetCompression compression, DataPageVersion version)
    {
        await WriteAsync(new ParquetWriteOptions { Compression = compression, DataPageVersion = version, BlockRows = 1_000, RowGroupRows = 12_000 });
        (List<string> one, long pipedOnOne) = await RowsAsync(1, batchRows: 0);
        Assert.Equal(Rows, one.Count);
        Assert.Equal(0, pipedOnOne);
        foreach ((int degree, int batchRows) in ((int, int)[])[(4, 0), (8, 1_000), (4, 777)])
        {
            (List<string> ahead, long piped) = await RowsAsync(degree, batchRows);
            Assert.Equal(one, ahead);
            Assert.True(piped > 0, $"No batch was decoded ahead on {degree} lanes.");
        }
    }

    [Fact]
    public async Task AScanStoppedWhileFieldsDecodeAheadLeavesNothingBehind()
    {
        await WriteAsync(new ParquetWriteOptions { BlockRows = 1_000, RowGroupRows = 12_000 });

        // Stopped at a batch while the fields decode those after it, in each row group: the pages
        // they hold go back, and the file then reads whole.
        await using (VortexSession session = VortexSession.Create(options => options.MaxDegreeOfParallelism = 4))
        {
            foreach (int stop in (int[])[1, 2, 3, 14, 25])
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

        Assert.Equal((await RowsAsync(1, 0)).Rows, (await RowsAsync(4, 0)).Rows);
    }

    [Fact]
    public async Task AFieldThatFailsAheadFailsTheScanAtTheBatchThatReachesIt()
    {
        await WriteAsync(new ParquetWriteOptions { BlockRows = 1_000, RowGroupRows = 12_000, WriteChecksums = true });

        // A byte of the last page of the second row group's notes altered, in a copy, since the
        // session keeps the file it scanned mapped: its checksum catches it.
        long end;
        await using (ParquetFile file = await ParquetFile.OpenAsync(_path, Ct))
        {
            (long start, int length) = file.ChunkRange(file.Footer.Chunk(1, 7));
            end = start + length;
        }

        byte[] bytes = await System.IO.File.ReadAllBytesAsync(_path, Ct);
        bytes[end - 3] ^= 0x10;
        string altered = _path + ".altered";
        await System.IO.File.WriteAllBytesAsync(altered, bytes, Ct);

        await using VortexSession session = VortexSession.Create(options => options.MaxDegreeOfParallelism = 4);
        await using ParquetFile verified = await session.OpenParquetAsync(altered, new ParquetOpenOptions { VerifyChecksums = true }, Ct);
        int read = 0;
        await Assert.ThrowsAsync<ParquetFormatException>(async () =>
        {
            await foreach (RecordBatch batch in verified.Scan().With(new ScanOptions { BatchRows = 1_000 }).ToBatchesAsync(Ct))
            {
                using (batch)
                {
                    read++;
                }
            }
        });

        // The first row group read whole, and the second up to its last page's batch.
        Assert.InRange(read, 12, 23);
        Assert.True(verified.Counters.Piped > 0, "No batch was decoded ahead.");
    }

    /// <summary>
    /// Every row of the file on <paramref name="degree"/> lanes, each value rendered, in batches of
    /// <paramref name="batchRows"/> rows or the scan's own, and the batches decoded ahead.
    /// </summary>
    private async Task<(List<string> Rows, long Piped)> RowsAsync(int degree, int batchRows)
    {
        await using VortexSession session = VortexSession.Create(options => options.MaxDegreeOfParallelism = degree);
        await using ParquetFile file = await session.OpenParquetAsync(_path, null, Ct);
        Scan scan = batchRows > 0 ? file.Scan().With(new ScanOptions { BatchRows = batchRows }) : file.Scan();
        List<string> rows = [];
        await foreach (RecordBatch batch in scan.ToBatchesAsync(Ct))
        {
            using (batch)
            {
                for (int r = 0; r < batch.RowCount; r++)
                {
                    rows.Add(string.Join(" | ", Enumerable.Range(0, batch.Schema.Count).Select(c => Render.Row(batch, c, r))));
                }
            }
        }

        return (rows, file.Counters.Piped);
    }

    private async Task WriteAsync(ParquetWriteOptions options)
    {
        Random random = new(43);
        Trip[] trips = new Trip[Rows];
        for (int i = 0; i < Rows; i++)
        {
            trips[i] = new Trip(
                i,
                i % 7 == 0 ? null : Math.Round(random.NextDouble() * 500, 2),
                i % 11 == 0 ? null : $"zone-{i % 23}",
                i % 5 == 0 ? null : i % 3 == 0,
                $"https://example.com/{random.Next(400)}/a/route/of/many/more/bytes/than/a/view/holds",
                random.Next(-100_000, 100_000) / 100m,
                i % 97 == 0 ? i : null,
                i % 3 == 0 ? "short" : $"note {random.Next()}",
                i / 1_000);
        }

        await using ParquetFileWriter writer = VortexSession.Default.CreateParquetWriter<Trip>(_path, options);
        await writer.WriteAsync<Trip>(trips, Ct);
        await writer.CompleteAsync(Ct);
    }
}
