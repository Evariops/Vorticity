using System;
using System.Collections.Generic;
using Vorticity.Serialization.Protobuf;

namespace Vorticity.Dataset;

/// <summary>
/// The chunker's boundary parameters: the smallest chunk it may cut, the size it aims for, and the
/// size at which it cuts whatever the boundary rule says.
/// </summary>
internal readonly record struct ChunkerSettings(int MinBytes, int TargetBytes, int MaxBytes);

/// <summary>
/// The compaction settings. <c>LevelTargetBytes</c> is level 1's target object size, which each
/// level above multiplies.
/// </summary>
internal readonly record struct CompactionSettings(int Levels, long LevelTargetBytes, int Fanout);

/// <summary>
/// The retention settings: how many versions to keep beyond the current one, and for how long, in
/// seconds.
/// </summary>
internal readonly record struct RetentionSettings(int Versions, long Seconds);

/// <summary>A page carried inside the header as well as at the offset its reference names.</summary>
internal readonly record struct InlinedPage(PageReference Reference, ReadOnlyMemory<byte> Bytes);

/// <summary>Where the pages region of an earlier version's commit object starts, one past its header.</summary>
internal readonly record struct PagesStart(ulong Version, long Offset);

/// <summary>How far the entries of one of the application's sources are counted in the dataset.</summary>
/// <param name="Source">The source's name, the application's: a shard of raw data, a partition of a log.</param>
/// <param name="Offset">The position up to which its entries are counted, in the source's own numbering.</param>
/// <param name="AtUnixMilliseconds">When the offset last moved, by the clock of the writer that moved it, for information.</param>
internal readonly record struct SourceOffset(string Source, ulong Offset, long AtUnixMilliseconds);

/// <summary>
/// One level of the dataset tree, as the header records it; level 0 is the newest and smallest.
/// <c>Inlined</c> holds the pages carried in the header, top first.
/// </summary>
internal sealed record CommitLevel(int Level, long Entries, PageReference Top, IReadOnlyList<InlinedPage> Inlined)
{
    /// <summary>A level with no inlined page.</summary>
    public CommitLevel(int level, long entries, PageReference top)
        : this(level, entries, top, [])
    {
    }

    /// <summary>
    /// The tree's levels under <see cref="Top"/>; 1 when the top is a leaf page. Recorded rather
    /// than derived: a page does not say how far it is from the leaves, and a reader needs the
    /// depth before it has read one.
    /// </summary>
    public int Depth { get; init; }

    /// <summary>The rows of every object under it.</summary>
    public long Rows { get; init; }

    /// <summary>
    /// The tree key the level's last job stopped at, which its next job starts past under a round
    /// robin; empty until a job of the level records one.
    /// </summary>
    public ReadOnlyMemory<byte> Pointer { get; init; }
}

/// <summary>A commit object's header.</summary>
internal sealed record CommitHeader
{
    /// <summary>This commit's version, which is also the inverse of its key.</summary>
    public required ulong Version { get; init; }

    /// <summary>The version this one was built on; 0 for the first.</summary>
    public ulong Parent { get; init; }

    /// <summary>The dataset's chunking seed, fixed at creation.</summary>
    public ulong Seed { get; init; }

    /// <summary>The schema, as the core serializes a dtype. Opaque here.</summary>
    public ReadOnlyMemory<byte> Schema { get; init; }

    /// <summary>
    /// How the dataset lays out its rows (<see cref="DatasetOptions.Layout"/>): its partition, its key, the prefix its
    /// objects index, and the policy its jobs organize them by; null for none, rows in the order they arrive.
    /// </summary>
    public DatasetLayout? Layout { get; init; }

    /// <summary>
    /// The indexes every writer of the dataset builds into the objects it writes, besides those of
    /// its own options: what <see cref="VortexDataset.IndexAsync"/> adds, in the bytes an index
    /// directory stores a policy in. Empty for none.
    /// </summary>
    public ReadOnlyMemory<byte> WritePolicy { get; init; }

    public ChunkerSettings Chunker { get; init; }

    public CompactionSettings Compaction { get; init; }

    public RetentionSettings Retention { get; init; }

    /// <summary>When the commit was written, in milliseconds since the Unix epoch.</summary>
    public long CreatedAtUnixMilliseconds { get; init; }

    /// <summary>The levels, lowest first.</summary>
    public IReadOnlyList<CommitLevel> Levels { get; init; } = [];

