using System;
using System.Collections.Generic;
using Vorticity.Editions;
using Vorticity.Indexes;
using Vorticity.Writing;

namespace Vorticity;

/// <summary>How much the writer does beyond the data.</summary>
internal enum WriteProfile
{
    /// <summary>Everything the options ask for.</summary>
    Default = 0,

    /// <summary>
    /// No index, whatever <see cref="VortexWriteOptions.WritePolicy"/> says: byte for byte a write under
    /// <see cref="WritePolicy.None"/> with the same <see cref="VortexWriteOptions.Identity"/>.
    /// </summary>
    Fastest = 1,
}

/// <summary>Policy for one written file.</summary>
public sealed class VortexWriteOptions
{
    /// <summary>The defaults: compression on.</summary>
    internal VortexWriteOptions()
    {
    }

    /// <summary>
    /// A copy, which the <c>With…</c> methods override one option of. An option added below is
    /// copied here, once: this is the only field list.
    /// </summary>
    private VortexWriteOptions(VortexWriteOptions other)
    {
        ArgumentNullException.ThrowIfNull(other);
        WritePolicy = other.WritePolicy;
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
    internal static VortexWriteOptions Default { get; } = new VortexWriteOptions();

    /// <summary>
    /// The index policy: per column path an <see cref="IndexPolicy"/>. Default
    /// <see cref="WritePolicy.Auto"/>, which keeps an index only where it pays. The policy is
    /// serialized into the index directory, so an append reuses it without being told.
    /// </summary>
    internal WritePolicy WritePolicy { get; init; } = WritePolicy.Auto;

    /// <summary>How much the writer does beyond the data. Default <see cref="WriteProfile.Default"/>.</summary>
    internal WriteProfile Profile { get; init; } = WriteProfile.Default;

    /// <summary>
    /// The scheme to write a column with, by column path, for callers who know. The hint is priced
    /// against each chunk's own statistics and written when it still applies; a chunk it cannot
    /// describe is priced in full, and the next chunk is offered the hint again. A path that names
    /// nothing in the schema throws at
    /// <see cref="VortexFileWriter.Create(ISegmentSink, Types.DType, VortexWriteOptions)"/>, because
    /// a hint that silently did nothing would have no channel to say so.
    /// </summary>
    internal IReadOnlyDictionary<string, VortexEncodingHint>? EncodingHints { get; init; }

    /// <summary>
    /// The bytes the file's indexes may occupy together, as a share of the data bytes, in parts per
    /// thousand. Default 100 (10 %). A builder that would take the file past this is abandoned
    /// whole, with the reason in the <see cref="WriteReport"/>.
    /// </summary>
    internal int IndexBudgetPerMille { get; init; } = 100;

    /// <summary>
    /// The encoder of the composite keys <see cref="WritePolicy.ForKey"/> asks for; null by default.
    /// The core does not row-encode, so a composite key asked for without an encoder is abandoned,
    /// with that reason in the <see cref="WriteReport"/>.
    /// </summary>
    internal IKeyEncoder? KeyEncoder { get; init; }

    /// <summary>
    /// The identity the file's postscript carries, or null -- the default -- for a fresh random
    /// one. Pinning it makes a write a pure function of its batches, byte for byte; two different
    /// files must never share one.
    /// </summary>
    public Guid? Identity { get; init; }

    /// <summary>These options with <see cref="Identity"/> pinned to <paramref name="identity"/>.</summary>
    /// <param name="identity">The sixteen bytes the postscript will carry.</param>
    /// <returns>A copy; these options are unchanged.</returns>
    internal VortexWriteOptions WithIdentity(Guid identity) =>
        new VortexWriteOptions(this) { Identity = identity };

    /// <summary>These options with a different index policy.</summary>
    /// <param name="indexes">The policy to write under.</param>
    /// <returns>A copy; these options are unchanged.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="indexes"/> is null.</exception>
    internal VortexWriteOptions WithIndexes(WritePolicy indexes)
    {
        ArgumentNullException.ThrowIfNull(indexes);
        return new VortexWriteOptions(this) { WritePolicy = indexes };
    }

    /// <summary>These options with a composite-key encoder.</summary>
    /// <param name="keyEncoder">The encoder, from the <c>Vorticity.RowEncoding</c> package.</param>
    /// <returns>A copy; these options are unchanged.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="keyEncoder"/> is null.</exception>
    internal VortexWriteOptions WithKeyEncoder(IKeyEncoder keyEncoder)
    {
        ArgumentNullException.ThrowIfNull(keyEncoder);
        return new VortexWriteOptions(this) { KeyEncoder = keyEncoder };
    }

    /// <summary>
    /// Where a locating index's chunk runs wait for their merge once they pass the memory budget,
    /// or null -- the default -- for the system's temporary directory. The files are deleted when
    /// the writer is disposed.
    /// </summary>
    internal string? ScratchDirectory { get; init; }

    /// <summary>What the chunk runs may hold in memory before they move to <see cref="ScratchDirectory"/>.</summary>
    internal long ScratchMemoryBytes { get; init; } = IndexWriter.DefaultScratchMemoryBytes;

    /// <summary>The row span above which a sorted run writes its rows at 64 bits.</summary>
    internal long WideRowsAbove { get; init; } = uint.MaxValue;

    /// <summary>When a locating run's segment table goes to fence pages, and how large they are.</summary>
    internal FenceShape Fences { get; init; } = FenceShape.Default;

    /// <summary>Whether the chooser reads a list's elements from their ingest blocks.</summary>
    internal bool ElementStatistics { get; init; } = true;

    /// <summary>A copy with the three things an append decides from the file.</summary>
    internal VortexWriteOptions ForAppend(
        int rowBlockSize, WritePolicy indexes, bool fileStatistics, int budgetPerMille) =>
        new VortexWriteOptions(this)
        {
            WritePolicy = indexes,
            IndexBudgetPerMille = budgetPerMille,
            FileStatistics = fileStatistics,
            RowBlockSize = rowBlockSize,
        };

    /// <summary>
    /// Whether the writer may pick an encoding per column chunk. Default <see langword="true"/>;
    /// off writes every column canonically.
    /// </summary>
    internal bool Compress { get; init; } = true;

    /// <summary>
    /// Whether the file carries a statistics segment: per top-level field its exact
    /// <c>min</c> / <c>max</c>, <c>null_count</c>, and <c>is_sorted</c> /
    /// <c>is_strict_sorted</c> when the pass tracked the column's order. Default on.
    /// </summary>
    internal bool FileStatistics { get; init; } = true;

    /// <summary>
    /// The byte limit of the string bounds a utf8 or binary column's zones carry, or 0 — the
    /// default — for none.
    /// </summary>
    /// <remarks>
    /// The lower bound is a prefix and the upper one a prefix with its last character incremented,
    /// so a maximum no cut can bound is written as <c>unknown</c> and prunes nothing. An append
    /// keeps the old zones' bounds only when the old file has them at the same limit.
    /// </remarks>
    public int StringBoundBytes { get; init; }

    /// <summary>
    /// The edition every component in the file must belong to: a frozen set of component ids, and
    /// the only way to say which readers must be able to open the result. Default
    /// <see cref="EditionRegistry.Newest"/>.
    /// </summary>
    /// <remarks>
    /// Lower targets are honoured rather than approximated: the zone map is omitted below the
    /// edition that introduced it, and a component the target cannot express fails the write rather
    /// than producing a file the target's readers cannot open.
    /// </remarks>
    public VortexEdition TargetEdition { get; init; } = EditionRegistry.Newest;

    /// <summary>
    /// Rows per written chunk, as a multiple. Default 8192; <c>null</c> writes one chunk per
    /// <c>WriteAsync</c> call. Every emitted chunk but the last is a multiple of it.
    /// </summary>
    /// <remarks>
    /// Each chunk carries its own array blob, segment entry and zone-map row, so small chunks pay
    /// that fixed cost over and over. The writer therefore decides the chunking: a batch handed to
    /// <c>WriteAsync</c> is not guaranteed to have reached the sink when the call returns, and rows
    /// are held until a block fills or <c>CompleteAsync</c> runs.
    /// </remarks>
    internal int? RowBlockSize { get; init; } = 8192;

    /// <summary>
    /// Uncompressed bytes to accumulate before a chunk is emitted. Default 1 MiB; <c>null</c>
    /// disables byte-size coalescing and leaves the row granularity to
    /// <see cref="RowBlockSize"/>.
    /// </summary>
    /// <remarks>
    /// A block is emitted once it is both at least this many bytes and at least
    /// <see cref="RowBlockSize"/> rows, then in whole multiples of the row block; the remainder
    /// goes out at close. Measured in canonical bytes, before compression, since that is the only
    /// size available when the decision is made, and over the whole batch, since the zones here are
    /// shared across columns.
    /// </remarks>
    internal long? DataBlockTargetBytes { get; init; } = 1L << 20;
}
