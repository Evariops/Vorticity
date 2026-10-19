// The synopsis part: the synopsis columns' chunks, held until the end of the data and written together
// after the rest of it, one contiguous range that the index directory names chunk by chunk, each with
// its checksum; the rows read as they would without it.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Indexes;
using Xunit;

namespace Vorticity.Tests.Writing;

public sealed partial class SynopsisPartTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// Twenty thousand rows in chunks of two blocks, a region and a status in the synopsis part, held in a
    /// spool of a kilobyte so that they wait in its file: every chunk of the two columns lies in one range
    /// after the rest of the data, in the order they came, each region's bytes its checksum's, and the
    /// rows are the ones written.
    /// </summary>
    [Fact]
    public async Task TheSynopsisColumnsLieTogetherAfterTheData()
    {
        Row[] rows = [.. Enumerable.Range(0, 20_000).Select(i => new Row(i, $"region-{i / 5_000}", 200 + (i % 7), $"host-{i % 997}"))];
        string path = Path.Combine(Path.GetTempPath(), $"vorticity-synopsis-{Guid.NewGuid():N}.vortex");
        try
        {
            VortexWriteOptions options = new VortexWriteOptions
            {
                BlockRows = 1_000,
                ChunkTargetBytes = 4_096,
                ScratchMemoryBytes = 1_024,
                SynopsisColumns = [nameof(Row.Region), nameof(Row.Status)],
            };
            await using (VortexFileWriter writer = VortexSession.Default.CreateWriter<Row>(path, options))
            {
                await writer.WriteAsync<Row>(rows, Ct);
                await writer.CompleteAsync(Ct);
            }

            byte[] bytes = await System.IO.File.ReadAllBytesAsync(path, Ct);
            await using VortexFile file = await VortexFile.OpenAsync(path, cancellationToken: Ct);
            IndexDirectory directory = (await file.ReadIndexDirectoryAsync(Ct))!;
            IndexEntry[] entries = [.. directory.Entries.Where(e => e.Kind == IndexKinds.SynopsisColumns)];
            Assert.Equal(
                [(uint)file.DType.IndexOfField(nameof(Row.Region)), (uint)file.DType.IndexOfField(nameof(Row.Status))],
                entries.Select(e => e.ColumnPath[0]).Order());

            // One range: the regions of both columns, end to end but for each segment's alignment.
            long start = BitConverter.ToInt64(entries[0].Options, 0);
            long end = BitConverter.ToInt64(entries[0].Options, 8);
            IndexSegment[] regions = [.. entries.SelectMany(e => e.Runs[0].Payload).OrderBy(r => r.Offset)];
            Assert.True(regions.Length > 4, $"{regions.Length} chunks");
            Assert.Equal((ulong)start, regions[0].Offset);
            Assert.Equal((ulong)end, regions[^1].Offset + regions[^1].Length);
            for (int i = 1; i < regions.Length; i++)
            {
                ulong gap = regions[i].Offset - (regions[i - 1].Offset + regions[i - 1].Length);
                Assert.InRange(gap, 0UL, (ulong)VortexLimits.MaxAlignment - 1);
            }

            Assert.All(regions, region => Assert.True(region.Holds(bytes.AsSpan((int)region.Offset, (int)region.Length))));

            // After every other data segment, and before the index regions.
            HashSet<long> part = [.. regions.Select(r => (long)r.Offset)];
            Assert.All(file.SegmentMap.Where(s => !part.Contains((long)s.Offset) && (long)s.Offset < start), s => Assert.True((long)s.Offset + s.Length <= start));

            Assert.Equal(rows, await file.Scan<Row>().ToRecordsAsync(Ct).ToListAsync(Ct));
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    /// <summary>A synopsis column must be one of the file's top-level columns.</summary>
    [Fact]
    public void ASynopsisColumnTheFileLacksIsRefused()
    {
        string path = Path.Combine(Path.GetTempPath(), $"vorticity-synopsis-{Guid.NewGuid():N}.vortex");
        Assert.Throws<ArgumentException>(() => VortexSession.Default.CreateWriter<Row>(path, new VortexWriteOptions { SynopsisColumns = ["Missing"] }));
        System.IO.File.Delete(path);
    }

    [VortexRecord]
    public partial record struct Row(long Id, string Region, int Status, string Host);
}