    /// <summary>
    /// Every name a column gave up when the schema changed, in the order they were given up: the
    /// objects written before the change hold their columns under them, and no column takes one again.
    /// </summary>
    public IReadOnlyList<RetiredColumn> Retired { get; init; } = [];

    /// <summary>
    /// Where the pages region starts of each earlier version whose pages the header names without
    /// carrying them, by version: a reader then reads such a page in one request, where it would
    /// first read that version's preamble to learn it. A header written before these were recorded
    /// has none, and a reader that does not know the field skips it.
    /// </summary>
    public IReadOnlyList<PagesStart> Starts { get; init; } = [];

    /// <summary>
    /// Whether the dataset lives on a store that keeps every object under a retention lock, fixed at
    /// creation: every writer then deletes by marks, compacts to the read bounds only, and every
    /// vacuum leaves what a lock still keeps.
    /// </summary>
    public bool LockedStore { get; init; }

    /// <summary>
    /// Per source of the application's entries, how far its entries are counted in the dataset, in
    /// the ordinal order of the names: what the commits that declared the ranges they consumed
    /// advanced.
    /// </summary>
    public IReadOnlyList<SourceOffset> Sources { get; init; } = [];

    /// <summary>
    /// The last version whose commit changed rows, which a commit that only moves them copies from
    /// its parent.
    /// </summary>
    public ulong LastDataChange { get; init; }

    /// <summary>
    /// The most time a deleted row stays in the store's bytes after its delete
    /// (<see cref="DatasetOptions.ErasureDelay"/>), fixed at creation and kept by every header; null
    /// for none.
    /// </summary>
    public TimeSpan? ErasureDelay { get; init; }

    /// <summary>
    /// The columns whose values questions look up (<see cref="DatasetOptions.Identifiers"/>), kept by
    /// every header; empty for none.
    /// </summary>
    public IReadOnlyList<string> Identifiers { get; init; } = [];

    /// <summary>
    /// The keys of variant columns every job shreds into columns of their own (<see cref="DatasetOptions.ShreddedPaths"/>),
    /// kept by every header; empty for none.
    /// </summary>
    public IReadOnlyList<string> ShreddedPaths { get; init; } = [];

    /// <summary>The tree of rollups (<see cref="RollupEntry"/>), as a level is recorded, without inlined pages; null for none.</summary>
    public CommitLevel? Rollups { get; init; }

    /// <summary>The longest delay a header reads, in seconds: what a <see cref="TimeSpan"/> holds.</summary>
    private const ulong MaxDelaySeconds = (ulong)(long.MaxValue / TimeSpan.TicksPerSecond);

    internal static class Field
    {
        internal const int Version = 1;
        internal const int Parent = 2;
        internal const int Seed = 3;
        internal const int Schema = 4;

        // 5 held the clustering key's columns, retired with it: never reused.
        internal const int WritePolicy = 6;
        internal const int Chunker = 7;
        internal const int Compaction = 8;
        internal const int Retention = 9;
        internal const int CreatedAt = 10;
        internal const int Levels = 11;
        internal const int Retired = 12;
        internal const int Starts = 13;
        internal const int LockedStore = 14;
        internal const int Sources = 15;
        internal const int LastDataChange = 16;

        // 17 held the features of format 4, 19, 21 and 22 the periods' lateness horizon, length and changes, and 23 the
        // partition columns of a design that preceded the partitions of the layout: all retired, never reused.
        internal const int ErasureDelay = 18;
        internal const int Identifiers = 20;
        internal const int ShreddedPaths = 24;

        // 25 held the clustering options, retired with them: never reused.
        internal const int Rollups = 26;
        internal const int Layout = 27;
    }

    internal static class SourceField
    {
        internal const int Name = 1;
        internal const int Offset = 2;
        internal const int At = 3;
    }

    internal static class StartField
    {
        internal const int Version = 1;
        internal const int Offset = 2;
    }

    internal static class RetiredField
    {
        internal const int Name = 1;
        internal const int Current = 2;
    }

    internal static class LayoutField
    {
        internal const int Partition = 1;
        internal const int Component = 2;
        internal const int Prefix = 3;
        internal const int Organization = 4;
        internal const int ObjectBytes = 5;
        internal const int MergeFanout = 6;
        internal const int CalmAfter = 7;
        internal const int LateShare = 8;
    }

