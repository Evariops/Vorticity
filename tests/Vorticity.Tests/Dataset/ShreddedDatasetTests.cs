// Shredding in a dataset's jobs: the objects a dataset of periods writes above level 0 shred their
// variant columns as their own rows pay for, while appends shred nothing; and every question over a key
// answers as over the same rows in a dataset of levels, whose objects shred nothing.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.Indexes;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed partial class ShreddedDatasetTests
{
    private static readonly DateTimeOffset Noon = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);

    private static readonly string[] Dense = ["ok", "route", "score", "status"];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// Two hours and more of events whose attributes hold a route, a status that is now and then text,
    /// a score and a flag: the appended objects carry no path map, every object the jobs write above
    /// level 0 carries one over the four keys, and each read of a key, its presence and a group by
    /// over keys answer as over the dataset of levels.
    /// </summary>
    [Fact]
    public async Task JobsShredWhatAppendsLeaveWhole()
    {
        await using MemoryObjectStore periodsStore = new MemoryObjectStore();
        await using MemoryObjectStore levelsStore = new MemoryObjectStore();
        await using VortexDataset periods = await VortexDataset.CreateAsync(periodsStore, Event.Schema, new DatasetOptions
        {
            ClusteringKey = [nameof(Event.At)],
            Periods = new PeriodOptions { Lateness = TimeSpan.FromMinutes(5), Length = SliceWidth.Hour, SliceTargetRows = 500 },
        }, Ct);
        await using VortexDataset levels = await VortexDataset.CreateAsync(levelsStore, Event.Schema, new DatasetOptions
        {
            ClusteringKey = [nameof(Event.At)],
        }, Ct);

        Random random = new Random(19);
        for (int part = 0; part < 16; part++)
        {
            Event[] rows = [.. Enumerable.Range(0, 400).Select(i => Row(random, part * 400 + i))];
            foreach (VortexDataset dataset in new[] { periods, levels })
            {
                await using ObjectDraft draft = dataset.StartObject();
                await draft.Writer.WriteAsync<Event>(rows, Ct);
                await dataset.AppendAsync(draft, Ct);
            }
        }

        // An append writes the rows as they come: no path map.
        Assert.All(await MapsAsync(periods), map => Assert.Equal((0, false), (map.Level, map.Keys is not null)));

        CompactionOptions compaction = new CompactionOptions { LevelZeroCeiling = 2 };
        foreach (VortexDataset dataset in new[] { periods, levels })
        {
            while (await dataset.CompactAsync(compaction, Ct) is not null)
            {
            }
        }

        // Every object a job wrote in sorted slices shreds what its own rows pay for. A sealed hour,
        // 2 880 rows, shreds the four keys its rows hold throughout. The fresh object of the last 640
        // rows leaves the flag in the residual: a boolean saves 0.13 bytes a row as a typed column,
        // 83 bytes there, short of the 202 its path costs in a chunk.
        List<(int Level, string[]? Keys)> shredded = await MapsAsync(periods);
        Assert.Equal(2, shredded.Count(map => map.Level == PeriodLevels.Periods));
        Assert.All(shredded.Where(map => map.Level == PeriodLevels.Periods), map => Assert.Equal(Dense, map.Keys));
        Assert.All(shredded.Where(map => PeriodLevels.IsFresh(map.Level)), map => Assert.Equal(["route", "score", "status"], map.Keys!));
        Assert.All(await MapsAsync(levels), map => Assert.Null(map.Keys));

        // Each question over a key answers alike, whatever the order the objects give the rows in.
        Assert.Equal(
            Sorted(await levels.Scan<Event>().Select(r => r.Attributes["status"].AsInt64()).ToListAsync(Ct)),
            Sorted(await periods.Scan<Event>().Select(r => r.Attributes["status"].AsInt64()).ToListAsync(Ct)));
        Assert.Equal(
            Sorted(await levels.Scan<Event>().Select(r => r.Attributes["status"].AsString()).ToListAsync(Ct)),
            Sorted(await periods.Scan<Event>().Select(r => r.Attributes["status"].AsString()).ToListAsync(Ct)));
        Assert.Equal(
            Sorted(await levels.Scan<Event>().Select(r => r.Attributes["score"].ToDouble()).ToListAsync(Ct)),
            Sorted(await periods.Scan<Event>().Select(r => r.Attributes["score"].ToDouble()).ToListAsync(Ct)));
        Assert.Equal(
            await levels.Scan<Event>().Where(r => r.Attributes.Has("rare")).CountAsync(Ct),
            await periods.Scan<Event>().Where(r => r.Attributes.Has("rare")).CountAsync(Ct));
        Assert.Equal(
            await RoutesAsync(levels),
            await RoutesAsync(periods));
    }

    private static Event Row(Random random, int i) => new Event(
        Noon.AddSeconds(i * 1.25),
        i % 991 == 0 ? Variant.From("no object") : Variant.Object(
        [
            ("ok", Variant.From(i % 3 == 0)),
            ("route", Variant.From($"/r/{random.Next(12)}")),
            ("score", Variant.From(random.NextDouble())),
            ("status", i % 97 == 0 ? Variant.From("502") : Variant.From(200 + random.Next(4))),
            .. (IEnumerable<(string, Variant)>)(i % 500 == 0 ? [("rare", Variant.From(i))] : []),
        ]));

    /// <summary>Each object's level, and the keys of its attributes' path map, null for none.</summary>
    private static async Task<List<(int Level, string[]? Keys)>> MapsAsync(VortexDataset dataset)
    {
        List<(int, string[]?)> maps = [];
        foreach ((int level, DatasetTree tree) in dataset.Levels.Occupied())
        {
            await foreach (TreeEntry entry in tree.EnumerateAsync(dataset.Pages, Ct))
            {
                await using ObjectLease lease = await dataset.RentAsync(ObjectEntry.FromBytes(entry.Value.Span), Ct);
                int field = lease.File.DType.IndexOfField(nameof(Event.Attributes));
                VariantPathMap? map = await VariantPathMap.ReadAsync(lease.File, field, Dense, Ct);
                maps.Add((level, map is null ? null : [.. map.Keys.Order(StringComparer.Ordinal)]));
            }
        }

        return maps;
    }

    private static async Task<List<RouteCount>> RoutesAsync(VortexDataset dataset) =>
        [.. (await dataset.Scan<Event>()
            .GroupBy(r => r.Attributes["route"].AsString())
            .Select(g => (g.Key, g.Count(), g.Sum(x => x.Attributes["status"].AsInt64())))
            .As<RouteCount>().ToRecordsAsync(Ct).ToListAsync(Ct))
            .OrderBy(g => g.Route ?? "~", StringComparer.Ordinal)];

    private static List<T> Sorted<T>(List<T> values) => [.. values.Order()];

    [VortexRecord]
    public partial record struct Event(DateTimeOffset At, Variant Attributes);

    [VortexRecord]
    public partial record struct RouteCount(string? Route, long Rows, long Statuses);
}
