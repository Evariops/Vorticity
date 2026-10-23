// Joins of small sealed periods (CompactionOptions.JoinedPeriodBytes): a partition whose periods are small next to a
// larger one joins them, in tiers, into objects near the size, the larger one's left as they are; the header names the
// feature; a late row of a joined period is folded into the whole object; an expiry keeps a joined object until the last
// period it holds lies before the cutoff; and no join spans two months.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.Expressions;
using Vorticity.Scanning;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed partial class DatasetPeriodsCompactionTests
{
    /// <summary>
    /// Thirty hours of two tenants, the small one a hundredth of the large one's rows, sealed with joins off: every hour
    /// of each is an object. Asked to join up to three of the large tenant's hours, the small tenant's join, in tiers,
    /// into objects of several hours each under the size, while the large tenant's are left as they are; the header
    /// names the feature from the first join, and every row of both reads back, by the tables and by the rows.
    /// </summary>
    [Fact]
    public async Task ASmallPartitionJoinsItsSealedPeriodsNearTheSize()
    {
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Tenanted.Schema, new DatasetOptions
        {
            ClusteringKey = [nameof(Tenanted.Tenant), nameof(Tenanted.At)],
            PartitionColumns = 1,
            Periods = new PeriodOptions { Lateness = TimeSpan.FromMinutes(5), Length = SliceWidth.Hour },
        }, Ct);
        List<Tenanted> appended = [];
        for (int hour = 0; hour < 30; hour++)
        {
            Tenanted[] rows =
            [
                .. Enumerable.Range(0, 600).Select(i => new Tenanted("large", Noon.AddHours(hour).AddSeconds(i * 6), (hour * 600) + i)),
                .. Enumerable.Range(0, 6).Select(i => new Tenanted("small", Noon.AddHours(hour).AddMinutes(i * 10), i)),
            ];
            await using ObjectDraft draft = dataset.StartObject();
            await draft.Writer.WriteAsync<Tenanted>(rows, Ct);
            await dataset.AppendAsync(draft, Ct);
            appended.AddRange(rows);
            while (await dataset.CompactAsync(new CompactionOptions { LevelZeroCeiling = 0, JoinedPeriodBytes = 0 }, Ct) is not null)
            {
            }
        }

        List<(string Tenant, long Bytes, int Hours)> before = await SealedAsync(dataset);
        long largeHour = before.Where(o => o.Tenant == "large").Min(o => o.Bytes);
        Assert.All(before, o => Assert.Equal(1, o.Hours));
        Assert.Equal(DatasetFeatures.None, dataset.Snapshot.Header.Features & DatasetFeatures.JoinedPeriods);

        // Joins up to three of the large tenant's hours: its own hours are an F-th of that, and never join.
        CompactionOptions joining = new CompactionOptions { LevelZeroCeiling = 0, Fanout = 3, JoinedPeriodBytes = 3 * largeHour };
        int joins = 0;
        while (await dataset.CompactAsync(joining, Ct) is { } result)
        {
            Assert.Equal(CompactionTrigger.Join, result.Trigger);
            Assert.Equal(OperationOutcome.Applied, result.Outcome);
            joins++;
        }

        List<(string Tenant, long Bytes, int Hours)> after = await SealedAsync(dataset);
        List<(string Tenant, long Bytes, int Hours)> small = [.. after.Where(o => o.Tenant == "small")];
        Console.Out.Write(FormattableString.Invariant(
            $"JOINS: {joins} joins; the small tenant's {before.Count(o => o.Tenant == "small")} hours of {before.Where(o => o.Tenant == "small").Average(o => o.Bytes):F0} bytes now {small.Count} objects of {string.Join('/', small.Select(o => $"{o.Hours} h {o.Bytes} B"))}, the size {joining.JoinedPeriodBytes} B.\n"));
        Assert.Equal(
            before.Where(o => o.Tenant == "large").Select(o => (o.Bytes, o.Hours)),
            after.Where(o => o.Tenant == "large").Select(o => (o.Bytes, o.Hours)));
        Assert.True(small.Count * 3 <= before.Count(o => o.Tenant == "small"), $"{small.Count} objects of the small tenant left");
        Assert.Contains(small, o => o.Hours >= 9);
        Assert.All(small, o => Assert.True(o.Bytes < joining.JoinedPeriodBytes, $"a joined object of {o.Bytes} bytes"));
        Assert.Equal(DatasetFeatures.JoinedPeriods, dataset.Snapshot.Header.Features & DatasetFeatures.JoinedPeriods);
        await CheckJoinedAsync(dataset);

        foreach (string tenant in new[] { "large", "small" })
        {
            foreach (bool synopses in new[] { true, false })
            {
                Assert.Equal(
                    appended.Count(r => r.Tenant == tenant),
                    await dataset.Scan<Tenanted>().With(new ScanOptions { UseSynopses = synopses }).Where(r => r.Tenant == tenant).CountAsync(Ct));
            }
        }

        Assert.Equal(
            appended.Where(r => r.Tenant == "small").Select(r => r.At).Order(),
            await dataset.Scan<Tenanted>().Where(r => r.Tenant == "small").OrderBy(r => r.At).Select(r => r.At).ToListAsync(Ct));
    }

    /// <summary>
    /// Five hours, three of them joined: late rows of the middle joined hour, past the horizon, wait in the late level
    /// until they crowd it, and a second seal folds them into the whole joined object, which spans the same hours and
    /// holds them; an expiry before its last hour keeps it, and one past that hour removes it.
    /// </summary>
    [Fact]
    public async Task ALateRowOfAJoinedPeriodIsFoldedIntoTheWholeObject()
    {
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await VortexDataset.CreateAsync(
            store, Request.Schema, Options(SliceWidth.Hour, TimeSpan.FromMinutes(10)), Ct);
        CompactionOptions compaction = new CompactionOptions { LevelZeroCeiling = 1, Fanout = 3 };
        List<DateTimeOffset> appended = [];
        for (int quarter = 0; quarter < 20; quarter++)
        {
            await AppendAsync(dataset, Noon.AddMinutes(quarter * 15), 15, appended);
        }

        Dictionary<CompactionTrigger, int> triggers = await DrainAsync(dataset, compaction);
        Assert.Equal(1, triggers.GetValueOrDefault(CompactionTrigger.Join));
        ObjectEntry joined = (await PeriodsLevelAsync(dataset))[0];
        Assert.Equal((Noon, Noon.AddHours(3)), HoursOf(joined));
        await CheckAsync(dataset, appended, SliceWidth.Hour);

        // Two late rows of the middle hour, each beside rows of the newest hour: the second crowds the late level.
        await AppendAsync(dataset, Noon.AddMinutes(90).AddSeconds(30), 1, appended);
        await AppendAsync(dataset, Noon.AddMinutes(300), 5, appended);
        Assert.Equal(0, (await DrainAsync(dataset, compaction)).GetValueOrDefault(CompactionTrigger.Seal));
        await AppendAsync(dataset, Noon.AddMinutes(91).AddSeconds(30), 1, appended);
        await AppendAsync(dataset, Noon.AddMinutes(305), 5, appended);
        Assert.Equal(1, (await DrainAsync(dataset, compaction)).GetValueOrDefault(CompactionTrigger.Seal));
        ObjectEntry folded = (await PeriodsLevelAsync(dataset))[0];
        Assert.Equal((Noon, Noon.AddHours(3)), HoursOf(folded));
        Assert.Equal(joined.Rows + 2, folded.Rows);
        Assert.Equal(0, (await CountsAsync(dataset))[PeriodLevels.Late]);
        await CheckAsync(dataset, appended, SliceWidth.Hour);

        // The joined object lies before a cutoff only once its last hour does.
        await dataset.ExpireAsync(Noon.AddHours(2).AddMinutes(30), Ct);
        Assert.Equal((Noon, Noon.AddHours(3)), HoursOf((await PeriodsLevelAsync(dataset))[0]));
        await dataset.ExpireAsync(Noon.AddHours(3).AddMinutes(30), Ct);
        Assert.True(HoursOf((await PeriodsLevelAsync(dataset))[0]).Start >= Noon.AddHours(3));
    }

    /// <summary>
    /// Hours on either side of the end of October, every one of them small: they join within each month, never across.
    /// An object cap a day of rows would pass keeps the calendar's hours, which a load this light would lengthen to a
    /// month from the first of November, and bounds what a join writes.
    /// </summary>
    [Fact]
    public async Task NoJoinSpansTwoMonths()
    {
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await VortexDataset.CreateAsync(
            store, Request.Schema, Options(SliceWidth.Hour, TimeSpan.FromMinutes(10)) with { MaxObjectBytes = 48 << 10 }, Ct);
        CompactionOptions compaction = new CompactionOptions { LevelZeroCeiling = 1, Fanout = 3 };
        DateTimeOffset start = new(2026, 10, 31, 20, 0, 0, TimeSpan.Zero);
        List<DateTimeOffset> appended = [];
        for (int quarter = 0; quarter < 36; quarter++)
        {
            await AppendAsync(dataset, start.AddMinutes(quarter * 15), 15, appended);
            await DrainAsync(dataset, compaction);
        }

        List<ObjectEntry> periods = await PeriodsLevelAsync(dataset);
        Assert.Contains(periods, entry => HoursOf(entry).End - HoursOf(entry).Start >= TimeSpan.FromHours(3));
        DateTimeOffset november = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
        Assert.All(periods, entry => Assert.True(
            HoursOf(entry).End <= november || HoursOf(entry).Start >= november, $"an object from {HoursOf(entry).Start:O} to {HoursOf(entry).End:O}"));
        await CheckAsync(dataset, appended, SliceWidth.Hour);
    }

    /// <summary>
    /// A schedule of minutes: appends in the order of their instants, one row in fifty late by up to twenty minutes, a
    /// delete now and then, each round compacted until nothing is due, periods of a minute sealed thirty seconds past
    /// their end. Small sealed minutes join in tiers, the late rows of joined minutes are folded into their whole
    /// objects, and after every round the levels keep their invariants and the rows read back are those appended and
    /// not deleted.
    /// </summary>
    [Theory]
    [InlineData(5)]
    [InlineData(17)]
    public async Task AScheduleOfMinutesJoinsAndKeepsTheInvariants(int seed)
    {
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await VortexDataset.CreateAsync(
            store, Request.Schema, Options(SliceWidth.Minute, TimeSpan.FromSeconds(30)), Ct);
        CompactionOptions compaction = new CompactionOptions { LevelZeroCeiling = 3, Fanout = 3 };
        Random random = new Random(seed);
        List<DateTimeOffset> appended = [];
        List<long> statuses = [];
        DateTimeOffset newest = Noon;
        Dictionary<CompactionTrigger, int> triggers = [];
        for (int round = 0; round < 120; round++)
        {
            List<DateTimeOffset> instants = [];
            for (int i = 0, rows = 5 + random.Next(20); i < rows; i++)
            {
                newest = newest.AddSeconds(1 + random.Next(4));
                instants.Add(random.Next(50) == 0 ? newest.AddSeconds(-random.Next(60, 1_200)) : newest);
            }

            await AppendAsync(dataset, instants, appended, statuses);
            if (random.Next(20) == 0)
            {
                long status = 200 + random.Next(20);
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
                Assert.True(++jobs < 100, $"round {round}: compaction does not settle, {result.Trigger} again");
                triggers[result.Trigger] = triggers.GetValueOrDefault(result.Trigger) + 1;
            }

            await CheckAsync(dataset, appended, SliceWidth.Minute);
        }

        List<ObjectEntry> periods = await PeriodsLevelAsync(dataset);
        Console.Out.Write(FormattableString.Invariant(
            $"MINUTES FUZZ seed {seed}: {appended.Count} rows, triggers {string.Join(", ", triggers.Select(t => $"{t.Key} {t.Value}"))}, {periods.Count} objects of the periods level, the widest {periods.Max(p => p.Rows)} rows.\n"));
        Assert.True(triggers.GetValueOrDefault(CompactionTrigger.Join) > 1, "the schedule must join its sealed minutes, and the joined ones again");
        Assert.True(triggers.GetValueOrDefault(CompactionTrigger.Seal) > 0, "the schedule must seal minutes");
        Assert.Equal(DatasetFeatures.JoinedPeriods, dataset.Snapshot.Header.Features & DatasetFeatures.JoinedPeriods);
    }

    /// <summary>
    /// The objects of the periods level, each with its tenant, its bytes and the hours it spans, in key order.
    /// </summary>
    private static async Task<List<(string Tenant, long Bytes, int Hours)>> SealedAsync(VortexDataset dataset)
    {
        List<(string, long, int)> objects = [];
        await foreach (TreeEntry leaf in dataset.Levels[PeriodLevels.Periods].EnumerateAsync(dataset.Pages, Ct))
        {
            ObjectEntry entry = ObjectEntry.FromBytes(leaf.Value.Span);
            Assert.True(entry.Summaries.TryGet(nameof(Tenanted.Tenant), out ColumnSummary tenant));
            Assert.Equal(tenant.Min, tenant.Max);
            (DateTimeOffset from, DateTimeOffset to) = HoursOf(entry);
            objects.Add((tenant.Min.Equals(FilterLiteral.From("small")) ? "small" : "large", entry.Bytes, (int)(to - from).TotalHours));
        }

        return objects;
    }

    /// <summary>Every object of the periods level spans whole hours of one month and one tenant, the level key-disjoint.</summary>
    private static async Task CheckJoinedAsync(VortexDataset dataset)
    {
        Assert.True(dataset.Levels.IsDisjoint(PeriodLevels.Periods));
        foreach (ObjectEntry entry in await PeriodsLevelAsync(dataset))
        {
            (DateTimeOffset from, DateTimeOffset to) = HoursOf(entry);
            Assert.Equal(new DateTimeOffset(from.Year, from.Month, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(to.AddTicks(-1).Year, to.AddTicks(-1).Month, 1, 0, 0, 0, TimeSpan.Zero));
        }
    }

    /// <summary>The objects of the periods level, in key order.</summary>
    private static async Task<List<ObjectEntry>> PeriodsLevelAsync(VortexDataset dataset)
    {
        List<ObjectEntry> entries = [];
        await foreach (TreeEntry leaf in dataset.Levels[PeriodLevels.Periods].EnumerateAsync(dataset.Pages, Ct))
        {
            entries.Add(ObjectEntry.FromBytes(leaf.Value.Span));
        }

        return entries;
    }

    /// <summary>The hours an object's instants lie in, from the start of the first to the end of the last.</summary>
    private static (DateTimeOffset Start, DateTimeOffset End) HoursOf(ObjectEntry entry)
    {
        Assert.True(entry.Summaries.TryGet(nameof(Request.At), out ColumnSummary at));
        long start = TimeSlicer.Floor(at.Min.SignedValue, TimeUnit.Microseconds, SliceWidth.Hour);
        long end = TimeSlicer.Next(TimeSlicer.Floor(at.Max.SignedValue, TimeUnit.Microseconds, SliceWidth.Hour), TimeUnit.Microseconds, SliceWidth.Hour);
        return (DateTimeOffset.UnixEpoch.AddTicks(start * 10), DateTimeOffset.UnixEpoch.AddTicks(end * 10));
    }

    [VortexRecord]
    public partial record struct Tenanted(string Tenant, DateTimeOffset At, long Bytes);
}
