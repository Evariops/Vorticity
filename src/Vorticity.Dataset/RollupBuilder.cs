using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO.Hashing;
using Vorticity.Expressions;
using Vorticity.Indexes;
using Vorticity.Keys;
using Vorticity.Scanning;

namespace Vorticity.Dataset;

/// <summary>
/// Sums the counts of a run into a rollup (<see cref="RollupEntry"/>) and its object, with no row read: at rank 1, the
/// counts of consecutive objects of a level; past it, the totals and the buckets of consecutive rollups of the rank below.
/// A column's totals add every child's; its buckets add each clustering value's apart, when the dataset declares one and
/// the run holds two to <see cref="MaxBuckets"/> of them. A column is kept when every child counts it.
/// </summary>
internal sealed class RollupBuilder
{
    /// <summary>The most clustering values a rollup keeps apart: past them, its buckets would weigh more than its children's totals.</summary>
    internal const int MaxBuckets = 256;

    private readonly int _level;
    private readonly int _rank;
    private readonly ClusteringKey _key;
    private readonly ValueCut? _values;
    private readonly int _maxBuckets;
    private readonly XxHash128 _fingerprint = new XxHash128();
    private readonly List<ObjectSummaries> _summaries = [];
    private readonly SortedDictionary<int, ColumnSums> _columns = [];
    private readonly List<BucketSums> _buckets = [];
    private ReadOnlyMemory<byte> _first;
    private ReadOnlyMemory<byte> _last;
    private int _children;
    private long _objects;
    private long _rows;

    // A child of no clustering value its summaries prove, or a rollup that keeps no buckets: the rollup keeps none.
    private bool _unbucketed;

