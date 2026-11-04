using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Vorticity.Dataset;

namespace Vorticity.Benchmarks.Queries;

/// <summary>
/// The synopsis cache under dashboards (<c>--micro cacheviews</c>), over a requests store <c>--micro paths --design</c>
/// built (<c>--hours</c>, <c>--rate</c>, <c>--budget</c> name it; thirteen months at a hundredth of the design's rate,
/// say): five dashboards of 8 to 15 panels each, a question a panel, the first dashboard opened most, each view of one
/// over a window that ends at a now moving through the data: the last hour mostly, else the last six hours, day, week or
/// month, and one view in twenty an incident's hour or six anywhere before. The cache records each region asked; the
/// replay gives each eviction policy's hits, by bytes and by asks, at capacities from a hundredth of the regions' bytes to
/// half of them; then the views are opened again through caches of several capacities over a store that counts, which
/// gives each question's reads, and opened once more through the cache that held every region, whose reads are the
/// data's alone, which no cache spares.
/// </summary>
internal static class CacheViews
{
    /// <summary>A panel's question over a window.</summary>
    private delegate Aggregation Panel(Scan<GeneratedRequest> scan);

    /// <summary>A view: a dashboard's panels over one window.</summary>
    private sealed record View(int Dashboard, DateTimeOffset From, DateTimeOffset To);