    internal static class ValueField
    {
        internal const int Column = 1;
        internal const int Key = 2;
        internal const int Class = 3;
        internal const int Function = 4;
        internal const int Unit = 5;
        internal const int Width = 6;
        internal const int FloatWidth = 7;

        // 8 is reserved for the length of a prefix of a text or a binary.
    }

    internal static class LevelField
    {
        internal const int Level = 1;
        internal const int Entries = 2;
        internal const int Top = 3;
        internal const int Inlined = 4;
        internal const int Depth = 5;
        internal const int Rows = 6;
        internal const int Pointer = 7;
    }

    internal static class InlinedField
    {
        internal const int Reference = 1;
        internal const int Bytes = 2;
    }

    /// <summary>
    /// Writes the header. Every offset it carries is relative to its object's pages region, so
    /// nothing here depends on where the header ends and it can be written in one pass.
    /// </summary>
    internal void Write(ref ProtoWriter writer)
    {
        writer.WriteUInt64(Field.Version, Version);
        writer.WriteUInt64(Field.Parent, Parent);
        writer.WriteUInt64(Field.Seed, Seed);
        if (!Schema.IsEmpty)
        {
            writer.WriteBytes(Field.Schema, Schema.Span);
        }

        if (!WritePolicy.IsEmpty)
        {
            writer.WriteBytes(Field.WritePolicy, WritePolicy.Span);
        }

        WriteChunker(ref writer);
        WriteCompaction(ref writer);
        WriteRetention(ref writer);
        writer.WriteInt64(Field.CreatedAt, CreatedAtUnixMilliseconds);
        foreach (CommitLevel level in Levels)
        {
            WriteLevel(ref writer, level, Field.Levels);
        }

        if (Rollups is { } rollups)
        {
            WriteLevel(ref writer, rollups, Field.Rollups);
        }

        foreach (RetiredColumn column in Retired)
        {
            WriteRetired(ref writer, column);
        }

        foreach (PagesStart start in Starts)
        {
            WriteStart(ref writer, start);
        }

        if (LockedStore)
        {
            writer.WriteUInt64(Field.LockedStore, 1);
        }

        foreach (SourceOffset source in Sources)
        {
            WriteSource(ref writer, source);
        }

        writer.WriteUInt64(Field.LastDataChange, LastDataChange);
        if (ErasureDelay is { } delay)
        {
            writer.WriteUInt64(Field.ErasureDelay, (ulong)delay.TotalSeconds);
        }

        foreach (string column in Identifiers)
        {
            writer.WriteString(Field.Identifiers, column);
        }

        foreach (string path in ShreddedPaths)
        {
            writer.WriteString(Field.ShreddedPaths, path);
        }

        if (Layout is { } layout)
        {
            WriteLayout(ref writer, layout);
        }
    }

    /// <summary>
    /// The layout, written whole even when it states nothing but the defaults: its presence is what says the dataset has
    /// one. The organization is the one the dataset keeps, never left to defaults a later library might change.
    /// </summary>
    private static void WriteLayout(ref ProtoWriter writer, DatasetLayout layout)
    {
        ProtoWriter inner = new ProtoWriter();
        try
        {
            if (layout.Partition is { } partition)
            {
                WriteValue(ref inner, LayoutField.Partition, partition);
            }

            foreach (LayoutValue component in layout.SortKey)
            {
                WriteValue(ref inner, LayoutField.Component, component);
            }

            inner.WriteUInt64(LayoutField.Prefix, (ulong)layout.Prefix);
            inner.WriteUInt64Always(LayoutField.Organization, (ulong)layout.Organized);
            inner.WriteUInt64Always(LayoutField.ObjectBytes, (ulong)layout.ObjectBytes);
            inner.WriteUInt64Always(LayoutField.MergeFanout, (ulong)layout.MergeFanout);
            if (layout.CalmAfter is { } calm)
            {
                inner.WriteUInt64Always(LayoutField.CalmAfter, (ulong)calm.TotalSeconds);
            }

            inner.WriteUInt64Always(LayoutField.LateShare, (ulong)layout.LateShare);
            writer.WriteBytesAlways(Field.Layout, inner.WrittenSpan);
        }
        finally
        {
            inner.Dispose();
        }
    }

