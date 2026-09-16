// The options of the two locating kinds, `vorticity.postings.blocks.v1` and
// `vorticity.sorted.runs.v1` (docs/10-indexes.md §6.1, §6.2, §4.2).
//
//   message KeyIndexOptions {           // the entry's options
//     uint32 version = 1;                // 1
//     uint32 segment_entries = 2;        // 10 §4.2's payload_block_rows: the most entries a segment holds
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

    /// <summary>The stride of a kind, or 0 for a kind that is not a locating one.</summary>
    /// <param name="kind">The kind name.</param>
    internal static int StrideOf(string kind) => kind switch
    {
        IndexKinds.PostingsBlocks => PostingsStride,
        IndexKinds.SortedRuns => SortedStride,
        _ => 0,
    };

    /// <summary>Serializes an entry's options.</summary>
    /// <param name="segmentEntries">The segment size the runs were cut at.</param>
    internal static byte[] Entry(int segmentEntries)
    {
        ProtoWriter writer = new ProtoWriter();
        try
        {
            writer.WriteUInt32Always(1, Version);
            writer.WriteUInt32Always(2, (uint)segmentEntries);
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
    internal static bool TryParseEntry(ReadOnlySpan<byte> bytes, out int segmentEntries)
    {
        segmentEntries = 0;
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
