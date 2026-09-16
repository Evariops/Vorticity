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

    /// <summary>
    /// Counts <paramref name="values"/> materialized: on the process-wide counter the decode-count
    /// tests read, and on the scan's own sink when it has one (docs/11 §6.4).
    /// </summary>
    private static void Decoded(ScanContext context, long values)
    {
        System.Threading.Interlocked.Add(ref ValuesDecoded, values);
        context.Metrics?.AddDecoded(values);
    }

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

        // THE BLOB IS PARSED WHERE IT IS READ, not on the way past. Loading it means resetting the
        // node arena, copying the Array FlatBuffer into it, walking the node tree and resolving
        // every buffer spec in the segment -- and none of that survives the call. A batch that
        // finds its chunk already decoded and retained never touches the root, so on a column
        // whose chunk spans 123 batches the parse ran 123 times to be used once. It was 15% of a
        // 1M-row pco scan, a file of 24 KB.
        //
        // The three readers below each load it themselves, immediately before the only use.

        // The selection is in the same space as `rows`, and a flat layout's space IS the segment's,
        // so the wanted rows need no translation at all - which is the whole reason the selection
        // is carried in the row argument's coordinate space rather than in absolute file rows.
        if (context.HasSelection)
        {
            return ExecuteSelected(in node, in fields, context, total, length);
        }

        // DEAD BLOCKS IN THIS NODE (docs/11 §6.1): the scan's mask says some of its blocks will
        // never be asked for, so decoding it whole and retaining it would materialize rows no
        // batch reads -- sixteen blocks for the one that lived, on a chunk of the default shape.
        // This batch's rows go through the selection path instead, as a counted range. The mask
        // reaching this reader means no chunked ancestor re-partitioned the rows, so `rows` ARE
        // file rows and the node covers the file: a chunked reader clears the mask for its
        // children and says the same thing in the selection.
        if (length < total && context.LiveBlocks is { } live && live.HasDeadBlocks(new RowRange(0, total)))
        {
            return ExecuteRange(in node, rows, in fields, context, total, length);
        }

        // WHOLE-NODE BATCH: nothing to retain, because there is no second batch to serve. This is
        // every file whose chunk is its batch, which is every conformance fixture and every file
        // this library writes at the default edition, so the common path is unchanged.
        if (rows.Start == 0 && length == total)
        {
            ArrayNode wholeRoot = LoadRoot(in node, context);
            Decoded(context, total);
            int whole = context.Decode.DecodeRoot(in wholeRoot, node.DType, total);
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
        long key = ScanContext.SegmentKey(node.Segments[0]);
        if (!context.TryGetRetained(key, out CanonicalArena held, out int retained))
        {
            Decoded(context, total);

            // DECODED STRAIGHT INTO THE ARENA THAT RETAINS IT. The chunk used to be decoded into
            // the batch's arena and then deep-copied into one that outlives it -- a second full
            // pass over every byte, 26% of a 1M-row sequence scan. `Canonical` is redirected for
            // the duration of this one decode and nothing else changes: the lifetime argument was
            // always about which arena the result lives in, and it lives in the same one.
            ArrayNode chunkRoot = LoadRoot(in node, context);
            held = context.BeginRetainedDecode();
            retained = -1;
            try
            {
                retained = context.Decode.DecodeRoot(in chunkRoot, node.DType, total);
            }
            finally
            {
                context.EndRetainedDecode(key, retained);
            }
        }

        int sliced = CanonicalSlice.SliceAcross(held, context.Canonical, retained, (int)rows.Start, length);
        return MaskProjection.Apply(context.Decode, sliced, in fields);
    }

    /// <summary>
    /// Produces the selected rows of this node, through the retained chunk when the encoding's
    /// <c>DecodeSelected</c> is the whole-node fallback.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A TAKE USED TO PAY THE QUADRATIC THE SCAN HAD ALREADY LEFT. The branch above serves one batch
    /// at a time and the selection is split across batches too, so an encoding that lands on
    /// <see cref="ArrayDecoder.DecodeSelected"/>'s default decoded the SAME node once per batch:
    /// counted on `vortex.zstd`, 5 824 calls for 5 824 wanted rows - one row each - decoding a
    /// million rows and ~881 zstd frames every time. 64 rows cost 427 ms where a full scan of that
    /// node costs 7 ms, which is 59 scans of the file to deliver 64 rows. The cure is the cache the
    /// batch path already has, used for the same reason.
    /// </para>
    /// <para>
    /// AND IT IS GATED, because for ten encodings it would be a regression rather than a cure: they
    /// override <c>DecodeSelected</c> and reach one row without materializing a node, so the `fsst`
    /// take reads 0.21 against the reference and a forced full decode would cost it the 26.5 ms this
    /// file's other comment names. <see cref="ArrayDecoder.SelectsWithoutFullDecode"/> is the
    /// question, asked of the ROOT decoder - a specialized root pushes the selection into its own
    /// children, and what those children are is its business, not this reader's.
    /// </para>
    /// <para>
    /// THE HIT PATH NEVER PARSES THE BLOB, which is the same 15% the batch path refuses to pay: the
    /// retained entry is looked up before <see cref="LoadRoot"/>, so only the first batch of a take
    /// touches the node arena at all.
    /// </para>
    /// </remarks>
    private int ExecuteSelected(
        in LayoutNode node, in FieldMask fields, ScanContext context, int total, int length)
    {
        // NOTHING TO RETAIN WHEN THIS CALL IS SERVED THE WHOLE NODE, which is the same argument the
        // whole-node batch above makes: no second call will come for the rest of it, so an entry
        // would be created, used once and evicted. It is not free to get this wrong - the take axis
        // of `PathAllocationTests` is 64 chunks of 1024 rows, and retaining every one of them put it
        // 7 008 B over a ceiling that may not rise.
        if (length < total)
        {
            long key = ScanContext.SegmentKey(node.Segments[0]);
            if (context.TryGetRetained(key, out CanonicalArena hit, out int hitNode))
            {
                return Gather(hit, hitNode, in fields, context, total);
            }

            ArrayNode chunkRoot = LoadRoot(in node, context);
            if (!ArrayDecoderTable
                    .Require(context, chunkRoot.Encoding, chunkRoot.EncodingSpecIndex)
                    .SelectsWithoutFullDecode)
            {
                Decoded(context, total);
                CanonicalArena held = context.BeginRetainedDecode();
                int retained = -1;
                try
                {
                    retained = context.Decode.DecodeRoot(in chunkRoot, node.DType, total);
                }
                finally
                {
                    context.EndRetainedDecode(key, retained);
                }

                return Gather(held, retained, in fields, context, total);
            }

            // THE SPECIALIZED ROUTE RE-ESTABLISHES ITS OWN INVARIANTS, once per batch, and this is
            // where that stops. The encoding takes its rows without decoding the node -- that part
            // works -- but the O(n) checks it runs on its side tables are facts about the NODE, not
            // about the selection: `vortex.runend` re-walked 15 625 run ends and ALP re-walked
            // 16 454 patch indices on every visit. Measured at 93% of a selective run-end take
            // (v2 R26). The scope is opened only here, under `length < total`, because below that
            // the node is visited once and there is nothing to remember.
            uint? outer = context.Decode.BeginNodeCheckScope(node.Segments[0]);
            try
            {
                return Push(in chunkRoot, in node, in fields, context, total);
            }
            finally
            {
                context.Decode.EndNodeCheckScope(outer);
            }
        }

        ArrayNode wholeRoot = LoadRoot(in node, context);
        return Push(in wholeRoot, in node, in fields, context, total);
    }

    /// <summary>Lets the encoding take the wanted rows itself, which is what it did before R23.</summary>
    private static int Push(
        in ArrayNode root, in LayoutNode node, in FieldMask fields, ScanContext context, int total)
    {
        // Counted like the whole-node decodes above: what a positional take materializes is its
        // selection, and a decode no instrument counts is a decode nobody sees.
        Decoded(context, context.Selection.Length);
        int taken = context.Decode.DecodeRootSelected(in root, node.DType, total, context.Selection);
        return MaskProjection.Apply(context.Decode, taken, in fields);
    }

    /// <summary>
    /// This batch's rows through the selection path, as a counted range: the decode of a node
    /// whose other blocks the scan's mask has killed.
    /// </summary>
    private int ExecuteRange(
        in LayoutNode node, RowRange rows, in FieldMask fields, ScanContext context, int total, int length)
    {
        int[] range = System.Buffers.ArrayPool<int>.Shared.Rent(Math.Max(length, 1));
        try
        {
            int start = (int)rows.Start;
            for (int i = 0; i < length; i++)
            {
                range[i] = start + i;
            }

            (int[]? Buffer, int Count) saved = context.ExchangeSelection(range, length);
            try
            {
                return ExecuteSelected(in node, in fields, context, total, length);
            }
            finally
            {
                context.ExchangeSelection(saved.Buffer, saved.Count);
            }
        }
        finally
        {
            System.Buffers.ArrayPool<int>.Shared.Return(range);
        }
    }

    /// <summary>Gathers <see cref="ScanContext.Selection"/> out of a retained whole-node decode.</summary>
    /// <remarks>
    /// Two steps and only the second moves bytes: the window is a full-width view onto the retained
    /// storage - records, no memory traffic, the property <see cref="CanonicalSlice.SliceAcross"/>
    /// exists for - and the gather then materializes exactly the wanted rows. The result borrows the
    /// retained arena, which <see cref="ScanContext.TryGetRetained"/> has just made un-evictable for
    /// the rest of this batch.
    /// </remarks>
    private static int Gather(
        CanonicalArena held, int retained, in FieldMask fields, ScanContext context, int total)
    {
        int whole = CanonicalSlice.SliceAcross(held, context.Canonical, retained, 0, total);
        int taken = Compute.CanonicalFilter.Apply(context.Canonical, whole, context.Selection);
        return MaskProjection.Apply(context.Decode, taken, in fields);
    }

    /// <summary>
    /// Parses this layout's array blob into the scan's node arena and returns its root.
    /// </summary>
    /// <remarks>
    /// The contained array's dtype is exactly the node's and its length exactly the node's row
    /// count; neither is carried by the array blob (docs/02-format.md §5.2).
    /// </remarks>
    /// <param name="node">The flat layout node.</param>
    /// <param name="context">The scan context owning the node arena.</param>
    /// <returns>The root of the parsed blob.</returns>
    private ArrayNode LoadRoot(in LayoutNode node, ScanContext context)
    {
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

        return context.Nodes.Root;
    }
}