    /// <summary>A layout value as a message of its source and its function: the function, then its unit and widths where they are not nought.</summary>
    private static void WriteValue(ref ProtoWriter writer, int field, LayoutValue value)
    {
        ProtoWriter inner = new ProtoWriter();
        try
        {
            inner.WriteStringAlways(ValueField.Column, value.SourcePath);
            if (value.Key is { } key)
            {
                inner.WriteStringAlways(ValueField.Key, key);
                inner.WriteUInt64Always(ValueField.Class, (ulong)value.Class);
            }

            inner.WriteUInt64Always(ValueField.Function, (ulong)value.Function);
            inner.WriteUInt64(ValueField.Unit, (ulong)value.Unit);
            inner.WriteSInt64(ValueField.Width, value.Width);
            inner.WriteDouble(ValueField.FloatWidth, value.FloatWidth);
            writer.WriteBytesAlways(field, inner.WrittenSpan);
        }
        finally
        {
            inner.Dispose();
        }
    }

    private static void WriteSource(ref ProtoWriter writer, SourceOffset source)
    {
        // The name is written even when empty, as a retired name is, so a reader can tell an entry
        // from a torn one.
        ProtoWriter inner = new ProtoWriter();
        try
        {
            inner.WriteStringAlways(SourceField.Name, source.Source);
            inner.WriteUInt64(SourceField.Offset, source.Offset);
            inner.WriteInt64(SourceField.At, source.AtUnixMilliseconds);
            writer.WriteBytes(Field.Sources, inner.WrittenSpan);
        }
        finally
        {
            inner.Dispose();
        }
    }

    private static void WriteStart(ref ProtoWriter writer, PagesStart start)
    {
        ProtoWriter inner = new ProtoWriter();
        try
        {
            inner.WriteUInt64(StartField.Version, start.Version);
            inner.WriteInt64(StartField.Offset, start.Offset);
            writer.WriteBytes(Field.Starts, inner.WrittenSpan);
        }
        finally
        {
            inner.Dispose();
        }
    }

    private static void WriteRetired(ref ProtoWriter writer, RetiredColumn column)
    {
        // The name is written even when empty, a column's name may be, so that a retired name is
        // never left out of the header, and a reader can tell an entry from a torn one.
        ProtoWriter inner = new ProtoWriter();
        try
        {
            inner.WriteStringAlways(RetiredField.Name, column.Name);
            if (column.Current.Length > 0)
            {
                inner.WriteString(RetiredField.Current, column.Current);
            }

            writer.WriteBytesAlways(Field.Retired, inner.WrittenSpan);
        }
        finally
        {
            inner.Dispose();
        }
    }

    private void WriteChunker(ref ProtoWriter writer)
    {
        if (Chunker == default)
        {
            return;
        }

        ProtoWriter inner = new ProtoWriter();
        try
        {
            inner.WriteInt32(1, Chunker.MinBytes);
            inner.WriteInt32(2, Chunker.TargetBytes);
            inner.WriteInt32(3, Chunker.MaxBytes);
            writer.WriteBytes(Field.Chunker, inner.WrittenSpan);
        }
        finally
        {
            inner.Dispose();
        }
    }

    private void WriteCompaction(ref ProtoWriter writer)
    {
        if (Compaction == default)
        {
            return;
        }

        ProtoWriter inner = new ProtoWriter();
        try
        {
            inner.WriteInt32(1, Compaction.Levels);
            inner.WriteInt64(2, Compaction.LevelTargetBytes);
            inner.WriteInt32(3, Compaction.Fanout);
            writer.WriteBytes(Field.Compaction, inner.WrittenSpan);
        }
        finally
        {
            inner.Dispose();
        }
    }

    private void WriteRetention(ref ProtoWriter writer)
    {
        if (Retention == default)
        {
            return;
        }

        ProtoWriter inner = new ProtoWriter();
        try
        {
            inner.WriteInt32(1, Retention.Versions);
            inner.WriteInt64(2, Retention.Seconds);
            writer.WriteBytes(Field.Retention, inner.WrittenSpan);
        }
        finally
        {
            inner.Dispose();
        }
    }

