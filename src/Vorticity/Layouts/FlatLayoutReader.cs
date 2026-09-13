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

    /// <summary>
    /// Values this reader has materialized, across every scan in the process.
    /// </summary>
    /// <remarks>
    /// Internal and diagnostic. The cost of maintaining it is one interlocked add per decoded node,
    /// beside a decode of that whole node, so it is not measurable; the cost of NOT having it was
    /// a quadratic that survived forty-two iterations of benchmarking.
    /// </remarks>
    internal static long ValuesDecoded;

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

        // WHOLE-NODE BATCH: nothing to retain, because there is no second batch to serve. This is
        // every file whose chunk is its batch, which is every conformance fixture and every file
        // this library writes at the default edition, so the common path is unchanged.
        if (rows.Start == 0 && length == total)
        {
            System.Threading.Interlocked.Add(ref ValuesDecoded, total);
            int whole = context.Decode.DecodeRoot(in root, node.DType, total);
            return MaskProjection.Apply(context.Decode, whole, in fields);
        }

        // A CHUNK LARGER THAN A BATCH IS DECODED ONCE, NOT ONCE PER BATCH. Decoding `total` and
        // slicing `length` out of it is correct and was quadratic: 123 batches over a million rows
        // decoded 123 million values to deliver one million. Measured at 3.4 SECONDS for a 1M-row
        // FSST column against 26.5 ms with the subdivision removed.
        //
        // The retained node lives in an arena `ResetBatch` does not touch, and the window costs
        // NOTHING BUT RECORDS: `SliceAcross` appends to the batch's arena records whose buffers are
        // narrowed VIEWS onto the retained storage. No bytes move however many rows the window spans,
        // and a VarBinView's data buffers travel as views rather than being rebuilt per batch, which
        // is what the string encodings needed.
        //
        // The batch therefore BORROWS the retained arena. `ScanContext.Retain` owes the lifetime
        // argument and makes it: an entry touched during the current batch is never evicted.
        uint segmentId = node.Segments[0];
        if (!context.TryGetRetained(segmentId, out CanonicalArena held, out int retained))
        {
            System.Threading.Interlocked.Add(ref ValuesDecoded, total);

            // DECODED STRAIGHT INTO THE ARENA THAT RETAINS IT. The chunk used to be decoded into
            // the batch's arena and then deep-copied into one that outlives it -- a second full
            // pass over every byte, 26% of a 1M-row sequence scan. `Canonical` is redirected for
            // the duration of this one decode and nothing else changes: the lifetime argument was
            // always about which arena the result lives in, and it lives in the same one.
            held = context.BeginRetainedDecode();
            retained = -1;
            try
            {
                retained = context.Decode.DecodeRoot(in root, node.DType, total);
            }
            finally
            {
                context.EndRetainedDecode(segmentId, retained);
            }
        }

        int sliced = CanonicalSlice.SliceAcross(held, context.Canonical, retained, (int)rows.Start, length);
        return MaskProjection.Apply(context.Decode, sliced, in fields);
    }
}
