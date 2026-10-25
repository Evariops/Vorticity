using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using Vorticity.Expressions;
using Vorticity.Indexes;
using Vorticity.Serialization.Protobuf;

namespace Vorticity.Dataset;

/// <summary>
/// One clustering value a rollup keeps apart (<see cref="RollupEntry.Buckets"/>): the value, the bounds of the key's first
/// column over its objects, and their rows.
/// </summary>
/// <param name="Image">The clustering value, the image of the key's first column through the dataset's clustering function.</param>
/// <param name="Low">The least value of the key's first column in its objects.</param>
/// <param name="High">The greatest.</param>
/// <param name="Rows">Its objects' rows.</param>
internal readonly record struct RollupBucket(FilterLiteral Image, FilterLiteral Low, FilterLiteral High, long Rows);

/// <summary>
/// Where a rollup keeps one column's counts: the totals over its objects, and the counts by bucket when it keeps buckets,
/// each beside its sketches, regions of the rollup's object.
/// </summary>
/// <param name="Field">The column, by its place in the dataset's schema.</param>
/// <param name="Counts">The totals (<see cref="ColumnCounts"/>), an entry a value.</param>
/// <param name="Sketches">Their sketches, null for none.</param>
/// <param name="BucketCounts">The counts kept by bucket (<see cref="CountsBuckets"/>), null when the rollup keeps no buckets.</param>
/// <param name="BucketSketches">Their sketches, null for none.</param>
internal readonly record struct RollupColumn(int Field, IndexSegment Counts, IndexSegment? Sketches, IndexSegment? BucketCounts, IndexSegment? BucketSketches)
{
    /// <summary>The measures the totals carry, by their places in the schema: what a question may sum or take the extremes of.</summary>
    public int[] MeasureFields { get; init; } = [];

    /// <summary>Whether the totals' sketches keep a quantile sketch of each measure.</summary>
    public bool Sketched { get; init; }

    /// <summary>Whether the totals hold an entry of the values a budget left out, which gives no value of its own.</summary>
    public bool LeavesOut { get; init; }

    /// <summary>
    /// The bytes of the totals' header and directory, which a reader of some values reads first, then the chunks that hold
    /// them, each checked against its checksum (<see cref="ColumnCounts.ChunkSums"/>); 0 when it reads them whole.
    /// </summary>
    public int CountsHead { get; init; }

    /// <summary>The bytes of the header and directory of the totals' sketches, as <see cref="CountsHead"/>; 0 for none.</summary>
    public int SketchesHead { get; init; }

    /// <summary>The bytes of the header and directory of the counts by bucket, as <see cref="CountsHead"/>; 0 for none.</summary>
    public int BucketCountsHead { get; init; }

    /// <summary>The bytes of the header and directory of their sketches, as <see cref="CountsHead"/>; 0 for none.</summary>
    public int BucketSketchesHead { get; init; }

    /// <inheritdoc/>
    public bool Equals(RollupColumn other) =>
        Field == other.Field && Counts == other.Counts && Sketches == other.Sketches && BucketCounts == other.BucketCounts
        && BucketSketches == other.BucketSketches && MeasureFields.AsSpan().SequenceEqual(other.MeasureFields) && Sketched == other.Sketched
        && LeavesOut == other.LeavesOut && CountsHead == other.CountsHead && SketchesHead == other.SketchesHead
        && BucketCountsHead == other.BucketCountsHead && BucketSketchesHead == other.BucketSketchesHead;

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Field, Counts, BucketCounts, Sketched, LeavesOut);
}

/// <summary>
/// A rollup, as the tree of rollups carries it: the counts of a run of consecutive objects of one level, in the order of
/// their tree keys, or of a run of consecutive rollups of the rank below over that level, summed with no row read. Its tree
/// key is its level, its rank and the first tree key it covers, so that the rollups of a level and rank lie in the order
/// of what they cover.
/// </summary>
/// <remarks>
/// A rollup the tree holds covers every object of its level between its first and its last tree key: a commit that puts or
/// removes an entry of the level between them removes the rollup with it. Its object is regions written one after the
/// other, each checksummed, which a reader fetches with no open, as an object's table of contents names its synopses.
/// </remarks>
internal sealed record RollupEntry
{
    /// <summary>The bytes of a tree key before the first key it covers: its level and its rank.</summary>
    internal const int KeyPrefix = 2;

