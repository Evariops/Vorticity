using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Vorticity.Aggregating;
using Vorticity.Dataset;
using Vorticity.IO;
using Vorticity.Scanning;

namespace Vorticity.Benchmarks.Queries;

/// <summary>
/// <c>--micro columns</c>: the battery "any column" of the design (22 §10.5). For each column of the reference requests, a
/// count by value and hour, then a filter on its most frequent value and on its rarest, grouped by each of the other
/// columns, asked of the same rows in the order they arrive (a key on the instant, an object an hour) and clustered by
/// service (a key on the service and the instant, blocks ending at each hour, objects of the same size). Each question
/// says the path its plan takes object by object, the requests and bytes its planner prices the exact answer at, those
/// a cold handle then reads, and those the rows take; its answer is held to the rows'. Then the frontier of the reads on
/// the fly: at the design's rate, the longest window whose rows a question reads exactly within a second, cold.
/// </summary>
/// <remarks>
/// In memory, with no wait: the counts are the figures, the store's requests and bytes. <c>--hours</c> (4) at
/// <c>--rate</c> rows a second (50) keep each object past the mebibyte from which an object keeps its synopses.
/// <c>--only</c> asks only the columns whose name holds its text.
/// </remarks>
internal static class ColumnBattery
{
    /// <summary>The columns the questions filter and group by.</summary>
    private static readonly string[] Columns =
        [nameof(EmitterRequest.Service), nameof(EmitterRequest.Endpoint), nameof(EmitterRequest.Method), nameof(EmitterRequest.Status),
         nameof(EmitterRequest.Region), nameof(EmitterRequest.Version), nameof(EmitterRequest.Host)];

    /// <summary>The design's rate, rows a second, at which the frontier is drawn.</summary>
    private const double DesignRate = 5e4;

