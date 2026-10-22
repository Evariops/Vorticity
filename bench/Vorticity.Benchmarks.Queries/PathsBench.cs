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
/// <c>~/.cache/vorticity/paths</c>, <see cref="BenchStores"/>), each request answered <c>--latency</c> ms (5 by default) after it is sent, to the
/// tenth of a millisecond (<see cref="PreciseDelay"/>), the reads' bytes crossing one link of <c>--gbps</c> (10 by
/// default, 0 for none) and each read's at most <c>--mbps</c> when set; the synopsis cache reads its own disk, with
/// neither. A dataset is written the first time its parameters are asked for (<c>--hours</c>, <c>--rate</c> requests a
/// second, <c>--log-rate</c> log records, <c>--design</c> at the design's own scale, its days loaded as sealed periods or,
/// with <c>--incremental</c>, its hours appended and compacted as they come, <see cref="DesignBuild"/>) and kept beside a
/// note of what it holds; <c>--rebuild</c> writes it again.
/// </para>
/// <para>
/// Each line says what the store saw (requests, dependent round trips, bytes), what the answer read (states, rows),
/// its time and processor time, the bytes the process allocated, every thread counted, and the most private bytes and
/// working set it held above what it held as the ask began (<see cref="MemoryPeaks"/>). Cold is a session and a
/// handle opened for the question with an empty cache, its open not counted; warm is the median of <c>--rounds</c>
/// asks by the same handle (<c>--rows-rounds</c> for the rows). With <c>--verify</c> each question's answer from
/// the synopses is held to the one from its rows, digest against digest. <c>--tsv</c> appends every line to a
/// file that <c>--compare</c> reads back.
/// </para>
/// </remarks>
internal static partial class PathsBench
{
    /// <summary>What a run asks for.</summary>
    internal sealed record Settings
    {
        internal string Store { get; init; } = Environment.GetEnvironmentVariable("VORTICITY_PATHS_STORE") ?? BenchStores.Of("paths");

        internal int Hours { get; init; } = 24;

        internal double Rate { get; init; } = 1_000;

        internal double LogRate { get; init; } = 1_000;

        internal int LogHours { get; init; } = 12;

        internal int Latency { get; init; } = 5;

        /// <summary>The round trip's 99th percentile in milliseconds, its waits drawn around <see cref="Latency"/>; zero for every wait at the median.</summary>
        internal int LatencyP99 { get; init; }

        /// <summary>The reads a second the store serves a prefix of keys at most; zero for no limit.</summary>
        internal double ReadsPerSecondPerPrefix { get; init; }

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

        /// <summary>Whether the requests' ids look random, 32 hexadecimal digits of a hash, rather than count up (<c>--random-ids</c>).</summary>
        internal bool RandomIds { get; init; }

        /// <summary>Whether the bench asks the client's questions, QP-1 to QP-10, over the requests and the client's spans and log records (<c>--qp</c>).</summary>
        internal bool Qp { get; init; }

        /// <summary>The hours of the client's spans and log records (<c>--qp-hours</c>): a day sealed, and the hours of the next fresh.</summary>
        internal int QpHours { get; init; } = 30;

        /// <summary>Their rows a second (<c>--qp-rate</c>): a tenth of the design's.</summary>
        internal double QpRate { get; init; } = 5_000;

        /// <summary>
        /// Whether the client's spans and log records declare what their questions read (<c>--declared</c>): groupings of the
        /// spans' synopsis columns for QP-1 and QP-2, and the log records' status code and feature flag shredded, on append too,
        /// for QP-3, QP-9 and QP-10. Their stores are named apart.
        /// </summary>
        internal bool Declared { get; init; }

        internal bool BuildOnly { get; init; }

        internal bool Verify { get; init; }

        /// <summary>Whether each question's plan is printed once warm: what each object reads, a table, its edges or its rows, and why.</summary>
        internal bool Explain { get; init; }

        /// <summary>Whether the bench only prints each object's entry and its table of contents, in bytes (<c>--entries</c>).</summary>
        internal bool Entries { get; init; }

        internal bool Design { get; init; }

        /// <summary>Whether <c>--design</c> appends its hours and compacts them as they come (<see cref="DesignBuild.FillAsync"/>) rather than load its days as sealed periods (<see cref="DesignBuild.LoadAsync"/>).</summary>
        internal bool Incremental { get; init; }

        /// <summary>The days a load of <c>--design</c> writes at once.</summary>
        internal int ParallelPeriods { get; init; } = 4;

