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
using System.Collections.Generic;
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
    /// zone map, its statistics and its identity, byte for byte a write under
    /// <see cref="WritePolicy.None"/> with the same <see cref="VortexWriteOptions.Identity"/>
    /// (docs/10-indexes.md §7.3).
    /// </summary>
    Fastest = 1,
}

/// <summary>Policy for one written file.</summary>
public sealed class VortexWriteOptions
{
    /// <summary>The defaults: compression on.</summary>
    public VortexWriteOptions()
    {
    }

    /// <summary>
    /// A copy, which the <c>With…</c> methods override one option of.
    /// </summary>
    /// <param name="other">The options to copy.</param>
    /// <remarks>
    /// THE FIELD LIST LIVES HERE AND NOWHERE ELSE. These options are a class, like every other
    /// options type of this library, so a copy is a constructor rather than a record's `with` — and
    /// a copy per caller was three lists of seventeen fields to keep in step, which is three
    /// chances to drop one silently. An option added below is copied here, once.
    /// </remarks>
    private VortexWriteOptions(VortexWriteOptions other)
    {
        ArgumentNullException.ThrowIfNull(other);
        Indexes = other.Indexes;
        Profile = other.Profile;
        EncodingHints = other.EncodingHints;
        IndexBudgetPerMille = other.IndexBudgetPerMille;
        KeyEncoder = other.KeyEncoder;
        Identity = other.Identity;
        ScratchDirectory = other.ScratchDirectory;
        ScratchMemoryBytes = other.ScratchMemoryBytes;
        WideRowsAbove = other.WideRowsAbove;
        Fences = other.Fences;
        ElementStatistics = other.ElementStatistics;
        Compress = other.Compress;
        FileStatistics = other.FileStatistics;
        StringBoundBytes = other.StringBoundBytes;
        TargetEdition = other.TargetEdition;
        RowBlockSize = other.RowBlockSize;
        DataBlockTargetBytes = other.DataBlockTargetBytes;
    }

    /// <summary>The defaults: compression on.</summary>
    public static VortexWriteOptions Default { get; } = new VortexWriteOptions();

    /// <summary>
    /// The index policy: per column path an <see cref="IndexPolicy"/>. Default
    /// <see cref="WritePolicy.Auto"/>.
    /// </summary>
    /// <remarks>
    /// `Auto` (docs/10-indexes.md §5.5) records the dictionary probe of every dictionary-encoded
    /// column and keeps a Bloom filter where it pays -- under 2 % of the column -- giving it up
    /// otherwise before a byte of it is written, and before it has hashed more than a generation of
    /// a column it cannot serve. Measured at +0,6 % on the `table_mixed` write, inside the +10 %
    /// docs/11 §5.3 allows, and inside every ceiling of the write axis. <see cref="WritePolicy.None"/> or
    /// <see cref="WriteProfile.Fastest"/> write no index at all.
    /// <para>
    /// The policy is serialized into the index directory, so an append reuses it without being
    /// told.
    /// </para>
    /// </remarks>
    public WritePolicy Indexes { get; init; } = WritePolicy.Auto;

    /// <summary>How much the writer does beyond the data. Default <see cref="WriteProfile.Default"/>.</summary>
    public WriteProfile Profile { get; init; } = WriteProfile.Default;

