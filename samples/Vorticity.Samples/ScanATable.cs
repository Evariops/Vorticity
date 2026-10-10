using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace Vorticity.Samples;

internal static class ScanATable
{
    internal static async Task RunAsync()
    {
        string path = await Demo.ReadingsAsync();
        await using VortexFile file = await VortexFile.OpenAsync(path);

        double total = 0;
        long seen = 0;
        ScanMetrics loop = default;
        TimeSpan loopTime = default;
        for (int round = 0; round < 3; round++)
        {
            Stopwatch clock = Stopwatch.StartNew();
            Scan<Reading> scan = file.Scan<Reading>();
            total = 0;
            seen = 0;
            await foreach (var (_, celsius, _) in scan)
            {
                foreach (double value in celsius.Values)
                {
                    total += value;
                }

                seen += celsius.Length;
            }

            (loop, loopTime) = (scan.Metrics, clock.Elapsed);
        }

        Console.WriteLine($"the loop: mean {total / seen:F4} over {seen} rows, {loopTime.TotalMilliseconds:F1} ms");
        Console.WriteLine($"  {loop.Batches} batches, {loop.BlocksDecoded} blocks decoded, {loop.Requests} requests, {loop.BytesRequested} bytes");

        double? mean = null;
        ScanMetrics avg = default;
        TimeSpan avgTime = default;
        for (int round = 0; round < 3; round++)
        {
            Stopwatch clock = Stopwatch.StartNew();
            Scan<Reading> scan = file.Scan<Reading>();
            mean = await scan.AverageAsync(r => r.Celsius);
            (avg, avgTime) = (scan.Metrics, clock.Elapsed);
        }

        Console.WriteLine($"AverageAsync: mean {mean:F4}, {avgTime.TotalMilliseconds:F1} ms");
        Console.WriteLine($"  {avg.BlocksDecoded} blocks decoded, {avg.Requests} requests, {avg.BytesRequested} bytes");

        FieldStatistics celsiusStatistics = file.Statistics[1];
        bool hasSum = celsiusStatistics.TryGetSum(out double sum);
        bool hasNulls = celsiusStatistics.TryGetNullCount(out long nulls);
        Console.WriteLine($"file statistics of Celsius: sum {(hasSum ? sum.ToString("F1") : "absent")}, null count {(hasNulls ? nulls.ToString() : "absent")}");

        bool hasDaySum = file.Statistics[0].TryGetSum(out long daySum);
        Scan<Reading> days = file.Scan<Reading>();
        double? meanDay = await days.AverageAsync(r => r.Day);
        Console.WriteLine($"file statistics of Day: sum {(hasDaySum ? daySum.ToString() : "absent")}; AverageAsync(r => r.Day) {meanDay}, {days.Metrics.Requests} requests");

        long defaultBytes = await AllocatedAsync(file, new ScanOptions());
        (long defaultBatches, _, _) = await BatchesAsync(file, new ScanOptions());
        long smallBytes = await AllocatedAsync(file, new ScanOptions { BatchRows = 4_096 });
        (long batches, long rows, long lastStart) = await BatchesAsync(file, new ScanOptions { BatchRows = 4_096 });
        long zoneBytes = await AllocatedAsync(file, new ScanOptions { BatchRows = 8_192 });
        (long zoneBatches, _, _) = await BatchesAsync(file, new ScanOptions { BatchRows = 8_192 });
        Console.WriteLine($"BatchRows 4096: {batches} batches, {rows} rows, the last starting at row {lastStart}");
        Console.WriteLine($"allocated by a whole scan: {defaultBytes} bytes in {defaultBatches} batches, {zoneBytes} bytes in {zoneBatches}, {smallBytes} bytes in {batches}");
    }

    private static async Task<(long Batches, long Rows, long LastStart)> BatchesAsync(VortexFile file, ScanOptions options)
    {
        long batches = 0;
        long rows = 0;
        long lastStart = 0;
        await foreach (Columns<Reading> columns in file.Scan<Reading>().With(options))
        {
            batches++;
            rows += columns.RowCount;
            lastStart = columns.StartRow;
        }

        return (batches, rows, lastStart);
    }

    private static async Task<long> AllocatedAsync(VortexFile file, ScanOptions options)
    {
        long least = long.MaxValue;
        for (int round = 0; round < 5; round++)
        {
            long before = GC.GetTotalAllocatedBytes(precise: true);
            await BatchesAsync(file, options);
            least = Math.Min(least, GC.GetTotalAllocatedBytes(precise: true) - before);
        }

        return least;
    }
}
