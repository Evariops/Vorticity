using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.IO;

namespace Vorticity.Benchmarks.Queries;

/// <summary>
/// The questions of the time-window and variant designs, asked of generated datasets (<c>--micro questions</c>):
/// requests (<see cref="RequestGenerator"/>) and log records (<see cref="LogGenerator"/>) over hours of periods,
/// in memory behind a store that counts and waits a round trip per request. Each question is asked cold, by a
/// handle and a session opened for it with an empty synopsis cache on disk, then warm, by the same handle
/// again, its cache filled, from its synopses and then from its rows. Each line says what the store saw
/// (requests, dependent round trips, bytes) and what the answer read (table rows folded as states, data rows),
/// with its time and the process's CPU time.
/// </summary>
/// <remarks>
/// The examples of the designs are thirty periods of a day at 5 × 10⁴ requests a second. The bench keeps their
/// shape at the scale of the Mac: periods of an hour (<c>--hours</c>, 24 by default), 20 requests a second
/// (<c>--rate</c>) and half as many log records, 30 ms a round trip (<c>--latency</c>). At that scale an hour's
/// tables weigh far more against its rows than a day's do at the design's rate, so the default budget of the
/// tables (<c>--budget</c>, in thousandths of the data, 100) keeps the shallow ones only. The counts are the
/// figures to hold; the times only say where the time goes.
/// </remarks>
internal static class QuestionBench
{
    /// <summary>The instant the generated datasets start at.</summary>
    internal static readonly DateTimeOffset Start = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

