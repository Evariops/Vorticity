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

        await AppendAsync(periods, levels);

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

    /// <summary>
    /// Each object a job writes carries the path directory of its attributes, and a question on a key an
    /// object lacks, on a class its key never holds, or outside its bounds, refutes it before its data:
    /// the key "deploy" is held by a hundred rows of the second hour alone. The answers are the
    /// dataset of levels' own, whose objects carry no directory.
    /// </summary>
    [Fact]
    public async Task AQuestionOnAKeyAnObjectLacksRefutesItBeforeItsData()
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
        await AppendAsync(periods, levels);
        foreach (VortexDataset dataset in new[] { periods, levels })
        {
            while (await dataset.CompactAsync(new CompactionOptions { LevelZeroCeiling = 2 }, Ct) is not null)
            {
            }
        }

        Assert.Equal(await levels.Scan<Event>().Where(r => r.Attributes.Has("deploy")).CountAsync(Ct), await periods.Scan<Event>().Where(r => r.Attributes.Has("deploy")).CountAsync(Ct));
        Assert.Equal(100, await periods.Scan<Event>().Where(r => r.Attributes.Has("deploy")).CountAsync(Ct));
        Assert.Equal(0, await periods.Scan<Event>().Where(r => r.Attributes["status"].AsInt64() > 10_000).CountAsync(Ct));
        Assert.Equal(
            await levels.Scan<Event>().Where(r => r.Attributes["status"].AsString() == "502").CountAsync(Ct),
            await periods.Scan<Event>().Where(r => r.Attributes["status"].AsString() == "502").CountAsync(Ct));
        Assert.Equal(
            await levels.Scan<Event>().Where(r => r.Attributes["deploy"].AsString() == "v1").CountAsync(Ct),
            await periods.Scan<Event>().Where(r => r.Attributes["deploy"].AsString() == "v1").CountAsync(Ct));

        // Two of the three objects lack the key: each read its directory, one region, and none of its blocks.
        ScanPlan plan = await periods.Scan<Event>().Where(r => r.Attributes.Has("deploy")).ExplainAsync(Ct);
        PruningStep step = Assert.Single(plan.Pruning, s => s.Structure == "path directory");
        Assert.Equal(3, step.SegmentsRead);
        Assert.InRange(step.BlocksPruned, 1, plan.Blocks - 1);
        Assert.Equal(plan.Blocks - step.BlocksPruned, plan.LiveBlocks);

        // A bound no object reaches refutes them all: what is read is their directories alone.
        ScanPlan none = await periods.Scan<Event>().Where(r => r.Attributes["status"].AsInt64() > 10_000).ExplainAsync(Ct);
        PruningStep refuted = Assert.Single(none.Pruning, s => s.Structure == "path directory");
        Assert.Equal(none.Blocks, refuted.BlocksPruned);
        Assert.Equal((refuted.SegmentsRead, refuted.BytesRead), (none.Segments, none.BytesToRead));
        Assert.False(none.MayMatch);
    }

    /// <summary>
    /// Three sources of attributes, a route and a status, a route and keys of data, a query alone,
    /// beside a host among forty: the objects a job writes order each slice by the attributes' shape
    /// before the host, since the shapes are fewer, so the rows of each shape lie together in every
    /// slice; and the rows are the ones appended.
    /// </summary>
    [Fact]
    public async Task AnObjectOrdersItsSlicesByShape()
    {
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Hosted.Schema, new DatasetOptions
        {
            ClusteringKey = [nameof(Hosted.At)],
            Periods = new PeriodOptions { Lateness = TimeSpan.FromMinutes(5), Length = SliceWidth.Hour, SliceTargetRows = 500 },
        }, Ct);
        Random random = new Random(23);
        List<Hosted> appended = [];
        for (int part = 0; part < 16; part++)
        {
            Hosted[] rows = [.. Enumerable.Range(part * 400, 400).Select(i => new Hosted(
                Noon.AddSeconds(i * 1.25),
                $"host-{random.Next(40)}",
                (i % 3) switch
                {
                    0 => Variant.Object(("route", Variant.From($"/r/{i % 9}")), ("status", Variant.From(200 + (i % 4)))),
                    1 => Variant.Object(("route", Variant.From($"/r/{i % 9}")), ($"user.{random.Next(1_000)}.score", Variant.From(i))),
                    _ => Variant.Object(("query", Variant.From($"q{i % 5}"))),
                }))];
            await using ObjectDraft draft = dataset.StartObject();
            await draft.Writer.WriteAsync<Hosted>(rows, Ct);
            await dataset.AppendAsync(draft, Ct);
            appended.AddRange(rows);
        }

        while (await dataset.CompactAsync(new CompactionOptions { LevelZeroCeiling = 2 }, Ct) is not null)
        {
        }

        int sealedObjects = 0;
        await foreach (TreeEntry leaf in dataset.Levels[PeriodLevels.Periods].EnumerateAsync(dataset.Pages, Ct))
        {
            await using ObjectLease lease = await dataset.RentAsync(ObjectEntry.FromBytes(leaf.Value.Span), Ct);
            int attributes = lease.File.DType.IndexOfField(nameof(Hosted.Attributes));
            SliceTable table = (await SliceTable.ReadAsync(lease.File, Ct))!;
            Assert.Equal([-attributes - 1, lease.File.DType.IndexOfField(nameof(Hosted.Host))], table.Order);

            // In every slice, the rows of one shape lie together: a route, or none.
            List<Hosted> stored = await lease.File.Scan<Hosted>().ToRecordsAsync(Ct).ToListAsync(Ct);
            foreach (TimeSlice slice in table.Slices)
            {
                bool[] routes = [.. stored.GetRange((int)slice.FirstRow, (int)slice.Rows).Select(r => r.Attributes.Has("route"))];
                Assert.Equal(1, routes.Zip(routes.Skip(1)).Count(pair => pair.First != pair.Second));
            }

            // The host's rows lie apart, on either side of the shapes, and its run statistics count them alike.
            Assert.True(await SynopsisTableTests.CoarseTablesAreTheMergeOfTheirRunsAsync(lease.File, table) > 0);
            sealedObjects++;
        }

        Assert.Equal(2, sealedObjects);
        Assert.Equal(
            appended.Select(r => (r.At, r.Host)).Order(),
            (await dataset.Scan<Hosted>().ToRecordsAsync(Ct).ToListAsync(Ct)).Select(r => (r.At, r.Host)).Order());
    }

    /// <summary>
    /// The .NET half of Rust reading the objects a job shreds: each object above level 0 of a dataset of
    /// periods written out as it is stored, beside its rows in the same order written whole, for
    /// `cargo run --example verify_shredded` to compare (`bench/crosscheck.sh`). It skips rather than
    /// passes without its directory.
    /// </summary>
    [Fact]
    public async Task WritesJobObjectsOutForTheRustCrossCheck()
    {
        string? root = Environment.GetEnvironmentVariable("VORTICITY_WRITE_SHREDDED");
        Assert.SkipWhen(string.IsNullOrEmpty(root), "Set VORTICITY_WRITE_SHREDDED to a directory to produce shredded objects for the Rust cross-check.");
        System.IO.Directory.CreateDirectory(root!);
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Event.Schema, new DatasetOptions
        {
            ClusteringKey = [nameof(Event.At)],
            Periods = new PeriodOptions { Lateness = TimeSpan.FromMinutes(5), Length = SliceWidth.Hour, SliceTargetRows = 500 },
        }, Ct);
        await AppendAsync(dataset);
        while (await dataset.CompactAsync(new CompactionOptions { LevelZeroCeiling = 2 }, Ct) is not null)
        {
        }

        int written = 0;
        foreach ((int level, DatasetTree tree) in dataset.Levels.Occupied())
        {
            if (level == 0)
            {
                continue;
            }

            await foreach (TreeEntry leaf in tree.EnumerateAsync(dataset.Pages, Ct))
            {
                ObjectEntry entry = ObjectEntry.FromBytes(leaf.Value.Span);
                string name = System.IO.Path.Combine(root!, $"job-{written++}");
                byte[] bytes = new byte[entry.Bytes];
                await store.ReadAsync(entry.Key, 0, bytes, null, Ct);
                await System.IO.File.WriteAllBytesAsync(name + ".shredded.vortex", bytes, Ct);

                List<Event> stored = [];
                await using (ObjectLease lease = await dataset.RentAsync(entry, Ct))
                {
                    Assert.NotNull(await VariantPathMap.ReadAsync(lease.File, lease.File.DType.IndexOfField(nameof(Event.Attributes)), Dense, Ct));
                    await foreach (Event row in lease.File.Scan<Event>().ToRecordsAsync(Ct))
                    {
                        stored.Add(row);
                    }
                }

                await using VortexFileWriter twin = VortexSession.Default.CreateWriter<Event>(name + ".whole.vortex");
                await twin.WriteAsync<Event>([.. stored], Ct);
                await twin.CompleteAsync(Ct);
            }
        }

        Assert.True(written >= 3, $"{written} shredded objects");
    }

    /// <summary>Sixteen appends of 400 events, 1.25 seconds apart from noon, the same into each dataset.</summary>
    private static async Task AppendAsync(params VortexDataset[] datasets)
    {
        Random random = new Random(19);
        for (int part = 0; part < 16; part++)
        {
            Event[] rows = [.. Enumerable.Range(0, 400).Select(i => Row(random, part * 400 + i))];
            foreach (VortexDataset dataset in datasets)
            {
                await using ObjectDraft draft = dataset.StartObject();
                await draft.Writer.WriteAsync<Event>(rows, Ct);
                await dataset.AppendAsync(draft, Ct);
            }
        }
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
            .. (IEnumerable<(string, Variant)>)(i is >= 3_000 and < 3_100 ? [("deploy", Variant.From($"v{i % 3}"))] : []),
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

    [VortexRecord]
    public partial record struct Hosted(DateTimeOffset At, string Host, Variant Attributes);
}
