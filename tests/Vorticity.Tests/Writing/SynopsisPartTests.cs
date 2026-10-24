// The synopsis part: the synopsis columns' chunks, held until the end of the data and written together
// after the rest of it, one contiguous range whose segments the footer names as any column's; the rows
// read as they would without it.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.File;
using Xunit;

namespace Vorticity.Tests.Writing;

public sealed partial class SynopsisPartTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// Twenty thousand rows in chunks of two blocks, a region and a status in the synopsis part, held in a
    /// spool of a kilobyte so that they wait in its file: every chunk of the two columns lies in one range
    /// after the rest of the data, in the order they came, and the rows are the ones written.
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

            await using VortexFile file = await VortexFile.OpenAsync(path, cancellationToken: Ct);
            List<VortexSegment>[] columns = await ColumnSegments.OfAsync(file, Ct);

            // One range: the segments of both columns, end to end but for each segment's alignment.
            VortexSegment[] part = [.. columns[file.DType.IndexOfField(nameof(Row.Region))]
                .Concat(columns[file.DType.IndexOfField(nameof(Row.Status))]).OrderBy(s => s.Offset)];
            Assert.True(part.Length > 4, $"{part.Length} chunks");
            for (int i = 1; i < part.Length; i++)
            {
                long gap = part[i].Offset - (part[i - 1].Offset + part[i - 1].Length);
                Assert.InRange(gap, 0, VortexLimits.MaxAlignment - 1);
            }

            // After every other column's segments.
            long start = part[0].Offset;
            foreach (VortexSegment other in columns[file.DType.IndexOfField(nameof(Row.Id))].Concat(columns[file.DType.IndexOfField(nameof(Row.Host))]))
            {
                Assert.True(other.Offset + other.Length <= start, $"a segment at {other.Offset} past the part's start at {start}");
            }

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