    internal static async Task<int> Run(int hours, double rate, int latency, int rounds, int budget)
    {
        await using MemoryObjectStore requestBytes = new MemoryObjectStore();
        await using CountingObjectStore requests = new CountingObjectStore(requestBytes);
        await using MemoryObjectStore logBytes = new MemoryObjectStore();
        await using CountingObjectStore logs = new CountingObjectStore(logBytes);

        long begin = Stopwatch.GetTimestamp();
        VortexWriteOptions write = new VortexWriteOptions { Indexes = IndexPolicy.Auto.WithBudgetPerMille(budget) };
        (long requestRows, string needle) = await FillRequestsAsync(requests, hours, rate, write);
        long logRows = await FillLogsAsync(logs, hours, rate / 2, write);
        Console.WriteLine(
            $"{requestRows} requests and {logRows} log records over {hours} periods of an hour, written and compacted in " +
            $"{Stopwatch.GetElapsedTime(begin).TotalSeconds:F1} s; tables up to {budget} ‰ of the data; {latency} ms a round trip; warm is the best of {rounds}");
        requestBytes.Latency = TimeSpan.FromMilliseconds(latency);
        logBytes.Latency = TimeSpan.FromMilliseconds(latency);

        DateTimeOffset end = Start.AddHours(hours);
        DateTimeOffset lastHour = end.AddHours(-1);
        TimeSpan half = TimeSpan.FromHours(12);
        DateTimeOffset from = Start.AddMinutes(37.5);
        DateTimeOffset to = end.AddMinutes(-13);
        Console.WriteLine($"{"question",-58} {"path",-8} {"",-4} {"requests",8} {"steps",6} {"MB",8} {"states",9} {"rows",9} {"ms",9} {"cpu ms",8}");

        (string Name, Func<Scan<GeneratedRequest>, Aggregation> Ask)[] requestQuestions =
        [
            ("a count per service and per 12 hours", scan => scan.GroupBy(r => (r.Service, r.At.Bucket(half))).Select(g => (g.Key.Service, g.Key.Item2, g.Count()))),
            ("services under Method == GET", scan => scan.Where(r => r.Method == "GET").GroupBy(r => r.Service).Select(g => (g.Key, g.Count()))),
            ("errors outside sa-east by region and method, per hour", scan => scan.Where(r => r.Status >= 500 && r.Region != "sa-east").GroupBy(r => (r.Region, r.Method, r.At.Truncate(CalendarUnit.Hour))).Select(g => (g.Key.Region, g.Key.Method, g.Key.Item3, g.Count()))),
            ("one service per minute, the last hour, under a predicate", scan => scan.Where(r => r.Service == "service-000" && r.At >= lastHour && r.Status == 200 && r.Region == "eu-west").GroupBy(r => r.At.Truncate(CalendarUnit.Minute)).Select(g => (g.Key, g.Count()))),
            ("the errors of one service, by endpoint, per day", scan => scan.Where(r => r.Service == "service-000" && r.Status >= 500).GroupBy(r => (r.Endpoint, r.At.Truncate(CalendarUnit.Day))).Select(g => (g.Key.Endpoint, g.Key.Item2, g.Count()))),
            ("a count per endpoint, for its top 10", scan => scan.GroupBy(r => r.Endpoint).Select(g => (g.Key, g.Count()))),
            ("one endpoint per day", scan => scan.Where(r => r.Endpoint == "/s0/e0").GroupBy(r => r.At.Truncate(CalendarUnit.Day)).Select(g => (g.Key, g.Count()))),
            ("one host per minute", scan => scan.Where(r => r.Host == "host-000-00").GroupBy(r => r.At.Truncate(CalendarUnit.Minute)).Select(g => (g.Key, g.Count()))),
            ("every row but one request's, per service and per 12 hours", scan => scan.Where(r => r.RequestId != needle).GroupBy(r => (r.Service, r.At.Bucket(half))).Select(g => (g.Key.Service, g.Key.Item2, g.Count()))),
            ("a count per service and per 12 hours, the window unaligned", scan => scan.Where(r => r.At >= from && r.At < to).GroupBy(r => (r.Service, r.At.Bucket(half))).Select(g => (g.Key.Service, g.Key.Item2, g.Count()))),
        ];
        foreach ((string name, Func<Scan<GeneratedRequest>, Aggregation> ask) in requestQuestions)
        {
            await AskAsync(requestBytes, requests, name, ask, rounds);
        }

        (string Name, Func<Scan<LogRow>, Aggregation> Ask)[] logQuestions =
        [
            ("a count per http.route and per day", scan => scan.GroupBy(r => (r.Attributes["http.route"].AsString(), r.At.Truncate(CalendarUnit.Day))).Select(g => (g.Key.Item1, g.Key.Item2, g.Count()))),
            ("services under status_code >= 500, per minute, the last hour", scan => scan.Where(r => r.Attributes["http.response.status_code"].AsInt64() >= 500 && r.At >= lastHour).GroupBy(r => (r.ServiceName, r.At.Truncate(CalendarUnit.Minute))).Select(g => (g.Key.ServiceName, g.Key.Item2, g.Count()))),
            ("the rows holding feature.flag.new-cart, per minute", scan => scan.Where(r => r.Attributes["feature.flag.new-cart"].Exists).GroupBy(r => r.At.Truncate(CalendarUnit.Minute)).Select(g => (g.Key, g.Count()))),
            ("feature.flag.new-cart == on, per service and per day", scan => scan.Where(r => r.Attributes["feature.flag.new-cart"].AsString() == "on").GroupBy(r => (r.ServiceName, r.At.Truncate(CalendarUnit.Day))).Select(g => (g.Key.ServiceName, g.Key.Item2, g.Count()))),
            ("the mean duration per route and per day", scan => scan.GroupBy(r => (r.Attributes["http.route"].AsString(), r.At.Truncate(CalendarUnit.Day))).Select(g => (g.Key.Item1, g.Key.Item2, g.Average(r => r.Attributes["http.server.request.duration"].AsDouble())))),
            ("status_code AsInt64() >= 500, per service", scan => scan.Where(r => r.Attributes["http.response.status_code"].AsInt64() >= 500).GroupBy(r => r.ServiceName).Select(g => (g.Key, g.Count()))),
            ("status_code ToInt64() >= 500, per service, text parsed", scan => scan.Where(r => r.Attributes["http.response.status_code"].ToInt64() >= 500).GroupBy(r => r.ServiceName).Select(g => (g.Key, g.Count()))),
        ];
        foreach ((string name, Func<Scan<LogRow>, Aggregation> ask) in logQuestions)
        {
            await AskAsync(logBytes, logs, name, ask, rounds);
        }

        return 0;
    }

