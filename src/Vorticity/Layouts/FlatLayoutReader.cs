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

        // Dead blocks in this node are handled a level up and not here. A branch used to read the
        // batch's rows as a counted range when the scan's mask killed blocks of the node, on the
        // reasoning that a mask reaching this reader means no chunked ancestor re-partitioned the
        // rows. It reached nothing: this library wraps every column in `vortex.chunked`, even for a
        // single chunk, and a chunked reader clears the mask for its children and says what it means
        // in the selection, so the branch above takes those rows. The one file in the corpus whose
        // column is a bare flat node carries no zone map, so its mask never has a dead block either.
        // A file that did have the shape reads correctly through the retained decode below, which is
        // what the chunked form pays for its first batch anyway.

        // WHOLE-NODE BATCH: nothing to retain, because there is no second batch to serve. This is
        // every file whose chunk is its batch, which is every conformance fixture and every file
        // this library writes at the default edition, so the common path is unchanged.
        if (rows.Start == 0 && length == total)
        {
            ArrayNode wholeRoot = LoadRoot(in node, context);
            if (context.PredicateAtNode)
            {
                int answered = TryAnswer(in wholeRoot, in node, context, total);
                if (answered >= 0)
                {
                    return answered;
                }
            }

            Decoded(context, total);
            return Narrowed(Decoded(in wholeRoot, in node, in fields, context, total), in fields, context);
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

        // THE ANSWER IS RETAINED LIKE THE CHUNK IS, and for the same reason: a chunk spans several
        // batches, so asking the encoding once per batch would ask it once per batch for one
        // answer. It is kept under a key of its own because the same chunk can be wanted both as an
        // answer and as values, and a cache that confused the two would hand a boolean column to a
        // caller that asked for strings.
        if (context.PredicateAtNode &&
            TryRetainedAnswer(in node, context, key, total, out CanonicalArena states, out int said))
        {
            return CanonicalSlice.SliceAcross(states, context.Canonical, said, (int)rows.Start, length);
        }

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
                retained = Decoded(in chunkRoot, in node, in fields, context, total);
            }
            finally
            {
                context.EndRetainedDecode(key, retained);
            }
        }

        int sliced = CanonicalSlice.SliceAcross(held, context.Canonical, retained, (int)rows.Start, length);
        return Narrowed(sliced, in fields, context);
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
        // THE PREDICATE IS ASKED HERE TOO, and the selective path is where a selective predicate
        // actually lands: a column with a zone map whose blocks the scan can kill reaches this
        // reader with a selection in force, and that is exactly the shape a selective equality
        // makes. Answering only on the branch above meant answering only for the columns whose
        // data defeats pruning -- a dictionary of sixteen labels, never a column of distinct
        // strings. The answer is for the whole node and the selection is gathered out of it,
        // which is what `Gather` does for values and for the same reason.
        if (context.PredicateAtNode)
        {
            long answerKey = ScanContext.SegmentKey(node.Segments[0]);
            if (TryRetainedAnswer(
                    in node, context, answerKey, total, out CanonicalArena answered, out int answer))
            {
                int whole = CanonicalSlice.SliceAcross(answered, context.Canonical, answer, 0, total);
                return Compute.CanonicalFilter.Apply(context.Canonical, whole, context.Selection);
            }
        }

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

    /// <summary>
    /// Decodes this node under <paramref name="fields"/>, letting a struct honour it if it can.
    /// </summary>
    /// <param name="root">The node's parsed array root.</param>
    /// <param name="node">The flat layout node.</param>
    /// <param name="fields">The projection.</param>
    /// <param name="context">The scan context.</param>
    /// <param name="total">The node's row count.</param>
    /// <returns>The canonical node, narrowed to the projection.</returns>
    /// <remarks>
    /// The projection is offered before the decode and the narrowing is applied after it only when
    /// the decode did not take it. Taking it is worth the whole difference: a struct stored as one
    /// array node has no layout to read fields lazily, so without this a scan projecting one column
    /// of fifty decodes all fifty -- 66 microseconds against 64 for the whole file, which is dearer
    /// than not projecting.
    /// </remarks>
    private static int Decoded(
        in ArrayNode root, in LayoutNode node, in FieldMask fields, ScanContext context, int total)
    {
        // A whole projection has nothing to push, and saying so here is what keeps an unprojected
        // scan from allocating the holder at all: these paths are held to a ceiling in bytes.
        if (fields.IsAll)
        {
            return context.Decode.DecodeRoot(in root, node.DType, total);
        }

        FieldMask outer = context.ExchangePushedFields(fields);
        try
        {
            return context.Decode.DecodeRoot(in root, node.DType, total);
        }
        finally
        {
            context.ExchangePushedFields(outer);
        }
    }

    /// <summary>Narrows what the decode did not narrow itself.</summary>
    /// <param name="node">The decoded node.</param>
    /// <param name="fields">The projection.</param>
    /// <param name="context">The scan context.</param>
    /// <returns>The node, narrowed to the projection.</returns>
    private static int Narrowed(int node, in FieldMask fields, ScanContext context) =>
        context.FieldsHonoured ? node : MaskProjection.Apply(context.Decode, node, in fields);

    /// <summary>Distinguishes a retained answer from the retained values of the same chunk.</summary>
    private const long AnswerKey = unchecked((long)0x8000_0000_0000_0000);

    /// <summary>
    /// The chunk's answer to the pushed comparison, asked of the encoding once per chunk.
    /// </summary>
    /// <param name="node">The flat layout node.</param>
    /// <param name="context">The scan context carrying the comparison.</param>
    /// <param name="key">The chunk's retention key.</param>
    /// <param name="total">The chunk's row count.</param>
    /// <param name="arena">The arena the answer is retained in.</param>
    /// <param name="answer">The answer's node in <paramref name="arena"/>, one state a row.</param>
    /// <returns><see langword="false"/> when the encoding declined.</returns>
    /// <remarks>
    /// It answers for the WHOLE chunk whatever the caller wants of it, because that is what can be
    /// retained: a chunk spans several batches and several selections, and an answer cut to one of
    /// them would have to be recomputed for the next. The callers window or gather out of it.
    /// </remarks>
    private bool TryRetainedAnswer(
        in LayoutNode node, ScanContext context, long key, int total,
        out CanonicalArena arena, out int answer)
    {
        long answerKey = key ^ AnswerKey;
        if (context.TryGetRetained(answerKey, out CanonicalArena hit, out int hitNode))
        {
            context.PredicateAnswered = true;
            arena = hit;
            answer = hitNode;
            return true;
        }

        arena = default!;
        answer = -1;

        ArrayNode root = LoadRoot(in node, context);
        ArrayDecoder decoder =
            ArrayDecoderTable.Require(context, root.Encoding, root.EncodingSpecIndex);
        if (!decoder.EvaluatesWithoutFullDecode)
        {
            return false;
        }

        byte[] rented = System.Buffers.ArrayPool<byte>.Shared.Rent(Math.Max(total, 1));
        try
        {
            Span<byte> states = rented.AsSpan(0, total);
            if (!decoder.TryCompare(
                    context.Decode, in root, node.DType, total,
                    context.PushedOp, context.PushedLiteral, states))
            {
                return false;
            }

            // Only the answer itself is retained. Whatever the encoding decoded to reach it -- a
            // dictionary's codes, say -- stays in the batch's arena and dies with the batch, which
            // is what keeps the retained arena the size of one bit a row.
            CanonicalArena held = context.BeginRetainedDecode();
            int retained = -1;
            try
            {
                retained = Answer(context, states, total);
            }
            finally
            {
                context.EndRetainedDecode(answerKey, retained);
            }

            context.PredicateAnswered = true;
            arena = held;
            answer = retained;
            return true;
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <summary>
    /// Offers the pushed comparison to this node's encoding, and turns an answer into the column.
    /// </summary>
    /// <param name="root">The node's parsed array root.</param>
    /// <param name="node">The flat layout node.</param>
    /// <param name="context">The scan context carrying the comparison.</param>
    /// <param name="total">The node's row count.</param>
    /// <returns>The canonical node holding the answer, or -1 when the encoding declined.</returns>
    /// <remarks>
    /// Asked here because this is where the decoder and the serialized node are both in hand and
    /// the blob has been parsed anyway, which is the same reason
    /// <see cref="ArrayDecoder.SelectsWithoutFullDecode"/> is asked a few lines below. The answer
    /// travels as a <c>Bool</c> column with validity, which is three-valued logic in the canonical
    /// model and needs no new form: true selects, false rejects, null is unknown.
    /// </remarks>
    private static int TryAnswer(
        in ArrayNode root, in LayoutNode node, ScanContext context, int total)
    {
        ArrayDecoder decoder =
            ArrayDecoderTable.Require(context, root.Encoding, root.EncodingSpecIndex);
        if (!decoder.EvaluatesWithoutFullDecode)
        {
            return -1;
        }

        byte[] rented = System.Buffers.ArrayPool<byte>.Shared.Rent(Math.Max(total, 1));
        try
        {
            Span<byte> states = rented.AsSpan(0, total);
            if (!decoder.TryCompare(
                    context.Decode, in root, node.DType, total,
                    context.PushedOp, context.PushedLiteral, states))
            {
                return -1;
            }

            context.PredicateAnswered = true;
            return Answer(context, states, total);
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <summary>Builds the <c>Bool</c> column one run of trilean states means.</summary>
    /// <param name="context">The scan context owning the arena.</param>
    /// <param name="states">One state per row.</param>
    /// <param name="total">The row count.</param>
    /// <returns>The canonical node's index.</returns>
    /// <remarks>
    /// Two bitmaps out of one byte array, and the second one only when a row is unknown: a
    /// comparison over a column with no null answers no unknown, and a validity bitmap nobody needs
    /// is a block rented and cleared for nothing.
    /// </remarks>
    private static int Answer(ScanContext context, ReadOnlySpan<byte> states, int total)
    {
        Arrays.CanonicalArena arena = context.Canonical;
        int bytes = Math.Max((total + 7) / 8, 1);

        VortexBuffer truth = arena.Allocate(bytes, 1, out Span<byte> bits);
        bits.Clear();
        bool unknown = false;
        for (int row = 0; row < total; row++)
        {
            byte state = states[row];
            if (state == Compute.Trilean.True)
            {
                bits[row >> 3] |= (byte)(1 << (row & 7));
            }
            else if (state == Compute.Trilean.Unknown)
            {
                unknown = true;
            }
        }

        Validity validity = Validity.NonNullable;
        if (unknown)
        {
            VortexBuffer known = arena.Allocate(bytes, 1, out Span<byte> valid);
            valid.Clear();
            for (int row = 0; row < total; row++)
            {
                if (states[row] != Compute.Trilean.Unknown)
                {
                    valid[row >> 3] |= (byte)(1 << (row & 7));
                }
            }

            validity = Validity.Bitmap(arena.AddBool(
                context.Types.Bool(Types.Nullability.NonNullable), total, Validity.NonNullable,
                known, 0));
        }

        return arena.AddBool(
            context.Types.Bool(unknown ? Types.Nullability.Nullable : Types.Nullability.NonNullable),
            total, validity, truth, 0);
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

    /// <summary>Gathers <see cref="ScanContext.Selection"/> out of a retained whole-node decode.</summary>
    /// <remarks>
    /// <para>
    /// Two steps and only the second moves bytes: the window is a full-width view onto the retained
    /// storage - records, no memory traffic, the property <see cref="CanonicalSlice.SliceAcross"/>
    /// exists for - and the gather then materializes exactly the wanted rows. The result borrows the
    /// retained arena, which <see cref="ScanContext.TryGetRetained"/> has just made un-evictable for
    /// the rest of this batch.
    /// </para>
    /// <para>
    /// The pruned paths hand this a contiguous selection every time, and a contiguous selection is a
    /// window that could be sliced rather than copied. It is not worth the branch: proved with one
    /// pass of integer comparisons and measured on the <c>filtered-pruned</c> scenario, which leaves
    /// one live block in each of 123 chunks of a million rows, slicing instead of gathering is worth
    /// 1,1 % on one column and 3,1 % on ten. The saving grows with the columns copied and stays far
    /// under what the branch would have to earn.
    /// </para>
    /// </remarks>
    private static int Gather(
        CanonicalArena held, int retained, in FieldMask fields, ScanContext context, int total)
    {
        int whole = CanonicalSlice.SliceAcross(held, context.Canonical, retained, 0, total);
        int taken = Compute.CanonicalFilter.Apply(context.Canonical, whole, context.Selection);
        return Narrowed(taken, in fields, context);
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
