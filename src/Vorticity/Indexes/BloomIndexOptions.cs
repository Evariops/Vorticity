// The `options` bytes of a `vorticity.bloom.sbbf.v1` entry (docs/10-indexes.md §5.1):
//
//   message BloomOptions {
//     uint32 version = 1;              // 1
//     uint32 level = 2;                // 0 = one filter per block, 1 = per generation, 2 = the file
//     uint32 fpp_ppm = 3;
//     uint32 hash = 4;                 // 0 = XxHash3-64, 1 = xxHash64
//     uint32 max_blocks = 5;
//     repeated uint32 n_blocks = 6;    // packed; one per filter, in the order a probe meets them
//     uint32 generation_blocks = 7;    // k
//     uint32 min_distinct = 8;
//   }
//
// ONE ENTRY PER RESOLUTION, because runs inside an entry are disjoint (10 §4.1) and a generation
// filter covers the same blocks as the block filters beneath it. The scan evaluates the coarsest
// entry first and asks a finer one only for the blocks still live (10 §4.3).
//
// THE TABLE LOCATES A FILTER WITHOUT READING THE OTHERS. At block level it has one count per block
// of the column, zero where a block got no filter, and a block's filter starts in its run's payload
// at eight words times the sum of the counts before it in that run. At the two coarser levels it
// has one count per run, in run order.
using System;
using System.Collections.Generic;
using Vorticity.Serialization.Protobuf;

namespace Vorticity.Indexes;

/// <summary>Which filters an entry holds.</summary>
internal enum BloomLevel
{
    /// <summary>One filter per block.</summary>
    Block = 0,

    /// <summary>One filter per generation of blocks.</summary>
    Generation = 1,

    /// <summary>One filter for the whole file.</summary>
    File = 2,
}

/// <summary>The parsed options of one Bloom entry.</summary>
/// <param name="Level">The resolution.</param>
/// <param name="FalsePositivePpm">The rate the filters were sized for.</param>
/// <param name="Hash">The hash they store.</param>
/// <param name="MaxBlocks">The ceiling they were clamped to.</param>
/// <param name="FilterBlocks">One count per filter, as the header describes.</param>
/// <param name="GenerationBlocks">Blocks per generation.</param>
/// <param name="MinDistinct">The floor below which a block got no filter.</param>
internal sealed record BloomIndexOptions(
    BloomLevel Level,
    int FalsePositivePpm,
    BloomHash Hash,
    int MaxBlocks,
    int[] FilterBlocks,
    int GenerationBlocks,
    int MinDistinct)
{
    private const uint Version = 1;

    /// <summary>Serializes the options.</summary>
    /// <returns>The entry's <c>options</c> bytes.</returns>
    internal byte[] ToBytes()
    {
        ProtoWriter writer = new ProtoWriter();
        try
        {
            writer.WriteUInt32Always(1, Version);
            writer.WriteUInt32Always(2, (uint)Level);
            writer.WriteUInt32Always(3, (uint)FalsePositivePpm);
            writer.WriteUInt32(4, (uint)Hash);
            writer.WriteUInt32Always(5, (uint)MaxBlocks);
            using (ProtoWriter.MessageScope packed = writer.BeginMessage(6))
            {
                foreach (int blocks in FilterBlocks)
                {
                    writer.WriteVarint((uint)blocks);
                }
            }

            writer.WriteUInt32Always(7, (uint)GenerationBlocks);
            writer.WriteUInt32Always(8, (uint)MinDistinct);
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
            uint level = uint.MaxValue;
            uint fpp = 0;
            uint hash = 0;
            uint maxBlocks = 0;
            uint generation = 0;
            uint minDistinct = 0;
            List<int> blocks = [];
            while (reader.TryReadTag(out int field, out ProtoWireType wire))
            {
                switch (field)
                {
                    case 1 when wire == ProtoWireType.Varint:
                        version = reader.ReadVarint32();
                        break;
                    case 2 when wire == ProtoWireType.Varint:
                        level = reader.ReadVarint32();
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
                    case 6 when wire == ProtoWireType.LengthDelimited:
                        ProtoReader packed = reader.ReadMessage();
                        while (!packed.End)
                        {
                            blocks.Add(Count(packed.ReadVarint32()));
                        }

                        break;
                    case 6 when wire == ProtoWireType.Varint:
                        blocks.Add(Count(reader.ReadVarint32()));
                        break;
                    case 7 when wire == ProtoWireType.Varint:
                        generation = reader.ReadVarint32();
                        break;
                    case 8 when wire == ProtoWireType.Varint:
                        minDistinct = reader.ReadVarint32();
                        break;
                    default:
                        reader.SkipField(wire);
                        break;
                }
            }

            // A hash this reader does not compute would turn every probe into a false "absent".
            if (version != Version || level > (uint)BloomLevel.File || hash > (uint)BloomHash.XxHash64
                || blocks.Contains(-1))
            {
                return false;
            }

            options = new BloomIndexOptions(
                (BloomLevel)level, (int)Math.Min(fpp, int.MaxValue), (BloomHash)hash,
                (int)Math.Min(maxBlocks, int.MaxValue), [.. blocks],
                (int)Math.Min(generation, int.MaxValue), (int)Math.Min(minDistinct, int.MaxValue));
            return true;
        }
        catch (VortexFormatException)
        {
            return false;
        }
    }

    /// <summary>A count that fits a filter we could hold, or -1 to refuse the entry.</summary>
    private static int Count(uint blocks) => blocks <= BloomBuilderLimits.MaxFilterBlocks ? (int)blocks : -1;
}

/// <summary>The largest filter any level may declare, which bounds what a probe will read.</summary>
internal static class BloomBuilderLimits
{
    /// <summary>1 MiB blocks: the file-level ceiling of 10 §5.4, the largest of the three.</summary>
    internal const uint MaxFilterBlocks = 1u << 20;
}
