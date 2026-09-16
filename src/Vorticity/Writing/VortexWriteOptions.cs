// Write-time policy.
//
// Two switches. COMPRESSION is the one a caller most plausibly wants off: it trades write CPU and a
// decode step on read for file size, and a caller producing a scratch file that will be read once
// may prefer neither. It is ON by default because that is the useful answer for a format whose
// point is compression.
//
// The TARGET EDITION is the one that decides who can read the result, and it is not cosmetic: an
// edition is a frozen set of component ids, so naming one is the only way to say "any Vortex from
// version N onward can read this".
using System;
using Vorticity.Editions;
using Vorticity.Indexes;

namespace Vorticity.Writing;

/// <summary>How much the writer does beyond the data (docs/11-write-strategy.md §7.1).</summary>
public enum WriteProfile
{
    /// <summary>Everything the options ask for.</summary>
    Default = 0,

    /// <summary>
    /// No index, whatever <see cref="VortexWriteOptions.Indexes"/> says: the file is the data, its
    /// zone map and its statistics, byte for byte what this writer produced before indexes existed
    /// (docs/10-indexes.md §7.3).
    /// </summary>
    Fastest = 1,
}

/// <summary>Policy for one written file.</summary>
public sealed class VortexWriteOptions
{
    /// <summary>The defaults: compression on.</summary>
    public static VortexWriteOptions Default { get; } = new VortexWriteOptions();

    /// <summary>
    /// The index policy: per column path an <see cref="IndexPolicy"/>. Default
    /// <see cref="WritePolicy.None"/>, for now.
    /// </summary>
    /// <remarks>
    /// THE TARGET DEFAULT IS <see cref="WritePolicy.Auto"/> (docs/10-indexes.md §5.5, docs/11 §7.1),
    /// and it is not the default YET because the spec itself says its write cost is measured before
    /// it becomes one. Until that measurement lands, a file carries an index only when the caller
    /// asks, and every file written with the defaults is byte for byte what it was.
    /// <para>
    /// The policy is serialized into the index directory, so an append reuses it without being
    /// told.
    /// </para>
    /// </remarks>
    public WritePolicy Indexes { get; init; } = WritePolicy.None;

    /// <summary>How much the writer does beyond the data. Default <see cref="WriteProfile.Default"/>.</summary>
    public WriteProfile Profile { get; init; } = WriteProfile.Default;

    /// <summary>
    /// The bytes the file's indexes may occupy together, as a share of the data bytes, in parts per
    /// thousand. Default 100 (10 %).
    /// </summary>
    /// <remarks>
    /// A builder that would take the file's indexes past this is abandoned whole, with the reason
    /// in the <see cref="WriteReport"/> (docs/10-indexes.md §7.2). A share rather than a byte count,
    /// because the right ceiling for a 10 MiB file and for a 10 GiB one is not the same number.
    /// </remarks>
    public int IndexBudgetPerMille { get; init; } = 100;

    /// <summary>
    /// Whether the writer may pick an encoding per column chunk. Default <see langword="true"/>.
    /// </summary>
    /// <remarks>
    /// Turning it off writes every column canonically, which is what the round-trip and
    /// cross-check suites use to separate "the writer produced the wrong bytes" from "the
    /// compressor chose badly".
    /// </remarks>
    public bool Compress { get; init; } = true;

    /// <summary>
    /// Whether the file carries a statistics segment: per top-level field its exact
    /// <c>min</c> / <c>max</c>, <c>null_count</c>, and <c>is_sorted</c> /
    /// <c>is_strict_sorted</c> when the pass tracked the column's order. Default on.
    /// </summary>
    /// <remarks>
    /// What <c>VortexFile.MayMatch</c> answers from without a scan, what <c>MinAsync</c> and
    /// <c>MaxAsync</c> answer from without a read, and what a key cursor's <c>SortedColumn</c>
    /// source exists on (docs/12-index-reads.md §3). The reference writer computes the first
    /// three by default and never the two order flags -- its file-level aggregation drops them
    /// (vortex-layout-0.86.1 layouts/file_stats.rs) -- so a file written by this writer is the
    /// one kind that says whether a column is sorted. A few dozen bytes per field.
    /// </remarks>
    public bool FileStatistics { get; init; } = true;

