// What a commit object's header holds - docs/13-dataset.md §3: "the version, its parent, the
// schema, the clustering key, the write policy, the chunker parameters and the dataset's seed
// (§4.1), the compaction and retention settings, and the level table: per level, its top page
// inlined, and the pages below it too while the header stays under 256 KiB".
//
// PROTO3, LIKE THE INDEX OPTIONS OF 10 §4.1, and for the same two reasons: an unknown field is
// skipped rather than fatal, so a later version of this library can add one without a format
// break; and the repository already has a single-pass writer and a bounds-checked reader for it,
// so this codec is field numbers and nothing else.
//
// EVERY PAGE REFERENCE IS A FIXED 36 BYTES, which is not an optimisation but what makes the layout
// possible at all. The header sits at offset zero and names pages that lie AFTER it, so their
// offsets are only known once the header's length is; and the header's length depends on what it
// holds. A varint offset would make that circular. Fixed-width references break the circle: the
// header is serialized once to learn its length, then again with the offsets rebased, and the
// second serialization is the same length as the first by construction -- which the builder
// asserts rather than assumes.
using System;
using System.Collections.Generic;
using Vorticity.Serialization.Protobuf;

namespace Vorticity.Dataset;

/// <summary>The chunker's boundary parameters (§4.1).</summary>
/// <param name="MinBytes">The smallest chunk the boundary rule may cut.</param>
/// <param name="TargetBytes">The size it aims for.</param>
/// <param name="MaxBytes">The size at which it cuts whatever the rule says.</param>
public readonly record struct ChunkerSettings(int MinBytes, int TargetBytes, int MaxBytes);

/// <summary>The compaction settings (§5).</summary>
/// <param name="Levels">How many levels the dataset keeps.</param>
/// <param name="LevelTargetBytes">Level 1's target object size; each level multiplies it.</param>
/// <param name="Fanout">How many objects of a level make one of the level above.</param>
public readonly record struct CompactionSettings(int Levels, long LevelTargetBytes, int Fanout);

/// <summary>The retention settings (§10).</summary>
/// <param name="Versions">How many versions to keep beyond the current one.</param>
/// <param name="Seconds">How long to keep a superseded version, in seconds.</param>
public readonly record struct RetentionSettings(int Versions, long Seconds);

/// <summary>A page carried inside the header rather than referenced.</summary>
/// <param name="Reference">Where the page also lies, and what it hashes to.</param>
/// <param name="Bytes">Its content.</param>
public readonly record struct InlinedPage(PageReference Reference, ReadOnlyMemory<byte> Bytes);

/// <summary>One level of the dataset tree, as the header records it.</summary>
/// <param name="Level">Its number; 0 is the newest and smallest (§5).</param>
/// <param name="Entries">The leaves it holds.</param>
/// <param name="Top">Its top page.</param>
/// <param name="Inlined">
/// Pages carried in the header, top first: §3's "its top page inlined, and the pages below it too
/// while the header stays under 256 KiB".
/// </param>
public sealed record CommitLevel(int Level, long Entries, PageReference Top, IReadOnlyList<InlinedPage> Inlined)
{
    /// <summary>A level with no inlined page.</summary>
    /// <param name="level">Its number.</param>
    /// <param name="entries">The leaves it holds.</param>
    /// <param name="top">Its top page.</param>
    public CommitLevel(int level, long entries, PageReference top)
        : this(level, entries, top, [])
    {
    }
}

/// <summary>A commit object's header.</summary>
public sealed record CommitHeader
{
    /// <summary>This commit's version, which is also its key's inverse (§3).</summary>
    public required ulong Version { get; init; }

    /// <summary>The version this one was built on; 0 for the first.</summary>
    public ulong Parent { get; init; }

    /// <summary>The dataset's chunking seed (§4.1), fixed at creation.</summary>
    public ulong Seed { get; init; }

    /// <summary>The schema, as the core serializes a dtype. Opaque here.</summary>
    public ReadOnlyMemory<byte> Schema { get; init; }

    /// <summary>The clustering key's columns, in key order.</summary>
    public IReadOnlyList<string> ClusteringKey { get; init; } = [];

    /// <summary>The write policy the dataset applies to new objects. Opaque here.</summary>
    public ReadOnlyMemory<byte> WritePolicy { get; init; }

    /// <summary>The chunker's parameters.</summary>
    public ChunkerSettings Chunker { get; init; }

