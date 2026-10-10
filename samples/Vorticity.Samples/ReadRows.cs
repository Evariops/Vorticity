using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Vorticity.Samples;

internal static class ReadRows
{
    internal static async Task RunAsync()
    {
        string path = await Demo.ReadingsAsync();
        await using VortexFile file = await VortexFile.OpenAsync(path);
        CancellationToken ct = CancellationToken.None;

        int shown = 0;
        long kept = 0;
        await foreach (Reading r in file.Scan<Reading>().Where(r => r.Day == 900).ToRecordsAsync(ct))
        {
            if (shown++ < 3)
            {
                Console.WriteLine($"{r.City}: {r.Celsius?.ToString() ?? "null"}");
            }

            kept++;
        }

        Console.WriteLine($"{kept} rows for day 900");

        List<Reading> hottest = await file.Scan<Reading>()
            .ToRecordsAsync(ct)
            .OrderByDescending(r => r.Celsius)
            .Take(10)
            .ToListAsync(ct);
        Console.WriteLine($"hottest, sorted on the client: {hottest[0].Celsius} in {hottest[0].City} on day {hottest[0].Day}");

        List<Reading> taken = await file.Scan<Reading>().Rows(4, 900_000).ToRecordsAsync(ct).ToListAsync(ct);
        List<Reading> selected = await file.Scan<Reading>().Where(r => r.Celsius > 49.0).With(new ScanOptions { Compact = false }).ToRecordsAsync(ct).ToListAsync(ct);
        Console.WriteLine($"a take of two rows yields {taken.Count} records; Celsius > 49 under Compact = false yields {selected.Count}, the coldest {selected.Min(r => r.Celsius)}");

        (long rows, TimeSpan rowTime, long rowBytes) = await MeasureAsync(() => RowsAsync(file));
        Console.WriteLine($"every row as a Reading: {rows} rows in {rowTime.TotalMilliseconds:F0} ms, {rowBytes / (1024 * 1024)} MiB allocated");

        (long values, TimeSpan columnTime, long columnBytes) = await MeasureAsync(() => ColumnsAsync(file));
        Console.WriteLine($"the same values as columns: {values} rows in {columnTime.TotalMilliseconds:F0} ms, {columnBytes / 1024} KiB allocated");

        (_, TimeSpan sortTime, long sortBytes) = await MeasureAsync(async () =>
            (await file.Scan<Reading>().ToRecordsAsync().OrderByDescending(r => r.Celsius).Take(10).ToListAsync()).Count);
        Console.WriteLine($"top ten on the client: {sortTime.TotalMilliseconds:F0} ms, {sortBytes / (1024 * 1024)} MiB allocated");

        (_, TimeSpan maxTime, long maxBytes) = await MeasureAsync(async () => (long)(await file.Scan<Reading>().MaxAsync(r => r.Celsius) ?? 0));
        Scan<Reading> pushed = file.Scan<Reading>();
        double? max = await pushed.MaxAsync(r => r.Celsius);
        Console.WriteLine($"MaxAsync, pushed: {max} in {maxTime.TotalMilliseconds:F3} ms, {maxBytes} bytes allocated, {pushed.Metrics.Requests} requests, {pushed.Metrics.BlocksDecoded} blocks decoded");

        (long hot, TimeSpan hotTime, long hotBytes) = await MeasureAsync(() => HottestAsync(file));
        Console.WriteLine($"the hottest rows, filtered before the sink: {hot} rows in {hotTime.TotalMilliseconds:F0} ms, {hotBytes / 1024} KiB allocated");
    }

    private static async Task<long> HottestAsync(VortexFile file)
    {
        double hottest = await file.Scan<Reading>().MaxAsync(r => r.Celsius) ?? double.NaN;
        List<Reading> rows = await file.Scan<Reading>().Where(r => r.Celsius >= hottest).ToRecordsAsync().ToListAsync();
        return rows.Count;
    }

    private static async Task<long> RowsAsync(VortexFile file)
    {
        long rows = 0;
        await foreach (Reading r in file.Scan<Reading>().ToRecordsAsync())
        {
            if (r.City.Length > 0)
            {
                rows++;
            }
        }

        return rows;
    }

    private static async Task<long> ColumnsAsync(VortexFile file)
    {
        long rows = 0;
        await foreach (var (_, _, city) in file.Scan<Reading>())
        {
            for (int i = 0; i < city.Length; i++)
            {
                if (city.GetLength(i) > 0)
                {
                    rows++;
                }
            }
        }

        return rows;
    }

    // Warmed first, then the best of five: the first passes of a process measure the JIT.
    private static async Task<(long Result, TimeSpan Elapsed, long Allocated)> MeasureAsync(Func<Task<long>> run)
    {
        for (int pass = 0; pass < 5; pass++)
        {
            await run();
        }

        long result = 0;
        TimeSpan best = TimeSpan.MaxValue;
        long allocated = long.MaxValue;
        for (int pass = 0; pass < 5; pass++)
        {
            long before = GC.GetTotalAllocatedBytes(precise: true);
            Stopwatch watch = Stopwatch.StartNew();
            result = await run();
            TimeSpan elapsed = watch.Elapsed;
            best = elapsed < best ? elapsed : best;
            allocated = Math.Min(allocated, GC.GetTotalAllocatedBytes(precise: true) - before);
        }

        return (result, best, allocated);
    }
}
