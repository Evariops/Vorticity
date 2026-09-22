using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace Vorticity.Samples;

// A class named Selection here would hide Vorticity.Selection from every sample.
internal static class SelectionCase
{
    internal static async Task RunAsync()
    {
        string path = await Demo.ReadingsAsync();
        await using VortexFile file = await VortexFile.OpenAsync(path);

        double total = 0;
        await foreach (var cols in file.Scan<Reading>().Where(r => r.Celsius > 20.0).With(new ScanOptions { Compact = false }))
        {
            ReadOnlySpan<double> values = cols.Celsius.Values;
            foreach (int i in cols.Selection)
            {
                total += values[i];
            }
        }

        Console.WriteLine($"total of the readings above 20 degrees: {total:F1}");

        await foreach (Columns<Reading> cols in file.Scan<Reading>().Where(r => r.Celsius > 20.0).With(new ScanOptions { Compact = false }))
        {
            int third = -1;
            int seen = 0;
            foreach (int i in cols.Selection)
            {
                if (++seen == 3)
                {
                    third = i;
                    break;
                }
            }

            Console.WriteLine($"first batch: {cols.RowCount} rows from file row {cols.StartRow}, {cols.Selection.Count} selected, the third at file row {cols.StartRow + third}");
            break;
        }

        await MeasureAsync<Reading>(file, "Reading, > 20", celsius: 1, r => r.Column<double?>(1) > 20.0);
        await MeasureAsync<Temperature>(file, "Temperature, > 20", celsius: 0, r => r.Celsius > 20.0);
        await MeasureAsync<Reading>(file, "Reading, > 49", celsius: 1, r => r.Column<double?>(1) > 49.0);
    }

    private static async Task MeasureAsync<TRecord>(VortexFile file, string name, int celsius, Func<Probe<TRecord>, Predicate> filter)
        where TRecord : IVortexRecord<TRecord>
    {
        (TimeSpan whole, long wholeBytes, long kept, long delivered) = await BestOfThreeAsync(file, celsius, filter, compact: false);
        (TimeSpan compacted, long compactedBytes, _, _) = await BestOfThreeAsync(file, celsius, filter, compact: true);
        Console.WriteLine($"{name,-18} {kept,7} of {delivered,7} rows  whole blocks {whole.TotalMilliseconds,5:F1} ms {wholeBytes,7} bytes   compacted {compacted.TotalMilliseconds,5:F1} ms {compactedBytes,7} bytes");
    }

    private static async Task<(TimeSpan Time, long Allocated, long Kept, long Delivered)> BestOfThreeAsync<TRecord>(
        VortexFile file, int celsius, Func<Probe<TRecord>, Predicate> filter, bool compact)
        where TRecord : IVortexRecord<TRecord>
    {
        TimeSpan best = TimeSpan.MaxValue;
        long allocated = long.MaxValue;
        long kept = 0;
        long delivered = 0;
        for (int round = 0; round < 3; round++)
        {
            long before = GC.GetTotalAllocatedBytes(precise: true);
            Stopwatch clock = Stopwatch.StartNew();
            double total = 0;
            kept = 0;
            delivered = 0;
            await foreach (Columns<TRecord> cols in file.Scan<TRecord>().Where(filter).With(new ScanOptions { Compact = compact }))
            {
                ReadOnlySpan<double> values = cols.Column<double?>(celsius).Values;
                foreach (int i in cols.Selection)
                {
                    total += values[i];
                }

                kept += cols.Selection.Count;
                delivered += cols.RowCount;
            }

            best = clock.Elapsed < best ? clock.Elapsed : best;
            allocated = Math.Min(allocated, GC.GetTotalAllocatedBytes(precise: true) - before);
        }

        return (best, allocated, kept, delivered);
    }
}

/// <summary>The temperature alone.</summary>
[VortexRecord]
public partial record struct Temperature(double? Celsius);
