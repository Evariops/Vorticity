using System;
using System.IO;
using System.Threading.Tasks;
using Vorticity.Columns;
using Vorticity.File;
using Vorticity.Scanning;

namespace Vorticity.Samples;

internal static class ScanATable
{
    internal static async Task RunAsync()
    {
        string path = await Demo.ReadingsAsync();

        await using VortexFile file = await VortexFile.OpenAsync(path);
        ScanMetrics metrics = new ScanMetrics();
        long rows = 0;
        int biggest = 0;
        await foreach (RecordBatch batch in file.Scan().WithMetrics(metrics).ExecuteAsync())
        {
            // The batch owns the decoded buffers; disposing it gives them back to the pool.
            using (batch)
            {
                rows += batch.RowCount;
                biggest = Math.Max(biggest, batch.RowCount);
            }
        }

        Console.WriteLine($"{rows} rows in {metrics.Batches} batches, the biggest {biggest} rows, " +
            $"{metrics.ValuesDecoded} values decoded");

        int smaller = 0;
        await foreach (RecordBatch batch in file.Scan().WithMaxBatchRows(4_096).ExecuteAsync())
        {
            using (batch)
            {
                smaller++;
            }
        }

        Console.WriteLine($"{smaller} batches of at most 4096 rows");

        (long requests, long bytes, long scanned) = await Demo.MeasureAsync(path, f => f.Scan());
        Console.WriteLine($"reading {scanned} rows asked the file for {requests} rounds totalling {bytes} bytes, " +
            $"for a file of {new FileInfo(path).Length} bytes");

        Console.WriteLine($"{await file.Scan().CountAsync()} rows, without decoding one");
    }
}