    /// <summary>The level whose objects it covers.</summary>
    public required int Level { get; init; }

    /// <summary>Its rank: 1 over objects, k + 1 over rollups of rank k.</summary>
    public required int Rank { get; init; }

    /// <summary>The first tree key of the level it covers.</summary>
    public required ReadOnlyMemory<byte> FirstKey { get; init; }

    /// <summary>The last tree key of the level it covers.</summary>
    public required ReadOnlyMemory<byte> LastKey { get; init; }

    /// <summary>What it sums directly: objects at rank 1, rollups past it.</summary>
    public int Children { get; init; }

    /// <summary>The objects under it.</summary>
    public long Objects { get; init; }

    /// <summary>Their rows.</summary>
    public long Rows { get; init; }

    /// <summary>Its object's key in the store.</summary>
    public required string Key { get; init; }

    /// <summary>Its object's identity, which the session's caches key its regions by.</summary>
    public UInt128 Uid { get; init; }

    /// <summary>Its object's bytes.</summary>
    public long Bytes { get; init; }

    /// <summary>The union of its objects' summaries, the key's first column bounded by their keys where their files do not bound it.</summary>
    public ObjectSummaries Summaries { get; init; } = ObjectSummaries.Empty;

    /// <summary>The hash of what it sums: each object's tree key and entry, or each rollup's tree key and fingerprint, in their order.</summary>
    public UInt128 Fingerprint { get; init; }

    /// <summary>The clustering values it keeps apart, ascending; one when it covers objects of one value; empty when the dataset declares none.</summary>
    public IReadOnlyList<RollupBucket> Buckets { get; init; } = [];

    /// <summary>The columns whose counts it keeps, by their place in the schema.</summary>
    public IReadOnlyList<RollupColumn> Columns { get; init; } = [];

    /// <summary>Its key in the tree of rollups.</summary>
    internal byte[] TreeKey => TreeKeyOf(Level, Rank, FirstKey.Span);

    /// <summary>The tree key of a rollup of level <paramref name="level"/> and rank <paramref name="rank"/> from <paramref name="firstKey"/>.</summary>
    internal static byte[] TreeKeyOf(int level, int rank, ReadOnlySpan<byte> firstKey)
    {
        byte[] key = new byte[KeyPrefix + firstKey.Length];
        key[0] = checked((byte)level);
        key[1] = checked((byte)rank);
        firstKey.CopyTo(key.AsSpan(KeyPrefix));
        return key;
    }

    /// <summary>The place in the tree of rollups where those of level <paramref name="level"/> and rank <paramref name="rank"/> start.</summary>
    internal static byte[] PrefixOf(int level, int rank) => TreeKeyOf(level, rank, []);

    /// <summary>Where it keeps the counts of column <paramref name="field"/>, or null when it keeps none.</summary>
    internal RollupColumn? ColumnOf(int field)
    {
        foreach (RollupColumn column in Columns)
        {
            if (column.Field == field)
            {
                return column;
            }
        }

        return null;
    }

    /// <summary>Whether it covers tree key <paramref name="treeKey"/> of its level: between its first and its last.</summary>
    internal bool Covers(ReadOnlySpan<byte> treeKey) =>
        treeKey.SequenceCompareTo(FirstKey.Span) >= 0 && treeKey.SequenceCompareTo(LastKey.Span) <= 0;

