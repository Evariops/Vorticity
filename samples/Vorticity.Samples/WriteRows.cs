using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Vorticity.Samples;

internal static class WriteRows
{
    private const int Rows = 1_000_000;

    internal static async Task RunAsync()
    {
        VortexSession session = VortexSession.Default;
        CancellationToken ct = CancellationToken.None;
        string path = Demo.Path("write-rows.vortex");
        Reading[] readings = Readings(Rows);

        long best = long.MaxValue;
        WriteReport? report = null;
        for (int round = 0; round < 3; round++)
        {
            Stopwatch clock = Stopwatch.StartNew();
            await using VortexFileWriter writer = session.CreateWriter<Reading>(path);
            await writer.WriteAsync<Reading>(readings.AsSpan(), ct);
            report = await writer.CompleteAsync(ct);
            best = Math.Min(best, clock.ElapsedMilliseconds);
        }

        Console.WriteLine($"a span of {readings.Length} rows: {report!.Bytes.Total} bytes, best of three {best} ms");
        Console.WriteLine($"  chunk rows: {WriteReportText.Chunks(report.ChunkRows)}");

        best = long.MaxValue;
        for (int round = 0; round < 3; round++)
        {
            Stopwatch clock = Stopwatch.StartNew();
            await using VortexFileWriter streaming = session.CreateWriter<Reading>(path);
            await streaming.WriteAsync(ReadingsAsync(Rows, ct), ct);
            report = await streaming.CompleteAsync(ct);
            best = Math.Min(best, clock.ElapsedMilliseconds);
        }

        Console.WriteLine($"a stream of {report.RowCount} rows: {report.Bytes.Total} bytes, best of three {best} ms");
        Console.WriteLine($"  chunk rows: {WriteReportText.Chunks(report.ChunkRows)}");

        await using VortexFile file = await VortexFile.OpenAsync(path);
        Reading last = default;
        await foreach (Reading row in file.Scan<Reading>().Rows(Rows - 1).ToRecordsAsync(ct))
        {
            last = row;
        }

        Console.WriteLine($"read back: {file.RowCount} rows, {new FileInfo(path).Length} bytes, the last one {last}");
    }

    private static Reading[] Readings(int rows)
    {
        Reading[] readings = new Reading[rows];
        for (int row = 0; row < rows; row++)
        {
            readings[row] = new Reading(row / 1_000, row % 50 == 0 ? null : 10.0 + (row % 400 / 10.0), Demo.Cities[row / 7 % Demo.Cities.Length]);
        }

        return readings;
    }

    /// <summary>The same rows, as a source that awaits between pages would hand them out.</summary>
    private static async IAsyncEnumerable<Reading> ReadingsAsync(int rows, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        for (int row = 0; row < rows; row++)
        {
            if (row % 10_000 == 0)
            {
                await Task.Yield();
                ct.ThrowIfCancellationRequested();
            }

            yield return new Reading(row / 1_000, row % 50 == 0 ? null : 10.0 + (row % 400 / 10.0), Demo.Cities[row / 7 % Demo.Cities.Length]);
        }
    }
}
