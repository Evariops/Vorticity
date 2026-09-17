// The options of the two locating kinds, `vorticity.postings.blocks.v1` and
// `vorticity.sorted.runs.v1` (docs/10-indexes.md §6.1, §6.2, §4.2).
//
//   message KeyIndexOptions {           // the entry's options
//     uint32 version = 1;                // 1
//     uint32 segment_entries = 2;        // 10 §4.2's payload_block_rows: the most entries a segment holds
//     bool   case_insensitive = 3;       // postings.ngram3 only: trigrams ASCII-lower-cased
//   }
//   message KeyRunOptions {             // each run's options
//     uint32 version = 1;                // 1
//     repeated KeySegment segments = 2;  // in key order
//   }
//   message KeySegment {
//     uint64 entries = 1;                // keys for postings, (key, row) pairs for sorted runs
//     bytes  min = 2;                    // the segment's first key, as the column's key bytes
//     bytes  max = 3;                    // its last
//   }
//
// A RUN IS BLOCKED LIKE DATA. Its payload is `stride` arrays per segment -- postings: keys, the
// offsets into the block lists, the block lists; sorted runs: keys, rows -- and a probe reads only
// the segments whose [min, max] can hold its key: "zones do the job of B-tree leaves and zone maps
// the job of internal nodes" (#9024), through the directory rather than a layout.
using System;
using System.Collections.Generic;
using Vorticity.Serialization.Protobuf;

namespace Vorticity.Indexes;

/// <summary>One segment of a locating run.</summary>
/// <param name="Entries">Its entries.</param>
/// <param name="Min">Its first key's bytes.</param>
/// <param name="Max">Its last key's bytes.</param>
internal sealed record KeySegment(ulong Entries, byte[] Min, byte[] Max);

/// <summary>The codecs of a locating entry's and run's options.</summary>
internal static class KeyRunOptions
{
    /// <summary>10 §4.2's default `payload_block_rows`.</summary>
    internal const int DefaultSegmentEntries = 65_536;

    private const uint Version = 1;

    /// <summary>Payload arrays per segment of a postings run: keys, offsets, blocks.</summary>
    internal const int PostingsStride = 3;

    /// <summary>Payload arrays per segment of a sorted run: keys, rows.</summary>
    internal const int SortedStride = 2;

    /// <summary>
    /// The serialized dtype of a sorted run's rows written at 64 bits, which a run spanning 2³² rows
    /// or more carries (13 §6.1); any other rows are 32-bit.
    /// </summary>
    private static readonly byte[] WideRowsDType = SerializeU64();

    private static byte[] SerializeU64()
    {
        Vorticity.Types.DTypeArena types = new Vorticity.Types.DTypeArena();
        return Vorticity.Types.Serialization.DTypeFlatBuffers.Serialize(
            types.Primitive(Vorticity.Types.PType.U64, Vorticity.Types.Nullability.NonNullable));
    }

    /// <summary>Whether a sorted run's rows array at <paramref name="payload"/> is 64-bit.</summary>
    /// <param name="run">The run.</param>
    /// <param name="payload">The rows array's index in the run's payload.</param>
    internal static bool WideRows(IndexRun run, int payload) =>
        payload < run.PayloadDTypes.Count && run.PayloadDTypes[payload].AsSpan().SequenceEqual(WideRowsDType);

    /// <summary>Whether a serialized dtype is the one 64-bit rows are written with.</summary>
    /// <param name="dtype">The dtype's bytes.</param>
    internal static bool IsWideRowsDType(ReadOnlySpan<byte> dtype) => dtype.SequenceEqual(WideRowsDType);

    /// <summary>The version of a run's options whose segment table is in fence pages (13 §6.3).</summary>
    private const uint PagedVersion = 2;