    /// <summary>The compaction settings.</summary>
    public CompactionSettings Compaction { get; init; }

    /// <summary>The retention settings.</summary>
    public RetentionSettings Retention { get; init; }

    /// <summary>When the commit was written, in milliseconds since the Unix epoch.</summary>
    public long CreatedAtUnixMilliseconds { get; init; }

    /// <summary>The levels, lowest first.</summary>
    public IReadOnlyList<CommitLevel> Levels { get; init; } = [];

    /// <summary>The field numbers, so that the writer and the reader cannot drift.</summary>
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
    }

    internal static class LevelField
    {
        internal const int Level = 1;
        internal const int Entries = 2;
        internal const int Top = 3;
        internal const int Inlined = 4;
    }

    internal static class InlinedField
    {
        internal const int Reference = 1;
        internal const int Bytes = 2;
    }

    /// <summary>
    /// Writes the header, rebasing every reference this commit owns by <paramref name="pagesStart"/>.
    /// </summary>
    /// <param name="writer">The destination.</param>
    /// <param name="pagesStart">
    /// Where the pages region begins, which is what a builder adds to the relative offsets it handed
    /// out. Pass 0 for the measuring pass.
    /// </param>
    internal void Write(ref ProtoWriter writer, long pagesStart)
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
            WriteLevel(ref writer, level, pagesStart);
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

    private void WriteLevel(ref ProtoWriter writer, CommitLevel level, long pagesStart)
    {
        ProtoWriter inner = new ProtoWriter();
        try
        {
            inner.WriteInt32(LevelField.Level, level.Level);
            inner.WriteInt64(LevelField.Entries, level.Entries);
            Span<byte> reference = stackalloc byte[PageReference.Bytes];
            Rebase(level.Top, pagesStart).Write(reference);
            inner.WriteBytes(LevelField.Top, reference);
            foreach (InlinedPage page in level.Inlined)
            {
                ProtoWriter inlined = new ProtoWriter();
                try
                {
                    Rebase(page.Reference, pagesStart).Write(reference);
                    inlined.WriteBytes(InlinedField.Reference, reference);
                    inlined.WriteBytes(InlinedField.Bytes, page.Bytes.Span);
                    inner.WriteBytes(LevelField.Inlined, inlined.WrittenSpan);
                }
                finally
                {
                    inlined.Dispose();
                }
            }

            writer.WriteBytes(Field.Levels, inner.WrittenSpan);
        }
        finally
        {
            inner.Dispose();
        }
    }

    /// <summary>
    /// Turns a reference this commit owns from relative to absolute; leaves every other alone.
    /// </summary>
    /// <param name="reference">The reference.</param>
    /// <param name="pagesStart">Where this object's pages region begins.</param>
    /// <remarks>
    /// A reference whose version is this commit's was handed out by this builder and carries an
    /// offset relative to the pages region, because the region's place is not known until the
    /// header is sized. A reference to an older version is already absolute in ITS object and must
    /// not be touched — that is §3's "a commit references the pages it did not change where they
    /// already are".
    /// </remarks>
    private PageReference Rebase(PageReference reference, long pagesStart) =>
        reference.Version == Version && reference.Exists
            ? reference with { Offset = reference.Offset + pagesStart }
            : reference;

    /// <summary>Reads a header written by <see cref="Write"/>.</summary>
    /// <param name="bytes">The header's bytes.</param>
    /// <returns>The header.</returns>
    /// <exception cref="CommitFormatException">The bytes are not a header.</exception>
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
                    default:
                        // 10 §4.1's rule: a field this version does not know is skipped, not fatal.
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
            ClusteringKey = clustering,
            WritePolicy = policy,
            Chunker = chunker,
            Compaction = compaction,
            Retention = retention,
            CreatedAtUnixMilliseconds = createdAt,
            Levels = levels,
        };
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
        PageReference top = PageReference.None;
        List<InlinedPage> inlined = [];
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
                case (LevelField.Top, ProtoWireType.LengthDelimited):
                    top = PageReference.Read(reader.ReadLengthDelimited());
                    break;
                case (LevelField.Inlined, ProtoWireType.LengthDelimited):
                    inlined.Add(ReadInlined(reader.ReadLengthDelimited()));
                    break;
                default:
                    reader.SkipField(wire);
                    break;
            }
        }

        return new CommitLevel(level, entries, top, inlined);
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
