using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Vorticity.Tests.Scan;

/// <summary>
/// The first rows of a filtered scan cost what the first window costs, whatever the file's size:
/// the zone maps of the filtered column, a segment however many zones it holds, then the data the
/// first rows need. A file sixteen times larger asks for as many segments; it is only its zone map
/// that is larger.
/// </summary>
public sealed partial class FilteredFirstBatchTests
{
    private const int Rows = 20_000;

    [Fact]
    public async Task AFilteredTakeAsksForAsManySegmentsOnAFileSixteenTimesLarger()
    {
        string small = await WriteAsync(Rows);
        string large = await WriteAsync(16 * Rows);
        try
        {
            await using VortexFile one = await VortexFile.OpenAsync(small, Ct);
            await using VortexFile sixteen = await VortexFile.OpenAsync(large, Ct);
            long[] expected = [.. Enumerable.Range(0, 16 * Rows).Where(row => Noise(row) > 5).Take(10).Select(row => (long)row)];

            Projection<long> fromOne = one.Scan<Row>().Where(r => r.Noise > 5).Select(r => r.Key).Take(10);
            Projection<long> fromSixteen = sixteen.Scan<Row>().Where(r => r.Noise > 5).Select(r => r.Key).Take(10);
            Assert.Equal(expected, await fromOne.ToListAsync(Ct));
            Assert.Equal(expected, await fromSixteen.ToListAsync(Ct));
            Assert.Equal(fromOne.Statistics.Requests, fromSixteen.Statistics.Requests);
            Assert.True(fromSixteen.Statistics.BytesRequested >= fromOne.Statistics.BytesRequested);

            // The first batch of the scan alone, as a consumer that stops there reads it.
            Scan<Row> scanOne = one.Scan<Row>().Where(r => r.Noise > 5);
            Scan<Row> scanSixteen = sixteen.Scan<Row>().Where(r => r.Noise > 5);
            Assert.Equal(await FirstAsync(scanOne), await FirstAsync(scanSixteen));
            Assert.Equal(scanOne.Statistics.Requests, scanSixteen.Statistics.Requests);
        }
        finally
        {
            System.IO.File.Delete(small);
            System.IO.File.Delete(large);
        }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static long Noise(int row) => (long)((ulong)row * 0x9E3779B97F4A7C15UL >> 57);

    private static async Task<long> FirstAsync(Scan<Row> scan)
    {
        await foreach (Columns<Row> batch in scan.WithCancellation(Ct))
        {
            return batch.RowCount;
        }

        return 0;
    }

    /// <summary>
    /// A file of <paramref name="rows"/> rows written in batches of two thousand, a chunk sealed at
    /// each, with a column of doubles that do not compress: past the 64 KiB an open reads whole, so
    /// that the scans ask for their segments rather than find them held.
    /// </summary>
    private static async Task<string> WriteAsync(int rows)
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "filtered-first-batch");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"rows-{rows}-{Environment.ProcessId}-{Guid.NewGuid():N}.vortex");
        await using (VortexFileWriter writer = VortexSession.Default.CreateWriter<Row>(path, new VortexWriteOptions { BlockRows = 1_024, ChunkTargetBytes = 16 << 10 }))
        {
            Row[] block = new Row[2_048];
            for (int first = 0; first < rows; first += block.Length)
            {
                int count = Math.Min(block.Length, rows - first);
                for (int i = 0; i < count; i++)
                {
                    block[i] = new Row(first + i, Noise(first + i), BitConverter.Int64BitsToDouble(0x3FF0000000000000L | (Noise(first + i) * 0x0123456789ABL)));
                }

                await writer.WriteAsync<Row>(block.AsSpan(0, count), Ct);
            }

            await writer.CompleteAsync(Ct);
        }

        return path;
    }

    [VortexRecord]
    public partial record struct Row(long Key, long Noise, double Value);
}
