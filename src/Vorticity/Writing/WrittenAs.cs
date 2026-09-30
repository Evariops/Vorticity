using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Buffers;
using Vorticity.File;
using Vorticity.Layouts;
using Vorticity.Serialization.Schemas;
using Vorticity.Types;

namespace Vorticity.Writing;

/// <summary>
/// What a column chunk's values were written as, read from the array tree of its segment: the
/// scheme's <see cref="EncodingHint"/> name, <c>Canonical</c> for values stored plain, or the array
/// id of an encoding no scheme writes. An extension is looked through to its storage and a list to
/// its elements, since the values are there; a struct, a map's entries or a variant is its fields'.
/// </summary>
internal static class WrittenAs
{
    /// <summary>The spelling of a chunk stored without a scheme.</summary>
    internal const string Canonical = nameof(EncodingHint.Canonical);

    /// <summary>What the blob of one column chunk holds.</summary>
    /// <param name="blob">The whole segment: the buffers, the array flatbuffer, its length.</param>
    /// <param name="encodings">The file's array encoding ids, which the nodes index.</param>
    /// <param name="dtype">The column's dtype.</param>
    internal static string Of(ReadOnlySpan<byte> blob, IReadOnlyList<string> encodings, DType dtype)
    {
        int length = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(blob[^4..]));
        return OfTree(blob.Slice(blob.Length - 4 - length, length), encodings, dtype);
    }

    /// <summary>What an array flatbuffer holds.</summary>
    private static string OfTree(ReadOnlySpan<byte> tree, IReadOnlyList<string> encodings, DType dtype)
    {
        int budget = VortexLimits.MaxFlatBufferTables;
        return Label(ArrayView.Root(tree, ref budget).Root_, dtype, encodings);
    }

    /// <summary>
    /// What one flat chunk of <paramref name="file"/> holds, from its layout's inlined tree or else
    /// from the tail of its segment, which is all of it that is read.
    /// </summary>
    internal static async ValueTask<string> ReadAsync(
        VortexFile file, LayoutNode flat, DType dtype, IReadOnlyList<string> encodings, CancellationToken cancellationToken)
    {
        Arrays.Metadata.FlatLayoutMetadata metadata = Arrays.Metadata.FlatLayoutMetadata.Read(flat.Metadata);
        if (metadata.HasArrayEncodingTree)
        {
            return OfTree(metadata.ArrayEncodingTree, encodings, dtype);
        }

        SegmentSpec spec = file.SegmentSpecs[checked((int)flat.Segments[0])];
        long end = (long)spec.Offset + spec.Length;
        int window = (int)Math.Min(spec.Length, 1024);
        using (SegmentOwner tail = await file.Segments.ReadRangeAsync(end - window, window, 1, cancellationToken).ConfigureAwait(false))
        {
            if (Fits(tail.Buffer, out string? label, encodings, dtype))
            {
                return label!;
            }
        }

        // A tree longer than the window: read it whole, with its length.
        int length;
        using (SegmentOwner trailer = await file.Segments.ReadRangeAsync(end - 4, 4, 1, cancellationToken).ConfigureAwait(false))
        {
            length = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(trailer.Buffer.Span));
        }

        using SegmentOwner whole = await file.Segments.ReadRangeAsync(end - 4 - length, length + 4, 1, cancellationToken).ConfigureAwait(false);
        return Of(whole.Buffer.Span, encodings, dtype);
    }

    /// <summary>The label of a segment's tail when the tail holds the whole tree.</summary>
    private static bool Fits(VortexBuffer tail, out string? label, IReadOnlyList<string> encodings, DType dtype)
    {
        ReadOnlySpan<byte> bytes = tail.Span;
        uint length = BinaryPrimitives.ReadUInt32LittleEndian(bytes[^4..]);
        label = length + 4L <= bytes.Length ? Of(bytes, encodings, dtype) : null;
        return label is not null;
    }

    private static string Label(ArrayNodeView node, DType dtype, IReadOnlyList<string> encodings)
    {
        string id = node.Encoding < encodings.Count ? encodings[node.Encoding] : string.Empty;
        if (dtype.IsDefault)
        {
            // A variant's metadata and value, which are bytes and wrap nothing.
            return Scheme(id);
        }

        switch (id)
        {
            case "vortex.ext" when dtype.Kind == DTypeKind.Extension && node.ChildCount == 1:
                return Label(node.GetChild(0), dtype.StorageType, encodings);
            case "vortex.listview" or "vortex.list" when dtype.Kind == DTypeKind.List && node.ChildCount > 0:
                return Label(node.GetChild(0), dtype.ElementType, encodings);
            case "vortex.fixed_size_list" when dtype.Kind == DTypeKind.FixedSizeList && node.ChildCount > 0:
                return Label(node.GetChild(0), dtype.ElementType, encodings);
            case "vortex.struct" when dtype.Kind == DTypeKind.Struct && node.ChildCount - dtype.FieldCount is 0 or 1:
                return Fields(node, dtype, encodings);
            case "vortex.map" when dtype.Kind == DTypeKind.Map && node.ChildCount == 1:
                return Entries(node.GetChild(0), dtype, encodings);
            case "vortex.parquet.variant" when dtype.Kind == DTypeKind.Variant && node.ChildCount >= 2:
                return $"{{metadata: {Label(node.GetChild(node.ChildCount - 2), default, encodings)}, value: {Label(node.GetChild(node.ChildCount - 1), default, encodings)}}}";
            default:
                return Scheme(id);
        }
    }

    /// <summary>A struct's fields, by name, in order.</summary>
    private static string Fields(ArrayNodeView node, DType dtype, IReadOnlyList<string> encodings)
    {
        int offset = node.ChildCount - dtype.FieldCount;
        StringBuilder text = new StringBuilder("{");
        for (int i = 0; i < dtype.FieldCount; i++)
        {
            text.Append(i == 0 ? string.Empty : ", ").Append(dtype.GetFieldName(i)).Append(": ")
                .Append(Label(node.GetChild(offset + i), dtype.GetField(i), encodings));
        }

        return text.Append('}').ToString();
    }

    /// <summary>A map's entries: the list under it, whose elements are a struct of the key and the value.</summary>
    private static string Entries(ArrayNodeView list, DType map, IReadOnlyList<string> encodings)
    {
        string id = list.Encoding < encodings.Count ? encodings[list.Encoding] : string.Empty;
        if (id is not ("vortex.listview" or "vortex.list") || list.ChildCount == 0)
        {
            return Scheme(id);
        }

        ArrayNodeView entries = list.GetChild(0);
        int offset = entries.ChildCount - 2;
        if (entries.Encoding >= encodings.Count || encodings[entries.Encoding] != "vortex.struct" || offset is not (0 or 1))
        {
            return Canonical;
        }

        return $"{{key: {Label(entries.GetChild(offset), map.KeyType, encodings)}, value: {Label(entries.GetChild(offset + 1), map.ValueType, encodings)}}}";
    }

    /// <summary>The scheme that writes an array id, <c>Canonical</c> for a plain form, the id itself for any other.</summary>
    private static string Scheme(string id) => id switch
    {
        "vortex.dict" => nameof(EncodingHint.Dictionary),
        "vortex.runend" => nameof(EncodingHint.RunEnd),
        "fastlanes.bitpacked" or "fastlanes.for" or "vortex.zigzag" => nameof(EncodingHint.BitPacked),
        "vortex.fsst" => nameof(EncodingHint.Fsst),
        "vortex.alp" => nameof(EncodingHint.Alp),
        "vortex.alprd" => nameof(EncodingHint.AlpRd),
        "vortex.sequence" => nameof(EncodingHint.Sequence),
        "vortex.zstd" => nameof(EncodingHint.Zstd),
        "vortex.decimal_byte_parts" => nameof(EncodingHint.DecimalByteParts),
        "vortex.constant" => nameof(EncodingHint.Constant),
        "vortex.datetimeparts" => nameof(EncodingHint.DateTimeParts),
        "vortex.sparse" => nameof(EncodingHint.Sparse),
        "vortex.onpair" => nameof(EncodingHint.OnPair),
        "vortex.pco" => nameof(EncodingHint.Pco),
        "vortex.null" or "vortex.bool" or "vortex.primitive" or "vortex.decimal" or "vortex.varbinview" or "vortex.varbin"
            or "vortex.struct" or "vortex.listview" or "vortex.list" or "vortex.fixed_size_list" or "vortex.ext" or "vortex.map"
            or "vortex.parquet.variant" => Canonical,
        _ => id,
    };
}