    /// <summary>The value its tree entry carries: everything but its tree key.</summary>
    internal byte[] ToBytes()
    {
        ProtoWriter writer = new ProtoWriter();
        try
        {
            writer.WriteBytes(Field.LastKey, LastKey.Span);
            writer.WriteUInt64(Field.Children, (ulong)Children);
            writer.WriteUInt64(Field.Objects, (ulong)Objects);
            writer.WriteUInt64(Field.Rows, (ulong)Rows);
            writer.WriteString(Field.Key, Key);
            Span<byte> uid = stackalloc byte[16];
            BinaryPrimitives.WriteUInt128BigEndian(uid, Uid);
            writer.WriteBytes(Field.Uid, uid);
            writer.WriteUInt64(Field.Bytes, (ulong)Bytes);
            writer.WriteBytes(Field.Summaries, Summaries.ToBytes());
            Span<byte> fingerprint = stackalloc byte[16];
            BinaryPrimitives.WriteUInt128BigEndian(fingerprint, Fingerprint);
            writer.WriteBytes(Field.Fingerprint, fingerprint);
            foreach (RollupBucket bucket in Buckets)
            {
                writer.WriteBytes(Field.Bucket, BucketBytes(bucket));
            }

            foreach (RollupColumn column in Columns)
            {
                ProtoWriter inner = new ProtoWriter();
                try
                {
                    inner.WriteUInt64Always(ColumnField.Field, (ulong)column.Field);
                    WriteRegion(ref inner, ColumnField.Counts, column.Counts);
                    WriteRegion(ref inner, ColumnField.Sketches, column.Sketches);
                    WriteRegion(ref inner, ColumnField.BucketCounts, column.BucketCounts);
                    WriteRegion(ref inner, ColumnField.BucketSketches, column.BucketSketches);
                    foreach (int measure in column.MeasureFields)
                    {
                        inner.WriteUInt64Always(ColumnField.Measure, (ulong)measure);
                    }

                    inner.WriteBool(ColumnField.Sketched, column.Sketched);
                    inner.WriteBool(ColumnField.LeavesOut, column.LeavesOut);
                    inner.WriteUInt32(ColumnField.CountsHead, (uint)column.CountsHead);
                    inner.WriteUInt32(ColumnField.SketchesHead, (uint)column.SketchesHead);
                    inner.WriteUInt32(ColumnField.BucketCountsHead, (uint)column.BucketCountsHead);
                    inner.WriteUInt32(ColumnField.BucketSketchesHead, (uint)column.BucketSketchesHead);
                    writer.WriteBytes(Field.Column, inner.WrittenSpan);
                }
                finally
                {
                    inner.Dispose();
                }
            }

            return writer.WrittenSpan.ToArray();
        }
        finally
        {
            writer.Dispose();
        }
    }

    /// <summary>The rollup under <paramref name="treeKey"/> whose tree entry carries <paramref name="value"/>.</summary>
    /// <exception cref="CommitFormatException">The key or the value is no rollup's.</exception>
    internal static RollupEntry FromBytes(ReadOnlySpan<byte> treeKey, ReadOnlySpan<byte> value)
    {
        if (treeKey.Length < KeyPrefix)
        {
            throw new CommitFormatException("A rollup's tree key is shorter than its level and rank.");
        }

        ReadOnlyMemory<byte> lastKey = default;
        int children = 0;
        long objects = 0;
        long rows = 0;
        string? key = null;
        UInt128 uid = UInt128.Zero;
        long bytes = 0;
        ObjectSummaries summaries = ObjectSummaries.Empty;
        UInt128 fingerprint = UInt128.Zero;
        List<RollupBucket> buckets = [];
        List<RollupColumn> columns = [];
        try
        {
            ProtoReader reader = new ProtoReader(value);
            while (reader.TryReadTag(out int field, out ProtoWireType wire))
            {
                switch (field, wire)
                {
                    case (Field.LastKey, ProtoWireType.LengthDelimited):
                        lastKey = reader.ReadLengthDelimited().ToArray();
                        break;
                    case (Field.Children, ProtoWireType.Varint):
                        children = checked((int)reader.ReadVarint());
                        break;
                    case (Field.Objects, ProtoWireType.Varint):
                        objects = checked((long)reader.ReadVarint());
                        break;
                    case (Field.Rows, ProtoWireType.Varint):
                        rows = checked((long)reader.ReadVarint());
                        break;
                    case (Field.Key, ProtoWireType.LengthDelimited):
                        key = System.Text.Encoding.UTF8.GetString(reader.ReadLengthDelimited());
                        break;
                    case (Field.Uid, ProtoWireType.LengthDelimited):
                        uid = BinaryPrimitives.ReadUInt128BigEndian(Exactly(reader.ReadLengthDelimited(), 16));
                        break;
                    case (Field.Bytes, ProtoWireType.Varint):
                        bytes = checked((long)reader.ReadVarint());
                        break;
                    case (Field.Summaries, ProtoWireType.LengthDelimited):
                        summaries = ObjectSummaries.FromBytes(reader.ReadLengthDelimited());
                        break;
                    case (Field.Fingerprint, ProtoWireType.LengthDelimited):
                        fingerprint = BinaryPrimitives.ReadUInt128BigEndian(Exactly(reader.ReadLengthDelimited(), 16));
                        break;
                    case (Field.Bucket, ProtoWireType.LengthDelimited):
                        buckets.Add(ReadBucket(reader.ReadLengthDelimited()));
                        break;
                    case (Field.Column, ProtoWireType.LengthDelimited):
                        columns.Add(ReadColumn(reader.ReadLengthDelimited()));
                        break;
                    default:
                        reader.SkipField(wire);
                        break;
                }
            }
        }
        catch (Exception cause) when (cause is VortexFormatException or OverflowException)
        {
            throw new CommitFormatException($"A rollup's entry does not decode: {cause.Message}", cause);
        }

        return new RollupEntry
        {
            Level = treeKey[0],
            Rank = treeKey[1],
            FirstKey = treeKey[KeyPrefix..].ToArray(),
            LastKey = lastKey,
            Children = children,
            Objects = objects,
            Rows = rows,
            Key = key ?? throw new CommitFormatException("A rollup's entry names no object."),
            Uid = uid,
            Bytes = bytes,
            Summaries = summaries,
            Fingerprint = fingerprint,
            Buckets = buckets,
            Columns = columns,
        };
    }

