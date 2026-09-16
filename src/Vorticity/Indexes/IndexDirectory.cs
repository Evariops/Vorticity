// The index directory of docs/10-indexes.md §4.1: one postscript metadata entry, key
// `vorticity.index`, whose segment is this message preceded by one version byte.
//
// WHY A METADATA ENTRY AND NOTHING ELSE (§3). A strict Rust 0.86.1 reader fails the open on an
// unknown aggregate id and on an unknown layout id, and ignores a metadata key it is not asked
// for. So the directory rides in the one place every reader already tolerates, and the runs it
// points at are file regions no layout references and no footer lists: a reader that does not ask
// for the key never touches a byte of them.
//
// AN INDEX IS A HINT, AND THE READ RULES SAY SO IN CODE (§4.1, docs/08-semantics.md §5). A
// directory whose row count disagrees with the file is ignored WHOLE -- a stale directory after a
// failed append proves nothing. An entry whose kind is unknown, whose options do not parse, whose
// runs overlap or step outside the file is ignored ALONE. Nothing here ever fails the open: the
// worst a lying index can do is cost pruning.
//
//   message IndexDirectory {
//     uint32 version = 1; uint64 row_count = 2; uint64 previous_eof = 3; bytes policy = 4;
//     repeated IndexEntry entries = 5;
//   }
//   message IndexEntry {
//     string kind = 1; repeated uint32 column_path = 2; uint64 block_len = 3; bytes options = 4;
//     repeated Run runs = 5;
//   }
//   message Run {
//     uint64 first_block = 1; uint32 block_count = 2; repeated Segment payload = 3;
//     repeated bytes payload_dtype = 4;
//     uint64 entry_count = 5;          // docs/12-index-reads.md §14's amendment
//     bytes options = 6;               // the per-segment bounds 10 §4.2 puts "in the run's options"
//   }
//   message Segment { uint64 offset = 1; uint32 length = 2; uint32 alignment_exponent = 3; }
using System;
using System.Collections.Generic;
using System.Text;
using Vorticity.Serialization.Protobuf;

namespace Vorticity.Indexes;

/// <summary>One payload region of a run.</summary>
/// <param name="Offset">Absolute file offset.</param>
/// <param name="Length">Length in bytes.</param>
/// <param name="AlignmentExponent">The region's alignment is <c>1 &lt;&lt; AlignmentExponent</c>.</param>
public readonly record struct IndexSegment(ulong Offset, uint Length, byte AlignmentExponent);

/// <summary>
/// One immutable run: the filters or keys that cover a contiguous range of blocks
/// (docs/10-indexes.md §4.2).
/// </summary>
/// <param name="FirstBlock">The first block covered.</param>
/// <param name="BlockCount">How many consecutive blocks.</param>
/// <param name="Payload">The file regions, in kind-defined order.</param>
/// <param name="PayloadDTypes">
/// The serialized <c>DType</c> (dtype.fbs) of each payload array, parallel to
/// <paramref name="Payload"/>; empty for a kind whose payload is not an array.
/// </param>
/// <param name="EntryCount">
/// The entries of a locating run, so that a cursor's <c>EntryCount</c> and <c>Explain</c> cost no
/// payload read (docs/12-index-reads.md §14's amendment to §4.1); <c>0</c> for a skipping kind.
/// </param>
/// <param name="Options">
/// Kind-defined bytes for this run alone: the per-segment bounds of a locating run's blocked
/// payload (docs/10-indexes.md §4.2); empty otherwise.
/// </param>
public sealed record IndexRun(
    ulong FirstBlock,
    uint BlockCount,
    IReadOnlyList<IndexSegment> Payload,
    IReadOnlyList<byte[]> PayloadDTypes,
    ulong EntryCount = 0,
    byte[]? Options = null)
{
    /// <summary>The run's options, never null.</summary>
    public ReadOnlySpan<byte> OptionBytes => Options ?? [];

    /// <summary>One past the last block covered.</summary>
    public ulong EndBlock => FirstBlock + BlockCount;
}

