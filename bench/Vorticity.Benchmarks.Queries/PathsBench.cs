using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.IO;

namespace Vorticity.Benchmarks.Queries;

/// <summary>
/// Every query path of a dataset of periods, at scale, over a store that waits a round trip per request
/// (<c>--micro paths</c>): generated requests and log records written once into a directory that stands for the
/// object store, then asked the questions of the time-window and variant designs, each from its synopses and from
/// its rows, cold and warm, at each degree asked. The synopsis cache lives on the same disk, without the wait.
/// </summary>
/// <remarks>
/// <para>
/// The store is a <see cref="FileObjectStore"/> under <c>--store</c> (the <c>VORTICITY_PATHS_STORE</c> variable, or
/// <c>E:/vorticity-queries</c>), each request answered <c>--latency</c> ms (5 by default) after it is sent, to the
/// tenth of a millisecond (<see cref="PreciseDelay"/>), the reads' bytes crossing one link of <c>--gbps</c> (10 by
/// default, 0 for none) and each read's at most <c>--mbps</c> when set; the synopsis cache reads its own disk, with
/// neither. A dataset is written the first time its parameters are asked for (<c>--hours</c>, <c>--rate</c> requests a
/// second, <c>--log-rate</c> log records, <c>--design</c> at the design's own scale, <see cref="DesignBuild"/>) and
/// kept beside a note of what it holds; <c>--rebuild</c> writes it again.
/// </para>
/// <para>
/// Each line says what the store saw (requests, dependent round trips, bytes), what the answer read (states, rows),
/// its time and processor time, and the bytes the process allocated, every thread counted. Cold is a session and a
/// handle opened for the question with an empty cache, its open not counted; warm is the median of <c>--rounds</c>
/// asks by the same handle (<c>--rows-rounds</c> for the rows). With <c>--verify</c> each question's answer from
/// the synopses is held to the one from its rows, digest against digest. <c>--tsv</c> appends every line to a
/// file that <c>--compare</c> reads back.
/// </para>
/// </remarks>
internal static class PathsBench
{
    /// <summary>What a run asks for.</summary>
    internal sealed record Settings
    {
        internal string Store { get; init; } = Environment.GetEnvironmentVariable("VORTICITY_PATHS_STORE") ?? "E:/vorticity-queries";

        internal int Hours { get; init; } = 24;

        internal double Rate { get; init; } = 1_000;

        internal double LogRate { get; init; } = 1_000;

        internal int LogHours { get; init; } = 12;

        internal int Latency { get; init; } = 5;

        internal double BytesPerSecond { get; init; }

        internal double LinkBitsPerSecond { get; init; } = 10e9;

        internal int Rounds { get; init; } = 5;

        internal int RowsRounds { get; init; } = 1;

        internal int[] Degrees { get; init; } = [1, Environment.ProcessorCount];

        internal string? Only { get; init; }

        internal string Paths { get; init; } = "all";

        internal string Temperatures { get; init; } = "all";

        internal int Budget { get; init; } = 100;

        internal int WriteDegree { get; init; } = Math.Max(1, Environment.ProcessorCount / 2);

        internal bool Rebuild { get; init; }

        internal bool BuildOnly { get; init; }

        internal bool Verify { get; init; }

        /// <summary>Whether each question's plan is printed once warm: what each object reads, a table, its edges or its rows, and why.</summary>
        internal bool Explain { get; init; }

        internal bool Design { get; init; }

        internal int ParallelHours { get; init; } = 4;

        internal int PartMinutes { get; init; } = 10;

        internal double LoopSeconds { get; init; }

        /// <summary>How long to ask the question cold again and again, each by a handle and a cache of its own, for a profiler; zero for no loop.</summary>
        internal double LoopColdSeconds { get; init; }

        internal string? Tsv { get; init; }

        internal string? Label { get; init; }

        internal string? TraceReads { get; init; }
    }

