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
using Vorticity.Scanning;
using Vorticity.Types;
using Vorticity.Types.Serialization;
using Vorticity.Writing;

namespace Vorticity.Dataset;

/// <summary>What a dataset needs beyond its store.</summary>
internal sealed record DatasetOptions
{
    /// <summary>The chunking seed. Drawn once at creation and carried by every header.</summary>
    public ulong Seed { get; init; } = (ulong)Random.Shared.NextInt64();

    /// <summary>How the data objects an append writes are encoded.</summary>
    public VortexWriteOptions Write { get; init; } = new VortexWriteOptions();

    /// <summary>
    /// The columns the dataset is ordered by, or null for the objects' first row position. Fixed at
    /// creation and carried by every header. Declaring one makes a leaf's key the row-encoded
    /// minimum of the key over the object, and gives every appended object a sorted run on that key
    /// so that a lookup inside one object is a seek rather than a scan.
    /// </summary>
    public IReadOnlyList<string>? ClusteringKey { get; init; }

    /// <summary>How many times a commit rebases before giving up.</summary>
    public int MaxAttempts { get; init; } = 8;

    /// <summary>
    /// What vacuum keeps, fixed at creation and carried by every header: the versions beyond the
    /// current one, and how long a superseded version stays readable.
    /// </summary>
    public RetentionSettings Retention { get; init; }

    /// <summary>
    /// The compaction settings every header carries: the level cap, level 1's object size and the
    /// fan-out. <see langword="default"/> states none, and a planner uses its own options.
    /// </summary>
    public CompactionSettings Compaction { get; init; }

    /// <summary>The most bytes one appended data object may buffer before the store takes it.</summary>
    public long MaxObjectBytes { get; init; } = ObjectSegmentSink.DefaultMaxBytes;

    /// <summary>The columns to summarise, or null for the first <see cref="SummaryColumnLimit"/>.</summary>
    public IReadOnlyList<string>? SummaryColumns { get; init; }

    /// <summary>How many columns an entry summarises when none are declared.</summary>
    public int SummaryColumnLimit { get; init; } = ColumnSummary.DefaultLimit;

    /// <summary>
    /// How many data objects stay open between scans. A data object is immutable, so an open handle
    /// never goes stale; this only bounds the descriptors and parsed footers a reader holds.
    /// </summary>
    public int MaxOpenObjects { get; init; } = 8;

    /// <summary>
    /// The boundary rule, or null for the prolly rule at the header's chunker settings, which is how
    /// every writer of a dataset agrees on where pages end. A rule given here overrides them, and it
    /// is then the caller's business to give the same rule to every writer: two writers under two
    /// rules still build a correct tree, but not the same pages for the same keys.
    /// </summary>
    public IBoundaryRule? Rule { get; init; }

    /// <summary>The clock a commit's creation time is read from; the system's by default.</summary>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    /// <summary>
    /// The session whose pool, cache, bound on reads in flight and parallelism the dataset's scans
    /// use; <see cref="VortexSession.Default"/> unless given.
    /// </summary>
    public VortexSession Session { get; init; } = VortexSession.Default;
}

/// <summary>One data object being written; its identity is minted before a byte is written.</summary>
internal sealed record ObjectDraft(
    Guid Identity, string Key, ObjectSegmentSink Sink, VortexFileWriter Writer);

/// <summary>
/// A data object that has reached the store, and its tree key: what orders it, then its uid.
/// </summary>
internal readonly record struct WrittenObject(ObjectEntry Entry, ReadOnlyMemory<byte> Key);

/// <summary>A versioned dataset over an object store.</summary>
internal sealed class VortexDataset : IAsyncDisposable
{
    /// <summary>The bytes of the uid that ends every tree key.</summary>
    private const int UidBytes = 16;

    private readonly IObjectStore _store;
    private readonly DatasetOptions _options;
    private readonly DTypeArena _types = new DTypeArena();
    private readonly ObjectCache _objects;
    private DatasetSnapshot _snapshot;