    /// <summary>Serializes a paged run's options: its inline root and its arrays' dtypes.</summary>
    /// <param name="root">The top page of the fence tree.</param>
    /// <param name="dtypes">The serialized dtype of each array of a segment.</param>
    internal static byte[] PagedRun(FencePage root, IReadOnlyList<byte[]> dtypes)
    {
        ProtoWriter writer = new ProtoWriter();
        try
        {
            writer.WriteUInt32Always(1, PagedVersion);
            using (ProtoWriter.MessageScope scope = writer.BeginMessage(3))
            {
                root.Write(ref writer);
            }

            foreach (byte[] dtype in dtypes)
            {
                writer.WriteBytesAlways(4, dtype);
            }

            return writer.WrittenSpan.ToArray();
        }
        finally
        {
            writer.Dispose();
        }
    }

    /// <summary>Whether a run's options are a paged run's (version 2), without parsing the root.</summary>
    /// <param name="options">The run's options.</param>
    internal static bool IsPaged(ReadOnlySpan<byte> options)
    {
        try
        {
            ProtoReader reader = new ProtoReader(options);
            while (reader.TryReadTag(out int field, out ProtoWireType wire))
            {
                if (field == 1 && wire == ProtoWireType.Varint)
                {
                    return reader.ReadVarint32() == PagedVersion;
                }

                reader.SkipField(wire);
            }
        }
        catch (VortexFormatException)
        {
            return false;
        }

        return false;
    }

    /// <summary>
    /// The serialized dtype of a run's keys array: the directory's, or a paged run's own; empty when
    /// neither says.
    /// </summary>
    /// <param name="run">The run.</param>
    internal static byte[] KeyDType(IndexRun run)
    {
        if (run.PayloadDTypes.Count > 0)
        {
            return run.PayloadDTypes[0];
        }

        try
        {
            ProtoReader reader = new ProtoReader(run.OptionBytes);
            while (reader.TryReadTag(out int field, out ProtoWireType wire))
            {
                if (field == 4 && wire == ProtoWireType.LengthDelimited)
                {
                    return reader.ReadLengthDelimited().ToArray();
                }

                reader.SkipField(wire);
            }
        }
        catch (VortexFormatException)
        {
            return [];
        }

        return [];
    }

    /// <summary>Parses a paged run's options; false for any other.</summary>
    /// <param name="bytes">The run's options.</param>
    /// <param name="stride">The arrays of a segment.</param>
    /// <param name="layout">The keys' layout.</param>
    /// <param name="root">The inline root.</param>
    /// <param name="dtypes">The arrays' dtypes.</param>
    internal static bool TryParsePagedRun(
        ReadOnlySpan<byte> bytes, int stride, KeyLayout layout, out FencePage? root, out byte[][]? dtypes)
    {
        root = null;
        dtypes = null;
        try
        {
            ProtoReader reader = new ProtoReader(bytes);
            uint version = 0;
            FencePage? page = null;
            List<byte[]> types = [];
            while (reader.TryReadTag(out int field, out ProtoWireType wire))
            {
                switch (field)
                {
                    case 1 when wire == ProtoWireType.Varint:
                        version = reader.ReadVarint32();
                        break;
                    case 3 when wire == ProtoWireType.LengthDelimited:
                        page = FencePage.Read(reader.ReadMessage(), stride, null, layout);
                        break;
                    case 4 when wire == ProtoWireType.LengthDelimited:
                        types.Add(reader.ReadLengthDelimited().ToArray());
                        break;
                    default:
                        reader.SkipField(wire);
                        break;
                }
            }

            if (version != PagedVersion || page is null)
            {
                return false;
            }

            root = page;
            dtypes = [.. types];
            return true;
        }
        catch (VortexFormatException)
        {
            return false;
        }
    }

    /// <summary>The stride of a kind, or 0 for a kind that is not a locating one.</summary>
    /// <param name="kind">The kind name.</param>
    internal static int StrideOf(string kind) => kind switch
    {
        IndexKinds.PostingsBlocks or IndexKinds.PostingsNgram3 => PostingsStride,
        IndexKinds.SortedRuns => SortedStride,
        _ => 0,
    };

