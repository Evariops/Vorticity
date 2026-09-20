using System;

using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Types;

namespace Vorticity.Arrays.Decoders.Compressed;

/// <summary>
/// Decodes <c>vortex.map</c>: a list of <c>{key, value}</c> entries per row. The encoding is empty
/// metadata, no buffers and one child, which must be a list view of a non-nullable
/// <c>Struct{key, value}</c>; there is no canonical map node, so the result is a list view that
/// keeps the map dtype, which reads as a list of entries while the schema still says map.
/// </summary>
public sealed class MapDecoder : ArrayDecoder
{
    private const string Id = "vortex.map";

    /// <summary>The shared, stateless instance.</summary>
    public static readonly MapDecoder Instance = new();

    /// <inheritdoc/>
    public override ReadOnlySpan<byte> IdUtf8 => "vortex.map"u8;

    /// <inheritdoc/>
    public override ArrayEncodingId EncodingId => ArrayEncodingId.Map;

    /// <inheritdoc/>
    public override int Decode(ArrayDecodeContext context, in ArrayNode node, DType dtype, int length)
    {
        ArgumentNullException.ThrowIfNull(context);

        ArrayDecodeContext.RequireBufferCount(node.BufferCount, 0, Id);
        ArrayDecodeContext.RequireChildCount(node.ChildCount, 1, Id);

        if (node.Metadata.Length != 0)
        {
            CompressedThrow.Format($"{Id} expects empty metadata; {node.Metadata.Length} bytes were given.");
        }

        if (dtype.Kind != DTypeKind.Map)
        {
            CompressedThrow.Format($"{Id} requires a map dtype; the node declares {dtype.Kind}.");
        }

        // The key and value types are imported into the context's arena first, then composed
        // there. A DType is an index into an arena and children must share their parent's, so the
        // file's types cannot be composed into the context's arena directly; composing into the
        // file's arena instead is not the way round it, because growing an arena rehashes its
        // dedup table with no synchronization while scans over one open file may run concurrently.
        // Importing costs nothing after the first batch: the target arena deduplicates, so every
        // later import finds the nodes already there.
        //
        // The entries dtype is rebuilt from the schema rather than taken from the child: handing
        // the child the shape it is expected to have is what turns a mismatch into an error rather
        // than a reinterpretation.
        DTypeArena types = context.Types;
        DType key = DTypeImport.Into(types, dtype.KeyType);
        DType value = DTypeImport.Into(types, dtype.ValueType);
        DType entriesList;

        // Interned handles and a pooled pair rather than two `new[]`s per decode. DType is a
        // managed type, so the fields cannot be `stackalloc`; Scratch is the house answer and is
        // what DTypeImport uses for the same two spans.
        Span<int> nameStack = stackalloc int[2];
        Scratch<DType> fields = new Scratch<DType>(2, default);
        try
        {
            nameStack[0] = types.InternName("key"u8);
            nameStack[1] = types.InternName("value"u8);
            Span<DType> fieldSpan = fields.Span;
            fieldSpan[0] = key;
            fieldSpan[1] = value;

            entriesList = types.List(
                types.Struct(nameStack, fieldSpan, Nullability.NonNullable), dtype.Nullability);
        }
        finally
        {
            fields.Dispose();
        }

        int child = context.DecodeChild(in node, 0, entriesList, length);
        CanonicalNode list = context.Canonical.GetNode(child);
        if (list.Kind != CanonicalKind.ListView)
        {
            CompressedThrow.ChildKind(Id, "entries", list.Kind, "a ListView");
        }

        if (list.Length != length)
        {
            CompressedThrow.ChildLength(Id, "entries", list.Length, length);
        }

        // Re-wrapped rather than returned as-is, so the node keeps the map dtype. Returning the
        // child would report the column as a list of structs and silently drop both the map-ness and
        // the keys-sorted flag the schema carries.
        return context.Canonical.AddListView(
            dtype,
            length,
            list.Validity,
            list.ElementsIndex,
            list.Offsets,
            list.OffsetPType,
            list.Sizes,
            list.SizePType);
    }
}