    private VortexDataset(IObjectStore store, DatasetOptions options, CommitHeader header, CommitPageSource pages)
    {
        _store = store;
        _options = options;
        _objects = new ObjectCache(store, options.MaxOpenObjects, options.Session);
        _snapshot = new DatasetSnapshot(header, pages, _objects);
        DType = header.Schema.IsEmpty
            ? default
            : DTypeProtobuf.Read(header.Schema.Span, _types);
        Schema = DType.IsDefault ? VortexSchema.Create([]) : VortexTypes.SchemaOf(DType);
        Key = DType.IsDefault ? null : ClusteringKey.For(header.ClusteringKey, DType);
    }

    /// <summary>The version this handle reads; it moves only when this handle commits or refreshes.</summary>
    public ulong Version => _snapshot.Version;

    /// <summary>The columns every object of the dataset holds.</summary>
    public VortexSchema Schema { get; }

    /// <summary>The session the dataset's scans run in.</summary>
    public VortexSession Session => _options.Session;

    /// <summary>The rows of every object it holds, over every level.</summary>
    public long RowCount => _snapshot.RowCount;

    /// <summary>The data objects it holds, over every level.</summary>
    public long ObjectCount => _snapshot.Levels.Entries;

    /// <summary>
    /// The objects level 0 holds above its ceiling. Reported, never refused: compaction is the
    /// user's background job, and a non-zero lag says how far a read bound has degraded.
    /// </summary>
    public long Lag => _snapshot.Levels.LagAtLevelZero();

    /// <summary>The columns it is ordered by, empty when it is ordered by row position.</summary>
    public IReadOnlyList<string> ClusteringKeyPaths => _snapshot.Header.ClusteringKey;

    /// <summary>The dataset's schema as the engine holds it.</summary>
    internal DType DType { get; }

    /// <summary>The version this handle reads, whole: what a scan built now keeps reading.</summary>
    internal DatasetSnapshot Snapshot => Volatile.Read(ref _snapshot);

    /// <summary>The options it was opened with.</summary>
    internal DatasetOptions Options => _options;

    /// <summary>Its levels, where level 0 is the one an append lands in.</summary>
    internal DatasetLevels Levels => _snapshot.Levels;

    /// <summary>Where this version's tree pages are read from, for a caller walking the levels.</summary>
    internal IPageSource Pages => _snapshot.Pages;

    /// <summary>The levels of level 0's tree: 0 when it is empty, 1 when one leaf page holds it all.</summary>
    internal int Depth => _snapshot.Levels[0].Depth;

    /// <summary>The seed every boundary of its trees is decided under.</summary>
    internal ulong Seed => _snapshot.Header.Seed;

    /// <summary>The compaction settings its header carries, zero-valued when it states none.</summary>
    internal CompactionSettings Compaction => _snapshot.Header.Compaction;

    /// <summary>What vacuum keeps, as the header carries it.</summary>
    internal RetentionSettings Retention => _snapshot.Header.Retention;

    /// <summary>Its clustering key, or null when it is ordered by row position.</summary>
    internal ClusteringKey? Key { get; }

    /// <summary>
    /// Creates a dataset in a store that holds none, and returns a handle on its first version.
    /// </summary>
    /// <param name="store">The store; the dataset is everything under its <c>commit/</c> and <c>data/</c> keys.</param>
    /// <param name="schema">The columns every object of the dataset holds.</param>
    /// <param name="options">The clustering key, the retention and how objects are written; null for the defaults.</param>
    /// <param name="cancellationToken">Cancels the requests.</param>
    /// <returns>The handle, on version 1; the caller disposes it.</returns>
    /// <exception cref="ObjectStoreException">The store already holds a dataset.</exception>
    /// <exception cref="ArgumentException">A clustering key column is not in the schema.</exception>
    public static ValueTask<VortexDataset> CreateAsync(
        IObjectStore store,
        VortexSchema schema,
        DatasetOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(schema);
        return CreateAsync(store, VortexTypes.ToDType(schema, new DTypeArena()), options, cancellationToken);
    }

