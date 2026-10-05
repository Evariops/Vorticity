using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Vorticity.IO;

namespace Vorticity.Samples;

internal static class Threads
{
    internal static async Task RunAsync()
    {
        string path = await Demo.ReadingsAsync();

        await using VortexSession session = VortexSession.Create(o =>
        {
            o.MemoryPool = new AlignedMemoryPool();
            o.SegmentCache = new SegmentCache(256L * 1024 * 1024);
            o.MaxConcurrentReads = 32;
            o.MaxDegreeOfParallelism = 4;
        });

        try
        {
            session.Options.MaxDegreeOfParallelism = 8;
        }
        catch (InvalidOperationException e)
        {
            Console.WriteLine($"after Create: {e.Message}");
        }

        await using VortexFile file = await session.OpenAsync(new FileSegmentSource(path));

        Task<long>[] concurrent = new Task<long>[4];
        for (int i = 0; i < concurrent.Length; i++)
        {
            concurrent[i] = CountRowsAsync(file.Scan<Reading>());
        }

        Console.WriteLine($"four concurrent scans of one open file: {string.Join(", ", await Task.WhenAll(concurrent))}");

        SegmentCache cache = session.Options.SegmentCache!;
        Scan<Reading> again = file.Scan<Reading>();
        await CountRowsAsync(again);
        Console.WriteLine($"a fifth scan: {again.Statistics.Requests} requests, {again.Statistics.CacheHits} served by the cache");
        Console.WriteLine($"the cache: {cache.Size / 1024} KiB held, {cache.Hits} hits, {cache.Misses} misses");

        Scan<Reading> once = file.Scan<Reading>();
        await once.CountAsync();
        try
        {
            await once.CountAsync();
        }
        catch (InvalidOperationException e)
        {
            Console.WriteLine($"a second sink: {e.Message}");
        }

        await using VortexFile mapped = await session.OpenAsync(path);
        ScanOptions one = new ScanOptions { DegreeOfParallelism = 1 };
        ScanOptions four = new ScanOptions { DegreeOfParallelism = 4 };
        TimeSpan[] degrees = await BestOfAsync(
            () => CountRowsAsync(mapped.Scan<Reading>().With(one)),
            () => CountRowsAsync(mapped.Scan<Reading>().With(four)),
            () => GroupAsync(mapped.Scan<Reading>().With(one)),
            () => GroupAsync(mapped.Scan<Reading>().With(four)),
            async () => (long)await mapped.Scan<Reading>().With(one).Where(r => r.Celsius > 20.0).SumAsync(r => r.Celsius),
            async () => (long)await mapped.Scan<Reading>().With(four).Where(r => r.Celsius > 20.0).SumAsync(r => r.Celsius));
        Console.WriteLine($"degree 1: scan {Ms(degrees[0])}, group by city {Ms(degrees[2])}, filtered sum {Ms(degrees[4])}");
        Console.WriteLine($"degree 4: scan {Ms(degrees[1])}, group by city {Ms(degrees[3])}, filtered sum {Ms(degrees[5])}");

        long previous = -1;
        bool inOrder = true;
        await foreach (Columns<Reading> cols in mapped.Scan<Reading>().With(new ScanOptions { DegreeOfParallelism = 4 }))
        {
            inOrder &= cols.StartRow > previous;
            previous = cols.StartRow;
        }

        Console.WriteLine($"degree 4, batches in file order: {inOrder}");

        ScanOptions none = new ScanOptions { Prefetch = 0 };
        ScanOptions ahead = new ScanOptions { Prefetch = 1 };
        ScanOptions further = new ScanOptions { Prefetch = 2 };
        TimeSpan[] prefetch = await BestOfAsync(
            () => ConsumeAsync(mapped.Scan<Reading>().With(none)),
            () => ConsumeAsync(mapped.Scan<Reading>().With(ahead)),
            () => ConsumeAsync(mapped.Scan<Reading>().With(further)));
        Console.WriteLine($"a consumer that works on every value: prefetch 0 {Ms(prefetch[0])}, 1 {Ms(prefetch[1])}, 2 {Ms(prefetch[2])}");

        VortexSession owner = VortexSession.Create(o => o.MaxDegreeOfParallelism = 2);
        VortexFile open = await owner.OpenAsync(path);
        try
        {
            await owner.DisposeAsync();
        }
        catch (InvalidOperationException e)
        {
            Console.WriteLine($"disposing a session with a file open: {e.Message.Replace(path, "readings.vortex")}");
        }

        await open.DisposeAsync();
        await owner.DisposeAsync();
    }

    private static async Task<long> CountRowsAsync(Scan<Reading> scan)
    {
        long rows = 0;
        await foreach (Columns<Reading> cols in scan)
        {
            rows += cols.RowCount;
        }

        return rows;
    }

    private static async Task<long> ConsumeAsync(Scan<Reading> scan)
    {
        long rows = 0;
        await foreach (var (day, celsius, _) in scan)
        {
            double acc = 0;
            ReadOnlySpan<double> values = celsius.Values;
            for (int i = 0; i < values.Length; i++)
            {
                acc += Math.Sqrt(Math.Abs(values[i]) + day[i]);
            }

            rows += acc > 0 ? values.Length : 0;
        }

        return rows;
    }

    private static async Task<long> GroupAsync(Scan<Reading> scan)
    {
        long groups = 0;
        await foreach ((string city, double? mean) in scan.GroupBy(r => r.City).Select(g => (g.Key, g.Average(r => r.Celsius))).As<CityMean>().ToRecordsAsync())
        {
            groups += city.Length > 0 && mean > 0 ? 1 : 0;
        }

        return groups;
    }

    // The variants run in turn, round after round, so that a busy machine slows them alike; the
    // first rounds let the JIT settle and are not kept.
    private static async Task<TimeSpan[]> BestOfAsync(params Func<Task<long>>[] variants)
    {
        TimeSpan[] best = new TimeSpan[variants.Length];
        best.AsSpan().Fill(TimeSpan.MaxValue);
        for (int round = 0; round < 30; round++)
        {
            for (int v = 0; v < variants.Length; v++)
            {
                Stopwatch watch = Stopwatch.StartNew();
                await variants[v]();
                if (round >= 15 && watch.Elapsed < best[v])
                {
                    best[v] = watch.Elapsed;
                }
            }
        }

        return best;
    }

    private static string Ms(TimeSpan elapsed) => $"{elapsed.TotalMilliseconds:F1} ms";
}

/// <summary>A city and its mean reading.</summary>
[VortexRecord]
public partial record struct CityMean(string City, double? Mean);