    private static ReadOnlySpan<byte> Exactly(ReadOnlySpan<byte> bytes, int length) =>
        bytes.Length == length ? bytes : throw new CommitFormatException($"A rollup's entry holds {bytes.Length} bytes where {length} belong.");

    private static byte[] BucketBytes(RollupBucket bucket)
    {
        byte[] bytes = new byte[ObjectSummaries.LiteralBytes(bucket.Image) + ObjectSummaries.LiteralBytes(bucket.Low)
            + ObjectSummaries.LiteralBytes(bucket.High) + TreePage.VarintBytes((ulong)bucket.Rows)];
        Span<byte> at = ObjectSummaries.WriteLiteral(bytes, bucket.Image);
        at = ObjectSummaries.WriteLiteral(at, bucket.Low);
        at = ObjectSummaries.WriteLiteral(at, bucket.High);
        TreePage.WriteVarint(at, (ulong)bucket.Rows);
        return bytes;
    }

    private static RollupBucket ReadBucket(ReadOnlySpan<byte> bytes)
    {
        int at = 0;
        FilterLiteral image = ObjectSummaries.ReadLiteral(bytes, ref at);
        FilterLiteral low = ObjectSummaries.ReadLiteral(bytes, ref at);
        FilterLiteral high = ObjectSummaries.ReadLiteral(bytes, ref at);
        long rows = checked((long)TreePage.ReadVarint(bytes, ref at));
        return at == bytes.Length ? new RollupBucket(image, low, high, rows) : throw new CommitFormatException("A rollup's bucket runs past its bytes.");
    }

    private static void WriteRegion(ref ProtoWriter writer, int field, IndexSegment? region)
    {
        if (region is not { } held)
        {
            return;
        }

        ProtoWriter inner = new ProtoWriter();
        try
        {
            inner.WriteUInt64Always(RegionField.Offset, held.Offset);
            inner.WriteUInt32Always(RegionField.Length, held.Length);
            inner.WriteUInt32(RegionField.Alignment, held.AlignmentExponent);
            if (held.Checksum is { } checksum)
            {
                inner.WriteUInt64Always(RegionField.Checksum, checksum);
            }

            writer.WriteBytes(field, inner.WrittenSpan);
        }
        finally
        {
            inner.Dispose();
        }
    }