    /// <summary>
    /// Creates a dataset under a store prefix — everything under its keys — and returns a handle on
    /// version 1.
    /// </summary>
    internal static async ValueTask<VortexDataset> CreateAsync(
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

    /// <summary>
    /// Opens a dataset's latest version, found with one listing and read with one request.
    /// </summary>
    /// <param name="store">The store holding the dataset.</param>
    /// <param name="options">How this handle reads and writes; the settings fixed at creation come from the dataset itself.</param>
    /// <param name="cancellationToken">Cancels the requests.</param>
    /// <returns>The handle, pinned to the latest version until it commits or refreshes; the caller disposes it.</returns>
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

        return new VortexDataset(
            store,
            (options ?? new DatasetOptions()) with { Seed = commit.Header.Seed },
            commit.Header,
            PagesOf(store, version, commit));
    }

    /// <summary>Moves this handle to the latest version.</summary>
    /// <param name="cancellationToken">Cancels the requests.</param>
    /// <returns>The version the handle now reads.</returns>
    public async ValueTask<ulong> RefreshAsync(CancellationToken cancellationToken = default)
    {
        (ulong version, CommitObject? commit) = await DatasetCommitter
            .LatestAsync(_store, cancellationToken).ConfigureAwait(false);
        if (commit is null)
        {
            return Version;
        }

        Volatile.Write(ref _snapshot, new DatasetSnapshot(commit.Header, PagesOf(_store, version, commit), _objects));
        return version;
    }

    /// <summary>A scan of the version this handle holds, typed by <typeparamref name="TRecord"/>, over every object.</summary>
    /// <typeparam name="TRecord">The record; its members bind to the dataset's columns by name.</typeparam>
    /// <returns>A fresh scan of the version the handle holds now, whatever the handle does next.</returns>
    /// <remarks>
    /// A batch's <c>StartRow</c> counts the dataset's rows, object after object in the tree's order,
    /// as <c>Rows</c> does. Objects whose summaries refute the filter are not opened.
    /// </remarks>
    public Scan<TRecord> Scan<TRecord>()
        where TRecord : IVortexRecord<TRecord> => new Scan<TRecord>(new DatasetScanSource(this, Snapshot));

    /// <summary>A scan of the version this handle holds for a caller without a record type: columns by name, filters as text.</summary>
    /// <param name="columns">The columns to read, by top-level name or <c>.</c>-separated path; none reads every column.</param>
    /// <returns>A fresh scan of the version the handle holds now.</returns>
    public Scan Scan(params ReadOnlySpan<string> columns) => new Scan(new DatasetScanSource(this, Snapshot), columns);

    /// <summary>
    /// Whether the version this handle holds may hold a row <paramref name="filter"/> is true for,
    /// from the object and subtree summaries its header already carries: false is a proof, true is not.
    /// </summary>
    /// <typeparam name="TRecord">The record the filter is written against.</typeparam>
    /// <param name="filter">A lambda over the record's columns.</param>
    /// <returns>False when the summaries prove no row matches.</returns>
    public bool MayMatch<TRecord>(Func<Probe<TRecord>, Predicate> filter)
        where TRecord : IVortexRecord<TRecord>
    {
        ArgumentNullException.ThrowIfNull(filter);
        RecordBinding binding = RecordBinding.For<TRecord>(Schema, Session.Options.Extensions);
        Predicate predicate = filter(new Probe<TRecord>(binding));
        return !predicate.IsNone && (predicate.IsAll ? RowCount > 0 : Snapshot.MayMatch(predicate.Node!));
    }

    /// <summary>A page source for one version, holding the pages its header inlines.</summary>
    private static CommitPageSource PagesOf(IObjectStore store, ulong version, CommitObject commit)
    {
        CommitPageSource pages = new CommitPageSource(store) { Reading = version };
        pages.Inline(commit.Header);
        pages.Know(version, commit.HeaderEnd);
        return pages;
    }

