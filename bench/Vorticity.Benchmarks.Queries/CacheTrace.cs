using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.Indexes;

namespace Vorticity.Benchmarks.Queries;

/// <summary>
/// The regions a run of questions asks of the synopsis cache, replayed through three eviction policies
/// (<c>--micro cachetrace</c>): requests over hours of periods, tables kept up to the data's own bytes, and a run of
/// questions of six kinds, the first asked most, over windows that end at a now moving through the data, so that
/// what was asked often ages, with a sweep of everything up to now one time in twenty. The cache records each region
/// asked, held or not; the replay gives each policy's hits, by bytes and by asks, at capacities from a hundredth of
/// the regions' bytes to half of them.
/// </summary>
internal static class CacheTrace
{
    internal static async Task<int> Run(int hours, double rate, int questions, int seed)
    {
        await using MemoryObjectStore store = new MemoryObjectStore();
        long begin = Stopwatch.GetTimestamp();
        (long rows, _) = await QuestionBench.FillRequestsAsync(store, hours, rate, new VortexWriteOptions { Indexes = IndexPolicy.Auto.WithBudgetPerMille(1_000) });
        Console.WriteLine($"{rows} requests over {hours} periods of an hour, written and compacted in {Stopwatch.GetElapsedTime(begin).TotalSeconds:F1} s");

        List<CacheAccess> trace = [];
        string directory = System.IO.Directory.CreateTempSubdirectory("vortex-cachetrace-").FullName;
        try
        {
            // A cache that holds everything, so that the trace is the asks alone, whatever a policy would have kept.
            SynopsisCache cache = new SynopsisCache(directory, 64L << 30);
            cache.Asked = (uid, region) =>
            {
                lock (trace)
                {
                    trace.Add(new CacheAccess(KeyOf(uid, region), region.Length));
                }
            };

            await using VortexSession session = VortexSession.Create(options => options.SynopsisCache = cache);
            await using VortexDataset dataset = await VortexDataset.OpenAsync(store, new DatasetOptions { Session = session });
            begin = Stopwatch.GetTimestamp();
            await AskAsync(dataset, hours, questions, seed);
            Console.WriteLine($"{questions} questions asked in {Stopwatch.GetElapsedTime(begin).TotalSeconds:F1} s");
        }
        finally
        {
            System.IO.Directory.Delete(directory, recursive: true);
        }

        Dictionary<long, long> regions = [];
        long asked = 0;
        foreach (CacheAccess access in trace)
        {
            regions[access.Key] = access.Bytes;
            asked += access.Bytes;
        }

        long distinct = regions.Values.Sum();
        Console.WriteLine($"{trace.Count} regions asked, {asked / 1e6:F1} MB; {regions.Count} distinct, {distinct / 1e6:F2} MB");
        Console.WriteLine($"{"capacity",9} {"MB",8}  " + string.Join("  ", CacheReplay.Policies(1, 1).Select(policy => $"{policy.Name,-20}")));
        Console.WriteLine($"{"",9} {"",8}  " + string.Join("  ", CacheReplay.Policies(1, 1).Select(_ => $"{"bytes",9} {"asks",10}")));
        foreach (double share in new[] { 0.01, 0.02, 0.05, 0.1, 0.2, 0.5 })
        {
            long capacity = Math.Max(1, (long)(distinct * share));
            IEnumerable<string> cells = CacheReplay.Policies(capacity, regions.Count).Select(policy =>
            {
                (double bytes, double asks) = CacheReplay.Replay(trace, policy);
                return $"{bytes,8:P1} {asks,10:P1}";
            });
            Console.WriteLine($"{share,9:P0} {capacity / 1e6,8:F2}  " + string.Join("  ", cells));
        }

        // S3-FIFO's one constant, the small queue's share of the capacity, swept.
        Console.WriteLine();
        Console.WriteLine($"{"capacity",9}  " + string.Join("  ", new[] { 0.05, 0.1, 0.2 }.Select(small => $"{"small " + small.ToString("P0", System.Globalization.CultureInfo.InvariantCulture),-20}")));
        foreach (double share in new[] { 0.05, 0.1, 0.2, 0.5 })
        {
            long capacity = Math.Max(1, (long)(distinct * share));
            IEnumerable<string> cells = new[] { 0.05, 0.1, 0.2 }.Select(small =>
            {
                (double bytes, double asks) = CacheReplay.Replay(trace, new CacheReplay.S3Fifo(capacity, small));
                return $"{bytes,8:P1} {asks,10:P1}";
            });
            Console.WriteLine($"{share,9:P0}  " + string.Join("  ", cells));
        }

        await LoadAsync(store, hours, questions, seed, distinct);
        return 0;
    }

