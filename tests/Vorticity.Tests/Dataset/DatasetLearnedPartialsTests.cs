// The partials a question learns of the objects it reads as rows, its family's: the same columns, its filters that ask a
// column for values made keys, so that the question asked again, or with other values, answers from them.
//
// THE ORACLE IS THE ROWS: every question of a family, learned then answered from its partials, gives the rows' answer;
// asked again with another value, it reads nothing of the objects; and a handle opened later finds the partials in the
// session's cache.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.Tests.Writing;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed partial class DatasetLearnedPartialsTests
{
    private static readonly DateTimeOffset Noon = new DateTimeOffset(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // Keys and results of a family, and the filters that ask it each a value of the same columns.
    private static readonly (string[] Keys, string[] Results, string[] Filters)[] Families =
    [
        (["Route"], ["Route", "count()", "sum(Size)"], ["Status = 404", "Status = 500", "Status in (500, 503)"]),
        (["Host"], ["Host", "count()", "avg(Latency)", "max(Size)"], ["Status = 200", "Status = 503"]),
        (["Service", "Route"], ["Service", "Route", "count()", "min(Latency)"], ["Status = 404 and Host = 'h-1-0'", "Status = 500 and Host = 'h-2-1'"]),
        ([], ["count()", "sum(Size)"], ["Status = 404", "Status = 200"]),
    ];

    [Fact]
    public async Task AFamilyLearnedAnswersEveryValueAsTheRowsDo()
    {
        await using MemoryObjectStore memory = new MemoryObjectStore();
        await (await CreateAsync(memory)).DisposeAsync();
        await using CountingObjectStore store = new CountingObjectStore(memory);
        await using VortexDataset dataset = await VortexDataset.OpenAsync(store, new DatasetOptions(), Ct);
        foreach ((string[] keys, string[] results, string[] filters) in Families)
        {
            for (int f = 0; f < filters.Length; f++)
            {
                List<string> rows = await DatasetPlannerTests.AskAsync(dataset, keys, results, filters[f], synopses: false);
                store.Reset();
                List<string> answered = await DatasetPlannerTests.AskAsync(dataset, keys, results, filters[f], synopses: true);
                Assert.NotEmpty(rows);
                Assert.True(rows.SequenceEqual(answered), $"{string.Join(", ", results)} under {filters[f]}: {string.Join(" | ", answered)} from the partials, {string.Join(" | ", rows)} through the rows");

                // Past the first value the family learned, the objects are answered from the partials: nothing is read.
                if (f > 0)
                {
                    Assert.True(store.BytesRead == 0, $"{filters[f]}: {store.BytesRead} bytes in {store.Requests} requests, where the partials hold the answer");
                }
            }
        }

        // The family is its columns, however the question is written: the typed question finds what the text one learned.
        ScanPlan plan = await dataset.Scan<Call>().With(new ScanOptions { UseSynopses = true }).Where(c => c.Status == 503)
            .GroupBy(c => c.Route).Select(g => (g.Key, g.Count(), g.Sum(c => c.Size))).ExplainAsync(Ct);
        Assert.NotEmpty(plan.States);
        Assert.All(plan.States, step => Assert.Equal(("partial", "the partial of the question's family it learned"), (step.Read, step.Detail)));
    }

    /// <summary>
    /// The objects a job wrote of an hour learn the family; their level, past its size, is rewritten into the next, each
    /// object into one of the same rows under another identity, whose partial is its input's. Asked after, with another
    /// value, the family reads no row, and answers as the rows do.
    /// </summary>
    [Fact]
    public async Task APartialOutlivesARewriteOfItsObject()
    {
        await using MemoryObjectStore memory = new MemoryObjectStore();
        await using CountingObjectStore store = new CountingObjectStore(memory);
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Call.Schema, new DatasetOptions
        {
            ClusteringKey = [nameof(Call.At)],
            Clustering = new ClusteringOptions { Value = ClusteringValue.Truncate(CalendarUnit.Hour) },
            ContentsObjectBytes = 1,
            SheetObjectBytes = 0,
        }, Ct);
        Random random = new Random(0xF2);
        for (int part = 0; part < 4; part++)
        {
            Call[] rows = [.. Enumerable.Range(0, 2_000).Select(i => new Call(
                $"svc-{random.Next(4)}",
                Noon.AddMinutes(part * 15).AddTicks(random.NextInt64(TimeSpan.FromMinutes(15).Ticks)),
                $"h-{random.Next(12)}",
                $"/r{random.Next(6)}",
                (short)(random.Next(20) switch { < 14 => 200, < 17 => 404, < 19 => 500, _ => 503 }),
                random.Next(4_000) / 4.0,
                random.Next(1_000_000)))];
            await using ObjectDraft draft = dataset.StartObject();
            await draft.Writer.WriteAsync<Call>(rows, Ct);
            await dataset.AppendAsync(draft, Ct);
            while (await dataset.CompactAsync(new CompactionOptions { LevelZeroCeiling = 1, TargetBytesAtLevelOne = 1L << 30 }, Ct) is not null)
            {
            }
        }

        string[] keys = ["Route"];
        string[] results = ["Route", "count()", "sum(Size)", "avg(Latency)"];
        Assert.Equal(
            await DatasetPlannerTests.AskAsync(dataset, keys, results, "Status = 404", synopses: false),
            await DatasetPlannerTests.AskAsync(dataset, keys, results, "Status = 404", synopses: true));
        HashSet<string> learned = [.. (await dataset.ScanBuilder().ObjectsAsync(Ct).ToListAsync(Ct)).Select(held => held.Entry.Key)];

        int rewrites = 0;
        while (await dataset.CompactAsync(new CompactionOptions { LevelZeroCeiling = 1, TargetBytesAtLevelOne = 1 }, Ct) is { } compacted)
        {
            rewrites += compacted.FromLevel > 0 && compacted.Outcome == OperationOutcome.Applied ? 1 : 0;
        }

        Assert.True(rewrites > 0, "the objects are rewritten into the next level");
        Assert.Contains(await dataset.ScanBuilder().ObjectsAsync(Ct).ToListAsync(Ct), held => !learned.Contains(held.Entry.Key));
        List<string> rows500 = await DatasetPlannerTests.AskAsync(dataset, keys, results, "Status = 500", synopses: false);
        store.Reset();
        List<string> inherited = await DatasetPlannerTests.AskAsync(dataset, keys, results, "Status = 500", synopses: true);
        Assert.Equal(rows500, inherited);
        Assert.True(store.BytesRead == 0, $"{store.BytesRead} bytes in {store.Requests} requests after the rewrites, where the inputs' partials hold the answer");
    }

    /// <summary>
    /// A window that moves: the objects it holds whole prove its bound, which leaves their family, so that they answer from
    /// the partials they learned under the window before, another status included; the object its edge cuts is read as the
    /// question reads it, learning nothing.
    /// </summary>
    [Fact]
    public async Task AMovingWindowKeepsThePartialsOfTheObjectsItHoldsWhole()
    {
        await using MemoryObjectStore memory = new MemoryObjectStore();
        await using CountingObjectStore store = new CountingObjectStore(memory);
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Call.Schema, new DatasetOptions
        {
            ClusteringKey = [nameof(Call.At)],
            Clustering = new ClusteringOptions { Value = ClusteringValue.Truncate(CalendarUnit.Hour) },
            ContentsObjectBytes = 1,
            SheetObjectBytes = 0,
        }, Ct);
        Random random = new Random(0xF3);
        for (int hour = 0; hour < 4; hour++)
        {
            Call[] rows = [.. Enumerable.Range(0, 4_000).Select(i => new Call(
                $"svc-{random.Next(4)}",
                Noon.AddHours(hour).AddTicks(random.NextInt64(TimeSpan.FromHours(1).Ticks)),
                $"h-{random.Next(12)}",
                $"/r{random.Next(6)}",
                (short)(random.Next(20) switch { < 14 => 200, < 17 => 404, < 19 => 500, _ => 503 }),
                random.Next(4_000) / 4.0,
                random.Next(1_000_000)))];
            await using ObjectDraft draft = dataset.StartObject();
            await draft.Writer.WriteAsync<Call>(rows, Ct);
            await dataset.AppendAsync(draft, Ct);
            while (await dataset.CompactAsync(new CompactionOptions { LevelZeroCeiling = 0 }, Ct) is not null)
            {
            }
        }

        string[] keys = ["Route"];
        string[] results = ["Route", "count()", "sum(Size)"];
        long[] bytes = new long[2];
        string[] filters = ["Status = 404 and At >= '2026-10-25T12:30:00.0000000+00:00'", "Status = 500 and At >= '2026-10-25T12:40:00.0000000+00:00'"];
        for (int ask = 0; ask < 2; ask++)
        {
            List<string> rows = await DatasetPlannerTests.AskAsync(dataset, keys, results, filters[ask], synopses: false);
            store.Reset();
            Assert.Equal(rows, await DatasetPlannerTests.AskAsync(dataset, keys, results, filters[ask], synopses: true));
            bytes[ask] = store.BytesRead;
        }

        Console.WriteLine($"PARTIALS a moving window: {bytes[0]:N0} bytes the first time, {bytes[1]:N0} once it moved");
        Assert.True(bytes[1] < bytes[0], $"{bytes[1]} bytes once the window moved, {bytes[0]} the first time");

        // The objects past the edge answer from the partials they learned: three hours, the edge's own read as rows.
        ScanPlan plan = await dataset.Scan<Call>().With(new ScanOptions { UseSynopses = true }).Where(c => c.Status == 503 && c.At >= Noon.AddMinutes(50))
            .GroupBy(c => c.Route).Select(g => (g.Key, g.Count(), g.Sum(c => c.Size))).ExplainAsync(Ct);
        Assert.Equal(3, plan.States.Count(step => step.Read == "partial" && step.Detail == "the partial of the question's family it learned"));
    }

    /// <summary>
    /// A monitor: the same question after each append of a minute's calls, over the hour so far. Once asked, the family is
    /// kept up, and each object appended after learns its partial as it is written: the question answers every minute's
    /// object but the first, appended before it asked, from its partial, however many minutes come, and answers as the rows
    /// do. The append's cost of keeping it up is measured against a dataset no question asks.
    /// </summary>
    [Theory]
    [InlineData(2_000)]
    [InlineData(20_000)]
    public async Task AMonitorAnswersTheObjectsAppendedAfterItAskedFromTheirPartials(int rowsAMinute)
    {
        await using MemoryObjectStore memory = new MemoryObjectStore();
        await using CountingObjectStore store = new CountingObjectStore(memory);
        await using MemoryObjectStore quiet = new MemoryObjectStore();
        DatasetOptions options = new DatasetOptions
        {
            ClusteringKey = [nameof(Call.At)],
            Clustering = new ClusteringOptions { Value = ClusteringValue.Truncate(CalendarUnit.Hour) },
            ContentsObjectBytes = 1,
            SheetObjectBytes = 0,
        };
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Call.Schema, options, Ct);
        await using VortexDataset control = await VortexDataset.CreateAsync(quiet, Call.Schema, options, Ct);
        Random random = new Random(0xF4);
        Call[] Calls(DateTimeOffset from, TimeSpan span, int count) => [.. Enumerable.Range(0, count).Select(i => new Call(
            $"svc-{random.Next(4)}",
            from.AddTicks(random.NextInt64(span.Ticks)),
            $"h-{random.Next(12)}",
            $"/r{random.Next(6)}",
            (short)(random.Next(20) switch { < 14 => 200, < 17 => 404, < 19 => 500, _ => 503 }),
            random.Next(4_000) / 4.0,
            random.Next(1_000_000)))];

        // Two hours of history a job wrote, then the monitor's hour, a minute at a time.
        foreach (VortexDataset written in (VortexDataset[])[dataset, control])
        {
            await using ObjectDraft draft = written.StartObject();
            await draft.Writer.WriteAsync<Call>(Calls(Noon, TimeSpan.FromHours(2), 8_000), Ct);
            await written.AppendAsync(draft, Ct);
            while (await written.CompactAsync(new CompactionOptions { LevelZeroCeiling = 0 }, Ct) is not null)
            {
            }
        }

        string[] keys = ["Route"];
        string[] results = ["Route", "count()", "sum(Size)"];
        string filter = "Status = 404 and At >= '2026-10-25T14:00:00.0000000+00:00'";
        TimeSpan kept = TimeSpan.Zero;
        TimeSpan plain = TimeSpan.Zero;
        const int Minutes = 12;
        for (int minute = 0; minute < Minutes; minute++)
        {
            Call[] rows = Calls(Noon.AddHours(2).AddMinutes(minute), TimeSpan.FromMinutes(1), rowsAMinute);
            foreach ((VortexDataset written, bool timed) in (ValueTuple<VortexDataset, bool>[])[(dataset, true), (control, false)])
            {
                await using ObjectDraft draft = written.StartObject();
                await draft.Writer.WriteAsync<Call>(rows, Ct);
                System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
                await written.AppendAsync(draft, Ct);
                if (minute > 0)
                {
                    (kept, plain) = timed ? (kept + clock.Elapsed, plain) : (kept, plain + clock.Elapsed);
                }
            }

            List<string> answer = await DatasetPlannerTests.AskAsync(dataset, keys, results, filter, synopses: false);
            Assert.Equal(answer, await DatasetPlannerTests.AskAsync(dataset, keys, results, filter, synopses: true));
            ScanPlan plan = await dataset.Scan<Call>().With(new ScanOptions { UseSynopses = true })
                .Where(c => c.Status == 404 && c.At >= Noon.AddHours(2)).GroupBy(c => c.Route).Select(g => (g.Key, g.Count(), g.Sum(c => c.Size))).ExplainAsync(Ct);
            Assert.Equal(minute, plan.States.Count(step => step.Read == "partial" && step.Detail == "the partial of the question's family it learned"));
        }

        double nanoseconds = (kept - plain).TotalNanoseconds / ((Minutes - 1) * rowsAMinute);
        Console.WriteLine($"PARTIALS a monitor of {rowsAMinute:N0} rows a minute: keeping its family up cost {nanoseconds:N0} ns a row appended");
    }

    /// <summary>Quantiles and approximate distinct counts, whose states are sketches, are learned and answered as the rows give them.</summary>
    [Fact]
    public async Task SketchesAreLearnedToo()
    {
        await using MemoryObjectStore memory = new MemoryObjectStore();
        await (await CreateAsync(memory)).DisposeAsync();
        await using CountingObjectStore store = new CountingObjectStore(memory);
        await using VortexDataset dataset = await VortexDataset.OpenAsync(store, new DatasetOptions(), Ct);
        foreach (short status in (short[])[404, 500, 503])
        {
            List<string> rows = await AskAsync(dataset, status, synopses: false);
            store.Reset();
            Assert.Equal(rows, await AskAsync(dataset, status, synopses: true));
            Assert.True(status == 404 || store.BytesRead == 0, $"{store.BytesRead} bytes under {status}, where the partials hold the answer");
        }

        static async Task<List<string>> AskAsync(VortexDataset dataset, short status, bool synopses)
        {
            List<string> rows = [];
            await using (IAsyncEnumerator<RecordBatch> batches = dataset.Scan<Call>().With(new ScanOptions { UseSynopses = synopses }).Where(c => c.Status == status)
                .GroupBy(c => c.Route).Select(g => (g.Key, g.Quantile(c => c.Latency, 0.9), g.ApproximateCountDistinct(c => c.Host))).Query.Batches(Ct))
            {
                while (await batches.MoveNextAsync())
                {
                    Values.DescribeRows(batches.Current, rows);
                }
            }

            rows.Sort(StringComparer.Ordinal);
            return rows;
        }
    }

    /// <summary>
    /// A value of the key's first column, which an object of several services holds the rows of together: the family would
    /// read every service's rows where the question reads one's, so such an object is read as the question reads it.
    /// </summary>
    [Fact]
    public async Task AValueOfTheKeysFirstColumnIsNotLearnedWhereItWouldWidenTheRead()
    {
        await using MemoryObjectStore memory = new MemoryObjectStore();
        await (await CreateAsync(memory)).DisposeAsync();
        await using CountingObjectStore store = new CountingObjectStore(memory);
        foreach (string service in (string[])["svc-1", "svc-2"])
        {
            await using VortexSession session = VortexSession.Create(_ => { });
            await using VortexDataset dataset = await VortexDataset.OpenAsync(store, new DatasetOptions { Session = session }, Ct);
            string filter = $"Service = '{service}' and Status = 500";
            List<string> rows = await DatasetPlannerTests.AskAsync(dataset, ["Route"], ["Route", "count()"], filter, synopses: false);
            store.Reset();
            Assert.Equal(rows, await DatasetPlannerTests.AskAsync(dataset, ["Route"], ["Route", "count()"], filter, synopses: true));
            Assert.True(store.BytesRead > 0, "an object of several services is read as the question reads it");
        }
    }

    /// <summary>A handle opened later, on the same session's cache, answers a learned family with no read of the objects.</summary>
    [Fact]
    public async Task ThePartialsOutliveTheHandleInTheSessionsCache()
    {
        string directory = Directory.CreateTempSubdirectory("vortex-partials-").FullName;
        try
        {
            await using MemoryObjectStore memory = new MemoryObjectStore();
            await (await CreateAsync(memory)).DisposeAsync();
            await using CountingObjectStore store = new CountingObjectStore(memory);
            long[] bytes = new long[3];
            List<string>[] answers = new List<string>[3];
            for (int ask = 0; ask < 3; ask++)
            {
                // Twice on one cache, the second handle finding what the first learned; once with none, reading the rows.
                await using VortexSession session = ask < 2
                    ? VortexSession.Create(options => options.SynopsisCache = new SynopsisCache(directory, 64L << 20))
                    : VortexSession.Create(_ => { });
                await using VortexDataset dataset = await VortexDataset.OpenAsync(store, new DatasetOptions { Session = session }, Ct);
                store.Reset();
                answers[ask] = await DatasetPlannerTests.AskAsync(dataset, ["Route"], ["Route", "count()", "sum(Size)"], ask == 0 ? "Status = 404" : "Status = 503", synopses: ask < 2);
                bytes[ask] = store.BytesRead;
            }

            Console.WriteLine($"PARTIALS learned {bytes[0]:N0} bytes, answered from the cache {bytes[1]:N0}, the rows {bytes[2]:N0}");
            Assert.Equal(answers[2], answers[1]);
            Assert.True(bytes[1] * 10 < bytes[2], $"{bytes[1]} bytes from the cache, {bytes[2]} through the rows");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// Four services, each 20 000 calls over two hours from noon, from three hosts and on six routes of its own, keyed by the
    /// service then the instant, appended in parts and compacted.
    /// </summary>
    private static async Task<VortexDataset> CreateAsync(MemoryObjectStore store)
    {
        Random random = new Random(0xF1);
        List<Call> rows = [];
        for (int service = 0; service < 4; service++)
        {
            for (int i = 0; i < 20_000; i++)
            {
                rows.Add(new Call(
                    $"svc-{service}",
                    Noon.AddTicks(random.NextInt64(TimeSpan.FromHours(2).Ticks)),
                    $"h-{service}-{random.Next(3)}",
                    $"/svc{service}/r{random.Next(6)}",
                    (short)(random.Next(20) switch { < 14 => 200, < 17 => 404, < 19 => 500, _ => 503 }),
                    random.Next(4_000) / 4.0,
                    random.Next(1_000_000)));
            }
        }

        random.Shuffle(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(rows));
        VortexDataset dataset = await VortexDataset.CreateAsync(store, Call.Schema, new DatasetOptions
        {
            ClusteringKey = [nameof(Call.Service), nameof(Call.At)],
            Clustering = new ClusteringOptions { ObjectBytes = 256 << 10 },
            ContentsObjectBytes = 1,
            SheetObjectBytes = 0,
        }, Ct);
        for (int from = 0; from < rows.Count; from += 20_000)
        {
            await using ObjectDraft draft = dataset.StartObject();
            await draft.Writer.WriteAsync<Call>(rows.Skip(from).Take(20_000).ToArray(), Ct);
            await dataset.AppendAsync(draft, Ct);
        }

        while (await dataset.CompactAsync(new CompactionOptions { LevelZeroCeiling = 1 }, Ct) is not null)
        {
        }

        return dataset;
    }

    [VortexRecord]
    public partial record struct Call(string Service, DateTimeOffset At, string Host, string Route, short Status, double Latency, long Size);
}
