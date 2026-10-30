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
using Vorticity.IO;
using Vorticity.Scanning;
using Vorticity.Tests.Writing;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed partial class DatasetLearnedPartialsTests
{
    private static readonly DateTimeOffset Noon = new DateTimeOffset(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // Keys and results of a family, and the filters that ask it each a value of the same columns: past the first, of rows
    // in the objects the first read, since an object its summaries refute is not read, and learns nothing.
    private static readonly (string[] Keys, string[] Results, string[] Filters)[] Families =
    [
        (["Route"], ["Route", "count()", "sum(Size)"], ["Status = 404", "Status = 500", "Status in (500, 503)"]),
        (["Host"], ["Host", "count()", "avg(Latency)", "max(Size)"], ["Status = 200", "Status = 503"]),
        (["Service", "Route"], ["Service", "Route", "count()", "min(Latency)"], ["Status = 404 and Host = 'h-1-0'", "Status = 500 and Host = 'h-1-1'"]),
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
            .GroupBy(c => c.Route).Select(g => (g.Key, g.Count(), g.Sum(c => c.Size))).ExplainAsync(analyze: true, Ct);
        Assert.NotEmpty(plan.Units);
        Assert.All(plan.Units, step => Assert.Equal(("partial", "the partial of the question's family it learned"), (step.Alternative, step.Detail)));
    }

    /// <summary>
    /// At several lanes, an object a partial of the question's family answers is a piece of its own, never cut over its rows:
    /// cut, each piece read its rows where the partial answered the object at no read. The exception groups of a pod's errors
    /// over S1's spans at 32 lanes read 121 MB in 473 requests where one lane read 1.1 MB in 4.
    /// </summary>
    [Fact]
    public async Task AtSeveralLanesTheObjectsAPartialAnswersAreNotCut()
    {
        // Objects of several chunks each, which a lane's piece may be cut between.
        await using MemoryObjectStore memory = new MemoryObjectStore();
        await (await CreateAsync(memory, chunkTargetBytes: 16 << 10)).DisposeAsync();
        (string[] keys, string[] results, string[] filters) = Families[0];

        // Learned on one lane, which reads each object whole.
        await using (VortexDataset learning = await VortexDataset.OpenAsync(memory, new DatasetOptions(), Ct))
        {
            await DatasetPlannerTests.AskAsync(learning, keys, results, filters[0], synopses: true, lanes: 1);
        }

        // Asked with another value on eight lanes, each by a handle of its own, which holds no segment another ask read:
        // through the partials, then through the rows.
        (List<string> Answer, long Bytes) partials = await AskAfreshAsync(synopses: true);
        (List<string> Answer, long Bytes) rows = await AskAfreshAsync(synopses: false);
        Assert.NotEmpty(rows.Answer);
        Assert.Equal(rows.Answer, partials.Answer);
        Assert.True(partials.Bytes * 10 < rows.Bytes, $"{partials.Bytes} bytes through the partials at 8 lanes, {rows.Bytes} through the rows");

        async Task<(List<string>, long)> AskAfreshAsync(bool synopses)
        {
            await using CountingObjectStore store = new CountingObjectStore(memory);
            await using VortexSession session = VortexSession.Create(options => options.MaxDegreeOfParallelism = 8);
            await using VortexDataset dataset = await VortexDataset.OpenAsync(store, new DatasetOptions { Session = session, LeastPieceRows = 1_000 }, Ct);
            List<string> answer = await DatasetPlannerTests.AskAsync(dataset, keys, results, filters[1], synopses, lanes: 8);
            return (answer, store.BytesRead);
        }
    }

    /// <summary>
    /// A family asked at several lanes alone is learned there: each lane's piece of an object learns the partial of its rows,
    /// and the pieces of an object, once they tile it, merge into its partial. Asked again at 32 lanes by a handle of its own,
    /// with another value, the family answers from the partials as the rows do; and each partial holds the groups and the
    /// states of the one a lane learns of its object whole.
    /// </summary>
    [Fact]
    public async Task AFamilyAskedAtSeveralLanesAloneIsLearnedThere()
    {
        // Objects of blocks of a thousand rows whose columns end together every thousand rows or so, which 32 lanes cut into
        // ten pieces each.
        await using MemoryObjectStore memory = new MemoryObjectStore();
        await (await CreateAsync(memory, chunkTargetBytes: 16 << 10)).DisposeAsync();
        (string[] keys, string[] results, string[] filters) = Families[0];

        await AskAfreshAsync(filters[0], synopses: true, lanes: 32);
        Dictionary<string, List<string>> merged = await PartialsAsync();
        (List<string> Answer, long Bytes) partials = await AskAfreshAsync(filters[1], synopses: true, lanes: 32);
        (List<string> Answer, long Bytes) rows = await AskAfreshAsync(filters[1], synopses: false, lanes: 32);
        Assert.NotEmpty(merged);
        Assert.NotEmpty(rows.Answer);
        Assert.Equal(rows.Answer, partials.Answer);
        Assert.True(partials.Bytes * 10 < rows.Bytes, $"{partials.Bytes} bytes through the partials learned at 32 lanes, {rows.Bytes} through the rows");

        // The family learned again on one lane, each object read whole: the same partials, object by object.
        await memory.DeleteAsync([.. merged.Keys], Ct);
        await AskAfreshAsync(filters[0], synopses: true, lanes: 1);
        Dictionary<string, List<string>> whole = await PartialsAsync();
        Assert.Equal(merged.Keys.Order(StringComparer.Ordinal), whole.Keys.Order(StringComparer.Ordinal));
        foreach ((string key, List<string> groups) in merged)
        {
            Assert.Equal(whole[key], groups);
        }

        async Task<(List<string>, long)> AskAfreshAsync(string filter, bool synopses, int lanes)
        {
            await using CountingObjectStore store = new CountingObjectStore(memory);
            await using VortexSession session = VortexSession.Create(options => options.MaxDegreeOfParallelism = lanes);
            await using VortexDataset dataset = await VortexDataset.OpenAsync(store, new DatasetOptions { Session = session, LeastPieceRows = 250 }, Ct);
            List<string> answer = await DatasetPlannerTests.AskAsync(dataset, keys, results, filter, synopses, lanes: lanes);
            return (answer, store.BytesRead);
        }

        // The partials the store keeps, by their keys, each as its groups in order.
        async Task<Dictionary<string, List<string>>> PartialsAsync()
        {
            Dictionary<string, List<string>> kept = [];
            await foreach (Vorticity.IO.ObjectListing listing in memory.ListAsync("learned/", null, Ct))
            {
                if (!listing.Key.EndsWith(".vxp", StringComparison.Ordinal))
                {
                    continue;
                }

                // A partial's bytes, then their hash.
                byte[] bytes = new byte[listing.Head.Length];
                await memory.ReadAsync(listing.Key, 0, bytes, null, Ct);
                await using VortexFile file = await VortexFile.OpenAsync(new Vorticity.Tests.File.TestSegmentSource(bytes[..^16]), VortexOpenOptions.Default, Ct);
                List<string> groups = [];
                await foreach (RecordBatch batch in file.ScanBuilder().ExecuteAsync().WithCancellation(Ct))
                {
                    ListShapes.Describe(batch, groups);
                }

                groups.Sort(StringComparer.Ordinal);
                kept[listing.Key] = groups;
            }

            return kept;
        }
    }

    /// <summary>
    /// At several lanes an object learns where one lane's plan would: one whose granules' statistics answer it for less than
    /// its rows is read as they choose, and learns nothing. Cut over all its rows to learn, each lane read the family's
    /// columns over its piece: the routes of a service under 5xx by hour over S1's spans read 343 MB at 32 lanes the first
    /// time, where the granules the key's index keeps hold 47.
    /// </summary>
    [Fact]
    public async Task AtSeveralLanesAnObjectLearnsWhatOneLaneLearns()
    {
        // Keyed by the status first: the objects of the statuses past 500 alone, whose granules each hold an hour of a service
        // or two, which their statistics answer.
        await using MemoryObjectStore memory = new MemoryObjectStore();
        await (await CreateAsync(memory, chunkTargetBytes: 16 << 10, sortKey: [nameof(Call.Status), nameof(Call.Service), nameof(Call.At)])).DisposeAsync();
        (List<string> Answer, long Bytes, int Learned) one = await AskAfreshAsync(synopses: true, lanes: 1);
        (List<string> Answer, long Bytes, int Learned) many = await AskAfreshAsync(synopses: true, lanes: 32);
        (List<string> Answer, long Bytes, int Learned) rows = await AskAfreshAsync(synopses: false, lanes: 32);
        Console.WriteLine($"LANES one lane {one.Bytes:N0} bytes, 32 lanes {many.Bytes:N0}, the rows at 32 lanes {rows.Bytes:N0}; {many.Learned} partials learned");
        Assert.NotEmpty(rows.Answer);
        Assert.Equal(rows.Answer, one.Answer);
        Assert.Equal(rows.Answer, many.Answer);
        Assert.True(one.Learned == 0, $"one lane learned {one.Learned} partials, where the granules answer the objects");
        Assert.True(many.Learned == 0, $"32 lanes learned {many.Learned} partials, where one lane learns none");

        async Task<(List<string>, long, int)> AskAfreshAsync(bool synopses, int lanes)
        {
            long bytes;
            List<string> answer = [];
            await using (CountingObjectStore store = new CountingObjectStore(memory))
            {
                await using VortexSession session = VortexSession.Create(options => options.MaxDegreeOfParallelism = lanes);
                await using VortexDataset dataset = await VortexDataset.OpenAsync(store, new DatasetOptions { Session = session, LeastPieceRows = 250 }, Ct);
                Vorticity.Aggregation asked = dataset.Scan<Call>().With(new ScanOptions { UseSynopses = synopses, DegreeOfParallelism = lanes })
                    .Where(c => c.Status >= 500).GroupBy(c => (c.Route, c.At.Truncate(CalendarUnit.Hour))).Select(g => (g.Key.Route, g.Key.Item2, g.Count()));
                await using (IAsyncEnumerator<RecordBatch> batches = asked.Query.Batches(Ct))
                {
                    while (await batches.MoveNextAsync())
                    {
                        Values.DescribeRows(batches.Current, answer);
                    }
                }

                bytes = store.BytesRead;
            }

            answer.Sort(StringComparer.Ordinal);
            int learned = 0;
            await foreach (Vorticity.IO.ObjectListing listing in memory.ListAsync("learned/", null, Ct))
            {
                learned += listing.Key.EndsWith(".vxp", StringComparison.Ordinal) ? 1 : 0;
            }

            return (answer, bytes, learned);
        }
    }

    /// <summary>
    /// Learning is a placement. A family that asks a column of the key for a value, the second, learns that value's groups
    /// and every other's, and so reads every row of an object, where the question reads the granules the key's index keeps
    /// of its value. Asked first, each object reads those granules and learns nothing; asked again by the same handle, with
    /// other values, each learns once the askings before would have read its kept rows again for as much as all its rows
    /// cost: of three objects, one keeping 48 % of its rows at the second asking, one keeping 19 % at the third, one keeping
    /// 28 % at the fourth. A handle opened after answers from the partials. At one lane and at several, whose lanes cut the
    /// objects as one lane chooses. The first asking of an emitter's errors per hour over S2's requests read 4.7 GB at 32
    /// lanes, where the key keeps 75 MB.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(32)]
    public async Task AFamilyAskedFirstReadsWhatTheKeyKeepsAndLearnsOnceAskedAgain(int lanes)
    {
        // Keyed by the service, the host and the instant, the first two indexed: a host keeps a third of its service's rows.
        await using MemoryObjectStore memory = new MemoryObjectStore();
        await (await CreateAsync(memory, chunkTargetBytes: 16 << 10, sortKey: [nameof(Call.Service), nameof(Call.Host), nameof(Call.At)], indexedPrefix: 2)).DisposeAsync();
        string[] results = ["Route", "count()", "avg(Latency)"];
        List<string[]> chosen = [];
        await using (VortexSession session = VortexSession.Create(options => options.MaxDegreeOfParallelism = lanes))
        {
            await using VortexDataset asked = await VortexDataset.OpenAsync(memory, new DatasetOptions { Session = session, LeastPieceRows = 250 }, Ct);
            foreach (string host in (string[])["h-1-0", "h-1-1", "h-1-2", "h-1-0"])
            {
                ScanPlan plan = await DatasetPlannerTests.Ask(asked, ["Route"], results, $"Host = '{host}'", synopses: true).ExplainAsync(analyze: true, Ct);
                chosen.Add([.. plan.Units.Select(static unit => unit.Alternative)]);
                Assert.Equal(
                    (await AskAfreshAsync(host, synopses: false)).Answer,
                    await DatasetPlannerTests.AskAsync(asked, ["Route"], results, $"Host = '{host}'", synopses: true, lanes: lanes));
            }
        }

        // A handle opened after finds every object of each host learned, and answers from the partials as the rows do.
        string[] after;
        await using (VortexSession session = VortexSession.Create(options => options.MaxDegreeOfParallelism = lanes))
        {
            await using VortexDataset fresh = await VortexDataset.OpenAsync(memory, new DatasetOptions { Session = session, LeastPieceRows = 250 }, Ct);
            after = [.. (await DatasetPlannerTests.Ask(fresh, ["Route"], results, "Host = 'h-1-2'", synopses: true).ExplainAsync(analyze: true, Ct)).Units
                .Select(static unit => unit.Alternative)];
            Assert.Equal(
                (await AskAfreshAsync("h-1-2", synopses: false)).Answer,
                await DatasetPlannerTests.AskAsync(fresh, ["Route"], results, "Host = 'h-1-2'", synopses: true, lanes: lanes));
        }

        Console.WriteLine($"PLACEMENT {lanes} lanes: {string.Join(" | ", chosen.Select(static units => string.Join(", ", units)))}; a handle after: {string.Join(", ", after)}");
        Assert.All(chosen[0], static alternative => Assert.Equal("rows", alternative));
        Assert.Contains("learn", chosen[1]);
        Assert.Contains("rows", chosen[1]);
        Assert.All(chosen[3], static alternative => Assert.Contains(alternative, (string[])["learn", "partial"]));
        Assert.NotEmpty(after);
        Assert.All(after, static alternative => Assert.Equal("partial", alternative));

        async Task<(List<string> Answer, long Bytes)> AskAfreshAsync(string host, bool synopses)
        {
            await using CountingObjectStore store = new CountingObjectStore(memory);
            await using VortexSession session = VortexSession.Create(options => options.MaxDegreeOfParallelism = lanes);
            await using VortexDataset dataset = await VortexDataset.OpenAsync(store, new DatasetOptions { Session = session, LeastPieceRows = 250 }, Ct);
            List<string> answer = await DatasetPlannerTests.AskAsync(dataset, ["Route"], results, $"Host = '{host}'", synopses, lanes: lanes);
            return (answer, store.BytesRead);
        }
    }

    /// <summary>
    /// A family answered through the statistics of an object's granules, which leave most of them: by service and hour, over
    /// an object of a hundred granules, one service holding 40 % of two hours of hits, its granules answered by their
    /// statistics, and 300 others the rest, a granule of them mixing several services, read as rows at each asking. Asked
    /// first, the object reads its statistics and the granules they leave, 3.3 MB of its 5.4, and learns nothing; asked
    /// again by the same handle, it learns, the granules each asking reads again having paid for it; then it answers from
    /// its partial. The answers are the rows'. Three objects of S2's requests answered T6, the p99 of a service's durations
    /// by hour, through their granules at every asking, 258 MB each, and never learned.
    /// </summary>
    [Fact]
    public async Task AFamilyAnsweredThroughItsGranulesLearnsOnceTheirRowsReadAgainPayForIt()
    {
        await using MemoryObjectStore memory = new MemoryObjectStore();
        await GranulesObjectAsync(memory);
        await using VortexDataset asked = await VortexDataset.OpenAsync(memory, cancellationToken: Ct);
        List<string> rows = await AnswerAsync(asked, synopses: false);
        List<string[]> chosen = [];
        for (int ask = 0; ask < 3; ask++)
        {
            ScanPlan plan = await Question(asked, synopses: true).ExplainAsync(analyze: true, Ct);
            chosen.Add([.. plan.Units.Select(static unit => $"{unit.Alternative}: {unit.Detail}")]);
            Assert.Equal(rows, await AnswerAsync(asked, synopses: true));
        }

        Console.WriteLine($"THROUGH THE GRANULES: {string.Join(" | ", chosen.Select(static units => string.Join(", ", units)))}");
        Assert.Single(chosen[0]);
        Assert.StartsWith("granules:", chosen[0][0], StringComparison.Ordinal);
        Assert.All(chosen[2], static unit => Assert.StartsWith("partial:", unit, StringComparison.Ordinal));

        static Vorticity.Aggregation Question(VortexDataset handle, bool synopses) => handle.Scan<Hit>().With(new ScanOptions { UseSynopses = synopses })
            .GroupBy(h => (h.Service, h.At.Truncate(CalendarUnit.Hour))).Select(g => (g.Key.Service, g.Key.Item2, g.Count(), g.Sum(h => h.Size)));

        static Task<List<string>> AnswerAsync(VortexDataset handle, bool synopses) => LinesAsync(Question(handle, synopses));
    }

    /// <summary>
    /// The same object under a filter that leaves one size out, a column outside the sort key that the hits draw at random:
    /// no granule's statistics decide it, its granules weigh all its rows, and the object learns at its first asking, then
    /// answers from its partial. Its granules were priced at the three fifths of its rows the one service does not keep
    /// whole, read whole at each asking, and learned only at the second. QP-5 of S1, every request but one, read an object
    /// so at every asking, 1.3 GB priced at 129 MB.
    /// </summary>
    [Fact]
    public async Task AFilterNoGranuleDecidesLearnsAtItsFirstAsking()
    {
        await using MemoryObjectStore memory = new MemoryObjectStore();
        await GranulesObjectAsync(memory);
        await using VortexDataset asked = await VortexDataset.OpenAsync(memory, cancellationToken: Ct);
        List<string> rows = await AnswerAsync(asked, synopses: false);
        List<string[]> chosen = [];
        for (int ask = 0; ask < 2; ask++)
        {
            ScanPlan plan = await Question(asked, synopses: true).ExplainAsync(analyze: true, Ct);
            chosen.Add([.. plan.Units.Select(static unit => $"{unit.Alternative}: {unit.Detail}")]);
            Assert.Equal(rows, await AnswerAsync(asked, synopses: true));
        }

        Console.WriteLine($"A FILTER NO GRANULE DECIDES: {string.Join(" | ", chosen.Select(static units => string.Join(", ", units)))}");
        Assert.All(chosen[1], static unit => Assert.StartsWith("partial:", unit, StringComparison.Ordinal));

        static Vorticity.Aggregation Question(VortexDataset handle, bool synopses) => handle.Scan<Hit>().With(new ScanOptions { UseSynopses = synopses })
            .Where(h => h.Size != 123_456).GroupBy(h => (h.Service, h.At.Truncate(CalendarUnit.Hour))).Select(g => (g.Key.Service, g.Key.Item2, g.Count()));

        static Task<List<string>> AnswerAsync(VortexDataset handle, bool synopses) => LinesAsync(Question(handle, synopses));
    }

    /// <summary>
    /// The granules an object's statistics leave in runs apart, read as one part of the scan, are read alone, a block at a
    /// time: those between the runs, which the statistics answered, are not read again. With no filter, that part read every
    /// row from its first run to its last, and counted twice the hits of the one service's second hour, 155 648 of them.
    /// </summary>
    [Fact]
    public async Task TheGranulesAnObjectsStatisticsLeaveApartAreReadAlone()
    {
        await using MemoryObjectStore memory = new MemoryObjectStore();
        await GranulesObjectAsync(memory, chunked: true);
        await using VortexDataset asked = await VortexDataset.OpenAsync(memory, cancellationToken: Ct);
        ScanPlan plan = await Question(asked, synopses: true).ExplainAsync(analyze: false, Ct);
        Assert.Equal("granules", Assert.Single(plan.Units).Alternative);
        Assert.Equal(await LinesAsync(Question(asked, synopses: false)), await LinesAsync(Question(asked, synopses: true)));

        static Vorticity.Aggregation Question(VortexDataset handle, bool synopses) =>
            handle.Scan<Hit>().With(new ScanOptions { UseSynopses = synopses, DegreeOfParallelism = 1 })
                .GroupBy(h => (h.Service, h.At.Truncate(CalendarUnit.Hour))).Select(g => (g.Key.Service, g.Key.Item2, g.Count(), g.Sum(h => h.Size)));
    }

    /// <summary>
    /// At several lanes, an object one lane answers through its granules' statistics is answered so too: its lanes' pieces
    /// each settle the granules they hold and read the rows they hold of the others, the statistics read once. Cut as rows,
    /// each piece read all of its rows: the object read its whole columns at 8 lanes, where one lane reads three fifths of
    /// them. At 32 lanes, QP-5 of S1 read again so 1.3 GB at each asking that its objects' granules answered.
    /// </summary>
    [Fact]
    public async Task AtSeveralLanesAnObjectItsGranulesAnswerSettlesThemByPieces()
    {
        await using MemoryObjectStore memory = new MemoryObjectStore();
        await GranulesObjectAsync(memory, chunked: true);
        (List<string> Answer, long Bytes, string Plan) one = await AskAfreshAsync(synopses: true, lanes: 1);
        (List<string> Answer, long Bytes, string Plan) many = await AskAfreshAsync(synopses: true, lanes: 8);
        (List<string> Answer, long Bytes, string Plan) rows = await AskAfreshAsync(synopses: false, lanes: 8);
        Console.WriteLine($"GRANULES BY PIECES one lane {one.Bytes:N0} bytes, 8 lanes {many.Bytes:N0}, the rows at 8 lanes {rows.Bytes:N0}; {one.Plan}");
        Assert.NotEmpty(rows.Answer);
        Assert.Equal(rows.Answer, one.Answer);
        Assert.Equal(rows.Answer, many.Answer);
        Assert.StartsWith("granules:", one.Plan, StringComparison.Ordinal);
        Assert.True(many.Bytes * 10 < rows.Bytes * 8, $"{many.Bytes} bytes at 8 lanes, {one.Bytes} at one, {rows.Bytes} through the rows");
        // Neighbouring pieces read some segments both, side by side: a few percent past one lane's, which varies.
        Assert.True(many.Bytes * 4 < one.Bytes * 5, $"{many.Bytes} bytes at 8 lanes, {one.Bytes} at one");

        async Task<(List<string>, long, string)> AskAfreshAsync(bool synopses, int lanes)
        {
            await using CountingObjectStore store = new CountingObjectStore(memory);
            await using VortexSession session = VortexSession.Create(options => options.MaxDegreeOfParallelism = lanes);
            await using VortexDataset dataset = await VortexDataset.OpenAsync(store, new DatasetOptions { Session = session, LeastPieceRows = 1_000 }, Ct);
            string plan = string.Join(", ", (await Question(dataset, synopses, 1).ExplainAsync(analyze: false, Ct)).Units.Select(static unit => $"{unit.Alternative}: {unit.Detail}"));
            store.Reset();
            List<string> answer = await LinesAsync(Question(dataset, synopses, lanes));
            return (answer, store.BytesRead, plan);
        }

        static Vorticity.Aggregation Question(VortexDataset handle, bool synopses, int lanes) =>
            handle.Scan<Hit>().With(new ScanOptions { UseSynopses = synopses, DegreeOfParallelism = lanes })
                .GroupBy(h => (h.Service, h.At.Truncate(CalendarUnit.Hour))).Select(g => (g.Key.Service, g.Key.Item2, g.Count(), g.Sum(h => h.Size)));
    }

    /// <summary>
    /// At eight lanes, one object cut into pieces under a filter on a column the question does not otherwise read, its
    /// columns' chunks of 16 KiB ending together every 200 000 rows alone: the services, sorted, take 16 384 rows a chunk,
    /// and the first 204 800 in one, where the sizes the question sums, drawn at random, take a block of 8 192. Cut where
    /// the services' chunks start as well, the object made 18 pieces; cut where the sizes' start, 25, a share of its rows
    /// each, and the services' chunks across an edge, read by each piece they reach, are a sliver of the bytes read. Cut by
    /// the filter's chunks of few values, the 19.2 M rows of an object of S2's logs made six pieces where their projection
    /// cuts sixteen, the routes under 5xx of a day (T11) twice as long at the S3 profile.
    /// </summary>
    [Fact]
    public async Task AnObjectIsCutForItsLanesWhereItsFiltersChunksWouldLeaveThemFewPieces()
    {
        await using MemoryObjectStore memory = new MemoryObjectStore();
        await GranulesObjectAsync(memory, chunked: true, groupRows: 200_000);
        await using RangeReads store = new RangeReads(memory);
        await using VortexSession session = VortexSession.Create(options => options.MaxDegreeOfParallelism = 8);
        await using VortexDataset dataset = await VortexDataset.OpenAsync(store, new DatasetOptions { Session = session, LeastPieceRows = 1_000 }, Ct);
        var question = Question(dataset, 8);
        List<string> answer = await LinesAsync(question);
        await using (VortexDataset alone = await VortexDataset.OpenAsync(memory, cancellationToken: Ct))
        {
            Assert.Equal(await LinesAsync(Question(alone, 1)), answer);
        }

        int pieces = question.Plan.LastRun!.Lanes.Sum(static lane => lane.Ranges);
        long all = store.Ranges.Sum(static range => (long)range.Value * range.Key.Length);
        long again = store.Ranges.Where(static range => range.Value > 1).Sum(static range => (long)(range.Value - 1) * range.Key.Length);
        Console.WriteLine($"CUT FOR THE LANES: {pieces} pieces, {again} of {all} bytes read again");
        Assert.True(pieces >= 24, $"{pieces} pieces for eight lanes");
        Assert.True(again * 100 < all, $"{again} of {all} bytes read again");

        static Vorticity.Aggregation Question(VortexDataset handle, int lanes) =>
            handle.Scan<Hit>().With(new ScanOptions { UseSynopses = false, DegreeOfParallelism = lanes })
                .Where(h => h.Service != "svc-000").GroupBy(h => h.At.Truncate(CalendarUnit.Hour)).Select(g => (g.Key, g.Count(), g.Sum(h => h.Size)));
    }

    /// <summary>
    /// The same object, its sizes declared an identifier, under a filter that leaves out the size of a hit of the service whose
    /// granules their statistics answer: the granules the size's postings do not place it in are answered under the rest of
    /// the filter, and the object answers through them at its first asking, as the rows do, where every granule was its rows
    /// and the object learned.
    /// </summary>
    [Fact]
    public async Task AnIdentifierLeftOutIsPlacedByItsPostingsAndTheOtherGranulesAnswer()
    {
        await using MemoryObjectStore memory = new MemoryObjectStore();
        Hit[] hits = await GranulesObjectAsync(memory, identifiers: [nameof(Hit.Size)]);
        long needle = hits.First(static h => h.Service == "svc-000").Size;
        await using VortexDataset asked = await VortexDataset.OpenAsync(memory, cancellationToken: Ct);
        List<string> rows = await LinesAsync(Question(asked, synopses: false));
        ScanPlan plan = await Question(asked, synopses: true).ExplainAsync(analyze: true, Ct);
        string chosen = string.Join(", ", plan.Units.Select(static unit => $"{unit.Alternative}: {unit.Detail}"));
        Console.WriteLine($"AN IDENTIFIER LEFT OUT: {chosen}");
        Assert.Equal(rows, await LinesAsync(Question(asked, synopses: true)));
        Assert.StartsWith("granules:", chosen, StringComparison.Ordinal);

        Vorticity.Aggregation Question(VortexDataset handle, bool synopses) => handle.Scan<Hit>().With(new ScanOptions { UseSynopses = synopses, DegreeOfParallelism = 1 })
            .Where(h => h.Size != needle).GroupBy(h => (h.Service, h.At.Truncate(CalendarUnit.Hour))).Select(g => (g.Key.Service, g.Key.Item2, g.Count()));
    }

    /// <summary>
    /// The granules an object's statistics leave in runs apart, those where a service or an hour ends, are read alone, the
    /// runs between them answered, as the rows answer: read in one part by a mask of them, a run between two costs no part
    /// more. Bridged as if it did, two hours of four services by service and hour read 2.37 MB, where the granules left and
    /// the statistics hold 0.51, and the rows 3.04.
    /// </summary>
    [Fact]
    public async Task TheRunsBetweenTheGranulesLeftAreNotRead()
    {
        await using MemoryObjectStore memory = new MemoryObjectStore();
        await PartitionedHitsAsync(memory);
        (List<string> Answer, long Bytes, string Plan) rows = await AskAfreshAsync(synopses: false);
        (List<string> Answer, long Bytes, string Plan) granules = await AskAfreshAsync(synopses: true);
        Console.WriteLine($"RUNS BETWEEN: {granules.Bytes:N0} bytes through the granules, {rows.Bytes:N0} through the rows; {granules.Plan}");
        Assert.NotEmpty(rows.Answer);
        Assert.Equal(rows.Answer, granules.Answer);
        Assert.StartsWith("granules:", granules.Plan, StringComparison.Ordinal);
        Assert.True(granules.Bytes * 3 < rows.Bytes, $"{granules.Bytes} bytes through the granules, {rows.Bytes} through the rows");

        async Task<(List<string>, long, string)> AskAfreshAsync(bool synopses)
        {
            await using CountingObjectStore store = new CountingObjectStore(memory);
            await using VortexDataset dataset = await VortexDataset.OpenAsync(store, cancellationToken: Ct);
            string plan = string.Join(" | ", (await Question(dataset, synopses).ExplainAsync(analyze: false, Ct)).Units.Select(static unit => $"{unit.Alternative}: {unit.Detail}"));
            store.Reset();
            List<string> answer = await LinesAsync(Question(dataset, synopses));
            return (answer, store.BytesRead, plan);
        }

        static Vorticity.Aggregation Question(VortexDataset handle, bool synopses) =>
            handle.Scan<Hit>().With(new ScanOptions { UseSynopses = synopses, DegreeOfParallelism = 1 })
                .GroupBy(h => (h.Service, h.At.Truncate(CalendarUnit.Hour))).Select(g => (g.Key.Service, g.Key.Item2, g.Count()));
    }

    /// <summary>
    /// Two hours of hits in a partition of a day, consolidated with the index of its partition, the sizes declared an
    /// identifier; every request but one, by service and day, which the object's counts answer, less the rows of the one size,
    /// and by service and hour, which its granules' statistics answer, the granules of the one size read as rows: the size
    /// placed by the partition's index, which holds the object's postings past a consolidation, at one lane and at eight, as
    /// the rows answer, reading a fraction of what they read. Placed by the object's own postings, which a consolidated object
    /// keeps none of, the counts read its rows after all, and QP-5 of S1, by service and twelve hours over consolidated days,
    /// read every request id the first time: 8.7 s at 32 lanes. Cut into pieces of two granules, which each read the object's
    /// structures again, the object is answered as the rows answer.
    /// </summary>
    [Theory]
    [InlineData(1, 50_000)]
    [InlineData(8, 50_000)]
    [InlineData(8, 1_000)]
    public async Task AnIdentifierLeftOutIsPlacedByThePartitionsIndexes(int lanes, long leastPieceRows)
    {
        await using MemoryObjectStore memory = new MemoryObjectStore();
        Hit[] hits = await PartitionedHitsAsync(memory);
        long needle = hits[hits.Length / 3].Size;
        foreach ((string by, string path, Func<Scan<Hit>, Vorticity.Aggregation> grouped) in ((string, string, Func<Scan<Hit>, Vorticity.Aggregation>)[])[
            ("day", "counts:", scan => scan.GroupBy(h => (h.Service, h.At.Truncate(CalendarUnit.Day))).Select(g => (g.Key.Service, g.Key.Item2, g.Count()))),
            ("hour", "granules:", scan => scan.GroupBy(h => (h.Service, h.At.Truncate(CalendarUnit.Hour))).Select(g => (g.Key.Service, g.Key.Item2, g.Count()))),
        ])
        {
            (List<string> Answer, long Bytes, string Plan) rows = await AskAfreshAsync(grouped, synopses: false);
            (List<string> Answer, long Bytes, string Plan) placed = await AskAfreshAsync(grouped, synopses: true);
            Console.WriteLine($"PLACED BY THE PARTITIONS by service and {by} at {lanes} lanes, pieces of {leastPieceRows} rows or more: {placed.Bytes:N0} bytes, {rows.Bytes:N0} through the rows; {placed.Plan}");
            Assert.NotEmpty(rows.Answer);
            Assert.Equal(rows.Answer, placed.Answer);
            Assert.All(placed.Plan.Split(" | "), unit => Assert.StartsWith(path, unit, StringComparison.Ordinal));

            // Pieces of two granules each read the object's marks and statistics' lookups again: their bytes are Q2b's.
            if (leastPieceRows >= 50_000)
            {
                Assert.True(placed.Bytes * 3 < rows.Bytes, $"by service and {by}: {placed.Bytes} bytes placed by the partitions, {rows.Bytes} through the rows");
            }
        }

        async Task<(List<string>, long, string)> AskAfreshAsync(Func<Scan<Hit>, Vorticity.Aggregation> grouped, bool synopses)
        {
            await using CountingObjectStore store = new CountingObjectStore(memory);
            await using VortexSession session = VortexSession.Create(options => options.MaxDegreeOfParallelism = lanes);
            await using VortexDataset dataset = await VortexDataset.OpenAsync(store, new DatasetOptions { Session = session, LeastPieceRows = leastPieceRows }, Ct);
            string plan = string.Join(" | ", (await Question(dataset, grouped, synopses, 1).ExplainAsync(analyze: false, Ct)).Units.Select(static unit => $"{unit.Alternative}: {unit.Detail}"));
            store.Reset();
            List<string> answer = await LinesAsync(Question(dataset, grouped, synopses, lanes));
            return (answer, store.BytesRead, plan);
        }

        Vorticity.Aggregation Question(VortexDataset handle, Func<Scan<Hit>, Vorticity.Aggregation> grouped, bool synopses, int degree) =>
            grouped(handle.Scan<Hit>().With(new ScanOptions { UseSynopses = synopses, DegreeOfParallelism = degree }).Where(h => h.Size != needle));
    }

    /// <summary>
    /// Under a value of an identifier left out, the rows of the granules the statistics leave are read under the whole filter,
    /// the identifier's column with the others', and priced so: by service and hour, the granules weigh more than with no
    /// filter by that column's bytes. Priced without it, objects of S1 whose granules each mix the hosts and hours of a service
    /// answered QP-5 through them, every row read at each asking where their rows would have learned at the first: 2.6 s and
    /// 3.2 GB an asking at 32 lanes, warm.
    /// </summary>
    [Fact]
    public async Task TheGranulesLeftUnderAValueLeftOutArePricedWithItsColumn()
    {
        await using MemoryObjectStore memory = new MemoryObjectStore();
        Hit[] hits = await PartitionedHitsAsync(memory);
        long needle = hits[hits.Length / 3].Size;
        await using VortexDataset asked = await VortexDataset.OpenAsync(memory, cancellationToken: Ct);
        UnitStep left = await GranulesAsync(scan => scan.Where(h => h.Size != needle));
        UnitStep all = await GranulesAsync(static scan => scan);
        Console.WriteLine($"PRICED WITH ITS COLUMN: {left.Bytes:N0} bytes leaving one size out, {all.Bytes:N0} with no filter");
        Assert.True(left.Bytes * 2 > all.Bytes * 3, $"{left.Bytes} bytes priced leaving one size out, {all.Bytes} with no filter");

        async Task<UnitStep> GranulesAsync(Func<Scan<Hit>, Scan<Hit>> filtered)
        {
            ScanPlan plan = await filtered(asked.Scan<Hit>().With(new ScanOptions { UseSynopses = true, DegreeOfParallelism = 1 }))
                .GroupBy(h => (h.Service, h.At.Truncate(CalendarUnit.Hour))).Select(g => (g.Key.Service, g.Key.Item2, g.Count())).ExplainAsync(analyze: false, Ct);
            UnitStep unit = Assert.Single(plan.Units);
            Assert.Equal("granules", unit.Alternative);
            return unit;
        }
    }

    /// <summary>
    /// One object of a hundred granules of 800 000 hits over two hours, sorted by service then instant: one service holds
    /// 40 % of them, 300 others the rest, and a size drawn at random each; the hits, in the order they were drawn. With
    /// <paramref name="chunked"/>, each column's chunks of 16 KiB, all ending together at each granule: a granule's rows are
    /// read alone, and lanes cut the object there; or every <paramref name="groupRows"/> rows when it is given. The columns
    /// <paramref name="identifiers"/> names declared identifiers.
    /// </summary>
    private static async Task<Hit[]> GranulesObjectAsync(MemoryObjectStore memory, bool chunked = false, string[]? identifiers = null, long? groupRows = null)
    {
        Random random = new Random(0xF3);
        Hit[] hits = [.. Enumerable.Range(0, 800_000).Select(_ => new Hit(
            random.Next(10) < 4 ? "svc-000" : $"svc-{1 + random.Next(300):D3}",
            Noon.UtcDateTime.AddTicks(random.NextInt64(TimeSpan.FromHours(2).Ticks)),
            random.Next(1_000_000)))];
        await using VortexDataset created = await VortexDataset.CreateAsync(memory, Hit.Schema, new DatasetOptions
        {
            Layout = new DatasetLayout { SortKey = [LayoutValue.Column(nameof(Hit.Service)), LayoutValue.Column(nameof(Hit.At))], IndexedPrefix = 1, ObjectBytes = 64 << 20 },
            ContentsObjectBytes = 1,
            SheetObjectBytes = 0,
            Identifiers = identifiers ?? [],
            Write = chunked ? new VortexWriteOptions { ChunkTargetBytes = 16 << 10 } : new VortexWriteOptions(),
            ChunkBytes = chunked ? 16 << 10 : 1 << 20,
            GroupRows = groupRows ?? (chunked ? 8_192 : 1L << 22),
        }, Ct);
        for (int from = 0; from < hits.Length; from += 200_000)
        {
            await using ObjectDraft draft = created.StartObject();
            await draft.Writer.WriteAsync<Hit>(hits[from..(from + 200_000)], Ct);
            await created.AppendAsync(draft, Ct);
        }

        // One object of a hundred granules.
        while (await created.CompactAsync(new CompactionOptions { LevelZeroCeiling = 1, TargetBytesAtLevelOne = 1L << 30 }, Ct) is not null)
        {
        }

        return hits;
    }

    /// <summary>
    /// 400 000 hits over two hours from noon, in partitions of a day sorted by service then instant, of four services, a size
    /// drawn at random each and declared an identifier; appended in eight parts of the order they were drawn in, so that the
    /// day is consolidated from several objects with the index of its partition, which holds the sizes' postings. The hits,
    /// in that order.
    /// </summary>
    private static async Task<Hit[]> PartitionedHitsAsync(MemoryObjectStore memory)
    {
        Random random = new Random(0xF5);
        Hit[] hits = [.. Enumerable.Range(0, 400_000).Select(_ => new Hit(
            $"svc-{random.Next(4)}",
            Noon.UtcDateTime.AddTicks(random.NextInt64(TimeSpan.FromHours(2).Ticks)),
            random.Next(1_000_000_000)))];
        await using VortexDataset created = await VortexDataset.CreateAsync(memory, Hit.Schema, new DatasetOptions
        {
            Layout = new DatasetLayout
            {
                Partition = LayoutValue.Column(nameof(Hit.At)).Truncate(CalendarUnit.Day),
                SortKey = [LayoutValue.Column(nameof(Hit.Service)), LayoutValue.Column(nameof(Hit.At))],
                IndexedPrefix = 1,
                ObjectBytes = 64 << 20,
                MergeFanout = 4,
                CalmAfter = null,
            },
            ContentsObjectBytes = 1,
            SheetObjectBytes = 0,
            Identifiers = [nameof(Hit.Size)],
            Write = new VortexWriteOptions { ChunkTargetBytes = 16 << 10 },
            ChunkBytes = 16 << 10,
            GroupRows = 8_192,
        }, Ct);
        for (int from = 0; from < hits.Length; from += 50_000)
        {
            await using ObjectDraft draft = created.StartObject();
            await draft.Writer.WriteAsync<Hit>(hits[from..(from + 50_000)], Ct);
            await created.AppendAsync(draft, Ct);
        }

        // The day's appends consolidated together, with the index of its partition.
        await created.ConsolidateAsync(Ct);
        return hits;
    }

    /// <summary>The rows of <paramref name="question"/>, each a line of its values, sorted.</summary>
    private static async Task<List<string>> LinesAsync(Vorticity.Aggregation question)
    {
        List<string> lines = [];
        await using (IAsyncEnumerator<RecordBatch> batches = question.Query.Batches(Ct))
        {
            while (await batches.MoveNextAsync())
            {
                Values.DescribeRows(batches.Current, lines);
            }
        }

        lines.Sort(StringComparer.Ordinal);
        return lines;
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
            Layout = new DatasetLayout { Partition = LayoutValue.Column(nameof(Call.At)).Truncate(CalendarUnit.Hour), SortKey = [LayoutValue.Column(nameof(Call.At))], Organization = LayoutOrganization.Leveled },
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
            Layout = new DatasetLayout { Partition = LayoutValue.Column(nameof(Call.At)).Truncate(CalendarUnit.Hour), SortKey = [LayoutValue.Column(nameof(Call.At))], Organization = LayoutOrganization.Leveled },
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
            .GroupBy(c => c.Route).Select(g => (g.Key, g.Count(), g.Sum(c => c.Size))).ExplainAsync(analyze: true, Ct);
        Assert.Equal(3, plan.Units.Count(step => step.Alternative == "partial" && step.Detail == "the partial of the question's family it learned"));
    }

    /// <summary>
    /// A monitor: the same question after each append of a minute's calls, over the hour so far. Once asked, the family is
    /// kept up, and each object appended after learns its partial as it is written; the first, appended before it asked,
    /// learns its own from its rows as it is asked, its table of contents naming its columns: the question answers every
    /// minute's object from its partial, however many minutes come, and answers as the rows do. The append's cost of keeping
    /// it up is measured against a dataset no question asks.
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
            Layout = new DatasetLayout { Partition = LayoutValue.Column(nameof(Call.At)).Truncate(CalendarUnit.Hour), SortKey = [LayoutValue.Column(nameof(Call.At))], Organization = LayoutOrganization.Leveled },
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
                .Where(c => c.Status == 404 && c.At >= Noon.AddHours(2)).GroupBy(c => c.Route).Select(g => (g.Key, g.Count(), g.Sum(c => c.Size))).ExplainAsync(analyze: true, Ct);
            Assert.Equal(minute + 1, plan.Units.Count(step => step.Alternative == "partial" && step.Detail == "the partial of the question's family it learned"));
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
    /// A handle opened later with no cache finds in the store the partials a first one learned: its planner prices each at a
    /// request and the bytes the store's listing gives, what the answer reads, where it priced them as a read from a disk
    /// cache and the question at nothing from the store.
    /// </summary>
    [Fact]
    public async Task APartialOnlyTheStoreKeepsIsPricedAtItsRead()
    {
        await using MemoryObjectStore memory = new MemoryObjectStore();
        await (await CreateAsync(memory)).DisposeAsync();
        await using CountingObjectStore store = new CountingObjectStore(memory);
        await using (VortexDataset learning = await VortexDataset.OpenAsync(store, new DatasetOptions(), Ct))
        {
            await DatasetPlannerTests.AskAsync(learning, ["Route"], ["Route", "count()", "sum(Size)"], "Status = 404", synopses: true);
        }

        await using VortexSession session = VortexSession.Create(_ => { });
        await using VortexDataset dataset = await VortexDataset.OpenAsync(store, new DatasetOptions { Session = session }, Ct);
        Vorticity.Aggregation question = dataset.Scan<Call>().With(new ScanOptions { UseSynopses = true }).Where(c => c.Status == 503)
            .GroupBy(c => c.Route).Select(g => (g.Key, g.Count(), g.Sum(c => c.Size)));
        (long Requests, long Bytes)? priced = await Aggregating.EstimateRun.ReadsOfAsync((Aggregating.AggregationQuery)question.Query, Ct);
        ScanPlan plan = await question.ExplainAsync(analyze: false, Ct);
        store.Reset();
        List<string> answer = await DatasetPlannerTests.AskAsync(dataset, ["Route"], ["Route", "count()", "sum(Size)"], "Status = 503", synopses: true);
        long read = store.BytesRead;
        Assert.Equal(await DatasetPlannerTests.AskAsync(dataset, ["Route"], ["Route", "count()", "sum(Size)"], "Status = 503", synopses: false), answer);
        Assert.All(plan.Units, step => Assert.Equal("partial", step.Alternative));
        Assert.NotNull(priced);
        Assert.True(read > 0 && read <= priced.Value.Bytes + (priced.Value.Bytes / 20), $"{read} bytes read, priced at {priced}");
    }

    /// <summary>
    /// Four services, each 20 000 calls over two hours from noon, from three hosts and on six routes of its own, keyed by the
    /// service then the instant, or by <paramref name="sortKey"/>, its first <paramref name="indexedPrefix"/> columns
    /// indexed, appended in parts and compacted.
    /// </summary>
    private static async Task<VortexDataset> CreateAsync(MemoryObjectStore store, int chunkTargetBytes = 0, string[]? sortKey = null, int indexedPrefix = 1)
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
            Layout = new DatasetLayout
            {
                SortKey = [.. (sortKey ?? [nameof(Call.Service), nameof(Call.At)]).Select(LayoutValue.Column)], IndexedPrefix = indexedPrefix, ObjectBytes = 256 << 10,
            },
            ContentsObjectBytes = 1,
            SheetObjectBytes = 0,
            Write = chunkTargetBytes > 0 ? new VortexWriteOptions { ChunkTargetBytes = chunkTargetBytes, BlockRows = 1_024 } : new VortexWriteOptions(),

            // Each column gathers its chunks to the same bytes, and they all end together every thousand rows or so: where the
            // lanes cut an object.
            ChunkBytes = chunkTargetBytes > 0 ? chunkTargetBytes : 1 << 20,
            GroupRows = chunkTargetBytes > 0 ? 1_000 : 1L << 22,
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

    [VortexRecord]
    public partial record struct Hit(string Service, DateTime At, long Size);

    /// <summary>A store that counts the reads of each range of the data objects.</summary>
    private sealed class RangeReads(IObjectStore inner) : ForwardingStore(inner)
    {
        /// <summary>The reads of each range of a data object, by its key, offset and length.</summary>
        internal System.Collections.Concurrent.ConcurrentDictionary<(string Key, long Offset, int Length), int> Ranges { get; } = new();

        public override ValueTask<ObjectRead> ReadAsync(string key, long offset, Memory<byte> destination, string? ifToken, CancellationToken cancellationToken)
        {
            if (key.StartsWith("data/", StringComparison.Ordinal))
            {
                Ranges.AddOrUpdate((key, offset, destination.Length), 1, static (_, reads) => reads + 1);
            }

            return base.ReadAsync(key, offset, destination, ifToken, cancellationToken);
        }
    }
}
