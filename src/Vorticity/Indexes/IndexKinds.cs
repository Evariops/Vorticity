// The kind names of docs/10-indexes.md §5 and §6, spelled once.
//
// A KIND NAME IS WIRE FORMAT. It is written into the directory and read back by a reader that may
// predate the kind, whose only correct answer to a name it does not know is to ignore the entry
// (§4.1). So the names are versioned in themselves -- `.v1` -- and a change of layout, hash or
// meaning is a new name rather than an edit to an old one. Rebuilding an index is cheap; migrating
// one is not.
using System;

namespace Vorticity.Indexes;

/// <summary>The index kinds this library writes and reads, as they appear in the directory.</summary>
public static class IndexKinds
{
    /// <summary>A split-block Bloom filter per block or generation (docs/10-indexes.md §5.1).</summary>
    public const string BloomSbbf = "vorticity.bloom.sbbf.v1";

    /// <summary>A trigram Bloom for <c>LIKE</c> and <c>CONTAINS</c> (§5.2).</summary>
    public const string BloomNgram3 = "vorticity.bloom.ngram3.v1";

    /// <summary>
    /// "The chunks of this column are dictionary-encoded; probe the values child" (§5.3). The one
    /// kind with no payload at all.
    /// </summary>
    public const string DictProbe = "vorticity.dict.probe.v1";

    /// <summary>Value to blocks, a superset at block granularity (§6.1).</summary>
    public const string PostingsBlocks = "vorticity.postings.blocks.v1";

    /// <summary>Value to rows, exact, in log-structured runs (§6.2).</summary>
    public const string SortedRuns = "vorticity.sorted.runs.v1";

    /// <summary>Trigram to blocks (§6.4).</summary>
    public const string PostingsNgram3 = "vorticity.postings.ngram3.v1";

    /// <summary>
    /// Whether this library knows the kind. An unknown kind is ignored, never rejected: an index
    /// is a hint (docs/08-semantics.md §5) and a reader that cannot use one is only slower.
    /// </summary>
    /// <param name="kind">The name read from a directory.</param>
    /// <returns>Whether a builder or a probe exists for it here.</returns>
    public static bool IsKnown(string kind) =>
        kind switch
        {
            BloomSbbf or BloomNgram3 or DictProbe or PostingsBlocks or SortedRuns
                or PostingsNgram3 => true,
            _ => false,
        };

    /// <summary>
    /// Whether the kind carries payload segments. <see cref="DictProbe"/> is the one that does
    /// not: its whole content is the statement that the column's chunks are dictionary-encoded.
    /// </summary>
    /// <param name="kind">The name.</param>
    /// <returns>Whether a run of this kind must carry at least one payload segment.</returns>
    public static bool HasPayload(string kind) =>
        !string.Equals(kind, DictProbe, StringComparison.Ordinal);
}
