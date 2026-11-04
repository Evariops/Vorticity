using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using Vorticity.Arrays.Metadata;
using Vorticity.IO;
using Vorticity.Serialization.Schemas;
using Vorticity.Types;

namespace Vorticity.Writing;

/// <summary>
/// What a column chunk's values cost the processor, read from the array tree of its segment (spec B, 2.1): the class of each
/// encoding its values lie under, and the units the class's price counts, values for most, runs for run-end, present values
/// for sparse. An extension is looked through to its storage, a list to its elements, a struct to its fields, a map to its
/// keys and values; a list's elements are counted as many as its rows, which its tree does not say.
/// </summary>
internal static class ChunkClasses
{
    /// <summary>The classes, <see cref="EncodingClass"/>'s values.</summary>
    internal const int Count = (int)EncodingClass.Heavy + 1;

    /// <summary>Adds what the chunk <paramref name="blob"/> of <paramref name="rows"/> rows holds to <paramref name="units"/>, by class.</summary>
    /// <param name="blob">The whole segment: the buffers, the array flatbuffer, its length.</param>
    /// <param name="encodings">The file's array encoding ids, which the nodes index.</param>
    /// <param name="dtype">The column's dtype.</param>
    /// <param name="rows">The chunk's rows.</param>
    /// <param name="units">The units of each class, <see cref="Count"/> of them.</param>
    internal static void Add(ReadOnlySpan<byte> blob, IReadOnlyList<string> encodings, DType dtype, long rows, Span<long> units)
    {
        int length = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(blob[^4..]));
        int budget = VortexLimits.MaxFlatBufferTables;
        Add(ArrayView.Root(blob.Slice(blob.Length - 4 - length, length), ref budget).Root_, dtype, encodings, rows, units);
    }

    private static void Add(ArrayNodeView node, DType dtype, IReadOnlyList<string> encodings, long rows, Span<long> units)
    {
        string id = node.Encoding < encodings.Count ? encodings[node.Encoding] : string.Empty;
        if (dtype.IsDefault || dtype.Kind == DTypeKind.Variant)
        {
            // A variant's metadata and values, bytes a row.
            units[(int)EncodingClass.Text] += rows;
            return;
        }

        switch (id)
        {
            case "vortex.ext" when dtype.Kind == DTypeKind.Extension && node.ChildCount == 1:
                Add(node.GetChild(0), dtype.StorageType, encodings, rows, units);
                return;
            case "vortex.listview" or "vortex.list" when dtype.Kind == DTypeKind.List && node.ChildCount > 0:
                Add(node.GetChild(0), dtype.ElementType, encodings, rows, units);
                return;
            case "vortex.fixed_size_list" when dtype.Kind == DTypeKind.FixedSizeList && node.ChildCount > 0:
                Add(node.GetChild(0), dtype.ElementType, encodings, rows * Math.Max(dtype.FixedSize, 1), units);
                return;
            case "vortex.struct" when dtype.Kind == DTypeKind.Struct && node.ChildCount - dtype.FieldCount is 0 or 1:
                int offset = node.ChildCount - dtype.FieldCount;
                for (int field = 0; field < dtype.FieldCount; field++)
                {
                    Add(node.GetChild(offset + field), dtype.GetField(field), encodings, rows, units);
                }

                return;
            case "vortex.map" when dtype.Kind == DTypeKind.Map && node.ChildCount == 1 && Entries(node.GetChild(0), encodings) is { IsNull: false } entries:
                int first = entries.ChildCount - 2;
                Add(entries.GetChild(first), dtype.KeyType, encodings, rows, units);
                Add(entries.GetChild(first + 1), dtype.ValueType, encodings, rows, units);
                return;
            case "vortex.runend":
                units[(int)EncodingClass.Runs] += checked((long)RunEndMetadata.Read(node.Metadata).NumRuns);
                return;
            case "vortex.sparse":
                units[(int)EncodingClass.Sparse] += checked((long)SparseMetadata.Read(node.Metadata).Patches.Length);
                return;
            default:
                units[(int)Of(id)] += rows;
                return;
        }
    }

    /// <summary>A map's entries, the struct of its keys and values under its list; null when the tree is shaped otherwise.</summary>
    private static ArrayNodeView Entries(ArrayNodeView list, IReadOnlyList<string> encodings)
    {
        string id = list.Encoding < encodings.Count ? encodings[list.Encoding] : string.Empty;
        if (id is not ("vortex.listview" or "vortex.list") || list.ChildCount == 0)
        {
            return default;
        }

        ArrayNodeView entries = list.GetChild(0);
        return entries.Encoding < encodings.Count && encodings[entries.Encoding] == "vortex.struct" && entries.ChildCount - 2 is 0 or 1
            ? entries
            : default;
    }

    /// <summary>The class of the values an array of <paramref name="id"/> holds, by what decoding them costs (spec B, 2.1).</summary>
    internal static EncodingClass Of(string id) => id switch
    {
        "vortex.constant" or "vortex.sequence" or "vortex.null" => EncodingClass.Free,
        "vortex.runend" => EncodingClass.Runs,
        "fastlanes.bitpacked" or "fastlanes.for" or "fastlanes.delta" or "fastlanes.rle" or "vortex.zigzag"
            or "vortex.decimal_byte_parts" or "vortex.bool" => EncodingClass.Light,
        "vortex.sparse" => EncodingClass.Sparse,
        "vortex.varbin" or "vortex.varbinview" or "vortex.listview" or "vortex.list" => EncodingClass.Text,
        "vortex.fsst" or "vortex.onpair" or "vortex.pco" or "vortex.zstd" => EncodingClass.Heavy,
        _ => EncodingClass.Medium,
    };
}
