// vortex.map - vortex-array-0.86.1/src/arrays/map/vtable/mod.rs.
//
// A MAP IS A LISTVIEW OF STRUCTS WEARING A DIFFERENT DTYPE, and that is the whole encoding: empty
// metadata, no buffers, one child, and upstream's `execute` returns the array unchanged because a
// map array is already canonical. The child must be a `vortex.listview` of
// `Struct{key, value}` - upstream checks the encoding explicitly rather than accepting any list.
//
// WHAT THIS BUILD DOES WITH IT. There is no `CanonicalKind.Map`, and adding one would mean a new
// column type for a shape `AsList().AsStruct()` already expresses. The node is therefore a
// `ListView` carrying the MAP dtype: the data reads as list-of-entries, and the schema still says
// map. `CanonicalArena.AddListView` does not constrain the dtype's kind, which is what makes that
// representable rather than a lie.
//
// The entries dtype is rebuilt here rather than taken from the child, because the child is
// FILE-SUPPLIED and the expected shape is SCHEMA-supplied: `List(Struct{key, value}, nullability)`
// with the struct non-nullable, exactly `MapDType::entries_dtype`. Handing the child its expected
// dtype is how a mismatch becomes an error instead of a reinterpretation.
using System;

using Vorticity.Types;

namespace Vorticity.Arrays.Decoders.Compressed;

/// <summary>Decodes <c>vortex.map</c>: a list of <c>{key, value}</c> entries per row.</summary>
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

        // BUILT IN THE DTYPE'S OWN ARENA, not the context's. A DType is an index into an arena and
        // children must share their parent's; composing the file's key and value types inside
        // `context.Types` throws "Child DTypes must come from the same DTypeArena as their parent",
        // which is the arena invariant doing its job rather than an obstacle to route around.
        DTypeArena types = dtype.Arena;
        DType entries = types.Struct(
            ["key", "value"], [dtype.KeyType, dtype.ValueType], Nullability.NonNullable);
        DType entriesList = types.List(entries, dtype.Nullability);

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

        // Re-wrapped rather than returned as-is, so the node keeps the MAP dtype. Returning the
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
