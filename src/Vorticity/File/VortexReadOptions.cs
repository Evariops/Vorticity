// Read-time policy. Carried on the open file and copied into every ScanContext, so it is immutable
// and shared: docs/09-contracts.md §1 allows concurrent scans on one open file.
using System;
using System.Collections.Generic;

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
    /// Index fragments built for this file (docs/13-dataset.md §6.4), each one whole container: the
    /// runs and the directory an indexer wrote for a block range of the file, bound to it by its
    /// identity. Empty by default.
    /// </summary>
    /// <remarks>
    /// They are ADDED to the file's own index directory, entry by entry — they replace the sidecar
    /// of docs/10-indexes.md §8, which step 42d retired. An entry the file already has, of the same
    /// kind, column, block length and options,
    /// stays the file's; the same entry across fragments joins its runs when their blocks are
    /// disjoint; an entry of other options is another entry. A fragment that is not this file's, or
    /// an entry that overlaps blocks another fragment covers, is left out with the reason in
    /// <see cref="VortexFile.IndexFragmentRefusals"/>, and never fails the open: an index is a hint
    /// (docs/10-indexes.md §6.6). A fragment is decoded against its own encoding table, never the
    /// file's footer, so the bytes are the same wherever they are stored. Binding reads no byte of
    /// the file: the length and the identity come from the tail the open read, and a file written
    /// without an identity is bound by its store token -- its length and modification time, taken
    /// when it is opened from a path -- which is a heuristic.
    /// </remarks>
    public IReadOnlyList<ReadOnlyMemory<byte>> IndexFragments { get; init; } = [];

    /// <summary>
    /// THE INTERNAL SWITCH OF PERF-AUDIT-v2.md Z1b. Default <see langword="true"/> since
    /// 2026-09-18.
    /// </summary>
    /// <remarks>
    /// <para>
    /// With this on, `ConstantCanonicalizer` emits <see cref="Arrays.CanonicalKind.Constant"/> --
    /// the element and a length -- instead of tiling the element over every row, for a primitive, a
    /// string or a blob. What it buys, measured by `bench/ab.sh` against the commit before it and
    /// by `--throughput` against Vortex Rust:
    /// </para>
    /// <para>
    /// <b>1M `variant` scan 605 us -> 104</b> (ratio against Rust <b>6,51 -> 0,96</b>), <b>its write
    /// 3,48 -> 0,39</b>, <b>1M `constant` scan 127 us -> 60</b> (0,96 -> 0,42) and <b>its write 0,34
    /// -> 0,072</b>. The `variant` axis was the furthest behind in the repository and is now ahead:
    /// a constant variant is two constant BINARY columns, which the tiling form turned into 2 x 1M
    /// views and 32 MB of buffer for two values that never change. No other axis of the four
    /// benchmarks moved outside its ceiling.
    /// </para>
    /// <para>
    /// IT IS A PER-SCAN OPTION AND NOT A STATIC FLAG, and it stays one now that it is on: a mutable
    /// global would poison every test running beside the one that flips it, and
    /// `ConstantFormTests` reads both forms of the same file to assert they agree.
    /// </para>
    /// <para>
    /// WHERE THE FORM ENDS. Two boundaries, and only two:
    /// <see cref="Arrays.CanonicalNode.Values"/> and the `RequireMaterialized` sibling behind
    /// <see cref="Arrays.CanonicalNode.Views"/>. Both promise a CONTIGUOUS array, which one element
    /// and a count cannot be, so both expand into a twin memoized on the record. Everything that
    /// never asks keeps the form -- and the filter does better than keep it: `ComparisonKernels`
    /// answers a whole constant column from ONE comparison, and `Extremes` from none at all.
    /// A decoder that wants a side table as a span uses
    /// <see cref="Arrays.Decoders.Canonical.CanonicalSupport.ExpandIfConstant"/>: this form exists
    /// to spare a CONSUMER a million copies of one value, never to spare a decoder a table it has
    /// to walk.
    /// </para>
    /// <para>
    /// WHAT THE SWITCH COST, stated because the ratchets recorded it: <b>+112 bytes</b> on
    /// `PathAllocationTests`'s "open, first batch" -- one extra record, once per open, on the one
    /// axis that pays for the form and collects none of it. The same file's full scan went DOWN
    /// 304 B. The argument is written where the ceiling is.
    /// </para>
    /// <para>
    /// AND WHAT IT BROKE ON THE WAY, because both were latent and neither was about constants:
    /// `CanonicalArena.Commit` let a record copied from another arena keep a `Materialized` index
    /// that meant nothing here, and `BlockStatsPass`'s Constant arm never touched the step
    /// bookkeeping -- so `SequencePlan` was told its walk had been done and wrote
    /// `vortex.sequence(base, 0)` over rows that climb. The corpus cross-check against Vortex Rust
    /// is what found the second one; `ConstantFormTests.EveryCorpusFileReadsTheSameValuesEitherWay`
    /// is the test that was missing for the first.
    /// </para>
    /// </remarks>
    internal bool ConstantForm { get; init; } = true;
}
