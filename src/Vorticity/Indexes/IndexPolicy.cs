// What a caller asks for, per column, from docs/10-indexes.md §5.5 and §7.1.
//
// THE DECISION IS WHEN TO ABANDON, NEVER WHEN TO START. `Auto` starts every cheap builder at block
// 0 and drops the ones the statistics disqualify or the budget refuses, which is what lets block 0
// be indexed like every other block -- a policy that decided at block 0 would have to decide on
// nothing. Everything abandoned is named in the `WriteReport` with its reason, so a caller who
// expected an index learns why there is none instead of inferring it from a slow scan.
//
// A POLICY IS ALSO WIRE FORMAT: it is serialized into the directory so that an append reuses it
// without being told (§4.1 `bytes policy`, §7.1 `Append(path)`). That is why the options are plain
// integers with fixed meanings and not a lambda.
using System;
using System.Collections.Generic;
using System.Globalization;

namespace Vorticity.Indexes;

/// <summary>Which index a column gets.</summary>
public enum IndexPolicyKind
{
    /// <summary>Nothing beyond the zone map.</summary>
    None = 0,

    /// <summary>Every cheap builder starts; the statistics and the budget decide what survives.</summary>
    Auto = 1,

    /// <summary>A split-block Bloom filter (docs/10-indexes.md §5.1).</summary>
    Bloom = 2,

    /// <summary>A trigram Bloom for <c>LIKE</c> (§5.2).</summary>
    NgramBloom = 3,

    /// <summary>Value to blocks (§6.1).</summary>
    Postings = 4,

    /// <summary>Value to rows, exact (§6.2).</summary>
    SortedRuns = 5,

    /// <summary>Trigram to blocks (§6.4).</summary>
    NgramPostings = 6,
}

/// <summary>Which hash a Bloom filter uses.</summary>
public enum BloomHash
{
    /// <summary>
    /// XxHash3-64, the default and what <c>vortex.bloom_filter.sbbf</c> uses at 0.86.1, so our
    /// filter is bit-identical to the reference's for as long as upstream keeps its layout.
    /// </summary>
    XxHash3 = 0,

    /// <summary>
    /// xxHash64, which makes the filter bit-identical to a Parquet SBBF. A variant for the rare
    /// caller that exchanges filters with Parquet tooling, never the default.
    /// </summary>
    XxHash64 = 1,
}

