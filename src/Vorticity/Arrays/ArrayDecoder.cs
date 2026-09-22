using System;
using Vorticity.Types;

namespace Vorticity.Arrays;

/// <summary>
/// One decoder per serialized array encoding id. Stateless and thread-safe: a single instance is
/// shared by every scan, and all per-decode state lives in the context and the arenas.
/// </summary>
/// <remarks>
/// A class rather than an interface because the dispatch table is an array indexed by encoding id:
/// reaching a decoder is one bounds-checked index plus one virtual call per array node, never per
/// element, and a sealed override devirtualizes inside a loop that decodes many nodes of the same
/// encoding.
/// </remarks>
internal abstract class ArrayDecoder
{
    /// <summary>
    /// The wire id, UTF-8, e.g. <c>"fastlanes.bitpacked"</c>. Must be a <c>u8</c> literal or a
    /// static readonly span - never allocated per call.
    /// </summary>
    public abstract ReadOnlySpan<byte> IdUtf8 { get; }

    /// <summary>The registry slot this decoder occupies.</summary>
    public abstract ArrayEncodingId EncodingId { get; }

    /// <summary>
    /// Decodes one serialized node into the canonical arena and returns the index of the canonical
    /// node it produced.
    /// </summary>
    /// <param name="context">Per-batch arenas, buffers, options and the decoder table.</param>
    /// <param name="node">The serialized node, a view into <c>context.Nodes</c>.</param>
    /// <param name="dtype">
    /// The DType this node must produce. The serialized node does not carry one, so the parent
    /// derives it and passes it down.
    /// </param>
    /// <param name="length">The row count this node must produce. Also supplied by the parent.</param>
    /// <returns>The canonical node's index in <c>context.Canonical</c>.</returns>
    /// <remarks>
    /// Synchronous by design: by the time this runs, every segment the batch needs is already
    /// materialized. There is no <c>CancellationToken</c> because cancellation granularity is the
    /// batch. Recurse through
    /// <see cref="ArrayDecodeContext.DecodeChild"/> and never by calling this method directly on a
    /// child, or the depth cap is skipped.
    /// </remarks>
    /// <exception cref="VortexFormatException">The node violates this encoding's contract.</exception>
    public abstract int Decode(ArrayDecodeContext context, in ArrayNode node, DType dtype, int length);

    /// <summary>
    /// Decodes only the rows at <paramref name="wanted"/>, producing a canonical node of
    /// <c>wanted.Length</c> rows.
    /// </summary>
    /// <param name="context">Per-batch arenas, buffers, options and the decoder table.</param>
    /// <param name="node">The serialized node.</param>
    /// <param name="dtype">The DType this node must produce.</param>
    /// <param name="length">The row count the whole node would produce, which bounds the indices.</param>
    /// <param name="wanted">
    /// Row indices into this node, strictly ascending and all in <c>[0, length)</c>.
    /// </param>
    /// <returns>The canonical node's index in <c>context.Canonical</c>.</returns>
    /// <remarks>
    /// The default is the fallback: decode the whole node, then gather. It is correct for every
    /// encoding and wasteful for most, so an encoding that can reach single rows cheaply overrides
    /// it.
    ///
    /// An override must produce a node indistinguishable from the default's: same dtype, same
    /// validity, same values in the same order. <c>TakeSpecializationTests</c> asserts exactly that
    /// against the default for every specialized encoding.
    /// </remarks>
    public virtual int DecodeSelected(
        ArrayDecodeContext context, in ArrayNode node, DType dtype, int length,
        ReadOnlySpan<int> wanted)
    {
        ArgumentNullException.ThrowIfNull(context);
        int decoded = Decode(context, in node, dtype, length);
        return Compute.CanonicalFilter.Apply(context.Canonical, decoded, wanted);
    }

    /// <summary>
    /// Whether <see cref="DecodeRange"/> can produce a contiguous range of this node's rows without
    /// materializing the rows outside it, given what <paramref name="node"/>'s children are encoded as.
    /// </summary>
    /// <param name="context">Per-batch arenas, buffers, options and the decoder table.</param>
    /// <param name="node">The serialized node.</param>
    /// <remarks>
    /// Asked per node rather than per encoding, because the answer is the encoding's and its
    /// children's together: a frame of reference over bit-packed codes slices, the same frame over
    /// a compressed blob does not. An encoding that answers <see langword="true"/> asks its children
    /// through <see cref="ArrayDecodeContext.ChildDecodesRange"/>. The flat layout reader decodes a
    /// chunk larger than its window in windows when the root answers yes, and whole otherwise.
    /// </remarks>
    public virtual bool DecodesRange(ArrayDecodeContext context, in ArrayNode node) => false;

