// Read-time policy. Carried on the open file and copied into every ScanContext, so it is immutable
// and shared: docs/09-contracts.md §1 allows concurrent scans on one open file.
using System;

namespace Vorticity.File;

/// <summary>Read-time policy, carried on the file and copied into every scan context.</summary>
public sealed class VortexReadOptions
{
    private readonly long _maxDecompressedSize = VortexLimits.DefaultMaxDecompressedSize;
    private readonly long _indexCacheBytes = DefaultIndexCacheBytes;

    /// <summary>The default of <see cref="IndexCacheBytes"/>: 64 MiB.</summary>
    public const long DefaultIndexCacheBytes = 64L << 20;

    /// <summary>The defaults: 256 MiB decompression ceiling, no statistics verification.</summary>
    public static VortexReadOptions Default { get; } = new VortexReadOptions();

    /// <summary>
    /// Ceiling on the bytes one decompression step may produce (docs/08-semantics.md §6). Defaults
    /// to <see cref="VortexLimits.DefaultMaxDecompressedSize"/>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not positive.</exception>
    public long MaxDecompressedSize
    {
        get => _maxDecompressedSize;
        init
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
            _maxDecompressedSize = value;
        }
    }

    /// <summary>
    /// The bytes of decoded index runs one open file keeps for its cursors, least recently used
    /// first out (docs/12-index-reads.md §9). Default <see cref="DefaultIndexCacheBytes"/>;
    /// <c>0</c> keeps nothing, and every seek then reads what it needs.
    /// </summary>
    /// <remarks>
    /// A cap in the sense of docs/08-semantics.md §6, and a guess until measured on real runs
    /// (docs/12 §13): a run's keys can be a chunk's rows, so an unbounded cache would be bounded by
    /// the file. A run larger than the whole cap is decoded, used and not kept.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative.</exception>
    public long IndexCacheBytes
    {
        get => _indexCacheBytes;
        init
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            _indexCacheBytes = value;
        }
    }

    /// <summary>
    /// Verify class II statistics — monotonic run ends, <c>is_sorted</c>, zone bounds — instead of
    /// trusting them (docs/08-semantics.md §5). O(n) at first decode when on. Default
    /// <see langword="false"/>.
    /// </summary>
    public bool VerifyStatistics { get; init; }

    /// <summary>
    /// Inspection mode: unknown components are preserved as inert nodes so a dump tool can list
    /// them (docs/03-architecture.md §5). It does <em>not</em> make lazy resolution happen — that
    /// is unconditional (docs/08-semantics.md §4). Default <see langword="false"/>.
    /// </summary>
    public bool AllowUnknownComponents { get; init; }

    /// <summary>
    /// A sidecar index file (<c>file.vortex.idx</c>, docs/10-indexes.md §8) to read when the file
    /// carries no index directory of its own; null by default, and nothing is looked for.
    /// </summary>
    /// <remarks>
    /// Opt-in, so that a file without a directory costs nothing for being probed. A sidecar whose
    /// recorded length or SHA-256 is not the file's is refused as stale, with the reason in
    /// <see cref="VortexFile.IndexDirectoryRefusal"/>; checking the hash reads the whole file once,
    /// on the first index read. <c>VortexFileIndexer.WriteSidecarAsync</c> writes one.
    /// </remarks>
    public string? IndexSidecarPath { get; init; }

    /// <summary>
    /// THE INTERNAL SWITCH OF PERF-AUDIT-v2.md Z1b. Default <see langword="false"/>.
    /// </summary>
    /// <remarks>
    /// With this on, `ConstantCanonicalizer` emits <see cref="Arrays.CanonicalKind.Constant"/> -- the
    /// element and a length -- instead of tiling the element over every row. Measured on the 1M
    /// `constant` file, a full scan goes from **201 us to 144**, a ratio of **0,716**: 28,4 % of that
    /// scan was tiling a value that never changes.
    /// <para>
    /// IT IS A PER-SCAN OPTION AND NOT A STATIC FLAG: the two forms coexist in ONE process until the
    /// refactor reaches its exit (§3.7 condition 5), and a mutable global would poison every test
    /// running beside the one that flips it.
    /// </para>
    /// <para>
    /// WHY IT IS STILL HERE, stated rather than left to be discovered: Z1b-c2c tried to take it out
    /// and **89 tests went red**. `VortexColumn.Resolve` and `CanonicalNode.Values` cover the typed
    /// primitive path, which is what Z1b-c2b2 measured; `AsExtension`, `AsFixedSizeList` and the
    /// layout split paths do not have their case yet. The remaining work is counted, not guessed --
    /// see Z1b-c2c2.
    /// </para>
    /// </remarks>
    internal bool ConstantForm { get; init; }
}
