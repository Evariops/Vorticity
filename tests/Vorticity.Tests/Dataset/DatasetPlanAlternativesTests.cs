// The plan (spec B; B1 to B4): the encoding classes each object's table of contents keeps, which the processor's price
// reads, and the store's profile; the logical plan every question explains; the alternatives a unit is answered by, each
// chosen and explained; the order a conjunction is judged in; the capabilities a source is seen through.
//
// THE ORACLE IS LINQ, THE ENCODINGS AND THE SOURCES: every answer is the rows'; a column's classes are those its encodings
// hold, a value a row unless runs or patches; a store's profile sets the ranges a read joins; an explanation names the
// question's version and exactness; a count by a key is answered by synopses, and a family's partials answer its question
// the second time; no member of a source is one a single source overrides.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.IO;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed partial class DatasetPlanAlternativesTests
{
    private static readonly DateTime Start = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// One object of 100 000 rows: a constant day, 50 services, instants seven to a second, a sequence and random doubles. Its
    /// table of contents names the encoding classes of each of its columns, in 256ths of its rows rounded up: the day and the
    /// sequence free, every value; the doubles a value a row, no run nor patch; the others some (B1).
    /// </summary>
    [Fact]
    public async Task EachColumnsEncodingClassesAreKeptInItsTableOfContents()
    {
        Random random = new Random(83);
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await CreateAsync(store, Ct);
        await AppendAsync(dataset, [.. Enumerable.Range(0, 100_000).Select(i => new Row(3, $"svc-{random.Next(0, 50):D2}", Start.AddSeconds(i / 7), i, random.NextDouble()))]);
        PositionedObject held = await dataset.ScanBuilder().ObjectsAsync(Ct).SingleAsync(Ct);
        uint[][] classes = [.. Enumerable.Range(0, 5).Select(field => held.Entry.Contents!.ClassesOf(field).ToArray())];
        Console.WriteLine($"CLASSES: {string.Join("; ", classes.Select(static column => string.Join(" + ", column.Select(static entry => $"{(EncodingClass)(entry & 7)} {entry >> 3}"))))}");
        Assert.All(classes, static column => Assert.All(column, static entry => Assert.InRange(entry >> 3, 1u, 256u)));
        Assert.Equal([(256u << 3) | (uint)EncodingClass.Free], classes[0]);
        Assert.Equal([(256u << 3) | (uint)EncodingClass.Free], classes[3]);
        Assert.NotEmpty(classes[1]);
        Assert.NotEmpty(classes[2]);
        Assert.DoesNotContain(classes[4], static entry => (EncodingClass)(entry & 7) is EncodingClass.Runs or EncodingClass.Sparse);
        Assert.InRange(classes[4].Sum(static entry => entry >> 3), 256u, 255u + (uint)classes[4].Length);
    }

    /// <summary>
    /// One question over one dataset of four tenants, sorted by the tenant, read through three profiles of the store (B1):
    /// the granules of one tenant read apart where a request costs less than the bytes it spares, memory's, and the chunks
    /// they lie in read whole where it costs more, the remote one's and, for chunks this small, the local disk's: as
    /// requests get cheaper, more of them and fewer bytes; the answers equal.
    /// </summary>
    [Fact]
    public async Task TheStoresProfileSetsTheRangesAReadJoins()
    {
        await using MemoryObjectStore memory = new MemoryObjectStore();
        Random random = new Random(0x0C3);
        await using (VortexDataset writing = await VortexDataset.CreateAsync(memory, Tenanted.Schema, new DatasetOptions
        {
            Layout = new DatasetLayout { SortKey = [LayoutValue.Column(nameof(Tenanted.Tenant)), LayoutValue.Column(nameof(Tenanted.At))], IndexedPrefix = 1 },
        }, Ct))
        {
            for (int part = 0; part < 4; part++)
            {
                Tenanted[] rows = [.. Enumerable.Range(0, 100_000).Select(i => new Tenanted(
                    $"tenant-{random.Next(4)}", (part * 100_000L) + i, (short)(200 + random.Next(4)), random.NextDouble(), random.Next(10)))];
                await using ObjectDraft draft = writing.StartObject();
                await draft.Writer.WriteAsync<Tenanted>(rows, Ct);
                await writing.AppendAsync(draft, Ct);
            }

            while (await writing.CompactAsync(new CompactionOptions { LevelZeroCeiling = 1 }, Ct) is not null)
            {
            }
        }

        List<(long Reads, long Bytes, long Answer)> seen = [];
        foreach (CostProfile profile in new[] { CostProfile.Remote, CostProfile.LocalDisk, CostProfile.Memory })
        {
            await using CountingObjectStore store = new CountingObjectStore(memory);
            await using VortexSession session = VortexSession.Create(_ => { });
            await using VortexDataset dataset = await VortexDataset.OpenAsync(store, new DatasetOptions { Session = session, CostProfile = profile }, Ct);
            store.Reset();
            List<Total> answers = await dataset.Scan<Tenanted>().Where(r => r.Tenant == "tenant-2")
                .GroupBy(r => (r.Status, r.Code)).Select(g => (g.Key.Status, g.Key.Code, g.Count())).As<Total>().ToRecordsAsync(Ct).ToListAsync(Ct);
            seen.Add((store.CountOf(ObjectOperation.Read), store.BytesRead, answers.Sum(static total => total.Count)));
        }

        Console.WriteLine($"PROFILES: {string.Join("; ", seen.Select(static s => $"{s.Reads} reads, {s.Bytes:N0} bytes, {s.Answer} rows"))}");
        Assert.All(seen, s => Assert.Equal(seen[0].Answer, s.Answer));
        Assert.True(seen[0].Reads <= seen[1].Reads && seen[1].Reads <= seen[2].Reads && seen[0].Reads < seen[2].Reads, "cheaper requests, more of them");
        Assert.True(seen[0].Bytes >= seen[1].Bytes && seen[1].Bytes >= seen[2].Bytes && seen[0].Bytes > seen[2].Bytes, "and fewer bytes");
    }

    /// <summary>
    /// A filtered read in an order, a group by with a count and a sum, the distinct values of a column, and the distinct keys
    /// of the key's column: each explains its logical plan, naming the version it reads and its exactness (B2).
    /// </summary>
    [Fact]
    public async Task EveryQuestionExplainsItsLogicalPlan()
    {
        Random random = new Random(89);
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await CreateAsync(store, Ct);
        await AppendAsync(dataset, [.. Enumerable.Range(0, 50_000).Select(i => new Row(random.Next(0, 2), $"svc-{random.Next(0, 50):D2}", Start.AddSeconds(i), i, random.NextDouble()))]);
        string[] logical =
        [
            (await dataset.Scan<Row>().Where(r => r.Service == "svc-01").OrderByDescending(r => r.At).ExplainAsync(Ct)).Logical,
            (await dataset.Scan<Row>().Where(r => r.Day == 1).GroupBy(r => r.Service).Select(g => (g.Key, g.Count(), g.Sum(r => r.Value))).ExplainAsync(Ct)).Logical,
            (await dataset.Scan<Row>().With(new ScanOptions { UseSynopses = false }).Select(r => r.Service).Distinct().ExplainAsync(Ct)).Logical,
            (await dataset.Scan<Row>().Keys(r => r.At).Distinct().ExplainAsync(Ct)).Logical,
        ];
        Assert.All(logical, static text => Assert.True(text.Contains("of version", StringComparison.Ordinal) && text.Contains("exact", StringComparison.Ordinal), text));
    }

    /// <summary>
    /// Six appends over six hours, partitioned by the hour, keyed by the service, consolidated and rolled up: a count by
    /// service is answered by rollups or by each object's counts, its units explained; a count under a filter the counts do
    /// not answer reads rows and learns its family's partials, which answer it alone the second time; every answer is
    /// LINQ's (B3). Each unit takes the least weight of the alternatives that cover it, a rollup no more than its objects: the
    /// weights adding up, the plan is the least of every assignment of alternatives to its units (spec B, 6.3 and 9).
    /// </summary>
    [Fact]
    public async Task EachUnitIsAnsweredByAnAlternativeChosenAndExplained()
    {
        Random random = new Random(97);
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Row.Schema, new DatasetOptions
        {
            Layout = new DatasetLayout
            {
                Partition = LayoutValue.Column(nameof(Row.At)).Truncate(CalendarUnit.Hour),
                SortKey = [LayoutValue.Column(nameof(Row.Service))],
                ObjectBytes = 1 << 18,
                MergeFanout = 4,
                CalmAfter = null,
            },
            ContentsObjectBytes = 0,
            SheetObjectBytes = 0,
            RollupFanout = 2,
        }, Ct);
        List<Row> all = [];
        for (int a = 0; a < 6; a++)
        {
            Row[] rows = [.. Enumerable.Range(0, 20_000).Select(i => new Row(
                0, $"svc-{random.Next(0, 30):D2}", Start.AddTicks((long)(random.NextDouble() * 6 * TimeSpan.TicksPerHour) / 10 * 10), all.Count + i, random.NextDouble()))];
            all.AddRange(rows);
            await AppendAsync(dataset, rows);
        }

        while (await dataset.CompactAsync(cancellationToken: Ct) is not null)
        {
        }

        await dataset.ConsolidateAsync(Ct);
        await dataset.RollUpAsync(Ct);

        ScanPlan byService = await dataset.Scan<Row>().GroupBy(r => r.Service).Select(g => g.Count()).ExplainAsync(Ct);
        Console.WriteLine($"UNITS: {string.Join(", ", byService.Units.GroupBy(static unit => unit.Alternative).Select(static g => $"{g.Count()} {g.Key}"))}");
        Assert.Contains(byService.Units, static unit => unit.Alternative is "rollup" or "counts");
        Assert.All(byService.Units, static unit => Assert.False(string.IsNullOrEmpty(unit.Detail)));
        TakesTheLeast(byService);
        Assert.Equal(
            all.GroupBy(static row => row.Service).Select(static g => (long)g.Count()).Order(),
            (await dataset.Scan<Row>().GroupBy(r => r.Service).Select(g => g.Count()).ToListAsync(Ct)).Order());

        List<long> hot = [.. all.Where(static row => row.Value > 0.5).GroupBy(static row => row.Service).Select(static g => (long)g.Count()).Order()];
        for (int pass = 0; pass < 2; pass++)
        {
            ScanPlan plan = await dataset.Scan<Row>().Where(r => r.Value > 0.5).GroupBy(r => r.Service).Select(g => g.Count()).ExplainAsync(Ct);
            Assert.Equal(hot, (await dataset.Scan<Row>().Where(r => r.Value > 0.5).GroupBy(r => r.Service).Select(g => g.Count()).ToListAsync(Ct)).Order());
            Assert.True(pass == 0 || plan.Units.All(static unit => unit.Alternative == "partial"), $"pass {pass}: {string.Join(", ", plan.Units.Select(static unit => unit.Alternative))}");
            TakesTheLeast(plan);
        }
    }

    /// <summary>
    /// Two appends consolidated, keyed by the service: an alternative that covers none of an object says why. A distinct
    /// count's granules decline it as a sketch, a filter its counts do not answer is unproven by its summaries, and a grouped
    /// count is more than its entry answers; no alternative of the synopses is left with no reason.
    /// </summary>
    [Fact]
    public async Task AnAlternativeThatCoversNoneSaysWhy()
    {
        Random random = new Random(41);
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Row.Schema, new DatasetOptions
        {
            Layout = new DatasetLayout
            {
                Partition = LayoutValue.Column(nameof(Row.At)).Truncate(CalendarUnit.Day),
                SortKey = [LayoutValue.Column(nameof(Row.Service)), LayoutValue.Column(nameof(Row.At))],
                IndexedPrefix = 1,
                ObjectBytes = 256 << 20,
                MergeFanout = 4,
                CalmAfter = null,
            },
            ContentsObjectBytes = 0,
            SheetObjectBytes = 0,
        }, Ct);
        for (int a = 0; a < 2; a++)
        {
            await AppendAsync(dataset, [.. Enumerable.Range(0, 20_000).Select(i => new Row(
                random.Next(4), $"svc-{random.Next(0, 6):D2}", Start.AddMilliseconds((a * 20_000) + i), (a * 20_000) + i, random.NextDouble()))]);
            while (await dataset.CompactAsync(cancellationToken: Ct) is not null)
            {
            }
        }

        await dataset.ConsolidateAsync(Ct);
        string[] distinct = [.. (await dataset.Scan<Row>().GroupBy(r => r.Day).Select(g => (g.Key, g.ApproximateCountDistinct(r => r.Service))).ExplainAsync(Ct)).Units.SelectMany(static unit => unit.Rejected)];
        Assert.Contains("granules: a distinct count is kept as a sketch", distinct);
        string[] filtered = [.. (await dataset.Scan<Row>().Where(r => r.Value > 0.5).GroupBy(r => r.Service).Select(g => g.Count()).ExplainAsync(Ct)).Units.SelectMany(static unit => unit.Rejected)];
        Assert.Contains("counts: its summaries do not prove the filter's conjuncts on other columns true over its rows", filtered);
        Assert.Contains("entry: the question asks more than a count of its rows or an extreme", filtered);
        foreach (string rejected in distinct.Concat(filtered))
        {
            Assert.False(
                rejected.EndsWith(": does not cover it", StringComparison.Ordinal) && !rejected.StartsWith("partial:", StringComparison.Ordinal) && !rejected.StartsWith("learn:", StringComparison.Ordinal),
                rejected);
        }
    }

    /// <summary>
    /// Each unit of <paramref name="plan"/> weighs no more than any alternative it explains it rejected for its weight, nor a
    /// rollup more than its objects, each weight explained to the unit.
    /// </summary>
    private static void TakesTheLeast(ScanPlan plan)
    {
        Assert.NotEmpty(plan.Units);
        foreach (UnitStep unit in plan.Units)
        {
            foreach (string rejected in unit.Rejected)
            {
                int at = rejected.IndexOf(": weigh", StringComparison.Ordinal);
                if (at >= 0)
                {
                    string weight = rejected[(rejected.IndexOf(' ', at + 2) + 1)..].Split(' ')[0];
                    Assert.True(unit.Weight <= double.Parse(weight, System.Globalization.CultureInfo.InvariantCulture) + 0.5, $"{unit.Unit}: {unit.Alternative} weighs {unit.Weight:F0}, {rejected}");
                }
            }
        }
    }

    /// <summary>
    /// Three appends of 30 000 rows, a second apart (B5): a count of every row is answered by each object's entry, reading
    /// nothing; a count and the last sequence by the hour, the synopses set aside, reads each object's rows with the blocks
    /// its zone maps settle taken from them; both answer as LINQ does.
    /// </summary>
    [Fact]
    public async Task TheEntryAndTheZoneMapsAnswerTheUnitsTheyCover()
    {
        Random random = new Random(103);
        await using MemoryObjectStore memory = new MemoryObjectStore();
        await using CountingObjectStore store = new CountingObjectStore(memory);
        await using VortexDataset dataset = await CreateAsync(store, Ct);
        List<Row> all = [];
        for (int a = 0; a < 3; a++)
        {
            Row[] rows = [.. Enumerable.Range(0, 30_000).Select(i => new Row(a, $"svc-{random.Next(0, 50):D2}", Start.AddSeconds(all.Count + i), all.Count + i, random.NextDouble()))];
            all.AddRange(rows);
            await AppendAsync(dataset, rows);
        }

        ScanPlan counted = await dataset.ScanBuilder().WithShape(LogicalShape.Count).PlanAsync(analyze: false, Ct);
        Assert.Equal(["entry", "entry", "entry"], counted.Units.Select(static unit => unit.Alternative));
        store.Reset();
        Assert.Equal(all.Count, await dataset.Scan<Row>().CountAsync(Ct));
        Assert.Equal(0, store.CountOf(ObjectOperation.Read));

        ScanPlan settled = await dataset.Scan<Row>().With(new ScanOptions { UseSynopses = false })
            .GroupBy(r => r.At.Truncate(CalendarUnit.Hour)).Select(g => (g.Key, g.Count(), g.Max(r => r.Sequence))).ExplainAsync(Ct);
        Console.WriteLine($"ZONES: {string.Join("; ", settled.Units.Select(static unit => $"{unit.Alternative} ({unit.Detail}; {string.Join(", ", unit.Rejected)})"))}");
        Assert.Equal(["zones", "zones", "zones"], settled.Units.Select(static unit => unit.Alternative));
        Assert.Equal(
            all.GroupBy(static row => new DateTime(row.At.Ticks / TimeSpan.TicksPerHour * TimeSpan.TicksPerHour, DateTimeKind.Utc))
                .Select(static g => new HourTotal(g.Key, g.Count(), g.Max(static row => row.Sequence))),
            (await dataset.Scan<Row>().With(new ScanOptions { UseSynopses = false })
                .GroupBy(r => r.At.Truncate(CalendarUnit.Hour)).Select(g => (g.Key, g.Count(), g.Max(r => r.Sequence))).As<HourTotal>().ToRecordsAsync(Ct).ToListAsync(Ct)).OrderBy(static total => total.Hour));
    }

    /// <summary>
    /// Six hours partitioned by the hour, keyed by the service, consolidated: each question's plan, worked out by a handle of
    /// its own, predicts what a cold handle asks of the store (spec B, 9): a count by service its granules' statistics answer,
    /// to the request and the byte; a sum under a filter read as rows, each object learning its family's partial from them,
    /// which costs no more where the key keeps every row, to the request, its bytes to a thousandth, the padding between
    /// segments being what the table of contents does not keep.
    /// </summary>
    [Fact]
    public async Task ThePlanPredictsWhatAColdReadAsksOfTheStore()
    {
        await using MemoryObjectStore memory = new MemoryObjectStore();
        (DatasetOptions options, List<Row> all) = await HoursAsync(memory);

        (ScanPlan counted, long reads, long bytes) = await PredictedAndAskedAsync(
            memory,
            options,
            dataset => dataset.Scan<Row>().GroupBy(r => r.Service).Select(g => g.Count()).ExplainAsync(analyze: true, Ct),
            async dataset => Assert.Equal(all.Count, (await dataset.Scan<Row>().GroupBy(r => r.Service).Select(g => g.Count()).ToListAsync(Ct)).Sum()));
        Assert.All(counted.Units, static unit => Assert.Equal("granules", unit.Alternative));
        Assert.Equal((counted.Cost!.Requests, counted.Cost.Bytes), (reads, bytes));

        (ScanPlan summed, reads, bytes) = await PredictedAndAskedAsync(
            memory,
            options,
            dataset => dataset.Scan<Row>().Where(r => r.Value > 0.9).GroupBy(r => r.Service).Select(g => g.Sum(r => r.Sequence)).ExplainAsync(analyze: true, Ct),
            async dataset => Assert.Equal(
                all.Where(static row => row.Value > 0.9).Sum(static row => row.Sequence),
                (await dataset.Scan<Row>().Where(r => r.Value > 0.9).GroupBy(r => r.Service).Select(g => g.Sum(r => r.Sequence)).ToListAsync(Ct)).Sum()));
        Assert.All(summed.Units, static unit => Assert.Equal("learn", unit.Alternative));
        Assert.Equal(summed.Cost!.Requests, reads);
        Assert.InRange(bytes, summed.Cost.Bytes - (summed.Cost.Bytes / 1000), summed.Cost.Bytes + (summed.Cost.Bytes / 1000));
    }

    /// <summary>
    /// Two hours of calls partitioned by the hour, keyed by the service then the instant, consolidated, in short chunks: the
    /// hosts of one route, a service's alone, at eight lanes, whose pieces of each object plan their states through their
    /// sheets and the lookup of the question's partitions' indexes: each partition's index is read once, where each piece
    /// read it again. QP-2's pods of a route at 32 lanes read three regions of their partition's index 36 times, 37 MB of
    /// the 220 the question read.
    /// </summary>
    [Fact]
    public async Task AQuestionsLanesReadEachPartitionsIndexOnce()
    {
        Random random = new Random(0xA9);
        await using MemoryObjectStore memory = new MemoryObjectStore();
        DatasetOptions options = new DatasetOptions
        {
            Layout = new DatasetLayout
            {
                Partition = LayoutValue.Column(nameof(DatasetLearnedPartialsTests.Call.At)).Truncate(CalendarUnit.Hour),
                SortKey = [LayoutValue.Column(nameof(DatasetLearnedPartialsTests.Call.Service)), LayoutValue.Column(nameof(DatasetLearnedPartialsTests.Call.At))],
                IndexedPrefix = 1,
                ObjectBytes = 1 << 18,
                MergeFanout = 4,
                CalmAfter = null,
            },
            ContentsObjectBytes = 0,
            SheetObjectBytes = 0,
            Write = new VortexWriteOptions { ChunkTargetBytes = 16 << 10, BlockRows = 1_024 },
            ChunkBytes = 16 << 10,
            GroupRows = 1_000,
        };
        await using (VortexDataset writing = await VortexDataset.CreateAsync(memory, DatasetLearnedPartialsTests.Call.Schema, options, Ct))
        {
            for (int part = 0; part < 4; part++)
            {
                DatasetLearnedPartialsTests.Call[] rows = [.. Enumerable.Range(0, 20_000).Select(i =>
                {
                    int service = random.Next(4);
                    return new DatasetLearnedPartialsTests.Call(
                        $"svc-{service}", Start.AddTicks(random.NextInt64(TimeSpan.FromHours(2).Ticks)), $"h-{service}-{random.Next(3)}",
                        $"/svc{service}/r{random.Next(6)}", (short)200, random.Next(4_000) / 4.0, random.Next(1_000_000));
                })];
                await using ObjectDraft draft = writing.StartObject();
                await draft.Writer.WriteAsync<DatasetLearnedPartialsTests.Call>(rows, Ct);
                await writing.AppendAsync(draft, Ct);
            }

            while (await writing.CompactAsync(cancellationToken: Ct) is not null)
            {
            }

            await writing.ConsolidateAsync(Ct);
        }

        LoggingStore logged = new LoggingStore(memory);
        await using VortexSession session = VortexSession.Create(o => o.MaxDegreeOfParallelism = 8);
        await using VortexDataset dataset = await VortexDataset.OpenAsync(logged, options with { Session = session, LeastPieceRows = 250 }, Ct);
        HashSet<string> objects = [];
        await foreach (DataObject held in dataset.ListObjectsAsync(Ct))
        {
            objects.Add(held.Key);
        }

        logged.Reads.Clear();
        List<string> hosts = await DatasetPlannerTests.AskAsync(dataset, ["Host"], ["Host", "count()"], "Route = '/svc1/r3'", synopses: true, lanes: 8);
        IGrouping<string, (string Key, long Offset, long Length, long Size)>[] indexes = [.. logged.Reads.GroupBy(static access => access.Key).Where(reads => !objects.Contains(reads.Key))];
        Assert.Equal(await DatasetPlannerTests.AskAsync(dataset, ["Host"], ["Host", "count()"], "Route = '/svc1/r3'", synopses: false, lanes: 8), hosts);
        Assert.Equal(3, hosts.Count);
        Assert.Equal(2, indexes.Length);
        Assert.All(indexes, static reads => Assert.True(reads.Count() == 1, $"{reads.Key}: {reads.Count()} reads"));
    }

    /// <summary>
    /// The same six hours: the rows of one service, found through each partition's index (D13), read each index in one
    /// request, whole, and each object in three at most, its open, the granules of its key's column, the other columns' at
    /// the rows kept: the marks of its columns, in the tail its open reads, cost no request of their own.
    /// </summary>
    [Fact]
    public async Task APartitionsIndexAndTheMarksInAnOpensTailCostNoRequestOfTheirOwn()
    {
        await using MemoryObjectStore memory = new MemoryObjectStore();
        (DatasetOptions options, List<Row> all) = await HoursAsync(memory);
        LoggingStore logged = new LoggingStore(memory);
        await using VortexSession session = VortexSession.Create(_ => { });
        await using VortexDataset dataset = await VortexDataset.OpenAsync(logged, options with { Session = session }, Ct);
        HashSet<string> objects = [];
        await foreach (DataObject held in dataset.ListObjectsAsync(Ct))
        {
            objects.Add(held.Key);
        }

        logged.Reads.Clear();
        Assert.Equal(all.LongCount(static row => row.Service == "svc-07"), await dataset.Scan<Row>().Where(r => r.Service == "svc-07").ToRecordsAsync(Ct).LongCountAsync(Ct));
        IGrouping<string, (string Key, long Offset, long Length, long Size)>[] read = [.. logged.Reads.GroupBy(static access => access.Key)];
        IGrouping<string, (string Key, long Offset, long Length, long Size)>[] indexes = [.. read.Where(reads => !objects.Contains(reads.Key))];
        Assert.Equal(6, indexes.Length);
        Assert.All(indexes, static reads => Assert.True(reads.Count() == 1 && reads.Single().Length == reads.Single().Size, $"{reads.Key}: {reads.Count()} reads"));
        Assert.All(read.Where(reads => objects.Contains(reads.Key)), static reads =>
        {
            long tail = Math.Max(0, reads.First().Size - ObjectStoreSource.TailBytes) & ~63L;
            Assert.True(reads.Count() <= 3 && reads.Count(access => access.Offset >= tail) == 1, $"{reads.Key}: {string.Join(", ", reads.Select(static access => $"[{access.Offset}, +{access.Length}]"))}");
        });
    }

    /// <summary>
    /// Six appends of 20 000 rows over six hours, partitioned by the hour and keyed by the service, merged and consolidated:
    /// an object an hour, and an index of each hour's partition.
    /// </summary>
    private static async Task<(DatasetOptions Options, List<Row> All)> HoursAsync(IObjectStore store)
    {
        Random random = new Random(107);
        DatasetOptions options = new DatasetOptions
        {
            Layout = new DatasetLayout
            {
                Partition = LayoutValue.Column(nameof(Row.At)).Truncate(CalendarUnit.Hour),
                SortKey = [LayoutValue.Column(nameof(Row.Service))],
                ObjectBytes = 1 << 18,
                MergeFanout = 4,
                CalmAfter = null,
            },
            ContentsObjectBytes = 0,
            SheetObjectBytes = 0,
        };
        List<Row> all = [];
        await using VortexDataset writing = await VortexDataset.CreateAsync(store, Row.Schema, options, Ct);
        for (int a = 0; a < 6; a++)
        {
            Row[] rows = [.. Enumerable.Range(0, 20_000).Select(i => new Row(
                random.Next(0, 3), $"svc-{random.Next(0, 30):D2}", Start.AddTicks((long)(random.NextDouble() * 6 * TimeSpan.TicksPerHour) / 10 * 10), all.Count + i, random.NextDouble()))];
            all.AddRange(rows);
            await AppendAsync(writing, rows);
        }

        while (await writing.CompactAsync(cancellationToken: Ct) is not null)
        {
        }

        await writing.ConsolidateAsync(Ct);
        return (options, all);
    }

    /// <summary>
    /// The plan <paramref name="explain"/> gives through a handle of its own, then the reads and bytes <paramref name="ask"/>
    /// asks of the store through a cold one.
    /// </summary>
    private static async Task<(ScanPlan Plan, long Reads, long Bytes)> PredictedAndAskedAsync(
        IObjectStore memory, DatasetOptions options, Func<VortexDataset, ValueTask<ScanPlan>> explain, Func<VortexDataset, Task> ask)
    {
        ScanPlan plan;
        await using (VortexSession explaining = VortexSession.Create(_ => { }))
        await using (VortexDataset explained = await VortexDataset.OpenAsync(memory, options with { Session = explaining }, Ct))
        {
            plan = await explain(explained);
        }

        await using CountingObjectStore store = new CountingObjectStore(memory);
        await using VortexSession session = VortexSession.Create(_ => { });
        await using VortexDataset dataset = await VortexDataset.OpenAsync(store, options with { Session = session }, Ct);
        store.Reset();
        await ask(dataset);
        return (plan, store.CountOf(ObjectOperation.Read), store.BytesRead);
    }

    /// <summary>
    /// Conjunctions of conjuncts cheap and dear, selective and not, an or inside one, one that keeps nothing, in the orders
    /// they are written: each reads and counts LINQ's rows, its sequences summing as LINQ's do (B3).
    /// </summary>
    [Fact]
    public async Task AConjunctionKeepsLinqsRowsInWhicheverOrderItIsWritten()
    {
        Random random = new Random(101);
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await CreateAsync(store, Ct);
        Row[] rows = [.. Enumerable.Range(0, 100_000).Select(i => new Row(random.Next(0, 7), $"svc-{random.Next(0, 50):D2}", Start.AddSeconds(i), i, random.NextDouble()))];
        await AppendAsync(dataset, rows);
        (string Name, Func<Probe<Row>, Predicate> Filter, Func<Row, bool> Want)[] cases =
        [
            ("dear selective, cheap open", r => r.Service == "svc-03" & r.Sequence >= 0, static r => r.Service == "svc-03" && r.Sequence >= 0),
            ("cheap open, dear selective", r => r.Sequence >= 0 & r.Service == "svc-03", static r => r.Sequence >= 0 && r.Service == "svc-03"),
            ("three, an or inside", r => r.Value > 0.2 & (r.Day == 3 | r.Day == 5) & r.Service != "svc-07", static r => r.Value > 0.2 && (r.Day == 3 || r.Day == 5) && r.Service != "svc-07"),
            ("none kept", r => r.Sequence < 100 & r.Sequence > 200 & r.Service == "svc-01", static r => r.Sequence < 100 && r.Sequence > 200 && r.Service == "svc-01"),
        ];
        foreach ((string name, Func<Probe<Row>, Predicate> filter, Func<Row, bool> want) in cases)
        {
            List<Row> read = await dataset.Scan<Row>().Where(filter).ToRecordsAsync(Ct).ToListAsync(Ct);
            Assert.True(read.Count == rows.Count(want) && read.Sum(static r => r.Sequence) == rows.Where(want).Sum(static r => r.Sequence), $"{name}: {read.Count} rows read");
            Assert.Equal(rows.LongCount(want), await dataset.Scan<Row>().Where(filter).CountAsync(Ct));
        }
    }

    /// <summary>
    /// The engine sees a source through its capabilities alone (B4): no member of <see cref="ScanSource"/> a source may
    /// override is overridden by one source alone, which would make it a member for that source.
    /// </summary>
    [Fact]
    public void NoMemberOfASourceIsOneASingleSourceOverrides()
    {
        Type[] sources = [.. new[] { typeof(ScanSource).Assembly, typeof(VortexDataset).Assembly }
            .SelectMany(static assembly => assembly.GetTypes())
            .Where(static type => type != typeof(ScanSource) && typeof(ScanSource).IsAssignableFrom(type))];
        const BindingFlags Declared = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        MethodInfo[] members = [.. typeof(ScanSource).GetMethods(Declared).Where(static method => method.IsVirtual && !method.IsFinal)];
        Assert.NotEmpty(members);
        foreach (MethodInfo member in members)
        {
            Type[] overriding = [.. sources.Where(type => type.GetMethods(Declared).Any(method => method.GetBaseDefinition().HasSameMetadataDefinitionAs(member)))];
            Assert.True(overriding.Length != 1, $"{member.Name} is overridden by {overriding.FirstOrDefault()?.Name} alone");
        }
    }

    private static ValueTask<VortexDataset> CreateAsync(IObjectStore store, CancellationToken cancellationToken) =>
        VortexDataset.CreateAsync(store, Row.Schema, new DatasetOptions
        {
            Layout = new DatasetLayout { SortKey = [LayoutValue.Column(nameof(Row.At))], ObjectBytes = 64 << 20 },
            ContentsObjectBytes = 0,
            SheetObjectBytes = 0,
        }, cancellationToken);

    private static async Task AppendAsync(VortexDataset dataset, Row[] rows)
    {
        await using ObjectDraft draft = dataset.StartObject();
        await draft.Writer.WriteAsync<Row>(rows, Ct);
        await dataset.AppendAsync(draft, Ct);
    }

    [VortexRecord]
    public partial record struct Row(int Day, string Service, DateTime At, long Sequence, double Value);

    [VortexRecord]
    public partial record struct Tenanted(string Tenant, long At, short Status, double Wide, int Code);

    [VortexRecord]
    public partial record struct Total(short Status, int Code, long Count);

    [VortexRecord]
    public partial record struct HourTotal(DateTime Hour, long Count, long Last);
}
