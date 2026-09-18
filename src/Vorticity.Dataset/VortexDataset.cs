// The dataset itself - docs/13-dataset.md §3 and §9: "a dataset is a store prefix" with two kinds
// of immutable object, and every answer it gives must equal the answer a single file would give
// (§14's acceptance).
//
// WHAT IT HOLDS. Creation, opening, the import of a file that is already in the store WITHOUT
// copying it (§3: "A single existing Vortex file becomes a dataset of one leaf: one commit object,
// no copy"), an append that writes one data object and one commit, a scan that reads the objects in
// key order while SKIPPING the ones its summaries refute and the subtrees a node's summaries refute
// (§4.2), `Rows(a, b)` through the tree's row sums (§6.6), and a cache that keeps the immutable data
// objects open across scans. The key-ordered read across objects is `DatasetScanBuilder.InKeyOrder`
// over `KeyOrderedMerge`; the walk below hands it each object's level and tree key, which on the
// clustering key is the exact lower bound the merge opens objects by.
//
// WHERE THE SUMMARIES COME FROM, and the reason it costs nothing. A data object's bounds are its own
// file statistics (02 §3), which the writer has just computed and which sit in the buffer the sink
// still holds. So an append reads them out of MEMORY, before the put, rather than opening the object
// it just wrote: §9.1 counts dependent round trips, and this is one that does not have to happen.
//
// THE KEY IS THE CLUSTERING KEY'S ENCODED MINIMUM when the dataset declares one, and the first row
// position otherwise -- §4.1's two cases. The minimum is the row encoding of the object's smallest
// key, read off its mandatory run (`ClusteringKey.MinimumAsync`); the position is eight big-endian
// bytes, whose `memcmp` order is their numeric order. Either is followed by the object's uid, which
// makes the key unique (`KeyOf`).
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Columns;
using Vorticity.Expressions;
using Vorticity.File;
using Vorticity.IO;
using Vorticity.Scan;
using Vorticity.Types;
using Vorticity.Types.Serialization;
using Vorticity.Writing;

namespace Vorticity.Dataset;

/// <summary>What a dataset needs beyond its store.</summary>
public sealed record DatasetOptions
{
    /// <summary>The chunking seed (§4.1). Drawn once at creation and carried by every header.</summary>
    public ulong Seed { get; init; } = (ulong)Random.Shared.NextInt64();

    /// <summary>How the data objects an append writes are encoded.</summary>
    public VortexWriteOptions Write { get; init; } = new VortexWriteOptions();

    /// <summary>
    /// The clustering key: the columns the dataset is ordered by (§4.1), or null for the objects'
    /// first row position.
    /// </summary>
    /// <remarks>
    /// Declaring one changes two things and nothing else. A leaf's key becomes the row-encoded
    /// minimum of the key over the object, so the tree walks the objects in key order; and every
    /// data object an append writes carries the mandatory sorted run on that key (§6.1), so a
    /// lookup inside one object is a seek rather than a scan. It is fixed at creation and travels
    /// in every header: a reader that opens the dataset finds it there.
    /// </remarks>
    public IReadOnlyList<string>? ClusteringKey { get; init; }

    /// <summary>How many times a commit rebases before giving up (§8.2).</summary>
    public int MaxAttempts { get; init; } = 8;

    /// <summary>
    /// What vacuum keeps (§10), fixed at creation and carried by every header: the versions beyond
    /// the current one, and how long a superseded version stays readable. <see langword="default"/>
    /// is the specification's seven days and no version beyond the window.
    /// </summary>
    public RetentionSettings Retention { get; init; }

    /// <summary>
    /// The compaction settings every header carries (§5): the level cap, level 1's object size and
    /// the fan-out. <see langword="default"/> states none, and a planner uses its own options.
    /// </summary>
    public CompactionSettings Compaction { get; init; }

    /// <summary>The most bytes one appended data object may buffer before the store takes it.</summary>
    public long MaxObjectBytes { get; init; } = ObjectSegmentSink.DefaultMaxBytes;

    /// <summary>The columns §4.2 summarises, or null for the first <see cref="SummaryColumnLimit"/>.</summary>
    public IReadOnlyList<string>? SummaryColumns { get; init; }

    /// <summary>How many columns an entry summarises when none are declared (§4.2: 32).</summary>
    public int SummaryColumnLimit { get; init; } = ColumnSummary.DefaultLimit;

    /// <summary>How many data objects stay open between scans.</summary>
    /// <remarks>
    /// A data object is immutable (§3), so an open handle on one can never go stale; the only reason
    /// to close it is to bound the file descriptors and the parsed footers a reader holds.
    /// </remarks>
    public int MaxOpenObjects { get; init; } = 8;

    /// <summary>The boundary rule, or null for the prolly rule at the header's chunker settings.</summary>
    /// <remarks>
    /// §4.1 puts the chunker's settings in the header so that every writer of a dataset agrees on
    /// where pages end, and <see langword="null"/> is how a handle reads them from there. A rule
    /// given HERE overrides them, which is what the B+tree comparison of §13.J and a test that wants
    /// a deep tree from few objects both need — and it is then the caller's business to give the
    /// same rule to every writer of that dataset, since only the prolly settings travel in the
    /// header. Two writers under two rules still produce a correct tree; they produce different
    /// pages for the same keys, which costs §4.1's history independence and nothing else.
    /// </remarks>
    public IBoundaryRule? Rule { get; init; }
}