    private static void WriteLevel(ref ProtoWriter writer, CommitLevel level, int field)
    {
        ProtoWriter inner = new ProtoWriter();
        try
        {
            inner.WriteInt32(LevelField.Level, level.Level);
            inner.WriteInt64(LevelField.Entries, level.Entries);
            inner.WriteInt32(LevelField.Depth, level.Depth);
            inner.WriteInt64(LevelField.Rows, level.Rows);
            Span<byte> reference = stackalloc byte[PageReference.Bytes];
            level.Top.Write(reference);
            inner.WriteBytes(LevelField.Top, reference);
            foreach (InlinedPage page in level.Inlined)
            {
                ProtoWriter inlined = new ProtoWriter();
                try
                {
                    page.Reference.Write(reference);
                    inlined.WriteBytes(InlinedField.Reference, reference);
                    inlined.WriteBytes(InlinedField.Bytes, page.Bytes.Span);
                    inner.WriteBytes(LevelField.Inlined, inlined.WrittenSpan);
                }
                finally
                {
                    inlined.Dispose();
                }
            }

            if (!level.Pointer.IsEmpty)
            {
                inner.WriteBytes(LevelField.Pointer, level.Pointer.Span);
            }

            writer.WriteBytes(field, inner.WrittenSpan);
        }
        finally
        {
            inner.Dispose();
        }
    }

    internal static CommitHeader Read(ReadOnlySpan<byte> bytes)
    {
        ulong version = 0;
        ulong parent = 0;
        ulong seed = 0;
        byte[] schema = [];
        byte[] policy = [];
        ChunkerSettings chunker = default;
        CompactionSettings compaction = default;
        RetentionSettings retention = default;
        long createdAt = 0;
        List<CommitLevel> levels = [];
        List<RetiredColumn> retired = [];
        List<PagesStart> starts = [];
        bool locked = false;
        List<SourceOffset> sources = [];
        ulong lastDataChange = 0;
        TimeSpan? erasureDelay = null;
        List<string> identifiers = [];
        List<string> shreddedPaths = [];
        DatasetLayout? layout = null;
        CommitLevel? rollups = null;

        try
        {
            ProtoReader reader = new ProtoReader(bytes);
            while (reader.TryReadTag(out int field, out ProtoWireType wire))
            {
                switch (field, wire)
                {
                    case (Field.Version, ProtoWireType.Varint):
                        version = reader.ReadVarint();
                        break;
                    case (Field.Parent, ProtoWireType.Varint):
                        parent = reader.ReadVarint();
                        break;
                    case (Field.Seed, ProtoWireType.Varint):
                        seed = reader.ReadVarint();
                        break;
                    case (Field.Schema, ProtoWireType.LengthDelimited):
                        schema = reader.ReadLengthDelimited().ToArray();
                        break;
                    case (Field.WritePolicy, ProtoWireType.LengthDelimited):
                        policy = reader.ReadLengthDelimited().ToArray();
                        break;
                    case (Field.Chunker, ProtoWireType.LengthDelimited):
                        chunker = ReadChunker(reader.ReadLengthDelimited());
                        break;
                    case (Field.Compaction, ProtoWireType.LengthDelimited):
                        compaction = ReadCompaction(reader.ReadLengthDelimited());
                        break;
                    case (Field.Retention, ProtoWireType.LengthDelimited):
                        retention = ReadRetention(reader.ReadLengthDelimited());
                        break;
                    case (Field.CreatedAt, ProtoWireType.Varint):
                        createdAt = (long)reader.ReadVarint();
                        break;
                    case (Field.Levels, ProtoWireType.LengthDelimited):
                        levels.Add(ReadLevel(reader.ReadLengthDelimited()));
                        break;
                    case (Field.Retired, ProtoWireType.LengthDelimited):
                        retired.Add(ReadRetired(reader.ReadLengthDelimited()));
                        break;
                    case (Field.Starts, ProtoWireType.LengthDelimited):
                        starts.Add(ReadStart(reader.ReadLengthDelimited()));
                        break;
                    case (Field.LockedStore, ProtoWireType.Varint):
                        locked = reader.ReadVarint() != 0;
                        break;
                    case (Field.Sources, ProtoWireType.LengthDelimited):
                        sources.Add(ReadSource(reader.ReadLengthDelimited()));
                        break;
                    case (Field.LastDataChange, ProtoWireType.Varint):
                        lastDataChange = reader.ReadVarint();
                        break;
                    case (Field.ErasureDelay, ProtoWireType.Varint):
                        erasureDelay = TimeSpan.FromSeconds((long)Math.Min(reader.ReadVarint(), MaxDelaySeconds));
                        break;
                    case (Field.Identifiers, ProtoWireType.LengthDelimited):
                        identifiers.Add(System.Text.Encoding.UTF8.GetString(reader.ReadLengthDelimited()));
                        break;
                    case (Field.ShreddedPaths, ProtoWireType.LengthDelimited):
                        shreddedPaths.Add(System.Text.Encoding.UTF8.GetString(reader.ReadLengthDelimited()));
                        break;
                    case (Field.Layout, ProtoWireType.LengthDelimited):
                        layout = ReadLayout(reader.ReadLengthDelimited());
                        break;
                    case (Field.Rollups, ProtoWireType.LengthDelimited):
                        rollups = ReadLevel(reader.ReadLengthDelimited());
                        break;
                    default:
                        // A field this version does not know is skipped, not fatal, so a later one
                        // can add fields without breaking the format.
                        reader.SkipField(wire);
                        break;
                }
            }
        }
        catch (VortexFormatException cause)
        {
            throw new CommitFormatException($"The commit header does not decode: {cause.Message}", cause);
        }

        if (version == 0)
        {
            throw new CommitFormatException("A commit header names no version.");
        }

        return new CommitHeader
        {
            Version = version,
            Parent = parent,
            Seed = seed,
            Schema = schema,
            WritePolicy = policy,
            Chunker = chunker,
            Compaction = compaction,
            Retention = retention,
            CreatedAtUnixMilliseconds = createdAt,
            Levels = levels,
            Retired = retired,
            Starts = starts,
            LockedStore = locked,
            Sources = sources,
            LastDataChange = lastDataChange,
            ErasureDelay = erasureDelay,
            Identifiers = identifiers,
            ShreddedPaths = shreddedPaths,
            Layout = layout,
            Rollups = rollups,
        };
    }

