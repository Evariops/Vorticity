// Compaction by periods: level 0's exit routes the rows past every fresh object to the first fresh
// level and the others to the late level, adjacent fresh objects of one period merge into the next
// level, the late level merges into itself past its ceiling, and no object spans two periods.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.Expressions;
using Vorticity.Indexes;
using Vorticity.Scanning;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed partial class DatasetPeriodsCompactionTests
{
    private static readonly DateTimeOffset Noon = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static DatasetOptions Options(SliceWidth length, TimeSpan? lateness = null) => new DatasetOptions
    {
        ClusteringKey = [nameof(Request.At)],
        Periods = new PeriodOptions { Lateness = lateness ?? TimeSpan.FromHours(1), Length = length },
    };

    /// <summary>
    /// Once the newest instant passes a period's end by the horizon, its objects become one object of
    /// the periods level, with its time slices, whose rows read without its footer. Late rows of it
    /// wait in the late level until they crowd it, then a second seal folds them in.
    /// </summary>
    [Fact]
    public async Task AClosedPeriodIsSealedIntoOneObjectWithItsSlices()
    {
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await VortexDataset.CreateAsync(
            store, Request.Schema, Options(SliceWidth.Hour, TimeSpan.FromMinutes(10)), Ct);
        CompactionOptions compaction = new CompactionOptions { LevelZeroCeiling = 1, Fanout = 100 };
        List<DateTimeOffset> appended = [];
        for (int quarter = 0; quarter < 6; quarter++)
        {
            await AppendAsync(dataset, Noon.AddMinutes(quarter * 15), 15, appended);
        }

        Dictionary<CompactionTrigger, int> triggers = await DrainAsync(dataset, compaction);
        Assert.Equal(1, triggers.GetValueOrDefault(CompactionTrigger.Seal));
        int[] counts = await CountsAsync(dataset);
        Assert.Equal(1, counts[PeriodLevels.Periods]);
        await CheckAsync(dataset, appended, SliceWidth.Hour);

        // The period's object: its slices cover its sixty rows, and read without its footer.
        TreeEntry period = await FirstAsync(dataset, PeriodLevels.Periods);
        ObjectEntry entry = ObjectEntry.FromBytes(period.Value.Span);
        Assert.Equal(60, entry.Rows);
        await using (ObjectLease lease = await dataset.RentAsync(entry, Ct))
        {
            SliceTable slices = (await SliceTable.ReadAsync(lease.File, Ct))!;
            Assert.Equal(60, slices.Rows);
            SliceReader reader = (await SliceReader.OpenAsync(lease.File, lease.File.FileLength, lease.File.DType, null, Ct))!;
            using RecordBatch rows = await reader.ReadSliceAsync(0, [0], Ct);
            Assert.Equal(slices.Slices[0].Rows, rows.RowCount);
        }

        // A late row of the sealed hour waits beside it; a second one crowds the late level, past its
        // ceiling of one, and both are sealed in.
        await AppendAsync(dataset, Noon.AddMinutes(30).AddSeconds(30), 1, appended);
        await AppendAsync(dataset, Noon.AddMinutes(100), 5, appended);
        Assert.Equal(0, (await DrainAsync(dataset, compaction)).GetValueOrDefault(CompactionTrigger.Seal));
        Assert.Equal(1, (await CountsAsync(dataset))[PeriodLevels.Late]);

        await AppendAsync(dataset, Noon.AddMinutes(31).AddSeconds(30), 1, appended);
        await AppendAsync(dataset, Noon.AddMinutes(105), 5, appended);
        Assert.Equal(1, (await DrainAsync(dataset, compaction)).GetValueOrDefault(CompactionTrigger.Seal));
        counts = await CountsAsync(dataset);
        Assert.Equal((0, 1), (counts[PeriodLevels.Late], counts[PeriodLevels.Periods]));
        Assert.Equal(62, ObjectEntry.FromBytes((await FirstAsync(dataset, PeriodLevels.Periods)).Value.Span).Rows);
        await CheckAsync(dataset, appended, SliceWidth.Hour);
    }

    /// <summary>
    /// A seal one handle planned is never abandoned by late rows of its period that another handle
    /// appends and exits meanwhile: the exit sends them to the late level, which the seal read nothing
    /// of that it removes. Hour after hour, each held seal commits.
    /// </summary>
    [Fact]
    public async Task ALateRowNeverAbandonsASeal()
    {
        await using MemoryObjectStore store = new MemoryObjectStore();
        DatasetOptions options = Options(SliceWidth.Hour, TimeSpan.FromMinutes(10));
        await using VortexDataset writer = await VortexDataset.CreateAsync(store, Request.Schema, options, Ct);
        await using VortexDataset sealer = await VortexDataset.OpenAsync(store, options, Ct);
        CompactionOptions compaction = new CompactionOptions { LevelZeroCeiling = 1 };
        List<DateTimeOffset> appended = [];
        int seals = 0;
        for (int hour = 0; hour < 5; hour++)
        {
            // An hour of rows and twenty minutes of the next, which closes it; their exit runs.
            DateTimeOffset start = Noon.AddHours(hour);
            await AppendAsync(writer, start, 40, appended);
            await AppendAsync(writer, start.AddMinutes(40), 40, appended);
            while (await writer.PlanCompactionAsync(compaction, Ct) is { Job.Trigger: CompactionTrigger.LevelZeroCeiling })
            {
                await writer.CompactAsync(compaction, Ct);
            }

            // The other handle plans the seal of the hour and holds it while late rows of that hour
            // come in and leave level 0.
            await sealer.RefreshAsync(Ct);
            CompactionJob? seal = (await sealer.PlanCompactionAsync(compaction, Ct)).Job;
            if (seal is not { Trigger: CompactionTrigger.Seal })
            {
                continue;
            }

            await AppendAsync(writer, start.AddMinutes(30).AddSeconds(30), 1, appended);
            await AppendAsync(writer, start.AddMinutes(31).AddSeconds(30), 1, appended);
            Assert.Equal(CompactionTrigger.LevelZeroCeiling, (await writer.CompactAsync(compaction, Ct))!.Trigger);

            Assert.Equal(OperationOutcome.Applied, (await DatasetCompactor.RunAsync(sealer, seal, Ct)).Outcome);
            seals++;
            await writer.RefreshAsync(Ct);
            await CheckAsync(writer, appended, SliceWidth.Hour);
        }

        Assert.True(seals >= 3, $"only {seals} seals were held across late rows");
    }

    /// <summary>
    /// Three handles interleave appends in order with late rows, deletes, and compactions each planned
    /// on the version its handle saw and committed later, after others moved the levels: after every
    /// step the invariants of periods hold on the latest version, and the rows are those appended and
    /// not deleted. A job the ground moved under is abandoned, never applied over it.
    /// </summary>
    [Theory]
    [InlineData(5)]
    [InlineData(17)]
    [InlineData(23)]
    public async Task InterleavedHandlesKeepTheInvariantsOfPeriods(int seed)
    {
        await using MemoryObjectStore store = new MemoryObjectStore();
        DatasetOptions options = Options(SliceWidth.Hour, TimeSpan.FromMinutes(10));
        await using VortexDataset first = await VortexDataset.CreateAsync(store, Request.Schema, options, Ct);
        await using VortexDataset second = await VortexDataset.OpenAsync(store, options, Ct);
        await using VortexDataset third = await VortexDataset.OpenAsync(store, options, Ct);
        await using VortexDataset reader = await VortexDataset.OpenAsync(store, options, Ct);
        VortexDataset[] handles = [first, second, third];
        CompactionJob?[] held = new CompactionJob?[handles.Length];
        CompactionOptions compaction = new CompactionOptions { LevelZeroCeiling = 2, Fanout = 3 };
        Random random = new Random(seed);
        List<DateTimeOffset> appended = [];
        List<long> statuses = [];
        Dictionary<(CompactionTrigger, OperationOutcome), int> outcomes = [];
        HashSet<UInt128> seen = [];
        DateTimeOffset newest = Noon;
        for (int step = 0; step < 240; step++)
        {
            int at = random.Next(handles.Length);
            VortexDataset handle = handles[at];
            switch (random.Next(12))
            {
                case < 5:
                {
                    List<DateTimeOffset> instants = [];
                    for (int i = 5 + random.Next(25); i > 0; i--)
                    {
                        newest = newest.AddSeconds(1 + random.Next(3));
                        instants.Add(random.Next(100) == 0 ? newest.AddSeconds(-random.Next(1, 1_800)) : newest);
                    }

                    await AppendAsync(handle, instants, appended, statuses);
                    break;
                }

                case < 8:
                    held[at] = (await handle.PlanCompactionAsync(compaction, Ct)).Job;
                    break;

                case < 11:
                    if (held[at] is { } job)
                    {
                        held[at] = null;
                        CompactionResult result = await DatasetCompactor.RunAsync(handle, job, Ct);
                        outcomes[(result.Trigger, result.Outcome)] = outcomes.GetValueOrDefault((result.Trigger, result.Outcome)) + 1;
                    }
                    else
                    {
                        await handle.RefreshAsync(Ct);
                    }

                    break;

                default:
                {
                    // A delete takes the rows of the version its handle reads: refreshed, that is every
                    // row the model holds.
                    long status = 200 + random.Next(30);
                    await handle.RefreshAsync(Ct);
                    await handle.DeleteAsync(VortexExpr.Parse($"Status = {status}"), Ct);
                    for (int i = appended.Count - 1; i >= 0; i--)
                    {
                        if (statuses[i] == status)
                        {
                            appended.RemoveAt(i);
                            statuses.RemoveAt(i);
                        }
                    }

                    break;
                }
            }

            await reader.RefreshAsync(Ct);
            await CheckAsync(reader, appended, SliceWidth.Hour);

            // Every object a version adds sorted as its slice table says, its synopsis tables the merge of its runs.
            await SortedAsTheirTablesAsync(reader, seen);
        }

        (int sortedObjects, int withTables) = await SortedAsTheirTablesAsync(reader);
        Console.Out.Write(FormattableString.Invariant(
            $"PERIODS INTERLEAVED seed {seed}: {appended.Count} rows, version {reader.Version}, {sortedObjects} objects in sorted slices, {withTables} with synopsis tables, {string.Join(", ", outcomes.OrderBy(o => o.Key).Select(o => $"{o.Key.Item1} {o.Key.Item2} {o.Value}"))}.\n"));
        // Half the exits or so are abandoned: another handle emptied level 0 first.
        Assert.True(outcomes.Where(o => o.Key.Item2 == OperationOutcome.Applied).Sum(o => o.Value) > 5, "the schedule must compact");
        Assert.True(outcomes.Where(o => o.Key.Item2 == OperationOutcome.Abandoned).Sum(o => o.Value) > 0, "the schedule must commit a job the ground moved under");
        Assert.True(outcomes.GetValueOrDefault((CompactionTrigger.Seal, OperationOutcome.Applied)) > 0, "the schedule must seal a period");
    }

    /// <summary>Runs every compaction due, and counts them by trigger.</summary>
    private static async Task<Dictionary<CompactionTrigger, int>> DrainAsync(VortexDataset dataset, CompactionOptions compaction)
    {
        Dictionary<CompactionTrigger, int> triggers = [];
        int jobs = 0;
        while (await dataset.CompactAsync(compaction, Ct) is { } result)
        {
            Assert.Equal(OperationOutcome.Applied, result.Outcome);
            Assert.True(++jobs < 50, $"compaction does not settle, {result.Trigger} again");
            triggers[result.Trigger] = triggers.GetValueOrDefault(result.Trigger) + 1;
        }

        return triggers;
    }

    private static async Task<TreeEntry> FirstAsync(VortexDataset dataset, int level)
    {
        await foreach (TreeEntry entry in dataset.Levels[level].EnumerateAsync(dataset.Pages, Ct))
        {
            return entry;
        }

        throw new InvalidOperationException($"Level {level} holds no object.");
    }

    [Fact]
    public async Task TheStyleFollowsThePeriodsAndTheirLengthIsKept()
    {
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using (VortexDataset dataset = await VortexDataset.CreateAsync(store, Request.Schema, Options(SliceWidth.Minute), Ct))
        {
            Assert.Equal(CompactionStyle.Periods, (await dataset.PlanCompactionAsync(null, Ct)).Style);
            await Assert.ThrowsAsync<ArgumentException>(async () => await dataset.PlanCompactionAsync(new CompactionOptions { Style = CompactionStyle.Leveled }, Ct));
        }

        await using VortexDataset reopened = await VortexDataset.OpenAsync(store, new DatasetOptions(), Ct);
        Assert.Equal(SliceWidth.Minute, reopened.Options.Periods!.Length);
        Assert.Throws<ArgumentOutOfRangeException>(() => new PeriodOptions { Length = SliceWidth.Second });

        await using MemoryObjectStore plain = new MemoryObjectStore();
        await using VortexDataset leveled = await VortexDataset.CreateAsync(plain, Request.Schema, new DatasetOptions { ClusteringKey = [nameof(Request.At)] }, Ct);
        await Assert.ThrowsAsync<ArgumentException>(async () => await leveled.PlanCompactionAsync(new CompactionOptions { Style = CompactionStyle.Periods }, Ct));
    }

    /// <summary>
    /// The first exit finds no fresh object and sends every row to the first fresh level; the next
    /// sends rows older than the last fresh instant to the late level, and the newer ones after it.
    /// </summary>
    [Fact]
    public async Task TheExitRoutesEachRowByTheNewestFreshInstant()
    {
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Request.Schema, Options(SliceWidth.Hour), Ct);
        CompactionOptions compaction = new CompactionOptions { LevelZeroCeiling = 2 };
        List<DateTimeOffset> appended = [];
        await AppendAsync(dataset, Noon, 20, appended);
        await AppendAsync(dataset, Noon.AddMinutes(20), 20, appended);
        await AppendAsync(dataset, Noon.AddMinutes(40), 20, appended);

        CompactionJob exit = (await dataset.PlanCompactionAsync(compaction, Ct)).Job!;
        Assert.Equal((0, PeriodLevels.FirstFresh, CompactionTrigger.LevelZeroCeiling), (exit.FromLevel, exit.ToLevel, exit.Trigger));
        Assert.Equal(OperationOutcome.Applied, (await dataset.CompactAsync(compaction, Ct))!.Outcome);
        Assert.Equal(new[] { 0, 0, 0, 1 }, await CountsAsync(dataset));

        // Late rows, an hour and a half back, then two minutes of new ones in the next hour.
        await AppendAsync(dataset, Noon.AddMinutes(-90), 10, appended);
        await AppendAsync(dataset, Noon.AddMinutes(60), 20, appended);
        await AppendAsync(dataset, Noon.AddMinutes(80), 20, appended);
        Assert.Equal(OperationOutcome.Applied, (await dataset.CompactAsync(compaction, Ct))!.Outcome);
        Assert.Equal(new[] { 0, 1, 0, 2 }, await CountsAsync(dataset));
        await CheckAsync(dataset, appended, SliceWidth.Hour);
    }

    /// <summary>Three adjacent fresh objects of one period merge into one of the next fresh level.</summary>
    [Fact]
    public async Task AdjacentFreshObjectsOfOnePeriodMergeUp()
    {
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Request.Schema, Options(SliceWidth.Hour), Ct);
        CompactionOptions compaction = new CompactionOptions { LevelZeroCeiling = 1, Fanout = 3 };
        List<DateTimeOffset> appended = [];
        for (int exit = 0; exit < 3; exit++)
        {
            await AppendAsync(dataset, Noon.AddMinutes(exit * 10), 5, appended);
            await AppendAsync(dataset, Noon.AddMinutes((exit * 10) + 5), 5, appended);
            Assert.Equal(CompactionTrigger.LevelZeroCeiling, (await dataset.CompactAsync(compaction, Ct))!.Trigger);
        }

        Assert.Equal(new[] { 0, 0, 0, 3 }, await CountsAsync(dataset));
        CompactionResult merged = (await dataset.CompactAsync(compaction, Ct))!;
        Assert.Equal((CompactionTrigger.FreshMerge, PeriodLevels.FirstFresh, PeriodLevels.FirstFresh + 1), (merged.Trigger, merged.FromLevel, merged.ToLevel));
        Assert.Equal(new[] { 0, 0, 0, 0, 1 }, await CountsAsync(dataset));
        Assert.Null(await dataset.CompactAsync(compaction, Ct));
        await CheckAsync(dataset, appended, SliceWidth.Hour);
    }

    /// <summary>Past level 0's ceiling, the late level merges into one object per period, in place.</summary>
    [Fact]
    public async Task TheLateLevelMergesPastItsCeiling()
    {
        // A horizon of three hours keeps every hour open, so that no seal takes the late rows first.
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await VortexDataset.CreateAsync(
            store, Request.Schema, Options(SliceWidth.Hour, TimeSpan.FromHours(3)), Ct);
        CompactionOptions compaction = new CompactionOptions { LevelZeroCeiling = 1 };
        List<DateTimeOffset> appended = [];
        await AppendAsync(dataset, Noon, 30, appended);
        await AppendAsync(dataset, Noon.AddMinutes(30), 30, appended);
        await dataset.CompactAsync(compaction, Ct);

        // Two exits of late rows from two hours: each leaves an object per hour in the late level.
        await AppendAsync(dataset, Noon.AddMinutes(-70), 20, appended);
        await AppendAsync(dataset, Noon.AddMinutes(-30), 10, appended);
        await dataset.CompactAsync(compaction, Ct);
        await AppendAsync(dataset, Noon.AddMinutes(-65), 3, appended);
        await AppendAsync(dataset, Noon.AddMinutes(-20), 3, appended);
        await dataset.CompactAsync(compaction, Ct);
        Assert.Equal(4, (await CountsAsync(dataset))[PeriodLevels.Late]);

        CompactionResult merged = (await dataset.CompactAsync(compaction, Ct))!;
        Assert.Equal((CompactionTrigger.LateCeiling, PeriodLevels.Late, PeriodLevels.Late), (merged.Trigger, merged.FromLevel, merged.ToLevel));
        Assert.Equal(2, (await CountsAsync(dataset))[PeriodLevels.Late]);
        await CheckAsync(dataset, appended, SliceWidth.Hour);
    }

    /// <summary>
    /// Appends in the order of their instants, one row in a hundred late by up to half an hour, a delete
    /// now and then, each round followed by compactions until none is due: after every round every level
    /// but 0 and the late one is key-disjoint, no object outside level 0 spans two periods, level 0
    /// stays under its ceiling and the late level too unless each of its periods holds one object, and
    /// the rows read back in key order are the rows appended and not deleted.
    /// </summary>
    [Theory]
    [InlineData(3)]
    [InlineData(11)]
    [InlineData(29)]
    public async Task AScheduleWithLateRowsKeepsTheInvariantsOfPeriods(int seed)
    {
        await using MemoryObjectStore store = new MemoryObjectStore();
        // Hours of appends of about half a minute each, an exit every few minutes: a period holds a few
        // dozen fresh objects, late rows cross its start, and it is sealed ten minutes past its end,
        // which late rows of up to half an hour then follow.
        await using VortexDataset dataset = await VortexDataset.CreateAsync(
            store, Request.Schema, Options(SliceWidth.Hour, TimeSpan.FromMinutes(10)), Ct);
        CompactionOptions compaction = new CompactionOptions { LevelZeroCeiling = 3, Fanout = 3 };
        Random random = new Random(seed);
        List<DateTimeOffset> appended = [];
        List<long> statuses = [];
        DateTimeOffset newest = Noon;
        Dictionary<CompactionTrigger, int> triggers = [];
        long ingested = 0;
        long rewritten = 0;
        int widest = 0;
        for (int round = 0; round < 300; round++)
        {
            int rows = 5 + random.Next(30);
            ingested += rows;
            List<DateTimeOffset> instants = [];
            for (int i = 0; i < rows; i++)
            {
                newest = newest.AddSeconds(1 + random.Next(2));
                instants.Add(random.Next(100) == 0 ? newest.AddSeconds(-random.Next(1, 1_800)) : newest);
            }

            await AppendAsync(dataset, instants, appended, statuses);

            // Now and then a delete of one status, which marks rows in every level or rewrites objects
            // in their own, and purges that follow.
            if (random.Next(15) == 0)
            {
                long status = 200 + random.Next(30);
                await dataset.DeleteAsync(VortexExpr.Parse($"Status = {status}"), Ct);
                for (int i = appended.Count - 1; i >= 0; i--)
                {
                    if (statuses[i] == status)
                    {
                        appended.RemoveAt(i);
                        statuses.RemoveAt(i);
                    }
                }
            }

            int jobs = 0;
            while (await dataset.CompactAsync(compaction, Ct) is { } result)
            {
                Assert.Equal(OperationOutcome.Applied, result.Outcome);
                Assert.True(++jobs < 50, $"round {round}: compaction does not settle, {result.Trigger} again");
                triggers[result.Trigger] = triggers.GetValueOrDefault(result.Trigger) + 1;
                rewritten += result.Rows;
            }

            await CheckAsync(dataset, appended, SliceWidth.Hour);
            widest = Math.Max(widest, await ObjectsOverAsync(dataset, newest - TimeSpan.FromMinutes(15), newest));
            int[] counts = await CountsAsync(dataset);
            Assert.InRange(counts[0], 0, compaction.LevelZeroCeiling);
            if (counts.Length > PeriodLevels.Late && counts[PeriodLevels.Late] > compaction.LevelZeroCeiling)
            {
                // Past its ceiling, the late level holds one object per period: only a seal lowers it.
                Assert.Equal(counts[PeriodLevels.Late], await LatePeriodsAsync(dataset, SliceWidth.Hour));
            }
        }

        (int sortedObjects, int withTables) = await SortedAsTheirTablesAsync(dataset);
        Assert.True(sortedObjects > 0, "the schedule must leave objects in sorted slices");
        Assert.True(withTables > 0, "the schedule must leave objects whose synopsis tables are worth their bytes");
        Console.Out.Write(FormattableString.Invariant(
            $"PERIODS FUZZ seed {seed}: {appended.Count} rows, levels {string.Join('/', await CountsAsync(dataset))}, triggers {string.Join(", ", triggers.Select(t => $"{t.Key} {t.Value}"))}, rewrites per row {(double)rewritten / ingested:F2}, at most {widest} objects over 15 minutes, {sortedObjects} objects in sorted slices, {withTables} with synopsis tables.\n"));
        Assert.True(triggers.GetValueOrDefault(CompactionTrigger.FreshMerge) > 0, "the schedule must merge fresh objects up");
        Assert.True(triggers.GetValueOrDefault(CompactionTrigger.LevelZeroCeiling) > 0, "the schedule must empty level 0");
        Assert.True(triggers.GetValueOrDefault(CompactionTrigger.Seal) > 0, "the schedule must seal a period");

        // Ratchets of the life of a row. A row is rewritten at level 0's exit, at a merge per fresh level
        // it climbs, about log_F of a period's exits, and at the seal: 4.65 to 4.78 measured, and five is
        // where the style would be given up. A question over a quarter of an hour opens at most level 0's
        // ceiling, the late level's, F − 1 objects of each of three fresh levels and a period: 13 here,
        // 9 to 10 measured.
        Assert.InRange((double)rewritten / ingested, 0, 5);
        Assert.InRange(widest, 1, compaction.LevelZeroCeiling + compaction.LevelZeroCeiling + (3 * (compaction.Fanout - 1)) + 1);
    }

    /// <summary>
    /// Every object above level 0 that carries a slice table holds each slice's rows sorted as the table
    /// says: by its synopsis column, the status here when it repeats enough, then by instant, and its
    /// synopsis tables count its rows. Returns the objects checked, an object a delete rewrote carrying no
    /// table, and those of them with synopsis tables. Objects <paramref name="seen"/> holds are not checked
    /// again, and those checked are added to it.
    /// </summary>
    private static async Task<(int Objects, int WithTables)> SortedAsTheirTablesAsync(VortexDataset dataset, HashSet<UInt128>? seen = null)
    {
        int checkedObjects = 0;
        int withTables = 0;
        for (int level = 1; level < dataset.Levels.Count; level++)
        {
            await foreach (TreeEntry entry in dataset.Levels[level].EnumerateAsync(dataset.Pages, Ct))
            {
                ObjectEntry stored = ObjectEntry.FromBytes(entry.Value.Span);
                if (seen is not null && !seen.Add(stored.Uid))
                {
                    continue;
                }

                await using ObjectLease lease = await dataset.RentAsync(stored, Ct);
                if (await SliceTable.ReadAsync(lease.File, Ct) is not { } table)
                {
                    continue;
                }

                // The order names the status, and the request id where ids repeat, as deletes make them.
                Dictionary<int, Comparison<Request>> by = new()
                {
                    [lease.File.DType.IndexOfField(nameof(Request.Status))] = static (l, r) => l.Status.CompareTo(r.Status),
                    [lease.File.DType.IndexOfField(nameof(Request.RequestId))] = static (l, r) => string.CompareOrdinal(l.RequestId, r.RequestId),
                };
                Assert.All(table.Order, field => Assert.True(by.ContainsKey(field), $"order {string.Join(',', table.Order)}"));
                List<Request> rows = [];
                await foreach (Request row in lease.File.Scan<Request>().ToRecordsAsync(Ct))
                {
                    rows.Add(row);
                }

                foreach (TimeSlice slice in table.Slices)
                {
                    List<Request> part = rows.GetRange((int)slice.FirstRow, (int)slice.Rows);
                    for (int i = 1; i < part.Count; i++)
                    {
                        int order = 0;
                        foreach (int field in table.Order)
                        {
                            order = by[field](part[i - 1], part[i]);
                            if (order != 0)
                            {
                                break;
                            }
                        }

                        order = order != 0 ? order : part[i - 1].At.CompareTo(part[i].At);
                        Assert.True(order <= 0, $"row {slice.FirstRow + i} comes before the row above it in its slice's order");
                    }
                }

                // Its runs count its rows, and its coarse tables are the merge of its slices' runs.
                withTables += await SynopsisTableTests.CoarseTablesAreTheMergeOfTheirRunsAsync(lease.File, table) > 0 ? 1 : 0;
                checkedObjects++;
            }
        }

        return (checkedObjects, withTables);
    }

    /// <summary>
    /// The invariants of a dataset of periods: every level above 0 but the late one key-disjoint, no
    /// object above level 0 across two periods, and the rows in key order those appended, through the
    /// key cursor and a key-ordered scan.
    /// </summary>
    private static async Task CheckAsync(VortexDataset dataset, List<DateTimeOffset> appended, SliceWidth length)
    {
        long microsecond = TimeSpan.TicksPerMillisecond / 1000;
        for (int level = 1; level < dataset.Levels.Count; level++)
        {
            long last = long.MinValue;
            await foreach (TreeEntry entry in dataset.Levels[level].EnumerateAsync(dataset.Pages, Ct))
            {
                Assert.True(ObjectEntry.FromBytes(entry.Value.Span).Summaries.TryGet(nameof(Request.At), out ColumnSummary at));
                long min = at.Min.SignedValue;
                long max = at.Max.SignedValue;
                Assert.Equal(TimeSlicer.Floor(min, TimeUnit.Microseconds, length), TimeSlicer.Floor(max, TimeUnit.Microseconds, length));
                if (dataset.Levels.IsDisjoint(level))
                {
                    Assert.True(last < min, $"level {level} holds an object from {min} after one that ends at {last}");
                    last = max;
                }
            }
        }

        List<long> sorted = [.. appended.Select(at => (at.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) / microsecond)];
        sorted.Sort();
        List<long> walked = [];
        await using (DatasetKeyCursor cursor = await DatasetKeyCursor.OpenAsync(dataset, Ct))
        {
            for (bool on = await cursor.SeekFirstAsync(Ct); on; on = await cursor.NextAsync(Ct))
            {
                walked.Add(cursor.Key.SignedValue);
            }
        }

        Assert.Equal(sorted, walked);
        Assert.Equal(appended.Order(), await dataset.Scan<Request>().OrderBy(r => r.At).Select(r => r.At).ToListAsync(Ct));
    }

    /// <summary>The objects of every level whose instants may meet <c>[from, to]</c>: what a question over that window opens.</summary>
    private static async Task<int> ObjectsOverAsync(VortexDataset dataset, DateTimeOffset from, DateTimeOffset to)
    {
        long low = from.ToUnixTimeMilliseconds() * 1000;
        long high = to.ToUnixTimeMilliseconds() * 1000;
        int objects = 0;
        for (int level = 0; level < dataset.Levels.Count; level++)
        {
            await foreach (TreeEntry entry in dataset.Levels[level].EnumerateAsync(dataset.Pages, Ct))
            {
                Assert.True(ObjectEntry.FromBytes(entry.Value.Span).Summaries.TryGet(nameof(Request.At), out ColumnSummary at));
                objects += at.Max.SignedValue >= low && at.Min.SignedValue <= high ? 1 : 0;
            }
        }

        return objects;
    }

    /// <summary>The periods the late level's objects lie in.</summary>
    private static async Task<int> LatePeriodsAsync(VortexDataset dataset, SliceWidth length)
    {
        HashSet<long> periods = [];
        await foreach (TreeEntry entry in dataset.Levels[PeriodLevels.Late].EnumerateAsync(dataset.Pages, Ct))
        {
            Assert.True(ObjectEntry.FromBytes(entry.Value.Span).Summaries.TryGet(nameof(Request.At), out ColumnSummary at));
            periods.Add(TimeSlicer.Floor(at.Min.SignedValue, TimeUnit.Microseconds, length));
        }

        return periods.Count;
    }

    /// <summary>The objects each level holds, level 0 first.</summary>
    private static async Task<int[]> CountsAsync(VortexDataset dataset)
    {
        int[] counts = new int[dataset.Levels.Count];
        for (int level = 0; level < counts.Length; level++)
        {
            await foreach (TreeEntry _ in dataset.Levels[level].EnumerateAsync(dataset.Pages, Ct))
            {
                counts[level]++;
            }
        }

        return counts;
    }

    /// <summary>Appends <paramref name="rows"/> requests a minute apart from <paramref name="from"/>, as one object.</summary>
    private static Task AppendAsync(VortexDataset dataset, DateTimeOffset from, int rows, List<DateTimeOffset> appended) =>
        AppendAsync(dataset, [.. Enumerable.Range(0, rows).Select(i => from.AddMinutes(i))], appended);

    /// <summary>Appends requests at <paramref name="instants"/>, as one object, their statuses 200 on.</summary>
    private static async Task AppendAsync(
        VortexDataset dataset, List<DateTimeOffset> instants, List<DateTimeOffset> appended, List<long>? statuses = null)
    {
        // A size drawn from the instant, which seldom repeats where ids do once deletes shrink the
        // model: a measure the synopsis tables sum, never a synopsis column.
        Request[] requests = [.. instants.Select((at, i) => new Request(at, $"r-{appended.Count + i}", 200 + i, at.ToUnixTimeSeconds() * 7_919L % 1_000_003))];
        await using ObjectDraft draft = dataset.StartObject();
        await draft.Writer.WriteAsync<Request>(requests, Ct);
        await dataset.AppendAsync(draft, Ct);
        appended.AddRange(instants);
        statuses?.AddRange(requests.Select(request => request.Status));
    }

    [VortexRecord]
    public partial record struct Request(DateTimeOffset At, string RequestId, long Status, long Bytes);
}