    /// <param name="level">The level whose objects the rollup covers.</param>
    /// <param name="rank">Its rank: 1 over objects.</param>
    /// <param name="key">The dataset's clustering key.</param>
    /// <param name="values">The dataset's clustering value, or null for none, which keeps no buckets.</param>
    /// <param name="maxBuckets">The most clustering values kept apart.</param>
    internal RollupBuilder(int level, int rank, ClusteringKey key, ValueCut? values, int maxBuckets = MaxBuckets)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(rank, 1);
        _level = level;
        _rank = rank;
        _key = key;
        _values = values;
        _maxBuckets = maxBuckets;
    }

    /// <summary>The children added.</summary>
    internal int Children => _children;

    /// <summary>
    /// Adds the object <paramref name="held"/>, the next of the run, with <paramref name="counts"/> the counts of each column
    /// it keeps and their sketches.
    /// </summary>
    internal void AddObject(in PositionedObject held, IReadOnlyList<(int Field, ColumnCounts Counts, ColumnSketches? Sketches)> counts)
    {
        if (_rank != 1)
        {
            throw new InvalidOperationException($"A rollup of rank {_rank} sums rollups, not objects.");
        }

        ObjectSummaries summaries = held.Summaries(_key);
        Child(held.TreeKey, held.TreeKey, held.Entry.ToBytes(), summaries, held.Entry.Rows, 1);
        BucketSums? bucket = _values is { } values && values.Single(summaries) is { } image ? BucketOf(image, summaries, held.Entry.Rows) : Unbucketed();
        foreach ((int field, ColumnCounts columnCounts, ColumnSketches? sketches) in counts)
        {
            ColumnSums sums = SumsOf(field);
            sums.Totals.Add(columnCounts, sketches);
            sums.Children++;
            bucket?.MergeOf(field).Add(columnCounts, sketches);
        }
    }

    /// <summary>
    /// Adds the rollup <paramref name="child"/>, of the rank below, the next of the run, with <paramref name="counts"/> the
    /// totals of each column it keeps and, when it keeps buckets, its counts by bucket, each with their sketches.
    /// </summary>
    internal void AddRollup(
        RollupEntry child,
        IReadOnlyList<(int Field, ColumnCounts Counts, ColumnSketches? Sketches, ColumnCounts? BucketCounts, ColumnSketches? BucketSketches)> counts)
    {
        if (child.Rank != _rank - 1 || child.Level != _level)
        {
            throw new InvalidOperationException($"A rollup of rank {_rank} sums rollups of rank {_rank - 1} of level {_level}, not of rank {child.Rank} of level {child.Level}.");
        }

        Span<byte> fingerprint = stackalloc byte[16];
        BinaryPrimitives.WriteUInt128BigEndian(fingerprint, child.Fingerprint);
        Child(child.FirstKey, child.LastKey, fingerprint, child.Summaries, child.Rows, child.Objects);

        // Its buckets, by their place in it; none when it keeps none.
        BucketSums?[] buckets = new BucketSums?[child.Buckets.Count];
        if (_values is null || child.Buckets.Count == 0)
        {
            Unbucketed();
        }
        else
        {
            for (int b = 0; b < buckets.Length; b++)
            {
                RollupBucket bucket = child.Buckets[b];
                buckets[b] = BucketOf(bucket.Image, bucket.Low, bucket.High, bucket.Rows);
            }
        }

        foreach ((int field, ColumnCounts totals, ColumnSketches? sketches, ColumnCounts? bucketCounts, ColumnSketches? bucketSketches) in counts)
        {
            ColumnSums sums = SumsOf(field);
            sums.Totals.Add(totals, sketches);
            sums.Children++;
            if (buckets.Length == 1 && buckets[0] is { } single)
            {
                single.MergeOf(field).Add(totals, sketches);
            }
            else if (buckets.Length > 1 && bucketCounts is { Buckets: > 0 } kept && kept.Buckets == buckets.Length)
            {
                for (int b = 0; b < buckets.Length; b++)
                {
                    if (CountsBuckets.Apart(kept, bucketSketches, b) is ({ } apart, var apartSketches))
                    {
                        buckets[b]!.MergeOf(field).Add(apart, apartSketches);
                    }
                }
            }
            else if (buckets.Length > 1)
            {
                Unbucketed();
            }
        }
    }

    /// <summary>
    /// The rollup's object, its regions one after the other, and its entry, the object stored under <paramref name="key"/>
    /// with <paramref name="uid"/>; null when no child was added.
    /// </summary>
    internal (byte[] Bytes, RollupEntry Entry)? Finish(string key, UInt128 uid)
    {
        if (_children == 0)
        {
            return null;
        }

        // The clustering values kept apart: one names the rollup's value, which its totals count; two or more, buckets.
        bool bucketed = _values is not null && !_unbucketed && _buckets.Count <= _maxBuckets;
        List<RollupBucket> buckets = [];
        if (bucketed)
        {
            foreach (BucketSums bucket in _buckets)
            {
                buckets.Add(new RollupBucket(bucket.Image, bucket.Low, bucket.High, bucket.Rows));
            }
        }

        ArrayBufferWriter<byte> written = new ArrayBufferWriter<byte>(64 << 10);
        List<RollupColumn> columns = [];
        foreach ((int field, ColumnSums sums) in _columns)
        {
            if (sums.Children != _children || sums.Totals.Finish() is not ({ } totals, var sketches))
            {
                continue;
            }

            IndexSegment counts = Region(written, totals.ToBytes());
            IndexSegment? totalSketches = sketches is null ? null : Region(written, sketches.ToBytes());
            IndexSegment? bucketCounts = null;
            IndexSegment? bucketSketches = null;
            if (buckets.Count > 1)
            {
                List<(ColumnCounts Counts, ColumnSketches? Sketches)?> parts = [];
                foreach (BucketSums bucket in _buckets)
                {
                    parts.Add(bucket.Columns.TryGetValue(field, out CountsMerge? merge) ? merge.Finish() : null);
                }

                if (CountsBuckets.Together(parts) is ({ } together, var togetherSketches))
                {
                    bucketCounts = Region(written, together.ToBytes());
                    bucketSketches = togetherSketches is null ? null : Region(written, togetherSketches.ToBytes());
                }
            }

            columns.Add(new RollupColumn(field, counts, totalSketches, bucketCounts, bucketSketches));
        }

        RollupEntry entry = new RollupEntry
        {
            Level = _level,
            Rank = _rank,
            FirstKey = _first,
            LastKey = _last,
            Children = _children,
            Objects = _objects,
            Rows = _rows,
            Key = key,
            Uid = uid,
            Bytes = written.WrittenCount,
            Summaries = ObjectSummaries.Union(_summaries),
            Fingerprint = _fingerprint.GetCurrentHashAsUInt128(),
            Buckets = buckets,
            Columns = columns,
        };
        return (written.WrittenSpan.ToArray(), entry);
    }

    private static IndexSegment Region(ArrayBufferWriter<byte> written, byte[] bytes)
    {
        IndexSegment region = IndexSegment.Of(written.WrittenCount, bytes, 0);
        written.Write(bytes);
        return region;
    }

    /// <summary>Counts a child: its first and last tree keys, what the fingerprint hashes of it, its summaries, rows and objects.</summary>
    private void Child(ReadOnlyMemory<byte> first, ReadOnlyMemory<byte> last, ReadOnlySpan<byte> hashed, ObjectSummaries summaries, long rows, long objects)
    {
        if (_children > 0 && first.Span.SequenceCompareTo(_last.Span) <= 0)
        {
            throw new InvalidOperationException("A rollup's children come in the order of their tree keys, each past the one before.");
        }

        _first = _children == 0 ? first : _first;
        _last = last;
        _fingerprint.Append(first.Span);
        _fingerprint.Append(hashed);
        _summaries.Add(summaries);
        _rows += rows;
        _objects += objects;
        _children++;
    }

    private ColumnSums SumsOf(int field)
    {
        if (!_columns.TryGetValue(field, out ColumnSums? sums))
        {
            _columns[field] = sums = new ColumnSums();
        }

        return sums;
    }

    /// <summary>The bucket of an object of clustering value <paramref name="image"/> and summaries <paramref name="summaries"/>.</summary>
    private BucketSums? BucketOf(FilterLiteral image, ObjectSummaries summaries, long rows) =>
        summaries.TryGet(_key.InstantPath, out ColumnSummary column) && column.HasMin && column.HasMax
            ? BucketOf(image, column.Min, column.Max, rows)
            : Unbucketed();

    /// <summary>The bucket of clustering value <paramref name="image"/>, its bounds and rows grown by a child's.</summary>
    private BucketSums BucketOf(FilterLiteral image, FilterLiteral low, FilterLiteral high, long rows)
    {
        // The children come in key order, and so their values: a value is the last bucket's or a new one past it.
        BucketSums bucket;
        if (_buckets.Count > 0 && KeyCursor.Compare(_buckets[^1].Image, image) == 0)
        {
            bucket = _buckets[^1];
            bucket.Low = KeyCursor.Compare(low, bucket.Low) < 0 ? low : bucket.Low;
            bucket.High = KeyCursor.Compare(high, bucket.High) > 0 ? high : bucket.High;
        }
        else
        {
            bucket = new BucketSums(image, low, high);
            _buckets.Add(bucket);
        }

        bucket.Rows += rows;
        return bucket;
    }

    private BucketSums? Unbucketed()
    {
        _unbucketed = true;
        return null;
    }

    /// <summary>A column's totals over the children, and how many children counted it.</summary>
    private sealed class ColumnSums
    {
        internal CountsMerge Totals { get; } = new CountsMerge();

        internal int Children { get; set; }
    }

    /// <summary>A clustering value's bounds, rows and counts over the children of that value.</summary>
    private sealed class BucketSums(FilterLiteral image, FilterLiteral low, FilterLiteral high)
    {
        internal FilterLiteral Image { get; } = image;

        internal FilterLiteral Low { get; set; } = low;

        internal FilterLiteral High { get; set; } = high;

        internal long Rows { get; set; }

        internal Dictionary<int, CountsMerge> Columns { get; } = [];

        internal CountsMerge MergeOf(int field)
        {
            if (!Columns.TryGetValue(field, out CountsMerge? merge))
            {
                Columns[field] = merge = new CountsMerge();
            }

            return merge;
        }
    }
}
