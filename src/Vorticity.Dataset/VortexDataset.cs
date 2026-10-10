using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
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

/// <summary>
/// A data object that has reached the store, and its tree key: what orders it, then its uid.
/// </summary>
internal readonly record struct WrittenObject(ObjectEntry Entry, ReadOnlyMemory<byte> Key);

/// <summary>
/// A versioned dataset over an object store: immutable Vortex files as its data objects, one commit
/// object per version, and a tree of the objects with a summary per node, so that neither reading
/// nor committing costs in proportion to the objects.
/// </summary>
/// <remarks>
/// A handle is pinned to one version: it reads that version until it commits, which moves it to the
/// version its commit created, or refreshes. Commits are conditional creations, so concurrent
/// writers need no lock: the one that loses re-applies its changes to the winner's version.
/// A handle is thread-safe for reads; its commits are serialised by the store.
/// </remarks>
public sealed class VortexDataset : IAsyncDisposable
{
    /// <summary>The bytes of the uid that ends every tree key.</summary>
    internal const int UidBytes = 16;

    private readonly IObjectStore _store;
    private readonly DatasetOptions _options;
    private readonly ObjectCache _objects;
    private readonly PageCache _pageCache;
    private DatasetSnapshot _snapshot;
    private Exception? _inlineFailure;

    // 1 while a compaction run after a commit is under way on this handle: another commit's finds it
    // has nothing to add, since the running one takes the same job.
    private int _compactingInline;

    private VortexDataset(IObjectStore store, DatasetOptions options, ulong version, CommitObject commit, DateTimeOffset knownAt)
    {
        _store = store;
        _options = options;
        _objects = new ObjectCache(store, options.MaxOpenObjects, options.Session);
        _pageCache = new PageCache(options.PageCacheBytes);
        _snapshot = new DatasetSnapshot(commit, PagesOf(version, commit), _objects, knownAt);
    }

    /// <summary>The version this handle reads; it moves only when this handle commits or refreshes.</summary>
    public ulong Version => Snapshot.Version;

    /// <summary>
    /// The dataset's columns at the version this handle reads. An object written under an earlier
    /// schema reads as this one: a column it lacks as nulls, a column renamed under its new name.
    /// </summary>
    public VortexSchema Schema => Snapshot.Schema.Columns;

    /// <summary>The session the dataset's scans run in.</summary>
    public VortexSession Session => _options.Session;

    /// <summary>The rows of every object of the version, over every level.</summary>
    public long RowCount => Snapshot.RowCount;

    /// <summary>The data objects of the version, over every level.</summary>
    public long ObjectCount => Snapshot.Levels.Entries;

    /// <summary>
    /// The objects level 0 holds above its ceiling of eight. Reported, never refused: compaction is
    /// the caller's background job, and a non-zero lag says how far the read bound has degraded.
    /// </summary>
    public long Lag => Snapshot.Levels.LagAtLevelZero();

    /// <summary>The columns the dataset is ordered by, empty when objects are kept in the order they arrived.</summary>
    public IReadOnlyList<string> ClusteringKeyPaths => Snapshot.Header.ClusteringKey;

    /// <summary>
    /// What the latest compaction run after a commit to fail on this handle raised
    /// (<see cref="DatasetOptions.InlineCompactionBytes"/>), or null while none has. The commit
    /// stood and its caller was told so; level 0 stays as it was, and a failure that is not a
    /// passing one meets every later commit, which this says.
    /// </summary>
    public Exception? LastInlineCompactionFailure => Volatile.Read(ref _inlineFailure);

    /// <summary>The dataset's schema as the engine holds it.</summary>
    internal DType DType => Snapshot.Schema.DType;

    /// <summary>The version this handle reads, whole: what a scan built now keeps reading.</summary>
    internal DatasetSnapshot Snapshot => Volatile.Read(ref _snapshot);

    /// <summary>The options it was opened with.</summary>
    internal DatasetOptions Options => _options;

    /// <summary>The store it reads and writes.</summary>
    internal IObjectStore Store => _store;

    /// <summary>How many of its open objects a read holds: none once every read has given its objects back.</summary>
    internal int LeasedObjects => _objects.Leased;

    /// <summary>Its levels, where level 0 is the one an append lands in.</summary>
    internal DatasetLevels Levels => Snapshot.Levels;

    /// <summary>Where this version's tree pages are read from, for a caller walking the levels.</summary>
    internal IPageSource Pages => Snapshot.Pages;

    /// <summary>The levels of level 0's tree: 0 when it is empty, 1 when one leaf page holds it all.</summary>
    internal int Depth => Snapshot.Levels[0].Depth;

    /// <summary>The seed every boundary of its trees is decided under.</summary>
    internal ulong Seed => Snapshot.Header.Seed;

    /// <summary>The compaction settings its header carries, zero-valued when it states none.</summary>
    internal CompactionSettings Compaction => Snapshot.Header.Compaction;

    /// <summary>What vacuum keeps, as the header carries it.</summary>
    internal RetentionSettings Retention => Snapshot.Header.Retention;

    /// <summary>Whether the dataset lives on a store that locks what it keeps, as the header carries it.</summary>
    internal bool LockedStore => Snapshot.Header.LockedStore;

    /// <summary>Its clustering key, or null when it is ordered by arrival.</summary>
    internal ClusteringKey? Key => Snapshot.Schema.Key;

    /// <summary>
    /// Creates a dataset in a store that holds none, and returns a handle on its first version.
    /// </summary>
    /// <param name="store">The store; the dataset is everything under its <c>commit/</c> and <c>data/</c> keys.</param>
    /// <param name="schema">The columns every object of the dataset holds.</param>
    /// <param name="options">The clustering key, the retention and how objects are written; null for the defaults.</param>
    /// <param name="cancellationToken">Cancels the requests.</param>
    /// <returns>The handle, on version 1; the caller disposes it.</returns>
    /// <exception cref="ObjectStoreException">The store already holds a dataset.</exception>
    /// <exception cref="ArgumentException">A clustering key column is not in the schema, or a key of several columns names one that may hold a null.</exception>
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
    /// Opens a dataset's latest version, found with one listing and read with one request.
    /// </summary>
    /// <param name="store">The store holding the dataset.</param>
    /// <param name="options">How this handle reads and writes; the settings fixed at creation come from the dataset itself.</param>
    /// <param name="cancellationToken">Cancels the requests.</param>
    /// <returns>The handle, pinned to the latest version until it commits or refreshes; the caller disposes it.</returns>
    /// <exception cref="ObjectNotFoundException">The store holds no dataset.</exception>
    /// <exception cref="TornCommitException">The newest commit is not whole; <see cref="RemoveTornCommitAsync"/> removes it.</exception>
    public static async ValueTask<VortexDataset> OpenAsync(
        IObjectStore store, DatasetOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        options ??= new DatasetOptions();
        DateTimeOffset asked = options.TimeProvider.GetUtcNow();
        (ulong version, CommitObject? commit) = await DatasetCommitter
            .LatestAsync(store, cancellationToken).ConfigureAwait(false);
        if (commit is null)
        {
            throw ObjectNotFoundException.For(CommitKey.Prefix);
        }

        return new VortexDataset(store, options.For(commit.Header), version, commit, asked);
    }

