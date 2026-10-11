using System;
using Vorticity.Serialization.Protobuf;

namespace Vorticity.Indexes;

/// <summary>
/// The parsed options of one Bloom entry: one entry per column, describing a single filter tree,
/// so that nothing in the directory grows with the data. Field numbers that earlier versions used
/// differently are left unread rather than reinterpreted, since an entry a reader cannot use is
/// ignored and costs a file its pruning, never a row.
/// </summary>
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

    /// <summary>Children per node: one generation of sixteen blocks, at every level.</summary>
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
    /// <returns>Whether they are usable; an unusable entry is ignored, never an error.</returns>
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
    /// <summary>The file-level ceiling, the largest a filter may ever be.</summary>
    internal const uint MaxFilterBlocks = 1u << 20;
}