    internal static async Task<int> Run(double hours, double rate, string? only, string? trace)
    {
        RequestGenerator generator = new RequestGenerator(11);
        List<EmitterRequest[]> batches = [.. generator.Appends(QuestionBench.Start, TimeSpan.FromHours(hours), rate, TimeSpan.FromMinutes(1), writers: 1)
            .Select(batch => batch.Select(EmitterRequest.Of).ToArray())];
        long rows = batches.Sum(batch => (long)batch.Length);
        Dictionary<string, Dictionary<string, long>> counts = Counts(batches);
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"columns: {rows:N0} requests, {hours:0.#} hours at {rate:N0} a second"));
        foreach (string column in Columns)
        {
            (string frequent, string rare) = Values(counts[column]);
            Console.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"  {column,-9} {counts[column].Count,6} values; frequent {frequent} ({counts[column][frequent] * 100.0 / rows:F2} %), rare {rare} ({counts[column][rare] * 100.0 / rows:F4} %)"));
        }

        await using LatencyObjectStore arrivalReads = new LatencyObjectStore(new MemoryObjectStore());
        await using CountingObjectStore arrival = new CountingObjectStore(arrivalReads);
        Layout arrivalLayout = await FillAsync("arrival", arrival, batches, new DatasetOptions
        {
            ClusteringKey = [nameof(EmitterRequest.At)],
            Clustering = new ClusteringOptions { Value = ClusteringValue.Truncate(CalendarUnit.Hour) },
        });

        // Clustered by service into objects of the arrival's size, so that the two differ by the order of their rows alone.
        await using LatencyObjectStore serviceReads = new LatencyObjectStore(new MemoryObjectStore());
        await using CountingObjectStore service = new CountingObjectStore(serviceReads);
        Layout serviceLayout = await FillAsync("service", service, batches, new DatasetOptions
        {
            ClusteringKey = [nameof(EmitterRequest.Service), nameof(EmitterRequest.At)],
            Clustering = new ClusteringOptions { BlockValue = ClusteringValue.Truncate(CalendarUnit.Hour), ObjectBytes = arrivalLayout.Bytes / Math.Max(1, arrivalLayout.Objects) },
        });

        Console.WriteLine(
            $"{"layout",-8} {"question",-34} {"path",-30} {"priced",8} {"MB",8} {"read",6} {"MB",8} {"rows",6} {"MB",8} {"answer",-6}");
        Totals arrivalTotals = new Totals();
        Totals serviceTotals = new Totals();
        Dictionary<string, (double Arrival, double Service)> byColumn = [];
        using System.IO.TextWriter? traced = trace is null ? null : new System.IO.StreamWriter(trace);
        foreach (string column in Columns)
        {
            (string frequent, string rare) = Values(counts[column]);
            List<(string Name, Func<Scan<EmitterRequest>, Aggregation> Ask)> questions = [($"{column} by hour", scan => CountByHour(scan, column))];
            foreach ((string kind, string value) in new[] { ("frequent", frequent), ("rare", rare) })
            {
                foreach (string other in Columns.Where(c => c != column))
                {
                    questions.Add(($"{column} {kind} by {other}", scan => CountBy(Holding(scan, column, value), other)));
                }
            }

            double arrivalMb = 0;
            double serviceMb = 0;
            foreach ((string name, Func<Scan<EmitterRequest>, Aggregation> ask) in questions.Where(q => only is null || only.Split('|').Any(text => q.Name.Contains(text, StringComparison.Ordinal))))
            {
                arrivalMb += (await AskAsync("arrival", arrival, arrivalReads, traced, name, ask, arrivalTotals)) / 1e6;
                serviceMb += (await AskAsync("service", service, serviceReads, traced, name, ask, serviceTotals)) / 1e6;
            }

            if (arrivalMb + serviceMb > 0)
            {
                byColumn[column] = (arrivalMb, serviceMb);
            }
        }

        Console.WriteLine();
        Console.WriteLine($"{"layout",-8} {"questions",9} {"priced MB",10} {"read MB",9} {"rows MB",9} {"read/priced",12} {"worst",7} {"over",5} {"wrong",6}");
        Console.WriteLine(arrivalTotals.Line("arrival"));
        Console.WriteLine(serviceTotals.Line("service"));
        Console.WriteLine();
        Console.WriteLine($"{"column",-9} {"arrival MB",11} {"service MB",11}");
        foreach ((string column, (double a, double s)) in byColumn)
        {
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{column,-9} {a,11:F2} {s,11:F2}"));
        }

        Frontier(arrivalLayout, serviceLayout, counts[nameof(EmitterRequest.Service)], rows);
        return arrivalTotals.Wrong + serviceTotals.Wrong == 0 ? 0 : 1;
    }

    /// <summary>The rows appended a minute at a time to a new dataset, compacted until nothing is due; its objects, bytes and bytes a row of each column.</summary>
    private static async Task<Layout> FillAsync(string name, IObjectStore store, List<EmitterRequest[]> batches, DatasetOptions options)
    {
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, EmitterRequest.Schema, options);
        foreach (EmitterRequest[] batch in batches)
        {
            await using ObjectDraft draft = dataset.StartObject();
            await draft.Writer.WriteAsync<EmitterRequest>(batch);
            await dataset.AppendAsync(draft);
        }

        while (await dataset.CompactAsync(new CompactionOptions { LevelZeroCeiling = 2 }) is not null)
        {
        }

        int objects = 0;
        int keeping = 0;
        long bytes = 0;
        long rows = 0;
        int fields = EmitterRequest.Schema.Count;
        double[] columnBytes = new double[fields];
        SortedDictionary<string, long> synopses = new SortedDictionary<string, long>(StringComparer.Ordinal);
        await foreach (PositionedObject held in dataset.Snapshot.WalkAsync(null, 0, long.MaxValue, null, default))
        {
            objects++;
            bytes += held.Entry.Bytes;
            rows += held.Entry.Rows;
            if (held.Entry.Contents?.ColumnBytes is { Count: > 0 } perColumn)
            {
                keeping++;
                for (int field = 0; field < Math.Min(fields, perColumn.Count); field++)
                {
                    columnBytes[field] += perColumn[field];
                }
            }

            if (held.Entry.Contents is { } contents)
            {
                Add("1 column sheets", contents.Sheets);
                Add("2 counts", contents.Counts);
                Add("3 counts' sketches", contents.Sketches);
                Add("4 block table", contents.Blocks);
                Add("5 block statistics", contents.BlockStats);
                Add("6 where values live", contents.Wheres);
            }
        }

        void Add(string kind, IReadOnlyList<(int Field, Vorticity.Indexes.IndexSegment Region)> regions)
        {
            foreach ((_, Vorticity.Indexes.IndexSegment region) in regions)
            {
                synopses[kind] = synopses.GetValueOrDefault(kind) + region.Length;
            }
        }

        Dictionary<string, double> perRow = [];
        for (int field = 0; field < fields; field++)
        {
            perRow[EmitterRequest.Schema[field].Name] = columnBytes[field] / Math.Max(1, rows);
        }

        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"{name}: {objects} objects, {keeping} with a table of contents, {bytes / 1e6:F2} MB, {bytes / (double)Math.Max(1, rows):F2} bytes a row"));

        // What each synopsis weighs, by the regions the tables of contents name: an object's, and a million rows'.
        foreach ((string kind, long held) in synopses)
        {
            Console.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"  {kind[2..],-20} {held / 1e3,10:F1} KB, {held / 1e3 / Math.Max(1, objects),9:F1} KB an object, {held * 1e6 / Math.Max(1, rows) / 1e3,9:F1} KB a million rows"));
        }

        await UnitsAsync(dataset, synopses);
        return new Layout(objects, bytes, perRow);
    }

    /// <summary>
    /// What a unit of each synopsis weighs, against the design's figures (22 §5): a sheet, against its formula over the
    /// object's distinct values; an entry of the counts, and a sketch; a block's statistics over every column; a value of
    /// the index of where values live.
    /// </summary>
    private static async Task UnitsAsync(VortexDataset dataset, SortedDictionary<string, long> synopses)
    {
        int fields = EmitterRequest.Schema.Count;
        int[] all = [.. Enumerable.Range(0, fields)];
        double sheetFormula = 0;
        long sheets = 0;
        long entries = 0;
        long sketched = 0;
        long blocks = 0;
        long values = 0;
        await foreach (PositionedObject held in dataset.Snapshot.WalkAsync(null, 0, long.MaxValue, null, default))
        {
            if (held.Entry.Contents is not { } contents)
            {
                continue;
            }

            // A sheet: 4.5 KB and 1.125 bytes a distinct value where the values repeat, the distinct-count sketch of 1.5 KB alone otherwise.
            foreach (Vorticity.Indexes.ColumnSheet? sheet in await dataset.ReadSheetsAsync(held.Entry, all, default))
            {
                if (sheet is not null)
                {
                    sheets++;
                    sheetFormula += sheet.Filter is null ? 1_536 : 4_608 + (1.125 * sheet.Distinct);
                }
            }

            foreach ((int field, _) in contents.Counts)
            {
                (Vorticity.Indexes.ColumnCounts? counts, Vorticity.Indexes.ColumnSketches? sketches) = await dataset.ReadCountsAsync(held.Entry, field, sketches: true, default);
                entries += counts?.Entries ?? 0;
                sketched += sketches is null ? 0 : (long)sketches.Entries * (sketches.Measures + sketches.Attached);
            }

            // Every column's statistics describe the object's blocks alike: their count is any one's.
            int objectBlocks = 0;
            foreach ((Vorticity.Indexes.ColumnWhere? where, Vorticity.Indexes.ColumnBlocks? columnBlocks) in await dataset.ReadBlockColumnsAsync(held.Entry, all, stats: true, default))
            {
                values += where?.Values.Length ?? 0;
                objectBlocks = Math.Max(objectBlocks, columnBlocks?.Count ?? 0);
            }

            blocks += objectBlocks;
        }

        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"  a sheet {synopses.GetValueOrDefault("1 column sheets") / (double)Math.Max(1, sheets):F0} bytes, by its formula {sheetFormula / Math.Max(1, sheets):F0}; " +
            $"a counts entry {synopses.GetValueOrDefault("2 counts") / (double)Math.Max(1, entries):F1} bytes over {entries:N0}; " +
            $"a sketch {synopses.GetValueOrDefault("3 counts' sketches") / (double)Math.Max(1, sketched):F0} bytes over {sketched:N0}; " +
            $"a block {(synopses.GetValueOrDefault("5 block statistics") + synopses.GetValueOrDefault("4 block table")) / (double)Math.Max(1, blocks):F0} bytes over {blocks:N0}; " +
            $"a value where it lives {synopses.GetValueOrDefault("6 where values live") / (double)Math.Max(1, values):F1} bytes over {values:N0}"));
    }

    /// <summary>
    /// One question asked of <paramref name="store"/>: priced, then read cold by its plan and by its rows, each by a handle and a
    /// session of its own; the bytes its plan read. Under <paramref name="trace"/>, the plan's steps and each read of both askings.
    /// </summary>
    private static async Task<long> AskAsync(
        string layout, CountingObjectStore store, LatencyObjectStore reads, System.IO.TextWriter? trace, string name, Func<Scan<EmitterRequest>, Aggregation> ask, Totals totals)
    {
        (long Requests, long Bytes)? priced;
        string path;
        await using (VortexSession session = VortexSession.Create(_ => { }))
        await using (VortexDataset dataset = await VortexDataset.OpenAsync(store, new DatasetOptions { Session = session }))
        {
            Aggregation question = ask(dataset.Scan<EmitterRequest>());
            priced = await EstimateRun.ReadsOfAsync((AggregationQuery)question.Query, default);
            ScanPlan plan = await question.ExplainAsync();
            path = Path(plan);
            trace?.WriteLine($"# {layout} {name}: priced {priced}");
            foreach (StatesStep step in plan.States)
            {
                trace?.WriteLine($"#   {step.Object} {step.Read} {step.Detail}");
            }
        }

        reads.Trace = trace;
        trace?.WriteLine("# by the plan");
        (long requests, long bytes, string digest) = await ReadAsync(store, ask, synopses: true);
        trace?.WriteLine("# by the rows");
        (long rowRequests, long rowBytes, string rowDigest) = await ReadAsync(store, ask, synopses: false);
        reads.Trace = null;
        bool right = digest == rowDigest;
        totals.Add(priced, bytes, rowBytes, right);
        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"{layout,-8} {name,-34} {path,-30} {priced?.Requests ?? -1,8} {(priced?.Bytes ?? 0) / 1e6,8:F3} {requests,6} {bytes / 1e6,8:F3} {rowRequests,6} {rowBytes / 1e6,8:F3} {(right ? "same" : "WRONG"),-6}"));
        return bytes;
    }

    /// <summary>The store's requests and bytes for one cold asking, by a handle and a session opened for it, the open not counted; and the answer's digest.</summary>
    private static async Task<(long Requests, long Bytes, string Digest)> ReadAsync(CountingObjectStore store, Func<Scan<EmitterRequest>, Aggregation> ask, bool synopses)
    {
        await using VortexSession session = VortexSession.Create(_ => { });
        await using VortexDataset dataset = await VortexDataset.OpenAsync(store, new DatasetOptions { Session = session });
        store.Reset();
        Aggregation aggregation = ask(dataset.Scan<EmitterRequest>().With(new ScanOptions { UseSynopses = synopses }));
        ResultDigest digest = default;
        IAsyncEnumerator<RecordBatch> batches = aggregation.Query.Batches(default);
        await using (batches)
        {
            while (await batches.MoveNextAsync())
            {
                digest.Add(batches.Current);
            }
        }

        return (store.Requests, store.BytesRead, digest.ToString());
    }

    /// <summary>What the plan says each object reads, counted by kind.</summary>
    private static string Path(ScanPlan plan)
    {
        if (plan.States.IsEmpty)
        {
            return "rows";
        }

        SortedDictionary<string, int> kinds = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (StatesStep step in plan.States)
        {
            kinds[step.Read] = kinds.GetValueOrDefault(step.Read) + 1;
        }

        return string.Join(", ", kinds.Select(kind => $"{kind.Value} {kind.Key}"));
    }

    /// <summary>A count by <paramref name="column"/>'s values and the hour.</summary>
    private static Aggregation CountByHour(Scan<EmitterRequest> scan, string column) => column == nameof(EmitterRequest.Status)
        ? scan.GroupBy(r => (r.Column<short>(column), r.At.Truncate(CalendarUnit.Hour))).Select(g => (g.Key.Item1, g.Key.Item2, g.Count()))
        : scan.GroupBy(r => (r.Column<string>(column), r.At.Truncate(CalendarUnit.Hour))).Select(g => (g.Key.Item1, g.Key.Item2, g.Count()));

    /// <summary>A count by <paramref name="column"/>'s values.</summary>
    private static Aggregation CountBy(Scan<EmitterRequest> scan, string column) => column == nameof(EmitterRequest.Status)
        ? scan.GroupBy(r => r.Column<short>(column)).Select(g => (g.Key, g.Count()))
        : scan.GroupBy(r => r.Column<string>(column)).Select(g => (g.Key, g.Count()));

    /// <summary>The rows whose <paramref name="column"/> holds <paramref name="value"/>.</summary>
    private static Scan<EmitterRequest> Holding(Scan<EmitterRequest> scan, string column, string value) => column == nameof(EmitterRequest.Status)
        ? scan.Where(r => r.Column<short>(column) == short.Parse(value, CultureInfo.InvariantCulture))
        : scan.Where(r => r.Column<string>(column) == value);

    /// <summary>The rows of each value of each column.</summary>
    private static Dictionary<string, Dictionary<string, long>> Counts(List<EmitterRequest[]> batches)
    {
        Dictionary<string, Dictionary<string, long>> counts = Columns.ToDictionary(column => column, _ => new Dictionary<string, long>(StringComparer.Ordinal));
        foreach (EmitterRequest[] batch in batches)
        {
            foreach (EmitterRequest request in batch)
            {
                Count(counts[nameof(EmitterRequest.Service)], request.Service);
                Count(counts[nameof(EmitterRequest.Endpoint)], request.Endpoint);
                Count(counts[nameof(EmitterRequest.Method)], request.Method);
                Count(counts[nameof(EmitterRequest.Status)], request.Status.ToString(CultureInfo.InvariantCulture));
                Count(counts[nameof(EmitterRequest.Region)], request.Region);
                Count(counts[nameof(EmitterRequest.Version)], request.Version);
                Count(counts[nameof(EmitterRequest.Host)], request.Host);
            }
        }

        return counts;

        static void Count(Dictionary<string, long> values, string value) => values[value] = values.GetValueOrDefault(value) + 1;
    }

    /// <summary>A column's most frequent value and its rarest, the first in ordinal order among equals.</summary>
    private static (string Frequent, string Rare) Values(Dictionary<string, long> values)
    {
        string[] ranked = [.. values.OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Key)];
        return (ranked[0], ranked[^1]);
    }

    /// <summary>
    /// The frontier of the reads on the fly: at the design's rate, with an object an hour, the longest window whose rows a
    /// question reads within a second, cold, at the price of a request and a byte (<see cref="ReadCost"/>): requests of a
    /// mebibyte of a column's chunks and an open an object. A filter on a service reads its share of the rows clustered by
    /// service, and every row in the order they arrive, where each block holds every service.
    /// </summary>
    private static void Frontier(Layout arrival, Layout service, Dictionary<string, long> services, long rows)
    {
        long[] ranked = [.. services.Values.OrderByDescending(count => count)];
        (string Name, double Share)[] scopes =
        [
            ("every row", 1.0),
            ("the largest service", ranked[0] / (double)rows),
            ("the median service", ranked[ranked.Length / 2] / (double)rows),
        ];
        (string Name, string[] Columns)[] shapes =
        [
            ("a count by status and hour", [nameof(EmitterRequest.Status), nameof(EmitterRequest.At)]),
            ("5xx by endpoint and hour", [nameof(EmitterRequest.Service), nameof(EmitterRequest.Status), nameof(EmitterRequest.Endpoint), nameof(EmitterRequest.At)]),
            ("the p99 duration by hour", [nameof(EmitterRequest.Service), nameof(EmitterRequest.DurationNs), nameof(EmitterRequest.At)]),
        ];

        Console.WriteLine();
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"the frontier on the fly at {DesignRate:N0} rows a second: the longest window read exactly within a second, cold"));
        Console.WriteLine($"{"question",-28} {"scope",-20} {"B/row arrival",14} {"window",10} {"B/row service",14} {"window",10}");
        foreach ((string shape, string[] columns) in shapes)
        {
            double arrivalRow = columns.Sum(column => arrival.PerRow[column]);
            double serviceRow = columns.Sum(column => service.PerRow[column]);
            foreach ((string scope, double share) in scopes)
            {
                // In the order they arrive, a service's rows lie in every block: the filter spares no byte.
                Console.WriteLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{shape,-28} {scope,-20} {arrivalRow,14:F2} {Window(arrivalRow),10} {serviceRow,14:F2} {Window(serviceRow * share),10}"));
            }
        }

        static string Window(double bytesPerRow)
        {
            // T(W) = (bytes(W) (1 + a request a mebibyte) + W an open) / link, an object an hour; T(W) = 1 s.
            double perHour = DesignRate * 3600 * bytesPerRow * (1 + (ReadCost.RequestBytes / (double)(1 << 20)));
            double hours = ReadCost.LinkBytesPerSecond / (perHour + ReadCost.RequestBytes);
            return hours >= 48 ? $"{hours / 24:F1} d" : hours >= 1 ? $"{hours:F1} h" : $"{hours * 60:F1} min";
        }
    }

    /// <summary>A layout's objects, bytes, and each column's bytes a row by the objects' tables of contents.</summary>
    private sealed record Layout(int Objects, long Bytes, Dictionary<string, double> PerRow);

    /// <summary>A layout's questions: what was priced and read, the worst read against its price, the answers that differ from the rows'.</summary>
    private sealed class Totals
    {
        private int _questions;
        private long _priced;
        private long _read;
        private long _rows;
        private double _worst;
        private int _over;

        internal int Wrong { get; private set; }

        internal void Add((long Requests, long Bytes)? priced, long read, long rows, bool right)
        {
            _questions++;
            _priced += priced?.Bytes ?? 0;
            _read += read;
            _rows += rows;
            Wrong += right ? 0 : 1;
            if (priced is { Bytes: > 0 } price)
            {
                double ratio = read / (double)price.Bytes;
                _worst = Math.Max(_worst, ratio);

                // Past the price by a tenth and a request's worth: what the formula did not foresee.
                _over += read > (price.Bytes * 1.1) + ReadCost.RequestBytes ? 1 : 0;
            }
        }

        internal string Line(string layout) => string.Create(
            CultureInfo.InvariantCulture,
            $"{layout,-8} {_questions,9} {_priced / 1e6,10:F2} {_read / 1e6,9:F2} {_rows / 1e6,9:F2} {_read / (double)Math.Max(1, _priced),12:F2} {_worst,7:F2} {_over,5} {Wrong,6}");
    }
}