    /// <summary>
    /// Whether this node's decode materializes nothing proportional to its rows: views onto its own
    /// buffers, or a single value, its children alike.
    /// </summary>
    /// <param name="context">Per-batch arenas, buffers, options and the decoder table.</param>
    /// <param name="node">The serialized node.</param>
    /// <param name="dtype">The DType the node decodes to.</param>
    /// <remarks>
    /// The flat layout reader decodes a chunk in windows to bound what it holds decoded at once, and
    /// a chunk whose decode materializes nothing holds the segment's bytes and a few records: a
    /// window of it would save no memory and cost a parse of the blob per window. Such a chunk is
    /// decoded whole, which for it means recording the views once.
    /// </remarks>
    public virtual bool MaterializesNothing(ArrayDecodeContext context, in ArrayNode node, DType dtype) => false;

    /// <summary>
    /// Decodes the rows <c>[start, start + count)</c> of this node, producing a canonical node of
    /// <paramref name="count"/> rows.
    /// </summary>
    /// <param name="context">Per-batch arenas, buffers, options and the decoder table.</param>
    /// <param name="node">The serialized node.</param>
    /// <param name="dtype">The DType this node must produce.</param>
    /// <param name="length">The row count the whole node would produce, which bounds the range.</param>
    /// <param name="start">The first row of the range.</param>
    /// <param name="count">How many rows, at least one.</param>
    /// <returns>The canonical node's index in <c>context.Canonical</c>.</returns>
    /// <remarks>
    /// Called only where <see cref="DecodesRange"/> answered <see langword="true"/> for the node,
    /// and expected to touch the range alone: what it produces must equal the same range sliced
    /// out of <see cref="Decode"/>, dtype, validity and values alike. The default refuses, since an
    /// encoding that decodes whole and slices would give the reader the memory cost it is asking
    /// this method to avoid.
    /// </remarks>
    /// <exception cref="NotSupportedException">The encoding does not decode ranges.</exception>
    public virtual int DecodeRange(
        ArrayDecodeContext context, in ArrayNode node, DType dtype, int length, int start, int count) =>
        throw new NotSupportedException(
            $"{System.Text.Encoding.UTF8.GetString(IdUtf8)} does not decode a range of its rows; ask DecodesRange first.");

    /// <summary>
    /// Whether <see cref="DecodeSelected"/> is overridden, i.e. whether this encoding can produce
    /// the wanted rows without materializing the whole node.
    /// </summary>
    /// <remarks>
    /// The caller of the fallback needs to know that it is the fallback, and that is the whole
    /// reason this exists. <c>FlatLayoutReader</c> serves a take one batch at a time, so an
    /// encoding left on the default decodes the same node again for every batch of the take. The
    /// cure is the retained-chunk cache the batch path already uses - decode the node once, gather
    /// out of it - and that same cure is a loss for an encoding that can reach a single row without
    /// decoding a node at all, because it forces the full decode it was avoiding. So the reader
    /// asks this first and reroutes only the fallback.
    ///
    /// <c>TakeSpecializationTests</c> asserts this flag against the declared method table, so it
    /// cannot drift from the overrides it claims to describe.
    /// </remarks>
    public virtual bool SelectsWithoutFullDecode => false;

    /// <summary>
    /// Whether <see cref="TryCompare"/> is overridden, i.e. whether this encoding can answer a
    /// comparison without materializing the whole node.
    /// </summary>
    /// <remarks>
    /// The guard, and the same one <see cref="SelectsWithoutFullDecode"/> puts in front of the
    /// take. Answering a predicate from the encoding lets a scan decode only the rows that
    /// survived, which means walking a split twice; paying for that where no encoding can answer
    /// would cost every filtered scan something for nothing. It is asked once per column and per
    /// predicate, never per row, and false leaves the single decoding pass exactly as it is.
    /// </remarks>
    public virtual bool EvaluatesWithoutFullDecode => false;

    /// <summary>
    /// Answers <paramref name="op"/> against <paramref name="literal"/> over this node's rows
    /// without decoding it, writing one <see cref="Compute.Trilean"/> state per row.
    /// </summary>
    /// <param name="context">Per-batch arenas, buffers, options and the decoder table.</param>
    /// <param name="node">The serialized node.</param>
    /// <param name="dtype">The DType this node would produce.</param>
    /// <param name="length">The row count this node would produce.</param>
    /// <param name="op">The comparison.</param>
    /// <param name="literal">Its right-hand side.</param>
    /// <param name="destination">Receives <paramref name="length"/> states.</param>
    /// <returns>
    /// <see langword="false"/> when this encoding will not answer this comparison, leaving
    /// <paramref name="destination"/> untouched and the caller to decode as it always has.
    /// </returns>
    /// <remarks>
    /// An override answers the same thing the kernels would answer over the decoded node, three
    /// valued and row for row: a dictionary compares the literal to its values and expands over its
    /// codes, and a compressed-string encoding compresses the needle rather than decompressing the
    /// column. Refusing per call rather than per encoding is deliberate -- an encoding that answers
    /// equality has no reason to answer an ordering, and saying so here costs one branch.
    /// </remarks>
    public virtual bool TryCompare(
        ArrayDecodeContext context, in ArrayNode node, DType dtype, int length,
        Expressions.ComparisonOp op, Expressions.FilterLiteral literal, Span<byte> destination) =>
        false;
}
