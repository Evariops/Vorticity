// Sorted slices: the objects a dataset of periods writes hold each slice's rows sorted by the object's
// synopsis columns, then by the instant, as their slice table says; and the dataset answers as before,
// in key order and by key.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.Expressions;
using Vorticity.Indexes;
using Vorticity.Keys;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed partial class SortedSliceTests
{
    private static readonly DateTimeOffset Noon = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// Two hours of spans, three regions and five statuses: the sealed hour's object is sorted by region,
    /// then status, then instant inside each of its slices, the host, near unique, left in that order;
    /// and a key-ordered read, a range and a key cursor give the rows appended.
    /// </summary>
    [Fact]
    public async Task ASealedPeriodIsSortedSliceBySlice()
    {
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Span.Schema, new DatasetOptions
        {
            ClusteringKey = [nameof(Span.At)],
            // Slices of a minute, 48 rows each.
            Periods = new PeriodOptions { Lateness = TimeSpan.FromMinutes(5), Length = SliceWidth.Hour, SliceTargetRows = 500 },
        }, Ct);
        Random random = new Random(9);
        List<Span> appended = [];
        for (int part = 0; part < 16; part++)
        {
            Span[] rows = [.. Enumerable.Range(0, 400).Select(i => new Span(
                Noon.AddSeconds((part * 400 + i) * 1.25),
                200 + (random.Next(5) * 100),
                $"region-{random.Next(3)}",
                $"host-{random.Next(100_000)}"))];
            await using ObjectDraft draft = dataset.StartObject();
            await draft.Writer.WriteAsync<Span>(rows, Ct);
            await dataset.AppendAsync(draft, Ct);
            appended.AddRange(rows);
        }

        CompactionOptions compaction = new CompactionOptions { LevelZeroCeiling = 2 };
        while (await dataset.CompactAsync(compaction, Ct) is not null)
        {
        }

        TreeEntry period = await FirstAsync(dataset, PeriodLevels.Periods);
        ObjectEntry entry = ObjectEntry.FromBytes(period.Value.Span);
        await using (ObjectLease lease = await dataset.RentAsync(entry, Ct))
        {
            SliceTable table = (await SliceTable.ReadAsync(lease.File, Ct))!;
            Assert.Equal([lease.File.DType.IndexOfField(nameof(Span.Region)), lease.File.DType.IndexOfField(nameof(Span.Status))], table.Order);

            List<Span> stored = [];
            await foreach (Span row in lease.File.Scan<Span>().ToRecordsAsync(Ct))
            {
                stored.Add(row);
            }

            Assert.True(table.Slices.Count > 1, "the hour is cut in several slices");
            foreach (TimeSlice slice in table.Slices)
            {
                List<Span> rows = stored.GetRange((int)slice.FirstRow, (int)slice.Rows);
                Assert.Equal(rows.OrderBy(r => r.Region, StringComparer.Ordinal).ThenBy(r => r.Status).ThenBy(r => r.At), rows);
                Assert.All(rows, r => Assert.InRange(r.At.ToUnixTimeMilliseconds() * 1000, slice.Start, slice.End - 1));
            }
        }

        // The dataset answers as before: in key order, by range, and through its key cursor.
        Assert.Equal(appended.Select(r => r.At).Order(), await dataset.Scan<Span>().OrderBy(r => r.At).Select(r => r.At).ToListAsync(Ct));
        DateTimeOffset from = Noon.AddMinutes(20);
        DateTimeOffset to = Noon.AddMinutes(70);
        Assert.Equal(
            appended.Count(r => r.At >= from && r.At < to),
            (await dataset.Scan<Span>().Where(r => r.At >= from & r.At < to).Select(r => r.At).ToListAsync(Ct)).Count);
        List<long> walked = [];
        await using (DatasetKeyCursor cursor = await DatasetKeyCursor.OpenAsync(dataset, Ct))
        {
            for (bool on = await cursor.SeekFirstAsync(Ct); on; on = await cursor.NextAsync(Ct))
            {
                walked.Add(cursor.Key.SignedValue);
            }
        }

        Assert.Equal(appended.Select(r => r.At.ToUnixTimeMilliseconds() * 1000).Order(), walked);
    }

    /// <summary>
    /// The same rows in a dataset of periods, whose objects above level 0 are sorted slice by slice and
    /// walked by their slice tables, and in a dataset of levels: every read in the key's order answers
    /// alike, forward and backward, filtered or not, through the key cursor's seeks and ranks and
    /// through a group by that streams on the instant, before and after a delete marks rows of the
    /// sorted objects.
    /// </summary>
    [Fact]
    public async Task EveryReadInKeyOrderAnswersAsADatasetOfLevels()
    {
        await using MemoryObjectStore periodsStore = new MemoryObjectStore();
        await using MemoryObjectStore levelsStore = new MemoryObjectStore();
        // Blocks of 16 rows, so that a read in key order takes a slice of a minute, 48 rows, in three
        // windows; deleted rows marked rather than rewritten out.
        VortexWriteOptions write = new VortexWriteOptions { BlockRows = 16 };
        await using VortexDataset periods = await VortexDataset.CreateAsync(periodsStore, Span.Schema, new DatasetOptions
        {
            ClusteringKey = [nameof(Span.At)],
            Periods = new PeriodOptions { Lateness = TimeSpan.FromMinutes(5), Length = SliceWidth.Hour, SliceTargetRows = 500 },
            MarkedObjectBytes = 0,
            MarkedVectorBytes = 1 << 20,
            Write = write,
        }, Ct);
        await using VortexDataset levels = await VortexDataset.CreateAsync(levelsStore, Span.Schema, new DatasetOptions
        {
            ClusteringKey = [nameof(Span.At)],
            MarkedObjectBytes = 0,
            MarkedVectorBytes = 1 << 20,
            Write = write,
        }, Ct);

        // Two hours and a quarter of spans, each at an instant of its own, and forty rows of the first
        // hour that come late, once it is sealed.
        Random random = new Random(21);
        List<Span[]> parts = [];
        for (int part = 0; part < 16; part++)
        {
            parts.Add([.. Enumerable.Range(0, 400).Select(i => new Span(
                Noon.AddSeconds((part * 400 + i) * 1.25),
                200 + (random.Next(5) * 100),
                $"region-{random.Next(3)}",
                $"host-{random.Next(100_000)}"))]);
        }

        parts.Insert(12, [.. Enumerable.Range(0, 40).Select(i => new Span(
            Noon.AddSeconds((i * 80) + 0.1), 404, "region-0", $"late-{i}"))]);
        CompactionOptions compaction = new CompactionOptions { LevelZeroCeiling = 2 };
        foreach (Span[] rows in parts)
        {
            foreach (VortexDataset dataset in new[] { periods, levels })
            {
                await using ObjectDraft draft = dataset.StartObject();
                await draft.Writer.WriteAsync<Span>(rows, Ct);
                await dataset.AppendAsync(draft, Ct);
                while (await dataset.CompactAsync(compaction, Ct) is not null)
                {
                }
            }
        }

        // The sealed hour carries no run on its key: its slice table walks it.
        await using (ObjectLease lease = await periods.RentAsync(ObjectEntry.FromBytes((await FirstAsync(periods, PeriodLevels.Periods)).Value.Span), Ct))
        {
            Assert.Equal(KeySourceKind.Slices, (await lease.File.Keys(nameof(Span.At)).ExplainAsync(Ct)).Source);
        }

        await SameAsync(periods, levels);
        foreach (VortexDataset dataset in new[] { periods, levels })
        {
            await dataset.DeleteAsync<Span>(r => r.Status == 300, Ct);
        }

        await SameAsync(periods, levels);
    }

    /// <summary>
    /// The .NET half of Rust reading the objects a dataset of periods sorts: under each profile, every
    /// object above level 0 written out as it is stored, beside its rows in time order written plainly,
    /// for `cargo run --example verify_sliced` to compare (`bench/crosscheck.sh`). It skips rather than
    /// passes without its directory, so a run that forgets it does not look like a green cross-check.
    /// </summary>
    [Fact]
    public async Task WritesSortedSlicesOutForTheRustCrossCheck()
    {
        string? root = Environment.GetEnvironmentVariable("VORTICITY_WRITE_SLICES");
        Assert.SkipWhen(string.IsNullOrEmpty(root), "Set VORTICITY_WRITE_SLICES to a directory to produce objects for the Rust cross-check.");
        System.IO.Directory.CreateDirectory(root!);
        int written = 0;
        foreach (CompressionProfile profile in new[] { CompressionProfile.Auto, CompressionProfile.Smallest })
        {
            await using MemoryObjectStore store = new MemoryObjectStore();
            await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Span.Schema, new DatasetOptions
            {
                ClusteringKey = [nameof(Span.At)],
                Periods = new PeriodOptions { Lateness = TimeSpan.FromMinutes(5), Length = SliceWidth.Hour, SliceTargetRows = 500 },
                Write = new VortexWriteOptions { Compression = profile },
            }, Ct);
            Random random = new Random(5);
            for (int part = 0; part < 16; part++)
            {
                Span[] rows = [.. Enumerable.Range(0, 400).Select(i => new Span(
                    Noon.AddSeconds((part * 400 + i) * 1.25),
                    200 + (random.Next(5) * 100),
                    $"region-{random.Next(3)}",
                    $"host-{random.Next(100_000)}"))];
                await using ObjectDraft draft = dataset.StartObject();
                await draft.Writer.WriteAsync<Span>(rows, Ct);
                await dataset.AppendAsync(draft, Ct);
                while (await dataset.CompactAsync(new CompactionOptions { LevelZeroCeiling = 2 }, Ct) is not null)
                {
                }
            }

            for (int level = 1; level < dataset.Levels.Count; level++)
            {
                await foreach (TreeEntry leaf in dataset.Levels[level].EnumerateAsync(dataset.Pages, Ct))
                {
                    ObjectEntry entry = ObjectEntry.FromBytes(leaf.Value.Span);
                    string name = System.IO.Path.Combine(root!, $"{profile}-{written++}");
                    byte[] bytes = new byte[entry.Bytes];
                    await store.ReadAsync(entry.Key, 0, bytes, null, Ct);
                    await System.IO.File.WriteAllBytesAsync(name + ".sorted.vortex", bytes, Ct);

                    List<Span> stored = [];
                    await using (ObjectLease lease = await dataset.RentAsync(entry, Ct))
                    {
                        Assert.NotNull(await SliceTable.ReadAsync(lease.File, Ct));
                        await foreach (Span row in lease.File.Scan<Span>().ToRecordsAsync(Ct))
                        {
                            stored.Add(row);
                        }
                    }

                    await using VortexFileWriter twin = VortexSession.Default.CreateWriter<Span>(name + ".time.vortex");
                    await twin.WriteAsync<Span>([.. stored.OrderBy(r => r.At).ThenBy(r => r.Host, StringComparer.Ordinal)], Ct);
                    await twin.CompleteAsync(Ct);
                }
            }
        }

        Assert.True(written >= 4, $"{written} objects in sorted slices");
    }

    /// <summary>What every read in the key's order gives, alike on both datasets.</summary>
    private static async Task SameAsync(VortexDataset periods, VortexDataset levels)
    {
        foreach (bool descending in new[] { false, true })
        {
            Assert.Equal(await OrderedAsync(levels, descending, filtered: false), await OrderedAsync(periods, descending, filtered: false));
            Assert.Equal(await OrderedAsync(levels, descending, filtered: true), await OrderedAsync(periods, descending, filtered: true));
        }

        await using DatasetKeyCursor byLevels = await DatasetKeyCursor.OpenAsync(levels, Ct);
        await using DatasetKeyCursor byPeriods = await DatasetKeyCursor.OpenAsync(periods, Ct);
        List<long> walked = [];
        for (bool on = await byPeriods.SeekFirstAsync(Ct); on; on = await byPeriods.NextAsync(Ct))
        {
            walked.Add(byPeriods.Key.SignedValue);
        }

        List<long> expected = [];
        for (bool on = await byLevels.SeekFirstAsync(Ct); on; on = await byLevels.NextAsync(Ct))
        {
            expected.Add(byLevels.Key.SignedValue);
        }

        Assert.Equal(expected, walked);
        List<long> backward = [];
        for (bool on = await byPeriods.SeekLastAsync(Ct); on; on = await byPeriods.PreviousAsync(Ct))
        {
            backward.Add(byPeriods.Key.SignedValue);
        }

        backward.Reverse();
        Assert.Equal(expected, backward);

        for (int i = 0; i < expected.Count; i += 37)
        {
            foreach (long probe in new[] { expected[i] - 1, expected[i], expected[i] + 1 })
            {
                FilterLiteral key = FilterLiteral.From(probe);
                foreach (SeekMode mode in new[] { SeekMode.AtOrAfter, SeekMode.After, SeekMode.AtOrBefore, SeekMode.Before, SeekMode.Exact })
                {
                    bool found = await byPeriods.SeekAsync(key, mode, Ct);
                    Assert.Equal(await byLevels.SeekAsync(key, mode, Ct), found);
                    if (found)
                    {
                        Assert.Equal(byLevels.Key, byPeriods.Key);
                    }
                }

                Assert.Equal(await byLevels.RankAsync(key, Ct), await byPeriods.RankAsync(key, Ct));
            }
        }

        for (int rank = 0; rank < expected.Count; rank += 97)
        {
            Assert.True(await byPeriods.SeekRankAsync(rank, Ct));
            Assert.Equal(expected[rank], byPeriods.Key.SignedValue);
        }

        // A group by on the instant's minute streams over the sorted objects and gives the same groups.
        Vorticity.Aggregation byMinute = periods.Scan<Span>().GroupBy(r => r.At.Truncate(CalendarUnit.Minute)).Select(g => (g.Key, g.Count(), g.Min(r => r.Status)));
        Assert.True(Vorticity.Aggregating.StreamingGroupBatches.Streaming((Vorticity.Aggregating.AggregationQuery)byMinute.Query) >= 0);
        Assert.Equal(
            await levels.Scan<Span>().GroupBy(r => r.At.Truncate(CalendarUnit.Minute)).Select(g => (g.Key, g.Count(), g.Min(r => r.Status))).As<MinuteCount>().ToRecordsAsync(Ct).ToListAsync(Ct),
            await byMinute.As<MinuteCount>().ToRecordsAsync(Ct).ToListAsync(Ct));
    }

    private static async Task<List<Span>> OrderedAsync(VortexDataset dataset, bool descending, bool filtered)
    {
        Scan<Span> scan = filtered ? dataset.Scan<Span>().Where(r => r.Region == "region-1") : dataset.Scan<Span>();
        return await (descending ? scan.OrderByDescending(r => r.At) : scan.OrderBy(r => r.At)).ToRecordsAsync(Ct).ToListAsync(Ct);
    }

    private static async Task<TreeEntry> FirstAsync(VortexDataset dataset, int level)
    {
        await foreach (TreeEntry entry in dataset.Levels[level].EnumerateAsync(dataset.Pages, Ct))
        {
            return entry;
        }

        throw new InvalidOperationException($"Level {level} holds no object.");
    }

    [VortexRecord]
    public partial record struct Span(DateTimeOffset At, int Status, string Region, string Host);

    [VortexRecord]
    public partial record struct MinuteCount(DateTimeOffset Minute, long Rows, int Status);
}
