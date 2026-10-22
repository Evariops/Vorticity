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

    /// <summary>The clustering key's columns, in key order.</summary>
    public IReadOnlyList<string> ClusteringKey { get; init; } = [];

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
    /// advanced. A header that carries one is written in commit format 3.
    /// </summary>
    public IReadOnlyList<SourceOffset> Sources { get; init; } = [];

    /// <summary>
    /// The last version whose commit changed rows, which a commit that only moves them copies from
    /// its parent; 0 when a writer that does not know the field wrote the header, and nothing is
    /// known then but that it is at most this version.
    /// </summary>
    public ulong LastDataChange { get; init; }

    /// <summary>
    /// The features of commit format 4 the dataset uses, kept by every header after the first that
    /// names one: the trees still hold what they describe. A header that names any is written in
    /// format 4.
    /// </summary>
    public DatasetFeatures Features { get; init; }

    /// <summary>The features this library knows: a header that names another is refused, a later library's.</summary>
    internal const DatasetFeatures KnownFeatures =
        DatasetFeatures.EntryFields | DatasetFeatures.ErasureDelay | DatasetFeatures.Periods | DatasetFeatures.Identifiers | DatasetFeatures.Partitions;

    /// <summary>
    /// Whether the dataset is compacted by periods: its flag (<see cref="DatasetFeatures.Periods"/>), which the lateness
    /// horizon goes with, a header that names one and not the other being refused.
    /// </summary>
    internal bool ByPeriods => (Features & DatasetFeatures.Periods) != 0;

    /// <summary>
    /// The most time a deleted row stays in the store's bytes after its delete
    /// (<see cref="DatasetOptions.ErasureDelay"/>), fixed at creation and kept by every header; null
    /// for none. A header that carries one names <see cref="DatasetFeatures.ErasureDelay"/>.
    /// </summary>
    public TimeSpan? ErasureDelay { get; init; }

    /// <summary>
    /// The lateness horizon of a dataset compacted by periods (<see cref="DatasetOptions.Periods"/>),
    /// fixed at creation and kept by every header; null for a dataset that is not. A header that
    /// carries one names <see cref="DatasetFeatures.Periods"/>.
    /// </summary>
    public TimeSpan? Lateness { get; init; }

    /// <summary>
    /// The calendar unit a period of a dataset compacted by periods spans (<see cref="PeriodOptions.Length"/>),
    /// which its every header names; null for a dataset that is not.
    /// </summary>
    internal SliceWidth? PeriodLength { get; init; }

    /// <summary>
    /// How many leading clustering-key columns are partition columns (<see cref="DatasetOptions.PartitionColumns"/>),
    /// fixed at creation; 0 for none. A header that carries some names <see cref="DatasetFeatures.Partitions"/>.
    /// </summary>
    internal int PartitionColumns { get; init; }

    /// <summary>
    /// The changes of the period length the dataset made since its creation, in order
    /// (<see cref="PeriodCalendar"/>); empty for none.
    /// </summary>
    internal IReadOnlyList<PeriodChange> PeriodChanges { get; init; } = [];

    /// <summary>
    /// The columns whose values questions look up (<see cref="DatasetOptions.Identifiers"/>), kept by
    /// every header; empty for none. A header that carries any names <see cref="DatasetFeatures.Identifiers"/>.
    /// </summary>
    public IReadOnlyList<string> Identifiers { get; init; } = [];

    /// <summary>The longest delay a header reads, in seconds: what a <see cref="TimeSpan"/> holds.</summary>
    private const ulong MaxDelaySeconds = (ulong)(long.MaxValue / TimeSpan.TicksPerSecond);

    internal static class Field
    {
        internal const int Version = 1;
        internal const int Parent = 2;
        internal const int Seed = 3;
        internal const int Schema = 4;
        internal const int ClusteringKey = 5;
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
        internal const int Features = 17;
        internal const int ErasureDelay = 18;
        internal const int Lateness = 19;
        internal const int Identifiers = 20;
        internal const int PeriodLength = 21;
        internal const int PeriodChanges = 22;
        internal const int PartitionColumns = 23;
    }

    internal static class PeriodChangeField
    {
        internal const int From = 1;
        internal const int Length = 2;
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

        foreach (string column in ClusteringKey)
        {
            writer.WriteString(Field.ClusteringKey, column);
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
            WriteLevel(ref writer, level);
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
        if (Features != DatasetFeatures.None)
        {
            writer.WriteUInt64(Field.Features, (ulong)Features);
        }

        if (ErasureDelay is { } delay)
        {
            writer.WriteUInt64(Field.ErasureDelay, (ulong)delay.TotalSeconds);
        }

        // Written even when zero: the field's presence is what says the dataset is compacted by periods.
        if (Lateness is { } lateness)
        {
            writer.WriteUInt64Always(Field.Lateness, (ulong)lateness.TotalSeconds);
        }

        foreach (string column in Identifiers)
        {
            writer.WriteString(Field.Identifiers, column);
        }

        if (PeriodLength is { } length)
        {
            writer.WriteUInt64Always(Field.PeriodLength, (ulong)length);
        }

        foreach (PeriodChange change in PeriodChanges)
        {
            ProtoWriter inner = new ProtoWriter();
            try
            {
                inner.WriteSInt64Always(PeriodChangeField.From, change.From);
                inner.WriteUInt64Always(PeriodChangeField.Length, (ulong)change.Length);
                writer.WriteBytes(Field.PeriodChanges, inner.WrittenSpan);
            }
            finally
            {
                inner.Dispose();
            }
        }

        if (PartitionColumns > 0)
        {
            writer.WriteUInt64Always(Field.PartitionColumns, (ulong)PartitionColumns);
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

    private static void WriteLevel(ref ProtoWriter writer, CommitLevel level)
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

            writer.WriteBytes(Field.Levels, inner.WrittenSpan);
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
        List<string> clustering = [];
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
        DatasetFeatures features = DatasetFeatures.None;
        TimeSpan? erasureDelay = null;
        TimeSpan? lateness = null;
        List<string> identifiers = [];
        SliceWidth? periodLength = null;
        List<PeriodChange> periodChanges = [];
        int partitionColumns = 0;

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
                    case (Field.ClusteringKey, ProtoWireType.LengthDelimited):
                        clustering.Add(System.Text.Encoding.UTF8.GetString(reader.ReadLengthDelimited()));
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
                    case (Field.Features, ProtoWireType.Varint):
                        features = (DatasetFeatures)reader.ReadVarint();
                        break;
                    case (Field.ErasureDelay, ProtoWireType.Varint):
                        erasureDelay = TimeSpan.FromSeconds((long)Math.Min(reader.ReadVarint(), MaxDelaySeconds));
                        break;
                    case (Field.Lateness, ProtoWireType.Varint):
                        lateness = TimeSpan.FromSeconds((long)Math.Min(reader.ReadVarint(), MaxDelaySeconds));
                        break;
                    case (Field.Identifiers, ProtoWireType.LengthDelimited):
                        identifiers.Add(System.Text.Encoding.UTF8.GetString(reader.ReadLengthDelimited()));
                        break;
                    case (Field.PeriodLength, ProtoWireType.Varint):
                        periodLength = PeriodUnit(reader.ReadVarint());
                        break;
                    case (Field.PeriodChanges, ProtoWireType.LengthDelimited):
                        periodChanges.Add(ReadPeriodChange(reader.ReadLengthDelimited()));
                        break;
                    case (Field.PartitionColumns, ProtoWireType.Varint):
                        partitionColumns = (int)Math.Min(reader.ReadVarint(), int.MaxValue);
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

        // A feature this library does not know is one of a later format, which it would misread.
        if ((features & ~KnownFeatures) != 0)
        {
            throw new CommitFormatException(
                $"A commit header names features 0x{(ulong)(features & ~KnownFeatures):x} this library does not know: a later library wrote the dataset.");
        }

        if (((features & DatasetFeatures.Periods) != 0) != lateness.HasValue || lateness.HasValue != periodLength.HasValue)
        {
            throw new CommitFormatException(
                "A commit header's flag of periods, its lateness horizon and its period length disagree: each goes with the others.");
        }

        return new CommitHeader
        {
            Version = version,
            Parent = parent,
            Seed = seed,
            Schema = schema,
            ClusteringKey = clustering,
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
            Features = features,
            ErasureDelay = erasureDelay,
            Lateness = lateness,
            Identifiers = identifiers,
            PeriodLength = periodLength,
            PeriodChanges = periodChanges,
            PartitionColumns = partitionColumns,
        };
    }

    /// <summary>A period unit as a header writes it.</summary>
    /// <exception cref="CommitFormatException">The code names no unit a period may span.</exception>
    private static SliceWidth PeriodUnit(ulong unit) =>
        unit is >= (ulong)SliceWidth.Minute and <= (ulong)SliceWidth.Month
            ? (SliceWidth)unit
            : throw new CommitFormatException($"A commit header names period unit {unit}, which is none of a minute, an hour, a day or a month.");

    private static PeriodChange ReadPeriodChange(ReadOnlySpan<byte> bytes)
    {
        long from = 0;
        ulong unit = 0;
        ProtoReader reader = new ProtoReader(bytes);
        while (reader.TryReadTag(out int field, out ProtoWireType wire))
        {
            switch (field, wire)
            {
                case (PeriodChangeField.From, ProtoWireType.Varint):
                    from = reader.ReadSInt64();
                    break;
                case (PeriodChangeField.Length, ProtoWireType.Varint):
                    unit = reader.ReadVarint();
                    break;
                default:
                    reader.SkipField(wire);
                    break;
            }
        }

        return new PeriodChange(from, PeriodUnit(unit));
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