    /// <summary>
    /// The scheme to write a column with, by column path, for callers who know
    /// (docs/11-write-strategy.md §7.1, §3.4.3).
    /// </summary>
    /// <remarks>
    /// PLAN MEMORY WITH THE TOLERANCE SET TO INFINITY, which is what §3.4.3 calls it: the hinted
    /// scheme is priced on every chunk's own statistics and written when it still applies, and
    /// nothing else is priced. A chunk the scheme cannot describe — a bit-packing on a chunk of
    /// nulls, a dictionary whose table gave up — is priced in full, and the next chunk is offered
    /// the hint again. Bounds, run counts and steps still come first: a progression is written as
    /// one whatever the hint says, because §3.4.1 answers it for nothing and no scheme beats it.
    /// <para>
    /// The paths are <see cref="WritePolicy"/>'s: a top-level column, or a <c>.</c>-separated path
    /// through structs. A path that names nothing in the schema throws at
    /// <see cref="VortexFileWriter.Create(ISegmentSink, Types.DType, VortexWriteOptions)"/>: an
    /// index is a hint whose absence costs nothing and is reported, while an encoding hint that
    /// silently did nothing would have no channel to say so.
    /// </para>
    /// </remarks>
    public IReadOnlyDictionary<string, VortexEncodingHint>? EncodingHints { get; init; }

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
    /// The encoder of the composite keys <see cref="WritePolicy.ForKey"/> asks for; null by default.
    /// </summary>
    /// <remarks>
    /// The core does not row-encode (docs/09-contracts.md §3): the <c>Vorticity.RowEncoding</c>
    /// package's <c>RowKeyEncoder</c> is the encoder, and a composite key asked for without one is
    /// abandoned, with that reason in the <see cref="WriteReport"/>. Its bytes are the index's keys,
    /// so a reader seeks with the same package's <c>RowEncoder.EncodeKey</c>.
    /// </remarks>
    public IKeyEncoder? KeyEncoder { get; init; }

    /// <summary>
    /// The identity the file's postscript carries, or null -- the default -- for a fresh random
    /// one.
    /// </summary>
    /// <remarks>
    /// Every postscript this writer writes carries sixteen bytes that name that version of the file
    /// (docs/13-dataset.md §7): a write, an append and a post-hoc indexing each mint their own, and
    /// <c>VortexFile.Identity</c> reads it back from the tail an open already reads. An index or a
    /// dataset bound to a file records it and refuses a file whose identity differs, without
    /// reading its data. Pinning it makes a write a pure function of its batches again, byte for
    /// byte, which is what a test comparing two writes needs; two different files must never share
    /// one.
    /// </remarks>
    public Guid? Identity { get; init; }

    /// <summary>These options with <see cref="Identity"/> pinned to <paramref name="identity"/>.</summary>
    /// <param name="identity">The sixteen bytes the postscript will carry.</param>
    /// <returns>A copy; these options are unchanged.</returns>
    /// <remarks>
    /// For a caller that mints the identity BEFORE the write and has to know it afterwards -- a
    /// dataset naming its data object (13 §7) is the case this exists for, since it records the
    /// identity in the leaf entry and cannot go back and read it out of the bytes it just streamed
    /// into a store. It is a copy method rather than a record's `with` because these options are a
    /// class, like every other options type of this library.
    /// </remarks>
    public VortexWriteOptions WithIdentity(Guid identity) =>
        new VortexWriteOptions(this) { Identity = identity };

    /// <summary>These options with a different index policy.</summary>
    /// <param name="indexes">The policy to write under.</param>
    /// <returns>A copy; these options are unchanged.</returns>
    /// <remarks>
    /// For a caller that must add an index to whatever the user asked for rather than replace it:
    /// a dataset with a declared clustering key writes every data object with the mandatory run on
    /// that key (docs/13-dataset.md §6.1), on top of the policy it was handed.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="indexes"/> is null.</exception>
    public VortexWriteOptions WithIndexes(WritePolicy indexes)
    {
        ArgumentNullException.ThrowIfNull(indexes);
        return new VortexWriteOptions(this) { Indexes = indexes };
    }

    /// <summary>These options with a composite-key encoder.</summary>
    /// <param name="keyEncoder">The encoder, from the <c>Vorticity.RowEncoding</c> package.</param>
    /// <returns>A copy; these options are unchanged.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="keyEncoder"/> is null.</exception>
    public VortexWriteOptions WithKeyEncoder(IKeyEncoder keyEncoder)
    {
        ArgumentNullException.ThrowIfNull(keyEncoder);
        return new VortexWriteOptions(this) { KeyEncoder = keyEncoder };
    }

