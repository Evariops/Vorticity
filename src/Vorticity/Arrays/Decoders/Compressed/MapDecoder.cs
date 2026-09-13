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

using Vorticity.Arrays.Decoders.Canonical;
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

        // IMPORTED INTO THE CONTEXT'S ARENA, then derived there - the order DTypeImport's header
        // states as the rule, and the reason it exists.
        //
        // THIS USED TO DERIVE INTO `dtype.Arena`, WHICH IS THE FILE'S. A DType is an index into an
        // arena and children must share their parent's, so composing the file's key and value types
        // straight into `context.Types` is refused - correctly - and deriving into the file's arena
        // instead looked like the way round it. It is not: `DTypeArena.Struct` grows arrays and
        // rehashes the dedup table with no synchronization at all, and docs/09-contracts.md §1
        // permits CONCURRENT SCANS over one open file. Two scans reaching a Map column at the same
        // moment are two unsynchronized writers on one table. That is why ScanContext owns an arena
        // (contract §8.3) and why `MaskedDecoder` and `ZoneMapSchema` already import first.
        //
        // Importing costs nothing after the first batch: the target arena deduplicates, so every
        // later import finds the nodes already there.
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
