using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Vorticity.Samples;

internal static class BlocksAndChunks
{
    internal static async Task RunAsync()
    {
        VortexSession session = VortexSession.Default;
        CancellationToken ct = CancellationToken.None;
        string path = Demo.Path("blocks-and-chunks.vortex");
        Reading[] rows = Readings(200_000);

        await using (VortexFileWriter writer = session.CreateWriter<Reading>(path))
        {
            int blockRows = writer.BlockRows;                          // 8 192 unless VortexWriteOptions.BlockRows says otherwise
            Console.WriteLine($"BlockRows {blockRows}");

            await writer.WriteAsync<Reading>(rows.AsSpan(0, 5_000), ct);
            Show("wrote 5 000 rows", writer, path);
            await writer.FlushAsync(ct);
            Show("flushed", writer, path);

            await writer.WriteAsync<Reading>(rows.AsSpan(5_000, 5_000), ct);
            Show("wrote 5 000 more", writer, path);
            await writer.FlushAsync(ct);
            Show("flushed", writer, path);

            WriteReport report = await writer.CompleteAsync(ct);
            Console.WriteLine($"completed: chunk rows {WriteReportText.Chunks(report.ChunkRows)}, {new FileInfo(path).Length} bytes");
        }

        Console.WriteLine();
        await Pattern(session, path, rows, "98 304 rows, writes of 8 192", 98_304, 8_192, flushEach: false, null);
        await Pattern(session, path, rows, "98 304 rows, writes of 8 192, a flush after each", 98_304, 8_192, flushEach: true, null);
        await Pattern(session, path, rows, "98 304 rows, one write", 98_304, 98_304, flushEach: false, null);
        await Pattern(session, path, rows, "100 000 rows, writes of 5 000", 100_000, 5_000, flushEach: false, null);
        await Pattern(session, path, rows, "100 000 rows, writes of 5 000, a flush after each", 100_000, 5_000, flushEach: true, null);
        await Pattern(session, path, rows, "98 304 rows, ChunkTargetBytes 64 KiB", 98_304, 8_192, flushEach: false, new VortexWriteOptions { ChunkTargetBytes = 64 << 10 });
        await Pattern(session, path, rows, "98 304 rows, ChunkTargetBytes 16 MiB", 98_304, 8_192, flushEach: false, new VortexWriteOptions { ChunkTargetBytes = 16 << 20 });
        await Pattern(session, path, rows, "98 304 rows, BlockRows 1 024", 98_304, 8_192, flushEach: false, new VortexWriteOptions { BlockRows = 1_024 });
    }

    // A created file is written beside its path, and CompleteAsync renames it over the path.
    private static void Show(string what, VortexFileWriter writer, string path)
    {
        FileInfo file = new FileInfo(path);
        Console.WriteLine($"{what}: RowCount {writer.RowCount}, UnflushedBytes {writer.UnflushedBytes}, " +
            (file.Exists ? $"{file.Length} bytes at the path" : "nothing at the path yet"));
    }

    private static async Task Pattern(VortexSession session, string path, Reading[] rows, string what, int total, int each, bool flushEach, VortexWriteOptions? options)
    {
        WriteReport report;
        await using (VortexFileWriter writer = session.CreateWriter<Reading>(path, options))
        {
            for (int start = 0; start < total; start += each)
            {
                await writer.WriteAsync<Reading>(rows.AsSpan(start, Math.Min(each, total - start)));
                if (flushEach) await writer.FlushAsync();
            }

            report = await writer.CompleteAsync();
        }

        long written = new FileInfo(path).Length;
        long resumesAt;
        await using (VortexFileWriter appender = await session.OpenWriterAsync(path))
        {
            resumesAt = appender.RowCount;
            appender.Abandon();
        }

        Console.WriteLine($"{what}: {report.ChunkRows.Length} chunks ({WriteReportText.Chunks(report.ChunkRows)}), {written} bytes; " +
            $"an append resumes at row {resumesAt} of {report.RowCount}");
    }

    private static Reading[] Readings(int count)
    {
        Reading[] readings = new Reading[count];
        for (int row = 0; row < count; row++)
        {
            readings[row] = new Reading(row / 1_000, row % 50 == 0 ? null : 10.0 + (row % 400 / 10.0), Demo.Cities[row / 7 % Demo.Cities.Length]);
        }

        return readings;
    }
}
