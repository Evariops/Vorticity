// The rollups of a dataset's objects, built from their counts with no row read, and their counts kept by bucket.
//
// WHAT IS HELD: counts kept by bucket read back as written, each bucket taken apart giving back its own counts; a rollup
// of rank 1 over the objects of one hour holds, for each value, the sum of its objects' entries, word for word, and their
// rows; a rollup of rank 2 over the hours' rollups holds the sum of every object's, keeps each hour apart as a bucket with
// its bounds and rows, each bucket the hour's rollup's totals; and a rollup's entry reads back as written, its regions
// checksummed.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Aggregating;
using Vorticity.Dataset;
using Vorticity.Indexes;
using Xunit;
using Drawn = Vorticity.Tests.Dataset.DatasetCountsTests.Drawn;
using Kind = Vorticity.Tests.Dataset.DatasetCountsTests.Kind;

namespace Vorticity.Tests.Dataset;

public sealed class RollupBuilderTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void CountsKeptByBucketReadBackAndComeApart()
    {
        // Three buckets of a code's counts and a measure, the second with no row of value 2, the third with nulls.
        (ColumnCounts, ColumnSketches?)?[] buckets =
        [
            Bucket([(1, 3, 10), (2, 1, 5)], nulls: 0),
            Bucket([(1, 2, 7)], nulls: 0),
            Bucket([(2, 4, 1), (3, 2, 2)], nulls: 3),
        ];
        (ColumnCounts together, _) = CountsBuckets.Together(buckets)!.Value;
        ColumnCounts read = ColumnCounts.From(together.ToBytes());
        Assert.Equal(3, read.Buckets);
        Assert.Equal(together.Entries, read.Entries);
        Assert.Equal(together.Words, read.Words);
        Assert.Equal(together.EntryBuckets, read.EntryBuckets);
        Assert.Equal(together.Groups.Select(g => (g.Kind, Hex(g.Value), g.Entries)), read.Groups.Select(g => (g.Kind, Hex(g.Value), g.Entries)));

        // The nulls first, then the values: value 1 in buckets 0 and 1, value 2 in 0 and 2, value 3 in 2.
        Assert.Equal(["Null:1", "Value:1:2", "Value:2:2", "Value:3:1"], read.Groups.Select(g => $"{g.Kind}:{(g.Value is null ? "" : $"{g.Value[0]}:")}{g.Entries}"));
        for (int b = 0; b < buckets.Length; b++)
        {
            (ColumnCounts apart, _) = CountsBuckets.Apart(read, null, b)!.Value;
            Assert.Equal(Sums(buckets[b]!.Value.Item1), Sums(apart));
        }
    }

    [Fact]
    public async Task ARollupSumsItsObjectsAndKeepsEachHourApart()
    {
        Random random = new Random(91);
        Drawn[] columns = [new Drawn(Kind.Text, false, 7), new Drawn(Kind.Code, false, 5), new Drawn(Kind.Int64, true, 0), new Drawn(Kind.Float64, false, 0)];
        object?[][] rows = DatasetCountsTests.Rows(columns, random);
        await using MemoryObjectStore memory = new MemoryObjectStore();
        await using CountingObjectStore store = new CountingObjectStore(memory);
        await using VortexDataset dataset = await DatasetCountsTests.WriteAsync(store, columns, rows, new DatasetOptions
        {
            ClusteringKey = ["At"],
            Clustering = new ClusteringOptions { Value = ClusteringValue.Truncate(CalendarUnit.Hour), ObjectBytes = 48L << 10 },
            Write = new VortexWriteOptions { RowBlockSize = 512, DataBlockTargetBytes = 4 << 10 },
            SheetObjectBytes = 0,
            ContentsObjectBytes = 0,
        });
        ClusteringKey key = dataset.Snapshot.Schema.Key!;
        ValueCut values = dataset.Snapshot.Schema.Cut!;

        // The counted objects of the deepest level, by hour, in the order of their tree keys.
        List<PositionedObject> objects = [];
        await foreach (PositionedObject held in dataset.ScanBuilder().ObjectsAsync(Ct))
        {
            objects.Add(held);
        }

        int bottom = objects.Max(held => held.Level);
        List<IGrouping<long, PositionedObject>> hours = [.. objects
            .Where(held => held.Level == bottom && held.Entry.Contents is { Counts.Count: > 0 })
            .OrderBy(held => held.TreeKey.ToArray(), Comparer<byte[]>.Create((a, b) => a.AsSpan().SequenceCompareTo(b)))
            .GroupBy(held => values.Single(held.Summaries(key))!.Value.SignedValue)];
        Assert.True(hours.Count >= 2, $"{hours.Count} hours");
        Assert.Contains(hours, hour => hour.Count() >= 2);

        // A rollup of rank 1 an hour, over its objects' counts: each value's entry the sum of the objects'.
        int[] fields = [.. Enumerable.Range(1, columns.Length)];
        List<(RollupEntry Entry, byte[] Bytes)> ranked = [];
        Dictionary<int, Dictionary<string, long[]>> everything = [];
        foreach (IGrouping<long, PositionedObject> hour in hours)
        {
            RollupBuilder builder = new RollupBuilder(bottom, 1, key, values);
            Dictionary<int, Dictionary<string, long[]>> expected = [];
            foreach (PositionedObject held in hour)
            {
                List<(int, ColumnCounts, ColumnSketches?)> counts = [];
                foreach (int field in fields)
                {
                    if (await dataset.ReadCountsAsync(held.Entry, field, sketches: true, Ct) is ({ } kept, var sketches))
                    {
                        counts.Add((field, kept, sketches));
                        Add(expected, field, kept);
                        Add(everything, field, kept);
                    }
                }

                builder.AddObject(held, counts);
            }

            (byte[] bytes, RollupEntry entry) = builder.Finish($"rollups/{hour.Key}", (UInt128)hour.Key)!.Value;
            Assert.Equal(hour.Sum(held => held.Entry.Rows), entry.Rows);
            Assert.Equal(hour.Count(), entry.Objects);
            RollupBucket bucket = Assert.Single(entry.Buckets);
            Assert.Equal(hour.Key, bucket.Image.SignedValue);
            Assert.Equal(entry.Rows, bucket.Rows);
            Assert.NotEmpty(entry.Columns);
            foreach (RollupColumn column in entry.Columns)
            {
                Assert.Null(column.BucketCounts);
                Assert.Equal(expected[column.Field], Sums(Region(bytes, column.Counts)));
            }

            ranked.Add((entry, bytes));
        }

        // A rollup of rank 2 over the hours': every object's sum, an hour a bucket, each bucket its hour's totals.
        RollupBuilder upper = new RollupBuilder(bottom, 2, key, values);
        foreach ((RollupEntry entry, byte[] bytes) in ranked)
        {
            upper.AddRollup(entry, [.. entry.Columns.Select(column => (column.Field, Region(bytes, column.Counts),
                column.Sketches is { } sketches ? ColumnSketches.From(Slice(bytes, sketches)) : null, (ColumnCounts?)null, (ColumnSketches?)null))]);
        }

        (byte[] upperBytes, RollupEntry top) = upper.Finish("rollups/top", UInt128.One)!.Value;
        Assert.Equal(ranked.Sum(r => r.Entry.Rows), top.Rows);
        Assert.Equal(ranked.Count, top.Children);
        Assert.Equal(ranked.Select(r => r.Entry.Buckets[0]), top.Buckets);
        foreach (RollupColumn column in top.Columns)
        {
            Assert.Equal(everything[column.Field], Sums(Region(upperBytes, column.Counts)));
            ColumnCounts kept = Region(upperBytes, Assert.IsType<IndexSegment>(column.BucketCounts));
            Assert.Equal(ranked.Count, kept.Buckets);
            for (int b = 0; b < ranked.Count; b++)
            {
                RollupColumn hourly = ranked[b].Entry.ColumnOf(column.Field)!.Value;
                Assert.Equal(Sums(Region(ranked[b].Bytes, hourly.Counts)), Sums(CountsBuckets.Apart(kept, null, b)!.Value.Counts));
            }
        }

        // The entry reads back as written.
        RollupEntry back = RollupEntry.FromBytes(top.TreeKey, top.ToBytes());
        Assert.Equal((top.Level, top.Rank, Hex(top.FirstKey.ToArray()), Hex(top.LastKey.ToArray()), top.Children, top.Objects, top.Rows, top.Key, top.Uid, top.Bytes, top.Fingerprint),
            (back.Level, back.Rank, Hex(back.FirstKey.ToArray()), Hex(back.LastKey.ToArray()), back.Children, back.Objects, back.Rows, back.Key, back.Uid, back.Bytes, back.Fingerprint));
        Assert.Equal(top.Buckets, back.Buckets);
        Assert.Equal(top.Columns, back.Columns);
        Assert.Equal(top.Summaries, back.Summaries);
        Assert.Equal(upperBytes.Length, back.Bytes);

        // Each region's header, of the size the entry records, reads alone: the directory of chunks a reader of some values
        // reads first, each chunk with its checksum.
        foreach (RollupColumn column in back.Columns)
        {
            foreach ((IndexSegment? region, int head) in (IEnumerable<(IndexSegment?, int)>)[(column.Counts, column.CountsHead), (column.BucketCounts, column.BucketCountsHead)])
            {
                CountsDirectory directory = ColumnCounts.ReadDirectory(upperBytes.AsSpan((int)region!.Value.Offset, head));
                Assert.Equal(head, directory.HeaderBytes);
                Assert.All(directory.Chunks, chunk => Assert.NotNull(chunk.Checksum));
            }
        }
    }

    /// <summary>A bucket's counts of a code: each value, its rows and a measure's one value repeated; and nulls.</summary>
    private static (ColumnCounts, ColumnSketches?)? Bucket((byte Value, long Rows, long Measure)[] values, long nulls)
    {
        List<CountGroup> groups = [];
        List<long> words = [];
        void Entry(CountedValue kind, byte[]? value, long rows, long measure)
        {
            groups.Add(new CountGroup(kind, value, groups.Count, 1));
            long[] state = new long[StateWords.Count([MeasureKind.Signed])];
            StateWords.Clear(state.AsSpan(), [MeasureKind.Signed]);
            state[0] = rows;
            state[1 + MeasureLayout.Values] = rows;
            StateWords.Write(rows * measure, state, 1 + MeasureLayout.Sum);
            state[1 + MeasureLayout.IntegerLeast] = measure;
            state[1 + MeasureLayout.IntegerGreatest] = measure;
            words.AddRange(state);
        }

        if (nulls > 0)
        {
            Entry(CountedValue.Null, null, nulls, 0);
        }

        foreach ((byte value, long rows, long measure) in values)
        {
            Entry(CountedValue.Value, [value], rows, measure);
        }

        return (new ColumnCounts([2], [MeasureKind.Signed], [.. groups], [.. words]), null);
    }

    /// <summary>Each group's words, by its kind and value; a group of several entries the sum of them.</summary>
    private static Dictionary<string, long[]> Sums(ColumnCounts counts)
    {
        Dictionary<string, long[]> sums = [];
        Add(sums, counts);
        return sums;
    }

    private static void Add(Dictionary<int, Dictionary<string, long[]>> into, int field, ColumnCounts counts)
    {
        if (!into.TryGetValue(field, out Dictionary<string, long[]>? sums))
        {
            into[field] = sums = [];
        }

        Add(sums, counts);
    }

    private static void Add(Dictionary<string, long[]> sums, ColumnCounts counts)
    {
        foreach (CountGroup group in counts.Groups)
        {
            string name = $"{group.Kind}:{Hex(group.Value)}";
            for (int e = group.FirstEntry; e < group.FirstEntry + group.Entries; e++)
            {
                if (sums.TryGetValue(name, out long[]? held))
                {
                    StateWords.Merge(held, counts.WordsOf(e), counts.Kinds);
                }
                else
                {
                    sums[name] = counts.WordsOf(e).ToArray();
                }
            }
        }
    }

    private static ColumnCounts Region(byte[] bytes, IndexSegment region) => ColumnCounts.From(Slice(bytes, region));

    private static ReadOnlySpan<byte> Slice(byte[] bytes, IndexSegment region)
    {
        ReadOnlySpan<byte> slice = bytes.AsSpan((int)region.Offset, (int)region.Length);
        Assert.True(region.Holds(slice));
        return slice;
    }

    private static string Hex(byte[]? bytes) => bytes is null ? "-" : Convert.ToHexString(bytes);
}