    internal static async Task<int> Run(string root, int hours, double rate, int budget, int views, int seed)
    {
        string directory = Path.Combine(root, string.Create(CultureInfo.InvariantCulture, $"requests-design-load-h{hours}-r{rate:0}-b{budget}"));
        if (!System.IO.File.Exists(directory + ".built"))
        {
            Console.Error.WriteLine($"No store is built at {directory}: build it with --micro paths --design --hours {hours} --rate {rate} --build-only.");
            return 2;
        }

        Panel[][] dashboards = Dashboards(seed);
        List<View> plan = Plan(hours, views, dashboards.Length, seed);
        int questions = plan.Sum(view => dashboards[view.Dashboard].Length);
        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"{views} views of {string.Join('/', dashboards.Select(d => d.Length))} panels, {questions} questions, over {directory}"));

        // A cache that holds everything, so that the trace is the asks alone, whatever a policy would have kept.
        List<CacheAccess> trace = [];
        string traced = Directory.CreateTempSubdirectory("vortex-cacheviews-").FullName;
        try
        {
            SynopsisCache cache = new SynopsisCache(traced, 1L << 40);
            cache.Asked = (uid, region) =>
            {
                lock (trace)
                {
                    trace.Add(new CacheAccess(CacheTrace.KeyOf(uid, region), region.Length));
                }
            };

            await using VortexSession session = VortexSession.Create(options => options.SynopsisCache = cache);
            await using FileObjectStore store = new FileObjectStore(directory);
            await using VortexDataset dataset = await VortexDataset.OpenAsync(store, new DatasetOptions { Session = session });
            long begin = Stopwatch.GetTimestamp();
            await OpenAsync(dataset, plan, dashboards);
            Console.WriteLine($"{questions} questions asked in {Stopwatch.GetElapsedTime(begin).TotalSeconds:F1} s");
        }
        finally
        {
            Directory.Delete(traced, recursive: true);
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
        Console.WriteLine($"{"capacity",9} {"MB",9}  " + string.Join("  ", CacheReplay.Policies(1, 1).Select(policy => $"{policy.Name,-20}")));
        Console.WriteLine($"{"",9} {"",9}  " + string.Join("  ", CacheReplay.Policies(1, 1).Select(_ => $"{"bytes",9} {"asks",10}")));
        foreach (double share in new[] { 0.01, 0.02, 0.05, 0.1, 0.2, 0.5 })
        {
            long capacity = Math.Max(1, (long)(distinct * share));
            IEnumerable<string> cells = CacheReplay.Policies(capacity, regions.Count).Select(policy =>
            {
                (double bytes, double asks) = CacheReplay.Replay(trace, policy);
                return $"{bytes,8:P1} {asks,10:P1}";
            });
            Console.WriteLine($"{share,9:P0} {capacity / 1e6,9:F2}  " + string.Join("  ", cells));
        }

        // The views again through the cache itself, at several capacities, then once more through the one that held every
        // region: its reads are the data's, which no cache spares; a capacity's reads past them, the regions it missed.
        Console.WriteLine();
        Console.WriteLine($"{"cache",9} {"MB",9} {"hits",8} {"reads/question",15} {"missed/question",16}");
        double floor = 0;
        foreach (double share in new[] { 1.0, 0.0, 0.01, 0.05, 0.2 })
        {
            string held = Directory.CreateTempSubdirectory("vortex-cacheviews-load-").FullName;
            try
            {
                long capacity = Math.Max(1, (long)(distinct * share));
                SynopsisCache? cache = share > 0 ? new SynopsisCache(held, capacity) : null;
                await using VortexSession session = VortexSession.Create(options => options.SynopsisCache = cache);
                await using FileObjectStore files = new FileObjectStore(directory);
                await using CountingObjectStore counted = new CountingObjectStore(files);
                await using VortexDataset dataset = await VortexDataset.OpenAsync(counted, new DatasetOptions { Session = session });
                counted.Reset();
                await OpenAsync(dataset, plan, dashboards);
                double reads = (double)counted.CountOf(ObjectOperation.Read) / questions;
                double hits = cache is null || cache.Hits + cache.Misses == 0 ? 0 : (double)cache.Hits / (cache.Hits + cache.Misses);
                if (share == 1.0)
                {
                    // The cache now holds every region the views ask: opened again, they read the data alone.
                    counted.Reset();
                    await OpenAsync(dataset, plan, dashboards);
                    floor = (double)counted.CountOf(ObjectOperation.Read) / questions;
                    Console.WriteLine($"{"all, warm",9} {distinct / 1e6,9:F2} {1,8:P1} {floor,15:F2} {0,16:F2}");
                }

                Console.WriteLine($"{share,9:P0} {(cache is null ? 0 : capacity) / 1e6,9:F2} {hits,8:P1} {reads,15:F2} {reads - floor,16:F2}");
            }
            finally
            {
                Directory.Delete(held, recursive: true);
            }
        }

        return 0;
    }

    /// <summary>
    /// The dashboards' panels: a dashboard of every service, one of errors, one of a service of its own, one of the
    /// endpoints and one of the hosts, of 8 to 15 panels each, drawn from what a dashboard of requests shows.
    /// </summary>
    private static Panel[][] Dashboards(int seed)
    {
        Random random = new Random(seed);
        Panel PerService() => scan => scan.GroupBy(r => (r.Service, r.At.Truncate(CalendarUnit.Hour))).Select(g => (g.Key.Service, g.Key.Item2, g.Count()));
        Panel ErrorsByPlace(short least) => scan => scan.Where(r => r.Status >= least).GroupBy(r => (r.Region, r.Method)).Select(g => (g.Key.Region, g.Key.Method, g.Count()));
        Panel ByMethod(string method) => scan => scan.Where(r => r.Method == method).GroupBy(r => r.Service).Select(g => (g.Key, g.Count()));
        Panel ByEndpoint() => scan => scan.GroupBy(r => r.Endpoint).Select(g => (g.Key, g.Count()));
        Panel ServiceErrors(string service) => scan => scan.Where(r => r.Service == service && r.Status >= 500).GroupBy(r => r.Endpoint).Select(g => (g.Key, g.Count()));
        Panel StatusPerHour() => scan => scan.GroupBy(r => (r.Status, r.At.Truncate(CalendarUnit.Hour))).Select(g => (g.Key.Status, g.Key.Item2, g.Count()));
        Panel Latency() => scan => scan.GroupBy(r => r.Service).Select(g => (g.Key, g.Count(), g.Average(r => r.DurationNs)));
        Panel ByVersion() => scan => scan.GroupBy(r => (r.Service, r.Version)).Select(g => (g.Key.Service, g.Key.Version, g.Count()));
        Panel ByRegion() => scan => scan.GroupBy(r => r.Region).Select(g => (g.Key, g.Count(), g.Sum(r => r.Weight)));
        string Service() => string.Create(CultureInfo.InvariantCulture, $"service-{random.Next(1, 20):D3}");

        Panel[] templates =
        [
            PerService(), ErrorsByPlace(500), ErrorsByPlace(400), ByMethod("GET"), ByMethod("POST"), ByEndpoint(), StatusPerHour(),
            Latency(), ByVersion(), ByRegion(),
        ];
        Panel[][] dashboards = new Panel[5][];
        for (int d = 0; d < dashboards.Length; d++)
        {
            int count = 8 + random.Next(8);
            List<Panel> panels = [.. templates.OrderBy(_ => random.Next()).Take(Math.Min(count, templates.Length))];
            while (panels.Count < count)
            {
                panels.Add(ServiceErrors(Service()));
            }

            dashboards[d] = [.. panels];
        }

        return dashboards;
    }

    /// <summary>
    /// The views: a dashboard drawn by its rank, a window drawn by its width, ending at a now that moves from a month in
    /// to the data's end; one view in twenty an incident's, anywhere before.
    /// </summary>
    private static List<View> Plan(int hours, int views, int dashboards, int seed)
    {
        Random random = new Random(seed + 1);
        double[] weights = [.. Enumerable.Range(1, dashboards).Select(rank => 1.0 / rank)];
        double total = weights.Sum();
        DateTimeOffset start = QuestionBench.Start;
        TimeSpan lead = TimeSpan.FromHours(Math.Min(31 * 24, hours / 2.0));
        TimeSpan span = TimeSpan.FromHours(hours) - lead;
        List<View> plan = new List<View>(views);
        for (int v = 0; v < views; v++)
        {
            DateTimeOffset now = start + lead + (span * v / views);
            double draw = random.NextDouble() * total;
            int dashboard = 0;
            while (dashboard < dashboards - 1 && (draw -= weights[dashboard]) > 0)
            {
                dashboard++;
            }

            double width = random.NextDouble();
            (DateTimeOffset from, DateTimeOffset to) = width < 0.05
                ? Incident(random, start, now)
                : (now - (width < 0.45 ? TimeSpan.FromHours(1)
                    : width < 0.65 ? TimeSpan.FromHours(6)
                    : width < 0.85 ? TimeSpan.FromDays(1)
                    : width < 0.95 ? TimeSpan.FromDays(7)
                    : TimeSpan.FromDays(30)), now);
            plan.Add(new View(dashboard, from < start ? start : from, to));
        }

        return plan;
    }

    /// <summary>An incident's window: an hour or six, starting anywhere between the data's start and <paramref name="now"/>.</summary>
    private static (DateTimeOffset From, DateTimeOffset To) Incident(Random random, DateTimeOffset start, DateTimeOffset now)
    {
        TimeSpan width = TimeSpan.FromHours(random.Next(2) == 0 ? 1 : 6);
        DateTimeOffset from = start + ((now - width - start) * random.NextDouble());
        return (from, from + width);
    }

    /// <summary>Opens every view: each of its dashboard's panels asked over its window, its answer read whole.</summary>
    private static async Task OpenAsync(VortexDataset dataset, List<View> plan, Panel[][] dashboards)
    {
        foreach (View view in plan)
        {
            foreach (Panel panel in dashboards[view.Dashboard])
            {
                Aggregation aggregation = panel(dataset.Scan<GeneratedRequest>().Where(r => r.At >= view.From && r.At < view.To));
                IAsyncEnumerator<RecordBatch> batches = aggregation.Query.Batches(default);
                await using (batches)
                {
                    while (await batches.MoveNextAsync())
                    {
                    }
                }
            }
        }
    }
}