    /// <summary>
    /// The same questions asked again through the cache itself, at several capacities, over a store that counts:
    /// the reads each question sends to the store, and so how many questions a second keep a bucket under its
    /// limit, 5 500 reads a second per prefix on S3 and 900 per bucket on OVHcloud.
    /// </summary>
    private static async Task LoadAsync(MemoryObjectStore store, int hours, int questions, int seed, long distinct)
    {
        Console.WriteLine();
        Console.WriteLine($"{"cache",9} {"MB",8} {"hits",8} {"reads/question",15} {"questions/s, S3",16} {"OVHcloud",9}");
        foreach (double share in new[] { 0.0, 0.05, 0.2, 1.0 })
        {
            string directory = System.IO.Directory.CreateTempSubdirectory("vortex-cacheload-").FullName;
            try
            {
                long capacity = Math.Max(1, (long)(distinct * share));
                SynopsisCache? cache = share > 0 ? new SynopsisCache(directory, capacity) : null;
                await using CountingObjectStore counted = new CountingObjectStore(store);
                await using VortexSession session = VortexSession.Create(options => options.SynopsisCache = cache);
                await using VortexDataset dataset = await VortexDataset.OpenAsync(counted, new DatasetOptions { Session = session });
                counted.Reset();
                await AskAsync(dataset, hours, questions, seed);
                double reads = (double)counted.CountOf(ObjectOperation.Read) / questions;
                double hits = cache is null || cache.Hits + cache.Misses == 0 ? 0 : (double)cache.Hits / (cache.Hits + cache.Misses);
                Console.WriteLine($"{share,9:P0} {(cache is null ? 0 : capacity) / 1e6,8:F2} {hits,8:P1} {reads,15:F2} {5_500 / reads,16:F0} {900 / reads,9:F0}");
            }
            finally
            {
                System.IO.Directory.Delete(directory, recursive: true);
            }
        }
    }

    /// <summary>The run of questions: a kind drawn by its rank, a window drawn by its width, ending at a moving now.</summary>
    private static async Task AskAsync(VortexDataset dataset, int hours, int questions, int seed)
    {
        Func<Scan<GeneratedRequest>, Aggregation>[] kinds =
        [
            scan => scan.GroupBy(r => (r.Service, r.At.Truncate(CalendarUnit.Hour))).Select(g => (g.Key.Service, g.Key.Item2, g.Count())),
            scan => scan.Where(r => r.Status >= 500).GroupBy(r => (r.Region, r.Method)).Select(g => (g.Key.Region, g.Key.Method, g.Count())),
            scan => scan.Where(r => r.Method == "GET").GroupBy(r => r.Service).Select(g => (g.Key, g.Count())),
            scan => scan.GroupBy(r => r.Endpoint).Select(g => (g.Key, g.Count())),
            scan => scan.Where(r => r.Service == "service-001" && r.Status >= 500).GroupBy(r => r.Endpoint).Select(g => (g.Key, g.Count())),
            scan => scan.GroupBy(r => (r.Status, r.At.Truncate(CalendarUnit.Hour))).Select(g => (g.Key.Status, g.Key.Item2, g.Count())),
        ];
        double[] weights = [.. Enumerable.Range(1, kinds.Length).Select(rank => 1.0 / rank)];
        double total = weights.Sum();
        Random random = new Random(seed);
        DateTimeOffset start = QuestionBench.Start;
        TimeSpan lead = TimeSpan.FromHours(Math.Min(6, hours / 2.0));
        TimeSpan span = TimeSpan.FromHours(hours) - lead;
        for (int q = 0; q < questions; q++)
        {
            DateTimeOffset now = start + lead + (span * q / questions);
            double width = random.NextDouble();
            DateTimeOffset from = width < 0.35 ? now.AddMinutes(-15)
                : width < 0.65 ? now.AddHours(-1)
                : width < 0.85 ? now.AddHours(-6)
                : width < 0.95 ? now.AddDays(-1)
                : start;
            from = from < start ? start : from;
            double draw = random.NextDouble() * total;
            int kind = 0;
            while (kind < kinds.Length - 1 && (draw -= weights[kind]) > 0)
            {
                kind++;
            }

            Aggregation aggregation = kinds[kind](dataset.Scan<GeneratedRequest>().Where(r => r.At >= from && r.At < now));
            IAsyncEnumerator<RecordBatch> batches = aggregation.Query.Batches(default);
            await using (batches)
            {
                while (await batches.MoveNextAsync())
                {
                }
            }
        }
    }

    /// <summary>A region's key: its object's uid and its place, mixed into 64 bits.</summary>
    internal static long KeyOf(UInt128 uid, IndexSegment region)
    {
        ulong mixed = ((ulong)(uid >> 64) * 0x9E3779B97F4A7C15UL) ^ (ulong)uid ^ (region.Offset * 0xBF58476D1CE4E5B9UL) ^ region.Length;
        return (long)(mixed ^ (mixed >> 29));
    }
}