        internal int ParallelHours { get; init; } = 4;

        internal int PartMinutes { get; init; } = 10;

        internal double LoopSeconds { get; init; }

        /// <summary>How long to ask the question cold again and again, each by a handle and a cache of its own, for a profiler; zero for no loop.</summary>
        internal double LoopColdSeconds { get; init; }

        /// <summary>
        /// Whether the batches are taken without a digest, for a profiler: a digest of a question's rows runs while the
        /// scan decodes its next split, which leaving its time out of the measure does not undo.
        /// </summary>
        internal bool NoDigest { get; init; }

        /// <summary>The reads each session keeps in flight at most (<see cref="VortexSessionOptions.MaxConcurrentReads"/>); zero for the session's default.</summary>
        internal int MaxReads { get; init; }

        /// <summary>
        /// The bytes of the segment cache of the session a question is asked warm by (<see cref="VortexSessionOptions.SegmentCache"/>,
        /// <c>--segment-cache-mb</c>), as a query node keeps the segments it read; zero for none, the default, which has every
        /// warm round read its rows again.
        /// </summary>
        internal long SegmentCacheBytes { get; init; }

        /// <summary>Whether the store keeps each object it reads open (<see cref="HeldFileObjectStore"/>) rather than open and close its file at every read.</summary>
        internal bool KeepHandles { get; init; }

        /// <summary>How long each question is asked untimed before its warm rounds, its code compiled past the JIT's first tier.</summary>
        internal double WarmSeconds { get; init; } = 3;

        /// <summary>How long <c>--qps</c> asks the questions for a count of clients; 0, the default, for no throughput.</summary>
        internal double QpsSeconds { get; init; }

        /// <summary>The counts of clients <c>--qps</c> asks the questions with at once (<c>--clients</c>).</summary>
        internal int[] Clients { get; init; } = [1, Environment.ProcessorCount];

        /// <summary>Whether <c>--qps</c> also gives each question's asks and latencies (<c>--qps-by-question</c>).</summary>
        internal bool QpsByQuestion { get; init; }

        /// <summary>The objects each handle of <c>--qps</c> keeps open (<c>--open-objects</c>); 0 for the dataset's default.</summary>
        internal int OpenObjects { get; init; }

        internal string? Tsv { get; init; }

        internal string? Label { get; init; }

        internal string? TraceReads { get; init; }
    }