    internal static async Task<int> Run(Settings settings)
    {
        Directory.CreateDirectory(settings.Store);
        Built requests = await EnsureAsync(
            settings,
            $"requests-{(settings.Design ? "design-" : string.Empty)}h{settings.Hours}-r{settings.Rate:0}-b{settings.Budget}",
            store => settings.Design
                ? DesignBuild.FillAsync(store, settings.Hours, settings.Rate, settings.Budget, settings.ParallelHours, settings.WriteDegree, settings.PartMinutes, Path.Combine(settings.Store, "scratch"))
                : FillRequestsAsync(store, settings));
        Built logs = await EnsureAsync(settings, $"logs-h{settings.LogHours}-r{settings.LogRate:0}-b{settings.Budget}", store => FillLogsAsync(store, settings));
        if (settings.BuildOnly)
        {
            return 0;
        }

        string label = settings.Label ?? Commit();
        Console.WriteLine(
            $"store {settings.Store}: {requests.Rows:N0} requests over {settings.Hours} h, {logs.Rows:N0} log records over {settings.LogHours} h; " +
            $"{settings.Latency} ms a request, {(settings.LinkBitsPerSecond > 0 ? $"a link of {settings.LinkBitsPerSecond / 1e9:0.##} Gb/s" : "no limit on the link")}{(settings.BytesPerSecond > 0 ? $", {settings.BytesPerSecond / 1e6:0} MB/s a read" : string.Empty)}; " +
            $"warm is the median of {settings.Rounds} ({settings.RowsRounds} for rows); {label}");
        Console.WriteLine(
            $"{"question",-62} {"path",-8} {"",-4} {"dop",3} {"ms",9} {"cpu ms",9} {"alloc MB",9} {"req",6} {"steps",6} {"MB",8} {"states",9} {"rows",11} {"groups",7}");

        if (settings.Explain)
        {
            await TablesAsync(requests);
        }

        DateTimeOffset end = QuestionBench.Start.AddHours(settings.Hours);
        DateTimeOffset lastHour = end.AddHours(-1);
        DateTimeOffset lastQuarter = end.AddMinutes(-15);
        DateTimeOffset logEnd = QuestionBench.Start.AddHours(settings.LogHours);
        DateTimeOffset logLastHour = logEnd.AddHours(-1);
        TimeSpan half = TimeSpan.FromHours(12);
        DateTimeOffset from = QuestionBench.Start.AddMinutes(37.5);
        DateTimeOffset to = end.AddMinutes(-13);
        string needle = requests.Needle;
        Question[] questions =
        [
            Asked<GeneratedRequest>("requests", "a count per service and per 12 hours", scan => scan.GroupBy(r => (r.Service, r.At.Bucket(half))).Select(g => (g.Key.Service, g.Key.Item2, g.Count()))),
            Asked<GeneratedRequest>("requests", "services under Method == GET", scan => scan.Where(r => r.Method == "GET").GroupBy(r => r.Service).Select(g => (g.Key, g.Count()))),
            Asked<GeneratedRequest>("requests", "errors outside sa-east by region and method, per hour", scan => scan.Where(r => r.Status >= 500 && r.Region != "sa-east").GroupBy(r => (r.Region, r.Method, r.At.Truncate(CalendarUnit.Hour))).Select(g => (g.Key.Region, g.Key.Method, g.Key.Item3, g.Count()))),
            Asked<GeneratedRequest>("requests", "one service per minute, the last hour, under a predicate", scan => scan.Where(r => r.Service == "service-000" && r.At >= lastHour && r.Status == 200 && r.Region == "eu-west").GroupBy(r => r.At.Truncate(CalendarUnit.Minute)).Select(g => (g.Key, g.Count()))),
            Asked<GeneratedRequest>("requests", "the errors of one service, by endpoint, per day", scan => scan.Where(r => r.Service == "service-000" && r.Status >= 500).GroupBy(r => (r.Endpoint, r.At.Truncate(CalendarUnit.Day))).Select(g => (g.Key.Endpoint, g.Key.Item2, g.Count()))),
            Asked<GeneratedRequest>("requests", "a count per endpoint", scan => scan.GroupBy(r => r.Endpoint).Select(g => (g.Key, g.Count()))),
            Asked<GeneratedRequest>("requests", "one endpoint per day", scan => scan.Where(r => r.Endpoint == "/s0/e0").GroupBy(r => r.At.Truncate(CalendarUnit.Day)).Select(g => (g.Key, g.Count()))),
            Asked<GeneratedRequest>("requests", "one host per minute", scan => scan.Where(r => r.Host == "host-000-00").GroupBy(r => r.At.Truncate(CalendarUnit.Minute)).Select(g => (g.Key, g.Count()))),
            Asked<GeneratedRequest>("requests", "every row but one request's, per service and per 12 hours", scan => scan.Where(r => r.RequestId != needle).GroupBy(r => (r.Service, r.At.Bucket(half))).Select(g => (g.Key.Service, g.Key.Item2, g.Count()))),
            Asked<GeneratedRequest>("requests", "a count per service and per 12 hours, the window unaligned", scan => scan.Where(r => r.At >= from && r.At < to).GroupBy(r => (r.Service, r.At.Bucket(half))).Select(g => (g.Key.Service, g.Key.Item2, g.Count()))),
            Asked<GeneratedRequest>("requests", "the same, the window aligned (AlignWindow)", scan => scan.Where(r => r.At >= from && r.At < to).GroupBy(r => (r.Service, r.At.Bucket(half))).Select(g => (g.Key.Service, g.Key.Item2, g.Count())), align: true),
            Asked<GeneratedRequest>("requests", "a count per status, the last 15 minutes", scan => scan.Where(r => r.At >= lastQuarter).GroupBy(r => r.Status).Select(g => (g.Key, g.Count()))),
            Asked<GeneratedRequest>("requests", "the mean duration per service, per hour", scan => scan.GroupBy(r => (r.Service, r.At.Truncate(CalendarUnit.Hour))).Select(g => (g.Key.Service, g.Key.Item2, g.Average(r => r.DurationNs)))),
            Asked<GeneratedRequest>("requests", "the weight and the longest duration per region, per hour", scan => scan.GroupBy(r => (r.Region, r.At.Truncate(CalendarUnit.Hour))).Select(g => (g.Key.Region, g.Key.Item2, g.Sum(r => r.Weight), g.Max(r => r.DurationNs)))),
            Asked<GeneratedRequest>("requests", "the 99th percentile of duration per method", scan => scan.GroupBy(r => r.Method).Select(g => (g.Key, g.Quantile(r => r.DurationNs, 0.99)))),
            Asked<GeneratedRequest>("requests", "distinct hosts per region, approximately", scan => scan.GroupBy(r => r.Region).Select(g => (g.Key, g.ApproximateCountDistinct(r => r.Host)))),
            Rows<GeneratedRequest>("requests", "the rows of one request id (needle)", scan => scan.Where(r => r.RequestId == needle)),
            Rows<GeneratedRequest>("requests", "the 5xx of one endpoint, the last hour, as rows", scan => scan.Where(r => r.Endpoint == "/s0/e0" && r.Status >= 500 && r.At >= lastHour)),
            Asked<LogRow>("logs", "a count per http.route and per day", scan => scan.GroupBy(r => (r.Attributes["http.route"].AsString(), r.At.Truncate(CalendarUnit.Day))).Select(g => (g.Key.Item1, g.Key.Item2, g.Count()))),
            Asked<LogRow>("logs", "services under status_code >= 500, per minute, the last hour", scan => scan.Where(r => r.Attributes["http.response.status_code"].AsInt64() >= 500 && r.At >= logLastHour).GroupBy(r => (r.ServiceName, r.At.Truncate(CalendarUnit.Minute))).Select(g => (g.Key.ServiceName, g.Key.Item2, g.Count()))),
            Asked<LogRow>("logs", "the rows holding feature.flag.new-cart, per minute", scan => scan.Where(r => r.Attributes["feature.flag.new-cart"].Exists).GroupBy(r => r.At.Truncate(CalendarUnit.Minute)).Select(g => (g.Key, g.Count()))),
            Asked<LogRow>("logs", "feature.flag.new-cart == on, per service and per day", scan => scan.Where(r => r.Attributes["feature.flag.new-cart"].AsString() == "on").GroupBy(r => (r.ServiceName, r.At.Truncate(CalendarUnit.Day))).Select(g => (g.Key.ServiceName, g.Key.Item2, g.Count()))),
            Asked<LogRow>("logs", "the mean duration per route and per day", scan => scan.GroupBy(r => (r.Attributes["http.route"].AsString(), r.At.Truncate(CalendarUnit.Day))).Select(g => (g.Key.Item1, g.Key.Item2, g.Average(r => r.Attributes["http.server.request.duration"].AsDouble())))),
            Asked<LogRow>("logs", "status_code AsInt64() >= 500, per service", scan => scan.Where(r => r.Attributes["http.response.status_code"].AsInt64() >= 500).GroupBy(r => r.ServiceName).Select(g => (g.Key, g.Count()))),
            Asked<LogRow>("logs", "status_code ToInt64() >= 500, per service, text parsed", scan => scan.Where(r => r.Attributes["http.response.status_code"].ToInt64() >= 500).GroupBy(r => r.ServiceName).Select(g => (g.Key, g.Count()))),
        ];

        bool failed = false;
        foreach (int degree in settings.Degrees)
        {
            foreach (Question question in questions)
            {
                if (settings.Only is { } only && !only.Split('|').Any(word => question.Name.Contains(word, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                Built built = question.Set == "logs" ? logs : requests;
                string? synopsesDigest = null;
                foreach (bool synopses in question.Aggregates ? new[] { true, false } : [false])
                {
                    string path = synopses ? "synopses" : "rows";
                    if (settings.Paths != "all" && settings.Paths != path)
                    {
                        continue;
                    }

                    (Measured cold, Measured warm) = await AskAsync(settings, built, question, synopses, degree);
                    if (cold.Digest is not null)
                    {
                        Print(settings, label, question, path, "cold", degree, cold);
                    }

                    if (warm.Digest is not null)
                    {
                        Print(settings, label, question, path, "warm", degree, warm);
                    }

                    string digest = warm.Digest ?? cold.Digest ?? string.Empty;
                    if (synopses)
                    {
                        synopsesDigest = digest;
                    }
                    else if (settings.Verify && synopsesDigest is not null && synopsesDigest != digest)
                    {
                        Console.WriteLine($"    DIGESTS DIFFER: synopses {synopsesDigest}, rows {digest}");
                        failed = true;
                    }
                }
            }
        }

        return failed ? 1 : 0;
    }

    /// <summary>The synopsis tables each object of the store holds, as its entry's table of contents names them.</summary>
    private static async Task TablesAsync(Built built)
    {
        await using FileObjectStore files = new FileObjectStore(built.Directory);
        await using VortexDataset dataset = await VortexDataset.OpenAsync(files, new DatasetOptions());
        await foreach (PositionedObject held in dataset.WalkAsync(null, 0, long.MaxValue, null, default))
        {
            if (held.Entry.Contents is not { } contents)
            {
                Console.WriteLine($"    tables {held.Entry.Key}: none, {held.Entry.Rows:N0} rows");
                continue;
            }

            Vorticity.Types.DType schema = contents.SchemaType;
            foreach ((Vorticity.Indexes.SynopsisTableInfo table, IReadOnlyList<Vorticity.Indexes.TableChunk> chunks) in contents.Tables)
            {
                string keys = string.Join(", ", table.Keys.Select(field => Vorticity.Indexes.SynopsisTableInfo.NameOf(field, schema, contents.Slices.Paths)));
                string measures = string.Join(", ", table.Measures.Select(field => Vorticity.Indexes.SynopsisTableInfo.NameOf(field, schema, contents.Slices.Paths)));
                Console.WriteLine($"    tables {held.Entry.Key}: {table.Kind} by {table.Bucket} on ({keys}) of [{measures}], {table.Rows:N0} rows in {chunks.Count} chunks");
            }
        }
    }

    /// <summary>A question of the catalogue: its dataset, its name, and how it is asked of a dataset under scan options.</summary>
    private sealed record Question(string Set, string Name, bool Aggregates, Func<VortexDataset, ScanOptions, Asking> Ask);

    /// <summary>A question asked: its batches, and what the scan did once they are read; its plan, for an aggregation.</summary>
    private sealed record Asking(IAsyncEnumerator<RecordBatch> Batches, Func<ScanMetrics> Metrics)
    {
        internal Func<ValueTask<ScanPlan>>? Explain { get; init; }
    }

    private static Question Asked<TRecord>(string set, string name, Func<Scan<TRecord>, Aggregation> ask, bool align = false)
        where TRecord : IVortexRecord<TRecord> =>
        new Question(set, name, true, (dataset, options) =>
        {
            Aggregation aggregation = ask(dataset.Scan<TRecord>().With(options with { AlignWindow = align }));
            return new Asking(aggregation.Query.Batches(default), () => aggregation.Metrics) { Explain = () => aggregation.ExplainAsync() };
        });

    private static Question Rows<TRecord>(string set, string name, Func<Scan<TRecord>, Scan<TRecord>> ask)
        where TRecord : IVortexRecord<TRecord> =>
        new Question(set, name, false, (dataset, options) =>
        {
            Scan<TRecord> scan = ask(dataset.Scan<TRecord>().With(options));
            return new Asking(scan.ToBatchesAsync().GetAsyncEnumerator(), () => scan.Metrics);
        });

    /// <summary>
    /// <c>--loop-cold</c>: the cold ask again and again for as long, for a profiler, and its mean: each by a session and a
    /// handle opened for it, with an empty synopsis cache, disposed and deleted after it.
    /// </summary>
    private static async Task LoopColdAsync(Settings settings, CountingObjectStore store, Question question, ScanOptions options, int degree)
    {
        if (settings.LoopColdSeconds <= 0)
        {
            return;
        }

        long until = Stopwatch.GetTimestamp() + (long)(settings.LoopColdSeconds * Stopwatch.Frequency);
        int asks = 0;
        double milliseconds = 0;
        long allocated = 0;
        while (Stopwatch.GetTimestamp() < until)
        {
            string cache = Path.Combine(settings.Store, "cache", Guid.NewGuid().ToString("N"));
            try
            {
                await using VortexSession session = VortexSession.Create(o =>
                {
                    o.MaxDegreeOfParallelism = degree;
                    o.SynopsisCache = new SynopsisCache(cache, 8L << 30);
                });
                await using VortexDataset dataset = await VortexDataset.OpenAsync(store, new DatasetOptions { Session = session });
                Measured again = await MeasureAsync(store, dataset, question, options);
                asks++;
                milliseconds += again.Milliseconds;
                allocated += again.Allocated;
            }
            finally
            {
                try
                {
                    Directory.Delete(cache, recursive: true);
                }
                catch (IOException)
                {
                }
            }
        }

        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture, $"    cold loop: {asks} asks, {milliseconds / asks:F3} ms and {allocated / (double)asks / 1e3:F1} KB allocated each"));
    }

    /// <summary>
    /// One question by one path: cold by a session and a handle opened for it, with an empty synopsis cache, then warm
    /// by the same handle, the median of the rounds; each asked once first, with no wait and nothing kept, so that
    /// neither pays the question's first compile.
    /// </summary>
    private static async Task<(Measured Cold, Measured Warm)> AskAsync(Settings settings, Built built, Question question, bool synopses, int degree)
    {
        ScanOptions options = new ScanOptions { UseSynopses = synopses, DegreeOfParallelism = degree };
        await using FileObjectStore files = new FileObjectStore(built.Directory);
        LatencyObjectStore remote = new LatencyObjectStore(files);
        await using CountingObjectStore store = new CountingObjectStore(remote);
        {
            await using VortexSession session = VortexSession.Create(o => o.MaxDegreeOfParallelism = degree);
            await using VortexDataset dataset = await VortexDataset.OpenAsync(store, new DatasetOptions { Session = session });
            await MeasureAsync(store, dataset, question, options);
        }

        remote.Latency = TimeSpan.FromMilliseconds(settings.Latency);
        remote.LinkBitsPerSecond = settings.LinkBitsPerSecond;
        remote.BytesPerSecond = settings.BytesPerSecond;
        string cache = Path.Combine(settings.Store, "cache", Guid.NewGuid().ToString("N"));
        try
        {
            await using VortexSession session = VortexSession.Create(o =>
            {
                o.MaxDegreeOfParallelism = degree;
                o.SynopsisCache = new SynopsisCache(cache, 8L << 30);
            });
            await using VortexDataset dataset = await VortexDataset.OpenAsync(store, new DatasetOptions { Session = session });
            using TextWriter? trace = settings.TraceReads is { } traced ? TextWriter.Synchronized(new StreamWriter(traced, append: true)) : null;
            trace?.WriteLine($"# {question.Name} {(synopses ? "synopses" : "rows")} cold, degree {degree}");
            remote.Trace = trace;
            Measured cold = settings.Temperatures is "all" or "cold" ? await MeasureAsync(store, dataset, question, options) : default;
            remote.Trace = null;
            await LoopColdAsync(settings, store, question, options, degree);
            if (settings.Temperatures is "cold")
            {
                return (cold, default);
            }

            if (settings.Temperatures is "warm")
            {
                await MeasureAsync(store, dataset, question, options);
            }

            // The first warm round's reads are written down after the cold one's.
            int rounds = synopses ? settings.Rounds : settings.RowsRounds;
            List<Measured> warm = [];
            for (int round = 0; round < rounds; round++)
            {
                if (round == 0 && trace is not null)
                {
                    trace.WriteLine($"# {question.Name} {(synopses ? "synopses" : "rows")} warm, degree {degree}");
                    remote.Trace = trace;
                }

                warm.Add(await MeasureAsync(store, dataset, question, options));
                remote.Trace = null;
            }

            warm.Sort((a, b) => a.Milliseconds.CompareTo(b.Milliseconds));

            // --explain: what each object reads, once warm.
            if (settings.Explain && question.Ask(dataset, options) is { Explain: { } explain } asked)
            {
                await asked.Batches.DisposeAsync();
                foreach (StatesStep step in (await explain()).States)
                {
                    Console.WriteLine($"    plan {step.Object}: {step.Read} {step.Detail} x{step.Count}");
                }
            }

            // --loop: the warm ask again and again for as long, for a profiler, and its mean.
            if (settings.LoopSeconds > 0)
            {
                long until = Stopwatch.GetTimestamp() + (long)(settings.LoopSeconds * Stopwatch.Frequency);
                int asks = 0;
                double milliseconds = 0;
                long allocated = 0;
                while (Stopwatch.GetTimestamp() < until)
                {
                    Measured again = await MeasureAsync(store, dataset, question, options);
                    asks++;
                    milliseconds += again.Milliseconds;
                    allocated += again.Allocated;
                }

                Console.WriteLine(string.Create(
                    CultureInfo.InvariantCulture, $"    loop: {asks} asks, {milliseconds / asks:F3} ms and {allocated / (double)asks / 1e3:F1} KB allocated each"));
            }

            return (cold, warm[warm.Count / 2]);
        }
        finally
        {
            try
            {
                Directory.Delete(cache, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private static async Task<Measured> MeasureAsync(CountingObjectStore store, VortexDataset dataset, Question question, ScanOptions options)
    {
        store.Reset();
        Asking asking = question.Ask(dataset, options);
        long allocated = GC.GetTotalAllocatedBytes(precise: true);
        TimeSpan cpu = Environment.CpuUsage.TotalTime;
        long begin = Stopwatch.GetTimestamp();
        long groups = 0;
        ResultDigest digest = default;

        // The digest is taken of each batch while it is current, and its time left out: over 20 280 groups it was
        // three tenths of a question's.
        long digesting = 0;
        await using (asking.Batches)
        {
            while (await asking.Batches.MoveNextAsync())
            {
                RecordBatch batch = asking.Batches.Current;
                groups += batch.SelectedRows;
                long taken = Stopwatch.GetTimestamp();
                digest.Add(batch);
                digesting += Stopwatch.GetTimestamp() - taken;
            }
        }

        double milliseconds = (Stopwatch.GetTimestamp() - begin - digesting) * 1000.0 / Stopwatch.Frequency;
        double cpuMilliseconds = (Environment.CpuUsage.TotalTime - cpu).TotalMilliseconds;
        long bytes = GC.GetTotalAllocatedBytes(precise: true) - allocated;
        ScanMetrics metrics = asking.Metrics();
        return new Measured(
            store.Requests, store.DependentSteps, store.BytesRead, metrics.SynopsisRows, metrics.Rows, milliseconds, cpuMilliseconds, bytes, groups, digest.ToString());
    }

    private static void Print(Settings settings, string label, Question question, string path, string temperature, int degree, Measured m)
    {
        string name = temperature == "cold" ? question.Name : string.Empty;
        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"{name,-62} {path,-8} {temperature,-4} {degree,3} {m.Milliseconds,9:F1} {m.CpuMilliseconds,9:F1} {m.Allocated / 1e6,9:F2} {m.Requests,6} {m.Steps,6} {m.Bytes / 1e6,8:F2} {m.States,9} {m.Rows,11} {m.Groups,7}"));
        if (settings.Tsv is { } tsv)
        {
            System.IO.File.AppendAllText(tsv, string.Create(
                CultureInfo.InvariantCulture,
                $"{label}\t{question.Set}\t{question.Name}\t{path}\t{temperature}\t{degree}\t{m.Milliseconds:F3}\t{m.CpuMilliseconds:F3}\t{m.Allocated}\t{m.Requests}\t{m.Steps}\t{m.Bytes}\t{m.States}\t{m.Rows}\t{m.Groups}\t{m.Digest}\n"));
        }
    }

    /// <summary>What one asking cost, and the digest of what it answered.</summary>
    private readonly record struct Measured(
        long Requests, long Steps, long Bytes, long States, long Rows, double Milliseconds, double CpuMilliseconds, long Allocated, long Groups, string? Digest);

    /// <summary>A dataset written under the store: its directory, its rows, and a request id in its middle.</summary>
    private sealed record Built(string Directory, long Rows, string Needle);

    /// <summary>
    /// The dataset <paramref name="name"/> under the store, written by <paramref name="fill"/> unless a note beside it says
    /// it was, whole.
    /// </summary>
    private static async Task<Built> EnsureAsync(Settings settings, string name, Func<IObjectStore, Task<(long Rows, string Needle)>> fill)
    {
        string directory = Path.Combine(settings.Store, name);
        string note = directory + ".built";
        if (!settings.Rebuild && System.IO.File.Exists(note))
        {
            string[] lines = System.IO.File.ReadAllLines(note);
            return new Built(directory, long.Parse(lines[0], CultureInfo.InvariantCulture), lines[1]);
        }

        System.IO.File.Delete(note);
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }

        Console.WriteLine($"writing {directory}");
        long begin = Stopwatch.GetTimestamp();
        (long rows, string needle) = await fill(new FileObjectStore(directory));
        double seconds = Stopwatch.GetElapsedTime(begin).TotalSeconds;
        long bytes = new DirectoryInfo(directory).EnumerateFiles("*", SearchOption.AllDirectories).Sum(file => file.Length);
        Console.WriteLine($"  {rows:N0} rows, {bytes / 1e9:F2} GB, written and compacted in {seconds:F0} s");
        System.IO.File.WriteAllLines(note, [rows.ToString(CultureInfo.InvariantCulture), needle, $"{bytes} bytes", $"{seconds:F0} s", Commit()]);
        return new Built(directory, rows, needle);
    }

    private static async Task<(long Rows, string Needle)> FillRequestsAsync(IObjectStore store, Settings settings)
    {
        VortexWriteOptions write = new VortexWriteOptions { Indexes = IndexPolicy.Auto.WithBudgetPerMille(settings.Budget) };
        return await QuestionBench.FillRequestsAsync(store, settings.Hours, settings.Rate, write, degree: settings.WriteDegree);
    }

    private static async Task<(long Rows, string Needle)> FillLogsAsync(IObjectStore store, Settings settings)
    {
        VortexWriteOptions write = new VortexWriteOptions { Indexes = IndexPolicy.Auto.WithBudgetPerMille(settings.Budget) };
        return (await QuestionBench.FillLogsAsync(store, settings.LogHours, settings.LogRate, write, null, settings.WriteDegree), string.Empty);
    }

    /// <summary>The commit the bench was built from, and whether the tree is dirty.</summary>
    private static string Commit()
    {
        try
        {
            ProcessStartInfo start = new ProcessStartInfo("git", "describe --always --dirty") { RedirectStandardOutput = true, UseShellExecute = false };
            using Process git = Process.Start(start)!;
            string text = git.StandardOutput.ReadToEnd().Trim();
            git.WaitForExit();
            return text.Length > 0 ? text : "unknown";
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return "unknown";
        }
    }
}