    /// <summary>
    /// Takes a Vortex file that is already in the store as a data object, without copying it, and
    /// returns the version created.
    /// </summary>
    /// <remarks>
    /// The file is opened to learn what the entry must say and its bytes are never rewritten. A
    /// file this library did not write has no identity, so it enters with a zero uid: its tree key
    /// stands on its object key, and it cannot be indexed by fragment until a compaction rewrites
    /// it with an identity. A file whose schema is not the dataset's is refused.
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
                // Every object has the dataset's schema; one with another is refused here rather
                // than failing the first scan.
                if (!DType.IsDefault && file.DType != DType)
                {
                    throw new ArgumentException(
                        $"The object '{objectKey}' has a schema other than the dataset's; a dataset " +
                        "holds objects of one schema.",
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

    /// <summary>
    /// Writes the batches as one data object, adds it to the dataset and returns the version
    /// created. The object is created before the commit, so a crash between the two leaves an
    /// object no commit references, which vacuum collects.
    /// </summary>
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
            : Version;
    }

    /// <summary>
    /// Applies operations, moves this handle to the version they created, and returns it.
    /// </summary>
    public async ValueTask<ulong> ApplyAsync(
        IReadOnlyList<DatasetOperation> operations, CancellationToken cancellationToken = default) =>
        (await CommitAsync(operations, cancellationToken).ConfigureAwait(false)).Version;

    /// <summary>Applies operations and reports what the commit made of each of them.</summary>
    internal async ValueTask<CommitResult> CommitAsync(
        IReadOnlyList<DatasetOperation> operations, CancellationToken cancellationToken)
    {
        CommitResult result = await DatasetCommitter
            .CommitAsync(_store, operations, Commit(_options, Snapshot.Header), cancellationToken).ConfigureAwait(false);
        await RefreshAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    /// <summary>Every data object of this version, over every level, in key order.</summary>
    public async IAsyncEnumerable<ObjectEntry> ObjectsAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (PositionedObject held in
            WalkAsync(null, 0, long.MaxValue, null, cancellationToken).ConfigureAwait(false))
        {
            yield return held.Entry;
        }
    }

    /// <summary>
    /// What compaction is due on this version and what it would cost; the plan's job is null when
    /// nothing is over its bound.
    /// </summary>
    public ValueTask<CompactionPlan> PlanCompactionAsync(
        CompactionOptions? options = null, CancellationToken cancellationToken = default) =>
        CompactionPolicy.PlanAsync(this, options, cancellationToken);

    /// <summary>
    /// Runs the compaction that is due, if one is, and returns what it did or null when nothing was
    /// due. One step only: a caller that wants the invariant restored loops until this returns
    /// null, rather than having a maintenance hint rewrite gigabytes in one call.
    /// </summary>
    public async ValueTask<CompactionResult?> CompactAsync(
        CompactionOptions? options = null, CancellationToken cancellationToken = default)
    {
        CompactionPlan plan = await PlanCompactionAsync(options, cancellationToken).ConfigureAwait(false);
        return plan.Job is { } job
            ? await DatasetCompactor.RunAsync(this, job, cancellationToken).ConfigureAwait(false)
            : null;
    }

    /// <summary>
    /// Deletes what no version inside the retention window references, and reports what it kept and
    /// deleted. It marks from the store's latest version, not from this handle's: a handle on an
    /// older version outlives the window at its own risk.
    /// </summary>
    public ValueTask<VacuumResult> VacuumAsync(VacuumOptions? options = null, CancellationToken cancellationToken = default) =>
        DatasetVacuum.RunAsync(_store, options, cancellationToken);

    /// <summary>
    /// Moves what this dataset still references in <paramref name="versions"/>' commit objects into
    /// a new one, so that the next vacuum past the window can delete them; returns the version
    /// created and whether anything moved.
    /// </summary>
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
    /// promise, and names every problem. <paramref name="since"/> is a version already verified,
    /// whose shared pages and entries are not checked again; null verifies everything.
    /// </summary>
    public ValueTask<DatasetVerification> VerifyAsync(ulong? since = null, CancellationToken cancellationToken = default) =>
        DatasetVerifier.VerifyAsync(_store, new VerifyOptions { Version = Version, Since = since }, cancellationToken);

    /// <summary>
    /// How many rows hold a key below <paramref name="key"/> on the clustering key; a null key is
    /// below nothing. Answered by the objects' entries wherever an object lies wholly inside the
    /// range, and by an exact count only of the objects the key cuts.
    /// </summary>
    /// <exception cref="InvalidOperationException">The dataset has no clustering key, or a composite one.</exception>
    public ValueTask<long> RankAsync(FilterLiteral key, CancellationToken cancellationToken = default)
    {
        if (Key is not { IsComposite: false } clustering)
        {
            throw new InvalidOperationException(
                "A rank is on the clustering key, and this dataset declares " +
                (Key is null ? "none (13 §4.1)." : "a composite one: rank a tuple's leading column with a count instead."));
        }

        return ScanBuilder()
            .Where(Expr.Lt(Expr.Field(clustering.Paths[0]), Expr.Literal(key)))
            .CountAsync(cancellationToken);
    }

    /// <summary>The engine's scan over every object of the version this handle holds now.</summary>
    internal DatasetScanBuilder ScanBuilder() => new DatasetScanBuilder(this, Snapshot);

    /// <inheritdoc/>
    public ValueTask DisposeAsync() => _objects.DisposeAsync();

    /// <summary>
    /// The objects inside <c>[from, to)</c> whose summaries, and their ancestors', do not refute
    /// the pruner, in key order. A null pruner keeps every object.
    /// </summary>
    internal IAsyncEnumerable<PositionedObject> WalkAsync(
        SummaryPruner? pruner, long from, long to, DatasetScanMetrics? metrics, CancellationToken cancellationToken) =>
        Snapshot.WalkAsync(pruner, from, to, metrics, cancellationToken);

    /// <summary>
    /// What orders an object in its tree: its tree key without the uid that ends it, which is the
    /// exact row-encoded minimum of the clustering key, or the first row position when the dataset
    /// declares none.
    /// </summary>
    internal static ReadOnlyMemory<byte> OrderOf(ReadOnlyMemory<byte> treeKey) => treeKey[..^UidBytes];

    /// <summary>
    /// Borrows one of the dataset's data objects, open with the index fragments its entry names.
    /// The caller disposes the lease when it is done reading.
    /// </summary>
    /// <exception cref="ObjectNotFoundException">
    /// The object is gone: this version fell out of the retention window and vacuum took it.
    /// </exception>
    internal ValueTask<ObjectLease> RentAsync(ObjectEntry entry, CancellationToken cancellationToken) =>
        Snapshot.RentAsync(entry, cancellationToken);

    /// <summary>
    /// The bytes of one of this version's index fragments, checked against the reference an
    /// object's leaf entry carries.
    /// </summary>
    internal ValueTask<ReadOnlyMemory<byte>> ReadFragmentAsync(PageReference reference, CancellationToken cancellationToken) =>
        Snapshot.Pages.ReadFragmentAsync(reference, cancellationToken);

    /// <summary>
    /// Starts one data object: a fresh identity, the sink that will hold it, and the writer the
    /// caller drives and completes. The one place a data object is started, so that the leaf key
    /// can only be derived one way, as a function of the object.
    /// </summary>
    internal ObjectDraft StartObject()
    {
        Guid identity = Guid.NewGuid();
        string key = CommitKey.ForData(identity.ToString("N", CultureInfo.InvariantCulture));
        ObjectSegmentSink sink = new ObjectSegmentSink(_store, key, _options.MaxObjectBytes);
        VortexWriteOptions write = _options.Write.WithIdentity(identity);
        if (Key is { } clustering)
        {
            // The mandatory sorted run on the clustering key, added to the caller's own policy.
            write = clustering.Applied(write);
        }

        return new ObjectDraft(identity, key, sink, VortexFileWriter.Create(sink, DType, write));
    }

    /// <summary>
    /// Puts a completed draft in the store and mints its leaf entry and tree key, or returns null
    /// when it held no rows and nothing was put.
    /// </summary>
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

        // Read out of the buffer the sink still holds, before the put: the entry's bounds and the
        // smallest key its leaf is ordered by then cost no request at all.
        (ObjectSummaries summaries, byte[] prefix) =
            await DescribeAsync(draft.Sink.Written, firstRow, cancellationToken).ConfigureAwait(false);
        if (await draft.Sink.CommitAsync(cancellationToken).ConfigureAwait(false) != PutOutcome.Created)
        {
            throw new ObjectStoreException($"'{draft.Key}' was taken; a fresh uid cannot collide (13 §3).");
        }

        ObjectEntry entry = new ObjectEntry(draft.Key, Uid(draft.Identity), rows, bytes, hash, summaries);
        return new WrittenObject(entry, KeyOf(prefix, entry));
    }

