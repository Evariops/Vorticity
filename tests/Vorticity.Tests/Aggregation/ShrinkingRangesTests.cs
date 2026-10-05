using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Aggregating;
using Xunit;

namespace Vorticity.Tests.Aggregation;

/// <summary>
/// The ranges a blocking group by's queue hands its lanes shrink toward the end: a quarter of a
/// lane's share each at first, then a share of what is left, down to a chunk, each ending where a
/// chunk does. A lane slower than the others, for whatever reason, then takes a small range last,
/// and no chunk is read by two lanes.
/// </summary>
public sealed partial class ShrinkingRangesTests
{
    private const int BlockRows = 1_024;

    private const int Chunks = 256;

    [Fact]
    public async Task RangesShrinkDownToAChunkAndEndWhereChunksEnd()
    {
        string path = await WriteAsync();
        try
        {
            await using VortexFile file = await VortexFile.OpenAsync(path, Ct);
            RowRange[] ranges = AggregationEngine.Ranges(new FileScanSource(file), new ScanSpec(), degree: 4)!;

            // Every row, once, in order, and every cut where a chunk ends.
            Assert.Equal(0, ranges[0].Start);
            Assert.Equal((long)BlockRows * Chunks, ranges[^1].End);
            for (int r = 1; r < ranges.Length; r++)
            {
                Assert.Equal(ranges[r - 1].End, ranges[r].Start);
                Assert.Equal(0, ranges[r].Start % BlockRows);
            }

            // A quarter of a lane's share first, never larger after, and a chunk each at the end:
            // sixteen ranges of sixteen chunks hold the first half, then each is an eighth of what
            // is left, then a chunk.
            Assert.Equal(16L * BlockRows, ranges[0].Length);
            for (int r = 1; r < ranges.Length; r++)
            {
                Assert.True(ranges[r].Length <= ranges[r - 1].Length, $"range {r} of {ranges[r].Length} rows after one of {ranges[r - 1].Length}");
            }

            Assert.All(ranges[^8..], range => Assert.Equal(BlockRows, range.Length));
            Assert.InRange(ranges.Length, 30, 60);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public async Task OneLaneOrTooFewRowsRunAsOneRange()
    {
        string path = await WriteAsync();
        try
        {
            await using VortexFile file = await VortexFile.OpenAsync(path, Ct);
            FileScanSource source = new FileScanSource(file);
            Assert.Null(AggregationEngine.Ranges(source, new ScanSpec(), degree: 1));
            Assert.Null(AggregationEngine.Ranges(source, new ScanSpec { Rows = new RowRange(0, BlockRows) }, degree: 4));
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A file of <see cref="Chunks"/> chunks of a block each.</summary>
    private static async Task<string> WriteAsync()
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "shrinking-ranges");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"rows-{Environment.ProcessId}-{Guid.NewGuid():N}.vortex");
        Row[] rows = new Row[BlockRows * Chunks];
        for (int row = 0; row < rows.Length; row++)
        {
            rows[row] = new Row(row % 1_000, row);
        }

        await using (VortexFileWriter writer = VortexSession.Default.CreateWriter<Row>(path, new VortexWriteOptions { RowBlockSize = BlockRows, ChunkTargetBytes = 1 }))
        {
            await writer.WriteAsync<Row>(rows, Ct);
            await writer.CompleteAsync(Ct);
        }

        return path;
    }

    [VortexRecord]
    public partial record struct Row(int Key, long Value);
}
