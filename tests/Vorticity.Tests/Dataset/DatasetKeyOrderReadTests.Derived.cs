// Reads in the order of the columns a key's components order, computed ones included, partitioned or not (the primary key
// plan, K2): rows ordered by (f(c), c) are ordered by c, and a read in that order needs no sorted run.
//
// THE ORACLE IS LINQ: the rows ordered as LINQ orders them, nulls last whichever way.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.Keys;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed partial class DatasetKeyOrderReadTests
{
    /// <summary>
    /// Under the key <c>(Service, Bucket(Sequence, 1000), Sequence)</c>, partitioned by day or not, merged and consolidated:
    /// no object keeps a sorted run, and a read by the service then the sequence, both ways, returns every row in order. It
    /// asked for a sorted run of each object before.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AReadInTheOrderOfAComputedKeysColumnsNeedsNoRun(bool partitioned)
    {
        Random random = new Random(66);
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, KeyRow.Schema, new DatasetOptions
        {
            Layout = new DatasetLayout
            {
                Partition = partitioned ? LayoutValue.Column(nameof(KeyRow.Day)) : null,
                SortKey = [LayoutValue.Column(nameof(KeyRow.Service)), LayoutValue.Column(nameof(KeyRow.Sequence)).Bucket(1_000), LayoutValue.Column(nameof(KeyRow.Sequence))],
                ObjectBytes = 256 << 10,
                MergeFanout = 4,
                CalmAfter = null,
            },
            ContentsObjectBytes = 0,
        }, Ct);
        List<KeyRow> all = await AppendAsync(dataset, random, appends: 6, rows: 10_000);
        Assert.Equal([nameof(KeyRow.Service), nameof(KeyRow.Sequence)], dataset.Snapshot.Schema.ObjectOrder!.Paths);

        Dictionary<long, KeyRow> bySequence = all.ToDictionary(static row => row.Sequence);
        foreach (bool descending in (bool[])[false, true])
        {
            List<KeyRow> read = await ReadAsync(dataset, [nameof(KeyRow.Service), nameof(KeyRow.Sequence)], descending, bySequence, null);
            Assert.Equal(all.Count, read.Count);
            for (int i = 1; i < read.Count; i++)
            {
                (string? before, string? after) = (read[i - 1].Service, read[i].Service);
                int order = before is null ? (after is null ? 0 : 1) : after is null ? -1 : string.CompareOrdinal(before, after);
                order = order != 0 ? order : read[i - 1].Sequence.CompareTo(read[i].Sequence);
                Assert.True(
                    descending ? (before is null ? after is null : after is null || order >= 0) : order <= 0,
                    $"a read by the service then the sequence {(descending ? "downward" : "upward")} goes back at row {i}");
            }
        }
    }

    /// <summary>
    /// The key cursor over <c>(Service, Bucket(Sequence, 1000), Sequence)</c> partitioned by day walks the service then the
    /// sequence, every row once in order; a seek of the service's walk alone opens an object of each partition, the
    /// partitions walked side by side, and lands on LINQ's first service at or past it. It refused the dataset, and the
    /// service's walk opened every object.
    /// </summary>
    [Fact]
    public async Task TheKeyCursorWalksTheColumnsAComputedKeyOrdersUnderAPartition()
    {
        Random random = new Random(69);
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, KeyRow.Schema, new DatasetOptions
        {
            Layout = new DatasetLayout
            {
                Partition = LayoutValue.Column(nameof(KeyRow.Day)),
                SortKey = [LayoutValue.Column(nameof(KeyRow.Service)), LayoutValue.Column(nameof(KeyRow.Sequence)).Bucket(1_000), LayoutValue.Column(nameof(KeyRow.Sequence))],
                ObjectBytes = 32 << 10,
                MergeFanout = 4,
                CalmAfter = null,
            },
            ContentsObjectBytes = 0,
        }, Ct);
        List<KeyRow> all = await AppendAsync(dataset, random, appends: 6, rows: 10_000);
        int objects = 0;
        await foreach (PositionedObject held in dataset.ScanBuilder().ObjectsAsync(Ct))
        {
            objects++;
        }

        await using (DatasetKeyCursor cursor = await DatasetKeyCursor.OpenAsync(dataset, Ct))
        {
            long entries = 0;
            byte[] previous = [];
            for (bool more = await cursor.SeekFirstAsync(Ct); more; more = await cursor.NextAsync(Ct))
            {
                byte[] key = cursor.KeyBytes.ToArray();
                Assert.True(previous.AsSpan().SequenceCompareTo(key) <= 0, $"the cursor goes back at entry {entries}");
                previous = key;
                entries++;
            }

            Assert.Equal(all.Count, entries);

            // Downward from the last, every row once in reverse.
            entries = 0;
            previous = [];
            for (bool more = await cursor.SeekLastAsync(Ct); more; more = await cursor.PreviousAsync(Ct))
            {
                byte[] key = cursor.KeyBytes.ToArray();
                Assert.True(entries == 0 || previous.AsSpan().SequenceCompareTo(key) >= 0, $"the cursor goes up at entry {entries} downward");
                previous = key;
                entries++;
            }

            Assert.Equal(all.Count, entries);
        }

        List<string> services = [.. all.Where(static row => row.Service is not null).Select(static row => row.Service!).Distinct().Order(StringComparer.Ordinal)];
        long levelZero = dataset.Snapshot.Levels[0].Entries;

        // The last services, downward: an object of each partition, and the one whose null services come last.
        await using (DatasetKeyCursor tail = await DatasetKeyCursor.OpenAsync(dataset.Snapshot, nameof(KeyRow.Service), distinct: false, indexes: true, Ct))
        {
            Assert.True(await tail.SeekLastAsync(Ct));
            for (int step = 0; step < 20; step++)
            {
                Assert.True(await tail.PreviousAsync(Ct));
            }

            Assert.True(tail.Cursors <= (2 * 3) + levelZero, $"the last services opened {tail.Cursors} objects of {objects}");
        }

        for (int probe = 0; probe < 20; probe++)
        {
            string sought = $"svc-{random.Next(0, 320):D3}";
            await using DatasetKeyCursor walk = await DatasetKeyCursor.OpenAsync(dataset.Snapshot, nameof(KeyRow.Service), distinct: false, indexes: true, Ct);
            bool found = await walk.SeekAsync(Vorticity.Expressions.FilterLiteral.From(sought), SeekMode.AtOrBefore, Ct);
            string? want = services.LastOrDefault(service => string.CompareOrdinal(service, sought) <= 0);
            Assert.Equal(want is not null, found);
            Assert.True(want is null || System.Text.Encoding.UTF8.GetString(walk.Key.BytesValue) == want, $"a seek at or before {sought} lands elsewhere than {want}");
            Assert.True(walk.Cursors <= (2 * 3) + levelZero, $"a seek at or before {sought} opened {walk.Cursors} objects of {objects}");
        }
        for (int probe = 0; probe < 20; probe++)
        {
            string sought = $"svc-{random.Next(0, 320):D3}";
            await using DatasetKeyCursor walk = await DatasetKeyCursor.OpenAsync(dataset.Snapshot, nameof(KeyRow.Service), distinct: false, indexes: true, Ct);
            bool found = await walk.SeekAsync(Vorticity.Expressions.FilterLiteral.From(sought), SeekMode.AtOrAfter, Ct);
            string? want = services.FirstOrDefault(service => string.CompareOrdinal(service, sought) >= 0);
            Assert.Equal(want is not null, found);
            Assert.True(want is null || System.Text.Encoding.UTF8.GetString(walk.Key.BytesValue) == want, $"a seek of {sought} lands elsewhere than {want}");
            Assert.True(walk.Cursors <= (2 * 3) + levelZero, $"a seek of {sought} opened {walk.Cursors} objects of {objects}");
        }
    }

    /// <summary>
    /// Partitioned by day under the key <c>(Service, Sequence)</c>, in small objects consolidated, several a partition: a read
    /// by the service, or by the whole key, each both ways, holds open an object of each partition and the few whose keys
    /// meet at their ends, downward too the one whose null services a read gives last, and returns every row in order. It
    /// held every object open at once, the services bounding none by their summaries.
    /// </summary>
    [Fact]
    public async Task AReadInTheKeysOrderUnderAPartitionHoldsAnObjectOfEachPartitionOpen()
    {
        Random random = new Random(67);
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, KeyRow.Schema, new DatasetOptions
        {
            Layout = new DatasetLayout
            {
                Partition = LayoutValue.Column(nameof(KeyRow.Day)),
                SortKey = [LayoutValue.Column(nameof(KeyRow.Service)), LayoutValue.Column(nameof(KeyRow.Sequence))],
                ObjectBytes = 32 << 10,
                MergeFanout = 4,
                CalmAfter = null,
            },
            ContentsObjectBytes = 0,
        }, Ct);
        List<KeyRow> all = await AppendAsync(dataset, random, appends: 6, rows: 10_000);
        int objects = 0;
        await foreach (PositionedObject held in dataset.ScanBuilder().ObjectsAsync(Ct))
        {
            objects++;
        }

        Assert.True(objects >= 4 * 3, $"{objects} objects over 3 partitions");
        Dictionary<long, KeyRow> bySequence = all.ToDictionary(static row => row.Sequence);
        foreach (string[] paths in (string[][])[[nameof(KeyRow.Service)], [nameof(KeyRow.Service), nameof(KeyRow.Sequence)]])
        {
            foreach (bool descending in (bool[])[false, true])
            {
                DatasetScanCounters counters = new DatasetScanCounters();
                List<KeyRow> read = await ReadAsync(dataset, paths, descending, bySequence, counters);
                string label = $"({string.Join(", ", paths)}) {(descending ? "downward" : "upward")}";
                Assert.Equal(all.Count, read.Count);
                Assert.True(counters.Cursors <= (descending ? 2 * 3 : 3) + 2, $"{label}: {counters.Cursors} objects open at once of {objects}");
                for (int i = 1; i < read.Count; i++)
                {
                    (string? before, string? after) = (read[i - 1].Service, read[i].Service);
                    int order = before is null ? (after is null ? 0 : 1) : after is null ? -1 : string.CompareOrdinal(before, after);
                    order = order != 0 || paths.Length == 1 ? order : read[i - 1].Sequence.CompareTo(read[i].Sequence);
                    Assert.True(
                        descending ? (before is null ? after is null : after is null || order >= 0) : order <= 0,
                        $"{label}: goes back at row {i}");
                }
            }
        }

        DatasetPlan plan = await dataset.ScanBuilder().InKeyOrder(nameof(KeyRow.Service)).ExplainAsync(Ct);
        Assert.True(plan.Cursors <= 3 * 2, $"the plan holds {plan.Cursors} cursors of {objects} objects");
    }

    /// <summary>
    /// Partitioned by day under the key <c>(Service, Sequence)</c>: the first ten rows by the service, through a fresh handle,
    /// are LINQ's and read an object of each partition and the few that meet at their ends, the source reading in the
    /// service's order on asking. They read every object's services first, the order asked of no source under a partition.
    /// </summary>
    [Fact]
    public async Task TheFirstRowsByTheKeyUnderAPartitionReadAnObjectOfEachPartition()
    {
        Random random = new Random(68);
        await using MemoryObjectStore store = new MemoryObjectStore();
        List<KeyRow> all;
        int objects = 0;
        await using (VortexDataset building = await VortexDataset.CreateAsync(store, KeyRow.Schema, new DatasetOptions
        {
            Layout = new DatasetLayout
            {
                Partition = LayoutValue.Column(nameof(KeyRow.Day)),
                SortKey = [LayoutValue.Column(nameof(KeyRow.Service)), LayoutValue.Column(nameof(KeyRow.Sequence))],
                ObjectBytes = 32 << 10,
                MergeFanout = 4,
                CalmAfter = null,
            },
            ContentsObjectBytes = 0,
        }, Ct))
        {
            all = await AppendAsync(building, random, appends: 6, rows: 10_000);
            await foreach (PositionedObject held in building.ScanBuilder().ObjectsAsync(Ct))
            {
                objects++;
            }
        }

        HashSet<string> read = [];
        store.Fails = (operation, key) =>
        {
            if (operation == ObjectOperation.Read && key.StartsWith(CommitKey.DataPrefix, StringComparison.Ordinal))
            {
                lock (read)
                {
                    read.Add(key);
                }
            }

            return false;
        };
        await using VortexDataset dataset = await VortexDataset.OpenAsync(store, cancellationToken: Ct);
        Assert.True(new DatasetScanSource(dataset, dataset.Snapshot).OrdersOnAsking(new Vorticity.Expressions.ColumnFieldExpr(nameof(KeyRow.Service)), stopsEarly: true));
        List<string?> first = await dataset.Scan<KeyRow>().OrderBy(r => r.Service).Select(r => r.Service).Take(10).ToListAsync(Ct);
        Assert.Equal(
            all.Where(static row => row.Service is not null).Select(static row => row.Service).Order(StringComparer.Ordinal).Take(10),
            first);
        Assert.True(read.Count is > 0 and <= 3 + 2, $"{read.Count} objects read of {objects}");

        // The first services' groups, in order, through another fresh handle: final once a greater service is read.
        read.Clear();
        await using VortexDataset grouping = await VortexDataset.OpenAsync(store, cancellationToken: Ct);
        List<ServiceCount> groups = await grouping.Scan<KeyRow>().GroupBy(r => r.Service).OrderBy(g => g.Key).Take(5)
            .Select(g => (g.Key, g.Count())).As<ServiceCount>().ToRecordsAsync(Ct).ToListAsync(Ct);
        Assert.Equal(
            all.Where(static row => row.Service is not null).GroupBy(static row => row.Service).OrderBy(static g => g.Key, StringComparer.Ordinal).Take(5)
                .Select(static g => new ServiceCount(g.Key, g.Count())),
            groups);
        Assert.True(read.Count is > 0 and <= 3 + 2, $"the first groups read {read.Count} objects of {objects}");
    }

    /// <summary>
    /// <paramref name="appends"/> appends of <paramref name="rows"/> rows over three days and 300 services, one in twenty
    /// null, merged every second append, then consolidated; no object keeps a sorted run.
    /// </summary>
    private static async Task<List<KeyRow>> AppendAsync(VortexDataset dataset, Random random, int appends, int rows)
    {
        List<KeyRow> all = [];
        for (int a = 0; a < appends; a++)
        {
            KeyRow[] batch = [.. Enumerable.Range(0, rows).Select(i => new KeyRow(
                random.Next(0, 3),
                random.Next(0, 20) == 0 ? null : $"svc-{random.Next(0, 300):D3}",
                all.Count + i,
                random.Next(0, 20) == 0 ? null : random.Next(0, 50)))];
            all.AddRange(batch);
            await using (ObjectDraft draft = dataset.StartObject())
            {
                await draft.Writer.WriteAsync<KeyRow>(batch, Ct);
                await dataset.AppendAsync(draft, Ct);
            }

            if (a % 2 == 1)
            {
                while (await dataset.CompactAsync(cancellationToken: Ct) is not null)
                {
                }
            }
        }

        await dataset.ConsolidateAsync(Ct);
        await foreach (PositionedObject held in dataset.ScanBuilder().ObjectsAsync(Ct))
        {
            Assert.False(held.Entry.Contents?.Locating?.Any(static entry => entry.Kind == Vorticity.Indexes.IndexKinds.SortedRuns) == true, $"'{held.Entry.Key}' keeps a sorted run");
        }

        return all;
    }

    /// <summary>Every row a read in the order of <paramref name="paths"/> returns, in its order, its most open cursors counted in <paramref name="counters"/>.</summary>
    private static async Task<List<KeyRow>> ReadAsync(
        VortexDataset dataset, string[] paths, bool descending, Dictionary<long, KeyRow> bySequence, DatasetScanCounters? counters)
    {
        List<KeyRow> read = [];
        DatasetScanBuilder scan = dataset.ScanBuilder().InKeyOrder(paths, descending);
        if (counters is not null)
        {
            scan.WithMetrics(counters);
        }

        await foreach (RecordBatch batch in scan.ExecuteAsync(Ct))
        {
            ReadOnlySpan<long> sequences = batch.Column(2).AsPrimitive<long>().Values;
            for (int row = 0; row < batch.RowCount; row++)
            {
                read.Add(bySequence[sequences[row]]);
            }
        }

        return read;
    }

    /// <summary>A service's rows counted.</summary>
    [VortexRecord]
    public partial record struct ServiceCount(string? Service, long Count);
}