    /// <summary>Serializes an entry's options.</summary>
    /// <param name="segmentEntries">The segment size the runs were cut at.</param>
    /// <param name="caseInsensitive">For trigram postings, whether trigrams were ASCII-lower-cased.</param>
    /// <param name="keyColumns">
    /// For a composite key (10 §6.5), each key column's field indices from the root, in key order;
    /// the entry's own <c>column_path</c> is then empty, which a reader that does not know this field
    /// resolves to the root struct and ignores.
    /// </param>
    /// <param name="keyFormat">For a composite key, what its bytes follow (<see cref="IKeyEncoder.Format"/>).</param>
    internal static byte[] Entry(
        int segmentEntries, bool caseInsensitive = false, IReadOnlyList<uint[]>? keyColumns = null, string? keyFormat = null)
    {
        ProtoWriter writer = new ProtoWriter();
        try
        {
            writer.WriteUInt32Always(1, Version);
            writer.WriteUInt32Always(2, (uint)segmentEntries);
            writer.WriteBool(3, caseInsensitive);
            foreach (uint[] path in keyColumns ?? [])
            {
                using ProtoWriter.MessageScope column = writer.BeginMessage(4);
                foreach (uint index in path)
                {
                    writer.WriteUInt32Always(1, index);
                }
            }

            if (keyFormat is not null)
            {
                writer.WriteStringAlways(5, keyFormat);
            }

            return writer.WrittenSpan.ToArray();
        }
        finally
        {
            writer.Dispose();
        }
    }

    /// <summary>Parses an entry's options.</summary>
    /// <param name="bytes">The options.</param>
    /// <param name="segmentEntries">The segment size.</param>
    /// <returns>Whether they are usable.</returns>
    internal static bool TryParseEntry(ReadOnlySpan<byte> bytes, out int segmentEntries) =>
        TryParseEntry(bytes, out segmentEntries, out _, out _);

    /// <summary>Parses an entry's options.</summary>
    /// <param name="bytes">The options.</param>
    /// <param name="segmentEntries">The segment size.</param>
    /// <param name="caseInsensitive">Whether trigrams were folded.</param>
    /// <returns>Whether they are usable.</returns>
    internal static bool TryParseEntry(ReadOnlySpan<byte> bytes, out int segmentEntries, out bool caseInsensitive) =>
        TryParseEntry(bytes, out segmentEntries, out caseInsensitive, out _);

    /// <summary>Parses an entry's options.</summary>
    /// <param name="bytes">The options.</param>
    /// <param name="segmentEntries">The segment size.</param>
    /// <param name="caseInsensitive">Whether trigrams were folded.</param>
    /// <param name="keyColumns">A composite key's columns, empty for a single column.</param>
    /// <returns>Whether they are usable.</returns>
    internal static bool TryParseEntry(
        ReadOnlySpan<byte> bytes, out int segmentEntries, out bool caseInsensitive, out List<uint[]> keyColumns) =>
        TryParseEntry(bytes, out segmentEntries, out caseInsensitive, out keyColumns, out _);