    /// <summary>Moves this handle to the latest version.</summary>
    /// <param name="cancellationToken">Cancels the requests.</param>
    /// <returns>The version the handle now reads.</returns>
    /// <exception cref="TornCommitException">The newest commit is not whole; <see cref="RemoveTornCommitAsync"/> removes it.</exception>
    public async ValueTask<ulong> RefreshAsync(CancellationToken cancellationToken = default)
    {
        DatasetSnapshot held = Snapshot;
        DateTimeOffset asked = _options.TimeProvider.GetUtcNow();
        (ulong version, CommitObject? commit) = await DatasetCommitter
            .LatestAsync(_store, held.Version, asked - held.KnownAt < TrustSpan(held), held.Commit, cancellationToken).ConfigureAwait(false);
        if (commit is not null)
        {
            MoveTo(version, commit, asked);
        }

        return Version;
    }

    /// <summary>
    /// Removes the newest commit when it is not whole, as a writer that stopped before its last byte
    /// leaves it on a store that claims a key before it writes the object. The dataset then reads as
    /// the version before it, and the next commit takes the number again.
    /// </summary>
    /// <param name="store">The store holding the dataset.</param>
    /// <param name="cancellationToken">Cancels the requests.</param>
    /// <returns>The version removed, or null when the newest commit is whole or there is none.</returns>
    /// <remarks>
    /// The commit is read whole and checked from its preamble to its trailer first. A store whose
    /// writer still holds the object makes the read wait for it, so a commit being written is never
    /// taken for a torn one.
    /// </remarks>
    public static async ValueTask<ulong?> RemoveTornCommitAsync(IObjectStore store, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ulong version = await DatasetCommitter.NewestVersionAsync(store, cancellationToken).ConfigureAwait(false);
        if (version == 0 || await DatasetCommitter.IsWholeAsync(store, version, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        await store.DeleteAsync([CommitKey.For(version)], cancellationToken).ConfigureAwait(false);
        return version;
    }

    /// <summary>A scan of the version this handle holds, typed by <typeparamref name="TRecord"/>, over every object.</summary>
    /// <typeparam name="TRecord">The record; its members bind to the dataset's columns by name.</typeparam>
    /// <returns>A fresh scan of the version the handle holds now, whatever the handle does next.</returns>
    /// <remarks>
    /// A batch's <c>StartRow</c> counts the version's rows, object after object in the order a scan
    /// delivers them, and <c>Rows</c> takes the same positions. Objects whose summaries refute the
    /// filter are not opened, and a key cursor walks every object's cursor merged.
    /// </remarks>
    public Scan<TRecord> Scan<TRecord>()
        where TRecord : IVortexRecord<TRecord> => new Scan<TRecord>(new DatasetScanSource(this, Snapshot));

    /// <summary>A scan of the version this handle holds for a caller without a record type: columns by name, filters as text.</summary>
    /// <param name="columns">The columns to read, by top-level name or <c>.</c>-separated path; none reads every column.</param>
    /// <returns>A fresh scan of the version the handle holds now.</returns>
    public Scan Scan(params ReadOnlySpan<string> columns) => new Scan(new DatasetScanSource(this, Snapshot), columns);

    /// <summary>
    /// Whether the version this handle holds may hold a row <paramref name="filter"/> is true for,
    /// from the object and subtree summaries of the pages it holds without a request, those its
    /// header carries and those it kept from the versions before: false is a proof, true is not.
    /// It asks the store nothing: a page not in hand, or one in hand that does not hash to its
    /// reference, is one it cannot see past, and answers true for, leaving the read that comes to
    /// it to say what is wrong.
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

    /// <summary>Every data object of the version this handle holds, over every level, in the order a scan reads them.</summary>
    /// <param name="cancellationToken">Cancels the reads of the tree.</param>
    /// <returns>The objects; reading them reads tree pages only, never an object.</returns>
    public async IAsyncEnumerable<DataObject> ListObjectsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (PositionedObject held in
            Snapshot.WalkAsync(null, 0, long.MaxValue, null, cancellationToken).ConfigureAwait(false))
        {
            yield return DataObject.Of(held);
        }
    }

    /// <summary>
    /// Starts a data object: a fresh key in the store and a writer with the dataset's schema and write
    /// options, plus the sorted run the clustering key requires.
    /// </summary>
    /// <returns>The draft; write its rows, then append it, or dispose it to abandon it.</returns>
    public ObjectDraft StartObject() => StartObjectUnder(Snapshot.Schema);

    /// <summary>
    /// Starts a data object under <paramref name="schema"/>: what a rewrite uses, whose rows are read
    /// as the schema of the version it read, whatever the handle has moved to since. A caller whose
    /// rows come in key order says so with <paramref name="inKeyOrder"/>, and a key whose column then
    /// orders the object by itself takes no run.
    /// </summary>
    internal ObjectDraft StartObjectUnder(DatasetSchema schema, bool inKeyOrder = false)
    {
        Guid identity = Guid.NewGuid();
        string key = CommitKey.ForData(identity.ToString("N", CultureInfo.InvariantCulture));
        ObjectSegmentSink sink = new ObjectSegmentSink(_store, key, _options.MaxObjectBytes, _options.Session.Options.MemoryPool);
        VortexWriteOptions write = _options.Write.WithIdentity(identity);
        bool bySortedColumn = inKeyOrder && schema.Key is { OrdersBySortedColumn: true };
        if (schema.Key is { } clustering && !bySortedColumn)
        {
            // The mandatory sorted run on the clustering key, added to the caller's own policy.
            write = clustering.Applied(write);
        }

        return new ObjectDraft(this, identity, key, sink, VortexFileWriter.Create(sink, schema.DType, write))
        {
            BySortedColumn = bySortedColumn,
        };
    }

    /// <summary>
    /// Completes a data object written through <see cref="StartObject"/>, puts it in the store and
    /// adds it to the dataset in one commit.
    /// </summary>
    /// <param name="draft">The object; its writer is completed here if the caller did not.</param>
    /// <param name="cancellationToken">Cancels the put and the commit.</param>
    /// <returns>
    /// The version the commit created; the current one when the object holds no row and nothing was
    /// committed. The handle reads that version, or the later one a compaction of level 0 run after
    /// the commit created (<see cref="DatasetOptions.InlineCompactionBytes"/>), which
    /// <see cref="Version"/> then says.
    /// </returns>
    /// <remarks>
    /// The object is created before the commit, so a crash between the two leaves an object no
    /// commit references, which vacuum collects. Without a clustering key its rows follow every
    /// other object's.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The draft belongs to another handle, was already committed, or its writer was abandoned.</exception>
    public async ValueTask<ulong> AppendAsync(ObjectDraft draft, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        WrittenObject? written = await CompleteAsync(draft, await EndAsync(cancellationToken).ConfigureAwait(false), cancellationToken)
            .ConfigureAwait(false);
        return written is { } produced
            ? await ApplyAsync([new DatasetOperation.AddObject(produced.Key, produced.Entry)], cancellationToken).ConfigureAwait(false)
            : Version;
    }

    /// <summary>
    /// Writes the batches as one data object and adds it to the dataset in one commit.
    /// </summary>
    /// <param name="batches">The rows, with the dataset's schema.</param>
    /// <param name="cancellationToken">Cancels the writes, the put and the commit.</param>
    /// <returns>
    /// The version the commit created; the current one when there was no row. The handle reads that
    /// version, or the later one a compaction of level 0 run after the commit created.
    /// </returns>
    public async ValueTask<ulong> AppendAsync(
        IAsyncEnumerable<RecordBatch> batches, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(batches);
        ObjectDraft draft = StartObject();
        try
        {
            await foreach (RecordBatch batch in batches.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                await draft.Writer.WriteBatchAsync(batch, cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            await draft.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        return await AppendAsync(draft, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Takes a Vortex file that is already in the store as a data object, without copying it, and
    /// adds it to the dataset in one commit.
    /// </summary>
    /// <param name="objectKey">The file's key in the store.</param>
    /// <param name="cancellationToken">Cancels the reads and the commit.</param>
    /// <returns>
    /// The version the commit created. The handle reads that version, or the later one a compaction
    /// of level 0 run after the commit created.
    /// </returns>
    /// <remarks>
    /// The file is opened to learn what its entry must say and its bytes are never rewritten. A file
    /// this library did not write has no identity: it is scanned like any other object, and cannot be
    /// indexed apart from itself until a compaction rewrites it with one.
    /// </remarks>
    /// <exception cref="ObjectNotFoundException">The store holds no object at <paramref name="objectKey"/>.</exception>
    /// <exception cref="ArgumentException">The file's columns do not read as the dataset's: one it lacks is not nullable, one is of a type the dataset's does not widen, or one is no column the dataset has or had.</exception>
    public async ValueTask<ulong> ImportAsync(string objectKey, CancellationToken cancellationToken = default)
    {
        ObjectKey.Check(objectKey);
        ObjectHead head = await _store.HeadAsync(objectKey, cancellationToken).ConfigureAwait(false)
            ?? throw ObjectNotFoundException.For(objectKey);

        long end = await EndAsync(cancellationToken).ConfigureAwait(false);
        ObjectSegmentSource source = new ObjectSegmentSource(_store, objectKey);
        ObjectEntry entry;
        ReadOnlyMemory<byte> treeKey;
        await using (source.ConfigureAwait(false))
        {
            VortexFile file = await VortexFile
                .OpenAsync(source, ObjectCache.OpenOptions, cancellationToken).ConfigureAwait(false);
            await using (file.ConfigureAwait(false))
            {
                // An object reads as the dataset's schema, or is refused here rather than failing
                // the first scan: the same columns, or those of an earlier schema, each under a
                // name the column has or had and of a type its values survive the dataset's in.
                DatasetSchema schema = Snapshot.Schema;
                if (!schema.DType.IsDefault && file.DType != schema.DType)
                {
                    try
                    {
                        _ = ObjectColumns.Map(schema, file.DType, objectKey, strict: true);
                    }
                    catch (VortexSchemaException refused)
                    {
                        throw new ArgumentException(
                            $"The object '{objectKey}' does not read as the dataset's schema: {refused.Message}",
                            nameof(objectKey),
                            refused);
                    }
                }

                entry = new ObjectEntry(
                    objectKey, Identity(file), file.RowCount, head.Length, UInt128.Zero, Summaries(file));
                treeKey = await TreeKeyAsync(file, entry, end, cancellationToken).ConfigureAwait(false);
            }
        }

        return await ApplyAsync(
            [new DatasetOperation.AddObject(treeKey, entry)], cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Removes data objects of the version this handle holds, in one commit.</summary>
    /// <param name="objects">Objects from <see cref="ListObjectsAsync"/>.</param>
    /// <param name="cancellationToken">Cancels the commit.</param>
    /// <returns>The version created, and whether the removal applied; the latest version, unchanged, when it did not.</returns>
    /// <remarks>The objects stay in the store, readable by older versions, until vacuum deletes them.</remarks>
    public ValueTask<ReplaceResult> RemoveObjectsAsync(IReadOnlyList<DataObject> objects, CancellationToken cancellationToken = default) =>
        ReplaceObjectsAsync(objects, [], cancellationToken);

    /// <summary>
    /// Removes data objects and adds others written through <see cref="StartObject"/>, in one commit:
    /// a reader sees either the old objects or the new ones, never both nor neither.
    /// </summary>
    /// <param name="removed">Objects from <see cref="ListObjectsAsync"/>.</param>
    /// <param name="added">Drafts whose rows are written; each is completed and put before the commit.</param>
    /// <param name="cancellationToken">Cancels the puts and the commit.</param>
    /// <returns>
    /// The version created, or <see cref="OperationOutcome.Abandoned"/> and the latest version when
    /// an object to remove was already gone from it, in which case no version is created and the
    /// added objects are left for vacuum.
    /// </returns>
    /// <remarks>
    /// The added objects land in level 0, from where compaction moves them. Without a clustering key
    /// their rows follow every other object's, as an append's do.
    /// </remarks>
    /// <exception cref="ArgumentException">An object does not come from this dataset's walk.</exception>
    /// <exception cref="InvalidOperationException">A draft belongs to another handle, was already committed, or its writer was abandoned.</exception>
    public async ValueTask<ReplaceResult> ReplaceObjectsAsync(
        IReadOnlyList<DataObject> removed, IReadOnlyList<ObjectDraft> added, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(removed);
        ArgumentNullException.ThrowIfNull(added);
        List<(int Level, ReadOnlyMemory<byte> Key)> inputs = new List<(int, ReadOnlyMemory<byte>)>(removed.Count);
        List<ObjectEntry> expected = new List<ObjectEntry>(removed.Count);
        foreach (DataObject gone in removed)
        {
            ArgumentNullException.ThrowIfNull(gone, nameof(removed));
            if (gone.TreeKey.Length == 0 || gone.Entry is not { } entry)
            {
                throw new ArgumentException("An object to remove comes from ListObjectsAsync, which knows where its entry is.", nameof(removed));
            }

            inputs.Add((gone.Level, Convert.FromHexString(gone.TreeKey)));
            expected.Add(entry);
        }

        List<(int Level, ReadOnlyMemory<byte> Key, ObjectEntry Entry)> outputs = new List<(int, ReadOnlyMemory<byte>, ObjectEntry)>(added.Count);
        long position = added.Count == 0 ? 0 : await EndAsync(cancellationToken).ConfigureAwait(false);
        for (int i = 0; i < added.Count; i++)
        {
            ObjectDraft draft = added[i] ?? throw new ArgumentNullException(nameof(added));
            WrittenObject? written;
            try
            {
                written = await CompleteAsync(draft, position, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                for (int rest = i + 1; rest < added.Count; rest++)
                {
                    await added[rest].DisposeAsync().ConfigureAwait(false);
                }

                throw;
            }

            if (written is { } produced)
            {
                outputs.Add((0, produced.Key, produced.Entry));
                position += produced.Entry.Rows;
            }
        }

        if (inputs.Count == 0 && outputs.Count == 0)
        {
            return new ReplaceResult { Version = Version, Outcome = OperationOutcome.AlreadyThere };
        }

        CommitResult commit = await CommitAsync([new DatasetOperation.ReplaceObjects(inputs, outputs) { Expected = expected }], cancellationToken)
            .ConfigureAwait(false);
        if (outputs.Count > 0 && commit.Outcomes[0] == OperationOutcome.Applied)
        {
            await CompactInlineAsync(cancellationToken).ConfigureAwait(false);
        }

        return new ReplaceResult { Version = commit.Version, Outcome = commit.Outcomes[0] };
    }

    /// <summary>
    /// Deletes the rows <paramref name="filter"/> is true for, in one commit: each object that holds
    /// one has them marked in its entry, or is rewritten without them, and one left with no row is
    /// removed.
    /// </summary>
    /// <typeparam name="TRecord">The record the filter is written against.</typeparam>
    /// <param name="filter">A lambda over the record's columns. A row it is false or unknown for stays.</param>
    /// <param name="cancellationToken">Cancels the reads, the writes and the commit.</param>
    /// <returns>The version created and the rows deleted; <see cref="OperationOutcome.AlreadyThere"/> and the version the handle held when no row matched.</returns>
    /// <remarks>
    /// <para>
    /// A mark costs the rows' positions, a few bytes in the object's entry, whatever the object's size:
    /// its file is not touched, and every read leaves the marked rows out until a compaction rewrites
    /// the object. A small object, one whose marked rows would pass an eighth of its rows, or one
    /// whose marks would outgrow the bytes an entry gives them, is rewritten instead, which costs the
    /// object; on a store that locks what it keeps, every object is marked. Reads of a marked object
    /// pay for its marks: a
    /// key-ordered walk of an object of level 0 reads the keys of its deleted rows, and a step can
    /// cross a run of them.
    /// </para>
    /// <para>
    /// An object whose summaries refute the filter is not opened, and one whose own count finds no
    /// row is not touched. The rows appended by a writer that commits in the meantime are not
    /// deleted; if one rewrites an object this delete read, or marks rows in it, the delete is worked
    /// out again on its version, while an index fragment attached to the object meanwhile stays.
    /// </para>
    /// </remarks>
    /// <exception cref="VortexSchemaException">A literal or a column of the filter does not fit the dataset.</exception>
    /// <exception cref="ObjectStoreException">Concurrent commits rewrote an object this delete read on every attempt.</exception>
    /// <exception cref="DatasetIntegrityException">A guard found that the delete would lose or double rows; nothing was committed.</exception>
    public ValueTask<RowChangeResult> DeleteAsync<TRecord>(
        Func<Probe<TRecord>, Predicate> filter, CancellationToken cancellationToken = default)
        where TRecord : IVortexRecord<TRecord>
    {
        ArgumentNullException.ThrowIfNull(filter);
        Predicate predicate = filter(new Probe<TRecord>(RecordBinding.For<TRecord>(Schema, Session.Options.Extensions)));
        return predicate.IsNone
            ? ValueTask.FromResult(Unchanged())
            : DatasetRowChanges.RunAsync(this, Checked(predicate.Node), null, cancellationToken);
    }

    /// <summary>
    /// Deletes the rows <paramref name="filter"/> is true for, in one commit, for a caller without a
    /// record type; see <see cref="DeleteAsync{TRecord}(Func{Probe{TRecord}, Predicate}, CancellationToken)"/>.
    /// </summary>
    /// <param name="filter">The filter, from <see cref="VortexExpr.Parse"/> or combined with operators.</param>
    /// <param name="cancellationToken">Cancels the reads, the writes and the commit.</param>
    /// <returns>The version created and the rows deleted.</returns>
    /// <exception cref="VortexSchemaException">The filter names a column the dataset does not have, or compares one with a literal of a type it cannot compare to.</exception>
    /// <exception cref="ObjectStoreException">Concurrent commits rewrote an object this delete read on every attempt.</exception>
    /// <exception cref="DatasetIntegrityException">A guard found that the delete would lose or double rows; nothing was committed.</exception>
    public ValueTask<RowChangeResult> DeleteAsync(VortexExpr filter, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        return DatasetRowChanges.RunAsync(this, Checked(ToolPaths.Check(Schema, filter)), null, cancellationToken);
    }

    /// <summary>
    /// Changes the rows <paramref name="filter"/> is true for, in one commit: each is read as a
    /// record, passed through <paramref name="update"/>, and written again, while the objects it
    /// came from are rewritten without it.
    /// </summary>
    /// <typeparam name="TRecord">The record the rows are read and written as; it has a member for every column of the dataset.</typeparam>
    /// <param name="filter">A lambda over the record's columns. A row it is false or unknown for is left as it is.</param>
    /// <param name="update">The change, called once per row with the row as it is, returning the row as it is to be.</param>
    /// <param name="cancellationToken">Cancels the reads, the writes and the commit.</param>
    /// <returns>The version created and the rows changed; <see cref="OperationOutcome.AlreadyThere"/> and the version the handle held when no row matched.</returns>
    /// <remarks>
    /// A delete and an insert, applied together: the changed rows land in new objects of level 0, as
    /// appended rows do, since a change may move a row's key, and without a clustering key they come
    /// after every other row. Everything <see cref="DeleteAsync{TRecord}(Func{Probe{TRecord}, Predicate}, CancellationToken)"/>
    /// says of its cost and of concurrent writers holds here too; <paramref name="update"/> may be
    /// called again for a row when the change is worked out again.
    /// </remarks>
    /// <exception cref="ArgumentException"><typeparamref name="TRecord"/> has no member for a column of the dataset.</exception>
    /// <exception cref="VortexSchemaException">A literal or a column of the filter does not fit the dataset.</exception>
    /// <exception cref="ObjectStoreException">Concurrent commits rewrote an object this update read on every attempt.</exception>
    /// <exception cref="DatasetIntegrityException">A guard found that the update would lose or double rows; nothing was committed.</exception>
    public ValueTask<RowChangeResult> UpdateAsync<TRecord>(
        Func<Probe<TRecord>, Predicate> filter, Func<TRecord, TRecord> update, CancellationToken cancellationToken = default)
        where TRecord : IVortexRecord<TRecord>
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(update);
        RecordBinding binding = RecordBinding.For<TRecord>(Schema, Session.Options.Extensions);
        bool[] covered = new bool[Schema.Count];
        foreach (int column in binding.FileIndex)
        {
            covered[column] = true;
        }

        int missing = Array.IndexOf(covered, false);
        if (missing >= 0)
        {
            throw new ArgumentException(
                $"An update writes whole rows, and {typeof(TRecord).Name} has no member for the column '{Schema[missing].Name}'.",
                nameof(update));
        }

        Predicate predicate = filter(new Probe<TRecord>(binding));
        return predicate.IsNone
            ? ValueTask.FromResult(Unchanged())
            : DatasetRowChanges.RunAsync(
                this,
                Checked(predicate.Node),
                (schema, end) => new ChangedRecords<TRecord>(this, schema, update, end),
                cancellationToken);
    }

    /// <summary>
    /// Changes the dataset's columns to <paramref name="schema"/>, in one commit that rewrites no
    /// object: the objects written before read as the new schema, and compaction writes each one it
    /// takes in it.
    /// </summary>
    /// <param name="schema">The columns the dataset holds from the version this creates, in the order a scan delivers them.</param>
    /// <param name="renamed">The columns renamed, each new name mapped to the column's current one; null when none is.</param>
    /// <param name="cancellationToken">Cancels the commit.</param>
    /// <returns>The version created, which this handle now reads; the one it held when the schema already is this one.</returns>
    /// <remarks>
    /// <para>
    /// A column is added, nullable, under a name no column of the dataset ever had; dropped, unless it
    /// is part of the clustering key; renamed; its numbers widened within their kind, a signed
    /// integer to a wider signed one, an unsigned to a wider unsigned, a float to a wider float; or
    /// made nullable. Nothing else: a change that some value already written would not survive is
    /// refused before anything is committed.
    /// </para>
    /// <para>
    /// An object written before the change holds a new column as nulls, a renamed one under its old
    /// name and a widened one in its old type: it is read through the name the column had, and its
    /// values widened, batch by batch, until compaction rewrites it. A name dropped or renamed away is
    /// never used again, so that an object written with it never lends its values to another column.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentException">The new schema is not one the dataset's objects read as; the message names the column and why.</exception>
    /// <exception cref="InvalidOperationException">Another writer changed the schema first; refresh, and change the schema the dataset has now.</exception>
    public async ValueTask<ulong> EvolveSchemaAsync(
        VortexSchema schema, IReadOnlyDictionary<string, string>? renamed = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(schema);
        DatasetSnapshot version = Snapshot;
        DatasetSchema current = version.Schema;
        DType next = VortexTypes.ToDType(schema, new DTypeArena());
        byte[] bytes = DTypeProtobuf.Serialize(next);
        if (bytes.AsSpan().SequenceEqual(current.Bytes.Span))
        {
            // Already the dataset's: a writer retrying a change another one made first is done.
            return version.Version;
        }

        IReadOnlyList<RetiredColumn> retired = current.Evolve(next, renamed);
        CommitResult commit = await CommitAsync([new DatasetOperation.ChangeSchema(current.Bytes, bytes, retired)], cancellationToken)
            .ConfigureAwait(false);
        return commit.Outcomes[0] is OperationOutcome.Applied or OperationOutcome.AlreadyThere
            ? commit.Version
            : throw new InvalidOperationException(
                $"Another writer changed the dataset's schema after version {version.Version}, which this change was checked " +
                $"against; version {commit.Version} has another. Refresh, and change the schema the dataset has now.");
    }

    /// <summary>
    /// What compaction is due on this version and what it would cost, read from the leaf entries
    /// alone; the plan's job is null when nothing is over its bound.
    /// </summary>
    /// <param name="options">The sizes and bounds to plan against; null for the defaults, and the dataset's own settings win where it states them.</param>
    /// <param name="cancellationToken">Cancels the reads of the tree.</param>
    /// <returns>The plan.</returns>
    /// <exception cref="ArgumentException">The options ask for <see cref="CompactionStyle.Tiered"/> on a dataset with a clustering key.</exception>
    public ValueTask<CompactionPlan> PlanCompactionAsync(
        CompactionOptions? options = null, CancellationToken cancellationToken = default) =>
        CompactionPolicy.PlanAsync(this, options, cancellationToken);

    /// <summary>
    /// Runs the compaction that is due, if one is. One step only: a caller that wants the invariant
    /// restored loops until this returns null, rather than having one call rewrite gigabytes.
    /// </summary>
    /// <param name="options">The sizes and bounds to plan against; null for the defaults.</param>
    /// <param name="cancellationToken">Cancels the rewrite and the commit.</param>
    /// <returns>What the compaction did, or null when nothing was due.</returns>
    /// <exception cref="ArgumentException">The options ask for <see cref="CompactionStyle.Tiered"/> on a dataset with a clustering key.</exception>
    /// <exception cref="DatasetIntegrityException">A guard found that the compaction would lose rows; nothing was committed.</exception>
    public async ValueTask<CompactionResult?> CompactAsync(
        CompactionOptions? options = null, CancellationToken cancellationToken = default)
    {
        CompactionPlan plan = await PlanCompactionAsync(options, cancellationToken).ConfigureAwait(false);
        return plan.Job is { } job
            ? await DatasetCompactor.RunAsync(this, job, cancellationToken).ConfigureAwait(false)
            : null;
    }

    /// <summary>
    /// Runs compaction in the background until <paramref name="cancellationToken"/> is cancelled:
    /// refreshes, plans, runs the job due for this loop, paces what it wrote, and sleeps when nothing
    /// is due. Any host runs it, a hosted service, a worker, a console; the library starts no thread
    /// of its own.
    /// </summary>
    /// <param name="schedule">The options it plans against, its pace, and the loops it shares the dataset with; null for one loop, unpaced, asking again every minute.</param>
    /// <param name="cancellationToken">Stops the loop, between jobs or inside one.</param>
    /// <returns>
    /// A task that ends with <see cref="OperationCanceledException"/> once the token is cancelled, or
    /// with the exception a refresh, a plan or a job raised: a host that wants it to go on catches
    /// that and runs it again.
    /// </returns>
    /// <remarks>
    /// A job is the one <see cref="CompactAsync"/> runs, so a loop is safe against every writer and
    /// every other loop. Loops that know one another spread over the due jobs by
    /// <see cref="CompactionSchedule.Loops"/>; loops that cannot count one another lease a job's
    /// levels (<see cref="CompactionSchedule.Leases"/>).
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The schedule's loop index, rate, sleep or lease span is out of range.</exception>
    public Task RunCompactionAsync(CompactionSchedule? schedule = null, CancellationToken cancellationToken = default) =>
        CompactionLoop.RunAsync(this, schedule ?? new CompactionSchedule(), cancellationToken);

    /// <summary>
    /// Deletes what no version inside the retention window references. It marks from the store's
    /// latest version, not from this handle's: a handle on an older version outlives the window at
    /// its own risk, and is told so by the object it can no longer read.
    /// </summary>
    /// <param name="options">The clock and whether to delete; null for the defaults.</param>
    /// <param name="cancellationToken">Cancels the marking and the deletes.</param>
    /// <returns>What vacuum kept and deleted.</returns>
    public ValueTask<VacuumResult> VacuumAsync(VacuumOptions? options = null, CancellationToken cancellationToken = default) =>
        DatasetVacuum.RunAsync(_store, options, cancellationToken);

    /// <summary>
    /// Checks this version's pages, objects and fragments against what its references and entries
    /// promise, and names every problem rather than throwing.
    /// </summary>
    /// <param name="since">A version already verified, whose shared pages and entries are not checked again; null verifies everything.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>What was checked and every problem found.</returns>
    public ValueTask<DatasetVerification> VerifyAsync(ulong? since = null, CancellationToken cancellationToken = default) =>
        DatasetVerifier.VerifyAsync(_store, new VerifyOptions { Version = Version, Since = since }, cancellationToken);

    /// <summary>Closes the data objects the handle keeps open.</summary>
    /// <returns>A task that completes when every object is closed.</returns>
    public ValueTask DisposeAsync() => _objects.DisposeAsync();

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

        _ = ClusteringKey.Declared(options.ClusteringKey, schema);

        CommitHeader template = new CommitHeader
        {
            Version = 1,
            Seed = options.Seed,
            Schema = DTypeProtobuf.Serialize(schema),
            ClusteringKey = [.. options.ClusteringKey ?? []],
            Retention = options.Retention,
            Compaction = options.LockedStore && options.Compaction.Fanout == 0
                ? options.Compaction with { Fanout = DatasetOptions.LockedFanout }
                : options.Compaction,
            LockedStore = options.LockedStore,
            Chunker = new ChunkerSettings(
                ProllyBoundaryRule.DefaultMinBytes,
                ProllyBoundaryRule.DefaultTargetBytes,
                ProllyBoundaryRule.DefaultMaxBytes),
        };

        await DatasetCommitter.CommitAsync(store, [], Commit(options, template), cancellationToken).ConfigureAwait(false);
        return await OpenAsync(store, options, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Applies operations, moves this handle to the version they created, and returns it.</summary>
    internal async ValueTask<ulong> ApplyAsync(
        IReadOnlyList<DatasetOperation> operations, CancellationToken cancellationToken = default)
    {
        CommitResult result = await CommitAsync(operations, cancellationToken).ConfigureAwait(false);
        await CompactInlineAsync(cancellationToken).ConfigureAwait(false);
        return result.Version;
    }

    /// <summary>
    /// The compaction a commit that added to level 0 runs before it returns when the dataset asks for
    /// one (<see cref="DatasetOptions.InlineCompactionBytes"/>): level 0, once past its ceiling, merged
    /// into the level above when the job reads no more than the budget.
    /// </summary>
    internal async ValueTask CompactInlineAsync(CancellationToken cancellationToken)
    {
        long budget = _options.InlineCompactionBytes;
        if (budget <= 0 || Lag == 0 || Interlocked.CompareExchange(ref _compactingInline, 1, 0) != 0)
        {
            return;
        }

        try
        {
            CompactionPlan plan = await PlanCompactionAsync(null, cancellationToken).ConfigureAwait(false);
            if (plan.Job is { Trigger: CompactionTrigger.LevelZeroCeiling } job && job.Bytes <= budget)
            {
                await DatasetCompactor.RunAsync(this, job, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Cancelled by the caller, whose commit stands.
        }
        catch (Exception failed) when (failed is not OutOfMemoryException)
        {
            // The commit stands whatever befalls the compaction after it, and its outcome is what the
            // caller is told: level 0 stays as it is, for the next commit or another driver, which
            // meets the same failure again if it is not a passing one. The handle says what it was.
            Volatile.Write(ref _inlineFailure, failed);
        }
        finally
        {
            Volatile.Write(ref _compactingInline, 0);
        }
    }

    /// <summary>
    /// Applies operations, reports what the commit made of each of them, and moves this handle to the
    /// version it created, or to the one it found nothing to change in.
    /// </summary>
    internal async ValueTask<CommitResult> CommitAsync(
        IReadOnlyList<DatasetOperation> operations, CancellationToken cancellationToken)
    {
        DatasetSnapshot held = Snapshot;
        CommitResult result = await DatasetCommitter
            .CommitAsync(
                _store,
                operations,
                Commit(_options, held.Header) with
                {
                    Known = held.Version,
                    KnownAt = held.KnownAt,
                    TrustSpan = TrustSpan(held),
                    Held = held.Commit,
                    PageCache = _pageCache,
                },
                cancellationToken)
            .ConfigureAwait(false);

        // As the writer holds it: reading it back would cost a request per version probed, then the
        // read that opens it, for bytes this process wrote.
        MoveTo(result.Version, result.Commit, result.KnownAt);
        return result;
    }

    /// <summary>
    /// Moves what this dataset still references in <paramref name="versions"/>' commit objects into
    /// a new one, so that the next vacuum past the window can delete them; returns the version
    /// created and whether anything moved.
    /// </summary>
    /// <remarks>
    /// A metadata-only commit: no row is read or written, and the version's content hash does not
    /// change. A version still inside the window keeps its own commit object whatever moves out of it.
    /// </remarks>
    internal async ValueTask<(ulong Version, OperationOutcome Outcome)> RepackAsync(
        IReadOnlyList<ulong> versions, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(versions);
        CommitResult result = await CommitAsync([new DatasetOperation.Repack(versions)], cancellationToken).ConfigureAwait(false);
        return (result.Version, result.Outcomes[0]);
    }

    /// <summary>
    /// How many rows hold a key below <paramref name="key"/> on the clustering key; a null key is
    /// below nothing. Answered by the objects' entries wherever an object lies wholly inside the
    /// range, and by an exact count only of the objects the key cuts.
    /// </summary>
    /// <exception cref="InvalidOperationException">The dataset has no clustering key, or a composite one.</exception>
    internal ValueTask<long> RankAsync(FilterLiteral key, CancellationToken cancellationToken = default)
    {
        if (Key is not { IsComposite: false } clustering)
        {
            throw new InvalidOperationException(
                "A rank is on the clustering key, and this dataset declares " +
                (Key is null ? "none." : "a composite one: rank a tuple's leading column with a count instead."));
        }

        return ScanBuilder()
            .Where(Expr.Lt(Expr.Field(clustering.Paths[0]), Expr.Literal(key)))
            .CountAsync(cancellationToken);
    }

    /// <summary>The engine's scan over every object of the version this handle holds now.</summary>
    internal DatasetScanBuilder ScanBuilder() => new DatasetScanBuilder(this, Snapshot);

    /// <summary>
    /// The objects inside <c>[from, to)</c> of the version this handle holds whose summaries, and
    /// their ancestors', do not refute the pruner, in key order. A null pruner keeps every object.
    /// </summary>
    internal IAsyncEnumerable<PositionedObject> WalkAsync(
        SummaryPruner? pruner, long from, long to, DatasetScanCounters? metrics, CancellationToken cancellationToken) =>
        Snapshot.WalkAsync(pruner, from, to, metrics, cancellationToken);

    /// <summary>
    /// What orders an object in its tree: its tree key without the uid that ends it, which is the
    /// exact row-encoded minimum of the clustering key, or a position when the dataset declares none.
    /// </summary>
    internal static ReadOnlyMemory<byte> OrderOf(ReadOnlyMemory<byte> treeKey) => treeKey[..^UidBytes];

    /// <summary>The position an unclustered object's tree key orders it by.</summary>
    internal static long PositionAt(ReadOnlySpan<byte> treeKey) => BinaryPrimitives.ReadInt64BigEndian(treeKey);

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

        // Read out of the chunks the sink still holds, before the put: the entry's bounds and the
        // smallest key its leaf is ordered by then cost no request at all.
        (ObjectSummaries summaries, byte[] prefix) =
            await DescribeAsync(draft.Sink.Content(), firstRow, draft.BySortedColumn, cancellationToken).ConfigureAwait(false);
        if (await draft.Sink.CommitAsync(cancellationToken).ConfigureAwait(false) != PutOutcome.Created)
        {
            throw new ObjectStoreException($"'{draft.Key}' was taken, which a fresh uid cannot be.");
        }

        ObjectEntry entry = new ObjectEntry(draft.Key, Uid(draft.Identity), rows, bytes, hash, summaries);
        return new WrittenObject(entry, KeyOf(prefix, entry));
    }

    /// <summary>
    /// Where the rows of a new object go when the dataset is ordered by arrival: after every
    /// object's. That is the row count until an object is removed, and past the last object's rows
    /// after, since a removal leaves the others' positions where they were.
    /// </summary>
    internal async ValueTask<long> EndAsync(CancellationToken cancellationToken)
    {
        DatasetSnapshot version = Snapshot;
        long end = version.RowCount;
        if (Key is not null)
        {
            return end;
        }

        foreach ((int _, DatasetTree tree) in version.Levels.Occupied())
        {
            PageReference at = tree.Root;
            for (int depth = tree.Depth; depth > 1; depth--)
            {
                ReadOnlyMemory<byte> node = await version.Pages.ReadPageAsync(at, cancellationToken).ConfigureAwait(false);
                IReadOnlyList<InternalEntry> children = TreePage.ReadInternal(node);
                at = children[^1].Child;
            }

            IReadOnlyList<TreeEntry> leaves = TreePage.ReadLeaf(await version.Pages.ReadPageAsync(at, cancellationToken).ConfigureAwait(false));
            TreeEntry last = leaves[^1];
            end = Math.Max(end, PositionAt(last.Key.Span) + last.Rows);
        }

        return end;
    }

    /// <summary>What a change reports when its filter can match no row: nothing read, nothing committed.</summary>
    private RowChangeResult Unchanged() => new RowChangeResult { Version = Version, Outcome = OperationOutcome.AlreadyThere };

    /// <summary>
    /// A change's filter, its literals checked against the schema before any object is read; null,
    /// which takes every row, as it is.
    /// </summary>
    private VortexExpr? Checked(VortexExpr? filter)
    {
        if (filter is not null)
        {
            FilterTypeCheck.Check(DType, filter, nameof(filter));
        }

        return filter;
    }

    /// <summary>A file's identity, or zero when it has none.</summary>
    internal static UInt128 Identity(VortexFile file) =>
        file.StoredIdentity is { } identity ? Uid(identity) : UInt128.Zero;

    /// <summary>
    /// Moves this handle to <paramref name="version"/>, known to be the latest at
    /// <paramref name="knownAt"/>, unless a commit or a refresh running beside this one has already
    /// moved it there or past it: a handle never goes back.
    /// </summary>
    private void MoveTo(ulong version, CommitObject commit, DateTimeOffset knownAt)
    {
        DatasetSnapshot? next = null;
        while (true)
        {
            DatasetSnapshot current = Volatile.Read(ref _snapshot);
            if (current.Version >= version)
            {
                if (current.Version == version)
                {
                    current.Confirm(knownAt);
                }

                return;
            }

            next ??= new DatasetSnapshot(commit, PagesOf(version, commit), _objects, knownAt, current.Schema);
            if (ReferenceEquals(Interlocked.CompareExchange(ref _snapshot, next, current), current))
            {
                return;
            }
        }
    }

    /// <summary>
    /// How long after a version was known to be the latest every version after it is surely still in
    /// the store: half the window its header retains superseded versions for, the other half left
    /// to the clocks' disagreement.
    /// </summary>
    private static TimeSpan TrustSpan(DatasetSnapshot version) => DatasetVacuum.WindowOf(version.Header.Retention) / 2;

    /// <summary>
    /// A page source for one version, holding what the read that opened it brought back, and reading
    /// through the pages this handle kept from the versions before.
    /// </summary>
    private CommitPageSource PagesOf(ulong version, CommitObject commit)
    {
        CommitPageSource pages = new CommitPageSource(_store, _pageCache) { Reading = version };
        pages.Open(version, commit);
        return pages;
    }

    /// <summary>
    /// Completes a caller's draft and puts it in the store, keyed after <paramref name="firstRow"/>
    /// when the dataset is ordered by arrival.
    /// </summary>
    private async ValueTask<WrittenObject?> CompleteAsync(ObjectDraft draft, long firstRow, CancellationToken cancellationToken)
    {
        if (!ReferenceEquals(draft.Owner, this))
        {
            throw new InvalidOperationException($"The draft of '{draft.Key}' was started by another dataset handle.");
        }

        draft.Take();
        if (draft.Writer.IsAbandoned)
        {
            draft.Sink.Discard();
            throw new InvalidOperationException(
                $"The writer of '{draft.Key}' did not complete a file, having been abandoned; nothing was put.");
        }

        try
        {
            // Completed here unless the caller already did; the rows it holds are known once the
            // last chunk is out.
            if (!draft.Writer.IsFinished)
            {
                await draft.Writer.CompleteAsync(cancellationToken).ConfigureAwait(false);
            }

            await draft.Writer.DisposeAsync().ConfigureAwait(false);
            return await SealAsync(draft, draft.Writer.RowCount, firstRow, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            draft.Sink.Discard();
            throw;
        }
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
    /// The two things a leaf entry needs, read out of the file as the sink still holds it, before it
    /// has reached the store: the object's bounds and the key its leaf sits at. Only the ranges the
    /// open and the first key ask for are read, where they lie.
    /// </summary>
    private async ValueTask<(ObjectSummaries Summaries, byte[] Prefix)> DescribeAsync(
        ISegmentSource bytes, long firstRow, bool bySortedColumn, CancellationToken cancellationToken)
    {
        SourceReader source = new SourceReader(bytes, ownsSource: true, _options.Session.Options.EnginePool);
        VortexFile file = await VortexFile
            .OpenAsync(source, ObjectCache.OpenOptions, cancellationToken).ConfigureAwait(false);
        await using (file.ConfigureAwait(false))
        {
            if (bySortedColumn)
            {
                await CheckSortedAsync(file, cancellationToken).ConfigureAwait(false);
            }

            ObjectSummaries summaries = Summaries(file);
            return (summaries, await PrefixAsync(file, summaries, firstRow, cancellationToken)
                .ConfigureAwait(false));
        }
    }

    /// <summary>
    /// Refuses an object written without a run whose key column does not walk by itself: its rows
    /// were to come in key order, and a key cursor that found no source in it would take it for an
    /// object without the key, whose rows no walk delivers.
    /// </summary>
    private async ValueTask CheckSortedAsync(VortexFile file, CancellationToken cancellationToken)
    {
        ClusteringKey clustering = Key!;
        Keys.KeyCursor? cursor = await clustering.TryOpenAsync(file, indexes: false, cancellationToken).ConfigureAwait(false);
        if (cursor is null)
        {
            throw new DatasetIntegrityException(
                $"An object written in key order carries no run on '{clustering.Paths[0]}', and its statistics do not say " +
                "the column is sorted: a rewrite that loses the order of its rows is the one thing it must not do.");
        }

        await cursor.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>What orders an object's leaf: its smallest key, or its position.</summary>
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
            InlineVectorBytes = options.InlineVectorBytes,
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
    /// An object's order when the dataset declares no clustering key: a position. Big-endian, so
    /// that the <c>memcmp</c> order the tree compares by is numeric order.
    /// </summary>
    private static byte[] PositionOf(long row)
    {
        byte[] prefix = new byte[sizeof(long)];
        BinaryPrimitives.WriteInt64BigEndian(prefix, row);
        return prefix;
    }

    private static UInt128 Uid(Guid identity)
    {
        Span<byte> bytes = stackalloc byte[16];
        identity.TryWriteBytes(bytes);
        return ((UInt128)BinaryPrimitives.ReadUInt64LittleEndian(bytes) << 64)
            | BinaryPrimitives.ReadUInt64LittleEndian(bytes[8..]);
    }
}
