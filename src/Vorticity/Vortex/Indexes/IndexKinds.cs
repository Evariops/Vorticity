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
    /// Value to blocks through the value's hash, a superset at block granularity: each run holds its
    /// (hash, block) pairs sorted by hash, the hashes cut to as many bits as its pairs need plus ten,
    /// per segment as gaps beside the blocks (<see cref="KeyRunOptions.HashShift"/>).
    /// </summary>
    public const string PostingsHashed = "vorticity.postings.hashed.v1";

    /// <summary>
    /// The sparse index of a sorted object, on the root: the key of the first row of each granule, the prefix of the sort
    /// key the object indexes, and the key of its last row, in its options a table of pages and their first keys, its pages in
    /// the payload, none when the table keeps the one page (<see cref="Indexes.GranuleKeys"/>). It replaces the slices of
    /// <c>vorticity.slices.v1</c>, retired.
    /// </summary>
    public const string KeyIndex = "vorticity.key.index.v1";

    /// <summary>
    /// Where the chunks of every top-level column lie and the array encodings their trees name, on
    /// the root, the encodings in its options and a payload region per column: what reads a span of
    /// rows without the footer (<see cref="Indexes.SliceChunks"/>).
    /// </summary>
    public const string SliceChunks = "vorticity.slices.chunks.v1";

    /// <summary>
    /// The geometry of every top-level column's chunks, on the root, a payload region per column: each chunk's encoding
    /// tree and where each granule starts in its buffers of variable length, which a read of a few granules needs before it
    /// reads their bytes alone (<see cref="Indexes.ColumnMarks"/>).
    /// </summary>
    public const string Marks = "vorticity.marks.v1";

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
    /// The block statistics of a variant column's shredded keys, on that column: per key and class, for
    /// each block that holds a value of it, their count and bounds, the keys and classes in its options
    /// and a payload region each (<see cref="VariantBlockStats"/>).
    /// </summary>
    public const string VariantBlocks = "vorticity.variant.blocks.v1";

    /// <summary>
    /// What a column holds, on that column: its rows and nulls, its distinct values and a sketch of them, its most
    /// frequent values, and a filter of its values when they repeat, in one payload region that a table of contents
    /// names, so that a question on a value an object lacks passes the object unopened (<see cref="ColumnSheet"/>).
    /// </summary>
    public const string ColumnSheet = "vorticity.column.sheet.v1";

    /// <summary>
    /// A column's counts over the object's rows, on that column: for each of its values, its nulls and the values its budget
    /// left out, an entry with its rows and the states of every measure column over them, in one payload region that a table of
    /// contents names, so that a question on that column alone is answered with no row read (<see cref="ColumnCounts"/>).
    /// </summary>
    public const string ColumnCounts = "vorticity.column.counts.v1";

    /// <summary>
    /// The sketches of a column's counts, on that column: for each entry of its counts, a quantile sketch of each
    /// measure and the registers of the distinct values of each wide column, in one payload region right after the
    /// counts, which a table of contents names, so that a question of counts alone reads none of them
    /// (<see cref="ColumnSketches"/>).
    /// </summary>
    public const string ColumnSketches = "vorticity.column.sketches.v1";

    // vorticity.block.table.v1 held each block's rows and its first and last keys, written and never read: retired, its
    // name never reused.

    // vorticity.block.stats.v1 held a column's statistics over runs of the key's prefix of 4,096 to 65,536 rows, and
    // vorticity.column.where.v1 the ranges of those runs each value lived in: retired, their names never reused.

    /// <summary>
    /// One column's statistics over the granules of a file whose rows follow a key: each granule's rows, nulls, values and
    /// their rows, and a measure's count, exact sum and extremes, with the values' dictionary, in one payload region that a
    /// table of contents names (<see cref="GranuleStats"/>).
    /// </summary>
    public const string GranuleStats = "vorticity.granule.stats.v1";

    /// <summary>
    /// A measure's histograms over the granules of a file whose rows follow a key: each granule's values in a log-linear
    /// sketch, apart from its statistics so that a sum never reads them, in one payload region that a table of contents names
    /// (<see cref="Indexes.GranuleHistograms"/>).
    /// </summary>
    public const string GranuleHistograms = "vorticity.granule.histograms.v1";

    /// <summary>
    /// Where each value of a column lies, granule by granule: its values by their bytes, each with the granules it lies in,
    /// or none where it lies everywhere, in one payload region that a table of contents names (<see cref="Indexes.ColumnLocate"/>).
    /// </summary>
    public const string ColumnLocate = "vorticity.locate.v1";

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
                or PostingsNgram3 or PostingsHashed or KeyIndex or SliceChunks or Marks or VariantPaths or VariantDirectory or VariantBlocks
                or ColumnSheet or ColumnCounts or ColumnSketches or GranuleStats or GranuleHistograms or ColumnLocate => true,
            _ => false,
        };

    /// <summary>
    /// Whether the kind carries payload segments. <see cref="DictProbe"/> does not, its whole content
    /// being the statement that the column's chunks are dictionary-encoded, and neither need
    /// <see cref="KeyIndex"/>, whose table keeps an object's one page.
    /// </summary>
    /// <param name="kind">The name.</param>
    /// <returns>Whether a run of this kind must carry at least one payload segment.</returns>
    public static bool HasPayload(string kind) =>
        kind switch
        {
            DictProbe or KeyIndex => false,
            _ => true,
        };

    /// <summary>
    /// Whether the kind locates the blocks that hold a column's values, which a lookup of rows by a value reads with no open
    /// of the file (<c>KeyIndexPruner</c>): sorted runs, postings of blocks, hashed postings, postings of trigrams, and the
    /// dictionary probe where none of them answers.
    /// </summary>
    /// <param name="kind">The name.</param>
    /// <returns>Whether a lookup reads it.</returns>
    internal static bool Locates(string kind) => kind is SortedRuns or PostingsBlocks or PostingsHashed or PostingsNgram3 or DictProbe;

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
            IndexPolicyKind.HashedPostings => PostingsHashed,
            _ => null,
        };
}
