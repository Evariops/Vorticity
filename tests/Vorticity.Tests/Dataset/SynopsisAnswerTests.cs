// An aggregation over a dataset of periods answered from its objects' synopses: the tables' states folded in
// place of the rows they stand for, and the rows of the slices a window cuts read beside them.
//
// WHAT IS HELD: every question gives the bits its rows give, with the synopses on and off: counts, exact
// integer and float sums, extremes and means, grouped by synopsis columns, under a filter on them, over a
// window aligned on the hours and one that cuts slices, and by the instant's hour; the sealed hours answer
// from their tables, and the hours still open from their rows.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.Tests.Writing;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed partial class SynopsisAnswerTests
{
    private static readonly DateTimeOffset Noon = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);

    private static readonly string[] Services = ["api", "auth", "billing", "cart", "search"];

    private static readonly string[] Methods = ["GET", "POST", "PUT"];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string> Questions() =>
    [
        "count by service",
        "every measure by service and method",
        "count by service under a status",
        "an aligned window",
        "an unaligned window",
        "a window across hours",
        "a cut hour without runs",
        "by the hour",
        "a window on slices",
    ];

    [Theory]
    [MemberData(nameof(Questions))]
    public async Task AQuestionGivesTheBitsOfItsRowsFromTheSynopses(string question)
    {
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await FilledAsync(store);
        (List<string> on, ScanMetrics metrics) = await AskAsync(dataset, question, synopses: true);
        (List<string> off, ScanMetrics without) = await AskAsync(dataset, question, synopses: false);
        Console.Out.Write($"SYNOPSIS {question}: {on.Count} groups, {metrics.SynopsisRows} table rows and {metrics.Rows} rows ({metrics.EdgeRows} at edges), against {without.Rows} rows.\n");
        Assert.Equal(off, on);
        Assert.NotEmpty(on);
        Assert.Equal(0, without.SynopsisRows);
        Assert.True(metrics.SynopsisRows > 0, "no state folded");
        Assert.True(metrics.Rows < without.Rows, $"{metrics.Rows} rows read, as many as without");
        // A window across hours reads its whole hours from their coarse tables, the slices of the hours it
        // cuts from their runs and the slices it cuts as rows; where no run of slices keys the service, the
        // slices of the hour it cuts come from the service's value counts per slice, beside the rows of the
        // slices it cuts.
        Assert.Equal(question is "an unaligned window" or "a window across hours" or "a cut hour without runs", metrics.EdgeRows > 0);
    }

    /// <summary>
    /// The objects' tables cut in chunks of a few rows, and an answer that folds more of their rows than a lane's share,
    /// however few, cut between them: at four lanes each lane answers its part of an object's slices under a window that
    /// ends where the part does, and every question gives the bits its rows give; a window on the slices' bounds, which
    /// reads no row at its edges, is answered in parts.
    /// </summary>
    [Theory]
    [MemberData(nameof(Questions))]
    public async Task AnAnswerCutAcrossLanesGivesTheBitsOfItsRows(string question)
    {
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await FilledAsync(store, tuned: options => options with { LeastPieceStates = 1, SynopsisChunking = (8, 256) });
        Vorticity.Aggregation aggregation = Question(dataset.Scan<Request>().With(new ScanOptions { DegreeOfParallelism = 4 }), question);
        (List<string> cut, ScanMetrics metrics) = await GroupsAsync(aggregation);
        long parts = aggregation.Query.Counters.AnsweredParts;
        (List<string> off, _) = await AskAsync(dataset, question, synopses: false);
        (List<string> whole, ScanMetrics alone) = await AskAsync(dataset, question, synopses: true, degree: 1);
        Console.Out.Write(
            $"CUT {question}: {parts} parts answered, {metrics.SynopsisRows} table rows, {metrics.Rows} rows ({metrics.EdgeRows} at edges); " +
            $"at one lane {alone.SynopsisRows} table rows, {alone.Rows} rows ({alone.EdgeRows} at edges).\n");
        Assert.Equal(off, cut);
        Assert.Equal(whole, cut);
        if (question == "a window on slices")
        {
            Assert.Equal(0, metrics.EdgeRows);
            Assert.True(parts > 1, $"{parts} parts answered");
        }
    }

    /// <summary>
    /// The objects written in chunks of a block of 16 rows, and every edge of an answer, a slice its window cuts, cut at
    /// four lanes between them into pieces of its rows alone: every question gives the bits its rows give and its answer
    /// at one lane, and a window that cuts slices reads fewer of their rows as an answer's edges than one lane does, the
    /// rest by pieces of their own.
    /// </summary>
    [Theory]
    [MemberData(nameof(Questions))]
    public async Task AnEdgeCutAcrossLanesGivesTheBitsOfItsRows(string question)
    {
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await FilledAsync(store, tuned: options => options with
        {
            LeastEdgePieceRows = 1,
            Write = new VortexWriteOptions { Indexes = IndexPolicy.None.WithBudgetPerMille(10_000), BlockRows = 16, ChunkTargetBytes = 1 },
        });
        Vorticity.Aggregation aggregation = Question(dataset.Scan<Request>().With(new ScanOptions { DegreeOfParallelism = 4 }), question);
        (List<string> cut, ScanMetrics metrics) = await GroupsAsync(aggregation);
        (List<string> off, _) = await AskAsync(dataset, question, synopses: false);
        (List<string> whole, ScanMetrics alone) = await AskAsync(dataset, question, synopses: true, degree: 1);
        Console.Out.Write(
            $"EDGES {question}: {metrics.Rows} rows ({metrics.EdgeRows} at edges); at one lane {alone.Rows} rows ({alone.EdgeRows} at edges).\n");
        Assert.Equal(off, cut);
        Assert.Equal(whole, cut);
        if (alone.EdgeRows > 0)
        {
            Assert.True(metrics.EdgeRows < alone.EdgeRows, $"{metrics.EdgeRows} rows at edges, {alone.EdgeRows} at one lane");
        }
    }

    /// <summary>
    /// The plan of a question over a window across hours says, per object, what it reads: the coarse table of
    /// the whole hour, the runs and the edges of the hour cut, and the rows of an object of level 0 or of
    /// the window's last hour, cut where no table of slices keys the service.
    /// </summary>
    [Fact]
    public async Task ThePlanSaysWhatEachObjectReads()
    {
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await FilledAsync(store);
        DateTimeOffset from = Noon.AddMinutes(37.5);
        DateTimeOffset to = Noon.AddMinutes(125.5);
        ScanPlan plan = await dataset.Scan<Request>().Where(r => r.At >= from && r.At < to).GroupBy(r => r.Method)
            .Select(g => (g.Key, g.Count())).ExplainAsync(Ct);
        foreach (StatesStep step in plan.States)
        {
            Console.Out.Write($"STEP {step.Object}: {step.Read} {step.Detail} x{step.Count}\n");
        }

        Assert.Contains(plan.States, step => step is { Read: "table", Detail: "Coarse by Hour on (Method)" });
        Assert.Contains(plan.States, step => step is { Read: "table", Detail: "Runs by Slice on (Method)" });
        Assert.Contains(plan.States, step => step is { Read: "edges", Count: > 0 });

        ScanPlan rows = await dataset.Scan<Request>().With(new ScanOptions { UseSynopses = false }).GroupBy(r => r.Method).Select(g => (g.Key, g.Count())).ExplainAsync(Ct);
        Assert.Empty(rows.States);
    }

    /// <summary>
    /// A window inside one sealed hour, cutting it, on a store that answers in 20 ms, the tree's pages in hand:
    /// the hour's tables and its edge slices are read beside each other, with no open of the object, two
    /// dependent round trips, the chunk directories before the chunks of every edge together, a chunk two
    /// edges share read once; a window on the hour's bounds, one.
    /// </summary>
    [Fact]
    public async Task AnUnalignedWindowCostsTwoRoundTripsAnAlignedOneOne()
    {
        await using MemoryObjectStore inner = new MemoryObjectStore();
        await using CountingObjectStore store = new CountingObjectStore(inner);
        await using VortexDataset dataset = await FilledAsync(store);

        // The version's pages read once, by a count the summaries answer.
        _ = await dataset.Scan<Request>().Where(r => r.At >= Noon).CountAsync(Ct);
        inner.Latency = TimeSpan.FromMilliseconds(20);
        foreach ((string name, DateTimeOffset from, DateTimeOffset to, long steps, long requests) in new[]
        {
            ("unaligned", Noon.AddMinutes(37.5), Noon.AddMinutes(52.25), 2L, 3L),
            ("aligned", Noon.AddHours(1), Noon.AddHours(2), 1L, 1L),
        })
        {
            store.Reset();
            (List<string> on, ScanMetrics metrics) = await GroupsAsync(
                dataset.Scan<Request>().Where(r => r.At >= from && r.At < to).GroupBy(r => r.Method).Select(g => (g.Key, g.Count(), g.Sum(r => r.Bytes))));
            Console.Out.Write($"ROUND TRIPS {name}: {store.DependentSteps} dependent steps, {store.Requests} requests, {metrics.SynopsisRows} table rows, {metrics.EdgeRows} edge rows.\n");
            Assert.True(metrics.SynopsisRows > 0);
            Assert.Equal(requests, store.Requests);

            // The store counts a step when a read starts with none in flight: the chunks, started the instant
            // their directory arrives, may count with the tables read beside it if those arrive a tick later.
            Assert.InRange(store.DependentSteps, 1, steps);
        }
    }

    /// <summary>
    /// A day of sealed hours, an object each, on a store that answers in 20 ms: a count by service reads the
    /// answers of every hour at once, newest first, so the day costs the round trips of an hour and the open
    /// of the fresh rows beside them, not a round trip an hour.
    /// </summary>
    [Fact]
    public async Task ADayOfHoursCostsTheRoundTripsOfAnHour()
    {
        await using MemoryObjectStore inner = new MemoryObjectStore();
        await using CountingObjectStore store = new CountingObjectStore(inner);
        await using VortexSession session = VortexSession.Create(static options => options.MaxConcurrentReads = 32);
        await using VortexDataset dataset = await DayAsync(store, session);
        inner.Latency = TimeSpan.FromMilliseconds(20);
        store.Reset();
        (List<string> on, ScanMetrics metrics) = await GroupsAsync(dataset.Scan<Request>().GroupBy(r => r.Service).Select(g => (g.Key, g.Count(), g.Sum(r => r.Bytes))));
        Console.Out.Write($"A DAY: {store.DependentSteps} dependent steps, {store.Requests} requests, {metrics.SynopsisRows} table rows, {metrics.Rows} rows.\n");
        Assert.InRange(store.DependentSteps, 1, 2);
        Assert.True(store.Requests >= 24, $"{store.Requests} requests for 24 hours");
        Assert.Equal(0, metrics.Rows);

        (List<string> off, _) = await GroupsAsync(dataset.Scan<Request>().With(new ScanOptions { UseSynopses = false })
            .GroupBy(r => r.Service).Select(g => (g.Key, g.Count(), g.Sum(r => r.Bytes))));
        Assert.Equal(off, on);
    }

    /// <summary>
    /// A day of requests, a row every ten seconds appended half an hour at a time, compacted into sealed hours of
    /// slices of a hundred rows, their tables kept whatever they weigh; every object's pages read once.
    /// </summary>
    private static async Task<VortexDataset> DayAsync(CountingObjectStore store, VortexSession session)
    {
        VortexDataset dataset = await VortexDataset.CreateAsync(store, Request.Schema, new DatasetOptions
        {
            Session = session,
            ClusteringKey = [nameof(Request.At)],
            Periods = new PeriodOptions { Lateness = TimeSpan.FromMinutes(5), Length = SliceWidth.Hour, SliceTargetRows = 100 },
            Write = new VortexWriteOptions { Indexes = IndexPolicy.None.WithBudgetPerMille(10_000) },
        }, Ct);
        Random random = new Random(47);
        DateTimeOffset midnight = new DateTimeOffset(2026, 10, 19, 0, 0, 0, TimeSpan.Zero);
        for (int part = 0; part < 49; part++)
        {
            Request[] rows = [.. Enumerable.Range(0, 180).Select(i => new Request(
                midnight.AddSeconds((part * 180 + i) * 10),
                Services[random.Next(Services.Length)],
                Methods[random.Next(Methods.Length)],
                random.Next(4) switch { 0 => 200L, 1 => 201L, 2 => 404L, _ => 500L },
                random.NextDouble() * 1_000,
                random.NextInt64(1, 1L << 40)))];
            await using ObjectDraft draft = dataset.StartObject();
            await draft.Writer.WriteAsync<Request>(rows, Ct);
            await dataset.AppendAsync(draft, Ct);
        }

        while (await dataset.CompactAsync(new CompactionOptions { LevelZeroCeiling = 2 }, Ct) is not null)
        {
        }

        _ = await dataset.Scan<Request>().Where(r => r.At >= midnight).CountAsync(Ct);
        return dataset;
    }

    /// <summary>
    /// A window across three hours asked part by part: the parts come newest first, end to end, cut at the
    /// hours; the newest is delivered while the oldest is still being asked, which waits for it; and the
    /// parts' counts and sums add up to the whole window's.
    /// </summary>
    [Fact]
    public async Task AWindowAskedPartByPartComesNewestFirst()
    {
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await FilledAsync(store);
        DateTimeOffset from = Noon.AddMinutes(37.5);
        DateTimeOffset to = Noon.AddMinutes(125.5);
        TaskCompletionSource newestIn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        List<WindowPart<List<ServiceTotals>>> parts = [];
        await foreach (WindowPart<List<ServiceTotals>> part in dataset.AskNewestFirstAsync(from, to, async (start, end, ct) =>
        {
            if (start == from)
            {
                await newestIn.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
            }

            return await TotalsAsync(dataset, start, end, ct);
        }, Ct))
        {
            parts.Add(part);
            newestIn.TrySetResult();
        }

        Assert.Equal([(Noon.AddHours(2), to), (Noon.AddHours(1), Noon.AddHours(2)), (from, Noon.AddHours(1))], parts.Select(part => (part.From, part.To)));
        Dictionary<string, (long Count, long Bytes)> added = [];
        foreach (ServiceTotals totals in parts.SelectMany(part => part.Answer))
        {
            (long count, long bytes) = added.GetValueOrDefault(totals.Service);
            added[totals.Service] = (count + totals.Count, bytes + totals.Bytes);
        }

        Assert.Equal(
            (await TotalsAsync(dataset, from, to, Ct)).OrderBy(totals => totals.Service, StringComparer.Ordinal).Select(totals => (totals.Service, totals.Count, totals.Bytes)),
            added.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => (pair.Key, pair.Value.Count, pair.Value.Bytes)));
        Assert.Throws<ArgumentException>(() => dataset.AskNewestFirstAsync(to, from, (start, end, ct) => TotalsAsync(dataset, start, end, ct), Ct));
    }

    /// <summary>
    /// A page of the 200 newest rows under a broad filter, three rows in four, on a store that answers in
    /// 20 ms, from the fresh tail and from inside a sealed hour: the rows the rows' own sort gives, and what
    /// reading them costs. The objects open as the merge reaches their newest instant, so the older hours
    /// are never read; in the fresh tail as inside the sealed hour, the segment of instants its slices lie in
    /// is read once for all of them, then the rows, two round trips. The fresh tail, of a quarter of an hour,
    /// outgrows the first read of its open once its tables count each column's values per slice.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task APageOfTheNewestRowsUnderABroadFilter(bool sealedHour)
    {
        await using MemoryObjectStore inner = new MemoryObjectStore();
        await using CountingObjectStore store = new CountingObjectStore(inner);
        await using VortexDataset dataset = await FilledAsync(store);
        _ = await dataset.Scan<Request>().Where(r => r.At >= Noon).CountAsync(Ct);
        List<Request> all = await dataset.Scan<Request>().ToRecordsAsync(Ct).ToListAsync(Ct);
        DateTimeOffset before = sealedHour ? Noon.AddMinutes(50) : DateTimeOffset.MaxValue;
        inner.Latency = TimeSpan.FromMilliseconds(20);
        store.Reset();
        Scan<Request> page = dataset.Scan<Request>().Where(r => r.Status != 500 && r.At < before).OrderByDescending(r => r.At);
        List<Request> newest = await page.ToRecordsAsync(Ct).Take(200).ToListAsync(Ct);
        Console.Out.Write($"NEWEST PAGE {(sealedHour ? "sealed" : "fresh")}: {store.DependentSteps} dependent steps, {store.Requests} requests, {store.BytesRead} bytes, {page.Metrics.Rows} rows of {all.Count}.\n");
        Assert.Equal(all.Where(r => r.Status != 500 && r.At < before).OrderByDescending(r => r.At).Take(200), newest);
        Assert.Equal(2, store.Requests);
        Assert.Equal(2, store.DependentSteps);
    }

    /// <summary>
    /// A window that cuts both sealed hours, aligned: each end moves out to the nearest bound the hour's tables answer
    /// the question at, its minute's when a table of slices holds the question's keys, as the methods' does, its
    /// hour's otherwise, as for the services, and no further than the rows the hour holds. No row is read, and the
    /// answer is the exact one of the window applied, which the metrics give, also when the rows answer it. The same
    /// window not aligned reads the rows the tables cannot answer, and says no window.
    /// </summary>
    [Theory]
    [InlineData("count by method", 1)]
    [InlineData("count", 60)]
    [InlineData("count and bytes", 60)]
    public async Task AnAlignedWindowReadsNoRowAndAnswersTheWindowItApplied(string question, int growthMinutes)
    {
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await FilledAsync(store);
        DateTimeOffset from = Noon.AddMinutes(37.5);
        DateTimeOffset to = Noon.AddMinutes(101.5);
        Vorticity.Aggregation aligned = Asked(dataset.Scan<Request>().With(new ScanOptions { AlignWindow = true }), question, from, to);
        (List<string> answer, ScanMetrics metrics) = await GroupsAsync(aligned);
        DateTimeOffset appliedFrom = Assert.NotNull(metrics.WindowFrom);
        DateTimeOffset appliedTo = Assert.NotNull(metrics.WindowTo);
        Console.Out.Write($"ALIGNED WINDOW, {question}: [{appliedFrom:HH:mm:ss}, {appliedTo:HH:mm:ss}) for [{from:HH:mm:ss}, {to:HH:mm:ss}), {metrics.SynopsisRows} table rows, {metrics.Rows} rows.\n");
        Assert.Equal(0, metrics.Rows);
        Assert.True(metrics.SynopsisRows > 0);
        Assert.InRange(from - appliedFrom, TimeSpan.FromTicks(1), TimeSpan.FromMinutes(growthMinutes));
        Assert.InRange(appliedTo - to, TimeSpan.FromTicks(1), TimeSpan.FromMinutes(growthMinutes));

        (List<string> exact, _) = await GroupsAsync(Asked(dataset.Scan<Request>().With(new ScanOptions { UseSynopses = false }), question, appliedFrom, appliedTo));
        Assert.Equal(exact, answer);

        // Read from the rows, the question applies the same window: turning the synopses off changes no answer.
        (List<string> rows, ScanMetrics read) = await GroupsAsync(
            Asked(dataset.Scan<Request>().With(new ScanOptions { UseSynopses = false, AlignWindow = true }), question, from, to));
        Assert.Equal((appliedFrom, appliedTo), (read.WindowFrom, read.WindowTo));
        Assert.Equal(answer, rows);

        (_, ScanMetrics asked) = await GroupsAsync(Asked(dataset.Scan<Request>(), question, from, to));
        Console.Out.Write($"ASKED WINDOW, {question}: {asked.SynopsisRows} table rows, {asked.Rows} rows, {asked.EdgeRows} edge rows.\n");
        Assert.True(asked.Rows + asked.EdgeRows > 0);
        Assert.Null(asked.WindowFrom);
        Assert.Null(asked.WindowTo);
    }

    /// <summary>A count by method, or by service with the sum of its bytes for the last question, over a window.</summary>
    private static Vorticity.Aggregation Asked(Scan<Request> scan, string question, DateTimeOffset from, DateTimeOffset to) => question switch
    {
        "count by method" => scan.Where(r => r.At >= from && r.At < to).GroupBy(r => r.Method).Select(g => (g.Key, g.Count())),
        "count" => scan.Where(r => r.At >= from && r.At < to).GroupBy(r => r.Service).Select(g => (g.Key, g.Count())),
        _ => scan.Where(r => r.At >= from && r.At < to).GroupBy(r => r.Service).Select(g => (g.Key, g.Count(), g.Sum(r => r.Bytes))),
    };

    /// <summary>A count and a sum of bytes by service over a window.</summary>
    private static async ValueTask<List<ServiceTotals>> TotalsAsync(VortexDataset dataset, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken) =>
        await dataset.Scan<Request>().Where(r => r.At >= from && r.At < to).GroupBy(r => r.Service).Select(g => (g.Key, g.Count(), g.Sum(r => r.Bytes)))
            .As<ServiceTotals>().ToRecordsAsync(cancellationToken).ToListAsync(cancellationToken);

    /// <summary>A whole scan's sums, extremes and means, over every row and over a window that cuts slices, are the bits of its rows.</summary>
    [Fact]
    public async Task AWholeScanGivesTheBitsOfItsRowsFromTheSynopses()
    {
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await FilledAsync(store);
        DateTimeOffset from = Noon.AddMinutes(37.5);
        DateTimeOffset to = Noon.AddMinutes(101);
        foreach (bool windowed in new[] { false, true })
        {
            foreach (bool synopses in new[] { true, false })
            {
                Scan<Request> scan = dataset.Scan<Request>().With(new ScanOptions { UseSynopses = synopses });
                scan = windowed ? scan.Where(r => r.At >= from && r.At < to) : scan;
                Totals totals = await scan.AggregateAsync<Totals>(a => (a.Sum(r => r.Bytes), a.Sum(r => r.Duration), a.Average(r => r.Duration), a.Max(r => r.Bytes)), Ct);
                Console.Out.Write(FormattableString.Invariant($"WHOLE {(windowed ? "window" : "rows")} {(synopses ? "on" : "off")}: {totals}, {scan.Metrics.SynopsisRows} table rows, {scan.Metrics.Rows} rows\n"));
                Assert.Equal(synopses, scan.Metrics.SynopsisRows > 0);
                (windowed ? WindowTotals : RowTotals).Add(totals);
            }
        }

        Assert.Equal(RowTotals[1], RowTotals[0]);
        Assert.Equal(WindowTotals[1], WindowTotals[0]);
        Assert.Equal(BitConverter.DoubleToInt64Bits(RowTotals[1].Duration), BitConverter.DoubleToInt64Bits(RowTotals[0].Duration));
    }

    /// <summary>
    /// Rows a delete marked in every object: each is read by its place, through the chunk directories, and
    /// taken out of its table's row, its count, its sums and its extremes, so every question gives the bits of
    /// the rows left; where a marked row holds an extreme of its group, its object is read as rows.
    /// </summary>
    [Theory]
    [MemberData(nameof(Questions))]
    public async Task RowsADeleteMarkedComeOutOfTheStates(string question)
    {
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await FilledAsync(store, marking: true);
        RowChangeResult deleted = await dataset.DeleteAsync(VortexExpr.Parse("Service = 'cart' and Status = 404"), Ct);
        Assert.True(deleted.ObjectsMarked > 0);
        (List<string> on, ScanMetrics metrics) = await AskAsync(dataset, question, synopses: true);
        (List<string> off, ScanMetrics without) = await AskAsync(dataset, question, synopses: false);
        Console.Out.Write($"DELETED {question}: {deleted.Rows} rows marked, {metrics.SynopsisRows} table rows and {metrics.Rows} rows, against {without.Rows} rows.\n");
        Assert.Equal(off, on);

        // A question that reads an extreme may meet a marked row holding it in every object, which then reads
        // its rows; the others answer from the tables corrected.
        if (question is not ("every measure by service and method" or "an unaligned window" or "a window across hours"))
        {
            Assert.True(metrics.SynopsisRows > 0, "no state folded under the marks");
        }
    }

    /// <summary>
    /// Deletes drawn at random, each marking rows of every object across services, statuses, methods and
    /// spans of time, the marks of one object growing from one to the next: after each, every question gives
    /// the bits of the rows left, with the tables corrected or the rows read.
    /// </summary>
    [Fact]
    public async Task RandomDeletesKeepEveryQuestionsBits()
    {
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await FilledAsync(store, marking: true);
        Random random = new Random(53);
        long folded = 0;
        for (int round = 0; round < 5; round++)
        {
            string service = Services[random.Next(Services.Length)];
            string method = Methods[random.Next(Methods.Length)];
            long from = Vorticity.Dataset.TimeSlicer.InstantOf(Noon.AddMinutes(random.Next(130)), TimeUnit.Microseconds);
            string delete = random.Next(3) switch
            {
                0 => $"Service = '{service}' and Method = '{method}'",
                1 => $"Status = {(random.Next(2) == 0 ? 500 : 201)} and At >= {from}",
                _ => $"Duration < {random.Next(5, 50)}",
            };
            RowChangeResult deleted = await dataset.DeleteAsync(VortexExpr.Parse(delete), Ct);
            foreach (string question in MatrixQuestions())
            {
                (List<string> on, ScanMetrics metrics) = await AskAsync(dataset, question, synopses: true);
                (List<string> off, _) = await AskAsync(dataset, question, synopses: false);
                Assert.True(off.SequenceEqual(on), $"round {round}, delete '{delete}' ({deleted.Rows} rows), question '{question}'");
                folded += metrics.SynopsisRows;
            }
        }

        Assert.True(folded > 0, "no table answered under the marks");
    }

    /// <summary>
    /// A question that leaves one request out, <c>RequestId != v || RequestId == null</c>, on an identifier
    /// mostly null: the whole from the tables, minus the rows of <c>v</c> its index finds, read and taken out
    /// as a delete's are, the rows a delete marked among them taken out once; the slices a window cuts read
    /// as rows under the whole filter. Every answer gives the bits of its rows.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ARequestLeftOutComesOutOfTheStates(bool marked)
    {
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await TracedAsync(store, marked);
        if (marked)
        {
            Assert.True((await dataset.DeleteAsync(VortexExpr.Parse("Service = 'cart' and Status = 404"), Ct)).ObjectsMarked > 0);
        }

        // A window across hours, by method: its whole hour from the coarse table, the slices of the hours it cuts
        // from their runs, and the slices it cuts as rows.
        DateTimeOffset from = Noon.AddMinutes(37.5);
        DateTimeOffset to = Noon.AddMinutes(125.5);
        foreach (string question in new[] { "one", "two", "a window" })
        {
            List<string>[] answers = new List<string>[2];
            ScanMetrics[] metrics = new ScanMetrics[2];
            foreach (bool synopses in new[] { true, false })
            {
                Scan<Traced> scan = dataset.Scan<Traced>().With(new ScanOptions { UseSynopses = synopses })
                    .Where(r => r.RequestId != "hot" || r.RequestId == null);
                (answers[synopses ? 0 : 1], metrics[synopses ? 0 : 1]) = await GroupsAsync(question switch
                {
                    "one" => scan.GroupBy(r => r.Service).Select(g => (g.Key, g.Count(), g.Sum(r => r.Bytes))),
                    "two" => scan.Where(r => r.RequestId != "r3-22" || r.RequestId == null).GroupBy(r => r.Service).Select(g => (g.Key, g.Count(), g.Sum(r => r.Bytes))),
                    _ => scan.Where(r => r.At >= from && r.At < to).GroupBy(r => r.Method).Select(g => (g.Key, g.Count(), g.Sum(r => r.Bytes))),
                });
            }

            Console.Out.Write($"LEFT OUT {(marked ? "marked" : "clean")} {question}: {metrics[0].SynopsisRows} table rows and {metrics[0].Rows} rows ({metrics[0].EdgeRows} at edges), against {metrics[1].Rows} rows.\n");
            Assert.Equal(answers[1], answers[0]);
            Assert.True(metrics[0].SynopsisRows > 0, "no state folded");
            Assert.Equal(question == "a window", metrics[0].EdgeRows > 0);
        }

        ScanPlan plan = await dataset.Scan<Traced>().Where(r => r.RequestId != "hot" || r.RequestId == null)
            .GroupBy(r => r.Service).Select(g => (g.Key, g.Count())).ExplainAsync(Ct);
        Assert.Contains(plan.States, step => step is { Read: "excluded", Count: 1 });
        Assert.Contains(plan.States, step => step.Read == "table");
    }

    /// <summary>
    /// On an identifier that holds no null, <c>RequestId != v</c> alone is the complement of <c>v</c>, as a typed
    /// question writes it: the whole from the tables, minus the rows of <c>v</c>. On one that holds nulls, the
    /// comparison drops them too, so the rows answer. Every answer gives the bits of its rows.
    /// </summary>
    [Fact]
    public async Task ARequestLeftOutOfAColumnWithoutNullsComesOutOfTheStates()
    {
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await StampedAsync(store);
        foreach (string left in new[] { "hot", "r3-22", "nowhere" })
        {
            List<string>[] answers = new List<string>[2];
            ScanMetrics[] metrics = new ScanMetrics[2];
            foreach (bool synopses in new[] { true, false })
            {
                (answers[synopses ? 0 : 1], metrics[synopses ? 0 : 1]) = await GroupsAsync(dataset.Scan<Stamped>().With(new ScanOptions { UseSynopses = synopses })
                    .Where(r => r.RequestId != left).GroupBy(r => r.Service).Select(g => (g.Key, g.Count(), g.Sum(r => r.Bytes))));
            }

            Assert.Equal(answers[1], answers[0]);
            Assert.True(metrics[0].SynopsisRows > 0, "no state folded");
        }

        ScanPlan plan = await dataset.Scan<Stamped>().Where(r => r.RequestId != "hot").GroupBy(r => r.Service).Select(g => (g.Key, g.Count())).ExplainAsync(Ct);
        Assert.Contains(plan.States, step => step is { Read: "excluded", Count: 1 });

        await using MemoryObjectStore nullable = new MemoryObjectStore();
        await using VortexDataset traced = await TracedAsync(nullable, marking: false);
        List<string>[] bare = new List<string>[2];
        foreach (bool synopses in new[] { true, false })
        {
            (bare[synopses ? 0 : 1], ScanMetrics metrics) = await GroupsAsync(traced.Scan<Traced>().With(new ScanOptions { UseSynopses = synopses })
                .Where(r => r.RequestId != "hot").GroupBy(r => r.Service).Select(g => (g.Key, g.Count())));
            Assert.Equal(0, metrics.SynopsisRows);
        }

        Assert.Equal(bare[1], bare[0]);
    }

    private List<Totals> RowTotals { get; } = [];

    private List<Totals> WindowTotals { get; } = [];

    /// <summary>The question's groups, each a line of its key and results, and what reading them did.</summary>
    private static Task<(List<string> Groups, ScanMetrics Metrics)> AskAsync(VortexDataset dataset, string question, bool synopses, int degree = 0) =>
        GroupsAsync(Question(dataset.Scan<Request>().With(new ScanOptions { UseSynopses = synopses, DegreeOfParallelism = degree }), question));

    /// <summary>The aggregation <paramref name="question"/> names, over <paramref name="scan"/>.</summary>
    private static Vorticity.Aggregation Question(Scan<Request> scan, string question)
    {
        DateTimeOffset from = Noon.AddHours(1);
        DateTimeOffset to = Noon.AddHours(2);
        DateTimeOffset cutFrom = Noon.AddMinutes(37.5);
        DateTimeOffset cutTo = Noon.AddMinutes(101);
        DateTimeOffset sliced = Noon.AddMinutes(40);
        return question switch
        {
            "count by service" => scan.GroupBy(r => r.Service).Select(g => (g.Key, g.Count())),
            "every measure by service and method" => scan.GroupBy(r => (r.Service, r.Method)).Select(g => (
                g.Key.Service, g.Key.Method, g.Count(), g.Sum(r => r.Bytes), g.Min(r => r.Bytes), g.Max(r => r.Bytes),
                g.Sum(r => r.Duration), g.Min(r => r.Duration), g.Max(r => r.Duration), g.Average(r => r.Duration))),
            "count by service under a status" => scan.Where(r => r.Status == 200).GroupBy(r => r.Service).Select(g => (g.Key, g.Count(), g.Sum(r => r.Bytes))),
            "an aligned window" => scan.Where(r => r.At >= from && r.At < to).GroupBy(r => r.Service).Select(g => (g.Key, g.Count(), g.Sum(r => r.Duration))),
            "an unaligned window" => scan.Where(r => r.At >= cutFrom && r.At < cutTo).GroupBy(r => r.Method).Select(g => (g.Key, g.Count(), g.Max(r => r.Bytes))),
            "a window across hours" => scan.Where(r => r.At >= cutFrom && r.At < Noon.AddMinutes(125.5)).GroupBy(r => r.Method).Select(g => (g.Key, g.Count(), g.Sum(r => r.Duration))),
            "a cut hour without runs" => scan.Where(r => r.At >= cutFrom && r.At < Noon.AddMinutes(125.5)).GroupBy(r => r.Service).Select(g => (g.Key, g.Count(), g.Sum(r => r.Duration))),
            "a set of services" => scan.Where(r => r.Service == "api" || r.Service == "cart" || r.Service == "search").GroupBy(r => r.Method).Select(g => (g.Key, g.Count(), g.Sum(r => r.Bytes))),
            "a range of statuses" => scan.Where(r => r.Status >= 400).GroupBy(r => r.Method).Select(g => (g.Key, g.Count(), g.Min(r => r.Bytes), g.Max(r => r.Duration))),
            "a negation" => scan.Where(r => !(r.Method == "GET")).GroupBy(r => r.Service).Select(g => (g.Key, g.Count(), g.Average(r => r.Duration))),
            "either of two keys" => scan.Where(r => r.Service == "api" || r.Method == "PUT").GroupBy(r => r.Service).Select(g => (g.Key, g.Count(), g.Sum(r => r.Bytes))),
            "a measure's bound" => scan.Where(r => r.Bytes > 1L << 39).GroupBy(r => r.Service).Select(g => (g.Key, g.Count())),
            "a window on slices" => scan.Where(r => r.At >= sliced && r.At < Noon.AddMinutes(100)).GroupBy(r => r.Method).Select(g => (g.Key, g.Count(), g.Sum(r => r.Bytes))),
            _ => scan.GroupBy(r => (r.At.Truncate(CalendarUnit.Hour), r.Service)).Select(g => (g.Key.Item1, g.Key.Service, g.Count(), g.Sum(r => r.Bytes))),
        };
    }

    /// <summary>An aggregation's groups, each a line of its key and results, and what reading them did.</summary>
    private static async Task<(List<string> Groups, ScanMetrics Metrics)> GroupsAsync(Vorticity.Aggregation aggregation)
    {
        List<string> groups = [];
        IAsyncEnumerator<RecordBatch> batches = aggregation.Query.Batches(Ct);
        await using (batches)
        {
            while (await batches.MoveNextAsync())
            {
                Values.DescribeRows(batches.Current, groups);
            }
        }

        groups.Sort(StringComparer.Ordinal);
        return (groups, aggregation.Metrics);
    }

    /// <summary>
    /// Two hours and a quarter of requests from noon, a row every 1.25 s, appended in sixteen parts and
    /// compacted: the first two hours sealed with their synopses, the rest in the fresh levels, whose jobs
    /// write synopses too. Slices of a minute, and every table kept.
    /// </summary>
    private static async Task<VortexDataset> FilledAsync(
        Vorticity.IO.IObjectStore store, bool marking = false, VortexSession? session = null, bool encrypted = false, Func<DatasetOptions, DatasetOptions>? tuned = null)
    {
        // A budget of the data's bytes ten times over keeps every table, even on hours this small.
        DatasetOptions options = new DatasetOptions
        {
            Session = session ?? VortexSession.Default,
            Encrypted = encrypted,
            ClusteringKey = [nameof(Request.At)],
            Periods = new PeriodOptions { Lateness = TimeSpan.FromMinutes(5), Length = SliceWidth.Hour, SliceTargetRows = 100 },
            Write = new VortexWriteOptions { Indexes = IndexPolicy.None.WithBudgetPerMille(10_000) },
            MarkedObjectBytes = marking ? 0 : 1L << 20,
            MarkedShare = marking ? 1 : 8,
        };
        VortexDataset dataset = await VortexDataset.CreateAsync(store, Request.Schema, tuned?.Invoke(options) ?? options, Ct);
        Random random = new Random(41);
        for (int part = 0; part < 16; part++)
        {
            Request[] rows = [.. Enumerable.Range(0, 400).Select(i => new Request(
                Noon.AddSeconds((part * 400 + i) * 1.25),
                Services[random.Next(Services.Length)],
                Methods[random.Next(Methods.Length)],
                random.Next(4) switch { 0 => 200L, 1 => 201L, 2 => 404L, _ => 500L },
                random.NextDouble() * 1_000,
                random.NextInt64(1, 1L << 40)))];
            await using ObjectDraft draft = dataset.StartObject();
            await draft.Writer.WriteAsync<Request>(rows, Ct);
            await dataset.AppendAsync(draft, Ct);
        }

        while (await dataset.CompactAsync(new CompactionOptions { LevelZeroCeiling = 2 }, Ct) is not null)
        {
        }

        return dataset;
    }

    /// <summary>
    /// The requests of <see cref="FilledAsync"/> with an identifier, mostly null: one row in eleven names a
    /// request of its own, one in thirty-seven the request "hot", which every hour holds.
    /// </summary>
    private static async Task<VortexDataset> TracedAsync(Vorticity.IO.IObjectStore store, bool marking)
    {
        VortexDataset dataset = await VortexDataset.CreateAsync(store, Traced.Schema, new DatasetOptions
        {
            ClusteringKey = [nameof(Traced.At)],
            Identifiers = [nameof(Traced.RequestId)],
            Periods = new PeriodOptions { Lateness = TimeSpan.FromMinutes(5), Length = SliceWidth.Hour, SliceTargetRows = 100 },
            Write = new VortexWriteOptions { Indexes = IndexPolicy.None.WithBudgetPerMille(10_000) },
            MarkedObjectBytes = marking ? 0 : 1L << 20,
            MarkedShare = marking ? 1 : 8,
        }, Ct);
        Random random = new Random(43);
        for (int part = 0; part < 16; part++)
        {
            Traced[] rows = [.. Enumerable.Range(0, 400).Select(i => new Traced(
                Noon.AddSeconds((part * 400 + i) * 1.25),
                Services[random.Next(Services.Length)],
                Methods[random.Next(Methods.Length)],
                random.Next(4) switch { 0 => 200L, 1 => 201L, 2 => 404L, _ => 500L },
                random.NextDouble() * 1_000,
                random.NextInt64(1, 1L << 40),
                i % 37 == 0 ? "hot" : i % 11 == 0 ? $"r{part}-{i}" : null))];
            await using ObjectDraft draft = dataset.StartObject();
            await draft.Writer.WriteAsync<Traced>(rows, Ct);
            await dataset.AppendAsync(draft, Ct);
        }

        while (await dataset.CompactAsync(new CompactionOptions { LevelZeroCeiling = 2 }, Ct) is not null)
        {
        }

        return dataset;
    }

    /// <summary>The requests of <see cref="TracedAsync"/>, each with an id: its own, or one in thirty-seven "hot".</summary>
    private static async Task<VortexDataset> StampedAsync(Vorticity.IO.IObjectStore store)
    {
        VortexDataset dataset = await VortexDataset.CreateAsync(store, Stamped.Schema, new DatasetOptions
        {
            ClusteringKey = [nameof(Stamped.At)],
            Identifiers = [nameof(Stamped.RequestId)],
            Periods = new PeriodOptions { Lateness = TimeSpan.FromMinutes(5), Length = SliceWidth.Hour, SliceTargetRows = 100 },
            Write = new VortexWriteOptions { Indexes = IndexPolicy.None.WithBudgetPerMille(10_000) },
        }, Ct);
        Random random = new Random(47);
        for (int part = 0; part < 16; part++)
        {
            Stamped[] rows = [.. Enumerable.Range(0, 400).Select(i => new Stamped(
                Noon.AddSeconds((part * 400 + i) * 1.25),
                Services[random.Next(Services.Length)],
                Methods[random.Next(Methods.Length)],
                random.Next(4) switch { 0 => 200L, 1 => 201L, 2 => 404L, _ => 500L },
                random.NextDouble() * 1_000,
                random.NextInt64(1, 1L << 40),
                i % 37 == 0 ? "hot" : $"r{part}-{i}"))];
            await using ObjectDraft draft = dataset.StartObject();
            await draft.Writer.WriteAsync<Stamped>(rows, Ct);
            await dataset.AppendAsync(draft, Ct);
        }

        while (await dataset.CompactAsync(new CompactionOptions { LevelZeroCeiling = 2 }, Ct) is not null)
        {
        }

        return dataset;
    }

    [VortexRecord]
    public partial record struct Request(DateTimeOffset At, string Service, string Method, long Status, double Duration, long Bytes);

    [VortexRecord]
    public partial record struct Stamped(DateTimeOffset At, string Service, string Method, long Status, double Duration, long Bytes, string RequestId);

    [VortexRecord]
    public partial record struct Traced(DateTimeOffset At, string Service, string Method, long Status, double Duration, long Bytes, string? RequestId);

    [VortexRecord]
    public partial record struct Totals(long Bytes, double Duration, double? Mean, long? Largest);

    [VortexRecord]
    public partial record struct ServiceTotals(string Service, long Count, long Bytes);
}