    private static DatasetLayout ReadLayout(ReadOnlySpan<byte> bytes)
    {
        LayoutValue? partition = null;
        List<LayoutValue> components = [];
        ulong prefix = 0;
        ulong organization = 0;
        ulong objectBytes = 0;
        ulong fanout = 0;
        ulong? calm = null;
        ulong share = 0;
        ProtoReader reader = new ProtoReader(bytes);
        while (reader.TryReadTag(out int field, out ProtoWireType wire))
        {
            switch (field, wire)
            {
                case (LayoutField.Partition, ProtoWireType.LengthDelimited):
                    partition = ReadValue(reader.ReadLengthDelimited());
                    break;
                case (LayoutField.Component, ProtoWireType.LengthDelimited):
                    components.Add(ReadValue(reader.ReadLengthDelimited()));
                    break;
                case (LayoutField.Prefix, ProtoWireType.Varint):
                    prefix = reader.ReadVarint();
                    break;
                case (LayoutField.Organization, ProtoWireType.Varint):
                    organization = reader.ReadVarint();
                    break;
                case (LayoutField.ObjectBytes, ProtoWireType.Varint):
                    objectBytes = reader.ReadVarint();
                    break;
                case (LayoutField.MergeFanout, ProtoWireType.Varint):
                    fanout = reader.ReadVarint();
                    break;
                case (LayoutField.CalmAfter, ProtoWireType.Varint):
                    calm = Math.Min(reader.ReadVarint(), MaxDelaySeconds);
                    break;
                case (LayoutField.LateShare, ProtoWireType.Varint):
                    share = reader.ReadVarint();
                    break;
                default:
                    reader.SkipField(wire);
                    break;
            }
        }

        if (components.Count > DatasetLayout.MostComponents || prefix > (ulong)components.Count || (components.Count > 0 && prefix == 0)
            || organization is not ((ulong)LayoutOrganization.Tiered or (ulong)LayoutOrganization.Leveled)
            || objectBytes is 0 or > long.MaxValue || fanout is < 2 or > int.MaxValue || share is < 1 or > int.MaxValue)
        {
            throw new CommitFormatException(
                $"A commit header's layout holds {components.Count} components, a prefix of {prefix}, organization {organization}, " +
                $"objects of {objectBytes} bytes, a fan-out of {fanout} and a share of {share}: no layout this library writes.");
        }

        return new DatasetLayout
        {
            Partition = partition,
            SortKey = components,
            IndexedPrefix = components.Count == 0 ? null : (int)prefix,
            Organization = (LayoutOrganization)organization,
            ObjectBytes = (long)objectBytes,
            MergeFanout = (int)fanout,
            CalmAfter = calm is { } seconds ? TimeSpan.FromSeconds((long)seconds) : null,
            LateShare = (int)share,
        };
    }

