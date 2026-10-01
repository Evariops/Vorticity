using System;
using System.Collections.Generic;
using Vorticity.Scanning;

namespace Vorticity.Dataset;

/// <summary>What a dataset handle needs beyond its store: how it writes, reads and commits.</summary>
/// <remarks>
/// The clustering key and the retention are fixed when the dataset is created and carried by every
/// commit; at an open they come from the dataset and these values are ignored.
/// </remarks>
public sealed record DatasetOptions
{
    /// <summary>
    /// The session whose pool, cache, bound on reads in flight and parallelism the dataset's scans
    /// use; <see cref="VortexSession.Default"/> unless given.
    /// </summary>
    public VortexSession Session { get; init; } = VortexSession.Default;

    /// <summary>How the data objects this handle writes are encoded.</summary>
    public VortexWriteOptions Write { get; init; } = VortexWriteOptions.Default;

    /// <summary>
    /// The columns the dataset is ordered by, or null for the order objects arrive in. Declaring one
    /// orders the objects by the smallest key they hold, gives every object this handle writes a
    /// sorted run on it so that a lookup inside one object is a seek, and makes compaction keep the
    /// levels above 0 key-disjoint, so a lookup by key touches a bounded number of objects.
    /// </summary>
    public IReadOnlyList<string>? ClusteringKey { get; init; }

    /// <summary>How many times a commit re-applies its changes to a newer version before giving up; 8 by default.</summary>
    public int MaxAttempts { get; init; } = 8;

    /// <summary>
    /// How long a superseded version stays readable before vacuum may delete what only it
    /// references; zero, the default, means seven days.
    /// </summary>
    public TimeSpan RetentionWindow { get; init; }

    /// <summary>How many superseded versions vacuum keeps whatever their age; none by default.</summary>
    public int RetainedVersions { get; init; }

    /// <summary>The most bytes one data object may take while it is written, before the store takes it; 1 GiB by default.</summary>
    public long MaxObjectBytes { get; init; } = ObjectSegmentSink.DefaultMaxBytes;

    /// <summary>The columns an object's entry summarises, or null for the first <see cref="SummaryColumnLimit"/>.</summary>
    /// <remarks>The summaries are what a scan prunes whole objects by without opening them; the clustering key's columns are always among them.</remarks>
    public IReadOnlyList<string>? SummaryColumns { get; init; }

    /// <summary>How many columns an entry summarises when none are declared; 32 by default.</summary>
    public int SummaryColumnLimit { get; init; } = ColumnSummary.DefaultLimit;

    /// <summary>
    /// How many data objects stay open between scans; 32 by default. A data object is immutable, so
    /// an open handle never goes stale: this only bounds the descriptors and parsed footers held.
    /// </summary>
    /// <remarks>
    /// A scan in key order holds open at once every object whose keys interleave with another's,
    /// whatever this bound, and the next such scan opens again those the bound did not keep: a
    /// bound at the number of objects spares it every open.
    /// </remarks>
    public int MaxOpenObjects { get; init; } = 32;

    /// <summary>
    /// The bytes of tree pages read from the store that the handle keeps from one version it reads to
    /// the next, least recently used first out; 32 MiB by default, and 0 keeps none past the version
    /// that read them. A version names every page its commit did not rewrite by the reference an
    /// earlier version gave it, so a refresh or a commit asks the store again only for the pages that
    /// changed, as long as the pages it walks fit here.
    /// </summary>
    /// <remarks>
    /// Beside them the handle keeps, whatever this says, what the reads opening the last sixteen
    /// versions it read or wrote brought back of their pages: the pages those versions wrote, which a
    /// later version names until a commit rewrites them.
    /// </remarks>
    public long PageCacheBytes { get; init; } = 32L << 20;

    /// <summary>
    /// The most bytes a commit that takes level 0 past its ceiling compacts before it returns; 0, the
    /// default, compacts nothing inline. Past the ceiling, level 0 is merged into the level above when
    /// the job reads no more than this, so a writer with no process to spare holds the read bound
    /// itself, at the price of the merge's latency in the commit that crosses it; a larger job is left
    /// to another driver, and the commit returns at once.
    /// </summary>
    /// <remarks>
    /// The commit stands whatever befalls the compaction after it, and its outcome is what the caller
    /// is told: a compaction that fails, or that the caller cancels, leaves level 0 as it was for the
    /// next commit or another driver. The compaction commits a version of its own, after the caller's.
    /// </remarks>
    public long InlineCompactionBytes { get; init; }

    /// <summary>
    /// The clock a commit's creation time is read from, and that dates when the handle last knew its
    /// version to be the latest; the system's by default.
    /// </summary>
    /// <remarks>
    /// It must agree with the store's to within half the retention window, as a vacuum's clock must:
    /// for that half window after it learned its version was the latest, a handle takes every version
    /// after it for still being in the store, and past it lists the commits instead.
    /// </remarks>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    /// <summary>
    /// The smallest object whose deleted rows are marked in its entry rather than rewritten out of
    /// it; 1 MiB by default, below which a rewrite costs a few milliseconds and keeps reads plain.
    /// </summary>
    internal long MarkedObjectBytes { get; init; } = 1L << 20;

    /// <summary>
    /// The share of an object's rows its marks may reach, as a divisor: past a
    /// <c>1/MarkedShare</c>-th of its rows deleted, a delete rewrites the object instead; 8 by default.
    /// </summary>
    internal int MarkedShare { get; init; } = 8;

    /// <summary>
    /// The most bytes an object's marks take in its entry, past which a delete rewrites the object
    /// instead; 1 KiB by default, a few hundred runs. The entry lives in a leaf page, which every
    /// commit that marks a row of any object on it writes again: the bound trades the rewrites it
    /// saves against the bytes it adds to each of those commits.
    /// </summary>
    internal int MarkedVectorBytes { get; init; } = 1 << 10;

    /// <summary>The chunking seed, drawn once at creation and carried by every header.</summary>
    internal ulong Seed { get; init; } = (ulong)Random.Shared.NextInt64();

    /// <summary>
    /// The compaction settings every header carries: the level cap, level 1's object size and the
    /// fan-out. <see langword="default"/> states none, and a planner uses its own options.
    /// </summary>
    internal CompactionSettings Compaction { get; init; }

    /// <summary>
    /// The boundary rule, or null for the prolly rule at the header's chunker settings, which is how
    /// every writer of a dataset agrees on where pages end. A rule given here overrides them, and it
    /// is then the caller's business to give the same rule to every writer: two writers under two
    /// rules still build a correct tree, but not the same pages for the same keys.
    /// </summary>
    internal IBoundaryRule? Rule { get; init; }

    /// <summary>What vacuum keeps, as a header records it.</summary>
    internal RetentionSettings Retention => new RetentionSettings(RetainedVersions, (long)RetentionWindow.TotalSeconds);
}