    /// <summary>
    /// The edition every component in the file must belong to. Default
    /// <see cref="EditionRegistry.Newest"/>, which is what the reference writer defaults to.
    /// </summary>
    /// <remarks>
    /// THE DEFAULT IS THE NEWEST FROZEN EDITION, not the read-forever floor, and the difference
    /// matters because the floor is not a target this writer can meet for every schema. Two of its
    /// own outputs say so: `vortex.zoned` and all six zone-map aggregates first appear in
    /// `core2026.08.0`, and `vortex.uuid` in `core2026.08.3` (spec/editions). A default of
    /// `core2025.05.0` would have been a claim the files themselves contradict, and one that turns
    /// a uuid column into a write failure for nobody's benefit.
    ///
    /// LOWER TARGETS ARE HONOURED RATHER THAN APPROXIMATED. Below `core2026.08.0` the zone map is
    /// omitted, because pruning is an optimization and dropping it costs correctness nothing; the
    /// scheme candidates are derived from the target before anything is measured, so the
    /// compressor never elects an encoding it cannot serialize. What the writer genuinely cannot
    /// express within the target - a List column at `core2025.05.0`, whose canonical form here is
    /// `vortex.listview`, which that edition does not carry - FAILS THE WRITE, naming the id and
    /// the edition that introduced it. Producing a file the target's readers cannot open would be
    /// the one unacceptable answer.
    /// </remarks>
    public VortexEdition TargetEdition { get; init; } = EditionRegistry.Newest;

    /// <summary>
    /// Rows per written chunk, as a multiple. Default 8192; <c>null</c> writes one chunk per
    /// <c>WriteAsync</c> call.
    /// </summary>
    /// <remarks>
    /// <para>
    /// THE FILE'S SHAPE USED TO BE THE CALLER'S BATCHING, and that is not a policy, it is the
    /// absence of one. The same rows handed over in batches of 1024 instead of 8192 produced a file
    /// <b>2.57x larger</b>, because every chunk carries its own array blob, its own segment entry
    /// and its own zone-map row -- about 26 kB of fixed cost that a caller with a small batch size
    /// paid over and over for nothing.
    /// </para>
    /// <para>
    /// Upstream's own name for this is <c>row_block_size</c> and its default is the same 8192
    /// (`vortex-file-0.86.1/src/strategy.rs`). Every emitted chunk but the last is a multiple of
    /// it.
    /// </para>
    /// </remarks>
    public int? RowBlockSize { get; init; } = 8192;

    /// <summary>
    /// Uncompressed bytes to accumulate before a chunk is emitted. Default 1 MiB; <c>null</c>
    /// disables byte-size coalescing and leaves the row granularity to
    /// <see cref="RowBlockSize"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The row count alone is the wrong unit for a chunk, in both directions: 8192 rows of a
    /// boolean column is a kilobyte, and 8192 rows of a wide struct is megabytes. The reference
    /// buffers until a block is <b>both</b> at least this many bytes and at least
    /// <see cref="RowBlockSize"/> rows, then emits whole multiples of the row block; the remainder
    /// goes out at close. One megabyte is upstream's default and its comment gives the reason: it
    /// is the size at which a single object-store request is efficient without losing read
    /// concurrency (Durner et al., VLDB Vol 16, Iss 11).
    /// </para>
    /// <para>
    /// MEASURED IN CANONICAL BYTES, before compression, because that is the only size available
    /// when the decision is made -- and it is what the reference measures too (`nbytes()` of the
    /// buffered arrays).
    /// </para>
    /// <para>
    /// ONE DIFFERENCE FROM THE REFERENCE, and it is a consequence of the layout rather than a
    /// choice: upstream repartitions PER COLUMN, so two columns of one file may have different
    /// chunk boundaries. A `vortex.zoned` layout here has one zone per chunk across every column,
    /// so the boundaries are shared and the accumulation is measured over the whole batch. The
    /// format permits both; the files differ in chunking, not in content.
    /// </para>
    /// </remarks>
    public long? DataBlockTargetBytes { get; init; } = 1L << 20;

    /// <summary>
    /// WRITE ATOMICITY, which <see cref="RowBlockSize"/> changes and which the contract has to say
    /// out loud: a batch handed to <c>WriteAsync</c> is no longer guaranteed to have reached the
    /// sink when the call returns.
    /// </summary>
    /// <remarks>
    /// Before repartitioning, one <c>WriteAsync</c> was one chunk and its segments were with the
    /// sink before the returned task completed. With accumulation, rows are held in the writer
    /// until a block fills or <c>CompleteAsync</c> runs. Nothing about durability changes -- the
    /// sink was never flushed per batch either -- but a caller that was reading the sink's position
    /// to infer progress will see it move in blocks. Setting <see cref="RowBlockSize"/> to
    /// <c>null</c> restores one chunk per call exactly.
    /// </remarks>
    internal const string AtomicityNote =
        "WriteAsync buffers rows until a block fills; CompleteAsync flushes the remainder.";
}