/// <summary>One index over one column.</summary>
/// <param name="Kind">The kind name (<see cref="IndexKinds"/>).</param>
/// <param name="ColumnPath">Field indices from the root struct; empty for the root column.</param>
/// <param name="BlockLength">Rows per block: the zone length.</param>
/// <param name="Options">Kind-defined, self-versioned bytes.</param>
/// <param name="Runs">In block order, disjoint.</param>
public sealed record IndexEntry(
    string Kind,
    IReadOnlyList<uint> ColumnPath,
    ulong BlockLength,
    byte[] Options,
    IReadOnlyList<IndexRun> Runs);

/// <summary>The index directory of one file (docs/10-indexes.md §4.1).</summary>
/// <param name="RowCount">The file's row count when the directory was written.</param>
/// <param name="PreviousEof">The file length before the append that wrote it; 0 for a first write.</param>
/// <param name="Policy">The policy the file was written under, so an append needs no options.</param>
/// <param name="Entries">The indexes that survived their reader-side checks.</param>
public sealed record IndexDirectory(
    ulong RowCount,
    ulong PreviousEof,
    WritePolicy Policy,
    IReadOnlyList<IndexEntry> Entries)
{
    /// <summary>The postscript metadata key the directory is stored under.</summary>
    public const string MetadataKey = "vorticity.index";

    /// <summary>The one version this library writes and reads.</summary>
    internal const byte FormatVersion = 1;

    /// <summary>The default index budget, which is not written.</summary>
    internal const int DefaultBudgetPerMille = 100;

    /// <summary>
    /// The share of the data bytes the file's indexes were allowed, in parts per thousand, so that
    /// an append without options builds under the same budget (field 6; absent means the default).
    /// </summary>
    public int BudgetPerMille { get; init; } = DefaultBudgetPerMille;

    /// <summary>For a sidecar: the length of the file it indexes (field 7); 0 in a file's own directory.</summary>
    public ulong FileLength { get; init; }

    /// <summary>For a sidecar: the SHA-256 of the file it indexes (field 8); null in a file's own directory.</summary>
    public byte[]? FileSha256 { get; init; }

    /// <summary>
    /// For a sidecar: the array encodings its payloads name, by index (field 9) -- a sidecar's
    /// payloads cannot use the data file's footer, which it does not rewrite.
    /// </summary>
    public IReadOnlyList<string>? ArrayEncodings { get; init; }

    // Field numbers, spelled once.
    private const int DirVersion = 1;
    private const int DirRowCount = 2;
    private const int DirPreviousEof = 3;
    private const int DirPolicy = 4;
    private const int DirEntries = 5;
    private const int DirBudget = 6;
    private const int DirFileLength = 7;
    private const int DirFileSha256 = 8;
    private const int DirArrayEncodings = 9;
    private const int EntryKind = 1;
    private const int EntryColumnPath = 2;
    private const int EntryBlockLen = 3;
    private const int EntryOptions = 4;
    private const int EntryRuns = 5;
    private const int RunFirstBlock = 1;
    private const int RunBlockCount = 2;
    private const int RunPayload = 3;
    private const int RunPayloadDType = 4;
    private const int RunEntryCount = 5;
    private const int RunOptions = 6;
    private const int SegOffset = 1;
    private const int SegLength = 2;
    private const int SegAlignment = 3;
    private const int PolicyDefault = 1;
    private const int PolicyColumns = 2;
    private const int PolicyKeys = 3;
    private const int ColumnKeyPaths = 10;
    private const int ColumnPath = 1;
    private const int ColumnKind = 2;
    private const int ColumnFpp = 3;
    private const int ColumnResolutions = 4;
    private const int ColumnMaxBlocks = 5;
    private const int ColumnMinDistinct = 6;
    private const int ColumnHash = 7;
    private const int ColumnCaseInsensitive = 8;
    private const int ColumnSegmentEntries = 9;

    /// <summary>The key as UTF-8, for <c>VortexFile.TryGetMetadataIndex</c>.</summary>
    internal static ReadOnlySpan<byte> MetadataKeyUtf8 => "vorticity.index"u8;

    /// <summary>Serializes the directory: the version byte, then the message.</summary>
    /// <returns>The segment's bytes.</returns>
    public byte[] ToBytes()
    {
        ProtoWriter writer = new ProtoWriter();
        try
        {
            writer.WriteRawBytes([FormatVersion]);
            writer.WriteUInt32Always(DirVersion, FormatVersion);
            writer.WriteUInt64Always(DirRowCount, RowCount);
            writer.WriteUInt64(DirPreviousEof, PreviousEof);
            using (ProtoWriter.MessageScope policy = writer.BeginMessage(DirPolicy))
            {
                WritePolicyBody(ref writer, Policy);
            }

            foreach (IndexEntry entry in Entries)
            {
                using ProtoWriter.MessageScope scope = writer.BeginMessage(DirEntries);
                WriteEntry(ref writer, entry);
            }

            if (BudgetPerMille != DefaultBudgetPerMille)
            {
                writer.WriteUInt32Always(DirBudget, (uint)Math.Max(BudgetPerMille, 0));
            }

            writer.WriteUInt64(DirFileLength, FileLength);
            if (FileSha256 is { } sha)
            {
                writer.WriteBytes(DirFileSha256, sha);
            }

            foreach (string id in ArrayEncodings ?? [])
            {
                writer.WriteStringAlways(DirArrayEncodings, id);
            }

            return writer.WrittenSpan.ToArray();
        }
        finally
        {
            writer.Dispose();
        }
    }

    private static void WritePolicyBody(ref ProtoWriter writer, WritePolicy policy)
    {
        using (ProtoWriter.MessageScope fallback = writer.BeginMessage(PolicyDefault))
        {
            WriteColumnPolicy(ref writer, string.Empty, policy.Default);
        }

        // Ordinal order, so that the same policy always serializes to the same bytes.
        List<string> paths = [.. policy.Columns.Keys];
        paths.Sort(StringComparer.Ordinal);
        foreach (string path in paths)
        {
            using ProtoWriter.MessageScope column = writer.BeginMessage(PolicyColumns);
            WriteColumnPolicy(ref writer, path, policy.Columns[path]);
        }

        // Composite keys in the order they were declared: the order is part of the policy.
        foreach (CompositeKeyPolicy key in policy.Keys)
        {
            using ProtoWriter.MessageScope scope = writer.BeginMessage(PolicyKeys);
            WriteColumnPolicy(ref writer, string.Empty, key.Policy);
            foreach (string path in key.Paths)
            {
                writer.WriteStringAlways(ColumnKeyPaths, path);
            }
        }
    }

    /// <remarks>
    /// AN OPTION AT ITS DEFAULT IS NOT WRITTEN: every file carries its directory since `Auto` became
    /// the default, and the defaults are the reader's as much as the writer's (a zero reads back as
    /// the default). `min_distinct` is the one option whose zero is a request, so it goes out
    /// whenever it differs from its default.
    /// </remarks>
    private static void WriteColumnPolicy(ref ProtoWriter writer, string path, IndexPolicy policy)
    {
        writer.WriteString(ColumnPath, path);
        writer.WriteUInt32(ColumnKind, (uint)policy.Kind);
        WriteIfNot(ref writer, ColumnFpp, policy.FalsePositivePpm, IndexPolicy.DefaultFalsePositivePpm);
        WriteIfNot(ref writer, ColumnResolutions, policy.Resolutions, IndexPolicy.DefaultResolutions);
        WriteIfNot(ref writer, ColumnMaxBlocks, policy.MaxBlocks, IndexPolicy.DefaultMaxBlocks);
        if (policy.MinDistinct != IndexPolicy.DefaultMinDistinct)
        {
            writer.WriteUInt32Always(ColumnMinDistinct, (uint)policy.MinDistinct);
        }

        writer.WriteUInt32(ColumnHash, (uint)policy.Hash);
        writer.WriteBool(ColumnCaseInsensitive, policy.CaseInsensitive);
        WriteIfNot(ref writer, ColumnSegmentEntries, policy.SegmentEntries, IndexPolicy.DefaultSegmentEntries);
    }

    private static void WriteIfNot(ref ProtoWriter writer, int field, int value, int fallback)
    {
        if (value != fallback)
        {
            writer.WriteUInt32Always(field, (uint)value);
        }
    }

    private static void WriteEntry(ref ProtoWriter writer, IndexEntry entry)
    {
        writer.WriteString(EntryKind, entry.Kind);
        foreach (uint field in entry.ColumnPath)
        {
            writer.WriteUInt32Always(EntryColumnPath, field);
        }

        writer.WriteUInt64Always(EntryBlockLen, entry.BlockLength);
        writer.WriteBytes(EntryOptions, entry.Options);
        foreach (IndexRun run in entry.Runs)
        {
            using ProtoWriter.MessageScope scope = writer.BeginMessage(EntryRuns);
            writer.WriteUInt64Always(RunFirstBlock, run.FirstBlock);
            writer.WriteUInt32Always(RunBlockCount, run.BlockCount);
            foreach (IndexSegment segment in run.Payload)
            {
                using ProtoWriter.MessageScope payload = writer.BeginMessage(RunPayload);
                writer.WriteUInt64Always(SegOffset, segment.Offset);
                writer.WriteUInt32Always(SegLength, segment.Length);
                writer.WriteUInt32(SegAlignment, segment.AlignmentExponent);
            }

            foreach (byte[] dtype in run.PayloadDTypes)
            {
                writer.WriteBytesAlways(RunPayloadDType, dtype);
            }

            writer.WriteUInt64(RunEntryCount, run.EntryCount);
            writer.WriteBytes(RunOptions, run.OptionBytes);
        }
    }

    /// <summary>
    /// Parses a directory and applies the reader rules of docs/10-indexes.md §4.1.
    /// </summary>
    /// <param name="bytes">The metadata segment.</param>
    /// <param name="fileRowCount">The file's own row count.</param>
    /// <param name="dataEnd">
    /// The first byte a run may not reach: the footer's start. A run segment lies inside the file
    /// and before the footer, or its entry is ignored.
    /// </param>
    /// <param name="directory">The directory, holding only the entries that passed.</param>
    /// <param name="reason">Why the whole directory was refused, when it was.</param>
    /// <returns>
    /// Whether the directory is usable. <see langword="false"/> is never an error: the file reads
    /// exactly as if it carried no index.
    /// </returns>
    public static bool TryParse(
        ReadOnlySpan<byte> bytes, ulong fileRowCount, ulong dataEnd,
        out IndexDirectory? directory, out string? reason)
    {
        directory = null;
        if (bytes.IsEmpty || bytes[0] != FormatVersion)
        {
            reason = bytes.IsEmpty
                ? "the directory segment is empty"
                : $"directory format version {bytes[0]} is not the {FormatVersion} this reader knows";
            return false;
        }

        try
        {
            return TryParseBody(bytes[1..], fileRowCount, dataEnd, out directory, out reason);
        }
        catch (VortexFormatException e)
        {
            // A malformed message is a malformed HINT: the directory is ignored and the file is
            // read without it (§4.1). The reason carries the parser's words.
            reason = "the directory does not parse: " + e.Message;
            directory = null;
            return false;
        }
    }

    private static bool TryParseBody(
        ReadOnlySpan<byte> body, ulong fileRowCount, ulong dataEnd,
        out IndexDirectory? directory, out string? reason)
    {
        directory = null;
        ProtoReader reader = new ProtoReader(body);
        uint version = 0;
        ulong rowCount = 0;
        ulong previousEof = 0;
        WritePolicy policy = WritePolicy.None;
        List<IndexEntry> entries = [];
        uint budget = DefaultBudgetPerMille;
        ulong fileLength = 0;
        byte[]? sha = null;
        List<string>? encodings = null;
        while (reader.TryReadTag(out int field, out ProtoWireType wire))
        {
            switch (field)
            {
                case DirBudget when wire == ProtoWireType.Varint:
                    budget = reader.ReadVarint32();
                    break;
                case DirFileLength when wire == ProtoWireType.Varint:
                    fileLength = reader.ReadVarint();
                    break;
                case DirFileSha256 when wire == ProtoWireType.LengthDelimited:
                    sha = reader.ReadLengthDelimited().ToArray();
                    break;
                case DirArrayEncodings when wire == ProtoWireType.LengthDelimited:
                    (encodings ??= []).Add(Encoding.UTF8.GetString(reader.ReadLengthDelimited()));
                    break;
                case DirVersion when wire == ProtoWireType.Varint:
                    version = reader.ReadVarint32();
                    break;
                case DirRowCount when wire == ProtoWireType.Varint:
                    rowCount = reader.ReadVarint();
                    break;
                case DirPreviousEof when wire == ProtoWireType.Varint:
                    previousEof = reader.ReadVarint();
                    break;
                case DirPolicy when wire == ProtoWireType.LengthDelimited:
                    policy = ReadPolicy(reader.ReadMessage());
                    break;
                case DirEntries when wire == ProtoWireType.LengthDelimited:
                    if (TryReadEntry(reader.ReadMessage(), out IndexEntry? entry))
                    {
                        entries.Add(entry!);
                    }

                    break;
                default:
                    reader.SkipField(wire);
                    break;
            }
        }

        if (version != FormatVersion)
        {
            reason = $"the directory declares version {version}, not {FormatVersion}";
            return false;
        }

        if (rowCount != fileRowCount)
        {
            reason = $"the directory was written for {rowCount} rows and the file has {fileRowCount}: " +
                "a stale directory after a failed append proves nothing";
            return false;
        }

        List<IndexEntry> kept = new List<IndexEntry>(entries.Count);
        foreach (IndexEntry entry in entries)
        {
            if (RunsAreSound(entry, dataEnd))
            {
                kept.Add(entry);
            }
        }

        directory = new IndexDirectory(rowCount, previousEof, policy, kept)
        {
            BudgetPerMille = (int)Math.Min(budget, int.MaxValue),
            FileLength = fileLength,
            FileSha256 = sha,
            ArrayEncodings = encodings,
        };
        reason = null;
        return true;
    }

    /// <summary>
    /// Runs disjoint and in order, each segment inside the file and before the footer, and a
    /// payload wherever the kind needs one. A failure costs the ENTRY, never the file.
    /// </summary>
    private static bool RunsAreSound(IndexEntry entry, ulong dataEnd)
    {
        bool needsPayload = IndexKinds.HasPayload(entry.Kind);
        ulong nextBlock = 0;
        foreach (IndexRun run in entry.Runs)
        {
            if (run.BlockCount == 0 || run.FirstBlock < nextBlock)
            {
                return false;
            }

            if (needsPayload && run.Payload.Count == 0)
            {
                return false;
            }

            if (run.PayloadDTypes.Count != 0 && run.PayloadDTypes.Count != run.Payload.Count)
            {
                return false;
            }

            foreach (IndexSegment segment in run.Payload)
            {
                ulong end = segment.Offset + segment.Length;
                if (end < segment.Offset || end > dataEnd || segment.AlignmentExponent > 16)
                {
                    return false;
                }
            }

            nextBlock = run.EndBlock;
        }

        return true;
    }

    private static bool TryReadEntry(ProtoReader reader, out IndexEntry? entry)
    {
        entry = null;
        string? kind = null;
        List<uint> path = [];
        ulong blockLength = 0;
        byte[] options = [];
        List<IndexRun> runs = [];
        while (reader.TryReadTag(out int field, out ProtoWireType wire))
        {
            switch (field)
            {
                case EntryKind when wire == ProtoWireType.LengthDelimited:
                    kind = Encoding.UTF8.GetString(reader.ReadLengthDelimited());
                    break;
                case EntryColumnPath when wire == ProtoWireType.Varint:
                    path.Add(reader.ReadVarint32());
                    break;
                case EntryColumnPath when wire == ProtoWireType.LengthDelimited:
                    // A packed repeated field: protobuf readers must accept both forms.
                    ProtoReader packed = reader.ReadMessage();
                    while (!packed.End)
                    {
                        path.Add(packed.ReadVarint32());
                    }

                    break;
                case EntryBlockLen when wire == ProtoWireType.Varint:
                    blockLength = reader.ReadVarint();
                    break;
                case EntryOptions when wire == ProtoWireType.LengthDelimited:
                    options = reader.ReadLengthDelimited().ToArray();
                    break;
                case EntryRuns when wire == ProtoWireType.LengthDelimited:
                    runs.Add(ReadRun(reader.ReadMessage()));
                    break;
                default:
                    reader.SkipField(wire);
                    break;
            }
        }

        // An unknown kind is ignored, not rejected (§4.1): a newer writer's index costs an older
        // reader nothing but the bytes.
        if (kind is null || !IndexKinds.IsKnown(kind) || blockLength == 0)
        {
            return false;
        }

        entry = new IndexEntry(kind, path, blockLength, options, runs);
        return true;
    }

    private static IndexRun ReadRun(ProtoReader reader)
    {
        ulong first = 0;
        uint count = 0;
        ulong entries = 0;
        byte[]? options = null;
        List<IndexSegment> payload = [];
        List<byte[]> dtypes = [];
        while (reader.TryReadTag(out int field, out ProtoWireType wire))
        {
            switch (field)
            {
                case RunFirstBlock when wire == ProtoWireType.Varint:
                    first = reader.ReadVarint();
                    break;
                case RunBlockCount when wire == ProtoWireType.Varint:
                    count = reader.ReadVarint32();
                    break;
                case RunPayload when wire == ProtoWireType.LengthDelimited:
                    payload.Add(ReadSegment(reader.ReadMessage()));
                    break;
                case RunPayloadDType when wire == ProtoWireType.LengthDelimited:
                    dtypes.Add(reader.ReadLengthDelimited().ToArray());
                    break;
                case RunEntryCount when wire == ProtoWireType.Varint:
                    entries = reader.ReadVarint();
                    break;
                case RunOptions when wire == ProtoWireType.LengthDelimited:
                    options = reader.ReadLengthDelimited().ToArray();
                    break;
                default:
                    reader.SkipField(wire);
                    break;
            }
        }

        return new IndexRun(first, count, payload, dtypes, entries, options);
    }

    private static IndexSegment ReadSegment(ProtoReader reader)
    {
        ulong offset = 0;
        uint length = 0;
        uint alignment = 0;
        while (reader.TryReadTag(out int field, out ProtoWireType wire))
        {
            switch (field)
            {
                case SegOffset when wire == ProtoWireType.Varint:
                    offset = reader.ReadVarint();
                    break;
                case SegLength when wire == ProtoWireType.Varint:
                    length = reader.ReadVarint32();
                    break;
                case SegAlignment when wire == ProtoWireType.Varint:
                    alignment = reader.ReadVarint32();
                    break;
                default:
                    reader.SkipField(wire);
                    break;
            }
        }

        // An exponent past a byte is out of range; RunsAreSound refuses anything above 16, so
        // saturating here cannot turn a bad value into an accepted one.
        return new IndexSegment(offset, length, (byte)Math.Min(alignment, byte.MaxValue));
    }

    private static WritePolicy ReadPolicy(ProtoReader reader)
    {
        IndexPolicy fallback = IndexPolicy.None;
        Dictionary<string, IndexPolicy> columns = new Dictionary<string, IndexPolicy>(StringComparer.Ordinal);
        List<CompositeKeyPolicy> keys = [];
        while (reader.TryReadTag(out int field, out ProtoWireType wire))
        {
            switch (field)
            {
                case PolicyDefault when wire == ProtoWireType.LengthDelimited:
                    fallback = ReadColumnPolicy(reader.ReadMessage(), out _, out _);
                    break;
                case PolicyColumns when wire == ProtoWireType.LengthDelimited:
                    IndexPolicy column = ReadColumnPolicy(reader.ReadMessage(), out string path, out _);
                    if (path.Length > 0)
                    {
                        columns[path] = column;
                    }

                    break;
                case PolicyKeys when wire == ProtoWireType.LengthDelimited:
                    IndexPolicy key = ReadColumnPolicy(reader.ReadMessage(), out _, out List<string> paths);
                    if (paths.Count >= 2 && key.Kind == IndexPolicyKind.SortedRuns)
                    {
                        keys.Add(new CompositeKeyPolicy(paths, key));
                    }

                    break;
                default:
                    reader.SkipField(wire);
                    break;
            }
        }

        return WritePolicy.FromStored(fallback, columns, keys);
    }

    private static IndexPolicy ReadColumnPolicy(ProtoReader reader, out string path, out List<string> keyPaths)
    {
        path = string.Empty;
        keyPaths = [];
        uint kind = 0;
        uint fpp = 0;
        uint resolutions = 0;
        uint maxBlocks = 0;
        long minDistinct = -1;
        uint hash = 0;
        uint segmentEntries = 0;
        bool caseInsensitive = false;
        while (reader.TryReadTag(out int field, out ProtoWireType wire))
        {
            switch (field)
            {
                case ColumnPath when wire == ProtoWireType.LengthDelimited:
                    path = Encoding.UTF8.GetString(reader.ReadLengthDelimited());
                    break;
                case ColumnKind when wire == ProtoWireType.Varint:
                    kind = reader.ReadVarint32();
                    break;
                case ColumnFpp when wire == ProtoWireType.Varint:
                    fpp = reader.ReadVarint32();
                    break;
                case ColumnResolutions when wire == ProtoWireType.Varint:
                    resolutions = reader.ReadVarint32();
                    break;
                case ColumnMaxBlocks when wire == ProtoWireType.Varint:
                    maxBlocks = reader.ReadVarint32();
                    break;
                case ColumnMinDistinct when wire == ProtoWireType.Varint:
                    minDistinct = reader.ReadVarint32();
                    break;
                case ColumnHash when wire == ProtoWireType.Varint:
                    hash = reader.ReadVarint32();
                    break;
                case ColumnCaseInsensitive when wire == ProtoWireType.Varint:
                    caseInsensitive = reader.ReadBool();
                    break;
                case ColumnSegmentEntries when wire == ProtoWireType.Varint:
                    segmentEntries = reader.ReadVarint32();
                    break;
                case ColumnKeyPaths when wire == ProtoWireType.LengthDelimited:
                    keyPaths.Add(Encoding.UTF8.GetString(reader.ReadLengthDelimited()));
                    break;
                default:
                    reader.SkipField(wire);
                    break;
            }
        }

        return IndexPolicy.FromStored(
            (int)Math.Min(kind, int.MaxValue),
            (int)Math.Min(fpp, int.MaxValue),
            (int)Math.Min(resolutions, int.MaxValue),
            (int)Math.Min(maxBlocks, int.MaxValue),
            (int)Math.Min(minDistinct, int.MaxValue),
            (int)Math.Min(hash, int.MaxValue),
            caseInsensitive,
            (int)Math.Min(segmentEntries, int.MaxValue));
    }
}