    /// <summary>
    /// Where a locating index's chunk runs wait for their merge once they pass the memory budget,
    /// or null -- the default -- for the system's temporary directory.
    /// </summary>
    /// <remarks>
    /// A postings or sorted-runs index is built one run per chunk and written as one run per entry
    /// (docs/13-dataset.md §6.1): the chunk runs are kept, raw, until the data ends, in memory up to
    /// 64 MiB and in a temporary file here beyond, deleted when the writer is disposed. A write
    /// without a locating index uses none.
    /// </remarks>
    public string? ScratchDirectory { get; init; }

    /// <summary>What the chunk runs may hold in memory before they move to <see cref="ScratchDirectory"/>.</summary>
    internal long ScratchMemoryBytes { get; init; } = IndexWriter.DefaultScratchMemoryBytes;

    /// <summary>
    /// The row span above which a sorted run writes its rows at 64 bits; 2³² − 1 by construction,
    /// lowered only by the tests that read such a run back.
    /// </summary>
    internal long WideRowsAbove { get; init; } = uint.MaxValue;

    /// <summary>
    /// When a locating run's segment table goes to fence pages, and how large they are (13 §6.3);
    /// lowered only by the tests that page a short run.
    /// </summary>
    internal FenceShape Fences { get; init; } = FenceShape.Default;

    /// <summary>
    /// Whether the chooser reads a list's elements from their ingest blocks (docs/11 §3.2.4);
    /// turned off only by the tests that compare the bytes against a chooser that measures them.
    /// </summary>
    internal bool ElementStatistics { get; init; } = true;

    /// <summary>A copy with the three things an append decides from the file.</summary>
    /// <param name="rowBlockSize">The file's block length.</param>
    /// <param name="indexes">The policy the append writes under.</param>
    /// <param name="fileStatistics">Whether the file keeps a statistics segment.</param>
    /// <param name="budgetPerMille">The index budget.</param>
    internal VortexWriteOptions ForAppend(
        int rowBlockSize, WritePolicy indexes, bool fileStatistics, int budgetPerMille) =>
        new VortexWriteOptions(this)
        {
            Indexes = indexes,
            IndexBudgetPerMille = budgetPerMille,
            FileStatistics = fileStatistics,
            RowBlockSize = rowBlockSize,
        };

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
    /// The byte limit of the string bounds a utf8 or binary column's zones carry, or 0 — the
    /// default — for none. The reference writes them at 64.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A zone of a string column then carries <c>vortex.bounded_min(n)</c> and
    /// <c>vortex.bounded_max(n)</c> beside its null count (docs/11-write-strategy.md §3.2, "when
    /// asked"): the block's smallest and largest value cut to at most <c>n</c> bytes, the lower
    /// bound a prefix and the upper one a prefix with its last character incremented, by the
    /// reference's own rules (vortex-array-0.86.1 <c>scalar/truncation.rs</c>). A reader prunes
    /// ranges, prefixes and equalities on them; a maximum no cut can bound is written as
    /// <c>unknown</c> and prunes nothing.
    /// </para>
    /// <para>
    /// OFF BY DEFAULT because it moves bytes: every string column's zone map gains two columns.
    /// The aggregates belong to <c>core2026.08.0</c>, like the zone map itself; a lower target
    /// writes no zone map at all. An append keeps the old zones' bounds when the old file has them
    /// at the same limit, and writes none for the column otherwise.
    /// </para>
    /// </remarks>
    public int StringBoundBytes { get; init; }

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
    /// <para>
    /// WRITE ATOMICITY changes with it, and the contract says so: a batch handed to
    /// <c>WriteAsync</c> is no longer guaranteed to have reached the sink when the call returns.
    /// Rows are held until a block fills or <c>CompleteAsync</c> runs. Nothing about durability
    /// changes -- the sink was never flushed per batch -- but a caller reading the sink's position
    /// to infer progress sees it move in blocks. <c>null</c> restores one chunk per call exactly.
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
}