    internal static async Task<int> Run(Settings settings)
    {
        if (settings.Verify && settings.NoDigest)
        {
            Console.Error.WriteLine("--verify holds answers to their digests, which --no-digest leaves untaken.");
            return 2;
        }

        Directory.CreateDirectory(settings.Store);
        Built requests = await EnsureAsync(
            settings,
            $"requests-{(!settings.Design ? string.Empty : settings.Incremental ? "design-" : "design-load-")}h{settings.Hours}-r{settings.Rate:0}-b{settings.Budget}{(settings.RandomIds ? "-rid" : string.Empty)}",
            store => !settings.Design ? FillRequestsAsync(store, settings)
                : settings.Incremental
                ? DesignBuild.FillAsync(store, settings.Hours, settings.Rate, settings.Budget, settings.ParallelHours, settings.WriteDegree, settings.PartMinutes, Path.Combine(settings.Store, "scratch"), settings.RandomIds)
                : DesignBuild.LoadAsync(store, settings.Hours, settings.Rate, settings.Budget, settings.ParallelPeriods, settings.WriteDegree, Path.Combine(settings.Store, "scratch"), settings.RandomIds));
        Built logs = await EnsureAsync(settings, $"logs-h{settings.LogHours}-r{settings.LogRate:0}-b{settings.Budget}", store => FillLogsAsync(store, settings));
        Built? spans = !settings.Qp ? null
            : await EnsureAsync(settings, $"spans-v2{(settings.Declared ? "-declared" : string.Empty)}-h{settings.QpHours}-r{settings.QpRate:0}-b{settings.Budget}", store => FillSpansAsync(store, settings));
        Built? logs2 = !settings.Qp ? null
            : await EnsureAsync(settings, $"logs-v2{(settings.Declared ? "-declared" : string.Empty)}-h{settings.QpHours}-r{settings.QpRate:0}-b{settings.Budget}", store => FillLogsV2Async(store, settings));
        if (settings.BuildOnly)
        {
            return 0;
        }

        // --entries: each store's entries, and with --explain its tables, with no question asked.
        if (settings.Entries)
        {
            foreach (Built? built in new[] { requests, logs, spans, logs2 })
            {
                if (built is not null)
                {
                    Console.WriteLine($"  {built.Directory}");
                    await EntriesAsync(built.Directory);
                    if (settings.Explain)
                    {
                        await TablesAsync(built);
                    }
                }
            }

            return 0;
        }

        string label = settings.Label ?? Commit();
        Console.WriteLine(
            $"store {settings.Store}: {requests.Rows:N0} requests over {settings.Hours} h, {logs.Rows:N0} log records over {settings.LogHours} h; " +
            $"{settings.Latency} ms a request{(settings.LatencyP99 > settings.Latency ? $" at the median, {settings.LatencyP99} at the 99th percentile" : string.Empty)}, " +
            $"{(settings.LinkBitsPerSecond > 0 ? $"a link of {settings.LinkBitsPerSecond / 1e9:0.##} Gb/s" : "no limit on the link")}{(settings.BytesPerSecond > 0 ? $", {settings.BytesPerSecond / 1e6:0} MB/s a read" : string.Empty)}" +
            $"{(settings.ReadsPerSecondPerPrefix > 0 ? $", {settings.ReadsPerSecondPerPrefix:0} reads a second a prefix" : string.Empty)}; " +
            $"warm is the median of {settings.Rounds} ({settings.RowsRounds} for rows){(settings.SegmentCacheBytes > 0 ? $", segments kept in {settings.SegmentCacheBytes >> 20} MiB" : string.Empty)}; processor time from cycles at {ProcessCpu.GigaHertz:F2} GHz; {label}");
        Console.WriteLine(
            $"{"question",-62} {"path",-8} {"",-4} {"dop",3} {"ms",9} {"cpu ms",9} {"alloc MB",9} {"priv MB",8} {"ws MB",8} {"req",6} {"steps",6} {"MB",8} {"states",9} {"rows",11} {"groups",7}");

        if (settings.Explain)
        {
            foreach (Built? built in new[] { requests, logs, spans, logs2 })
            {
                if (built is not null)
                {
                    await TablesAsync(built);
                }
            }
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

        if (settings.Qp)
        {
            questions = QpQuestions(settings, requests.Needle, spans!.Needle, half);
        }

        if (settings.QpsSeconds > 0)
        {
            Question[] asked = [.. questions.Where(question =>
                settings.Only is not { } only || only.Split('|').Any(word => question.Name.Contains(word, StringComparison.OrdinalIgnoreCase)))];
            await ThroughputAsync(
                settings, asked, question => question.Set switch { "logs" => logs, "spans" => spans!, "logs2" => logs2!, _ => requests }, label);
            return 0;
        }

        bool failed = false;
        foreach (int degree in settings.Degrees)
        {
            foreach (Question question in questions)
            {
                if (settings.Only is { } only && !only.Split('|').Any(word => question.Name.Contains(word, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                Built built = question.Set switch { "logs" => logs, "spans" => spans!, "logs2" => logs2!, _ => requests };
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

                    if (synopses && question.Window is { } window && settings.Temperatures is "all" or "cold")
                    {
                        Print(settings, label, question, path, "first", degree, await FirstAsync(settings, built, window, degree));
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

    /// <summary>
    /// <c>--entries</c>, <c>--micro entries</c>: each object's entry in the tree of the dataset at
    /// <paramref name="directory"/>, which a walk reads for every object it plans and the handle's page cache keeps, and
    /// what the table of contents of its synopsis holds: its slices, its tables' chunks, its synopsis columns' chunks, its
    /// schema.
    /// </summary>
    internal static async Task EntriesAsync(string directory)
    {
        await using FileObjectStore files = new FileObjectStore(directory);
        await using VortexDataset dataset = await VortexDataset.OpenAsync(files, new DatasetOptions());
        long total = 0;
        await foreach (PositionedObject held in dataset.WalkAsync(null, 0, long.MaxValue, null, default))
        {
            Vorticity.Dataset.ObjectEntry entry = held.Entry;
            long bytes = entry.ToBytes().Length;
            total += bytes;
            string parts = entry.Contents is not { } contents ? "no table of contents" : string.Create(
                CultureInfo.InvariantCulture,
                $"contents {entry.Synopsis.Length / 1e6:F2} MB: {contents.Slices.Slices.Count} slices in {contents.Slices.ToBytes().Length / 1e3:F1} KB, " +
                $"{contents.Tables.Count} tables of {contents.Tables.Sum(table => table.Chunks.Count)} chunks, schema {contents.Schema.Length} B");
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"    {entry.Key}: {entry.Rows:N0} rows, entry {bytes / 1e6:F2} MB, {parts}"));
        }

        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"    {total / 1e6:F2} MB of entries"));
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
                string distinct = table.Distinct.Length == 0 ? string.Empty : $", distinct ({string.Join(", ", table.Distinct.Select(field =>
                    Vorticity.Indexes.SynopsisTableInfo.NameOf(field, schema, contents.Slices.Paths) + (table.HoldsDistinct(field) ? string.Empty : " lost")))})";
                Console.WriteLine($"    tables {held.Entry.Key}: {table.Kind} by {table.Bucket} on ({keys}) of [{measures}]{distinct}, {table.Rows:N0} rows in {chunks.Count} chunks");
            }
        }
    }

    /// <summary>A question of the catalogue: its dataset, its name, and how it is asked of a dataset under scan options.</summary>
    private sealed record Question(string Set, string Name, bool Aggregates, Func<VortexDataset, ScanOptions, Asking> Ask)
    {
        /// <summary>
        /// The window a newest-first asking cuts at its periods' starts, and the question asked over a part of it; null when the
        /// question has no first result to time (<see cref="FirstAsync"/>).
        /// </summary>
        internal (DateTimeOffset From, DateTimeOffset To, Func<VortexDataset, ScanOptions, DateTimeOffset, DateTimeOffset, Asking> Ask)? Window { get; init; }
    }

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

    /// <summary>
    /// An aggregation, asked as <see cref="Asked"/> does, whose first result <c>--qp</c> times too: the part of the window from
    /// <paramref name="from"/> to <paramref name="to"/> a newest-first asking delivers first, each part bounded by
    /// <paramref name="within"/>.
    /// </summary>
    private static Question AskedOver<TRecord>(
        string set, string name, DateTimeOffset from, DateTimeOffset to, Func<Scan<TRecord>, DateTimeOffset, DateTimeOffset, Scan<TRecord>> within,
        Func<Scan<TRecord>, Aggregation> ask)
        where TRecord : IVortexRecord<TRecord> =>
        Asked(set, name, ask) with
        {
            Window = (from, to, (dataset, options, partFrom, partTo) =>
            {
                Aggregation aggregation = ask(within(dataset.Scan<TRecord>().With(options), partFrom, partTo));
                return new Asking(aggregation.Query.Batches(default), () => aggregation.Metrics);
            }),
        };

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
                    o.MaxConcurrentReads = settings.MaxReads > 0 ? settings.MaxReads : o.MaxConcurrentReads;
                    o.SynopsisCache = new SynopsisCache(cache, 8L << 30);
                });
                await using VortexDataset dataset = await VortexDataset.OpenAsync(store, new DatasetOptions { Session = session });
                Measured again = await MeasureAsync(store, dataset, question, options, !settings.NoDigest);
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
        LatencyObjectStore remote = new LatencyObjectStore(settings.KeepHandles ? new HeldFileObjectStore(files) : files);
        await using CountingObjectStore store = new CountingObjectStore(remote);
        {
            await using VortexSession session = VortexSession.Create(o =>
            {
                o.MaxDegreeOfParallelism = degree;
                o.MaxConcurrentReads = settings.MaxReads > 0 ? settings.MaxReads : o.MaxConcurrentReads;
            });
            await using VortexDataset dataset = await VortexDataset.OpenAsync(store, new DatasetOptions { Session = session });
            await MeasureAsync(store, dataset, question, options, !settings.NoDigest);
        }

        remote.Latency = TimeSpan.FromMilliseconds(settings.Latency);
        remote.LatencyP99 = TimeSpan.FromMilliseconds(settings.LatencyP99);
        remote.ReadsPerSecondPerPrefix = settings.ReadsPerSecondPerPrefix;
        remote.LinkBitsPerSecond = settings.LinkBitsPerSecond;
        remote.BytesPerSecond = settings.BytesPerSecond;
        string cache = Path.Combine(settings.Store, "cache", Guid.NewGuid().ToString("N"));
        try
        {
            await using VortexSession session = VortexSession.Create(o =>
            {
                o.MaxDegreeOfParallelism = degree;
                o.MaxConcurrentReads = settings.MaxReads > 0 ? settings.MaxReads : o.MaxConcurrentReads;
                o.SynopsisCache = new SynopsisCache(cache, 8L << 30);
                o.SegmentCache = settings.SegmentCacheBytes > 0 ? new SegmentCache(settings.SegmentCacheBytes) : null;
            });
            await using VortexDataset dataset = await VortexDataset.OpenAsync(store, new DatasetOptions { Session = session });
            using TextWriter? trace = settings.TraceReads is { } traced ? TextWriter.Synchronized(new StreamWriter(traced, append: true)) : null;
            trace?.WriteLine($"# {question.Name} {(synopses ? "synopses" : "rows")} cold, degree {degree}");
            remote.Trace = trace;
            Measured cold = settings.Temperatures is "all" or "cold" ? await MeasureAsync(store, dataset, question, options, !settings.NoDigest) : default;
            remote.Trace = null;
            await LoopColdAsync(settings, store, question, options, degree);
            if (settings.Temperatures is "cold")
            {
                return (cold, default);
            }

            // Untimed asks before the timed rounds, for as long as --warm says: a question of a few milliseconds asked a
            // few times after its cold round ran the JIT's first tier still, 25.3 ms a round where it takes 6.6 ms
            // compiled (one host per minute over probe7, 2026-10-20), and 23.7 ms after a second of asks, 6.1 after
            // three. Once at least where no cold round came first.
            long warmUntil = Stopwatch.GetTimestamp() + (long)(settings.WarmSeconds * Stopwatch.Frequency);
            for (bool once = settings.Temperatures is "warm"; once || Stopwatch.GetTimestamp() < warmUntil; once = false)
            {
                await MeasureAsync(store, dataset, question, options, !settings.NoDigest);
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

                warm.Add(await MeasureAsync(store, dataset, question, options, !settings.NoDigest));
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
                    Measured again = await MeasureAsync(store, dataset, question, options, !settings.NoDigest);
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

    private static async Task<Measured> MeasureAsync(CountingObjectStore store, VortexDataset dataset, Question question, ScanOptions options, bool digests)
    {
        store.Reset();
        Asking asking = question.Ask(dataset, options);
        MemoryPeaks.Window memory = MemoryPeaks.Open();
        long allocated = GC.GetTotalAllocatedBytes(precise: true);
        double cpu = ProcessCpu.Milliseconds();
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
                if (digests)
                {
                    long taken = Stopwatch.GetTimestamp();
                    digest.Add(batch);
                    digesting += Stopwatch.GetTimestamp() - taken;
                }
            }
        }

        double milliseconds = (Stopwatch.GetTimestamp() - begin - digesting) * 1000.0 / Stopwatch.Frequency;
        double cpuMilliseconds = ProcessCpu.Milliseconds() - cpu;
        long bytes = GC.GetTotalAllocatedBytes(precise: true) - allocated;
        MemoryPeaks.Close(memory);
        ScanMetrics metrics = asking.Metrics();
        return new Measured(
            store.Requests, store.DependentSteps, store.BytesRead, metrics.SynopsisRows, metrics.Rows, milliseconds, cpuMilliseconds, bytes, groups,
            digests ? digest.ToString() : "-", memory.Private, memory.Working);
    }

    /// <summary>
    /// The first result of an aggregation, as the client's review defines it: cold, by a session and a handle opened for it with
    /// an empty synopsis cache, the window asked part by part newest first (<see cref="VortexDataset.AskNewestFirstAsync"/>), and
    /// the time its first part took to arrive. The other figures are the whole asking's, its groups the parts, its rows the
    /// answers' rows.
    /// </summary>
    private static async Task<Measured> FirstAsync(
        Settings settings, Built built, (DateTimeOffset From, DateTimeOffset To, Func<VortexDataset, ScanOptions, DateTimeOffset, DateTimeOffset, Asking> Ask) window,
        int degree)
    {
        ScanOptions options = new ScanOptions { UseSynopses = true, DegreeOfParallelism = degree };
        await using FileObjectStore files = new FileObjectStore(built.Directory);
        LatencyObjectStore remote = new LatencyObjectStore(settings.KeepHandles ? new HeldFileObjectStore(files) : files)
        {
            Latency = TimeSpan.FromMilliseconds(settings.Latency),
            LatencyP99 = TimeSpan.FromMilliseconds(settings.LatencyP99),
            ReadsPerSecondPerPrefix = settings.ReadsPerSecondPerPrefix,
            LinkBitsPerSecond = settings.LinkBitsPerSecond,
            BytesPerSecond = settings.BytesPerSecond,
        };
        await using CountingObjectStore store = new CountingObjectStore(remote);
        string cache = Path.Combine(settings.Store, "cache", Guid.NewGuid().ToString("N"));
        try
        {
            await using VortexSession session = VortexSession.Create(o =>
            {
                o.MaxDegreeOfParallelism = degree;
                o.MaxConcurrentReads = settings.MaxReads > 0 ? settings.MaxReads : o.MaxConcurrentReads;
                o.SynopsisCache = new SynopsisCache(cache, 8L << 30);
            });
            await using VortexDataset dataset = await VortexDataset.OpenAsync(store, new DatasetOptions { Session = session });
            store.Reset();
            long allocated = GC.GetTotalAllocatedBytes(precise: true);
            double cpu = ProcessCpu.Milliseconds();
            long begin = Stopwatch.GetTimestamp();
            double first = 0;
            long parts = 0;
            long rows = 0;
            await foreach (WindowPart<long> part in dataset.AskNewestFirstAsync(window.From, window.To, async (from, to, ct) =>
            {
                Asking asking = window.Ask(dataset, options, from, to);
                long answered = 0;
                await using (asking.Batches)
                {
                    while (await asking.Batches.MoveNextAsync())
                    {
                        answered += asking.Batches.Current.SelectedRows;
                    }
                }

                return answered;
            }))
            {
                if (parts++ == 0)
                {
                    first = Stopwatch.GetElapsedTime(begin).TotalMilliseconds;
                }

                rows += part.Answer;
            }

            return new Measured(
                store.Requests, store.DependentSteps, store.BytesRead, 0, rows, first, ProcessCpu.Milliseconds() - cpu,
                GC.GetTotalAllocatedBytes(precise: true) - allocated, parts, Digest: null);
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

    private static void Print(Settings settings, string label, Question question, string path, string temperature, int degree, Measured m)
    {
        string name = temperature == "cold" ? question.Name : string.Empty;
        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"{name,-62} {path,-8} {temperature,-4} {degree,3} {m.Milliseconds,9:F1} {m.CpuMilliseconds,9:F1} {m.Allocated / 1e6,9:F2} {m.PeakPrivate / 1e6,8:F1} {m.PeakWorking / 1e6,8:F1} {m.Requests,6} {m.Steps,6} {m.Bytes / 1e6,8:F2} {m.States,9} {m.Rows,11} {m.Groups,7}"));
        if (settings.Tsv is { } tsv)
        {
            System.IO.File.AppendAllText(tsv, string.Create(
                CultureInfo.InvariantCulture,
                $"{label}\t{question.Set}\t{question.Name}\t{path}\t{temperature}\t{degree}\t{m.Milliseconds:F3}\t{m.CpuMilliseconds:F3}\t{m.Allocated}\t{m.Requests}\t{m.Steps}\t{m.Bytes}\t{m.States}\t{m.Rows}\t{m.Groups}\t{m.Digest}\t{m.PeakPrivate}\t{m.PeakWorking}\n"));
        }
    }

    /// <summary>
    /// What one asking cost, the digest of what it answered, and the most private bytes and working set the process held
    /// above what it held when the asking began (<see cref="MemoryPeaks"/>).
    /// </summary>
    private readonly record struct Measured(
        long Requests, long Steps, long Bytes, long States, long Rows, double Milliseconds, double CpuMilliseconds, long Allocated, long Groups, string? Digest,
        long PeakPrivate = 0, long PeakWorking = 0);

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
        return await QuestionBench.FillRequestsAsync(store, settings.Hours, settings.Rate, write, degree: settings.WriteDegree, randomIds: settings.RandomIds);
    }

    private static async Task<(long Rows, string Needle)> FillLogsAsync(IObjectStore store, Settings settings)
    {
        VortexWriteOptions write = new VortexWriteOptions { Indexes = IndexPolicy.Auto.WithBudgetPerMille(settings.Budget) };
        return (await QuestionBench.FillLogsAsync(store, settings.LogHours, settings.LogRate, write, null, settings.WriteDegree), string.Empty);
    }

    /// <summary>
    /// The client's spans (<see cref="SpanGeneratorV2"/>) of <c>--qp-hours</c> at <c>--qp-rate</c>, appended and compacted
    /// as a client's ingestion does, in periods of a day, their trace and span ids declared identifiers; and the trace id of
    /// a span in the middle, a needle.
    /// </summary>
    private static async Task<(long Rows, string Needle)> FillSpansAsync(IObjectStore store, Settings settings)
    {
        Directory.CreateDirectory(Path.Combine(settings.Store, "scratch"));
        await using VortexSession session = VortexSession.Create(options => options.MaxDegreeOfParallelism = settings.WriteDegree);
        long perHour = (long)(settings.QpRate * 3_600);
        int middle = settings.QpHours / 2;
        string needle = string.Empty;
        DatasetOptions options = new DatasetOptions
        {
            Session = session,
            ClusteringKey = [nameof(GeneratedSpanV2.At)],
            Periods = new PeriodOptions { Lateness = TimeSpan.FromMinutes(5), Length = SliceWidth.Day },
            Identifiers = [nameof(GeneratedSpanV2.TraceId), nameof(GeneratedSpanV2.SpanId)],
            Write = new VortexWriteOptions { Indexes = IndexPolicy.Auto.WithBudgetPerMille(settings.Budget), ScratchDirectory = Path.Combine(settings.Store, "scratch") },

            // --declared: the columns QP-1 and QP-2 filter and group by together, which the order of the spans' facets
            // places far apart, and the service alone, whose durations' sketches by slice answer QP-6's last minutes.
            SynopsisGroupings = !settings.Declared ? null :
            [
                [nameof(GeneratedSpanV2.Service), nameof(GeneratedSpanV2.HttpStatus), nameof(GeneratedSpanV2.Route)],
                [nameof(GeneratedSpanV2.Route), nameof(GeneratedSpanV2.Pod)],
                [nameof(GeneratedSpanV2.Pod), nameof(GeneratedSpanV2.Status), nameof(GeneratedSpanV2.ExceptionGroup)],
                [nameof(GeneratedSpanV2.Service)],
            ],
        };
        long rows = await IncrementalBuild.FillAsync<GeneratedSpanV2>(store, options, settings.QpHours, perHour, settings.ParallelHours, settings.PartMinutes, hour =>
        {
            SpanGeneratorV2 generator = new SpanGeneratorV2(2_000 + hour, hour * perHour);
            long drawn = 0;
            return at =>
            {
                GeneratedSpanV2 span = generator.Next(at);
                if (hour == middle && ++drawn == perHour / 2)
                {
                    needle = span.TraceId;
                }

                return span;
            };
        });
        return (rows, needle);
    }

    /// <summary>The log records' status code as QP-3 and QP-9 read it.</summary>
    private const string StatusCode = "Attributes['http.response.status_code'].AsInt64()";

    /// <summary>The feature flag QP-10 reads.</summary>
    private const string NewCart = "Attributes['feature.flag.new-cart'].AsString()";

    /// <summary>The client's log records (<see cref="LogGeneratorV2"/>) of <c>--qp-hours</c> at <c>--qp-rate</c>, as the spans.</summary>
    private static async Task<(long Rows, string Needle)> FillLogsV2Async(IObjectStore store, Settings settings)
    {
        Directory.CreateDirectory(Path.Combine(settings.Store, "scratch"));
        await using VortexSession session = VortexSession.Create(options => options.MaxDegreeOfParallelism = settings.WriteDegree);
        long perHour = (long)(settings.QpRate * 3_600);
        DatasetOptions options = new DatasetOptions
        {
            Session = session,
            ClusteringKey = [nameof(LogRow.At)],
            Periods = new PeriodOptions { Lateness = TimeSpan.FromMinutes(5), Length = SliceWidth.Day },
            Write = new VortexWriteOptions { Indexes = IndexPolicy.Auto.WithBudgetPerMille(settings.Budget), ScratchDirectory = Path.Combine(settings.Store, "scratch") },

            // --declared: the keys of the attributes QP-3, QP-9 and QP-10 filter by, shredded by every job and every append,
            // and the service they group by with them.
            ShreddedPaths = !settings.Declared ? null : [StatusCode, NewCart],
            ShredOnAppend = settings.Declared,
            SynopsisGroupings = !settings.Declared ? null : [[StatusCode, nameof(LogRow.ServiceName)], [NewCart, nameof(LogRow.ServiceName)]],
        };
        long rows = await IncrementalBuild.FillAsync<LogRow>(store, options, settings.QpHours, perHour, settings.ParallelHours, settings.PartMinutes, hour =>
        {
            LogGeneratorV2 generator = new LogGeneratorV2(3_000 + hour);
            return generator.Next;
        });
        return (rows, string.Empty);
    }

    /// <summary>
    /// The client's questions, QP-1 to QP-10 but QP-8, which needs thirteen months, as its review writes them: over the
    /// spans, the log records and the requests, "now" the end of their hours. The requests' ids hold no null, so an exclusion
    /// leaves out the review's "|| id == null".
    /// </summary>
    private static Question[] QpQuestions(Settings settings, string requestNeedle, string traceNeedle, TimeSpan half)
    {
        DateTimeOffset start = QuestionBench.Start;
        DateTimeOffset end = start.AddHours(settings.QpHours);
        DateTimeOffset requestsEnd = start.AddHours(settings.Hours);
        DateTimeOffset lastQuarter = end.AddMinutes(-15);
        string pod = SpanGeneratorV2.PodOf(0, 0, 0);
        static Scan<GeneratedSpanV2> Spans(Scan<GeneratedSpanV2> scan, DateTimeOffset from, DateTimeOffset to) => scan.Where(s => s.At >= from && s.At < to);
        static Scan<LogRow> Logs(Scan<LogRow> scan, DateTimeOffset from, DateTimeOffset to) => scan.Where(r => r.At >= from && r.At < to);
        static Scan<GeneratedRequest> Requests(Scan<GeneratedRequest> scan, DateTimeOffset from, DateTimeOffset to) => scan.Where(r => r.At >= from && r.At < to);
        return
        [
            AskedOver<GeneratedSpanV2>("spans", "QP-1 one service's routes under 5xx, per hour", start, end, Spans, scan => scan.Where(s => s.Service == "svc-000" && s.HttpStatus >= 500).GroupBy(s => (s.Route, s.At.Truncate(CalendarUnit.Hour))).Select(g => (g.Key.Route, g.Key.Item2, g.Count()))),
            AskedOver<GeneratedSpanV2>("spans", "QP-2 the pods of one route", start, end, Spans, scan => scan.Where(s => s.Route == "/api/s0/r0").GroupBy(s => s.Pod).Select(g => (g.Key, g.Count()))),
            AskedOver<GeneratedSpanV2>("spans", "QP-2 the exception groups of one pod's errors", start, end, Spans, scan => scan.Where(s => s.Pod == pod && s.Status == "error").GroupBy(s => s.ExceptionGroup).Select(g => (g.Key, g.Count()))),
            AskedOver<LogRow>("logs2", "QP-3 services under status_code AsInt64() >= 500", start, end, Logs, scan => scan.Where(r => r.Attributes["http.response.status_code"].AsInt64() >= 500).GroupBy(r => r.ServiceName).Select(g => (g.Key, g.Count()))),
            Rows<GeneratedSpanV2>("spans", "QP-4 the spans of one trace (needle)", scan => scan.Where(s => s.TraceId == traceNeedle)),
            Rows<GeneratedRequest>("requests", "QP-4 the rows of one request id (needle)", scan => scan.Where(r => r.RequestId == requestNeedle)),
            AskedOver<GeneratedRequest>("requests", "QP-5 every row but one request's, per service and per 12 hours", start, requestsEnd, Requests, scan => scan.Where(r => r.RequestId != requestNeedle).GroupBy(r => (r.Service, r.At.Bucket(half))).Select(g => (g.Key.Service, g.Key.Item2, g.Count()))),
            AskedOver<GeneratedSpanV2>("spans", "QP-6 the 99th percentile of duration per service, the last 15 minutes", lastQuarter, end, Spans, scan => scan.Where(s => s.At >= lastQuarter).GroupBy(s => s.Service).Select(g => (g.Key, g.Quantile(s => s.DurationNs, 0.99)))),
            AskedOver<GeneratedRequest>("requests", "QP-7 distinct hosts per region, but one request's", start, requestsEnd, Requests, scan => scan.Where(r => r.RequestId != requestNeedle).GroupBy(r => r.Region).Select(g => (g.Key, g.ApproximateCountDistinct(r => r.Host)))),
            AskedOver<LogRow>("logs2", "QP-9 services under status_code AsInt64() >= 500, the last 15 minutes", lastQuarter, end, Logs, scan => scan.Where(r => r.Attributes["http.response.status_code"].AsInt64() >= 500 && r.At >= lastQuarter).GroupBy(r => r.ServiceName).Select(g => (g.Key, g.Count()))),
            AskedOver<LogRow>("logs2", "QP-10 feature.flag.new-cart == on, per service", start, end, Logs, scan => scan.Where(r => r.Attributes["feature.flag.new-cart"].AsString() == "on").GroupBy(r => r.ServiceName).Select(g => (g.Key, g.Count()))),
        ];
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