    private static LayoutValue ReadValue(ReadOnlySpan<byte> bytes)
    {
        string? column = null;
        string? key = null;
        ulong @class = 0;
        ulong function = 0;
        ulong unit = 0;
        long width = 0;
        double floatWidth = 0;
        ProtoReader reader = new ProtoReader(bytes);
        while (reader.TryReadTag(out int field, out ProtoWireType wire))
        {
            switch (field, wire)
            {
                case (ValueField.Column, ProtoWireType.LengthDelimited):
                    column = System.Text.Encoding.UTF8.GetString(reader.ReadLengthDelimited());
                    break;
                case (ValueField.Key, ProtoWireType.LengthDelimited):
                    key = System.Text.Encoding.UTF8.GetString(reader.ReadLengthDelimited());
                    break;
                case (ValueField.Class, ProtoWireType.Varint):
                    @class = reader.ReadVarint();
                    break;
                case (ValueField.Function, ProtoWireType.Varint):
                    function = reader.ReadVarint();
                    break;
                case (ValueField.Unit, ProtoWireType.Varint):
                    unit = reader.ReadVarint();
                    break;
                case (ValueField.Width, ProtoWireType.Varint):
                    width = reader.ReadSInt64();
                    break;
                case (ValueField.FloatWidth, ProtoWireType.Fixed64):
                    floatWidth = reader.ReadDouble();
                    break;
                default:
                    reader.SkipField(wire);
                    break;
            }
        }

        return column is null
            ? throw new CommitFormatException("A commit header's layout names a value of no column.")
            : LayoutValue.Of(column, key, @class, function, unit, width, floatWidth);
    }

    private static SourceOffset ReadSource(ReadOnlySpan<byte> bytes)
    {
        string name = string.Empty;
        ulong offset = 0;
        long at = 0;
        ProtoReader reader = new ProtoReader(bytes);
        while (reader.TryReadTag(out int field, out ProtoWireType wire))
        {
            switch (field, wire)
            {
                case (SourceField.Name, ProtoWireType.LengthDelimited):
                    name = System.Text.Encoding.UTF8.GetString(reader.ReadLengthDelimited());
                    break;
                case (SourceField.Offset, ProtoWireType.Varint):
                    offset = reader.ReadVarint();
                    break;
                case (SourceField.At, ProtoWireType.Varint):
                    at = (long)reader.ReadVarint();
                    break;
                default:
                    reader.SkipField(wire);
                    break;
            }
        }

        return new SourceOffset(name, offset, at);
    }

    private static PagesStart ReadStart(ReadOnlySpan<byte> bytes)
    {
        ulong version = 0;
        long offset = 0;
        ProtoReader reader = new ProtoReader(bytes);
        while (reader.TryReadTag(out int field, out ProtoWireType wire))
        {
            switch (field, wire)
            {
                case (StartField.Version, ProtoWireType.Varint):
                    version = reader.ReadVarint();
                    break;
                case (StartField.Offset, ProtoWireType.Varint):
                    offset = (long)reader.ReadVarint();
                    break;
                default:
                    reader.SkipField(wire);
                    break;
            }
        }

        if (version == 0 || offset < CommitFormat.PreambleBytes)
        {
            throw new CommitFormatException($"The header says version {version}'s pages start at {offset}, which is no version's.");
        }

        return new PagesStart(version, offset);
    }

    private static RetiredColumn ReadRetired(ReadOnlySpan<byte> bytes)
    {
        string? name = null;
        string current = string.Empty;
        ProtoReader reader = new ProtoReader(bytes);
        while (reader.TryReadTag(out int field, out ProtoWireType wire))
        {
            switch (field, wire)
            {
                case (RetiredField.Name, ProtoWireType.LengthDelimited):
                    name = System.Text.Encoding.UTF8.GetString(reader.ReadLengthDelimited());
                    break;
                case (RetiredField.Current, ProtoWireType.LengthDelimited):
                    current = System.Text.Encoding.UTF8.GetString(reader.ReadLengthDelimited());
                    break;
                default:
                    reader.SkipField(wire);
                    break;
            }
        }

        return name is null
            ? throw new CommitFormatException("The header retires a column it does not name.")
            : new RetiredColumn(name, current);
    }

