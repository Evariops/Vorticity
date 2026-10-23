// Two partitions of unequal loads under the one calendar of their dataset: the length is weighed once a period is
// sealed in both, on its larger object, which every constraint bounds, and it settles once.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.Indexes;
using Vorticity.Tests.Writing;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed partial class PeriodLengthTests
{
    private static readonly DateTimeOffset Midnight = new DateTimeOffset(2026, 10, 18, 0, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Two partitions, one with a hundred times the other's rows, thirty hours sealed hour by hour, some sixty seals,
    /// under a cap on objects that lets the large one's day and the small one's month but not the large one's month:
    /// the length goes to days, once. Weighed on each partition's object alone, the seals asked for months and days
    /// in turn, a change each.
    /// </summary>
    [Fact]
    public async Task PartitionsOfUnequalLoadsSettleTheLengthOnce()
    {
        // The objects of an hour of each partition, measured on a first dataset with no cap.
        (long largest, long fewest, long small) = await HourObjectsAsync();
        long cap = Math.Max(24 * largest, 720 * small);
        Assert.True(cap < 720 * fewest, $"the cap of {cap} B lets the large partition's month of hours of {fewest} B");

        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await MeteredAsync(store, cap);
        List<CompactionTrigger> ran = await CompactedAsync(dataset);
        Assert.InRange(ran.Count(trigger => trigger == CompactionTrigger.Seal), 50, 60);
        PeriodChange change = Assert.Single(dataset.Snapshot.Header.PeriodChanges);
        Assert.Equal(SliceWidth.Day, change.Length);
    }

    /// <summary>
    /// The bytes of the large partition's largest and smallest object of an hour, and of the small partition's largest, over
    /// the hours sealed of a dataset whose objects have no cap.
    /// </summary>
    private static async Task<(long Largest, long Fewest, long Small)> HourObjectsAsync()
    {
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await MeteredAsync(store, new DatasetOptions().MaxObjectBytes);
        await CompactedAsync(dataset);
        (long largest, long fewest, long small) = (0, long.MaxValue, 0);
        await foreach (PositionedObject held in dataset.Snapshot.WalkAsync(null, 0, long.MaxValue, null, Ct))
        {
            if (held.Level != PeriodLevels.Periods)
            {
                continue;
            }

            if (held.Entry.Rows >= 1_000)
            {
                (largest, fewest) = (Math.Max(largest, held.Entry.Bytes), Math.Min(fewest, held.Entry.Bytes));
            }
            else
            {
                small = Math.Max(small, held.Entry.Bytes);
            }
        }

        Assert.True(largest > 0 && small > 0, "no hour sealed in one of the partitions");
        return (largest, fewest, small);
    }

    /// <summary>
    /// Thirty hours of readings from midnight, an append an hour, of a large partition of 2 000 rows an hour and a small
    /// one of 20, into a dataset of hours whose objects take at most <paramref name="cap"/> bytes.
    /// </summary>
    private static async Task<VortexDataset> MeteredAsync(Vorticity.IO.IObjectStore store, long cap)
    {
        VortexDataset dataset = await VortexDataset.CreateAsync(store, Metered.Schema, new DatasetOptions
        {
            ClusteringKey = [nameof(Metered.Tenant), nameof(Metered.At)],
            PartitionColumns = 1,
            Periods = new PeriodOptions { Lateness = TimeSpan.FromMinutes(5), Length = SliceWidth.Hour },
            MaxObjectBytes = cap,
        }, Ct);
        Random random = new Random(29);
        for (int hour = 0; hour < 30; hour++)
        {
            Metered[] rows =
            [
                .. Enumerable.Range(0, 2_000).Select(i => new Metered("large", Midnight.AddHours(hour).AddSeconds(i * 1.8), random.NextInt64(1L << 40))),
                .. Enumerable.Range(0, 20).Select(i => new Metered("small", Midnight.AddHours(hour).AddMinutes(i * 3), random.NextInt64(1L << 40))),
            ];
            await using ObjectDraft draft = dataset.StartObject();
            await draft.Writer.WriteAsync<Metered>(rows, Ct);
            await dataset.AppendAsync(draft, Ct);
        }

        return dataset;
    }

    /// <summary>Compacts the dataset until no job is due, and says the trigger of each job that ran.</summary>
    private static async Task<List<CompactionTrigger>> CompactedAsync(VortexDataset dataset)
    {
        List<CompactionTrigger> ran = [];
        CompactionOptions compaction = new CompactionOptions { LevelZeroCeiling = 1, JoinedPeriodBytes = 0 };
        while (await dataset.CompactAsync(compaction, Ct) is { } result)
        {
            ran.Add(result.Trigger);
        }

        return ran;
    }

    [VortexRecord]
    public partial record struct Metered(string Tenant, DateTimeOffset At, long Value);
}