/// <summary>The index policy of one column.</summary>
public readonly struct IndexPolicy : IEquatable<IndexPolicy>
{
    /// <summary>1 %, as parts per million: the default false-positive rate of a Bloom filter.</summary>
    public const int DefaultFalsePositivePpm = 10_000;

    /// <summary>4 096 blocks of 256 bits = 128 KiB, the default ceiling on one filter.</summary>
    public const int DefaultMaxBlocks = 4_096;

    /// <summary>
    /// Below eight distinct values a block gets no filter: the zone map or the dictionary already
    /// answers equality there.
    /// </summary>
    public const int DefaultMinDistinct = 8;

    /// <summary>One filter per block, and one per generation of sixteen.</summary>
    public const int DefaultResolutions = 2;

    // ZERO MEANS "THE DEFAULT" in every field, so that `default(IndexPolicy)` is a usable `None`
    // with every option at its documented value. `MinDistinct` is the one option whose zero is also
    // a legitimate request -- a filter on every block -- so it is stored shifted by one.
    private readonly int _fppPpm;
    private readonly int _resolutions;
    private readonly int _maxBlocks;
    private readonly int _minDistinctPlusOne;
    private readonly int _segmentEntries;

    /// <summary>
    /// 10 §4.2's `payload_block_rows`: the most entries one segment of a locating run holds.
    /// </summary>
    public const int DefaultSegmentEntries = 65_536;

    private IndexPolicy(
        IndexPolicyKind kind, int fppPpm, int resolutions, int maxBlocks, int minDistinct,
        BloomHash hash, bool caseInsensitive, int segmentEntries = 0)
    {
        Kind = kind;
        _fppPpm = fppPpm;
        _resolutions = resolutions;
        _maxBlocks = maxBlocks;
        _minDistinctPlusOne = minDistinct < 0 ? 0 : minDistinct + 1;
        Hash = hash;
        CaseInsensitive = caseInsensitive;
        _segmentEntries = segmentEntries;
    }

    /// <summary>The most entries one segment of a locating run holds.</summary>
    public int SegmentEntries => _segmentEntries == 0 ? DefaultSegmentEntries : _segmentEntries;

    /// <summary>The same policy with another segment size for its locating runs.</summary>
    /// <param name="entries">The most entries a segment holds; at least 1.</param>
    /// <returns>A new policy.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="entries"/> is not positive.</exception>
    public IndexPolicy WithSegmentEntries(int entries)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(entries, 1);
        return new IndexPolicy(
            Kind, _fppPpm, _resolutions, _maxBlocks, _minDistinctPlusOne - 1, Hash, CaseInsensitive, entries);
    }

    /// <summary>What the column gets.</summary>
    public IndexPolicyKind Kind { get; }

    /// <summary>The Bloom false-positive rate, as parts per million.</summary>
    public int FalsePositivePpm => _fppPpm == 0 ? DefaultFalsePositivePpm : _fppPpm;

    /// <summary>
    /// How much of a Bloom filter's tree carries filters (docs/13-dataset.md §6.2): 1 = the blocks
    /// alone; 2 = the blocks and every node above them, generations and root included, each while it
    /// fits <see cref="MaxBlocks"/>; 3 = the same, with the root under the file-level ceiling
    /// (docs/10-indexes.md §4.3, §5.4).
    /// </summary>
    public int Resolutions => _resolutions == 0 ? DefaultResolutions : _resolutions;

    /// <summary>The ceiling on one filter's 256-bit blocks.</summary>
    public int MaxBlocks => _maxBlocks == 0 ? DefaultMaxBlocks : _maxBlocks;

    /// <summary>Below this many distinct values in a block, no filter is built for it.</summary>
    public int MinDistinct => _minDistinctPlusOne == 0 ? DefaultMinDistinct : _minDistinctPlusOne - 1;

    /// <summary>The hash a Bloom filter uses.</summary>
    public BloomHash Hash { get; }

    /// <summary>Whether a trigram Bloom lower-cases its trigrams.</summary>
    public bool CaseInsensitive { get; }

    /// <summary>Nothing beyond the zone map.</summary>
    public static IndexPolicy None => new IndexPolicy(
        IndexPolicyKind.None, 0, 0, 0, -1, BloomHash.XxHash3, false);

    /// <summary>Start everything cheap; abandon what the statistics or the budget refuse.</summary>
    public static IndexPolicy Auto => new IndexPolicy(
        IndexPolicyKind.Auto, 0, 0, 0, -1, BloomHash.XxHash3, false);

    /// <summary>Value to blocks (docs/10-indexes.md §6.1).</summary>
    public static IndexPolicy Postings => new IndexPolicy(
        IndexPolicyKind.Postings, 0, 0, 0, -1, BloomHash.XxHash3, false);

    /// <summary>Value to rows, exact (§6.2).</summary>
    public static IndexPolicy SortedRuns => new IndexPolicy(
        IndexPolicyKind.SortedRuns, 0, 0, 0, -1, BloomHash.XxHash3, false);

    /// <summary>Trigram to blocks, for <c>LIKE</c> and <c>CONTAINS</c> (§6.4).</summary>
    /// <param name="caseInsensitive">Whether trigrams are ASCII-lower-cased on both sides.</param>
    /// <returns>The policy.</returns>
    public static IndexPolicy NgramPostings(bool caseInsensitive = false) => new IndexPolicy(
        IndexPolicyKind.NgramPostings, 0, 0, 0, -1, BloomHash.XxHash3, caseInsensitive);

    /// <summary>A split-block Bloom filter (§5.1).</summary>
    /// <param name="falsePositivePpm">
    /// The target false-positive rate as parts per million, in <c>[1, 500 000]</c>. Default 1 %.
    /// </param>
    /// <param name="resolutions">1, 2 or 3; see <see cref="Resolutions"/>.</param>
    /// <param name="maxBlocks">The ceiling on one filter's 256-bit blocks, a positive number.</param>
    /// <param name="minDistinct">Below this many distinct values a block gets no filter.</param>
    /// <param name="hash">The hash; <see cref="BloomHash.XxHash64"/> gives a Parquet-compatible filter.</param>
    /// <returns>The policy.</returns>
    /// <exception cref="ArgumentOutOfRangeException">An option is outside its range.</exception>
    public static IndexPolicy Bloom(
        int falsePositivePpm = DefaultFalsePositivePpm,
        int resolutions = DefaultResolutions,
        int maxBlocks = DefaultMaxBlocks,
        int minDistinct = DefaultMinDistinct,
        BloomHash hash = BloomHash.XxHash3)
    {
        CheckBloom(falsePositivePpm, resolutions, maxBlocks, minDistinct);
        return new IndexPolicy(
            IndexPolicyKind.Bloom, falsePositivePpm, resolutions, maxBlocks, minDistinct, hash,
            false);
    }

    /// <summary>A trigram Bloom for <c>LIKE</c> and <c>CONTAINS</c> (§5.2).</summary>
    /// <param name="falsePositivePpm">As <see cref="Bloom"/>.</param>
    /// <param name="resolutions">As <see cref="Bloom"/>.</param>
    /// <param name="maxBlocks">As <see cref="Bloom"/>.</param>
    /// <param name="caseInsensitive">Whether trigrams are lower-cased on both sides.</param>
    /// <returns>The policy.</returns>
    /// <exception cref="ArgumentOutOfRangeException">An option is outside its range.</exception>
    public static IndexPolicy NgramBloom(
        int falsePositivePpm = DefaultFalsePositivePpm,
        int resolutions = DefaultResolutions,
        int maxBlocks = DefaultMaxBlocks,
        bool caseInsensitive = false)
    {
        CheckBloom(falsePositivePpm, resolutions, maxBlocks, DefaultMinDistinct);
        return new IndexPolicy(
            IndexPolicyKind.NgramBloom, falsePositivePpm, resolutions, maxBlocks,
            DefaultMinDistinct, BloomHash.XxHash3, caseInsensitive);
    }

    /// <summary>
    /// Rebuilds a policy from the integers a directory stores, clamping rather than throwing: these
    /// bytes come from a file and a bad option must cost the entry, never the open (§4.1).
    /// </summary>
    /// <param name="kind">The policy kind; an unknown value becomes <see cref="None"/>.</param>
    /// <param name="fppPpm">The false-positive rate in ppm, clamped into range.</param>
    /// <param name="resolutions">The resolution count, clamped into <c>[1, 3]</c>.</param>
    /// <param name="maxBlocks">The block ceiling, clamped positive.</param>
    /// <param name="minDistinct">The distinct floor; a negative value, which a file cannot hold, means the default.</param>
    /// <param name="hash">The hash; an unknown value becomes <see cref="BloomHash.XxHash3"/>.</param>
    /// <param name="caseInsensitive">Whether trigrams are lower-cased.</param>
    /// <returns>A policy inside every range.</returns>
    /// <param name="segmentEntries">The locating segment size; 0 for the default.</param>
    internal static IndexPolicy FromStored(
        int kind, int fppPpm, int resolutions, int maxBlocks, int minDistinct, int hash,
        bool caseInsensitive, int segmentEntries = 0)
    {
        IndexPolicyKind policy = kind is >= (int)IndexPolicyKind.None and <= (int)IndexPolicyKind.NgramPostings
            ? (IndexPolicyKind)kind
            : IndexPolicyKind.None;
        return new IndexPolicy(
            policy,
            Math.Clamp(fppPpm == 0 ? DefaultFalsePositivePpm : fppPpm, 1, 500_000),
            Math.Clamp(resolutions == 0 ? DefaultResolutions : resolutions, 1, 3),
            Math.Max(maxBlocks == 0 ? DefaultMaxBlocks : maxBlocks, 1),
            minDistinct,
            hash == (int)BloomHash.XxHash64 ? BloomHash.XxHash64 : BloomHash.XxHash3,
            caseInsensitive,
            Math.Max(segmentEntries, 0));
    }

    private static void CheckBloom(int falsePositivePpm, int resolutions, int maxBlocks, int minDistinct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(falsePositivePpm, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(falsePositivePpm, 500_000);
        ArgumentOutOfRangeException.ThrowIfLessThan(resolutions, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(resolutions, 3);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBlocks, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(minDistinct);
    }

    /// <inheritdoc/>
    public bool Equals(IndexPolicy other) =>
        Kind == other.Kind
        && FalsePositivePpm == other.FalsePositivePpm
        && Resolutions == other.Resolutions
        && MaxBlocks == other.MaxBlocks
        && MinDistinct == other.MinDistinct
        && Hash == other.Hash
        && CaseInsensitive == other.CaseInsensitive
        && SegmentEntries == other.SegmentEntries;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is IndexPolicy other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() =>
        HashCode.Combine(
            Kind, FalsePositivePpm, Resolutions, MaxBlocks, MinDistinct, Hash, CaseInsensitive, SegmentEntries);

    /// <summary>Whether two policies ask for the same thing.</summary>
    /// <param name="left">One policy.</param>
    /// <param name="right">The other.</param>
    /// <returns>Whether they are equal.</returns>
    public static bool operator ==(IndexPolicy left, IndexPolicy right) => left.Equals(right);

    /// <summary>Whether two policies differ.</summary>
    /// <param name="left">One policy.</param>
    /// <param name="right">The other.</param>
    /// <returns>Whether they differ.</returns>
    public static bool operator !=(IndexPolicy left, IndexPolicy right) => !left.Equals(right);

    /// <inheritdoc/>
    public override string ToString() =>
        Kind switch
        {
            IndexPolicyKind.Bloom or IndexPolicyKind.NgramBloom => string.Create(
                CultureInfo.InvariantCulture,
                $"{Kind}(fpp={FalsePositivePpm}ppm, resolutions={Resolutions}, hash={Hash})"),
            _ => Kind.ToString(),
        };
}

/// <summary>The index policy of a whole file: a default, and overrides by column path.</summary>
/// <remarks>
/// Paths are the scan's own: <c>"a"</c> for a top-level field, <c>"a.b"</c> for a nested one.
/// A path naming no column is not an error here -- a policy is written before the schema is walked
/// and survives into the directory -- it simply never matches, and the report says the column was
/// never seen.
/// </remarks>
public sealed class WritePolicy
{
    private readonly Dictionary<string, IndexPolicy> _columns;
    private readonly List<CompositeKeyPolicy> _keys;

    private WritePolicy(IndexPolicy fallback, Dictionary<string, IndexPolicy> columns, List<CompositeKeyPolicy>? keys = null)
    {
        Default = fallback;
        _columns = columns;
        _keys = keys ?? [];
    }

    /// <summary>The composite keys, in the order they were added (docs/10-indexes.md §6.5).</summary>
    public IReadOnlyList<CompositeKeyPolicy> Keys => _keys;

    /// <summary>
    /// The same policy with a locating index over the tuple of <paramref name="columnPaths"/>,
    /// keyed by its row encoding (docs/10-indexes.md §6.5, docs/12-index-reads.md §4.6).
    /// </summary>
    /// <param name="columnPaths">Two or more columns, in key order, <c>.</c>-separated for a nested field.</param>
    /// <param name="policy"><see cref="IndexPolicy.SortedRuns"/>, with its options.</param>
    /// <returns>A new policy.</returns>
    /// <remarks>
    /// The writer needs an encoder for it, <c>VortexWriteOptions.KeyEncoder</c>, which the
    /// <c>Vorticity.RowEncoding</c> package provides; without one the index is abandoned and the
    /// report says so. A row whose tuple holds a null is not an entry.
    /// </remarks>
    /// <exception cref="ArgumentException">Fewer than two paths, an empty one, or a policy that is not sorted runs.</exception>
    public WritePolicy ForKey(IReadOnlyList<string> columnPaths, IndexPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(columnPaths);
        if (columnPaths.Count < 2)
        {
            throw new ArgumentException("A composite key has at least two columns; use For for one.", nameof(columnPaths));
        }

        foreach (string path in columnPaths)
        {
            ArgumentException.ThrowIfNullOrEmpty(path, nameof(columnPaths));
        }

        if (policy.Kind != IndexPolicyKind.SortedRuns)
        {
            throw new ArgumentException(
                "A composite key is a sorted-runs index (docs/10-indexes.md §6.5).", nameof(policy));
        }

        List<CompositeKeyPolicy> keys = [.. _keys, new CompositeKeyPolicy([.. columnPaths], policy)];
        return new WritePolicy(Default, new Dictionary<string, IndexPolicy>(_columns, StringComparer.Ordinal), keys);
    }

    /// <summary>Every column indexed by <see cref="IndexPolicy.Auto"/>.</summary>
    public static WritePolicy Auto { get; } =
        new WritePolicy(IndexPolicy.Auto, new Dictionary<string, IndexPolicy>(StringComparer.Ordinal));

    /// <summary>No index anywhere.</summary>
    public static WritePolicy None { get; } =
        new WritePolicy(IndexPolicy.None, new Dictionary<string, IndexPolicy>(StringComparer.Ordinal));

    /// <summary>What a column with no override gets.</summary>
    public IndexPolicy Default { get; }

    /// <summary>The overrides, by column path.</summary>
    public IReadOnlyDictionary<string, IndexPolicy> Columns => _columns;

    /// <summary>The same policy with a different fallback.</summary>
    /// <param name="fallback">What a column with no override gets.</param>
    /// <returns>A new policy.</returns>
    public WritePolicy WithDefault(IndexPolicy fallback) =>
        new WritePolicy(fallback, new Dictionary<string, IndexPolicy>(_columns, StringComparer.Ordinal), [.. _keys]);

    /// <summary>The same policy with one column overridden.</summary>
    /// <param name="columnPath">The column, as the scan spells it: <c>"a"</c> or <c>"a.b"</c>.</param>
    /// <param name="policy">What that column gets.</param>
    /// <returns>A new policy.</returns>
    /// <exception cref="ArgumentException">The path is empty.</exception>
    public WritePolicy For(string columnPath, IndexPolicy policy)
    {
        ArgumentException.ThrowIfNullOrEmpty(columnPath);
        Dictionary<string, IndexPolicy> columns =
            new Dictionary<string, IndexPolicy>(_columns, StringComparer.Ordinal)
            {
                [columnPath] = policy,
            };
        return new WritePolicy(Default, columns, [.. _keys]);
    }

    /// <summary>The policy that applies to one column.</summary>
    /// <param name="columnPath">The column path.</param>
    /// <returns>The override when there is one, otherwise <see cref="Default"/>.</returns>
    public IndexPolicy Of(string columnPath) =>
        columnPath is not null && _columns.TryGetValue(columnPath, out IndexPolicy policy)
            ? policy
            : Default;

    /// <summary>Rebuilds a policy read from a directory.</summary>
    /// <param name="fallback">The stored default.</param>
    /// <param name="columns">The stored overrides.</param>
    /// <param name="keys">The stored composite keys.</param>
    /// <returns>The policy.</returns>
    internal static WritePolicy FromStored(
        IndexPolicy fallback, Dictionary<string, IndexPolicy> columns, List<CompositeKeyPolicy>? keys = null) =>
        new WritePolicy(fallback, columns, keys);
}

/// <summary>A locating index over the tuple of several columns.</summary>
/// <param name="Paths">The columns, in key order.</param>
/// <param name="Policy">The index: sorted runs.</param>
public sealed record CompositeKeyPolicy(IReadOnlyList<string> Paths, IndexPolicy Policy);