    /// <summary>Parses an entry's options, a composite key's format included.</summary>
    /// <param name="bytes">The options.</param>
    /// <param name="segmentEntries">The segment size.</param>
    /// <param name="caseInsensitive">Whether trigrams were folded.</param>
    /// <param name="keyColumns">A composite key's columns, empty for a single column.</param>
    /// <param name="keyFormat">What a composite key's bytes follow, or null.</param>
    /// <returns>Whether they are usable.</returns>
    internal static bool TryParseEntry(
        ReadOnlySpan<byte> bytes, out int segmentEntries, out bool caseInsensitive, out List<uint[]> keyColumns,
        out string? keyFormat)
    {
        segmentEntries = 0;
        caseInsensitive = false;
        keyColumns = [];
        keyFormat = null;
        try
        {
            ProtoReader reader = new ProtoReader(bytes);
            uint version = 0;
            uint entries = 0;
            while (reader.TryReadTag(out int field, out ProtoWireType wire))
            {
                switch (field)
                {
                    case 1 when wire == ProtoWireType.Varint:
                        version = reader.ReadVarint32();
                        break;
                    case 2 when wire == ProtoWireType.Varint:
                        entries = reader.ReadVarint32();
                        break;
                    case 3 when wire == ProtoWireType.Varint:
                        caseInsensitive = reader.ReadBool();
                        break;
                    case 4 when wire == ProtoWireType.LengthDelimited:
                        keyColumns.Add(ReadPath(reader.ReadMessage()));
                        break;
                    case 5 when wire == ProtoWireType.LengthDelimited:
                        keyFormat = System.Text.Encoding.UTF8.GetString(reader.ReadLengthDelimited());
                        break;
                    default:
                        reader.SkipField(wire);
                        break;
                }
            }

            if (version != Version || entries == 0 || entries > int.MaxValue)
            {
                return false;
            }

            segmentEntries = (int)entries;
            return true;
        }
        catch (VortexFormatException)
        {
            return false;
        }
    }

    /// <summary>One key column's field indices.</summary>
    private static uint[] ReadPath(ProtoReader reader)
    {
        List<uint> path = [];
        while (reader.TryReadTag(out int field, out ProtoWireType wire))
        {
            if (field == 1 && wire == ProtoWireType.Varint)
            {
                path.Add(reader.ReadVarint32());
            }
            else
            {
                reader.SkipField(wire);
            }
        }

        return [.. path];
    }

    /// <summary>Serializes a run's segment table.</summary>
    /// <param name="segments">The segments, in key order.</param>
    internal static byte[] Run(IReadOnlyList<KeySegment> segments)
    {
        ProtoWriter writer = new ProtoWriter();
        try
        {
            writer.WriteUInt32Always(1, Version);
            foreach (KeySegment segment in segments)
            {
                using ProtoWriter.MessageScope scope = writer.BeginMessage(2);
                writer.WriteUInt64Always(1, segment.Entries);
                writer.WriteBytesAlways(2, segment.Min);
                writer.WriteBytesAlways(3, segment.Max);
            }

            return writer.WrittenSpan.ToArray();
        }
        finally
        {
            writer.Dispose();
        }
    }

    /// <summary>Parses a run's segment table.</summary>
    /// <param name="bytes">The run's options.</param>
    /// <param name="segments">The segments.</param>
    /// <returns>Whether the table is usable.</returns>
    internal static bool TryParseRun(ReadOnlySpan<byte> bytes, out List<KeySegment> segments)
    {
        segments = [];
        try
        {
            ProtoReader reader = new ProtoReader(bytes);
            uint version = 0;
            while (reader.TryReadTag(out int field, out ProtoWireType wire))
            {
                switch (field)
                {
                    case 1 when wire == ProtoWireType.Varint:
                        version = reader.ReadVarint32();
                        break;
                    case 2 when wire == ProtoWireType.LengthDelimited:
                        segments.Add(ReadSegment(reader.ReadMessage()));
                        break;
                    default:
                        reader.SkipField(wire);
                        break;
                }
            }

            return version == Version && segments.Count > 0;
        }
        catch (VortexFormatException)
        {
            segments = [];
            return false;
        }
    }

    private static KeySegment ReadSegment(ProtoReader reader)
    {
        ulong entries = 0;
        byte[] min = [];
        byte[] max = [];
        while (reader.TryReadTag(out int field, out ProtoWireType wire))
        {
            switch (field)
            {
                case 1 when wire == ProtoWireType.Varint:
                    entries = reader.ReadVarint();
                    break;
                case 2 when wire == ProtoWireType.LengthDelimited:
                    min = reader.ReadLengthDelimited().ToArray();
                    break;
                case 3 when wire == ProtoWireType.LengthDelimited:
                    max = reader.ReadLengthDelimited().ToArray();
                    break;
                default:
                    reader.SkipField(wire);
                    break;
            }
        }

        return new KeySegment(entries, min, max);
    }
}