/// <summary>One data object being written: its identity, its sink and its writer.</summary>
/// <param name="Identity">The uid §7 mints for it, before a byte is written.</param>
/// <param name="Key">Its key in the store.</param>
/// <param name="Sink">Where its bytes go; <c>Position</c> is what it has written so far.</param>
/// <param name="Writer">The file writer over the sink.</param>
internal sealed record ObjectDraft(
    Guid Identity, string Key, ObjectSegmentSink Sink, VortexFileWriter Writer);

/// <summary>A data object that has reached the store, and the key its leaf sits at (§4.1).</summary>
/// <param name="Entry">Its leaf entry, summaries included.</param>
/// <param name="Key">Its tree key: what orders it, then its uid.</param>
internal readonly record struct WrittenObject(ObjectEntry Entry, ReadOnlyMemory<byte> Key);

/// <summary>A versioned dataset over an object store.</summary>
public sealed class VortexDataset : IAsyncDisposable
{
    /// <summary>The bytes of the uid that ends every tree key (<see cref="KeyOf"/>).</summary>
    private const int UidBytes = 16;

    private readonly IObjectStore _store;
    private readonly DatasetOptions _options;
    private readonly DTypeArena _types = new DTypeArena();
    private readonly ObjectCache _objects;
    private CommitHeader _header;
    private DatasetLevels _levels;
    private CommitPageSource _pages;

    private VortexDataset(
        IObjectStore store, DatasetOptions options, CommitHeader header, DatasetLevels levels, CommitPageSource pages)
    {
        _store = store;
        _options = options;
        _objects = new ObjectCache(store, options.MaxOpenObjects);
        _header = header;
        _levels = levels;
        _pages = pages;
        Schema = header.Schema.IsEmpty
            ? default
            : DTypeProtobuf.Read(header.Schema.Span, _types);
        Key = Schema.IsDefault ? null : ClusteringKey.For(header.ClusteringKey, Schema);
    }

    /// <summary>The version this handle reads.</summary>
    public ulong Version => _header.Version;

    /// <summary>The dataset's schema.</summary>
    public DType Schema { get; }

    /// <summary>The rows of every object it holds, over every level.</summary>
    public long RowCount => _levels.Rows;

    /// <summary>The data objects it holds, over every level.</summary>
    public long ObjectCount => _levels.Entries;

    /// <summary>Its levels (§5.2), where level 0 is the one an append lands in.</summary>
    public DatasetLevels Levels => _levels;

    /// <summary>Where this version's tree pages are read from, for a caller walking the levels.</summary>
    public IPageSource Pages => _pages;

    /// <summary>The levels of level 0's tree: 0 when it is empty, 1 when one leaf page holds it all.</summary>
    public int Depth => _levels[0].Depth;

    /// <summary>
    /// How far this version is from §5.2's invariant: the objects level 0 holds above its ceiling.
    /// </summary>
    /// <remarks>
    /// Reported, never refused (§5.3): compaction is the user's background job and a library never
    /// stalls a writer. A non-zero lag is a read bound that has degraded and says by how much.
    /// </remarks>
    public long Lag => _levels.LagAtLevelZero();

    /// <summary>The seed every boundary of its trees is decided under (§4.1).</summary>
    public ulong Seed => _header.Seed;

    /// <summary>The compaction settings its header carries (§5), zero-valued when it states none.</summary>
    public CompactionSettings Compaction => _header.Compaction;

    /// <summary>What vacuum keeps (§10), as the header carries it.</summary>
    public RetentionSettings Retention => _header.Retention;

    /// <summary>The columns it is ordered by (§4.1), empty when it is ordered by row position.</summary>
    public IReadOnlyList<string> ClusteringKeyPaths => _header.ClusteringKey;

    /// <summary>Its clustering key (§4.1), or null when it is ordered by row position.</summary>
    public ClusteringKey? Key { get; }