    /// <summary>
    /// The requests of <paramref name="hours"/> hours at <paramref name="rate"/> a second, appended a minute at a
    /// time and compacted until nothing is due; and the id of a request in the middle, a needle to leave out.
    /// </summary>
    internal static async Task<(long Rows, string Needle)> FillRequestsAsync(IObjectStore store, int hours, double rate, VortexWriteOptions write)
    {
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, GeneratedRequest.Schema, new DatasetOptions
        {
            ClusteringKey = [nameof(GeneratedRequest.At)],
            Periods = new PeriodOptions { Lateness = TimeSpan.FromMinutes(5), Length = SliceWidth.Hour },
            Identifiers = [nameof(GeneratedRequest.RequestId)],
            Write = write,
        });

        RequestGenerator generator = new RequestGenerator(17);
        long rows = 0;
        string needle = string.Empty;
        foreach (GeneratedRequest[] batch in generator.Appends(Start, TimeSpan.FromHours(hours), rate, TimeSpan.FromMinutes(1), writers: 1))
        {
            await using ObjectDraft draft = dataset.StartObject();
            await draft.Writer.WriteAsync<GeneratedRequest>(batch);
            await dataset.AppendAsync(draft);
            if (rows <= (long)(hours * 1_800 * rate) && rows + batch.Length > (long)(hours * 1_800 * rate))
            {
                needle = batch[0].RequestId;
            }

            rows += batch.Length;
        }

