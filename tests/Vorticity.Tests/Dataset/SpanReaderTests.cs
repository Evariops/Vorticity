// Spans of a file's rows read without its footer: the chunk directories a file of contents carries in its index directory,
// and a span's rows read through them without a byte of the dtype, the layout or the footer, its columns alone.
//
// THE ORACLE IS THE FILE'S OWN ROWS: every span reads back the rows the writer took, in their order.
using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Indexes;
using Vorticity.Serialization.Schemas;
using Vorticity.Tests.File;
using Vorticity.Types;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed partial class SpanReaderTests
{
    private static readonly long Noon = new DateTimeOffset(2026, 10, 18, 12, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds() * 1000;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A span's rows, read through the chunk directories, without a byte of the dtype, the layout or the footer.</summary>
    [Fact]
    public async Task ASpanIsReadWithoutTheFooter()
    {
        (Reading[] rows, byte[] bytes, DType schema) = await WriteAsync(contents: true, Ct);

        // Spans of 2 000 rows over chunks of 1 024: each starts and ends inside one.
        TestSegmentSource source = new(bytes) { Ranges = [] };
        SpanReader reader = (await SpanReader.OpenAsync(source, bytes.Length, schema, null, Ct))!;
        int spans = rows.Length / SpanRows;
        for (int s = 0; s < spans; s++)
        {
            using RecordBatch batch = await reader.ReadRowsAsync(s * (long)SpanRows, SpanRows, null, Ct);
            Assert.Equal(rows[(s * SpanRows)..((s + 1) * SpanRows)], Read(batch));
        }

        // Nothing read lies between the index directory and the postscript, where the dtype, the layout and the footer are:
        // two reads of the end, the directory, the chunk directories with the first span, then one request a span.
        SegmentSpec directory = (await VortexFile.ReadMetadataSpecAsync(new TestSegmentSource(bytes), bytes.Length, IndexDirectory.MetadataKey, Ct))!.Value;
        long footerEnd = bytes.Length - 8 - BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(bytes.Length - 6, 2));
        Assert.True(footerEnd > (long)directory.End);
        Assert.All(source.Ranges!, range => Assert.True(
            range.Offset + range.Length <= (long)directory.End || range.Offset >= footerEnd, $"{range} reads the footer"));
        Assert.Equal(2, source.RangeReads);
        Assert.Equal(spans + 1, source.BatchReads);

        // The instant alone reads its own chunk directory and chunks, none of the other column's.
        TestSegmentSource instants = new(bytes) { Ranges = [] };
        SpanReader edge = (await SpanReader.OpenAsync(instants, bytes.Length, schema, null, Ct))!;
        int opened = instants.Ranges!.Count;
        using (RecordBatch batch = await edge.ReadRowsAsync(3 * SpanRows, SpanRows, [0], Ct))
        {
            DateTimeOffset[] at = new DateTimeOffset[batch.RowCount];
            batch.View.Column<DateTimeOffset>(0).CopyTo(at);
            Assert.Equal(rows[(3 * SpanRows)..(4 * SpanRows)].Select(r => r.At), at);
            Assert.Equal(nameof(Reading.At), Assert.Single(batch.View.Schema).Name);
        }

        // Span 3, rows 6 000 to 8 000, lies across several chunks: one region and its chunks for the instant alone, against
        // twice as many reads for both columns.
        TestSegmentSource both = new(bytes) { Ranges = [] };
        SpanReader whole = (await SpanReader.OpenAsync(both, bytes.Length, schema, null, Ct))!;
        using (await whole.ReadRowsAsync(3 * SpanRows, SpanRows, null, Ct))
        {
        }

        Assert.InRange(instants.Ranges.Count - opened, 1 + 2, int.MaxValue);
        Assert.Equal(2 * (instants.Ranges.Count - opened), both.Ranges!.Count - opened);
        Assert.True(
            instants.Ranges.Skip(opened).Sum(range => range.Length) < both.Ranges.Skip(opened).Sum(range => range.Length),
            "the instant alone reads less than both columns");

        // A file that carries no contents opens no span reader.
        byte[] plain = (await WriteAsync(contents: false, Ct)).Bytes;
        Assert.Null(await SpanReader.OpenAsync(new TestSegmentSource(plain), plain.Length, schema, null, Ct));
    }

    /// <summary>The rows of a span the tests read.</summary>
    internal const int SpanRows = 2_000;

    internal readonly record struct Written(Reading[] Rows, byte[] Bytes, DType Schema);

    /// <summary>20 000 rows 30 ms apart, in chunks of 1 024 rows: the file's bytes and schema, with its contents or none.</summary>
    internal static async Task<Written> WriteAsync(bool contents, CancellationToken ct)
    {
        Reading[] rows = [.. Enumerable.Range(0, 20_000).Select(i => new Reading(DateTimeOffset.FromUnixTimeMilliseconds((Noon / 1000) + (i * 30L)), i))];
        string path = Path.Combine(Path.GetTempPath(), $"vorticity-spans-{Guid.NewGuid():N}.vortex");
        try
        {
            VortexWriteOptions options = new() { CarriesContents = contents, BlockRows = 1024, ChunkTargetBytes = 16 * 1024 };
            await using (VortexFileWriter writer = VortexSession.Default.CreateWriter<Reading>(path, options))
            {
                await writer.WriteAsync<Reading>(rows, ct);
                await writer.CompleteAsync(ct);
            }

            await using VortexFile file = await VortexFile.OpenAsync(path, ct);
            return new Written(rows, await System.IO.File.ReadAllBytesAsync(path, ct), file.DType);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    private static Reading[] Read(RecordBatch batch)
    {
        Columns<Reading> columns = batch.As<Reading>();
        DateTimeOffset[] at = new DateTimeOffset[batch.RowCount];
        long[] values = new long[batch.RowCount];
        columns.Column<DateTimeOffset>(0).CopyTo(at);
        columns.Column<long>(1).CopyTo(values);
        return [.. at.Zip(values, (a, v) => new Reading(a, v))];
    }

    [VortexRecord]
    public partial record struct Reading(DateTimeOffset At, long Value);
}
