// The `options` bytes of a `vorticity.bloom.sbbf.v1` or `vorticity.bloom.ngram3.v1` entry
// (docs/10-indexes.md §5.1, as amended by docs/13-dataset.md §6.2 at step 24):
//
//   message BloomOptions {
//     uint32 version = 1;              // 2: one filter tree per run
//     uint32 fpp_ppm = 3;
//     uint32 hash = 4;                 // 0 = XxHash3-64, 1 = xxHash64
//     uint32 max_blocks = 5;           // a node's ceiling: a node that needs more has no filter
//     uint32 fanout = 7;               // children per node: 16
//     uint32 min_distinct = 8;         // below it, a block or a node has no filter
//     bool   case_insensitive = 9;     // bloom.ngram3 only: trigrams ASCII-lower-cased on both sides
//     uint32 root_max_blocks = 10;     // the root's ceiling: max_blocks, or the file-level one
//   }
//
// ONE ENTRY PER COLUMN, AND NOTHING IN IT GROWS WITH THE FILE. Version 1 listed one entry per
// resolution and, at block level, one filter size per block of the file: a directory that grew
// with the data, and a probe that read every generation. Its entries are no longer read. An entry a
// reader cannot use is ignored (10 §4.1), which costs a file written before step 24 its pruning and
// never a row; fields 2 and 6 stay version 1's.
using System;
using Vorticity.Serialization.Protobuf;

namespace Vorticity.Indexes;

/// <summary>The parsed options of one Bloom entry.</summary>
/// <param name="FalsePositivePpm">The rate the filters were sized for.</param>
/// <param name="Hash">The hash they store.</param>
/// <param name="MaxBlocks">A leaf's clamp and a node's ceiling.</param>
/// <param name="MinDistinct">The floor below which a block or a node got no filter.</param>
/// <param name="CaseInsensitive">For a trigram filter, whether its trigrams were ASCII-lower-cased.</param>
/// <param name="RootMaxBlocks">The root's ceiling.</param>
internal sealed record BloomIndexOptions(
    int FalsePositivePpm,
    BloomHash Hash,
    int MaxBlocks,
    int MinDistinct,
    bool CaseInsensitive,
    int RootMaxBlocks)
{
    /// <summary>The version this library writes and reads: a filter tree per run.</summary>
    internal const uint Version = 2;

    /// <summary>Children per node: 10 §4.3's generation of sixteen blocks, at every level.</summary>
    internal const int Fanout = 16;

    /// <summary>Serializes the options.</summary>
    /// <returns>The entry's <c>options</c> bytes.</returns>
    internal byte[] ToBytes()
    {
        ProtoWriter writer = new ProtoWriter();
        try
        {
            writer.WriteUInt32Always(1, Version);
            writer.WriteUInt32Always(3, (uint)FalsePositivePpm);
            writer.WriteUInt32(4, (uint)Hash);
            writer.WriteUInt32Always(5, (uint)MaxBlocks);
            writer.WriteUInt32Always(7, Fanout);
            writer.WriteUInt32Always(8, (uint)MinDistinct);
            writer.WriteBool(9, CaseInsensitive);
            writer.WriteUInt32Always(10, (uint)RootMaxBlocks);
            return writer.WrittenSpan.ToArray();
        }
        finally
        {
            writer.Dispose();
        }
    }

    /// <summary>Parses the options, refusing what this reader cannot use.</summary>
    /// <param name="bytes">The entry's <c>options</c>.</param>
    /// <param name="options">The parsed options.</param>
    /// <returns>Whether they are usable; an unusable entry is ignored, never an error (10 §4.1).</returns>
    internal static bool TryParse(ReadOnlySpan<byte> bytes, out BloomIndexOptions? options)
    {
        options = null;
        try
        {
            ProtoReader reader = new ProtoReader(bytes);
            uint version = 0;
            uint fpp = 0;
            uint hash = 0;
            uint maxBlocks = 0;
            uint fanout = 0;
            uint minDistinct = 0;
            bool caseInsensitive = false;
            uint rootMaxBlocks = 0;
            while (reader.TryReadTag(out int field, out ProtoWireType wire))
            {
                switch (field)
                {
                    case 1 when wire == ProtoWireType.Varint:
                        version = reader.ReadVarint32();
                        break;
                    case 3 when wire == ProtoWireType.Varint:
                        fpp = reader.ReadVarint32();
                        break;
                    case 4 when wire == ProtoWireType.Varint:
                        hash = reader.ReadVarint32();
                        break;
                    case 5 when wire == ProtoWireType.Varint:
                        maxBlocks = reader.ReadVarint32();
                        break;
                    case 7 when wire == ProtoWireType.Varint:
                        fanout = reader.ReadVarint32();
                        break;
                    case 8 when wire == ProtoWireType.Varint:
                        minDistinct = reader.ReadVarint32();
                        break;
                    case 9 when wire == ProtoWireType.Varint:
                        caseInsensitive = reader.ReadBool();
                        break;
                    case 10 when wire == ProtoWireType.Varint:
                        rootMaxBlocks = reader.ReadVarint32();
                        break;
                    default:
                        reader.SkipField(wire);
                        break;
                }
            }

            // A hash this reader does not compute would turn every probe into a false "absent"; a
            // fan-out it does not know would place every child wrongly.
            if (version != Version || hash > (uint)BloomHash.XxHash64 || fanout != Fanout
                || !Fits(maxBlocks) || !Fits(rootMaxBlocks))
            {
                return false;
            }

            options = new BloomIndexOptions(
                (int)Math.Min(fpp, int.MaxValue), (BloomHash)hash, (int)maxBlocks,
                (int)Math.Min(minDistinct, int.MaxValue), caseInsensitive, (int)rootMaxBlocks);
            return true;
        }
        catch (VortexFormatException)
        {
            return false;
        }
    }

    private static bool Fits(uint blocks) => blocks is >= 1 and <= BloomBuilderLimits.MaxFilterBlocks;
}

/// <summary>The largest filter any node may declare, which bounds what a probe will read.</summary>
internal static class BloomBuilderLimits
{
    /// <summary>1 MiB blocks: the file-level ceiling of 10 §5.4, the largest there is.</summary>
    internal const uint MaxFilterBlocks = 1u << 20;
}
