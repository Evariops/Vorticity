using System;

namespace Vorticity.Indexes;

/// <summary>
/// The index kinds this library writes and reads, as they appear in the directory. A kind name is
/// wire format, read back by readers that may predate the kind, so each name carries its own
/// version and a change of layout, hash or meaning takes a new name rather than editing an old one:
/// rebuilding an index is cheap, migrating one is not.
/// </summary>
internal static class IndexKinds
{
    /// <summary>A split-block Bloom filter per block or generation.</summary>
    public const string BloomSbbf = "vorticity.bloom.sbbf.v1";

    /// <summary>A trigram Bloom, for the substring and pattern predicates on a string column.</summary>
    public const string BloomNgram3 = "vorticity.bloom.ngram3.v1";

    /// <summary>
    /// "The chunks of this column are dictionary-encoded; probe the values child". The one kind
    /// with no payload at all.
    /// </summary>
    public const string DictProbe = "vorticity.dict.probe.v1";

    /// <summary>Value to blocks, a superset at block granularity.</summary>
    public const string PostingsBlocks = "vorticity.postings.blocks.v1";

    /// <summary>Value to rows, exact, in log-structured runs.</summary>
    public const string SortedRuns = "vorticity.sorted.runs.v1";

    /// <summary>Trigram to blocks.</summary>
    public const string PostingsNgram3 = "vorticity.postings.ngram3.v1";

    /// <summary>
    /// The time slices of a file, on its instant column: per slice its bounds, its first row and its
    /// row count, held in the entry's options with no payload (<see cref="SliceTable"/>).
    /// </summary>
    public const string Slices = "vorticity.slices.v1";

    /// <summary>
    /// Where the chunks of every top-level column lie and the array encodings their trees name, on
    /// the root, the encodings in its options and a payload region per column: what reads a slice's
    /// rows without the footer (<see cref="Indexes.SliceChunks"/>).
    /// </summary>
    public const string SliceChunks = "vorticity.slices.chunks.v1";

    /// <summary>
    /// Where each shredded key of a variant column lies, chunk by chunk, on that column: the
    /// encodings and the keys in its options, and a payload region per key holding the range of its
    /// buffers and its tree in each chunk, so a read of one key fetches its bytes alone
    /// (<see cref="VariantPathMap"/>).
    /// </summary>
    public const string VariantPaths = "vorticity.variant.paths.v1";

    /// <summary>
    /// Every path and class a variant column's rows hold in the file, on that column: each its count,
    /// its bounds and where it lies, in one payload region, so that a question on a path the file
    /// lacks, or outside its bounds, refutes the file (<see cref="VariantDirectory"/>).
    /// </summary>
    public const string VariantDirectory = "vorticity.variant.directory.v1";

    /// <summary>
    /// Whether this library knows the kind. An unknown kind is ignored, never rejected: an index
    /// is only a hint, so a reader that cannot use one is slower and never wrong.
    /// </summary>
    /// <param name="kind">The name read from a directory.</param>
    /// <returns>Whether a builder or a probe exists for it here.</returns>
    public static bool IsKnown(string kind) =>
        kind switch
        {
            BloomSbbf or BloomNgram3 or DictProbe or PostingsBlocks or SortedRuns
                or PostingsNgram3 or Slices or SliceChunks or VariantPaths or VariantDirectory => true,
            _ => false,
        };

    /// <summary>
    /// Whether the kind carries payload segments. <see cref="DictProbe"/> does not, its whole content
    /// being the statement that the column's chunks are dictionary-encoded, and neither does
    /// <see cref="Slices"/>, whose table fits in its options.
    /// </summary>
    /// <param name="kind">The name.</param>
    /// <returns>Whether a run of this kind must carry at least one payload segment.</returns>
    public static bool HasPayload(string kind) =>
        kind switch
        {
            DictProbe or Slices => false,
            _ => true,
        };

    /// <summary>The kind a policy of <paramref name="policy"/> builds, or null for a policy that names none.</summary>
    /// <param name="policy">The policy's kind.</param>
    /// <returns>The kind's name in the directory.</returns>
    internal static string? Of(IndexPolicyKind policy) =>
        policy switch
        {
            IndexPolicyKind.Bloom => BloomSbbf,
            IndexPolicyKind.NgramBloom => BloomNgram3,
            IndexPolicyKind.Postings => PostingsBlocks,
            IndexPolicyKind.SortedRuns => SortedRuns,
            IndexPolicyKind.NgramPostings => PostingsNgram3,
            _ => null,
        };
}
