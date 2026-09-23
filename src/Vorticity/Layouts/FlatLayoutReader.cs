using System;

using Vorticity.Arrays;
using Vorticity.Arrays.Metadata;
using Vorticity.Buffers;
using Vorticity.IO;

namespace Vorticity.Layouts;

/// <summary>
/// Reads a <c>vortex.flat</c> layout: the terminal layout, exactly one segment holding one
/// serialized array and no children at all. The segment is
/// <c>[padding][buffer 0]..[array flatbuffer][u32 flatbuffer length]</c> and the encoding tree is
/// normally read off its tail; when the metadata carries an inline encoding tree the same tree is
/// read from there instead, over byte-identical segment bytes, and the buffer walk simply never
/// reaches the tail.
/// </summary>
internal sealed class FlatLayoutReader : LayoutReader
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
    /// Internal and diagnostic. Maintaining it costs one interlocked add per decoded node, beside a
    /// decode of that whole node, so it does not show; without it, a node decoded far more often
    /// than the rows delivered justify is invisible to the tests.
    /// </remarks>
    internal static long ValuesDecoded;

    /// <summary>
    /// Counts <paramref name="values"/> materialized: on the process-wide counter the decode-count
    /// tests read, and on the scan's own sink when it has one.
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

        // The blob is parsed where it is read, not on the way past. Loading it means resetting the
        // node arena, copying the array flatbuffer into it, walking the node tree and resolving
        // every buffer spec in the segment -- and none of that survives the call. A batch that
        // finds its chunk already decoded and retained never touches the root, so parsing it up
        // front would pay for every batch of a chunk to serve the one that needs it.
        //
        // The three readers below each load it themselves, immediately before the only use.

        // The selection is in the same space as `rows`, and a flat layout's space is the segment's,
        // so the wanted rows need no translation at all - which is the whole reason the selection
        // is carried in the row argument's coordinate space rather than in absolute file rows.
        if (context.HasSelection)
        {
            return ExecuteSelected(in node, rows, in fields, context, total, length);
        }

        // Dead blocks in this node are handled a level up and not here: this library wraps every
        // column in `vortex.chunked`, even for a single chunk, and a chunked reader clears the mask
        // for its children and says what it means in the selection, so those rows arrive through
        // the selection branch above. A column that is a bare flat node carries no zone map, so its
        // mask never has a dead block either; were that shape to occur, the retained decode below
        // reads it correctly, which is what the chunked form pays for its first batch anyway.

        // A whole-node batch retains nothing, because there is no second batch to serve. This is
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

        // A chunk larger than a batch is decoded once, not once per batch: decoding `total` and
        // slicing `length` out of it per batch is correct and quadratic, since every batch of the
        // chunk would decode the whole chunk again to deliver its own window of it.
        //
        // The retained node lives in an arena `ResetBatch` does not touch, and the window costs
        // nothing but records: `SliceAcross` appends to the batch's arena records whose buffers are
        // narrowed views onto the retained storage. No bytes move however many rows the window spans,
        // and a VarBinView's data buffers travel as views rather than being rebuilt per batch, which
        // is what the string encodings needed.
        //
        // The batch therefore borrows the retained arena. `ScanContext.Retain` owes the lifetime
        // argument and makes it: an entry touched during the current batch is never evicted.
        long key = ScanContext.SegmentKey(node.Segments[0]);

        // The answer is retained like the chunk is, and for the same reason: a chunk spans several
        // batches, so asking the encoding once per batch would ask it once per batch for one
        // answer. It is kept under a key of its own because the same chunk can be wanted both as an
        // answer and as values, and a cache that confused the two would hand a boolean column to a
        // caller that asked for strings.
        if (context.PredicateAtNode &&
            TryRetainedAnswer(in node, context, key, total, out CanonicalArena states, out int said))
        {
            return CanonicalSlice.SliceAcross(states, context.Canonical, said, (int)rows.Start, length);
        }

        // A chunk a batch before this one decoded whole is sliced: the one lookup a batch of a chunk
        // whose decode is a set of views pays.
        if (Windowed(context, total) && context.TryPeekRetained(key, out CanonicalArena whole, out int wholeNode))
        {
            return Narrowed(
                CanonicalSlice.SliceAcross(whole, context.Canonical, wholeNode, (int)rows.Start, length), in fields, context);
        }

        // A chunk larger than a window is decoded a window at a time when its encoding can decode a
        // range of its rows, so that what a scan holds decoded at once is a window of each column
        // and not a chunk of each: a chunk of half a million rows is many megabytes a column, and
        // a batch carved out of it reads memory the caches have long given up on, where a window
        // stays in the second-level cache from its decode to its last batch.
        ArrayNode root = default;
        bool loaded = false;
        if (Windowed(context, total) &&
            TryWindow(in node, rows, in fields, context, total, length, ref root, ref loaded, out int windowed))
        {
            return windowed;
        }

        if (!context.TryGetRetained(key, out CanonicalArena held, out int retained))
        {
            Decoded(context, total);

            // Decoded straight into the arena that retains it. Decoding into the batch's arena and
            // deep-copying into one that outlives it would be a second full pass over every byte.
            // `Canonical` is redirected for the duration of this one decode and nothing else
            // changes: the lifetime argument was always about which arena the result lives in, and
            // it lives in the same one.
            ArrayNode chunkRoot = loaded ? root : LoadRoot(in node, context);
            held = context.BeginRetainedDecode();
            retained = -1;
            try
            {
                retained = Decoded(in chunkRoot, in node, in fields, context, total);
            }
            finally
            {
                context.EndRetainedDecode(retained);
            }
        }

        int sliced = CanonicalSlice.SliceAcross(held, context.Canonical, retained, (int)rows.Start, length);
        return Narrowed(sliced, in fields, context);
    }

    /// <summary>
    /// The most rows a window of a chunk holds: sixteen batches of the default size, a megabyte of
    /// fixed-width values or two of string views per column, about what the reference decodes at
    /// once. A window pays a parse of the chunk's blob and of its encodings' metadata, so a smaller
    /// one makes a cheap encoding pay that parse more often than the memory it saves is worth.
    /// </summary>
    internal const int WindowRows = 1 << 17;

    /// <summary>
    /// Serves the batch out of the window the scan's plan put it in, decoding the window on its
    /// first batch, when the chunk's encoding decodes a range of its rows and materializes them.
    /// </summary>
    /// <param name="node">The flat layout node.</param>
    /// <param name="rows">The batch's rows, in the node's space.</param>
    /// <param name="fields">The projection.</param>
    /// <param name="context">The scan context.</param>
    /// <param name="total">The node's row count.</param>
    /// <param name="length">The batch's row count.</param>
    /// <param name="root">Receives the parsed root when this had to load it, for the caller to reuse.</param>
    /// <param name="loaded">Whether <paramref name="root"/> was loaded.</param>
    /// <param name="result">The batch's node, when served.</param>
    /// <returns><see langword="false"/> when the chunk is to be decoded whole.</returns>
    /// <remarks>
    /// A window is whole batches, as many as fit in <see cref="WindowRows"/>: the plan cuts a span
    /// into batches of one size but the last, and groups them, so no batch straddles two windows
    /// and every batch of a window, the last included, is given the same one. A window is retained
    /// under a key of its own and evicted as a chunk is, once no later batch borrows it; a window
    /// of a single batch is not retained at all, the batch decoding its own rows.
    /// </remarks>
    private bool TryWindow(
        in LayoutNode node, RowRange rows, in FieldMask fields, ScanContext context, int total, int length,
        ref ArrayNode root, ref bool loaded, out int result)
    {
        result = -1;
        if (!WindowOf(context, rows, length, total, out int windowStart, out int windowLength))
        {
            return false;
        }

        int into = (int)(rows.Start - windowStart);
        long? key = windowLength > length ? ScanContext.WindowKey(node.Segments[0], windowStart) : null;
        if (key is long found &&
            context.TryPeekRetained(found, out CanonicalArena hit, out int hitNode) &&
            Holds(hit, hitNode, into + length))
        {
            result = Narrowed(CanonicalSlice.SliceAcross(hit, context.Canonical, hitNode, into, length), in fields, context);
            return true;
        }

        if (!Ranged(in node, context, ref root, ref loaded))
        {
            return false;
        }

        if (key is long claimed &&
            TryAcquireWindow(in root, in node, in fields, context, total, windowStart, windowLength, claimed, out CanonicalArena held, out int window) &&
            Holds(held, window, into + length))
        {
            result = Narrowed(CanonicalSlice.SliceAcross(held, context.Canonical, window, into, length), in fields, context);
            return true;
        }

        // A window of this one batch, or one a window of another shape already holds the key of: the
        // batch's rows are decoded as a range of their own, into the batch, and retained by nothing.
        result = Narrowed(DecodedDirect(in root, in node, in fields, context, total, (int)rows.Start, length), in fields, context);
        return true;
    }

    /// <summary>
    /// Whether the scan grouped the batch in flight into a window smaller than this node, which is
    /// when a window saves anything: a window holding the whole chunk is the chunk decoded whole.
    /// </summary>
    private static bool Windowed(ScanContext context, int total) =>
        context.WindowSpan > 0 && context.WindowSpan < total;

    /// <summary>
    /// The window of this node the batch in flight belongs to, in the node's row space, when the
    /// scan grouped its batches into windows and the window lies inside the node.
    /// </summary>
    /// <param name="context">The scan context, carrying the window the scan's plan gave the batch.</param>
    /// <param name="rows">The batch's rows, in the node's space.</param>
    /// <param name="length">The batch's row count.</param>
    /// <param name="total">The node's row count.</param>
    /// <param name="windowStart">The window's first row, in the node's space.</param>
    /// <param name="windowLength">The window's row count.</param>
    /// <returns><see langword="false"/> when no window was given, or the one given does not fit this node's rows.</returns>
    private static bool WindowOf(
        ScanContext context, RowRange rows, int length, int total, out int windowStart, out int windowLength)
    {
        windowLength = context.WindowSpan;
        long start = rows.Start - context.WindowLead;
        windowStart = (int)Math.Max(start, 0);
        return windowLength > 0 && start >= 0 && context.WindowLead + (long)length <= windowLength &&
            start + windowLength <= total;
    }

    /// <summary>
    /// Whether the chunk's root decodes a range of its rows and materializes them, parsing the blob
    /// first when no one has: a chunk whose decode is a set of views onto the segment holds nothing
    /// a window would not, and a window of it would parse the blob once per window for no saving.
    /// </summary>
    private bool Ranged(in LayoutNode node, ScanContext context, ref ArrayNode root, ref bool loaded)
    {
        if (!loaded)
        {
            root = LoadRoot(in node, context);
            loaded = true;
        }

        return context.Decode.DecodesRange(in root) && !context.Decode.MaterializesNothing(in root, node.DType);
    }

    /// <summary>Whether a retained window holds the rows up to <paramref name="end"/> of it.</summary>
    /// <remarks>
    /// A window's key names where it starts and not how long it is, so this is what makes a window
    /// found under a batch's key the rows the batch wants: every batch of a scan is handed its
    /// window by the one plan, and a reader running a child over rows in another space may land on
    /// a window cut for other rows, which is then left alone rather than sliced past its end.
    /// </remarks>
    private static bool Holds(CanonicalArena arena, int nodeIndex, int end) =>
        arena.GetNode(nodeIndex).Length >= end;

    /// <summary>
    /// The window of the chunk starting at <paramref name="windowStart"/>, retained: found under
    /// <paramref name="key"/>, or decoded now into the arena that retains it.
    /// </summary>
    /// <param name="root">The chunk's parsed root.</param>
    /// <param name="node">The flat layout node.</param>
    /// <param name="fields">The projection.</param>
    /// <param name="context">The scan context.</param>
    /// <param name="total">The node's row count.</param>
    /// <param name="windowStart">The window's first row.</param>
    /// <param name="windowLength">The window's row count.</param>
    /// <param name="key">The window's retention key.</param>
    /// <param name="arena">The arena holding the window.</param>
    /// <param name="nodeIndex">The window's node in <paramref name="arena"/>.</param>
    /// <returns>Always <see langword="true"/>: another context decoding the window is waited for.</returns>
    /// <remarks>
    /// The O(n) checks a node's side tables need are remembered from one window to the next under
    /// the node's check scope, and its shared children are retained once for every window of it:
    /// they are facts about the node, and every window of it is the same node.
    /// </remarks>
    private static bool TryAcquireWindow(
        in ArrayNode root, in LayoutNode node, in FieldMask fields, ScanContext context, int total,
        int windowStart, int windowLength, long key, out CanonicalArena arena, out int nodeIndex)
    {
        if (context.TryGetRetained(key, out arena, out nodeIndex))
        {
            return true;
        }

        Decoded(context, windowLength);
        arena = context.BeginRetainedDecode();
        nodeIndex = -1;
        uint? outer = context.Decode.BeginNodeCheckScope(node.Segments[0]);
        try
        {
            nodeIndex = DecodedRange(in root, in node, in fields, context, total, windowStart, windowLength);
        }
        finally
        {
            context.Decode.EndNodeCheckScope(outer);
            context.EndRetainedDecode(nodeIndex);
        }

        return true;
    }

    /// <summary>
    /// Decodes rows <c>[start, start + count)</c> of this node into the batch, retained by nothing,
    /// under the node's check scope so that its checks and shared children serve every batch.
    /// </summary>
    private static int DecodedDirect(
        in ArrayNode root, in LayoutNode node, in FieldMask fields, ScanContext context, int total, int start, int count)
    {
        Decoded(context, count);
        uint? outer = context.Decode.BeginNodeCheckScope(node.Segments[0]);
        try
        {
            return DecodedRange(in root, in node, in fields, context, total, start, count);
        }
        finally
        {
            context.Decode.EndNodeCheckScope(outer);
        }
    }

    /// <summary>
    /// Gathers <see cref="ScanContext.Selection"/> out of the window the scan's plan put the batch
    /// in, found retained or, when <paramref name="mayDecode"/>, decoded now; or, for a selection
    /// too sparse to pay for a window, out of the range it spans, decoded for it alone.
    /// </summary>
    /// <param name="node">The flat layout node.</param>
    /// <param name="rows">The batch's rows, in the node's space; the selection lies inside them.</param>
    /// <param name="fields">The projection.</param>
    /// <param name="context">The scan context, carrying the selection in the node's space.</param>
    /// <param name="total">The node's row count.</param>
    /// <param name="length">The batch's row count.</param>
    /// <param name="mayDecode">Whether anything not yet retained may be decoded, which parses the blob.</param>
    /// <param name="root">Receives the parsed root when this had to load it, for the caller to reuse.</param>
    /// <param name="loaded">Whether <paramref name="root"/> holds the parsed root.</param>
    /// <param name="result">The gathered node, when served.</param>
    /// <returns><see langword="false"/> when the selection is empty, no window was given, or the chunk is not to be read in ranges.</returns>
    /// <remarks>
    /// A selection spanning at least half its batch is the rows of a pruned or filtered scan, which
    /// the next batches of the window want too, so the window is decoded and kept; a sparser one is
    /// a take, which wants a few rows of each batch, and the range it spans is all it decodes.
    /// </remarks>
    private bool TryWindowSelected(
        in LayoutNode node, RowRange rows, in FieldMask fields, ScanContext context, int total, int length,
        bool mayDecode, ref ArrayNode root, ref bool loaded, out int result)
    {
        result = -1;
        if (!context.TryGetSelectionBounds(out int from, out int last) ||
            !WindowOf(context, rows, length, total, out int windowStart, out int windowLength))
        {
            return false;
        }

        int end = last - windowStart + 1;
        long? key = windowLength > length ? ScanContext.WindowKey(node.Segments[0], windowStart) : null;
        if (key is long found &&
            context.TryPeekRetained(found, out CanonicalArena hit, out int hitNode) &&
            Holds(hit, hitNode, end))
        {
            result = Narrowed(GatherRebased(context, WholeOf(hit, hitNode, context), windowStart), in fields, context);
            return true;
        }

        if (!mayDecode || !Ranged(in node, context, ref root, ref loaded))
        {
            return false;
        }

        int spanned = last + 1 - from;
        if (key is long claimed && 2L * spanned >= length &&
            TryAcquireWindow(in root, in node, in fields, context, total, windowStart, windowLength, claimed, out CanonicalArena held, out int window) &&
            Holds(held, window, end))
        {
            result = Narrowed(GatherRebased(context, WholeOf(held, window, context), windowStart), in fields, context);
            return true;
        }

        int decoded = DecodedDirect(in root, in node, in fields, context, total, from, spanned);
        result = Narrowed(
            spanned == context.SelectionCount ? decoded : GatherRebased(context, decoded, from), in fields, context);
        return true;
    }

    /// <summary>
    /// Whether the selection holds at least half the rows between its first and its last, and at
    /// least a block of them: below a block a range decode unpacks a whole block for a few rows,
    /// which the selective route does not.
    /// </summary>
    private static bool Dense(ScanContext context) =>
        context.SelectionCount >= Arrays.Decoders.Compressed.FastLanes.BlockSize &&
        context.TryGetSelectionBounds(out int first, out int last) &&
        2L * context.SelectionCount >= (long)last - first + 1;

    /// <summary>
    /// The selection's rows of <paramref name="nodeIndex"/>, a node of the batch's arena in the
    /// selection's space: a slice, views and no copy, when they are a range, a gather otherwise.
    /// </summary>
    private static int Selected(ScanContext context, int nodeIndex) =>
        context.TryGetSelectionRange(out int first, out int count)
            ? CanonicalSlice.SliceAcross(context.Canonical, context.Canonical, nodeIndex, first, count)
            : Compute.CanonicalFilter.Apply(context.Canonical, nodeIndex, context.Selection);

    /// <summary>The whole of a retained window, as records in the batch's arena viewing the retained storage.</summary>
    private static int WholeOf(CanonicalArena held, int retained, ScanContext context) =>
        CanonicalSlice.SliceAcross(held, context.Canonical, retained, 0, held.GetNode(retained).Length);

    /// <summary>Gathers the selection, in the chunk's space, out of a node whose first row is <paramref name="origin"/>.</summary>
    /// <remarks>
    /// A range is a slice of the node; other indices move by the node's start before the gather: a
    /// batch's selection is a few thousand rows, rebased into a rented buffer.
    /// </remarks>
    private static int GatherRebased(ScanContext context, int nodeIndex, int origin)
    {
        if (context.TryGetSelectionRange(out int first, out int count))
        {
            return CanonicalSlice.SliceAcross(context.Canonical, context.Canonical, nodeIndex, first - origin, count);
        }

        ReadOnlySpan<int> selection = context.Selection;
        int[] rented = System.Buffers.ArrayPool<int>.Shared.Rent(selection.Length);
        try
        {
            Span<int> rebased = rented.AsSpan(0, selection.Length);
            for (int i = 0; i < selection.Length; i++)
            {
                rebased[i] = selection[i] - origin;
            }

            return Compute.CanonicalFilter.Apply(context.Canonical, nodeIndex, rebased);
        }
        finally
        {
            System.Buffers.ArrayPool<int>.Shared.Return(rented);
        }
    }

    /// <summary>Decodes rows <c>[start, start + count)</c> of this node under <paramref name="fields"/>, as <see cref="Decoded(in ArrayNode, in LayoutNode, in FieldMask, ScanContext, int)"/> decodes the whole.</summary>
    private static int DecodedRange(
        in ArrayNode root, in LayoutNode node, in FieldMask fields, ScanContext context, int total, int start, int count)
    {
        if (fields.IsAll)
        {
            return context.Decode.DecodeRootRange(in root, node.DType, total, start, count, context.KeepEncodings);
        }

        FieldMask outer = context.ExchangePushedFields(fields);
        try
        {
            return context.Decode.DecodeRootRange(in root, node.DType, total, start, count, context.KeepEncodings);
        }
        finally
        {
            context.ExchangePushedFields(outer);
        }
    }

    /// <summary>
    /// Produces the selected rows of this node, through the retained chunk when the encoding's
    /// <c>DecodeSelected</c> is the whole-node fallback.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The batch branch above serves one batch at a time and the selection is split across batches
    /// too, so an encoding that lands on <see cref="ArrayDecoder.DecodeSelected"/>'s default would
    /// decode the same node once per batch - in the worst case once per wanted row, decoding the
    /// whole node each time. The cure is the cache the batch path already has, used for the same
    /// reason.
    /// </para>
    /// <para>
    /// It is gated, because for the encodings that override <c>DecodeSelected</c> it would be a
    /// regression rather than a cure: they reach one row without materializing a node at all, and
    /// forcing a full decode on them throws that away.
    /// <see cref="ArrayDecoder.SelectsWithoutFullDecode"/> is the question, asked of the root
    /// decoder - a specialized root pushes the selection into its own children, and what those
    /// children are is its business, not this reader's.
    /// </para>
    /// <para>
    /// The hit path never parses the blob: the retained entry is looked up before
    /// <see cref="LoadRoot"/>, so only the first batch of a take touches the node arena at all.
    /// </para>
    /// </remarks>
    private int ExecuteSelected(
        in LayoutNode node, RowRange rows, in FieldMask fields, ScanContext context, int total, int length)
    {
        // Nothing to retain when this call is served the whole node, which is the same argument the
        // whole-node batch above makes: no second call will come for the rest of it, so an entry
        // would be created, used once and evicted. It is not free to get this wrong - a take over
        // many small chunks would retain every one of them, and these paths are held to a ceiling
        // in bytes.
        //
        // The predicate is asked here too, because the selective path is where a selective
        // predicate actually lands: a column with a zone map whose blocks the scan can kill reaches
        // this reader with a selection in force, and that is exactly the shape a selective equality
        // makes. Answering only on the branch above would answer only for the columns whose data
        // defeats pruning. The answer is for the whole node and the selection is gathered out of
        // it, which is what `Gather` does for values and for the same reason.
        if (context.PredicateAtNode)
        {
            long answerKey = ScanContext.SegmentKey(node.Segments[0]);
            if (TryRetainedAnswer(
                    in node, context, answerKey, total, out CanonicalArena answered, out int answer))
            {
                int whole = CanonicalSlice.SliceAcross(answered, context.Canonical, answer, 0, total);
                return Selected(context, whole);
            }
        }

        if (length < total)
        {
            // A look before a claim: whether the chunk is decoded at all is the encoding's to say,
            // and the blob has to be parsed to ask it, so the retained entry is looked for first and
            // claimed only for a decode.
            long key = ScanContext.SegmentKey(node.Segments[0]);
            if (context.TryPeekRetained(key, out CanonicalArena hit, out int hitNode))
            {
                return Gather(hit, hitNode, in fields, context, total);
            }

            // The window the batch falls in, when a batch of this chunk has already decoded it:
            // gathering out of a decoded window beats every route below, the specialized one
            // included, and looking costs a lookup.
            ArrayNode chunkRoot = default;
            bool loaded = false;
            if (Windowed(context, total) &&
                TryWindowSelected(in node, rows, in fields, context, total, length, mayDecode: false, ref chunkRoot, ref loaded, out int gathered))
            {
                return gathered;
            }

            if (!loaded)
            {
                chunkRoot = LoadRoot(in node, context);
            }

            if (!ArrayDecoderTable
                    .Require(context, chunkRoot.Encoding, chunkRoot.EncodingSpecIndex)
                    .SelectsWithoutFullDecode)
            {
                // Where the whole chunk would be decoded to gather a few rows out of it, a window of
                // it, or the range the selection spans, is decoded instead when the encoding can.
                if (Windowed(context, total) &&
                    TryWindowSelected(in node, rows, in fields, context, total, length, mayDecode: true, ref chunkRoot, ref loaded, out gathered))
                {
                    return gathered;
                }

                if (!context.TryGetRetained(key, out CanonicalArena held, out int retained))
                {
                    Decoded(context, total);
                    held = context.BeginRetainedDecode();
                    retained = -1;
                    try
                    {
                        retained = context.Decode.DecodeRoot(in chunkRoot, node.DType, total, context.KeepEncodings);
                    }
                    finally
                    {
                        context.EndRetainedDecode(retained);
                    }
                }

                return Gather(held, retained, in fields, context, total);
            }

            // A selection covering most of the rows it spans is a range to the encoding, even one
            // that takes rows without decoding: decoding the span and gathering out of it touches
            // each row once in a kernel's stride, where the selective route pays a positioned read
            // of every row. The rows a partly pruned chunk wants are such a selection, contiguous.
            if (Dense(context) && Windowed(context, total) &&
                TryWindowSelected(in node, rows, in fields, context, total, length, mayDecode: true, ref chunkRoot, ref loaded, out gathered))
            {
                return gathered;
            }

            // The specialized route re-establishes its own invariants once per batch, and this is
            // where that stops. The encoding takes its rows without decoding the node -- that part
            // works -- but the O(n) checks it runs on its side tables are facts about the node, not
            // about the selection, so an encoding like `vortex.runend` or ALP re-walks its whole
            // run-end or patch table on every visit. The scope is opened only here, under
            // `length < total`, because below that the node is visited once and there is nothing to
            // remember.
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
    /// out of fifty decodes all fifty, which costs about as much as not projecting at all.
    /// </remarks>
    private static int Decoded(
        in ArrayNode root, in LayoutNode node, in FieldMask fields, ScanContext context, int total)
    {
        // A whole projection has nothing to push, and saying so here is what keeps an unprojected
        // scan from allocating the holder at all: these paths are held to a ceiling in bytes.
        if (fields.IsAll)
        {
            return context.Decode.DecodeRoot(in root, node.DType, total, context.KeepEncodings);
        }

        FieldMask outer = context.ExchangePushedFields(fields);
        try
        {
            return context.Decode.DecodeRoot(in root, node.DType, total, context.KeepEncodings);
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
    /// It answers for the whole chunk whatever the caller wants of it, because that is what can be
    /// retained: a chunk spans several batches and several selections, and an answer cut to one of
    /// them would have to be recomputed for the next. The callers window or gather out of it.
    /// </remarks>
    private bool TryRetainedAnswer(
        in LayoutNode node, ScanContext context, long key, int total,
        out CanonicalArena arena, out int answer)
    {
        // A look before a claim: an encoding that declines is asked again by every batch of the
        // chunk, and a claim made and given back each time would churn an entry per batch.
        long answerKey = key ^ AnswerKey;
        if (context.TryPeekRetained(answerKey, out CanonicalArena hit, out int hitNode))
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

            // Another lane may have published the answer since the look; otherwise this one is
            // claimed and published. Only the answer itself is retained. Whatever the encoding
            // decoded to reach it -- a dictionary's codes, say -- stays in the batch's arena and
            // dies with the batch, which is what keeps the retained arena the size of one bit a row.
            if (!context.TryGetRetained(answerKey, out CanonicalArena held, out int retained))
            {
                held = context.BeginRetainedDecode();
                retained = -1;
                try
                {
                    retained = Answer(context, states, total);
                }
                finally
                {
                    context.EndRetainedDecode(retained);
                }
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

    /// <summary>Lets the encoding take the wanted rows itself, without materializing the node.</summary>
    private static int Push(
        in ArrayNode root, in LayoutNode node, in FieldMask fields, ScanContext context, int total)
    {
        // Counted like the whole-node decodes above: what a positional take materializes is its
        // selection, and a decode no instrument counts is a decode nobody sees.
        Decoded(context, context.SelectionCount);
        int taken = context.Decode.DecodeRootSelected(
            in root, node.DType, total, context.Selection, context.KeepEncodings);
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
    /// The pruned paths hand this a range every time, which is sliced rather than copied, and never
    /// written out as indices.
    /// </para>
    /// </remarks>
    private static int Gather(
        CanonicalArena held, int retained, in FieldMask fields, ScanContext context, int total)
    {
        if (context.TryGetSelectionRange(out int first, out int count))
        {
            return Narrowed(
                CanonicalSlice.SliceAcross(held, context.Canonical, retained, first, count), in fields, context);
        }

        int whole = CanonicalSlice.SliceAcross(held, context.Canonical, retained, 0, total);
        int taken = Compute.CanonicalFilter.Apply(context.Canonical, whole, context.Selection);
        return Narrowed(taken, in fields, context);
    }

    /// <summary>
    /// Parses this layout's array blob into the scan's node arena and returns its root.
    /// </summary>
    /// <remarks>
    /// The contained array's dtype is exactly the node's and its length exactly the node's row
    /// count; neither is carried by the array blob itself.
    /// </remarks>
    /// <param name="node">The flat layout node.</param>
    /// <param name="context">The scan context owning the node arena.</param>
    /// <returns>The root of the parsed blob.</returns>
    private ArrayNode LoadRoot(scoped in LayoutNode node, ScanContext context)
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
