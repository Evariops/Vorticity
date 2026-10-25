using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Collections.Immutable;
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
internal readonly record struct WrittenObject(ObjectEntry Entry, ReadOnlyMemory<byte> Key, long DataBytes = 0);

/// <summary>
/// A versioned dataset over an object store: immutable Vortex files as its data objects, one commit
/// object per version, and a tree of the objects with a summary per node, so that neither reading
/// nor committing costs in proportion to the objects.
/// </summary>
/// <remarks>
/// A handle is pinned to one version: it reads that version until it commits, which moves it to the
/// version its commit created, or refreshes. Commits are conditional creations, so concurrent
/// writers need no lock: the one that loses re-applies its changes to the winner's version.
/// A handle is thread-safe for reads; its commits are serialised by the store. On a store that does
/// not declare <see cref="ObjectStoreCapabilities.CreatesConditionally"/>, a handle reads, and every
/// write throws <see cref="NotSupportedException"/> before it writes anything.
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

    // The store the handle made for a local directory it was given by location, which it disposes.
    private IObjectStore? _ownedStore;

    // The location the handle was opened at, as its caller named it, which tags the activities of its
    // commits, compactions and vacuums; null for a handle given a store.
    private string? _location;

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
    /// The last version whose commit changed rows, as the version this handle reads records it: an
    /// append, an import, a delete, an update, a replacement of objects or a change of schema. A
    /// compaction, an index fragment, a repack or a rekey creates a version without moving it, so a
    /// result worked out at a version at or after it still holds.
    /// </summary>
    /// <remarks>
    /// A version written by a library that predates the field records nothing, and reads as that
    /// version itself until a commit that knows the field changes rows: no later change, which is all
    /// that can be said.
    /// </remarks>
    public ulong LastDataChange => DatasetCommitter.LastDataChangeOf(Snapshot.Header, Snapshot.Version);

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
    public IReadOnlyList<string> ClusteringKey => Snapshot.Header.ClusteringKey;

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

    /// <summary>The location it was opened at, as its caller named it; null for a handle given a store.</summary>
    internal string? Location => _location;

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
    /// <remarks>
    /// Two requests: a listing that finds no version, and the conditional creation of the first. A
    /// creation is exclusive: of several that race for one store, one creates the dataset and every
    /// other throws, rather than handing its caller a dataset of another's schema and settings.
    /// </remarks>
    /// <exception cref="ObjectStoreException">The store already holds a dataset, or another creation made it first.</exception>
    /// <exception cref="ArgumentException">A clustering key column is not in the schema, or may hold a null.</exception>
    /// <exception cref="NotSupportedException">
    /// The store's listing is not in ordinal order (<see cref="ObjectStoreCapabilities.ListsInOrder"/>), the store does
    /// not create a key only while it is free (<see cref="ObjectStoreCapabilities.CreatesConditionally"/>), or the
    /// dataset is created for a locked store and the store keeps no locks (<see cref="ObjectStoreCapabilities.KeepsLocks"/>).
    /// </exception>
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
    /// Creates a dataset at <paramref name="location"/>: under a location of a store the options'
    /// session holds, <c>s3://bucket/tables/readings/</c>, or in a local directory.
    /// </summary>
    /// <param name="location">A location, whose keys the dataset's are relative to, or a local directory.</param>
    /// <param name="schema">The columns every object of the dataset holds.</param>
    /// <param name="options">The clustering key, the retention, how objects are written and the session; null for the defaults.</param>
    /// <param name="cancellationToken">Cancels the requests.</param>
    /// <returns>The handle, on version 1; the caller disposes it.</returns>
    /// <exception cref="InvalidOperationException">No store of the session serves the location.</exception>
    /// <exception cref="ObjectStoreException">The location already holds a dataset, or another creation made it first.</exception>
    public static ValueTask<VortexDataset> CreateAsync(
        string location, VortexSchema schema, DatasetOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(schema);
        options ??= new DatasetOptions();
        (IObjectStore store, IObjectStore? owned) = StoreAt(location, options);
        return OwningAsync(CreateAtAsync(store, VortexTypes.ToDType(schema, new DTypeArena()), options, location, cancellationToken), owned, location);
    }

    /// <summary>
    /// Opens the latest version of the dataset at <paramref name="location"/>: under a location of a store
    /// the options' session holds, or in a local directory.
    /// </summary>
    /// <param name="location">A location, whose keys the dataset's are relative to, or a local directory.</param>
    /// <param name="options">How this handle reads and writes, and its session; null for the defaults.</param>
    /// <param name="cancellationToken">Cancels the requests.</param>
    /// <returns>The handle; the caller disposes it.</returns>
    /// <exception cref="InvalidOperationException">No store of the session serves the location.</exception>
    /// <exception cref="ObjectNotFoundException">The location holds no dataset.</exception>
    public static ValueTask<VortexDataset> OpenAsync(string location, DatasetOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new DatasetOptions();
        (IObjectStore store, IObjectStore? owned) = StoreAt(location, options);
        return OwningAsync(OpenAsync(store, options, cancellationToken), owned, location);
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
    /// <exception cref="NotSupportedException">The store's listing is not in ordinal order (<see cref="ObjectStoreCapabilities.ListsInOrder"/>).</exception>
    public static async ValueTask<VortexDataset> OpenAsync(
        IObjectStore store, DatasetOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        RequireOrderedListing(store);
        options ??= new DatasetOptions();
        DateTimeOffset asked = options.TimeProvider.GetUtcNow();
        store = SealedObjectStore.Over(store, options.Session);
        try
        {
            (ulong version, CommitObject? commit) = await DatasetCommitter
                .LatestAsync(store, cancellationToken).ConfigureAwait(false);
            if (commit is null)
            {
                throw ObjectNotFoundException.For(CommitKey.Prefix);
            }

            RequireEncryption(store, options);
            return new VortexDataset(store, options.For(commit.Header), version, commit, asked);
        }
        catch
        {
            if (store is SealedObjectStore sealedStore)
            {
                await sealedStore.DisposeAsync().ConfigureAwait(false);
            }

            throw;
        }
    }

    /// <summary>
    /// Opens <paramref name="version"/> of the dataset at <paramref name="location"/>: under a location
    /// of a store the options' session holds, or in a local directory.
    /// </summary>
    /// <param name="location">A location, whose keys the dataset's are relative to, or a local directory.</param>
    /// <param name="version">The version to read, one the dataset has created.</param>
    /// <param name="options">How this handle reads and writes, and its session; null for the defaults.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The handle, pinned to that version until it commits or refreshes; the caller disposes it.</returns>
    /// <exception cref="InvalidOperationException">No store of the session serves the location.</exception>
    /// <exception cref="ObjectNotFoundException">The dataset has no such version: it never created it, or vacuum took it.</exception>
    public static ValueTask<VortexDataset> OpenAsync(
        string location, ulong version, DatasetOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new DatasetOptions();
        (IObjectStore store, IObjectStore? owned) = StoreAt(location, options);
        return OwningAsync(OpenAsync(store, version, options, cancellationToken), owned, location);
    }

    /// <summary>
    /// Opens one version of a dataset, known by its number, in one request: the version's commit
    /// object names its own key, so no listing has to find it.
    /// </summary>
    /// <param name="store">The store holding the dataset.</param>
    /// <param name="version">The version to read, one the dataset has created.</param>
    /// <param name="options">How this handle reads and writes; the settings fixed at creation come from the dataset itself.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The handle, pinned to that version until it commits or refreshes; the caller disposes it.</returns>
    /// <remarks>
    /// The version may not be the latest, and the handle does not take it to be: its first commit or
    /// refresh looks for the latest version with a listing. A commit from it applies its changes to the
    /// latest version, as any commit's rebase does, whatever versions came after this one. A version
    /// superseded for longer than the retention window may lose its objects to vacuum while it is
    /// read, and its reads then throw <see cref="ObjectNotFoundException"/>, never a partial answer.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="version"/> is 0, which no dataset creates.</exception>
    /// <exception cref="ObjectNotFoundException">The dataset has no such version: it never created it, or vacuum took it.</exception>
    /// <exception cref="TornCommitException">The version's commit is not whole; <see cref="RemoveTornCommitAsync"/> removes it.</exception>
    /// <exception cref="NotSupportedException">The store's listing is not in ordinal order (<see cref="ObjectStoreCapabilities.ListsInOrder"/>).</exception>
    public static async ValueTask<VortexDataset> OpenAsync(
        IObjectStore store, ulong version, DatasetOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentOutOfRangeException.ThrowIfZero(version);
        RequireOrderedListing(store);
        options ??= new DatasetOptions();
        store = SealedObjectStore.Over(store, options.Session);
        try
        {
            CommitObject commit;
            try
            {
                commit = await DatasetCommitter.OpenAsync(store, version, cancellationToken).ConfigureAwait(false);
            }
            catch (ObjectNotFoundException missing)
            {
                throw new ObjectNotFoundException(
                    $"The dataset has no version {version}: it never created it, or vacuum took it once it was past the retention window.",
                    missing)
                {
                    Key = missing.Key,
                };
            }

            RequireEncryption(store, options);

            // Not known to be the latest: the handle's first commit or refresh lists the commits.
            return new VortexDataset(store, options.For(commit.Header), version, commit, DateTimeOffset.MinValue);
        }
        catch
        {
            if (store is SealedObjectStore sealedStore)
            {
                await sealedStore.DisposeAsync().ConfigureAwait(false);
            }

            throw;
        }
    }

    /// <summary>
    /// The identifiers as a commit keeps them: a top-level column of <paramref name="schema"/>, or a key
    /// of one of its variant columns read as a string or an integer, <c>Attributes['user'].AsString()</c>,
    /// written as a plan writes it. Refuses any other, and one named twice.
    /// </summary>
    private static string[] RequireIdentifiers(IReadOnlyList<string> identifiers, DType schema, string parameter)
    {
        HashSet<string> seen = new(StringComparer.Ordinal);
        string[] kept = new string[identifiers.Count];
        for (int i = 0; i < identifiers.Count; i++)
        {
            string identifier = identifiers[i];
            string? name = identifier is null || schema.Kind != DTypeKind.Struct
                ? null
                : schema.IndexOfField(identifier) >= 0 ? identifier : IdentifierPath(identifier, schema)?.Text;
            if (name is null)
            {
                throw new ArgumentException(
                    $"The identifier '{identifier}' names no column of the dataset's schema, nor a key of a variant column read as a string or an integer, such as Attributes['user'].AsString().",
                    parameter);
            }

            if (!seen.Add(name))
            {
                throw new ArgumentException($"The identifier '{identifier}' is named twice.", parameter);
            }

            kept[i] = name;
        }

        return kept;
    }

    /// <summary>
    /// The shredded paths as a commit keeps them (<see cref="DatasetOptions.ShreddedPaths"/>): a key of a variant column
    /// of <paramref name="schema"/> read as a string, an integer, a double or a boolean, written as a plan writes it.
    /// Refuses any other, and a key named twice, in one class or two.
    /// </summary>
    private static string[] RequireShreddedPaths(IReadOnlyList<string> paths, DType schema, string parameter)
    {
        HashSet<(int, string)> seen = [];
        string[] kept = new string[paths.Count];
        for (int i = 0; i < paths.Count; i++)
        {
            if (paths[i] is not { } text || schema.Kind != DTypeKind.Struct || ShreddedPath(text, schema) is not { } path)
            {
                throw new ArgumentException(
                    $"The shredded path '{paths[i]}' names no key of a variant column of the dataset's schema read as a string, an integer, a double or a boolean, such as Attributes['status'].AsInt64().",
                    parameter);
            }

            if (!seen.Add((path.Field, path.Key)))
            {
                throw new ArgumentException($"The key of the shredded path '{text}' is named twice.", parameter);
            }

            kept[i] = path.Text;
        }

        return kept;
    }

    /// <summary>
    /// The key of a variant column a shredded path names, and the class it is kept in, for a path written
    /// <c>Attributes['status'].AsInt64()</c>, <c>.AsString()</c>, <c>.AsDouble()</c> or <c>.AsBool()</c>; null for any
    /// other. <c>Text</c> is the path as a plan writes it.
    /// </summary>
    internal static (int Field, string Key, Types.Variants.VariantClass Class, string Text)? ShreddedPath(string path, DType schema)
    {
        if (Expressions.ExprText.LastPath(path) is not ([string column], [{ Key: { } key }], { } read))
        {
            return null;
        }

        Types.Variants.VariantClass? @class = read switch
        {
            Compute.VariantRead.AsString => Types.Variants.VariantClass.String,
            Compute.VariantRead.AsInt64 => Types.Variants.VariantClass.Integer,
            Compute.VariantRead.AsDouble => Types.Variants.VariantClass.Double,
            Compute.VariantRead.AsBool => Types.Variants.VariantClass.Boolean,
            _ => null,
        };
        int field = schema.IndexOfField(column);
        if (@class is null || field < 0 || schema.GetField(field).Kind != DTypeKind.Variant)
        {
            return null;
        }

        string text = new Compute.VariantFieldExpr(new Expressions.ColumnFieldExpr(column), [Compute.VariantStep.Of(key)], read).Text;
        return (field, key, @class.Value, text);
    }

    /// <summary>
    /// The key of a variant column an identifier names, and the class its values are read in, for an
    /// identifier written <c>Attributes['user'].AsString()</c> or <c>.AsInt64()</c>; null for any other,
    /// a column's name among them. <c>Text</c> is the path as a plan writes it, the name a write
    /// policy gives the key's index.
    /// </summary>
    internal static (int Field, string Key, Types.Variants.VariantClass Class, string Text)? IdentifierPath(string identifier, DType schema)
    {
        if (Expressions.ExprText.LastPath(identifier) is not ([string column], [{ Key: { } key }], { } read)
            || read is not (Compute.VariantRead.AsString or Compute.VariantRead.AsInt64))
        {
            return null;
        }

        int field = schema.IndexOfField(column);
        if (field < 0 || schema.GetField(field).Kind != DTypeKind.Variant)
        {
            return null;
        }

        string text = new Compute.VariantFieldExpr(new Expressions.ColumnFieldExpr(column), [Compute.VariantStep.Of(key)], read).Text;
        Types.Variants.VariantClass @class = read == Compute.VariantRead.AsString ? Types.Variants.VariantClass.String : Types.Variants.VariantClass.Integer;
        return (field, key, @class, text);
    }

    /// <summary>
    /// Refuses a store whose listing is not in ordinal order, as an S3 directory bucket's is not: the
    /// latest version is the first key of <c>commit/</c>, which such a listing does not give.
    /// </summary>
    private static void RequireOrderedListing(IObjectStore store)
    {
        if ((store.Capabilities & ObjectStoreCapabilities.ListsInOrder) == 0)
        {
            throw new NotSupportedException(
                $"A dataset needs a store whose listing is in ordinal order and resumes after a key, and {store.GetType().Name} " +
                "does not declare one (ObjectStoreCapabilities.ListsInOrder): the latest version is the first key of commit/.");
        }
    }

    /// <summary>
    /// Refuses to write to a store that cannot create a key only while it is free: a commit is that
    /// creation, and on such a store two writers of one version would both believe they had committed,
    /// the second's commit object over the first's. A reader needs no such creation, so a handle on
    /// the store reads and refuses only what writes, before it writes anything.
    /// </summary>
    private static void RequireConditionalCreation(IObjectStore store)
    {
        if ((store.Capabilities & ObjectStoreCapabilities.CreatesConditionally) == 0)
        {
            string name = (store is SealedObjectStore sealedStore ? sealedStore.Inner : store).GetType().Name;
            throw new NotSupportedException(
                $"A dataset commits by creating a key only while it is free, and {name} does not declare that it can " +
                "(ObjectStoreCapabilities.CreatesConditionally). Run the store contract tests against the service, many times " +
                "over, then declare it: on an S3 store, set S3StoreOptions.CreatesConditionally.");
        }
    }

    /// <summary>
    /// Refuses a plain dataset to a handle that asks for an encrypted one, or whose session refuses
    /// plaintext: a reader that expects sealed data is not handed plain data in its place.
    /// </summary>
    private static void RequireEncryption(IObjectStore store, DatasetOptions options)
    {
        bool sealedData = store is SealedObjectStore { Seals: true };
        if (!sealedData && (options.Encrypted || options.Session.Options.RefusePlaintext))
        {
            throw Sealing.SealedFiles.RefusedPlain(options.Encrypted ? "The dataset, opened as encrypted," : "The dataset");
        }
    }

    /// <summary>
    /// Changes the data key the dataset is sealed under: one commit that draws a new data key and is
    /// sealed under it, after which every writer seals under the new key. The objects sealed before
    /// keep the key they were sealed with until a compaction rewrites them, so a full rekey is this,
    /// a compaction of every object, and a vacuum.
    /// </summary>
    /// <param name="cancellationToken">Cancels the key's generation and the commit.</param>
    /// <returns>The version the rekeying commit created.</returns>
    /// <exception cref="InvalidOperationException">The dataset is not encrypted.</exception>
    public async ValueTask<ulong> RekeyAsync(CancellationToken cancellationToken = default)
    {
        if (_store is not SealedObjectStore { Seals: true } sealedStore)
        {
            throw new InvalidOperationException("The dataset is not encrypted, so it has no data key to change.");
        }

        RequireConditionalCreation(_store);
        await sealedStore.RekeyAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            CommitResult result = await CommitAsync([new DatasetOperation.Rekey()], CommitKind.Rekey, cancellationToken).ConfigureAwait(false);
            return result.Version;
        }
        finally
        {
            sealedStore.AbandonRekey();
        }
    }

    /// <summary>Whether the dataset is encrypted: its commit objects, and so every object of it, sealed.</summary>
    public bool IsEncrypted => _store is SealedObjectStore { Seals: true };

    /// <summary>Moves this handle to the latest version.</summary>
    /// <param name="cancellationToken">Cancels the requests.</param>
    /// <returns>
    /// The version the handle now reads: one that was the latest at some moment during the call,
    /// since another writer may commit the next just after.
    /// </returns>
    /// <remarks>
    /// A handle that knew its version to be the latest less than half the retention window ago reads
    /// the next version while it asks whether the one after exists, both at once: a refresh that finds
    /// no commit or one takes a round trip, and one that finds more goes on from the last it saw. Past
    /// that half window, or on a handle opened at a version given by number, it lists the commits.
    /// </remarks>
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
    /// taken for a torn one. An encrypted dataset's commit is checked by its envelope, which needs no
    /// key: it is whole when it ends with the trailer its writer writes last.
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
    /// Asks <paramref name="question"/> over the values of the clustering key's first column from <paramref name="from"/>
    /// up to <paramref name="to"/> part by part, a part a clustering value (<see cref="ClusteringOptions.Value"/>), the last
    /// part first unless <paramref name="descending"/> is false: each part's answer delivered once it and every part before
    /// it are in, with the share of the range they cover (<see cref="ValuePart{TValue, TAnswer}.Covered"/>).
    /// </summary>
    /// <typeparam name="TValue">The values of the key's first column: an instant, a date, an integer.</typeparam>
    /// <typeparam name="TAnswer">What the question answers.</typeparam>
    /// <param name="from">The range's first value.</param>
    /// <param name="to">The value past its last.</param>
    /// <param name="question">
    /// The question, asked with each part's bounds, the first in the part and the second past it, which it applies to its
    /// own filter: <c>(from, to, ct) =&gt; dataset.Scan&lt;Request&gt;().Where(r =&gt; r.At &gt;= from &amp; r.At &lt; to)…</c>.
    /// </param>
    /// <param name="descending">Whether the last part comes first, as a page that shows the newest of its values does; true by default.</param>
    /// <param name="cancellationToken">Cancels the questions in flight.</param>
    /// <returns>The parts, in the order asked, each with its bounds, its answer and the share of the range covered so far.</returns>
    /// <exception cref="InvalidOperationException">
    /// The dataset declares no clustering value, or one of a column whose next value no function tells, a text's or a
    /// float's.
    /// </exception>
    /// <exception cref="ArgumentException">The range holds no value the column stores.</exception>
    /// <remarks>
    /// A part's answer is final for its values, so a page that shows a range part by part shows the first part asked while
    /// the rest is read. Every object above level 0 holds rows of one clustering value, so a part's question reads whole
    /// objects but at the range's ends. The parts are asked several at once, sixteen at most and no more than the session
    /// lets reads be in flight. Answers over parts add up for counts and sums, and their extremes are the range's, but a
    /// mean over the range is its sum over its count.
    /// </remarks>
    public IAsyncEnumerable<ValuePart<TValue, TAnswer>> AskByValuesAsync<TValue, TAnswer>(
        TValue from, TValue to, Func<TValue, TValue, CancellationToken, ValueTask<TAnswer>> question, bool descending = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(question);
        return WindowParts.AskAsync(
            ValueParts.Of(Snapshot.Schema, Session.Options.Extensions, from, to, descending),
            Math.Min(Session.Options.MaxConcurrentReads, WindowParts.Width),
            question,
            static (lower, upper, covered, answer) => new ValuePart<TValue, TAnswer>(lower, upper, answer) { Covered = covered },
            cancellationToken);
    }

    /// <summary>
    /// Reads into the session's <see cref="VortexSessionOptions.SynopsisCache"/>, before a question needs
    /// them, the directories of every object whose summaries say it may hold a row
    /// <paramref name="filter"/> selects: <c>dataset.WarmAsync&lt;Request&gt;(r =&gt; r.At &gt;= DateTime.UtcNow.AddDays(-30))</c>.
    /// </summary>
    /// <typeparam name="TRecord">The record the filter is written against.</typeparam>
    /// <param name="filter">A lambda over the record's columns.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>The bytes read from the store into the cache; zero without a cache, or when it held them all.</returns>
    /// <remarks>
    /// Each object's regions are read in one request set, the objects side by side as many as the session
    /// lets reads be in flight, and the regions the cache holds are not read again. Of a sealed object, the
    /// cache keeps the trailer and the frames that hold the regions, the bytes the store holds.
    /// </remarks>
    public ValueTask<long> WarmAsync<TRecord>(Func<Probe<TRecord>, Predicate> filter, CancellationToken cancellationToken = default)
        where TRecord : IVortexRecord<TRecord>
    {
        ArgumentNullException.ThrowIfNull(filter);
        Predicate predicate = filter(new Probe<TRecord>(RecordBinding.For<TRecord>(Schema, Session.Options.Extensions)));
        return predicate.IsNone ? ValueTask.FromResult(0L) : WarmCoreAsync(Checked(predicate.Node), cancellationToken);
    }

    /// <summary>
    /// As <see cref="WarmAsync{TRecord}(Func{Probe{TRecord}, Predicate}, CancellationToken)"/>, for a caller
    /// without a record type.
    /// </summary>
    /// <param name="filter">The filter, from <see cref="VortexExpr.Parse"/> or combined with operators.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>The bytes read from the store into the cache.</returns>
    /// <exception cref="VortexSchemaException">The filter names a column the dataset does not have, or compares one with a literal of a type it cannot compare to.</exception>
    public ValueTask<long> WarmAsync(VortexExpr filter, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        return WarmCoreAsync(Checked(ToolPaths.Check(Schema, filter)), cancellationToken);
    }

    private async ValueTask<long> WarmCoreAsync(VortexExpr? filter, CancellationToken cancellationToken)
    {
        if (Session.Options.SynopsisCache is not { } cache)
        {
            return 0;
        }

        List<Task<long>> reads = [];
        await foreach (PositionedObject held in Snapshot.WalkAsync(filter is null ? null : new SummaryPruner(filter), 0, long.MaxValue, null, cancellationToken)
            .ConfigureAwait(false))
        {
            if (held.Entry.Contents is { } contents)
            {
                reads.Add(WarmObjectAsync(cache, held.Entry, contents, cancellationToken));
            }
        }

        long bytes = 0;
        foreach (long read in await Task.WhenAll(reads).ConfigureAwait(false))
        {
            bytes += read;
        }

        return bytes;
    }

    /// <summary>Reads through the cache the regions of one object it does not hold yet: the bytes it read.</summary>
    private async Task<long> WarmObjectAsync(SynopsisCache cache, ObjectEntry entry, Indexes.ObjectContents contents, CancellationToken cancellationToken)
    {
        if (_store is SealedObjectStore { Seals: true })
        {
            return await WarmSealedAsync(cache, entry, contents, cancellationToken).ConfigureAwait(false);
        }

        List<Indexes.IndexSegment> missing = contents.Declared();
        missing.RemoveAll(region => cache.Holds(entry.Uid, region));
        if (missing.Count == 0 || entry.Uid == UInt128.Zero)
        {
            return 0;
        }

        ISegmentReader source = await OpenFooterlessAsync(entry, contents, cancellationToken).ConfigureAwait(false);
        await using (source.ConfigureAwait(false))
        {
            ISegmentReader reader = Footerless(source, entry, contents);
            long bytes = 0;
            using SegmentRequestSet requests = new SegmentRequestSet(missing.Count);
            foreach (Indexes.IndexSegment region in missing)
            {
                requests.Add(new Serialization.Schemas.SegmentSpec(region.Offset, region.Length, region.AlignmentExponent, 0, 0));
                bytes += region.Length;
            }

            await reader.ReadManyAsync(requests, cancellationToken).ConfigureAwait(false);
            return bytes;
        }
    }

    /// <summary>
    /// Reads the regions of one sealed object through a reader that keeps their frames, those the cache keeps
    /// from the disk, the rest from the store: the bytes it kept, its trailer and its frames as the store holds them.
    /// </summary>
    private async Task<long> WarmSealedAsync(SynopsisCache cache, ObjectEntry entry, Indexes.ObjectContents contents, CancellationToken cancellationToken)
    {
        if (SealedSynopsisCache.Over(cache, entry.Uid, contents) is not { } kept)
        {
            return 0;
        }

        ISegmentReader source = await SealedObjectStore.OpenDataAsync(_store, entry.Key, entry.Uid, entry.Bytes, cancellationToken, kept).ConfigureAwait(false);
        await using (source.ConfigureAwait(false))
        {
            ISegmentReader reader = Footerless(source, entry, contents);
            List<Indexes.IndexSegment> regions = contents.Declared();
            using SegmentRequestSet requests = new SegmentRequestSet(regions.Count);
            foreach (Indexes.IndexSegment region in regions)
            {
                requests.Add(new Serialization.Schemas.SegmentSpec(region.Offset, region.Length, region.AlignmentExponent, 0, 0));
            }

            await reader.ReadManyAsync(requests, cancellationToken).ConfigureAwait(false);
            return kept.AdmittedBytes;
        }
    }

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
    public ObjectDraft StartObject()
    {
        DatasetSchema schema = Snapshot.Schema;
        return StartObjectUnder(schema, shredded: _options.ShredOnAppend ? DeclaredShredding(schema) : null);
    }

    /// <summary>
    /// The shredding of the <see cref="DatasetOptions.ShreddedPaths"/> by variant column, each key in its class, which an
    /// append writes when <see cref="DatasetOptions.ShredOnAppend"/> says so; null for none.
    /// </summary>
    private static Dictionary<string, Types.Variants.VariantShredding>? DeclaredShredding(DatasetSchema schema)
    {
        Dictionary<string, List<(string, Types.Variants.VariantShredding)>>? keys = null;
        foreach (string declared in schema.ShreddedPaths)
        {
            if (ShreddedPath(declared, schema.DType) is { } path)
            {
                string column = schema.DType.GetFieldName(path.Field);
                if (!(keys ??= new(StringComparer.Ordinal)).TryGetValue(column, out List<(string, Types.Variants.VariantShredding)>? list))
                {
                    keys[column] = list = [];
                }

                list.Add((path.Key, Types.Variants.VariantShredding.Primitive(path.Class)));
            }
        }

        if (keys is null)
        {
            return null;
        }

        Dictionary<string, Types.Variants.VariantShredding> plans = new(StringComparer.Ordinal);
        foreach ((string column, List<(string, Types.Variants.VariantShredding)> list) in keys)
        {
            plans[column] = Types.Variants.VariantShredding.Object([.. list]);
        }

        return plans;
    }

    /// <summary>
    /// Starts a data object under <paramref name="schema"/>: what a rewrite uses, whose rows are read
    /// as the schema of the version it read, whatever the handle has moved to since. A caller whose
    /// rows come in key order says so with <paramref name="inKeyOrder"/>, and a key whose column then
    /// orders the object by itself takes no run. A rewrite announces with <paramref name="byteBound"/>
    /// the most bytes the object takes should it stream (<see cref="ObjectBound"/>).
    /// </summary>
    internal ObjectDraft StartObjectUnder(
        DatasetSchema schema, bool inKeyOrder = false, long rowBound = 0, bool sliced = false,
        IReadOnlyDictionary<string, Types.Variants.VariantShredding>? shredded = null, IReadOnlyDictionary<string, int>? variantChunks = null,
        IReadOnlyDictionary<string, Indexes.VariantDirectory>? directories = null, long byteBound = -1, bool blocked = false)
    {
        RequireConditionalCreation(_store);
        Guid identity = Guid.NewGuid();
        string key = CommitKey.ForData(identity.ToString("N", CultureInfo.InvariantCulture));
        ObjectSegmentSink sink = new ObjectSegmentSink(
            _store, key, _options.MaxObjectBytes, _options.Session.Options.MemoryPool, _options.StreamedObjectBytes, byteBound);

        VortexWriteOptions write = _options.Write.WithIdentity(identity) with
        {
            RowBound = rowBound,
            CarriesSlices = sliced,
            GatheredChunkBytes = _options.ChunkBytes,
            GatheredGroupRows = _options.GroupRows,

            // A sliced object, which a job writes, keeps a sheet of each column its table of contents names, when it is
            // large enough for a scan to read the sheets before it, and the counts of each column over its rows.
            ColumnSheetBytes = sliced ? _options.SheetObjectBytes : null,
            Counted = sliced,

            // An object a job writes in its key's order, in blocks, keeps each column's statistics over them.
            Blocked = sliced && blocked,
        };

        // The variant columns a job shreds, each in long chunks of its own; an append shreds the declared paths when its
        // handle says so (DatasetOptions.ShredOnAppend), and nothing otherwise.
        if (shredded is { Count: > 0 })
        {
            write = write with
            {
                ShreddedVariants = shredded,
                ColumnChunkTargetBytes = write.ColumnChunkTargetBytes.SetItems(variantChunks ?? ImmutableDictionary<string, int>.Empty),
            };
        }

        // Every path and class of the variant columns, which a job counted from the object's rows.
        if (directories is { Count: > 0 })
        {
            write = write with { VariantDirectories = directories };
        }

        if (_store is SealedObjectStore { Seals: true })
        {
            // The runs an index of the object moves to disk hold its values: sealed as the object is.
            write = write with { SealScratch = true };
        }

        // The indexes the dataset was given, at the version the write builds on, whatever this
        // handle's own options say: a compaction by a handle that never heard of them keeps them,
        // and its budget does not refuse them, since they were asked for by name.
        if (schema.IndexPolicy.Columns.Count > 0)
        {
            write = write.WithIndexes(write.WritePolicy.With(schema.IndexPolicy)) with { BudgetSpares = schema.IndexedColumns };
        }

        // A default the dataset was given, such as every column's automatic index: each column the write meets
        // that no override names takes it, those schema evolution added after the policy included. The budget
        // weighs it, since no column asked for it by name.
        if (schema.IndexPolicy.Default.Kind != Indexes.IndexPolicyKind.None)
        {
            write = write.WithIndexes(write.WritePolicy.WithDefault(schema.IndexPolicy.Default));
        }

        // Each declared identifier's hashed postings, required and spared by the budget as the clustering
        // key's run is: a lookup of one value reads the objects that hold it and almost no other, one
        // segment of about 64 KiB an object, at about four bytes a (value, block) pair. A Bloom filter,
        // which a tree of them loses past what one filter holds, is never tried on it. The identifiers are
        // those of the version the write builds on, as its indexes are; one whose column a change of the
        // schema dropped or renamed names nothing the write holds, and is left out rather than required.
        foreach (string identifier in schema.Identifiers)
        {
            if (schema.DType.IndexOfField(identifier) >= 0 || IdentifierPath(identifier, schema.DType) is not null)
            {
                write = write.WithIndexes(write.WritePolicy.For(identifier, Indexes.IndexSpec.HashedPostings.AsRequired())) with { BudgetSparesRequired = true };
            }
        }

        bool bySortedColumn = inKeyOrder && schema.Key is { OrdersBySortedColumn: true };

        // An object in key slices keeps its run, or its sorted column.
        if (schema.Key is { } clustering && !bySortedColumn)
        {
            // The mandatory sorted run on the clustering key, added to the caller's own policy.
            write = clustering.Applied(write);
        }

        // The session's degree and its lanes, which every writer of the session shares.
        return new ObjectDraft(this, identity, key, sink, VortexFileWriter.Create(sink, schema.DType, write, _options.Session))
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
        List<WrittenObject> written = await WrittenAsync(draft, cancellationToken).ConfigureAwait(false);
        if (written.Count == 0)
        {
            return Version;
        }

        ulong version = await ApplyAsync(Added(written), CommitKind.Append, cancellationToken).ConfigureAwait(false);
        await KeptUpAsync(written, cancellationToken).ConfigureAwait(false);
        return version;
    }

    /// <summary>How long a family this handle's questions asked is kept up: the objects it appends learn its partials as they are written.</summary>
    internal static readonly TimeSpan KeptUpFor = TimeSpan.FromHours(1);

    /// <summary>
    /// Learns, of each object an append wrote, the partial of each family this handle's questions asked in the last
    /// <see cref="KeptUpFor"/> (<see cref="LearnedPartials.Asked"/>), as the object makes it (<see cref="FamilyProof"/>): the
    /// family run over the object's rows as the append left them, so that a question of it answers the object with no read.
    /// </summary>
    private async ValueTask KeptUpAsync(List<WrittenObject> written, CancellationToken cancellationToken)
    {
        List<Aggregating.LearnedFamily> live = Partials.Live(_options.TimeProvider.GetUtcNow().UtcTicks - KeptUpFor.Ticks);
        if (live.Count == 0)
        {
            return;
        }

        DatasetSchema schema = Snapshot.Schema;
        SynopsisCache? cache = Session.Options.SynopsisCache;
        foreach (WrittenObject made in written)
        {
            ObjectEntry entry = made.Entry;
            ObjectLease? lease = null;
            try
            {
                foreach (Aggregating.LearnedFamily family in live)
                {
                    if (new FamilyProof(family, schema).Of(entry.Summaries, entry.PhysicalRows) is not { } own || Partials.Holds(entry.Uid, own.Key, cache))
                    {
                        continue;
                    }

                    lease ??= await RentAsync(entry, cancellationToken).ConfigureAwait(false);
                    AggregationPartial partial = await own.Family.LearnAsync(
                        new FileScanSource(lease.File), new RowRange(0, lease.File.RowCount), ScanOptions.Default, cancellationToken).ConfigureAwait(false);
                    Partials.Keep(entry.Uid, own.Key, partial, cache);
                }
            }
            finally
            {
                if (lease is not null)
                {
                    await lease.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
    }

    /// <summary>The objects an append of <paramref name="draft"/> adds: the draft's own, completed and put.</summary>
    private async ValueTask<List<WrittenObject>> WrittenAsync(ObjectDraft draft, CancellationToken cancellationToken)
    {
        WrittenObject? written = await CompleteAsync(draft, await EndAsync(cancellationToken).ConfigureAwait(false), cancellationToken)
            .ConfigureAwait(false);
        return written is { } produced ? [produced] : [];
    }

    /// <summary>The batches of an append, as one object.</summary>
    private async ValueTask<List<WrittenObject>> WrittenAsync(IAsyncEnumerable<RecordBatch> batches, CancellationToken cancellationToken)
    {
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

        return await WrittenAsync(draft, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The additions of written objects, one commit's operations.</summary>
    private static DatasetOperation[] Added(List<WrittenObject> written)
    {
        DatasetOperation[] operations = new DatasetOperation[written.Count];
        for (int i = 0; i < operations.Length; i++)
        {
            operations[i] = new DatasetOperation.AddObject(written[i].Key, written[i].Entry);
        }

        return operations;
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
        List<WrittenObject> written = await WrittenAsync(batches, cancellationToken).ConfigureAwait(false);
        if (written.Count == 0)
        {
            return Version;
        }

        ulong version = await ApplyAsync(Added(written), CommitKind.Append, cancellationToken).ConfigureAwait(false);
        await KeptUpAsync(written, cancellationToken).ConfigureAwait(false);
        return version;
    }

    /// <summary>
    /// How far the dataset counts the entries of <paramref name="source"/>, at the version this handle
    /// reads: the offset the last range of it a commit consumed ended at, or 0 for a source no commit
    /// consumed. It is free: the version's header holds it.
    /// </summary>
    /// <param name="source">The source's name.</param>
    /// <returns>The offset, where the next range of the source starts.</returns>
    /// <exception cref="ArgumentException"><paramref name="source"/> is null or empty.</exception>
    public ulong SourceOffset(string source)
    {
        ArgumentException.ThrowIfNullOrEmpty(source);
        return SourceTable.OffsetOf(Snapshot.Header.Sources, source);
    }

    /// <summary>
    /// Completes a data object written through <see cref="StartObject"/> from entries of the ranges
    /// <paramref name="consumed"/>, puts it in the store and adds it in one commit that counts them,
    /// so that each entry is counted once however many writers take it.
    /// </summary>
    /// <param name="draft">The object; its writer is completed here if the caller did not. It may hold no row, and the ranges are counted all the same.</param>
    /// <param name="consumed">The ranges the rows take into account, one per source, each starting where the dataset counts its source up to (<see cref="SourceOffset"/>).</param>
    /// <param name="cancellationToken">Cancels the put and the commit.</param>
    /// <returns>
    /// <see cref="OperationOutcome.Applied"/> and the version created, the offsets moved to the
    /// ranges' ends; or <see cref="OperationOutcome.AlreadyThere"/> and the version that counts them
    /// when another writer counted every range first, in which case nothing was added and the
    /// object is left for vacuum.
    /// </returns>
    /// <remarks>
    /// The ranges are judged at every attempt against the version the attempt lands on, rebases
    /// included: the commit applies where each range starts at its source's offset, adds nothing where
    /// each ends at or before it, and throws otherwise, since its rows mix its sources and cannot
    /// apply in part. Act outside the dataset, a notification for one, only on
    /// <see cref="OperationOutcome.Applied"/>: the commit names the one writer that counted the ranges.
    /// A dataset whose commits count source offsets is written in commit format 3, which a library
    /// that predates them refuses rather than forget the offsets at its first commit.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="consumed"/> is empty, or names a source twice.</exception>
    /// <exception cref="SourceOffsetConflictException">
    /// A range would leave a gap, or overlap one another commit counted. Nothing was committed, the
    /// handle reads the version the conflict was decided against, and the ranges are worked out
    /// again from <see cref="SourceOffsetConflictException.Recorded"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">The draft belongs to another handle, was already committed, or its writer was abandoned.</exception>
    public async ValueTask<SourceCommit> AppendAsync(
        ObjectDraft draft, IReadOnlyList<SourceRange> consumed, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        SourceTable.Check(consumed, nameof(consumed));
        return await CountedAsync(await WrittenAsync(draft, cancellationToken).ConfigureAwait(false), consumed, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Commits written objects with the source ranges they take into account, as one commit.</summary>
    private async ValueTask<SourceCommit> CountedAsync(List<WrittenObject> written, IReadOnlyList<SourceRange> consumed, CancellationToken cancellationToken)
    {
        CommitResult commit = await CommitAsync(Added(written), CommitKind.Append, consumed, null, cancellationToken).ConfigureAwait(false);
        if (commit.Created)
        {
            await CompactInlineAsync(cancellationToken).ConfigureAwait(false);
        }

        return new SourceCommit
        {
            Version = commit.Version,
            Outcome = commit.Counted ? OperationOutcome.AlreadyThere : OperationOutcome.Applied,
        };
    }

    /// <summary>
    /// Writes the batches, taken from the ranges <paramref name="consumed"/>, as one data object, and
    /// adds it in one commit that counts them; see <see cref="AppendAsync(ObjectDraft, IReadOnlyList{SourceRange}, CancellationToken)"/>.
    /// </summary>
    /// <param name="batches">The rows, with the dataset's schema; there may be none.</param>
    /// <param name="consumed">The ranges the rows take into account, one per source.</param>
    /// <param name="cancellationToken">Cancels the writes, the put and the commit.</param>
    /// <returns>What the commit made of the ranges, and the version.</returns>
    /// <exception cref="ArgumentException"><paramref name="consumed"/> is empty, or names a source twice.</exception>
    /// <exception cref="SourceOffsetConflictException">A range would leave a gap, or overlap one another commit counted; nothing was committed.</exception>
    public async ValueTask<SourceCommit> AppendAsync(
        IAsyncEnumerable<RecordBatch> batches, IReadOnlyList<SourceRange> consumed, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(batches);
        SourceTable.Check(consumed, nameof(consumed));
        return await CountedAsync(await WrittenAsync(batches, cancellationToken).ConfigureAwait(false), consumed, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Forgets the offsets of <paramref name="sources"/>, in one commit that changes no row: what a
    /// dataset does with a source no writer takes any more, whose offset it would carry otherwise in
    /// every header.
    /// </summary>
    /// <param name="sources">The sources' names.</param>
    /// <param name="cancellationToken">Cancels the commit.</param>
    /// <returns>The version created.</returns>
    /// <remarks>
    /// Safe only once no writer can take one of the sources again: one that did would start it from
    /// 0, and count its entries a second time.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="sources"/> is empty, or names an empty source.</exception>
    public async ValueTask<ulong> ForgetSourcesAsync(IReadOnlyList<string> sources, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sources);
        if (sources.Count == 0)
        {
            throw new ArgumentException("Name at least one source to forget.", nameof(sources));
        }

        foreach (string source in sources)
        {
            ArgumentException.ThrowIfNullOrEmpty(source, nameof(sources));
        }

        CommitResult commit = await CommitAsync([], CommitKind.Sources, null, new HashSet<string>(sources, StringComparer.Ordinal), cancellationToken)
            .ConfigureAwait(false);
        return commit.Version;
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
        RequireConditionalCreation(_store);
        ObjectHead head = await _store.HeadAsync(objectKey, cancellationToken).ConfigureAwait(false)
            ?? throw ObjectNotFoundException.For(objectKey);

        long end = await EndAsync(cancellationToken).ConfigureAwait(false);
        ISegmentReader source = await ImportReaderAsync(objectKey, head.Length, cancellationToken).ConfigureAwait(false);
        ObjectEntry entry;
        ReadOnlyMemory<byte> treeKey;
        await using (source.ConfigureAwait(false))
        {
            VortexFile file = await VortexFile
                .OpenAsync(source, ObjectCache.OpenOptions, cancellationToken).ConfigureAwait(false);
            await using (file.ConfigureAwait(false))
            {
                // A sealed file keeps the binding it was written with: its object id is its identity,
                // which its entry records and every later open checks again.
                if (source is Sealing.SealedSegmentReader sealedFile
                    && !sealedFile.Layout.Descriptor.ObjectId.SequenceEqual(SealedObjectStore.UidBytes(Identity(file))))
                {
                    throw VortexEncryptionException.Unauthenticated(
                        $"'{objectKey}' is not the file its envelope binds: its identity and its object id differ.");
                }

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
                    objectKey, Identity(file), file.RowCount, head.Length, UInt128.Zero, Summaries(file))
                    .WithFooterBytes(file.TailBytes);
                treeKey = await TreeKeyAsync(file, entry, end, cancellationToken).ConfigureAwait(false);
            }
        }

        return await ApplyAsync(
            [new DatasetOperation.AddObject(treeKey, entry)], CommitKind.Import, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The reader an import opens a file through: sealed into an encrypted dataset, which refuses a
    /// plain one, and plain into a plain one, which refuses a sealed one, as a dataset is sealed
    /// whole or not at all.
    /// </summary>
    private async ValueTask<ISegmentReader> ImportReaderAsync(string objectKey, long length, CancellationToken cancellationToken)
    {
        if (_store is SealedObjectStore { Seals: true } sealedStore)
        {
            return await sealedStore.OpenDataAsync(objectKey, UInt128.Zero, length, cancellationToken).ConfigureAwait(false);
        }

        ObjectStoreSource plain = new ObjectStoreSource(_store, objectKey, length);
        if (_store is SealedObjectStore && await Sealing.SealedFiles.EndsSealedAsync(plain, cancellationToken).ConfigureAwait(false))
        {
            await plain.DisposeAsync().ConfigureAwait(false);
            throw VortexEncryptionException.Refused(
                $"'{objectKey}' is sealed and the dataset is not encrypted: a dataset is sealed whole or not at all.");
        }

        return plain;
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
    public ValueTask<ReplaceResult> ReplaceObjectsAsync(
        IReadOnlyList<DataObject> removed, IReadOnlyList<ObjectDraft> added, CancellationToken cancellationToken = default) =>
        ReplaceAsync(removed, added, null, cancellationToken);

    /// <summary>
    /// Removes data objects and adds others, written from entries of the ranges
    /// <paramref name="consumed"/>, in one commit that counts them; see
    /// <see cref="ReplaceObjectsAsync(IReadOnlyList{DataObject}, IReadOnlyList{ObjectDraft}, CancellationToken)"/>
    /// and <see cref="AppendAsync(ObjectDraft, IReadOnlyList{SourceRange}, CancellationToken)"/>.
    /// </summary>
    /// <param name="removed">Objects from <see cref="ListObjectsAsync"/>.</param>
    /// <param name="added">Drafts whose rows are written; each is completed and put before the commit.</param>
    /// <param name="consumed">The ranges the change takes into account, one per source.</param>
    /// <param name="cancellationToken">Cancels the puts and the commit.</param>
    /// <returns>
    /// The version created; or <see cref="OperationOutcome.AlreadyThere"/> and the version that
    /// counts the ranges when another commit counted them first; or
    /// <see cref="OperationOutcome.Abandoned"/> when an object to remove was gone, the ranges then
    /// not counted. The objects written for a commit that did not apply are left for vacuum.
    /// </returns>
    /// <exception cref="ArgumentException">An object does not come from this dataset's walk, or <paramref name="consumed"/> is empty or names a source twice.</exception>
    /// <exception cref="SourceOffsetConflictException">A range would leave a gap, or overlap one another commit counted; nothing was committed.</exception>
    /// <exception cref="InvalidOperationException">A draft belongs to another handle, was already committed, or its writer was abandoned.</exception>
    public ValueTask<ReplaceResult> ReplaceObjectsAsync(
        IReadOnlyList<DataObject> removed,
        IReadOnlyList<ObjectDraft> added,
        IReadOnlyList<SourceRange> consumed,
        CancellationToken cancellationToken = default)
    {
        SourceTable.Check(consumed, nameof(consumed));
        return ReplaceAsync(removed, added, consumed, cancellationToken);
    }

    private async ValueTask<ReplaceResult> ReplaceAsync(
        IReadOnlyList<DataObject> removed, IReadOnlyList<ObjectDraft> added, IReadOnlyList<SourceRange>? consumed, CancellationToken cancellationToken)
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

        if (inputs.Count == 0 && outputs.Count == 0 && consumed is null)
        {
            return new ReplaceResult { Version = Version, Outcome = OperationOutcome.AlreadyThere };
        }

        DatasetOperation[] operations = inputs.Count == 0 && outputs.Count == 0
            ? []
            : [new DatasetOperation.ReplaceObjects(inputs, outputs) { Expected = expected }];
        CommitResult commit = await CommitAsync(operations, CommitKind.Replace, consumed, null, cancellationToken).ConfigureAwait(false);
        OperationOutcome outcome = commit.Counted ? OperationOutcome.AlreadyThere
            : commit.Outcomes.Count > 0 ? commit.Outcomes[0]
            : OperationOutcome.Applied;
        if (outputs.Count > 0 && outcome == OperationOutcome.Applied)
        {
            await CompactInlineAsync(cancellationToken).ConfigureAwait(false);
        }

        return new ReplaceResult { Version = commit.Version, Outcome = outcome };
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
    /// Drops, in one commit and reading none of them, every object whose rows <paramref name="filter"/> is true for, as
    /// the bounds its entry states prove: what a delete of those rows does to such an object, with no other opened.
    /// </summary>
    /// <typeparam name="TRecord">The record the filter is written against.</typeparam>
    /// <param name="filter">A lambda over the record's columns: <c>r =&gt; r.At &lt; cutoff</c>.</param>
    /// <param name="cancellationToken">Cancels the walk and the commit.</param>
    /// <returns>
    /// The version created and the rows dropped; <see cref="OperationOutcome.AlreadyThere"/> and the version the handle
    /// held when the filter covers no object whole.
    /// </returns>
    /// <remarks>
    /// <para>
    /// The rows of an object the filter does not cover whole stay, those of an object of level 0 that straddles its bound
    /// among them, until an expiry finds them in objects it covers; a delete takes them as well, at the price of the
    /// objects it opens. Under a clustering value (<see cref="DatasetOptions.Clustering"/>), every object above level 0
    /// holds rows of one value, so a filter that keeps the values from one on covers whole objects: <c>r =&gt; r.At &lt;
    /// day</c>, on objects cut at every midnight, drops every day before <c>day</c>, and a row outlives the filter by a
    /// value at most.
    /// </para>
    /// <para>
    /// An object's bounds are those of its summaries, which a number and a time have and a text has not: a filter on a
    /// text covers no object.
    /// </para>
    /// </remarks>
    /// <exception cref="VortexSchemaException">A literal or a column of the filter does not fit the dataset.</exception>
    /// <exception cref="ObjectStoreException">Concurrent commits rewrote an object this expiry would drop on every attempt.</exception>
    public ValueTask<RowChangeResult> ExpireAsync<TRecord>(Func<Probe<TRecord>, Predicate> filter, CancellationToken cancellationToken = default)
        where TRecord : IVortexRecord<TRecord>
    {
        ArgumentNullException.ThrowIfNull(filter);
        Predicate predicate = filter(new Probe<TRecord>(RecordBinding.For<TRecord>(Schema, Session.Options.Extensions)));
        return predicate.IsNone
            ? ValueTask.FromResult(Unchanged())
            : DatasetRowChanges.ExpireAsync(this, Checked(predicate.Node), cancellationToken);
    }

    /// <summary>
    /// Drops the objects <paramref name="filter"/> covers whole, for a caller without a record type; see
    /// <see cref="ExpireAsync{TRecord}(Func{Probe{TRecord}, Predicate}, CancellationToken)"/>.
    /// </summary>
    /// <param name="filter">The filter, from <see cref="VortexExpr.Parse"/> or combined with operators.</param>
    /// <param name="cancellationToken">Cancels the walk and the commit.</param>
    /// <returns>The version created and the rows dropped.</returns>
    /// <exception cref="VortexSchemaException">The filter names a column the dataset does not have, or compares one with a literal of a type it cannot compare to.</exception>
    /// <exception cref="ObjectStoreException">Concurrent commits rewrote an object this expiry would drop on every attempt.</exception>
    public ValueTask<RowChangeResult> ExpireAsync(VortexExpr filter, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        return DatasetRowChanges.ExpireAsync(this, Checked(ToolPaths.Check(Schema, filter)), cancellationToken);
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
        where TRecord : IVortexRecord<TRecord> =>
        ChangeAsync(filter, update, null, cancellationToken);

    /// <summary>
    /// Changes the rows <paramref name="filter"/> is true for, in one commit that counts the source
    /// ranges <paramref name="consumed"/>; see
    /// <see cref="UpdateAsync{TRecord}(Func{Probe{TRecord}, Predicate}, Func{TRecord, TRecord}, CancellationToken)"/>
    /// and <see cref="AppendAsync(ObjectDraft, IReadOnlyList{SourceRange}, CancellationToken)"/>.
    /// </summary>
    /// <typeparam name="TRecord">The record the rows are read and written as; it has a member for every column of the dataset.</typeparam>
    /// <param name="filter">A lambda over the record's columns.</param>
    /// <param name="update">The change, called once per row.</param>
    /// <param name="consumed">The ranges the change takes into account, one per source; counted even when no row matches.</param>
    /// <param name="cancellationToken">Cancels the reads, the writes and the commit.</param>
    /// <returns>
    /// The version created and the rows changed; or <see cref="OperationOutcome.AlreadyThere"/> and
    /// the version that counts the ranges when another commit counted them first, no row changed.
    /// </returns>
    /// <exception cref="ArgumentException"><typeparamref name="TRecord"/> has no member for a column of the dataset, or <paramref name="consumed"/> is empty or names a source twice.</exception>
    /// <exception cref="SourceOffsetConflictException">A range would leave a gap, or overlap one another commit counted; nothing was committed.</exception>
    /// <exception cref="ObjectStoreException">Concurrent commits rewrote an object this update read on every attempt.</exception>
    public ValueTask<RowChangeResult> UpdateAsync<TRecord>(
        Func<Probe<TRecord>, Predicate> filter,
        Func<TRecord, TRecord> update,
        IReadOnlyList<SourceRange> consumed,
        CancellationToken cancellationToken = default)
        where TRecord : IVortexRecord<TRecord>
    {
        SourceTable.Check(consumed, nameof(consumed));
        return ChangeAsync(filter, update, consumed, cancellationToken);
    }

    /// <summary>
    /// Deletes the rows <paramref name="filter"/> is true for, in one commit that counts the source
    /// ranges <paramref name="consumed"/>; see
    /// <see cref="DeleteAsync{TRecord}(Func{Probe{TRecord}, Predicate}, CancellationToken)"/> and
    /// <see cref="AppendAsync(ObjectDraft, IReadOnlyList{SourceRange}, CancellationToken)"/>.
    /// </summary>
    /// <typeparam name="TRecord">The record whose members name the columns.</typeparam>
    /// <param name="filter">A lambda over the record's columns.</param>
    /// <param name="consumed">The ranges the delete takes into account, one per source; counted even when no row matches.</param>
    /// <param name="cancellationToken">Cancels the reads, the writes and the commit.</param>
    /// <returns>
    /// The version created and the rows deleted; or <see cref="OperationOutcome.AlreadyThere"/> and
    /// the version that counts the ranges when another commit counted them first, no row deleted.
    /// </returns>
    /// <exception cref="ArgumentException"><paramref name="consumed"/> is empty, or names a source twice.</exception>
    /// <exception cref="SourceOffsetConflictException">A range would leave a gap, or overlap one another commit counted; nothing was committed.</exception>
    /// <exception cref="ObjectStoreException">Concurrent commits rewrote an object this delete read on every attempt.</exception>
    public ValueTask<RowChangeResult> DeleteAsync<TRecord>(
        Func<Probe<TRecord>, Predicate> filter, IReadOnlyList<SourceRange> consumed, CancellationToken cancellationToken = default)
        where TRecord : IVortexRecord<TRecord>
    {
        ArgumentNullException.ThrowIfNull(filter);
        SourceTable.Check(consumed, nameof(consumed));
        Predicate predicate = filter(new Probe<TRecord>(RecordBinding.For<TRecord>(Schema, Session.Options.Extensions)));
        return predicate.IsNone
            ? CountRangesAsync(CommitKind.Delete, consumed, cancellationToken)
            : DatasetRowChanges.RunAsync(this, Checked(predicate.Node), null, consumed, cancellationToken);
    }

    /// <summary>
    /// Deletes the rows <paramref name="filter"/> is true for, in one commit that counts the source
    /// ranges <paramref name="consumed"/>, for a caller without a record type.
    /// </summary>
    /// <param name="filter">The filter, from <see cref="VortexExpr.Parse"/> or combined with operators.</param>
    /// <param name="consumed">The ranges the delete takes into account, one per source.</param>
    /// <param name="cancellationToken">Cancels the reads, the writes and the commit.</param>
    /// <returns>The version created and the rows deleted, or <see cref="OperationOutcome.AlreadyThere"/> when the ranges were counted already.</returns>
    /// <exception cref="ArgumentException"><paramref name="consumed"/> is empty, or names a source twice.</exception>
    /// <exception cref="VortexSchemaException">The filter names a column the dataset does not have, or compares one with a literal of a type it cannot compare to.</exception>
    /// <exception cref="SourceOffsetConflictException">A range would leave a gap, or overlap one another commit counted; nothing was committed.</exception>
    public ValueTask<RowChangeResult> DeleteAsync(
        VortexExpr filter, IReadOnlyList<SourceRange> consumed, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        SourceTable.Check(consumed, nameof(consumed));
        return DatasetRowChanges.RunAsync(this, Checked(ToolPaths.Check(Schema, filter)), null, consumed, cancellationToken);
    }

    /// <summary>The commit of ranges whose change can match no row: the offsets move, and nothing else.</summary>
    private async ValueTask<RowChangeResult> CountRangesAsync(CommitKind kind, IReadOnlyList<SourceRange> consumed, CancellationToken cancellationToken)
    {
        CommitResult commit = await CommitAsync([], kind, consumed, null, cancellationToken).ConfigureAwait(false);
        return new RowChangeResult
        {
            Version = commit.Version,
            Outcome = commit.Counted ? OperationOutcome.AlreadyThere : OperationOutcome.Applied,
            Attempts = commit.Attempts,
        };
    }

    private ValueTask<RowChangeResult> ChangeAsync<TRecord>(
        Func<Probe<TRecord>, Predicate> filter, Func<TRecord, TRecord> update, IReadOnlyList<SourceRange>? consumed, CancellationToken cancellationToken)
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
        if (predicate.IsNone)
        {
            return consumed is null ? ValueTask.FromResult(Unchanged()) : CountRangesAsync(CommitKind.Update, consumed, cancellationToken);
        }

        return DatasetRowChanges.RunAsync(
            this,
            Checked(predicate.Node),
            (schema, end) => new ChangedRecords<TRecord>(this, schema, update, end),
            consumed,
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
        CommitResult commit = await CommitAsync([new DatasetOperation.ChangeSchema(current.Bytes, bytes, retired)], CommitKind.Schema, cancellationToken)
            .ConfigureAwait(false);
        return commit.Outcomes[0] is OperationOutcome.Applied or OperationOutcome.AlreadyThere
            ? commit.Version
            : throw new InvalidOperationException(
                $"Another writer changed the dataset's schema after version {version.Version}, which this change was checked " +
                $"against; version {commit.Version} has another. Refresh, and change the schema the dataset has now.");
    }

    /// <summary>
    /// Adds identifiers to the dataset's (<see cref="DatasetOptions.Identifiers"/>), in one commit that rewrites no object:
    /// every object written from the version it creates carries hashed postings on them, appends and compactions alike,
    /// and the objects written before are read without them.
    /// </summary>
    /// <param name="identifiers">
    /// The identifiers, each a column of the schema or a key of a variant column read as a string or an integer,
    /// <c>Attributes['user'].AsString()</c>; one the dataset has already stays as it is.
    /// </param>
    /// <param name="cancellationToken">Cancels the commit.</param>
    /// <returns>The version created, which this handle now reads; the one it held when the dataset has every identifier already.</returns>
    /// <remarks>
    /// A lookup of an added identifier reads an object written since by its postings, and one written before as a scan
    /// reads any object, its rows found all the same, until a compaction rewrites it: an object no job takes again, one of the
    /// last level, is read so until an expiry drops it. Two writers that add one identifier each keep both.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="identifiers"/> is null.</exception>
    /// <exception cref="ArgumentException">An identifier names neither a column of the schema nor such a key, or is named twice.</exception>
    /// <exception cref="InvalidOperationException">Another writer changed the schema first; refresh, and add the identifiers to the schema the dataset has now.</exception>
    public async ValueTask<ulong> AddIdentifiersAsync(IReadOnlyList<string> identifiers, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identifiers);
        DatasetSnapshot version = Snapshot;
        DatasetSchema current = version.Schema;
        string[] adding = RequireIdentifiers(identifiers, current.DType, nameof(identifiers));
        if (new HashSet<string>(current.Identifiers, StringComparer.Ordinal).IsSupersetOf(adding))
        {
            return version.Version;
        }

        CommitResult commit = await CommitAsync([new DatasetOperation.AddIdentifiers(current.Bytes, adding)], CommitKind.Index, cancellationToken)
            .ConfigureAwait(false);
        return commit.Outcomes[0] is OperationOutcome.Applied or OperationOutcome.AlreadyThere
            ? commit.Version
            : throw new InvalidOperationException(
                $"Another writer changed the dataset's schema after version {version.Version}, which the identifiers were checked " +
                $"against; version {commit.Version} has another. Refresh, and add the identifiers to the schema the dataset has now.");
    }

    /// <summary>
    /// Sets the keys of variant columns every job shreds into columns of their own (<see cref="DatasetOptions.ShreddedPaths"/>),
    /// in one commit that rewrites no object: each job that starts from the version it creates shreds these, and no
    /// other the dataset declared, and the objects written before keep their columns as they lie.
    /// </summary>
    /// <param name="paths">
    /// The paths, each a key of a variant column read as a string, an integer, a double or a boolean,
    /// <c>Attributes['http.response.status_code'].AsInt64()</c>; empty for none. Every question reads what the rows hold,
    /// whichever objects shred a key: a path added or taken away changes the cost of a question, never its answer.
    /// </param>
    /// <param name="cancellationToken">Cancels the commit.</param>
    /// <returns>The version created, which this handle now reads; the one it held when the dataset has these paths already.</returns>
    /// <remarks>
    /// A key an identifier names is shredded by every job whether a path names it or not, in the identifier's class.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="paths"/> is null.</exception>
    /// <exception cref="ArgumentException">A path names no such key, or a key is named twice.</exception>
    /// <exception cref="InvalidOperationException">
    /// Another writer changed the schema or the paths first; refresh, and set the paths against those the dataset has now.
    /// </exception>
    public async ValueTask<ulong> SetShreddedPathsAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        DatasetSnapshot version = Snapshot;
        DatasetSchema current = version.Schema;
        string[] set = RequireShreddedPaths(paths, current.DType, nameof(paths));
        if (DatasetCommitter.SameNames(set, current.ShreddedPaths))
        {
            return version.Version;
        }

        CommitResult commit = await CommitAsync(
                [new DatasetOperation.ChangeShreddedPaths(current.Bytes, current.ShreddedPaths, set)], CommitKind.Layout, cancellationToken)
            .ConfigureAwait(false);
        return commit.Outcomes[0] is OperationOutcome.Applied or OperationOutcome.AlreadyThere
            ? commit.Version
            : throw new InvalidOperationException(
                $"Another writer changed the dataset's schema or its shredded paths after version {version.Version}, which these " +
                $"were set against; version {commit.Version} has others. Refresh, and set the paths against those the dataset has now.");
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
    /// Sums the counts of the deepest level's objects into rollups, rank by rank, where the latest version holds none yet:
    /// runs of about <see cref="DatasetOptions.RollupFanout"/> consecutive objects, then of as many rollups, each summed
    /// from what its children keep, with no row read. A question over a long range of the key then reads a rollup where it
    /// would read the counts of its objects. A commit that puts or takes away an object takes away the rollups over it,
    /// which a later call sums again; <see cref="RunCompactionAsync"/> calls this when no job is due.
    /// </summary>
    /// <param name="cancellationToken">Stops the work between rollups or inside one; a rollup written and not put is swept by a vacuum.</param>
    /// <returns>The rollups put.</returns>
    public ValueTask<int> RollUpAsync(CancellationToken cancellationToken = default) => DatasetRollups.RollUpAsync(this, cancellationToken);

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
    /// every other loop. Two loops that do not know each other waste work, though: both plan the
    /// same job, and the commit of one abandons the other. A dataset with several writers is
    /// compacted by one loop, in a process of its own, while the writers leave inline compaction off
    /// (<see cref="DatasetOptions.InlineCompactionBytes"/>, 0 by default). Loops that know one another
    /// spread over the due jobs by <see cref="CompactionSchedule.Loops"/> and
    /// <see cref="CompactionSchedule.Loop"/>.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The schedule's loop index, rate or sleep is out of range.</exception>
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
        DatasetVacuum.RunAsync(_store, options, _location, cancellationToken);

    /// <summary>
    /// Gives the dataset indexes: every writer of it builds them from then on, compactions included,
    /// and the objects already written take them as index fragments, a batch of objects per commit.
    /// </summary>
    /// <param name="policy">The indexes, each on one column, e.g. <c>IndexPolicy.None.Bloom("trace_id")</c>.</param>
    /// <param name="options">How many objects a commit indexes; null for the defaults.</param>
    /// <param name="cancellationToken">Cancels the indexing between two objects; what was committed stays.</param>
    /// <returns>What the indexing did.</returns>
    /// <remarks>
    /// <para>
    /// The first commit adds the indexes to those the dataset's commits carry, joined to them rather
    /// than put in their place, so two indexings of two columns at once keep both. From that version
    /// on, every write and every compaction builds them into the objects it writes, whatever its
    /// handle's options say, so a compaction can no longer undo an indexing by rewriting an indexed
    /// object without its index. The indexing then walks the objects and gives each one that lacks an
    /// index a fragment, and walks again until a walk finds nothing to index: a compaction planned
    /// before the first commit may still write an object without the indexes, which the next walk
    /// finds.
    /// </para>
    /// <para>
    /// Every version in between answers as the dataset without the indexes does: an object partly
    /// indexed prunes the blocks its indexes cover. The indexes are asked for by name, so no budget
    /// refuses them, neither a writer's nor the policy's. An object that carries no identity, written
    /// by another writer, cannot take a fragment, and one with no block an index can be built for is
    /// not asked again. <see cref="IndexingResult.ObjectsLeft"/> counts both, and a compaction that
    /// rewrites the first builds the indexes into it.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="policy"/> is null.</exception>
    /// <exception cref="ArgumentException">The policy names no index of a column, or names a composite key, whose encoder the dataset's commits cannot carry.</exception>
    /// <exception cref="VortexSchemaException">The policy names a column the dataset does not have.</exception>
    /// <exception cref="NotSupportedException">The store cannot create an object only when it is absent, and hosts readers only.</exception>
    public ValueTask<IndexingResult> IndexAsync(IndexPolicy policy, IndexingOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return DatasetIndexing.RunAsync(this, policy, options ?? IndexingOptions.Default, cancellationToken);
    }

    /// <summary>
    /// Checks this version's pages, objects and fragments against what its references and entries
    /// promise, and names every problem rather than throwing.
    /// </summary>
    /// <param name="since">A version already verified, whose shared pages and entries are not checked again; null verifies everything.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>What was checked and every problem found.</returns>
    public ValueTask<DatasetVerification> VerifyAsync(ulong? since = null, CancellationToken cancellationToken = default) =>
        DatasetVerifier.VerifyAsync(_store, new VerifyOptions { Version = Version, Since = since }, cancellationToken);

    /// <summary>Closes the data objects the handle keeps open, and wipes the keys of the commit objects it opened.</summary>
    /// <returns>A task that completes when every object is closed.</returns>
    public async ValueTask DisposeAsync()
    {
        await LevelZero.DisposeAsync().ConfigureAwait(false);
        await _objects.DisposeAsync().ConfigureAwait(false);
        if (_store is SealedObjectStore sealedStore)
        {
            await sealedStore.DisposeAsync().ConfigureAwait(false);
        }

        if (Interlocked.Exchange(ref _ownedStore, null) is { } owned)
        {
            await owned.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The store at <paramref name="location"/>: a view of the session's store for a location, which
    /// the session owns, or a <see cref="FileObjectStore"/> over a local directory, which the handle owns.
    /// </summary>
    private static (IObjectStore Store, IObjectStore? Owned) StoreAt(string location, DatasetOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(location);
        if (ObjectStoreRegistry.IsLocation(location))
        {
            return (options.Session.Options.ObjectStores.Resolve(location), null);
        }

        FileObjectStore directory = new FileObjectStore(location);
        return (directory, directory);
    }

    /// <summary>
    /// Hands the handle the store it made and the location it was opened at, or disposes the store
    /// when no handle came of it.
    /// </summary>
    private static async ValueTask<VortexDataset> OwningAsync(ValueTask<VortexDataset> opening, IObjectStore? owned, string location)
    {
        try
        {
            VortexDataset dataset = await opening.ConfigureAwait(false);
            dataset._ownedStore = owned;
            dataset._location = location;
            return dataset;
        }
        catch
        {
            if (owned is not null)
            {
                await owned.DisposeAsync().ConfigureAwait(false);
            }

            throw;
        }
    }

    /// <summary>
    /// Creates a dataset under a store prefix — everything under its keys — and returns a handle on
    /// version 1.
    /// </summary>
    internal static ValueTask<VortexDataset> CreateAsync(
        IObjectStore store,
        DType schema,
        DatasetOptions? options = null,
        CancellationToken cancellationToken = default) =>
        CreateAtAsync(store, schema, options, null, cancellationToken);

    /// <summary>
    /// <see cref="CreateAsync(IObjectStore, DType, DatasetOptions?, CancellationToken)"/>, the
    /// creation's commit tagged with the location the caller named, when it named one.
    /// </summary>
    private static async ValueTask<VortexDataset> CreateAtAsync(
        IObjectStore store,
        DType schema,
        DatasetOptions? options,
        string? location,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        RequireOrderedListing(store);
        RequireConditionalCreation(store);
        options ??= new DatasetOptions();
        if (options.LockedStore && (store.Capabilities & ObjectStoreCapabilities.KeepsLocks) == 0)
        {
            throw new NotSupportedException(
                $"A dataset created for a locked store needs a store that keeps locks, and {store.GetType().Name} keeps none " +
                "(ObjectStoreCapabilities.KeepsLocks): its objects would be deleted while it believes them kept.");
        }

        if (options.LockedStore && options.ErasureDelay is not null)
        {
            throw new ArgumentException(
                "A dataset on a locked store keeps every byte it writes for the lock's term, so it cannot erase deleted rows within a delay.",
                nameof(options));
        }

        // Encrypted when asked, and when the session seals what it writes or refuses plaintext, whose
        // own open would refuse a plain dataset.
        bool encrypted = options.Encrypted || options.Session.Seals || options.Session.Options.RefusePlaintext;
        if (encrypted && options.Session.Keys is null)
        {
            throw new InvalidOperationException(
                "An encrypted dataset is created in a session whose keyring seals it: set VortexSessionOptions.Keyring on DatasetOptions.Session.");
        }

        store = SealedObjectStore.Over(store, options.Session);
        if (encrypted)
        {
            await ((SealedObjectStore)store).StartSealedAsync(cancellationToken).ConfigureAwait(false);
        }

        ClusteringKey? key = global::Vorticity.Dataset.ClusteringKey.Declared(options.ClusteringKey, schema, options.AcceptsNullableKey);
        DatasetFeatures features = options.ErasureDelay is null ? DatasetFeatures.None : DatasetFeatures.ErasureDelay;
        if (options.Clustering is { } clustering)
        {
            // Resolved once here, so that a value the key's first column does not take is refused before anything is written.
            _ = ValueCut.Of(clustering, key);
            _ = ValueCut.OfBlocks(clustering, key);
            features |= DatasetFeatures.Clustering;
        }

        string[] declared = [];
        if (options.Identifiers is { Count: > 0 } identifiers)
        {
            declared = RequireIdentifiers(identifiers, schema, nameof(DatasetOptions.Identifiers));
            features |= DatasetFeatures.Identifiers;
        }

        string[] shredded = [];
        if (options.ShreddedPaths is { Count: > 0 } paths)
        {
            shredded = RequireShreddedPaths(paths, schema, nameof(DatasetOptions.ShreddedPaths));
            features |= DatasetFeatures.ShreddedPaths;
        }

        CommitHeader template = new CommitHeader
        {
            Version = 1,
            Seed = DatasetOptions.SeedOf(options),
            Schema = DTypeProtobuf.Serialize(schema),
            ClusteringKey = [.. options.ClusteringKey ?? []],
            Retention = options.Retention,
            Compaction = options.LockedStore && options.Compaction.Fanout == 0
                ? options.Compaction with { Fanout = DatasetOptions.LockedFanout }
                : options.Compaction,
            LockedStore = options.LockedStore,
            ErasureDelay = options.ErasureDelay,
            Identifiers = declared,
            ShreddedPaths = shredded,
            Clustering = options.Clustering,
            Features = features,
            Chunker = new ChunkerSettings(
                ProllyBoundaryRule.DefaultMinBytes,
                ProllyBoundaryRule.DefaultTargetBytes,
                ProllyBoundaryRule.DefaultMaxBytes),
        };

        // One listing, which finds no version, and one creation, which another creation may beat to
        // the number: the loser is told so, rather than rebased onto the winner's dataset. The handle
        // reads the version as its commit wrote it, with nothing read back.
        CommitResult created = await DatasetCommitter
            .CommitAsync(
                store,
                [],
                Commit(options, template) with { OnlyFirst = true, Kind = CommitKind.Create, Location = location },
                cancellationToken)
            .ConfigureAwait(false);
        RequireEncryption(store, options);
        return new VortexDataset(store, options.For(created.Commit.Header), created.Version, created.Commit, created.KnownAt);
    }

    /// <summary>Applies operations, moves this handle to the version they created, and returns it.</summary>
    internal async ValueTask<ulong> ApplyAsync(
        IReadOnlyList<DatasetOperation> operations, CommitKind kind, CancellationToken cancellationToken = default)
    {
        CommitResult result = await CommitAsync(operations, kind, cancellationToken).ConfigureAwait(false);
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
    internal ValueTask<CommitResult> CommitAsync(IReadOnlyList<DatasetOperation> operations, CancellationToken cancellationToken) =>
        CommitAsync(operations, CommitKind.Replace, cancellationToken);

    /// <summary>
    /// Applies operations made for <paramref name="kind"/>, which the commit's telemetry names,
    /// reports what the commit made of each of them, and moves this handle on.
    /// </summary>
    internal ValueTask<CommitResult> CommitAsync(
        IReadOnlyList<DatasetOperation> operations, CommitKind kind, CancellationToken cancellationToken) =>
        CommitAsync(operations, kind, null, null, cancellationToken);

    /// <summary>
    /// <see cref="CommitAsync(IReadOnlyList{DatasetOperation}, CommitKind, CancellationToken)"/>, for
    /// rows that take the source ranges <paramref name="consumed"/> into account, or for a commit that
    /// forgets the sources <paramref name="forgotten"/>.
    /// </summary>
    /// <exception cref="SourceOffsetConflictException">A range does not start where the dataset counts its source up to.</exception>
    internal async ValueTask<CommitResult> CommitAsync(
        IReadOnlyList<DatasetOperation> operations,
        CommitKind kind,
        IReadOnlyList<SourceRange>? consumed,
        IReadOnlyCollection<string>? forgotten,
        CancellationToken cancellationToken)
    {
        RequireConditionalCreation(_store);
        DatasetSnapshot held = Snapshot;
        CommitResult result;
        try
        {
            result = await DatasetCommitter
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
                        Kind = kind,
                        Location = _location,
                        Consumed = consumed,
                        Forgotten = forgotten,
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (SourceOffsetConflictException conflict) when (conflict.Held is { } decided)
        {
            // The handle reads the version the conflict was decided against, whose offsets the
            // caller works its ranges out again from.
            MoveTo(conflict.Version, decided, conflict.KnownAt);
            throw;
        }

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
        CommitResult result = await CommitAsync([new DatasetOperation.Repack(versions)], CommitKind.Repack, cancellationToken).ConfigureAwait(false);
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

    /// <summary>The objects of level 0 this node read through keys of their variant columns, shredded in memory once.</summary>
    internal LevelZeroMemo LevelZero { get; } = new LevelZeroMemo();

    /// <summary>
    /// The deepest level's root and the tree of rollups' root as this handle's last <see cref="RollUpAsync"/> left them: a
    /// call that finds both where they were has nothing to sum, and walks nothing.
    /// </summary>
    internal (PageReference Bottom, PageReference Rollups)? RolledUp { get; set; }

    /// <summary>
    /// The store's reader of an object read with no open of it, which the caller disposes: a sealed object's
    /// keeps its trailer and the frames of the regions its table of contents declares in the session's synopsis
    /// cache, the bytes the store holds.
    /// </summary>
    private ValueTask<ISegmentReader> OpenFooterlessAsync(ObjectEntry entry, Indexes.ObjectContents contents, CancellationToken cancellationToken) =>
        SealedObjectStore.OpenDataAsync(
            _store, entry.Key, entry.Uid, entry.Bytes, cancellationToken,
            _store is SealedObjectStore { Seals: true } ? SealedSynopsisCache.Over(Session.Options.SynopsisCache, entry.Uid, contents) : null);

    /// <summary>
    /// The reader of an object read with no open of it, from the store's reader <paramref name="source"/>: the
    /// session's, through the session's synopsis cache for the regions the object's table of contents declares
    /// when the object is plain. A sealed object's reader decrypts what its cache keeps, which holds no plaintext.
    /// </summary>
    private ISegmentReader Footerless(ISegmentReader source, ObjectEntry entry, Indexes.ObjectContents contents) =>
        SessionReader.Wrap(
            source is ObjectStoreSource ? SynopsisCacheReader.Over(source, Session.Options.SynopsisCache, entry.Uid, contents) : source,
            Session);

    /// <summary>
    /// Whether an index on the column of a field of <paramref name="schema"/>'s root is one <paramref name="filter"/> may
    /// read, by the columns it names, their first field from the root; none on the root itself, the slices' chunks,
    /// whose runs a lookup parsed at every question for nothing; null, every one, when a name is none of the schema's or
    /// past the 64th field.
    /// </summary>
    private static Func<uint, bool>? ColumnsOf(VortexExpr filter, DType schema)
    {
        HashSet<string> named = new HashSet<string>(StringComparer.Ordinal);
        filter.CollectFields(named);
        ulong roots = 0;
        foreach (string path in named)
        {
            int field = schema.IndexOfField(path);
            if (field < 0 && path.IndexOf('.', StringComparison.Ordinal) is int dot and > 0)
            {
                field = schema.IndexOfField(path[..dot]);
            }

            if (field is < 0 or >= 64)
            {
                return null;
            }

            roots |= 1UL << field;
        }

        return column => column < 64 && ((roots >> (int)column) & 1) != 0;
    }

    /// <summary>
    /// The rows of the object <paramref name="entry"/> names that its locating indexes, its identifiers'
    /// runs among them, leave for <paramref name="filter"/>, read with no open of the object: its index
    /// directory beside every column's chunk directory, then the runs' segments that may hold the values,
    /// then the chunks of the blocks left, three round trips cold. The rows of a block a run could not rule
    /// out are all returned: the caller keeps those the filter holds for. Null when the entry carries no
    /// table of contents this library reads, or the object no locating index the filter asks.
    /// </summary>
    /// <param name="entry">The object.</param>
    /// <param name="filter">What the rows are found for.</param>
    /// <param name="columns">The fields of the object's schema the rows are read for, the filter's among them; null for every one.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    internal async ValueTask<List<RecordBatch>?> LookupAsync(ObjectEntry entry, VortexExpr filter, int[]? columns, CancellationToken cancellationToken)
    {
        if (entry.Contents is not { DirectoryRegion: { } region } contents)
        {
            return null;
        }

        ISegmentReader source = await OpenFooterlessAsync(entry, contents, cancellationToken).ConfigureAwait(false);
        await using (source.ConfigureAwait(false))
        {
            ISegmentReader reader = Footerless(source, entry, contents);
            if (Indexes.SliceReader.Open(reader, null, contents) is not { } opened)
            {
                return null;
            }

            using Indexes.SliceReader slices = opened;

            // The directory's entries on the columns the filter reads alone: its runs' segments were a list each, every
            // index of the object parsed at every lookup, three fifths of what a question that leaves an identifier out
            // allocated. Read alone, those of the locating indexes, when the contents name them apart: the whole directory
            // of a day's object, when it named every chunk of the synopsis tables of the time, was 11.5 MB a lookup read for a
            // few runs.
            DType schema = contents.SchemaType;
            Func<uint, bool>? indexed = ColumnsOf(filter, schema);
            List<Indexes.IndexSegment>? named = Named(contents, indexed);
            if (named is { Count: 0 })
            {
                return null;
            }

            // The first round trip: those entries, or the directory, beside every column's chunk directory.
            Task<Indexes.IndexDirectory?> read = named is null ? WholeAsync() : EntriesAsync(named);

            // The chunk directories of the columns the rows are read for: each read and parsed for every column, at every
            // lookup, was a fifth of what a question that leaves an identifier out allocated.
            Task[] chunks = new Task[columns?.Length ?? schema.FieldCount];
            for (int c = 0; c < chunks.Length; c++)
            {
                chunks[c] = slices.LoadDirectoryAsync(columns?[c] ?? c, cancellationToken).AsTask();
            }

            Indexes.IndexDirectory? directory;
            try
            {
                directory = await read.ConfigureAwait(false);
            }
            catch
            {
                // The chunk directories are read into the slice reader the method lets go on its way out: they end first.
                await ((Task)Task.WhenAll(chunks)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                throw;
            }

            await Task.WhenAll(chunks).ConfigureAwait(false);
            if (directory is null)
            {
                return null;
            }

            // The second: the runs' segments that may hold a value the filter asks.
            Indexes.IndexEntry? located = null;
            foreach (Indexes.IndexEntry index in directory!.Entries)
            {
                if (index.Kind is Indexes.IndexKinds.SortedRuns or Indexes.IndexKinds.PostingsBlocks or Indexes.IndexKinds.PostingsHashed)
                {
                    located = index;
                    break;
                }
            }

            Indexes.FooterlessIndexes host = new Indexes.FooterlessIndexes(reader, schema, directory, [.. contents.Encodings]);
            long blockRows = (long)(located?.BlockLength ?? 0);
            if (blockRows <= 0
                || await Indexes.KeyIndexPruner.BuildAsync(host, filter, blockRows, cancellationToken).ConfigureAwait(false) is not { } pruner)
            {
                return null;
            }

            Compute.BlockMask live = new Compute.BlockMask(host.RowCount, blockRows);
            await pruner.RefineAsync(host, live, cancellationToken).ConfigureAwait(false);

            // The third: every run of blocks left, their chunks together, a chunk two runs share read once.
            List<Indexes.TimeSlice> ranges = [];
            ReadOnlySpan<ulong> words = live.Words;
            int block = 0;
            while (block < live.BlockCount)
            {
                if (((words[block >> 6] >> (block & 63)) & 1) == 0)
                {
                    block++;
                    continue;
                }

                int first = block;
                while (block < live.BlockCount && ((words[block >> 6] >> (block & 63)) & 1) != 0)
                {
                    block++;
                }

                long start = first * blockRows;
                long end = Math.Min(block * blockRows, host.RowCount);
                ranges.Add(new Indexes.TimeSlice(0, 1, start, end - start));
            }

            return [.. await slices.ReadSpansAsync(ranges, columns, cancellationToken).ConfigureAwait(false)];

            // The whole directory, its entries on the filter's columns kept.
            async Task<Indexes.IndexDirectory?> WholeAsync()
            {
                using Buffers.SegmentOwner bytes = await reader.ReadAsync(
                    new Serialization.Schemas.SegmentSpec(region.Offset, region.Length, region.AlignmentExponent, 0, 0), cancellationToken).ConfigureAwait(false);
                return region.Holds(bytes.Buffer.Span)
                    && Indexes.IndexDirectory.TryParse(bytes.Buffer.Span, Indexes.IndexDirectory.UnknownRows, region.Offset, indexed, out Indexes.IndexDirectory? whole, out _)
                    ? whole
                    : null;
            }

            // The entries alone, a directory of them over the object's rows: each checked against its own checksum, one that
            // fails costing itself, as in a directory read whole.
            async Task<Indexes.IndexDirectory?> EntriesAsync(List<Indexes.IndexSegment> regions)
            {
                using SegmentRequestSet requests = new SegmentRequestSet(regions.Count);
                int[] slots = new int[regions.Count];
                for (int i = 0; i < regions.Count; i++)
                {
                    slots[i] = requests.Add(new Serialization.Schemas.SegmentSpec(regions[i].Offset, regions[i].Length, regions[i].AlignmentExponent, 0, 0));
                }

                await reader.ReadManyAsync(requests, cancellationToken).ConfigureAwait(false);
                List<Indexes.IndexEntry> kept = new List<Indexes.IndexEntry>(regions.Count);
                for (int i = 0; i < regions.Count; i++)
                {
                    ReadOnlySpan<byte> bytes = requests.GetBuffer(slots[i]).Span;
                    if (regions[i].Holds(bytes) && Indexes.IndexDirectory.TryParseEntry(bytes, region.Offset, out Indexes.IndexEntry? index))
                    {
                        kept.Add(index);
                    }
                }

                return new Indexes.IndexDirectory((ulong)entry.PhysicalRows, 0, Indexes.WritePolicy.None, kept);
            }
        }
    }

    /// <summary>
    /// The regions of the entries of the locating indexes on the columns <paramref name="indexed"/> keeps, every one when it
    /// is null, that <paramref name="contents"/> name (<see cref="Indexes.ObjectContents.Locating"/>); null when they name
    /// none, and a lookup reads the whole directory.
    /// </summary>
    private static List<Indexes.IndexSegment>? Named(Indexes.ObjectContents contents, Func<uint, bool>? indexed)
    {
        if (contents.Locating is not { Count: > 0 } locating)
        {
            return null;
        }

        List<Indexes.IndexSegment> regions = [];
        for (int i = 0; i < locating.Count; i++)
        {
            if (indexed is null || indexed(locating[i].Column))
            {
                regions.Add(locating[i].Region);
            }
        }

        return regions;
    }

    /// <summary>The synopses this handle read, its objects' column sheets, counts and sketches, by object and region (<see cref="ReadSynopsesAsync"/>).</summary>
    private readonly SynopsisMemo _synopses = new SynopsisMemo();

    /// <summary>The partials this handle learned of its objects' families, kept in memory and in the session's cache (<see cref="LearnedPartials"/>).</summary>
    internal LearnedPartials Partials { get; } = new LearnedPartials();

    /// <summary>The batches where the filters this handle evaluated on its objects' rows kept a row (<see cref="PredicatePages"/>).</summary>
    internal PredicatePages Predicates { get; } = new PredicatePages();

    /// <summary>A synopsis read from the bytes of the region at <paramref name="position"/> of those asked, and the bytes it holds in memory.</summary>
    private delegate (object Synopsis, long Bytes) SynopsisReader(int position, ReadOnlySpan<byte> bytes);

    /// <summary>
    /// The sheets of columns <paramref name="fields"/> of the object <paramref name="entry"/> names, by their position in
    /// <paramref name="fields"/>, read with no open of the object: the regions its entry's table of contents names, in
    /// one round trip, none once the session's cache holds them, and none once this handle read them. A column the object
    /// keeps no sheet of, or an object with no table of contents this library reads, has none.
    /// </summary>
    internal async ValueTask<Indexes.ColumnSheet?[]> ReadSheetsAsync(ObjectEntry entry, IReadOnlyList<int> fields, CancellationToken cancellationToken)
    {
        Indexes.ColumnSheet?[] sheets = new Indexes.ColumnSheet?[fields.Count];
        if (entry.Contents is not { Sheets.Count: > 0 } contents)
        {
            return sheets;
        }

        Indexes.IndexSegment?[] regions = new Indexes.IndexSegment?[fields.Count];
        for (int i = 0; i < fields.Count; i++)
        {
            regions[i] = contents.SheetOf(fields[i]);
        }

        await ReadSynopsesAsync(entry, contents, regions, sheets, static (_, bytes) => (Indexes.ColumnSheet.From(bytes), bytes.Length), cancellationToken)
            .ConfigureAwait(false);
        return sheets;
    }

    /// <summary>
    /// The counts of column <paramref name="field"/> of the object <paramref name="entry"/> names, and when
    /// <paramref name="sketches"/> asks their sketches, read with no open of the object: the regions its entry's table of
    /// contents names, side by side in one round trip, none once this handle read them. A column the object keeps no
    /// counts or sketches of, or an object with no table of contents this library reads, has none.
    /// </summary>
    internal async ValueTask<(Indexes.ColumnCounts? Counts, Indexes.ColumnSketches? Sketches)> ReadCountsAsync(
        ObjectEntry entry, int field, bool sketches, CancellationToken cancellationToken)
    {
        if (entry.Contents is not { Counts.Count: > 0 } contents)
        {
            return (null, null);
        }

        object?[] read = new object?[2];
        Indexes.IndexSegment?[] regions = [contents.CountsOf(field), sketches ? contents.SketchesOf(field) : null];
        await ReadSynopsesAsync(entry, contents, regions, read, CountsOrSketches, cancellationToken).ConfigureAwait(false);
        return (read[0] as Indexes.ColumnCounts, read[1] as Indexes.ColumnSketches);
    }

    /// <summary>
    /// The counts of every column the object <paramref name="entry"/> names keeps them of, by the column's place, read as
    /// <see cref="ReadCountsAsync"/> reads one's, side by side in one round trip; null when it keeps none, or one of them
    /// does not read.
    /// </summary>
    internal async ValueTask<IReadOnlyList<(int Field, Indexes.ColumnCounts Counts)>?> ReadEveryCountsAsync(
        ObjectEntry entry, CancellationToken cancellationToken)
    {
        if (entry.Contents is not { Counts.Count: > 0 } contents)
        {
            return null;
        }

        Indexes.IndexSegment?[] regions = new Indexes.IndexSegment?[contents.Counts.Count];
        for (int i = 0; i < regions.Length; i++)
        {
            regions[i] = contents.Counts[i].Region;
        }

        object?[] read = new object?[regions.Length];
        await ReadSynopsesAsync(entry, contents, regions, read, static (_, bytes) => CountsOrSketches(0, bytes), cancellationToken).ConfigureAwait(false);
        (int Field, Indexes.ColumnCounts Counts)[] counted = new (int Field, Indexes.ColumnCounts Counts)[regions.Length];
        for (int i = 0; i < regions.Length; i++)
        {
            if (read[i] is not Indexes.ColumnCounts counts)
            {
                return null;
            }

            counted[i] = (contents.Counts[i].Field, counts);
        }

        return counted;
    }

    /// <summary>
    /// The blocks of the object <paramref name="entry"/> names (<see cref="Indexes.BlockTable"/>), on its key's first
    /// column <paramref name="field"/>, read with no open of the object: the region its entry's table of contents names, none
    /// once this handle read it. An object that keeps none, or whose table of contents this library does not read, has none.
    /// </summary>
    internal async ValueTask<Indexes.BlockTable?> ReadBlocksAsync(ObjectEntry entry, int field, CancellationToken cancellationToken)
    {
        if (entry.Contents is not { Blocks.Count: > 0 } contents)
        {
            return null;
        }

        object?[] read = new object?[1];
        await ReadSynopsesAsync(entry, contents, [contents.BlocksOf(field)], read, static (_, bytes) => Indexes.BlockTable.From(bytes) is { } table
            ? (table, table.Bytes)
            : throw new VortexFormatException("The blocks of an object do not decode as a table of blocks."), cancellationToken).ConfigureAwait(false);
        return read[0] as Indexes.BlockTable;
    }

    /// <summary>
    /// Where the values of columns <paramref name="fields"/> of the object <paramref name="entry"/> names live
    /// (<see cref="Indexes.ColumnWhere"/>), and with <paramref name="stats"/> each one's statistics over the object's blocks
    /// (<see cref="Indexes.ColumnBlocks"/>), by their position in <paramref name="fields"/>, read with no open of the object:
    /// the regions its entry's table of contents names, side by side in one round trip, a column's statistics right after
    /// its index, none once this handle read them. A column the object keeps none of has none.
    /// </summary>
    internal async ValueTask<(Indexes.ColumnWhere? Where, Indexes.ColumnBlocks? Blocks)[]> ReadBlockColumnsAsync(
        ObjectEntry entry, IReadOnlyList<int> fields, bool stats, CancellationToken cancellationToken)
    {
        (Indexes.ColumnWhere?, Indexes.ColumnBlocks?)[] columns = new (Indexes.ColumnWhere?, Indexes.ColumnBlocks?)[fields.Count];
        if (entry.Contents is not { BlockStats.Count: > 0 } contents)
        {
            return columns;
        }

        object?[] read = new object?[2 * fields.Count];
        Indexes.IndexSegment?[] regions = new Indexes.IndexSegment?[read.Length];
        for (int i = 0; i < fields.Count; i++)
        {
            regions[2 * i] = contents.WhereOf(fields[i]);
            regions[(2 * i) + 1] = stats ? contents.BlockStatsOf(fields[i]) : null;
        }

        await ReadSynopsesAsync(entry, contents, regions, read, WhereOrBlocks, cancellationToken).ConfigureAwait(false);
        for (int i = 0; i < fields.Count; i++)
        {
            columns[i] = (read[2 * i] as Indexes.ColumnWhere, read[(2 * i) + 1] as Indexes.ColumnBlocks);
        }

        return columns;
    }

    /// <summary>Where a column's values live, at an even position, or its statistics over the blocks, at an odd one, and the bytes they hold in memory.</summary>
    private static (object Synopsis, long Bytes) WhereOrBlocks(int position, ReadOnlySpan<byte> bytes)
    {
        if (position % 2 == 0)
        {
            Indexes.ColumnWhere where = Indexes.ColumnWhere.From(bytes)
                ?? throw new VortexFormatException("Where an object's values live does not decode as an index of them.");
            return (where, where.Bytes);
        }

        Indexes.ColumnBlocks blocks = Indexes.ColumnBlocks.From(bytes)
            ?? throw new VortexFormatException("A column's statistics over an object's blocks do not decode as statistics.");
        return (blocks, blocks.Bytes);
    }

    /// <summary>The counts, at the first position, or their sketches, at the second, and the bytes they hold in memory.</summary>
    private static (object Synopsis, long Bytes) CountsOrSketches(int position, ReadOnlySpan<byte> bytes)
    {
        if (position == 0)
        {
            Indexes.ColumnCounts counts = Indexes.ColumnCounts.From(bytes);
            return (counts, counts.Bytes);
        }

        Indexes.ColumnSketches sketches = Indexes.ColumnSketches.From(bytes);
        return (sketches, sketches.Bytes);
    }

    /// <summary>
    /// Reads into <paramref name="synopses"/>, by their position, the synopses of <paramref name="regions"/> of the object
    /// <paramref name="entry"/> names, each as <paramref name="reader"/> reads its bytes: those this handle read before
    /// from its memo, the others with no open of the object, side by side in one round trip, none once the session's cache
    /// holds them, and kept. A region null, or whose bytes fail their checksum, reads none.
    /// </summary>
    private ValueTask ReadSynopsesAsync(
        ObjectEntry entry, Indexes.ObjectContents contents, Indexes.IndexSegment?[] regions, object?[] synopses, SynopsisReader reader,
        CancellationToken cancellationToken) =>
        Recalled(entry.Uid, regions, synopses) == 0 ? default : ReadUnheldAsync(entry, contents, regions, synopses, reader, cancellationToken);

    /// <summary>
    /// <see cref="ReadSynopsesAsync"/>'s regions its memo does not hold, apart so that a call that reads none builds no
    /// opener: a closure over its parameters would be built as the call starts.
    /// </summary>
    private ValueTask ReadUnheldAsync(
        ObjectEntry entry, Indexes.ObjectContents contents, Indexes.IndexSegment?[] regions, object?[] synopses, SynopsisReader reader,
        CancellationToken cancellationToken) =>
        ReadRegionsAsync(
            entry.Uid, ct => OpenFooterlessAsync(entry, contents, ct), source => Footerless(source, entry, contents), regions, synopses, reader, cancellationToken);

    /// <summary>
    /// A region of a rollup's counts that a reader of some values reads by its header, then their chunks, holds more than
    /// this many chunks past its header: a smaller one is read whole, in the one request the header alone would take.
    /// </summary>
    internal const int PartialChunks = 4;

    /// <summary>The bit that keys a region's header in the memo apart from the region read whole, which starts where it does.</summary>
    private const ulong HeadKey = 1UL << 63;

    /// <summary>
    /// The counts of column <paramref name="field"/> a rollup (<see cref="RollupEntry"/>) keeps over its objects, and when
    /// <paramref name="sketches"/> asks their sketches, side by side in one round trip with no open, none once this handle
    /// read them: (null, null) for a column it keeps none of, or whose bytes fail a checksum. Its counts by bucket in
    /// place of its totals under <paramref name="buckets"/>. Under <paramref name="values"/>, the values a question reads
    /// alone, a large region (<see cref="ReadsInPart"/>) gives the groups of the chunks that hold them alone, read after its
    /// header (<see cref="ReadRollupValuesAsync"/>).
    /// </summary>
    internal async ValueTask<(Indexes.ColumnCounts? Counts, Indexes.ColumnSketches? Sketches)> ReadRollupCountsAsync(
        RollupEntry rollup, int field, bool sketches, IReadOnlyList<byte[]>? values, bool buckets, CancellationToken cancellationToken)
    {
        if (rollup.ColumnOf(field) is not { } column || column.Regions(buckets) is not ({ } counted, var sketched, _, _))
        {
            return (null, null);
        }

        if (ReadsInPart(column, sketches, values, buckets) && !_synopses.TryGet(rollup.Uid, counted.Offset, out Indexes.ColumnCounts? _))
        {
            return await ReadRollupValuesAsync(rollup, column, sketches, values!, buckets, cancellationToken).ConfigureAwait(false);
        }

        object?[] read = new object?[2];
        Indexes.IndexSegment?[] regions = [counted, sketches ? sketched : null];
        if (Recalled(rollup.Uid, regions, read, 0) > 0)
        {
            await ReadRollupRegionsAsync(rollup, regions, read, CountsOrSketches, 0, cancellationToken).ConfigureAwait(false);
        }

        return (read[0] as Indexes.ColumnCounts, read[1] as Indexes.ColumnSketches);
    }

    /// <summary>
    /// Whether a reader of <paramref name="values"/> alone reads the totals <paramref name="column"/>, or its counts by
    /// bucket under <paramref name="buckets"/>, by their header, then the chunks that hold them: counts of more than
    /// <see cref="PartialChunks"/> chunks, each chunk and the header checksummed, and their sketches too when
    /// <paramref name="sketches"/> asks them.
    /// </summary>
    internal static bool ReadsInPart(RollupColumn column, bool sketches, IReadOnlyList<byte[]>? values, bool buckets) =>
        values is not null && column.Regions(buckets) is ({ } counts, var sketched, int countsHead, int sketchesHead)
        && countsHead > 0 && counts.Length - countsHead > PartialChunks * Indexes.ColumnCounts.ChunkBytes
        && (!sketches || sketched is null || sketchesHead > 0);

    /// <summary>
    /// The groups of <paramref name="values"/> in a rollup's totals <paramref name="column"/>, or its counts by bucket under
    /// <paramref name="buckets"/>, and when <paramref name="sketches"/> asks their sketches: the headers side by side in a
    /// round trip, then the chunks that hold
    /// the values and their entries' sketches in another, each checked against its checksum, none once this handle read
    /// them. The counts hold those chunks' groups alone, their entries from 0, and the sketches as many entries; (null,
    /// null) when a header or a chunk fails its checksum.
    /// </summary>
    private async ValueTask<(Indexes.ColumnCounts? Counts, Indexes.ColumnSketches? Sketches)> ReadRollupValuesAsync(
        RollupEntry rollup, RollupColumn column, bool sketches, IReadOnlyList<byte[]> values, bool buckets, CancellationToken cancellationToken)
    {
        (Indexes.IndexSegment? regionOfCounts, Indexes.IndexSegment? regionOfSketches, int countsHead, int sketchesHead) = column.Regions(buckets);
        Indexes.IndexSegment counted = regionOfCounts!.Value;
        Indexes.IndexSegment? sketched = sketches ? regionOfSketches : null;
        try
        {
            object?[] heads = new object?[2];
            Indexes.IndexSegment?[] headRegions =
            [
                new Indexes.IndexSegment(counted.Offset, (uint)countsHead, 0),
                sketched is { } region ? new Indexes.IndexSegment(region.Offset, (uint)sketchesHead, 0) : null,
            ];
            if (Recalled(rollup.Uid, headRegions, heads, HeadKey) > 0)
            {
                await ReadRollupRegionsAsync(rollup, headRegions, heads, Directories, HeadKey, cancellationToken).ConfigureAwait(false);
            }

            if (heads[0] is not Indexes.CountsDirectory directory || (sketched is not null && heads[1] is not Indexes.SketchesDirectory)
                || (directory.Groups > 0 && directory.Chunks.Count == 0))
            {
                return (null, null);
            }

            // The chunks of the values asked, then those of the sketches of every entry they hold.
            Indexes.SketchesDirectory? sketchDirectory = heads[1] as Indexes.SketchesDirectory;
            List<int> chunks = Indexes.ColumnCounts.ChunksOf(directory, values, leading: false);
            List<int> sketchChunks = sketchDirectory is null ? [] : Indexes.ColumnSketches.ChunksOf(sketchDirectory, Indexes.ColumnCounts.EntriesOf(directory, chunks));
            Indexes.IndexSegment?[] pieces = new Indexes.IndexSegment?[chunks.Count + sketchChunks.Count];
            for (int i = 0; i < chunks.Count; i++)
            {
                Indexes.CountsChunk chunk = directory.Chunks[chunks[i]];
                pieces[i] = new Indexes.IndexSegment(counted.Offset + (ulong)(directory.HeaderBytes + chunk.Offset), (uint)chunk.Length, 0);
            }

            for (int i = 0; i < sketchChunks.Count; i++)
            {
                (int offset, int length, _, _) = sketchDirectory!.Chunks[sketchChunks[i]];
                pieces[chunks.Count + i] = new Indexes.IndexSegment(sketched!.Value.Offset + (ulong)(sketchDirectory.HeaderBytes + offset), (uint)length, 0);
            }

            object?[] read = new object?[pieces.Length];
            if (Recalled(rollup.Uid, pieces, read, 0) > 0)
            {
                await ReadRollupRegionsAsync(rollup, pieces, read, Raw, 0, cancellationToken).ConfigureAwait(false);
            }

            ReadOnlyMemory<byte>[] bytes = new ReadOnlyMemory<byte>[read.Length];
            for (int i = 0; i < read.Length; i++)
            {
                bytes[i] = read[i] as byte[] ?? throw new VortexFormatException("A chunk of a rollup's counts read no bytes.");
            }

            Indexes.ColumnCounts counts = Indexes.ColumnCounts.FromChunks(directory, chunks, bytes.AsSpan(0, chunks.Count).ToArray(), out int[] entries);
            return (counts, sketchDirectory is null ? null : Indexes.ColumnSketches.FromEntries(sketchDirectory, sketchChunks, bytes[chunks.Count..], entries));
        }
        catch (VortexFormatException)
        {
            // A header or a chunk that fails its checksum reads none, as a region read whole does.
            return (null, null);
        }
    }

    /// <summary>The directory of a rollup's totals, at the first position, or of their sketches, at the second.</summary>
    private static (object Synopsis, long Bytes) Directories(int position, ReadOnlySpan<byte> bytes) =>
        position == 0 ? (Indexes.ColumnCounts.ReadDirectory(bytes), bytes.Length) : (Indexes.ColumnSketches.ReadDirectory(bytes), bytes.Length);

    /// <summary>A chunk's bytes, which its directory checks once read.</summary>
    private static (object Synopsis, long Bytes) Raw(int position, ReadOnlySpan<byte> bytes) => (bytes.ToArray(), bytes.Length);

    /// <summary>
    /// The regions of the rollup <paramref name="rollup"/> its memo does not hold, apart as <see cref="ReadUnheldAsync"/> is,
    /// each as <paramref name="reader"/> reads it and kept under its offset with <paramref name="keyed"/>.
    /// </summary>
    private ValueTask ReadRollupRegionsAsync(
        RollupEntry rollup, Indexes.IndexSegment?[] regions, object?[] read, SynopsisReader reader, ulong keyed, CancellationToken cancellationToken) =>
        ReadRegionsAsync(
            rollup.Uid,
            ct => SealedObjectStore.OpenDataAsync(_store, rollup.Key, rollup.Uid, rollup.Bytes, ct, null),
            source => SessionReader.Wrap(source, Session),
            regions,
            read,
            reader,
            cancellationToken,
            keyed);

    /// <summary>
    /// Takes into <paramref name="synopses"/>, by their position, the regions <paramref name="regions"/> of the object of
    /// <paramref name="uid"/> this handle read before, from its memo under their offsets with <paramref name="keyed"/>, and
    /// leaves them null among the regions: the number of regions left to read.
    /// </summary>
    private int Recalled(UInt128 uid, Indexes.IndexSegment?[] regions, object?[] synopses, ulong keyed = 0)
    {
        int asked = 0;
        for (int i = 0; i < regions.Length; i++)
        {
            if (regions[i] is { } region && _synopses.TryGet(uid, region.Offset | keyed, out object? held))
            {
                synopses[i] = held;
                regions[i] = null;
            }

            asked += regions[i] is null ? 0 : 1;
        }

        return asked;
    }

    /// <summary>
    /// Reads into <paramref name="synopses"/>, by their position, the regions <paramref name="regions"/> of the object of
    /// <paramref name="uid"/> not null, those its memo does not hold (<see cref="Recalled"/>), each as
    /// <paramref name="reader"/> reads its bytes: side by side in one round trip through the reader <paramref name="open"/>
    /// gives and <paramref name="wrap"/> wraps, and kept under their offsets with <paramref name="keyed"/>. A region whose
    /// bytes fail their checksum reads none.
    /// </summary>
    private async ValueTask ReadRegionsAsync(
        UInt128 uid, Func<CancellationToken, ValueTask<ISegmentReader>> open, Func<ISegmentReader, ISegmentReader> wrap,
        Indexes.IndexSegment?[] regions, object?[] synopses, SynopsisReader reader, CancellationToken cancellationToken, ulong keyed = 0)
    {
        int asked = 0;
        foreach (Indexes.IndexSegment? region in regions)
        {
            asked += region is null ? 0 : 1;
        }

        ISegmentReader source = await open(cancellationToken).ConfigureAwait(false);
        await using (source.ConfigureAwait(false))
        {
            using SegmentRequestSet requests = new SegmentRequestSet(asked);
            int[] slots = new int[regions.Length];
            for (int i = 0; i < regions.Length; i++)
            {
                slots[i] = regions[i] is { } region
                    ? requests.Add(new Serialization.Schemas.SegmentSpec(region.Offset, region.Length, region.AlignmentExponent, 0, 0))
                    : -1;
            }

            await wrap(source).ReadManyAsync(requests, cancellationToken).ConfigureAwait(false);
            for (int i = 0; i < regions.Length; i++)
            {
                if (slots[i] >= 0 && regions[i]!.Value.Holds(requests.GetBuffer(slots[i]).Span))
                {
                    (object synopsis, long bytes) = reader(i, requests.GetBuffer(slots[i]).Span);
                    synopses[i] = synopsis;
                    _synopses.Keep(uid, regions[i]!.Value.Offset | keyed, synopsis, bytes);
                }
            }
        }
    }

    /// <summary>
    /// The path directory of variant column <paramref name="field"/> of the object <paramref name="entry"/>
    /// names, read with no open of the object: the region its entry's table of contents names, in one round
    /// trip, none once the session's cache holds it. Null when the entry carries no table of contents this
    /// library reads, or the column no directory.
    /// </summary>
    internal async ValueTask<Indexes.VariantDirectory?> ReadPathDirectoryAsync(ObjectEntry entry, int field, CancellationToken cancellationToken)
    {
        if (entry.Contents is not { } contents)
        {
            return null;
        }

        foreach ((int column, Indexes.IndexSegment region) in contents.PathDirectories ?? [])
        {
            if (column != field)
            {
                continue;
            }

            ISegmentReader source = await OpenFooterlessAsync(entry, contents, cancellationToken).ConfigureAwait(false);
            await using (source.ConfigureAwait(false))
            {
                using Buffers.SegmentOwner bytes = await Footerless(source, entry, contents)
                    .ReadAsync(new Serialization.Schemas.SegmentSpec(region.Offset, region.Length, region.AlignmentExponent, 0, 0), cancellationToken)
                    .ConfigureAwait(false);
                return region.Holds(bytes.Buffer.Span) ? Indexes.VariantDirectory.From(bytes.Buffer.Span) : null;
            }
        }

        return null;
    }

    /// <summary>
    /// The variant column <paramref name="field"/> of the slices of the object <paramref name="entry"/> names
    /// that may hold a value at <paramref name="key"/>, a top-level key, read with no open of the object: its
    /// path directory and the column's chunk directory together, then those slices' chunks together, two
    /// round trips cold, none once the session's cache holds them. Null when the entry carries no table of
    /// contents this library reads; empty when the object holds no value there.
    /// </summary>
    internal async ValueTask<List<RecordBatch>?> ReadPathAsync(ObjectEntry entry, int field, string key, CancellationToken cancellationToken)
    {
        if (entry.Contents is not { } contents)
        {
            return null;
        }

        ISegmentReader source = await OpenFooterlessAsync(entry, contents, cancellationToken).ConfigureAwait(false);
        await using (source.ConfigureAwait(false))
        {
            ISegmentReader reader = Footerless(source, entry, contents);
            if (Indexes.SliceReader.Open(reader, null, contents) is not { } opened)
            {
                return null;
            }

            using Indexes.SliceReader slices = opened;

            // The first round trip: the path directory beside the column's chunk directory.
            ValueTask<Indexes.VariantDirectory?> directory = ReadPathDirectoryAsync(entry, field, cancellationToken);
            Task chunks = slices.LoadDirectoryAsync(field, cancellationToken).AsTask();
            Indexes.VariantDirectory? paths;
            try
            {
                paths = await directory.ConfigureAwait(false);
            }
            catch
            {
                // The chunk directory is read into the slice reader the method lets go on its way out: it ends first.
                await ((Task)chunks).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                throw;
            }

            await chunks.ConfigureAwait(false);
            if (paths is null)
            {
                return null;
            }

            // The second: every slice the path may hold a value in, at once.
            ReadOnlySpan<Indexes.PathEntry> held = paths.Of([key]);
            List<int> wanted = [];
            for (int slice = 0; slice < slices.Slices.Slices.Count; slice++)
            {
                foreach (Indexes.PathEntry path in held)
                {
                    if (path.In(slice))
                    {
                        wanted.Add(slice);
                        break;
                    }
                }
            }

            Indexes.TimeSlice[] spans = new Indexes.TimeSlice[wanted.Count];
            for (int i = 0; i < spans.Length; i++)
            {
                spans[i] = slices.Slices.Slices[wanted[i]];
            }

            return [.. await slices.ReadSpansAsync(spans, [field], cancellationToken).ConfigureAwait(false)];
        }
    }

    /// <summary>
    /// The rows of <paramref name="slices"/> of the object <paramref name="entry"/> names, read with no open of
    /// the object through its table of contents <paramref name="contents"/>: per slice, in order, a batch of
    /// <paramref name="columns"/>, every column when null, counted from the object's first row, which the
    /// caller disposes. The columns' chunk directories, then every slice's chunks together, a chunk two
    /// slices share read once: two round trips cold, however many slices.
    /// </summary>
    internal async ValueTask<RecordBatch[]> ReadSlicesAsync(
        ObjectEntry entry, Indexes.ObjectContents contents, IReadOnlyList<Indexes.TimeSlice> slices, int[]? columns,
        CancellationToken cancellationToken)
    {
        ISegmentReader source = await OpenFooterlessAsync(entry, contents, cancellationToken).ConfigureAwait(false);
        await using (source.ConfigureAwait(false))
        {
            using Indexes.SliceReader reader = Indexes.SliceReader.Open(Footerless(source, entry, contents), null, contents)
                ?? throw new VortexFormatException($"The table of contents of object {entry.Key} names no chunk directory.");
            return await reader.ReadSpansAsync(slices, columns, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The rows strictly inside <paramref name="first"/> to <paramref name="end"/> of the object <paramref name="entry"/>
    /// names at which a chunk of one of <paramref name="columns"/> starts, read from its chunk directories with no open of it
    /// (<see cref="Indexes.SliceReader.ChunkStartsAsync"/>): where a span of its rows may be cut between lanes.
    /// </summary>
    internal async ValueTask<long[]> ChunkStartsAsync(
        ObjectEntry entry, Indexes.ObjectContents contents, int[] columns, long first, long end, CancellationToken cancellationToken)
    {
        ISegmentReader source = await OpenFooterlessAsync(entry, contents, cancellationToken).ConfigureAwait(false);
        await using (source.ConfigureAwait(false))
        {
            using Indexes.SliceReader reader = Indexes.SliceReader.Open(Footerless(source, entry, contents), null, contents)
                ?? throw new VortexFormatException($"The table of contents of object {entry.Key} names no chunk directory.");
            return await reader.ChunkStartsAsync(columns, first, end, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The rows of slice <paramref name="slice"/> of the object <paramref name="entry"/> names, of every column
    /// or of <paramref name="columns"/>, read with no open of the object: the chunk directories its entry's
    /// table of contents names, then the slice's chunks, in two round trips. Null when the entry carries no
    /// table of contents this library reads.
    /// </summary>
    internal async ValueTask<RecordBatch?> ReadSliceAsync(ObjectEntry entry, int slice, int[]? columns, CancellationToken cancellationToken)
    {
        if (entry.Contents is not { } contents)
        {
            return null;
        }

        ISegmentReader source = await OpenFooterlessAsync(entry, contents, cancellationToken).ConfigureAwait(false);
        await using (source.ConfigureAwait(false))
        {
            if (Indexes.SliceReader.Open(Footerless(source, entry, contents), null, contents) is not { } opened)
            {
                return null;
            }

            using Indexes.SliceReader reader = opened;
            return await reader.ReadSliceAsync(slice, columns, cancellationToken).ConfigureAwait(false);
        }
    }

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

        UInt128 hash = draft.Sink.ContentHash;

        // Read out of the chunks the sink still holds, before the put: the entry's bounds and the
        // smallest key its leaf is ordered by then cost no request at all. An object that went to the
        // store as it was written is read from there once put, its footer in a request or two: an object
        // past the part threshold took a request a part anyway. Should that read fail, the object is
        // left unnamed, as an abandoned job's are, for a vacuum to take.
        ObjectSummaries summaries;
        byte[] prefix;
        byte[] last;
        bool streamed = draft.Sink.IsStreamed;
        try
        {
            if (!streamed)
            {
                SourceReader held = new SourceReader(draft.Sink.Content(), ownsSource: true, _options.Session.Options.EnginePool);
                (summaries, prefix, last) = await DescribeAsync(held, firstRow, draft, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                (summaries, prefix, last) = (ObjectSummaries.Empty, [], []);
            }

            if (await draft.Sink.CommitAsync(cancellationToken).ConfigureAwait(false) != PutOutcome.Created)
            {
                throw new ObjectStoreException($"'{draft.Key}' was taken, which a fresh uid cannot be.");
            }
        }
        catch
        {
            // Not put: the chunks the sink holds go back to their pool, and an upload it streams is given up.
            draft.Sink.Discard();
            throw;
        }

        // What the store holds, sealed bytes included: what a head reports, and verification checks.
        long bytes = draft.Sink.StoredLength;
        if (streamed)
        {
            ISegmentReader stored = await SealedObjectStore.OpenDataAsync(_store, draft.Key, Uid(draft.Identity), bytes, cancellationToken).ConfigureAwait(false);
            (summaries, prefix, last) = await DescribeAsync(stored, firstRow, draft, cancellationToken, draft.Writer.TailBytes).ConfigureAwait(false);
        }
        ObjectEntry entry = new ObjectEntry(draft.Key, Uid(draft.Identity), rows, bytes, hash, summaries)
            .WithFooterBytes(draft.Writer.TailBytes)
            .WithTableOfContents(draft.Writer.TableOfContents)
            .WithLastKey(last);
        return new WrittenObject(entry, KeyOf(prefix, entry), draft.Writer.DataBytes);
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
    /// through the pages this handle kept from the versions before, then through the disk of the
    /// session's synopsis cache, where a handle opened earlier left the pages it read.
    /// </summary>
    private CommitPageSource PagesOf(ulong version, CommitObject commit)
    {
        CommitPageSource pages = new CommitPageSource(_store, _pageCache, PageDisk) { Reading = version };
        pages.Open(version, commit);
        return pages;
    }

    /// <summary>
    /// The session's synopsis cache, which keeps the tree pages the handle reads on the disk: null for none, and for a
    /// sealed dataset, whose pages stay off the disk, which holds no plaintext of it.
    /// </summary>
    private SynopsisCache? PageDisk => _store is SealedObjectStore { Seals: true } ? null : Session.Options.SynopsisCache;

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
            // last chunk is out. A writer that took no row is not: an object of no row is not put,
            // and the run a clustered dataset requires could not be built over nothing.
            if (!draft.Writer.IsFinished && draft.Writer.RowCount > 0)
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
    /// <param name="file">The object, open.</param>
    private ObjectSummaries Summaries(VortexFile file)
    {
        IReadOnlyList<ColumnSummary> columns = _options.SummaryColumns is { } declared
            ? ColumnSummaries.Of(file, declared)
            : ColumnSummaries.Of(file, _options.SummaryColumnLimit);
        if (Key is not { } clustering)
        {
            return ObjectSummaries.From(columns);
        }

        List<ColumnSummary> all = [];
        HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (ColumnSummary column in columns)
        {
            if (seen.Add(column.Path))
            {
                all.Add(column);
            }
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
    /// <param name="source">The object's bytes, held or in the store; the file opened over it owns it.</param>
    /// <param name="firstRow">The position of its first row, for a dataset without a clustering key.</param>
    /// <param name="draft">The object's draft, which says what walks it by key without a run: its rows in key order, or its slices, which is checked.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <param name="tailBytes">The bytes of its footer when they pass an open's first read, so that the open reads them at once; 0 otherwise.</param>
    private async ValueTask<(ObjectSummaries Summaries, byte[] Prefix, byte[] Last)> DescribeAsync(
        ISegmentReader source, long firstRow, ObjectDraft draft, CancellationToken cancellationToken, long tailBytes = 0)
    {
        VortexOpenOptions options = tailBytes > ObjectEntry.FirstReadBytes
            ? ObjectCache.OpenOptions with { InitialReadBytes = (int)Math.Min(tailBytes, int.MaxValue) }
            : ObjectCache.OpenOptions;
        VortexFile file = await VortexFile
            .OpenAsync(source, options, cancellationToken).ConfigureAwait(false);
        await using (file.ConfigureAwait(false))
        {
            if (draft.BySortedColumn)
            {
                await CheckSortedAsync(file, cancellationToken).ConfigureAwait(false);
            }

            ObjectSummaries summaries = Summaries(file);
            (byte[] prefix, byte[] last) = await BoundsAsync(file, summaries, firstRow, cancellationToken).ConfigureAwait(false);
            return (summaries, prefix, last);
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

    /// <summary>
    /// What orders an object's leaf, its smallest key or its position, and its largest key, which its entry states when
    /// its statistics do not bound the key (<see cref="ClusteringKey.StatesLastKey"/>): empty otherwise, without a key, or
    /// without a cursor to say it.
    /// </summary>
    private async ValueTask<(byte[] Prefix, byte[] Last)> BoundsAsync(
        VortexFile file, ObjectSummaries summaries, long firstRow, CancellationToken cancellationToken) =>
        Key is not { } clustering ? (PositionOf(firstRow), [])
        : clustering.StatesLastKey ? await clustering.BoundsAsync(file, summaries, cancellationToken).ConfigureAwait(false)
        : (await clustering.MinimumAsync(file, summaries, cancellationToken).ConfigureAwait(false), []);

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