    private static ChunkerSettings ReadChunker(ReadOnlySpan<byte> bytes)
    {
        int min = 0;
        int target = 0;
        int max = 0;
        ProtoReader reader = new ProtoReader(bytes);
        while (reader.TryReadTag(out int field, out ProtoWireType wire))
        {
            switch (field, wire)
            {
                case (1, ProtoWireType.Varint):
                    min = (int)reader.ReadVarint();
                    break;
                case (2, ProtoWireType.Varint):
                    target = (int)reader.ReadVarint();
                    break;
                case (3, ProtoWireType.Varint):
                    max = (int)reader.ReadVarint();
                    break;
                default:
                    reader.SkipField(wire);
                    break;
            }
        }

        return new ChunkerSettings(min, target, max);
    }

    private static CompactionSettings ReadCompaction(ReadOnlySpan<byte> bytes)
    {
        int levels = 0;
        long target = 0;
        int fanout = 0;
        ProtoReader reader = new ProtoReader(bytes);
        while (reader.TryReadTag(out int field, out ProtoWireType wire))
        {
            switch (field, wire)
            {
                case (1, ProtoWireType.Varint):
                    levels = (int)reader.ReadVarint();
                    break;
                case (2, ProtoWireType.Varint):
                    target = (long)reader.ReadVarint();
                    break;
                case (3, ProtoWireType.Varint):
                    fanout = (int)reader.ReadVarint();
                    break;
                default:
                    reader.SkipField(wire);
                    break;
            }
        }

        return new CompactionSettings(levels, target, fanout);
    }

    private static RetentionSettings ReadRetention(ReadOnlySpan<byte> bytes)
    {
        int versions = 0;
        long seconds = 0;
        ProtoReader reader = new ProtoReader(bytes);
        while (reader.TryReadTag(out int field, out ProtoWireType wire))
        {
            switch (field, wire)
            {
                case (1, ProtoWireType.Varint):
                    versions = (int)reader.ReadVarint();
                    break;
                case (2, ProtoWireType.Varint):
                    seconds = (long)reader.ReadVarint();
                    break;
                default:
                    reader.SkipField(wire);
                    break;
            }
        }

        return new RetentionSettings(versions, seconds);
    }

    private static CommitLevel ReadLevel(ReadOnlySpan<byte> bytes)
    {
        int level = 0;
        long entries = 0;
        int depth = 0;
        long rows = 0;
        PageReference top = PageReference.None;
        List<InlinedPage> inlined = [];
        byte[] pointer = [];
        ProtoReader reader = new ProtoReader(bytes);
        while (reader.TryReadTag(out int field, out ProtoWireType wire))
        {
            switch (field, wire)
            {
                case (LevelField.Level, ProtoWireType.Varint):
                    level = (int)reader.ReadVarint();
                    break;
                case (LevelField.Entries, ProtoWireType.Varint):
                    entries = (long)reader.ReadVarint();
                    break;
                case (LevelField.Depth, ProtoWireType.Varint):
                    depth = (int)reader.ReadVarint();
                    break;
                case (LevelField.Rows, ProtoWireType.Varint):
                    rows = (long)reader.ReadVarint();
                    break;
                case (LevelField.Top, ProtoWireType.LengthDelimited):
                    top = PageReference.Read(reader.ReadLengthDelimited());
                    break;
                case (LevelField.Inlined, ProtoWireType.LengthDelimited):
                    inlined.Add(ReadInlined(reader.ReadLengthDelimited()));
                    break;
                case (LevelField.Pointer, ProtoWireType.LengthDelimited):
                    pointer = reader.ReadLengthDelimited().ToArray();
                    break;
                default:
                    reader.SkipField(wire);
                    break;
            }
        }

        return new CommitLevel(level, entries, top, inlined) { Depth = depth, Rows = rows, Pointer = pointer };
    }

    private static InlinedPage ReadInlined(ReadOnlySpan<byte> bytes)
    {
        PageReference reference = PageReference.None;
        byte[] content = [];
        ProtoReader reader = new ProtoReader(bytes);
        while (reader.TryReadTag(out int field, out ProtoWireType wire))
        {
            switch (field, wire)
            {
                case (InlinedField.Reference, ProtoWireType.LengthDelimited):
                    reference = PageReference.Read(reader.ReadLengthDelimited());
                    break;
                case (InlinedField.Bytes, ProtoWireType.LengthDelimited):
                    content = reader.ReadLengthDelimited().ToArray();
                    break;
                default:
                    reader.SkipField(wire);
                    break;
            }
        }

        return new InlinedPage(reference, content);
    }
}
