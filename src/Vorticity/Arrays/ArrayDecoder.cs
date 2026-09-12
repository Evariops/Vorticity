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
}
