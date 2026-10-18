// The length of a period: the cheapest calendar unit that holds the store's cap, the footer's chunk,
// the purge's delay and the retention's share; the calendar that records its changes, each from a
// boundary of both units; and a seal that weighs its period and records the length its load calls for.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed partial class PeriodLengthTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The design's example: 5 × 10⁴ rows a second, 40 bytes a row over 20 columns, 8 in the widest.</summary>
    private static readonly PeriodLoad Example = new PeriodLoad(2e6, 5e4, 20, 8, long.MaxValue, null, null);

    [Fact]
    public void TheExampleLandsOnADay()
    {
        // A month would take chunks of 5 × 10⁶ rows to keep its footer under the cap, 41 MB of the
        // widest column; a day takes 1.7 × 10⁵ rows, 1.4 MB, and touches fewer objects than an hour.
        Assert.False(PeriodLengths.Holds(Example, SliceWidth.Month));
        Assert.True(PeriodLengths.Holds(Example, SliceWidth.Day));
        Assert.Equal(SliceWidth.Day, PeriodLengths.Choose(Example));

        // A hundredth of the volume lands on a month.
        Assert.Equal(SliceWidth.Month, PeriodLengths.Choose(Example with { BytesPerSecond = 2e4, RowsPerSecond = 500 }));
    }

    [Fact]
    public void EachConstraintShortensThePeriod()
    {
        // The store takes 100 GiB, below a day's 173 GB.
        Assert.Equal(SliceWidth.Hour, PeriodLengths.Choose(Example with { MaxObjectBytes = 100L << 30 }));

        // A purge of a day, 1 728 s at the rewrite rate, past half an erasure delay of half an hour.
        Assert.Equal(SliceWidth.Hour, PeriodLengths.Choose(Example with { ErasureDelay = TimeSpan.FromMinutes(30) }));
        Assert.Equal(SliceWidth.Day, PeriodLengths.Choose(Example with { ErasureDelay = TimeSpan.FromDays(30) }));

        // A week's retention takes periods of a tenth of it at most.
        Assert.Equal(SliceWidth.Hour, PeriodLengths.Choose(Example with { Retention = TimeSpan.FromDays(7) }));

        // Nothing holds: the finest unit.
        Assert.Equal(SliceWidth.Minute, PeriodLengths.Choose(Example with { MaxObjectBytes = 1 }));
    }

    [Fact]
    public void ACalendarCutsEachSideOfAChangeByItsOwnUnit()
    {
        long Micros(DateTimeOffset at) => at.ToUnixTimeMilliseconds() * 1000;
        DateTimeOffset noon = new DateTimeOffset(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);
        PeriodCalendar hours = new PeriodCalendar(SliceWidth.Hour, [], TimeUnit.Microseconds);

        // From hours to days past noon: from the next midnight, a boundary of both.
        PeriodChange toDays = hours.ChangeTo(SliceWidth.Day, Micros(noon.AddMinutes(30)))!.Value;
        Assert.Equal(Micros(new DateTimeOffset(2026, 10, 19, 0, 0, 0, TimeSpan.Zero)), toDays.From);
        Assert.Null(hours.ChangeTo(SliceWidth.Hour, Micros(noon)));

        PeriodCalendar changed = new PeriodCalendar(SliceWidth.Hour, [toDays], TimeUnit.Microseconds);
        Assert.Equal(Micros(noon), changed.Floor(Micros(noon.AddMinutes(30))));
        Assert.Equal(Micros(noon.AddHours(1)), changed.Next(Micros(noon)));
        Assert.Equal(toDays.From, changed.Next(Micros(noon.AddHours(11))));
        Assert.Equal(toDays.From, changed.Floor(Micros(noon.AddHours(20))));
        Assert.Equal(toDays.From + Micros(DateTimeOffset.UnixEpoch.AddDays(1)), changed.Next(toDays.From));

        // From days back to hours: from the next midnight too, the end of the day under way.
        Assert.Equal(Micros(new DateTimeOffset(2026, 10, 20, 0, 0, 0, TimeSpan.Zero)),
            changed.ChangeTo(SliceWidth.Hour, Micros(noon.AddHours(14)))!.Value.From);
    }

    /// <summary>
    /// A seal weighs the period it wrote and records, in the same commit, the length its load calls for:
    /// a light dataset of hours goes to months from the next month. A change worked out on a calendar
    /// another writer changed first is dropped.
    /// </summary>
    [Fact]
    public async Task ASealRecordsTheLengthItsPeriodCallsFor()
    {
        DateTimeOffset noon = new DateTimeOffset(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Reading.Schema, new DatasetOptions
        {
            ClusteringKey = [nameof(Reading.At)],
            Periods = new PeriodOptions { Lateness = TimeSpan.FromMinutes(5), Length = SliceWidth.Hour },
        }, Ct);
        for (int quarter = 0; quarter < 6; quarter++)
        {
            await using ObjectDraft draft = dataset.StartObject();
            await draft.Writer.WriteAsync<Reading>([.. Enumerable.Range(0, 15).Select(i => new Reading(noon.AddMinutes((quarter * 15) + i), i))], Ct);
            await dataset.AppendAsync(draft, Ct);
        }

        CompactionOptions compaction = new CompactionOptions { LevelZeroCeiling = 1 };
        List<CompactionTrigger> ran = [];
        while (await dataset.CompactAsync(compaction, Ct) is { } result)
        {
            ran.Add(result.Trigger);
        }

        Assert.Contains(CompactionTrigger.Seal, ran);
        PeriodChange change = Assert.Single(dataset.Snapshot.Header.PeriodChanges);
        Assert.Equal((SliceWidth.Month, new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds() * 1000), (change.Length, change.From));

        await using VortexDataset reopened = await VortexDataset.OpenAsync(store, new DatasetOptions(), Ct);
        Assert.Equal(change, Assert.Single(reopened.Snapshot.Header.PeriodChanges));

        CommitResult stale = await dataset.CommitAsync([new DatasetOperation.ChangePeriodLength(change with { Length = SliceWidth.Day }, 0)], Ct);
        Assert.Equal(OperationOutcome.Dropped, Assert.Single(stale.Outcomes));
    }

    [VortexRecord]
    public partial record struct Reading(DateTimeOffset At, long Value);
}
