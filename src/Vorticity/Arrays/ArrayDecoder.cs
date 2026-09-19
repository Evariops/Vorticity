// Phase 1 contract §2.4. An abstract CLASS, not an interface: the table holds ArrayDecoder[]
// indexed by ArrayEncodingId, so dispatch is one bounds-checked array index plus one virtual call
// PER ARRAY NODE - never per element (docs/03-architecture.md §4 invariant 3). Sealed overrides
// let the JIT devirtualize inside a loop that decodes many nodes of the same encoding.
using System;
using Vorticity.Types;

namespace Vorticity.Arrays;

/// <summary>
/// One decoder per serialized array encoding id. Stateless and thread-safe: a single instance is
/// shared by every scan, and all per-decode state lives in the context and the arenas.
/// </summary>
public abstract class ArrayDecoder
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
    /// The DType this node must produce. The node does not carry it (docs/02-format.md §5.2); the
    /// parent derives it per the table in contract §2.5.
    /// </param>
    /// <param name="length">The row count this node must produce. Also supplied by the parent.</param>
    /// <returns>The canonical node's index in <c>context.Canonical</c>.</returns>
    /// <remarks>
    /// Synchronous by design: by the time this runs every segment the batch needs is already
    /// materialized (docs/03-architecture.md §3.6). There is no <c>CancellationToken</c> because
    /// cancellation granularity is the batch. Recurse through
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
    /// <param name="length">The row count the node WOULD produce, which bounds the indices.</param>
    /// <param name="wanted">
    /// Row indices into this node, strictly ascending and all in <c>[0, length)</c>.
    /// </param>
    /// <returns>The canonical node's index in <c>context.Canonical</c>.</returns>
    /// <remarks>
    /// THE DEFAULT IS THE FALLBACK, and it is what every encoding did before any of them were
    /// specialized: decode the whole node, then gather. Correct for every encoding and wasteful for
    /// most, which is exactly the trade [90-registry.md](../../docs/90-registry.md)'s `take` table
    /// describes - it names the encodings worth specializing and documents the zone-decode fallback
    /// everywhere else, rather than pretending the fallback does not exist.
    ///
    /// An override must produce a node INDISTINGUISHABLE from the default's: same dtype, same
    /// validity, same values in the same order. `TakeSpecializationTests` asserts that against the
    /// default for every specialized encoding, which is the only way an optimization like this can
    /// be trusted.
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
    /// Whether <see cref="DecodeSelected"/> is overridden, i.e. whether this encoding can produce
    /// the wanted rows WITHOUT materializing the whole node.
    /// </summary>
    /// <remarks>
    /// THE CALLER OF THE FALLBACK NEEDS TO KNOW IT IS THE FALLBACK, and that is the whole reason
    /// this exists. `FlatLayoutReader` serves a take one batch at a time, so an encoding that lands
    /// on the default above decodes the SAME million-row node once per batch: 64 rows of
    /// `vortex.zstd` cost 427 ms against 7 ms for a full scan of that node, which is 59 scans of
    /// the file to deliver 64 rows (v2 R23). The cure is the retained-chunk cache the batch path
    /// already uses - decode the node once, gather out of it - and the cure is a REGRESSION for the
    /// ten encodings below, which reach one row without decoding a node at all: the `fsst` take
    /// reads 0.21 against the reference and a forced full decode would cost it 26.5 ms instead of
    /// 0.2. So the reader asks this before choosing, and only the fallback is rerouted.
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
    /// predicate, never per row, and false leaves today's single pass exactly as it is.
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