    /// <summary>Creates a dataset under a store prefix and returns a handle on version 1.</summary>
    /// <param name="store">The store. The dataset is everything under its keys.</param>
    /// <param name="schema">The rows' dtype.</param>
    /// <param name="options">The seed and the write options.</param>
    /// <param name="cancellationToken">Cancels the requests.</param>
    /// <returns>The dataset.</returns>
    /// <exception cref="ObjectStoreException">The store already holds a dataset.</exception>
    public static async ValueTask<VortexDataset> CreateAsync(
        IObjectStore store,
        DType schema,
        DatasetOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        options ??= new DatasetOptions();
        (ulong existing, _) = await DatasetCommitter.LatestAsync(store, cancellationToken).ConfigureAwait(false);
        if (existing != 0)
        {
            throw new ObjectStoreException(
                $"This store already holds a dataset at version {existing}; open it rather than creating it.");
        }

        CommitHeader template = new CommitHeader
        {
            Version = 1,
            Seed = options.Seed,
            Schema = DTypeProtobuf.Serialize(schema),
            ClusteringKey = [.. options.ClusteringKey ?? []],
            Retention = options.Retention,
            Compaction = options.Compaction,
            Chunker = new ChunkerSettings(
                ProllyBoundaryRule.DefaultMinBytes,
                ProllyBoundaryRule.DefaultTargetBytes,
                ProllyBoundaryRule.DefaultMaxBytes),
        };

        CommitResult result = await DatasetCommitter
            .CommitAsync(store, [], Commit(options, template), cancellationToken).ConfigureAwait(false);
        return await OpenAsync(store, options, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Opens the dataset's latest version.</summary>
    /// <param name="store">The store.</param>
    /// <param name="options">The write options; the seed is the dataset's own.</param>
    /// <param name="cancellationToken">Cancels the requests.</param>
    /// <returns>The dataset.</returns>
    /// <exception cref="ObjectNotFoundException">The store holds no dataset.</exception>
    public static async ValueTask<VortexDataset> OpenAsync(
        IObjectStore store, DatasetOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        (ulong version, CommitObject? commit) = await DatasetCommitter
            .LatestAsync(store, cancellationToken).ConfigureAwait(false);
        if (commit is null)
        {
            throw ObjectNotFoundException.For(CommitKey.Prefix);
        }

        CommitPageSource pages = new CommitPageSource(store) { Reading = version };
        pages.Inline(commit.Header);
        pages.Know(version, commit.HeaderEnd);
        return new VortexDataset(
            store,
            (options ?? new DatasetOptions()) with { Seed = commit.Header.Seed },
            commit.Header,
            DatasetLevels.Of(commit.Header),
            pages);
    }

    /// <summary>Re-reads the latest version.</summary>
    /// <param name="cancellationToken">Cancels the requests.</param>
    /// <returns>The version this handle now reads.</returns>
    public async ValueTask<ulong> RefreshAsync(CancellationToken cancellationToken = default)
    {
        (ulong version, CommitObject? commit) = await DatasetCommitter
            .LatestAsync(_store, cancellationToken).ConfigureAwait(false);
        if (commit is null)
        {
            return _header.Version;
        }

        CommitPageSource pages = new CommitPageSource(_store) { Reading = version };
        pages.Inline(commit.Header);
        pages.Know(version, commit.HeaderEnd);
        _header = commit.Header;
        _levels = DatasetLevels.Of(commit.Header);
        _pages = pages;
        return version;
    }

    /// <summary>
    /// Takes a Vortex file that is already in the store as a data object, **without copying it**
    /// (§3).
    /// </summary>
    /// <param name="objectKey">Its key in the store.</param>
    /// <param name="cancellationToken">Cancels the requests.</param>
    /// <returns>The version created.</returns>
    /// <remarks>
    /// The file is opened to learn what the entry must say — its rows and its identity — and its
    /// bytes are never read again, let alone rewritten. A file with no identity (one this library
    /// did not write) enters with a zero uid: it is scanned like any other, its tree key stands on
    /// its object key, and it cannot be indexed by fragment until a compaction rewrites it with an
    /// identity (docs/13-dataset.md §7, "as delivered"). A file whose schema is not the dataset's is
    /// refused (§15.4).
    /// </remarks>
    public async ValueTask<ulong> ImportAsync(string objectKey, CancellationToken cancellationToken = default)
    {
        ObjectKey.Check(objectKey);
        ObjectHead head = await _store.HeadAsync(objectKey, cancellationToken).ConfigureAwait(false)
            ?? throw ObjectNotFoundException.For(objectKey);

        ObjectSegmentSource source = new ObjectSegmentSource(_store, objectKey);
        ObjectEntry entry;
        ReadOnlyMemory<byte> treeKey;
        await using (source.ConfigureAwait(false))
        {
            VortexFile file = await VortexFile
                .OpenAsync(source, new VortexOpenOptions(), cancellationToken).ConfigureAwait(false);
            await using (file.ConfigureAwait(false))
            {
                // §15.4: until schema evolution is decided, every object has the dataset's schema,
                // and one with another is refused here rather than failing the first scan.
                if (!Schema.IsDefault && file.Schema != Schema)
                {
                    throw new ArgumentException(
                        $"The object '{objectKey}' has a schema other than the dataset's; a dataset " +
                        "holds objects of one schema (docs/13-dataset.md §15.4).",
                        nameof(objectKey));
                }

                entry = new ObjectEntry(
                    objectKey, Identity(file), file.RowCount, head.Length, UInt128.Zero, Summaries(file));
                treeKey = await TreeKeyAsync(file, entry, RowCount, cancellationToken).ConfigureAwait(false);
            }
        }

        return await ApplyAsync(
            [new DatasetOperation.AddObject(treeKey, entry)], cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Writes the batches as one data object and adds it to the dataset.</summary>
    /// <param name="batches">The rows.</param>
    /// <param name="cancellationToken">Cancels the requests.</param>
    /// <returns>The version created.</returns>
    /// <remarks>
    /// One object, one commit. The object is created before the commit, so a crash between the two
    /// leaves an object no commit references — "a crash leaves nothing to clean up but data
    /// objects" (§4.3), which vacuum collects (§10).
    /// </remarks>
    public async ValueTask<ulong> AppendAsync(
        IAsyncEnumerable<RecordBatch> batches, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(batches);
        ObjectDraft draft = StartObject();
        long rows = 0;
        await using (draft.Writer.ConfigureAwait(false))
        {
            await foreach (RecordBatch batch in batches.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                rows += batch.RowCount;
                await draft.Writer.WriteAsync(batch, cancellationToken).ConfigureAwait(false);
            }

            await draft.Writer.CompleteAsync(cancellationToken).ConfigureAwait(false);
        }

        WrittenObject? written = await SealAsync(draft, rows, RowCount, cancellationToken)
            .ConfigureAwait(false);
        return written is { } produced
            ? await ApplyAsync(
                [new DatasetOperation.AddObject(produced.Key, produced.Entry)], cancellationToken)
                .ConfigureAwait(false)
            : _header.Version;
    }

    /// <summary>Applies operations and moves this handle to the version they created.</summary>
    /// <param name="operations">What to do (§8.2).</param>
    /// <param name="cancellationToken">Cancels the requests.</param>
    /// <returns>The version created.</returns>
    public async ValueTask<ulong> ApplyAsync(
        IReadOnlyList<DatasetOperation> operations, CancellationToken cancellationToken = default) =>
        (await CommitAsync(operations, cancellationToken).ConfigureAwait(false)).Version;

    /// <summary>Applies operations and reports what the commit made of each of them (§8.2).</summary>
    /// <param name="operations">What to do.</param>
    /// <param name="cancellationToken">Cancels the requests.</param>
    /// <returns>The commit, outcomes included.</returns>
    internal async ValueTask<CommitResult> CommitAsync(
        IReadOnlyList<DatasetOperation> operations, CancellationToken cancellationToken)
    {
        CommitResult result = await DatasetCommitter
            .CommitAsync(_store, operations, Commit(_options, _header), cancellationToken).ConfigureAwait(false);
        await RefreshAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    /// <summary>Every data object of this version, over every level, in key order.</summary>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>The entries.</returns>
    public async IAsyncEnumerable<ObjectEntry> ObjectsAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (PositionedObject held in
            WalkAsync(null, 0, long.MaxValue, null, cancellationToken).ConfigureAwait(false))
        {
            yield return held.Entry;
        }
    }

    /// <summary>What compaction is due on this version, and what it would cost (§5.3).</summary>
    /// <param name="options">The numbers of §5, or null for the specification's.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>The plan; its job is null when nothing is over its bound.</returns>
    public ValueTask<CompactionPlan> PlanCompactionAsync(
        CompactionOptions? options = null, CancellationToken cancellationToken = default) =>
        CompactionPolicy.PlanAsync(this, options, cancellationToken);

    /// <summary>Runs the compaction that is due, if one is (§5.3).</summary>
    /// <param name="options">The numbers of §5, or null for the specification's.</param>
    /// <param name="cancellationToken">Cancels the reads, the writes and the commit.</param>
    /// <returns>What it did, or null when nothing was due.</returns>
    /// <remarks>
    /// ONE STEP, AND THE CALLER DECIDES WHETHER THERE IS ANOTHER. §5.3 makes compaction "the user's
    /// background job": a library that looped here would rewrite gigabytes inside a call that looks
    /// like a maintenance hint. A caller that wants the invariant restored loops until this returns
    /// null, and one that wants to spend a fixed budget calls it a fixed number of times.
    /// </remarks>
    public async ValueTask<CompactionResult?> CompactAsync(
        CompactionOptions? options = null, CancellationToken cancellationToken = default)
    {
        CompactionPlan plan = await PlanCompactionAsync(options, cancellationToken).ConfigureAwait(false);
        return plan.Job is { } job
            ? await DatasetCompactor.RunAsync(this, job, cancellationToken).ConfigureAwait(false)
            : null;
    }

    /// <summary>Deletes what no version inside the retention window references (§10).</summary>
    /// <param name="options">The clock and the dry run, or null for the defaults.</param>
    /// <param name="cancellationToken">Cancels the reads and the deletes.</param>
    /// <returns>What it kept and what it deleted.</returns>
    /// <remarks>
    /// Explicit, never automatic (§10). It marks from the store's latest version, not from this
    /// handle's: a handle on an older version is a reader like any other, and outlives the window at
    /// its own risk.
    /// </remarks>
    public ValueTask<VacuumResult> VacuumAsync(VacuumOptions? options = null, CancellationToken cancellationToken = default) =>
        DatasetVacuum.RunAsync(_store, options, cancellationToken);

    /// <summary>
    /// Moves what this dataset still references in <paramref name="versions"/>' commit objects into
    /// a new one, so that the next vacuum past the window can delete them (§10's repack).
    /// </summary>
    /// <param name="versions">The commit objects to empty, usually <see cref="VacuumResult.Sparse"/>.</param>
    /// <param name="cancellationToken">Cancels the reads and the commit.</param>
    /// <returns>The version created, and whether anything moved.</returns>
    /// <remarks>
    /// A metadata-only commit: no row is read or written, and the version's content hash does not
    /// change. A version still inside the window keeps its own commit object whatever moves out of it.
    /// </remarks>
    public async ValueTask<(ulong Version, OperationOutcome Outcome)> RepackAsync(
        IReadOnlyList<ulong> versions, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(versions);
        CommitResult result = await CommitAsync([new DatasetOperation.Repack(versions)], cancellationToken).ConfigureAwait(false);
        return (result.Version, result.Outcomes[0]);
    }

    /// <summary>
    /// Checks this version's pages, objects and fragments against what its references and entries
    /// promise (§10); offline, and the only reader of the content hash.
    /// </summary>
    /// <param name="since">A version already verified, whose shared pages and entries are not checked
    /// again; null to verify everything.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>What held, and every problem named.</returns>
    public ValueTask<DatasetVerification> VerifyAsync(ulong? since = null, CancellationToken cancellationToken = default) =>
        DatasetVerifier.VerifyAsync(_store, new VerifyOptions { Version = Version, Since = since }, cancellationToken);

    /// <summary>
    /// The rank of <paramref name="key"/> on the clustering key: how many rows hold a smaller key
    /// (§6.6's fourth row).
    /// </summary>
    /// <param name="key">The key, in the clustering key's domain.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>The rows whose key is below <paramref name="key"/>; a null key is below nothing.</returns>
    /// <remarks>
    /// A count of the range below the key, answered by the objects' entries wherever an object lies
    /// wholly inside it and by the exact cover of the objects the key cuts
    /// (<see cref="DatasetScanBuilder.CountAsync"/>): at most two per level above 0, plus level 0's.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The dataset has no clustering key, or a composite one.</exception>
    public ValueTask<long> RankAsync(FilterLiteral key, CancellationToken cancellationToken = default)
    {
        if (Key is not { IsComposite: false } clustering)
        {
            throw new InvalidOperationException(
                "A rank is on the clustering key, and this dataset declares " +
                (Key is null ? "none (13 §4.1)." : "a composite one: rank a tuple's leading column with a count instead."));
        }

        return Scan()
            .Where(Expr.Lt(Expr.Field(clustering.Paths[0]), Expr.Literal(key)))
            .CountAsync(cancellationToken);
    }

    /// <summary>A scan over every object of this version.</summary>
    /// <returns>The builder.</returns>
    public DatasetScanBuilder Scan() => new DatasetScanBuilder(this);

    /// <summary>A scan over the dataset's rows <c>[from, to)</c>, in the tree's order (§6.6).</summary>
    /// <param name="from">The first row, inclusive.</param>
    /// <param name="to">One past the last.</param>
    /// <returns>The builder.</returns>
    public DatasetScanBuilder Rows(long from, long to) => new DatasetScanBuilder(this).Rows(from, to);

    /// <inheritdoc/>
    public ValueTask DisposeAsync() => _objects.DisposeAsync();

    /// <summary>
    /// The objects a walk keeps: those inside <c>[from, to)</c> whose summaries, and their
    /// ancestors', do not refute the predicate.
    /// </summary>
    /// <param name="pruner">The predicate, prepared once, or null to keep every object.</param>
    /// <param name="from">The first row of the dataset to reach.</param>
    /// <param name="to">One past the last.</param>
    /// <param name="metrics">Counters to fill, or null.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>The objects, in key order.</returns>
    internal async IAsyncEnumerable<PositionedObject> WalkAsync(
        SummaryPruner? pruner,
        long from,
        long to,
        DatasetScanMetrics? metrics,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        Func<InternalEntry, bool>? descend = pruner is null ? null : node =>
        {
            if (node.Summary.IsEmpty
                || ObjectSummaries.FromBytes(node.Summary.Span).MayMatch(pruner, node.Rows))
            {
                return true;
            }

            if (metrics is { } counters)
            {
                counters.SubtreesSkipped++;
            }

            return false;
        };

        await foreach ((TreeEntry held, long firstRow, int level) in
            EntriesAsync(from, to, descend, cancellationToken).ConfigureAwait(false))
        {
            ObjectEntry entry = ObjectEntry.FromBytes(held.Value.Span);
            if (metrics is { } counters)
            {
                counters.ObjectsConsidered++;
            }

            if (pruner is not null && !entry.Summaries.MayMatch(pruner, entry.Rows))
            {
                if (metrics is { } skipped)
                {
                    skipped.ObjectsSkipped++;
                }

                continue;
            }

            yield return new PositionedObject(entry, firstRow) { Level = level, TreeKey = held.Key };
        }
    }

    /// <summary>What orders an object in its tree: its tree key without the uid that ends it.</summary>
    /// <param name="treeKey">The key, as <see cref="KeyOf"/> made it.</param>
    /// <returns>
    /// The row-encoded minimum of the clustering key, exact (§4.1), or the first row position when
    /// the dataset declares none.
    /// </returns>
    internal static ReadOnlyMemory<byte> OrderOf(ReadOnlyMemory<byte> treeKey) => treeKey[..^UidBytes];

    /// <summary>
    /// Every level's entries, merged into one key order, each with its first row in the dataset.
    /// </summary>
    /// <param name="from">The first row to reach.</param>
    /// <param name="to">One past the last.</param>
    /// <param name="descend">Whether a subtree is worth reading, or null to read all of them.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <remarks>
    /// ONE LEVEL IS THE FAST PATH AND IT IS NOT AN OPTIMISATION. A single level's own walk tests a
    /// subtree's ROW SUM before descending into it, so `Rows(a, b)` costs O(log N) there (§6.6).
    /// Across levels the row offsets are not known until the merge has produced them — an object of
    /// level 1 may sit between two objects of level 0 — so the range is applied per entry instead,
    /// and the cost is the objects rather than the rows. Bounded either way, and the difference is
    /// stated rather than hidden. A k-way merge over ≤ 8 + L cursors is what §6.6 prices anyway.
    /// </remarks>
    private async IAsyncEnumerable<(TreeEntry Entry, long FirstRow, int Level)> EntriesAsync(
        long from,
        long to,
        Func<InternalEntry, bool>? descend,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        List<DatasetTree> trees = [];
        List<int> levels = [];
        foreach ((int level, DatasetTree tree) in _levels.Occupied())
        {
            trees.Add(tree);
            levels.Add(level);
        }

        if (trees.Count <= 1)
        {
            DatasetTree only = trees.Count == 1 ? trees[0] : DatasetTree.Empty;
            int level = trees.Count == 1 ? levels[0] : 0;
            await foreach (PositionedEntry positioned in
                only.WalkAsync(_pages, from, to, descend, cancellationToken).ConfigureAwait(false))
            {
                yield return (positioned.Entry, positioned.FirstRow, level);
            }

            yield break;
        }

        IAsyncEnumerator<PositionedEntry>[] walks = new IAsyncEnumerator<PositionedEntry>[trees.Count];
        bool[] live = new bool[trees.Count];
        try
        {
            for (int i = 0; i < trees.Count; i++)
            {
                walks[i] = trees[i]
                    .WalkAsync(_pages, 0, long.MaxValue, descend, cancellationToken)
                    .GetAsyncEnumerator(cancellationToken);
                live[i] = await walks[i].MoveNextAsync().ConfigureAwait(false);
            }

            long row = 0;
            while (true)
            {
                int smallest = -1;
                for (int i = 0; i < walks.Length; i++)
                {
                    if (live[i]
                        && (smallest < 0
                            || TreePage.Compare(
                                walks[i].Current.Entry.Key.Span, walks[smallest].Current.Entry.Key.Span) < 0))
                    {
                        smallest = i;
                    }
                }

                if (smallest < 0)
                {
                    yield break;
                }

                TreeEntry entry = walks[smallest].Current.Entry;
                if (row < to && row + entry.Rows > from)
                {
                    yield return (entry, row, levels[smallest]);
                }

                row += entry.Rows;
                live[smallest] = await walks[smallest].MoveNextAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            foreach (IAsyncEnumerator<PositionedEntry>? walk in walks)
            {
                if (walk is not null)
                {
                    await walk.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
    }

    /// <summary>
    /// Borrows one of the dataset's data objects, open with the index fragments its entry names
    /// (§3: the objects are immutable; §6.4: their fragments are not).
    /// </summary>
    /// <param name="entry">Its leaf entry, as this version holds it.</param>
    /// <param name="cancellationToken">Cancels the reads and the open.</param>
    /// <returns>A lease the caller disposes when it is done reading.</returns>
    /// <exception cref="ObjectNotFoundException">
    /// The object is gone: this version fell out of the retention window and vacuum took it (§10).
    /// </exception>
    internal async ValueTask<ObjectLease> RentAsync(ObjectEntry entry, CancellationToken cancellationToken)
    {
        try
        {
            return await _objects.RentAsync(entry, _pages, cancellationToken).ConfigureAwait(false);
        }
        catch (ObjectNotFoundException missing) when (missing.Version == 0)
        {
            throw ObjectNotFoundException.InVersion(entry.Key, Version, missing);
        }
    }

    /// <summary>The bytes of one of this version's index fragments (§6.4), checked against its reference.</summary>
    /// <param name="reference">The fragment's reference, from an object's leaf entry.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The fragment: a container, or a bundle of them.</returns>
    internal ValueTask<ReadOnlyMemory<byte>> ReadFragmentAsync(PageReference reference, CancellationToken cancellationToken) =>
        _pages.ReadFragmentAsync(reference, cancellationToken);

    /// <summary>Starts one data object: a fresh identity, the sink that will hold it, its writer.</summary>
    /// <returns>The draft, whose writer the caller drives and completes.</returns>
    /// <remarks>
    /// ONE PLACE WRITES A DATA OBJECT, and the fuzzer of step 40 is why. An append and a compaction
    /// both mint an identity, apply §6.1's mandatory run and derive the leaf key from what they
    /// wrote; two copies of that would be two chances to derive a key that is not a function of the
    /// object, which is the defect that turned ten appends into six objects.
    /// </remarks>
    internal ObjectDraft StartObject()
    {
        Guid identity = Guid.NewGuid();
        string key = CommitKey.ForData(identity.ToString("N", CultureInfo.InvariantCulture));
        ObjectSegmentSink sink = new ObjectSegmentSink(_store, key, _options.MaxObjectBytes);
        VortexWriteOptions write = _options.Write.WithIdentity(identity);
        if (Key is { } clustering)
        {
            // §6.1's mandatory run, added to whatever policy the caller asked for.
            write = clustering.Applied(write);
        }

        return new ObjectDraft(identity, key, sink, VortexFileWriter.Create(sink, Schema, write));
    }

    /// <summary>Puts a completed draft in the store and mints its leaf entry and tree key.</summary>
    /// <param name="draft">The draft, whose writer has been completed.</param>
    /// <param name="rows">What it wrote.</param>
    /// <param name="firstRow">Its first row in the dataset, for an object ordered by position.</param>
    /// <param name="cancellationToken">Cancels the put.</param>
    /// <returns>The object, or null when it held no rows and nothing was put.</returns>
    internal async ValueTask<WrittenObject?> SealAsync(
        ObjectDraft draft, long rows, long firstRow, CancellationToken cancellationToken)
    {
        if (rows == 0)
        {
            draft.Sink.Discard();
            return null;
        }

        long bytes = draft.Sink.Position;
        UInt128 hash = draft.Sink.ContentHash;

        // Before the put, out of the buffer the sink still holds: the bounds §4.2 asks the entry to
        // carry, and the smallest key §4.1 orders the leaf by, cost no request at all this way.
        (ObjectSummaries summaries, byte[] prefix) =
            await DescribeAsync(draft.Sink.Written, firstRow, cancellationToken).ConfigureAwait(false);
        if (await draft.Sink.CommitAsync(cancellationToken).ConfigureAwait(false) != PutOutcome.Created)
        {
            throw new ObjectStoreException($"'{draft.Key}' was taken; a fresh uid cannot collide (13 §3).");
        }

        ObjectEntry entry = new ObjectEntry(draft.Key, Uid(draft.Identity), rows, bytes, hash, summaries);
        return new WrittenObject(entry, KeyOf(prefix, entry));
    }

    /// <summary>The bounds §4.2 asks an entry to carry, read out of a file's own statistics.</summary>
    /// <param name="file">The data object, open.</param>
    /// <remarks>
    /// THE CLUSTERING KEY'S COLUMNS ARE ALWAYS AMONG THEM, whatever the limit or the declared list
    /// says. They are the columns the dataset is ORDERED by: an entry that did not summarise them
    /// could not fall back on §4.1's minimum for an object with no cursor, and a scan could not
    /// prune on the one column a reader is most likely to filter by. A key on the fortieth column
    /// of a wide table is exactly the case a default of "the first 32" would have lost in silence.
    /// </remarks>
    private ObjectSummaries Summaries(VortexFile file)
    {
        IReadOnlyList<ColumnSummary> columns = _options.SummaryColumns is { } declared
            ? ColumnSummaries.Of(file, declared)
            : ColumnSummaries.Of(file, _options.SummaryColumnLimit);
        if (Key is not { } clustering)
        {
            return ObjectSummaries.From(columns);
        }

        List<ColumnSummary> all = [.. columns];
        HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (ColumnSummary column in columns)
        {
            seen.Add(column.Path);
        }

        foreach (ColumnSummary column in ColumnSummaries.Of(file, clustering.Paths))
        {
            if (seen.Add(column.Path))
            {
                all.Add(column);
            }
        }

        return ObjectSummaries.From(all);
    }

    /// <summary>
    /// The two things a leaf entry needs, read out of bytes that have not reached the store yet:
    /// the object's bounds and the key its leaf sits at.
    /// </summary>
    /// <param name="bytes">The whole file, as the sink still holds it.</param>
    /// <param name="firstRow">Its first row in the dataset.</param>
    /// <param name="cancellationToken">Cancels the parse.</param>
    private async ValueTask<(ObjectSummaries Summaries, byte[] Prefix)> DescribeAsync(
        ReadOnlyMemory<byte> bytes, long firstRow, CancellationToken cancellationToken)
    {
        MemorySegmentSource source = new MemorySegmentSource(bytes);
        VortexFile file = await VortexFile
            .OpenAsync(source, new VortexOpenOptions(), cancellationToken).ConfigureAwait(false);
        await using (file.ConfigureAwait(false))
        {
            ObjectSummaries summaries = Summaries(file);
            return (summaries, await PrefixAsync(file, summaries, firstRow, cancellationToken)
                .ConfigureAwait(false));
        }
    }

    /// <summary>What orders an object's leaf (§4.1): its smallest key, or its first row position.</summary>
    /// <param name="file">The object, open.</param>
    /// <param name="summaries">Its bounds.</param>
    /// <param name="firstRow">Its first row in the dataset, as this handle counts them.</param>
    /// <param name="cancellationToken">Cancels the seek a clustering key makes.</param>
    private async ValueTask<byte[]> PrefixAsync(
        VortexFile file, ObjectSummaries summaries, long firstRow, CancellationToken cancellationToken) =>
        Key is { } clustering
            ? await clustering.MinimumAsync(file, summaries, cancellationToken).ConfigureAwait(false)
            : PositionOf(firstRow);

    /// <summary>The tree key of an object: what orders it, then what makes it unique.</summary>
    /// <param name="file">The object, open.</param>
    /// <param name="entry">Its leaf entry, whose identity is the suffix.</param>
    /// <param name="firstRow">Its first row in the dataset.</param>
    /// <param name="cancellationToken">Cancels the seek a clustering key makes.</param>
    private async ValueTask<ReadOnlyMemory<byte>> TreeKeyAsync(
        VortexFile file, ObjectEntry entry, long firstRow, CancellationToken cancellationToken) =>
        KeyOf(
            await PrefixAsync(file, entry.Summaries, firstRow, cancellationToken).ConfigureAwait(false),
            entry);

    private static CommitOptions Commit(DatasetOptions options, CommitHeader template)
    {
        ulong seed = template.Seed == 0 ? options.Seed : template.Seed;
        return new CommitOptions
        {
            Seed = seed,
            Template = template,
            MaxAttempts = options.MaxAttempts,
            Rule = options.Rule ?? RuleOf(template.Chunker, seed),
        };
    }

    /// <summary>The rule a header's chunker settings name, or null for the defaults.</summary>
    /// <param name="chunker">The settings the header carries (§4.1).</param>
    /// <param name="seed">The dataset's seed.</param>
    private static IBoundaryRule? RuleOf(ChunkerSettings chunker, ulong seed) =>
        chunker.MinBytes > 0 && chunker.TargetBytes > 0 && chunker.MaxBytes > 0
            ? new ProllyBoundaryRule(seed, chunker.MinBytes, chunker.TargetBytes, chunker.MaxBytes)
            : null;

    /// <summary>The tree key of an object: what orders it, then what makes it unique.</summary>
    /// <param name="prefix">Its order: the row-encoded clustering key, or its first row position.</param>
    /// <param name="entry">Its leaf entry, whose identity (§7) no other object shares.</param>
    /// <remarks>
    /// THE SUFFIX IS THE UID, AND THE FUZZER IS WHY. The prefix orders the object and does not
    /// identify it: two writers on one dataset read the same version, compute the same first row
    /// position, and produce the same prefix — and an add at a key another object holds REPLACES
    /// it. Ten appends became six objects, and nothing said so. The suffix has to make the key
    /// unique, and it has to be a function of the OBJECT rather than of the tree: §8.2 re-applies
    /// an operation after a rebase, and a store that crashed after its put holds the object under
    /// the key the first attempt chose, so a suffix recomputed from the winner's state would add
    /// the object a second time. The uid is both: unique by construction (§7 mints it per version
    /// of the bytes) and carried by the entry.
    ///
    /// What it costs is the ORDER between two objects whose prefixes are equal: it is their uids'
    /// order, which is arbitrary, where §8.2's first row says "order by commit". Under a clustering
    /// key that is a tie between equal minima and means nothing. Without one it is two concurrent
    /// appends that believed they started at the same row, and they did; their relative order is
    /// then a coin the uid already flipped, and both of them are there, which is the property that
    /// was actually at stake.
    /// </remarks>
    private static ReadOnlyMemory<byte> KeyOf(ReadOnlySpan<byte> prefix, ObjectEntry entry)
    {
        // A file this library did not write has no identity to mint (§7), and its entry's uid is
        // zero; two of them would share a suffix. Its OBJECT KEY is unique in the store by
        // definition, so it stands in, hashed to the same sixteen bytes. The entry's uid stays
        // zero, because that field says what the postscript says and a fragment binds to it.
        UInt128 unique = entry.Uid != UInt128.Zero
            ? entry.Uid
            : System.IO.Hashing.XxHash128.HashToUInt128(System.Text.Encoding.UTF8.GetBytes(entry.Key));
        byte[] key = new byte[prefix.Length + UidBytes];
        prefix.CopyTo(key);
        BinaryPrimitives.WriteUInt64BigEndian(key.AsSpan(prefix.Length), (ulong)(unique >> 64));
        BinaryPrimitives.WriteUInt64BigEndian(key.AsSpan(prefix.Length + 8), (ulong)unique);
        return key;
    }

    /// <summary>An object's order when the dataset declares no clustering key: its first row.</summary>
    /// <param name="row">Its first row in the dataset, as this handle counts them.</param>
    /// <remarks>
    /// Big-endian, so that `memcmp` order is numeric order: the tree compares keys as bytes and
    /// nothing else (06), and a little-endian key would order 256 before 2.
    /// </remarks>
    private static byte[] PositionOf(long row)
    {
        byte[] prefix = new byte[sizeof(long)];
        BinaryPrimitives.WriteInt64BigEndian(prefix, row);
        return prefix;
    }

    /// <summary>A file's identity as §7 mints it, or zero when it has none.</summary>
    internal static UInt128 Identity(VortexFile file) =>
        file.Identity is { } identity ? Uid(identity) : UInt128.Zero;

    private static UInt128 Uid(Guid identity)
    {
        Span<byte> bytes = stackalloc byte[16];
        identity.TryWriteBytes(bytes);
        return ((UInt128)BinaryPrimitives.ReadUInt64LittleEndian(bytes) << 64)
            | BinaryPrimitives.ReadUInt64LittleEndian(bytes[8..]);
    }
}
