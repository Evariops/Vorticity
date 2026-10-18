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
using Vorticity.Scanning;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed partial class DatasetPeriodsCompactionTests
{
    private static readonly DateTimeOffset Noon = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static DatasetOptions Options(SliceWidth length) => new DatasetOptions
    {
        ClusteringKey = [nameof(Request.At)],
        Periods = new PeriodOptions { Lateness = TimeSpan.FromHours(1), Length = length },
    };

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
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Request.Schema, Options(SliceWidth.Hour), Ct);
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
        // dozen fresh objects, and late rows cross its start.
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Request.Schema, Options(SliceWidth.Hour), Ct);
        CompactionOptions compaction = new CompactionOptions { LevelZeroCeiling = 3, Fanout = 3 };
        Random random = new Random(seed);
        List<DateTimeOffset> appended = [];
        List<long> statuses = [];
        DateTimeOffset newest = Noon;
        Dictionary<CompactionTrigger, int> triggers = [];
        for (int round = 0; round < 300; round++)
        {
            int rows = 5 + random.Next(30);
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
            }

            await CheckAsync(dataset, appended, SliceWidth.Hour);
            int[] counts = await CountsAsync(dataset);
            Assert.InRange(counts[0], 0, compaction.LevelZeroCeiling);
            if (counts.Length > PeriodLevels.Late && counts[PeriodLevels.Late] > compaction.LevelZeroCeiling)
            {
                // Past its ceiling, the late level holds one object per period: only a seal lowers it.
                Assert.Equal(counts[PeriodLevels.Late], await LatePeriodsAsync(dataset, SliceWidth.Hour));
            }
        }

        Console.Out.Write(FormattableString.Invariant(
            $"PERIODS FUZZ seed {seed}: {appended.Count} rows, levels {string.Join('/', await CountsAsync(dataset))}, triggers {string.Join(", ", triggers.Select(t => $"{t.Key} {t.Value}"))}.\n"));
        Assert.True(triggers.GetValueOrDefault(CompactionTrigger.FreshMerge) > 0, "the schedule must merge fresh objects up");
        Assert.True(triggers.GetValueOrDefault(CompactionTrigger.LevelZeroCeiling) > 0, "the schedule must empty level 0");
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
        Request[] requests = [.. instants.Select((at, i) => new Request(at, $"r-{appended.Count + i}", 200 + i))];
        await using ObjectDraft draft = dataset.StartObject();
        await draft.Writer.WriteAsync<Request>(requests, Ct);
        await dataset.AppendAsync(draft, Ct);
        appended.AddRange(instants);
        statuses?.AddRange(requests.Select(request => request.Status));
    }

    [VortexRecord]
    public partial record struct Request(DateTimeOffset At, string RequestId, long Status);
}