    /// <summary>
    /// The bounds an entry carries, read out of a file's own statistics. The clustering key's
    /// columns are always among them, whatever the limit or the declared list says: without them
    /// an object with no cursor has no minimum to fall back on, and a scan cannot prune on the
    /// column a reader is most likely to filter by.
    /// </summary>
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
    /// The two things a leaf entry needs, read out of the whole file as the sink still holds it,
    /// before it has reached the store: the object's bounds and the key its leaf sits at.
    /// </summary>
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

    /// <summary>What orders an object's leaf: its smallest key, or its first row position.</summary>
    private async ValueTask<byte[]> PrefixAsync(
        VortexFile file, ObjectSummaries summaries, long firstRow, CancellationToken cancellationToken) =>
        Key is { } clustering
            ? await clustering.MinimumAsync(file, summaries, cancellationToken).ConfigureAwait(false)
            : PositionOf(firstRow);

    /// <summary>The tree key of an object: what orders it, then what makes it unique.</summary>
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
            TimeProvider = options.TimeProvider,
        };
    }

    /// <summary>The rule a header's chunker settings name, or null for the defaults.</summary>
    private static IBoundaryRule? RuleOf(ChunkerSettings chunker, ulong seed) =>
        chunker.MinBytes > 0 && chunker.TargetBytes > 0 && chunker.MaxBytes > 0
            ? new ProllyBoundaryRule(seed, chunker.MinBytes, chunker.TargetBytes, chunker.MaxBytes)
            : null;

    /// <summary>
    /// The tree key of an object: the prefix that orders it, then the uid that makes it unique.
    /// </summary>
    /// <remarks>
    /// The prefix orders the object without identifying it — two writers can compute the same one —
    /// and an add at a key another object holds would replace it. The suffix must therefore be
    /// unique and a function of the object rather than of the tree, since an operation re-applied
    /// after a rebase must land on the key its first attempt chose. The uid is both. Two objects
    /// with equal prefixes are then ordered by their uids, which is arbitrary, but both are there.
    /// </remarks>
    private static ReadOnlyMemory<byte> KeyOf(ReadOnlySpan<byte> prefix, ObjectEntry entry)
    {
        // A file this library did not write has a zero uid, and two of them would share a suffix.
        // Its object key is unique in the store, so it stands in, hashed to the same sixteen bytes.
        // The entry's uid stays zero, because that field says what the postscript says.
        UInt128 unique = entry.Uid != UInt128.Zero
            ? entry.Uid
            : System.IO.Hashing.XxHash128.HashToUInt128(System.Text.Encoding.UTF8.GetBytes(entry.Key));
        byte[] key = new byte[prefix.Length + UidBytes];
        prefix.CopyTo(key);
        BinaryPrimitives.WriteUInt64BigEndian(key.AsSpan(prefix.Length), (ulong)(unique >> 64));
        BinaryPrimitives.WriteUInt64BigEndian(key.AsSpan(prefix.Length + 8), (ulong)unique);
        return key;
    }

    /// <summary>
    /// An object's order when the dataset declares no clustering key: its first row. Big-endian, so
    /// that the <c>memcmp</c> order the tree compares by is numeric order.
    /// </summary>
    private static byte[] PositionOf(long row)
    {
        byte[] prefix = new byte[sizeof(long)];
        BinaryPrimitives.WriteInt64BigEndian(prefix, row);
        return prefix;
    }

    /// <summary>A file's identity, or zero when it has none.</summary>
    internal static UInt128 Identity(VortexFile file) =>
        file.StoredIdentity is { } identity ? Uid(identity) : UInt128.Zero;

    private static UInt128 Uid(Guid identity)
    {
        Span<byte> bytes = stackalloc byte[16];
        identity.TryWriteBytes(bytes);
        return ((UInt128)BinaryPrimitives.ReadUInt64LittleEndian(bytes) << 64)
            | BinaryPrimitives.ReadUInt64LittleEndian(bytes[8..]);
    }
}