        await CompactAsync(dataset);
        return (rows, needle);
    }

    /// <summary>The log records of <paramref name="hours"/> hours at <paramref name="rate"/> a second, as the requests.</summary>
    private static async Task<long> FillLogsAsync(IObjectStore store, int hours, double rate, VortexWriteOptions write)
    {
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, LogRow.Schema, new DatasetOptions
        {
            ClusteringKey = [nameof(LogRow.At)],
            Periods = new PeriodOptions { Lateness = TimeSpan.FromMinutes(5), Length = SliceWidth.Hour },
            Write = write,
        });

        LogGenerator generator = new LogGenerator(23);
        int perMinute = (int)(rate * 60);
        long rows = 0;
        for (int minute = 0; minute < hours * 60; minute++)
        {
            LogRow[] batch = new LogRow[perMinute];
            for (int i = 0; i < batch.Length; i++)
            {
                batch[i] = generator.Next(Start.AddMinutes(minute).AddTicks(i * (TimeSpan.TicksPerMinute / perMinute)));
            }

            await using ObjectDraft draft = dataset.StartObject();
            await draft.Writer.WriteAsync<LogRow>(batch);
            await dataset.AppendAsync(draft);
            rows += batch.Length;
        }

        await CompactAsync(dataset);
        return rows;
    }

    /// <summary>Compacts until nothing is due, and says what the dataset then holds: its objects, and those with synopsis tables.</summary>
    internal static async Task CompactAsync(VortexDataset dataset)
    {
        while (await dataset.CompactAsync(new CompactionOptions { LevelZeroCeiling = 2 }) is not null)
        {
        }

        int objects = 0;
        int kept = 0;
        int tables = 0;
        await foreach (PositionedObject held in dataset.Snapshot.WalkAsync(null, 0, long.MaxValue, null, default))
        {
            objects++;
            using Indexes.SynopsisTableSet? set = await dataset.ReadSynopsisAsync(held.Entry, null, default);
            if (set is { Tables.Count: > 0 })
            {
                kept++;
                tables += set.Tables.Count;
            }
        }

        Console.WriteLine($"{objects} objects, {kept} with synopsis tables, {tables} tables in all");
    }

    /// <summary>
    /// One question, from the synopses and then from the rows: cold, by a handle and a session opened for it with an
    /// empty synopsis cache on disk, the open not counted; then warm, by that handle again, the best of
    /// <paramref name="rounds"/>. <paramref name="bytes"/> is the store under the count, whose wait is lifted for a
    /// first ask.
    /// </summary>
    internal static async Task AskAsync<TRecord>(MemoryObjectStore bytes, CountingObjectStore store, string name, Func<Scan<TRecord>, Aggregation> ask, int rounds)
        where TRecord : IVortexRecord<TRecord>
    {
        // Asked once each way first, with no wait and nothing kept, so that neither cold ask pays the question's
        // first compile.
        TimeSpan latency = bytes.Latency;
        bytes.Latency = TimeSpan.Zero;
        foreach (bool synopses in new[] { true, false })
        {
            await using VortexSession session = VortexSession.Create(static _ => { });
            await using VortexDataset dataset = await VortexDataset.OpenAsync(store, new DatasetOptions { Session = session });
            await MeasureAsync(store, dataset, ask, synopses);
        }

        bytes.Latency = latency;
        foreach (bool synopses in new[] { true, false })
        {
            string directory = System.IO.Directory.CreateTempSubdirectory("vortex-questions-").FullName;
            try
            {
                await using VortexSession session = VortexSession.Create(options => options.SynopsisCache = new SynopsisCache(directory, 256L << 20));
                await using VortexDataset dataset = await VortexDataset.OpenAsync(store, new DatasetOptions { Session = session });
                Measured cold = await MeasureAsync(store, dataset, ask, synopses);
                Measured warm = await MeasureAsync(store, dataset, ask, synopses);
                for (int round = 1; round < rounds; round++)
                {
                    Measured again = await MeasureAsync(store, dataset, ask, synopses);
                    warm = again.Milliseconds < warm.Milliseconds ? again : warm;
                }

                string path = synopses ? "synopses" : "rows";
                Console.WriteLine(Line(name, path, "cold", cold));
                Console.WriteLine(Line(string.Empty, path, "warm", warm));
                if (synopses)
                {
                    Console.WriteLine($"    plan: {await PlanAsync(dataset, ask)}");
                }
            }
            finally
            {
                System.IO.Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static async Task<Measured> MeasureAsync<TRecord>(CountingObjectStore store, VortexDataset dataset, Func<Scan<TRecord>, Aggregation> ask, bool synopses)
        where TRecord : IVortexRecord<TRecord>
    {
        store.Reset();
        Aggregation aggregation = ask(dataset.Scan<TRecord>().With(new ScanOptions { UseSynopses = synopses }));
        TimeSpan cpu = Environment.CpuUsage.TotalTime;
        long begin = Stopwatch.GetTimestamp();
        long groups = 0;
        IAsyncEnumerator<RecordBatch> batches = aggregation.Query.Batches(default);
        await using (batches)
        {
            while (await batches.MoveNextAsync())
            {
                groups += batches.Current.RowCount;
            }
        }

        double milliseconds = Stopwatch.GetElapsedTime(begin).TotalMilliseconds;
        double cpuMilliseconds = (Environment.CpuUsage.TotalTime - cpu).TotalMilliseconds;
        ScanMetrics metrics = aggregation.Metrics;
        return new Measured(store.Requests, store.DependentSteps, store.BytesRead, metrics.SynopsisRows, metrics.Rows, milliseconds, cpuMilliseconds, groups);
    }

    /// <summary>What the plan says each object reads, counted by kind, and why the first object read as rows is.</summary>
    private static async Task<string> PlanAsync<TRecord>(VortexDataset dataset, Func<Scan<TRecord>, Aggregation> ask)
        where TRecord : IVortexRecord<TRecord>
    {
        ScanPlan plan = await ask(dataset.Scan<TRecord>()).ExplainAsync();
        SortedDictionary<string, int> kinds = new SortedDictionary<string, int>(StringComparer.Ordinal);
        string? why = null;
        foreach (StatesStep step in plan.States)
        {
            kinds[step.Read] = kinds.GetValueOrDefault(step.Read) + 1;
            why ??= step.Read == "rows" ? step.Detail : null;
        }

        return kinds.Count == 0
            ? "no states asked"
            : string.Join(", ", System.Linq.Enumerable.Select(kinds, kind => $"{kind.Value} {kind.Key}")) + (why is null ? string.Empty : $" (rows: {why})");
    }

    private static string Line(string name, string path, string temperature, Measured measured) =>
        $"{name,-58} {path,-8} {temperature,-4} {measured.Requests,8} {measured.Steps,6} {measured.Bytes / 1e6,8:F2} {measured.States,9} {measured.Rows,9} {measured.Milliseconds,9:F1} {measured.CpuMilliseconds,8:F1}";

    /// <summary>What one asking cost: the store's requests, dependent round trips and bytes, the states and rows the answer read, its time.</summary>
    private readonly record struct Measured(long Requests, long Steps, long Bytes, long States, long Rows, double Milliseconds, double CpuMilliseconds, long Groups);
}