    private static RollupColumn ReadColumn(ReadOnlySpan<byte> bytes)
    {
        int field = -1;
        IndexSegment? counts = null;
        IndexSegment? sketches = null;
        IndexSegment? bucketCounts = null;
        IndexSegment? bucketSketches = null;
        List<int> measures = [];
        bool sketched = false;
        bool leavesOut = false;
        int[] heads = new int[4];
        ProtoReader reader = new ProtoReader(bytes);
        while (reader.TryReadTag(out int tag, out ProtoWireType wire))
        {
            switch (tag, wire)
            {
                case (ColumnField.Field, ProtoWireType.Varint):
                    field = checked((int)reader.ReadVarint());
                    break;
                case (ColumnField.Counts, ProtoWireType.LengthDelimited):
                    counts = ReadRegion(reader.ReadLengthDelimited());
                    break;
                case (ColumnField.Sketches, ProtoWireType.LengthDelimited):
                    sketches = ReadRegion(reader.ReadLengthDelimited());
                    break;
                case (ColumnField.BucketCounts, ProtoWireType.LengthDelimited):
                    bucketCounts = ReadRegion(reader.ReadLengthDelimited());
                    break;
                case (ColumnField.BucketSketches, ProtoWireType.LengthDelimited):
                    bucketSketches = ReadRegion(reader.ReadLengthDelimited());
                    break;
                case (ColumnField.Measure, ProtoWireType.Varint):
                    measures.Add(checked((int)reader.ReadVarint()));
                    break;
                case (ColumnField.Sketched, ProtoWireType.Varint):
                    sketched = reader.ReadBool();
                    break;
                case (ColumnField.LeavesOut, ProtoWireType.Varint):
                    leavesOut = reader.ReadBool();
                    break;
                case (>= ColumnField.CountsHead and <= ColumnField.BucketSketchesHead, ProtoWireType.Varint):
                    heads[tag - ColumnField.CountsHead] = checked((int)reader.ReadVarint());
                    break;
                default:
                    reader.SkipField(wire);
                    break;
            }
        }

        return field >= 0 && counts is { } totals
            ? new RollupColumn(field, totals, sketches, bucketCounts, bucketSketches)
            {
                MeasureFields = [.. measures],
                Sketched = sketched,
                LeavesOut = leavesOut,
                CountsHead = heads[0],
                SketchesHead = heads[1],
                BucketCountsHead = heads[2],
                BucketSketchesHead = heads[3],
            }
            : throw new CommitFormatException("A rollup's column names no field or no counts.");
    }

    private static IndexSegment ReadRegion(ReadOnlySpan<byte> bytes)
    {
        ulong offset = 0;
        uint length = 0;
        byte alignment = 0;
        ulong? checksum = null;
        ProtoReader reader = new ProtoReader(bytes);
        while (reader.TryReadTag(out int tag, out ProtoWireType wire))
        {
            switch (tag, wire)
            {
                case (RegionField.Offset, ProtoWireType.Varint):
                    offset = reader.ReadVarint();
                    break;
                case (RegionField.Length, ProtoWireType.Varint):
                    length = reader.ReadVarint32();
                    break;
                case (RegionField.Alignment, ProtoWireType.Varint):
                    alignment = checked((byte)reader.ReadVarint());
                    break;
                case (RegionField.Checksum, ProtoWireType.Varint):
                    checksum = reader.ReadVarint();
                    break;
                default:
                    reader.SkipField(wire);
                    break;
            }
        }

        return new IndexSegment(offset, length, alignment, checksum);
    }

    private static class Field
    {
        internal const int LastKey = 1;
        internal const int Children = 2;
        internal const int Objects = 3;
        internal const int Rows = 4;
        internal const int Key = 5;
        internal const int Uid = 6;
        internal const int Bytes = 7;
        internal const int Summaries = 8;
        internal const int Fingerprint = 9;
        internal const int Bucket = 10;
        internal const int Column = 11;
    }

    private static class ColumnField
    {
        internal const int Field = 1;
        internal const int Counts = 2;
        internal const int Sketches = 3;
        internal const int BucketCounts = 4;
        internal const int BucketSketches = 5;
        internal const int Measure = 6;
        internal const int Sketched = 7;
        internal const int LeavesOut = 8;
        internal const int CountsHead = 9;
        internal const int SketchesHead = 10;
        internal const int BucketCountsHead = 11;
        internal const int BucketSketchesHead = 12;
    }

    private static class RegionField
    {
        internal const int Offset = 1;
        internal const int Length = 2;
        internal const int Alignment = 3;
        internal const int Checksum = 4;
    }
}
