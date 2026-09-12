// vortex.flat - vortex-layout-0.86.1/src/layouts/flat/{mod.rs,reader.rs}. The terminal layout:
// exactly one segment holding one serialized array, exactly zero children.
//
// Two variants, ONE walk. Normally the segment is
// [padding][buffer 0]..[Array flatbuffer][u32 fb length] and the tree is read off its tail. When
// `array_encoding_tree` is present in the metadata the same tree is ALSO inlined there, and the
// segment bytes are byte-identical - the writer still appends the FlatBuffer and the u32, so the
// buffer walk simply never reaches the tail (contract §11.3, verified against
// corpus/containers/flat_inline_array_node.vortex).
using System;

using Vorticity.Arrays;
using Vorticity.Arrays.Metadata;
using Vorticity.Buffers;
using Vorticity.IO;

namespace Vorticity.Layouts;

/// <summary>Reads a <c>vortex.flat</c> layout: one segment, one array.</summary>
public sealed class FlatLayoutReader : LayoutReader
{
    /// <summary>The shared, stateless instance.</summary>
    public static readonly FlatLayoutReader Instance = new FlatLayoutReader();

    private FlatLayoutReader()
    {
    }

    /// <inheritdoc/>
    public override LayoutEncodingId EncodingId => LayoutEncodingId.Flat;

    /// <inheritdoc/>
    public override ReadOnlySpan<byte> IdUtf8 => "vortex.flat"u8;

    /// <inheritdoc/>
    public override void RegisterSegments(
        in LayoutNode node, RowRange rows, in FieldMask fields, SegmentRequestSet segments)
    {
        ArgumentNullException.ThrowIfNull(segments);
        CheckRange(in node, rows);

        // A flat layout is indivisible: whatever rows are wanted, the whole segment is read and the
        // array decoded, then sliced. Upstream says the same with `is_indivisible() -> true`.
        RegisterSegment(in node, 0, segments);
    }

    /// <inheritdoc/>
    public override int Execute(in LayoutNode node, RowRange rows, in FieldMask fields, ScanContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        CheckRange(in node, rows);

        int total = NodeLength(in node);
        int length = BatchLength(rows);

        VortexBuffer segment = SegmentBuffer(in node, 0, context);
        FlatLayoutMetadata metadata = FlatLayoutMetadata.Read(node.Metadata);
        if (metadata.HasArrayEncodingTree)
        {
            context.Decode.LoadBlob(metadata.ArrayEncodingTree, segment);
        }
        else
        {
            context.Decode.LoadBlob(segment);
        }

        // The contained array's dtype is exactly the node's and its length exactly the node's row
        // count; neither is carried by the array blob (docs/02-format.md §5.2).
        ArrayNode root = context.Nodes.Root;

        // The selection is in the same space as `rows`, and a flat layout's space IS the segment's,
        // so the wanted rows need no translation at all - which is the whole reason the selection
        // is carried in the row argument's coordinate space rather than in absolute file rows.
        if (context.HasSelection)
        {
            int taken = context.Decode.DecodeRootSelected(
                in root, node.DType, total, context.Selection);
            return MaskProjection.Apply(context.Decode, taken, in fields);
        }

        int decoded = context.Decode.DecodeRoot(in root, node.DType, total);

        int sliced = CanonicalSlice.Slice(context.Decode, decoded, (int)rows.Start, length);
        return MaskProjection.Apply(context.Decode, sliced, in fields);
    }
}
