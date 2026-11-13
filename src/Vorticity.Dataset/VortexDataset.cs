using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Expressions;
using Vorticity.IO;
using Vorticity.RowEncoding;
using Vorticity.Scanning;
using Vorticity.Types;
using Vorticity.Types.Serialization;
using Vorticity.Types.Variants;

namespace Vorticity.Dataset;

/// <summary>
/// A data object that has reached the store, its tree key, what orders it then its uid, the bytes of its data, when its
/// writer started it, before a byte of it went to the store (<see cref="ObjectOutput.StartedAt"/>), and the data keys
/// its handle had sealed under then (<see cref="ObjectOutput.KeyGeneration"/>).
/// </summary>
internal readonly record struct WrittenObject(ObjectEntry Entry, ReadOnlyMemory<byte> Key, long DataBytes, DateTimeOffset StartedAt, long KeyGeneration);

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
        _objects = new ObjectCache(store, options.MaxOpenObjects, options.Session, options.MaxOpenObjectBytes);
        _pageCache = new PageCache(options.PageCacheBytes);
        Predicates = new PredicatePages(options.PredicatePageBytes);
        _snapshot = new DatasetSnapshot(commit, PagesOf(version, commit), _objects, knownAt, Costs);
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

    /// <summary>How the dataset lays out its rows, as its commits carry it; null when it keeps the order they arrive in.</summary>
    public DatasetLayout? Layout => Snapshot.Header.Layout;

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

    /// <summary>What the objects open between scans weigh, as <see cref="DatasetOptions.MaxOpenObjectBytes"/> bounds them.</summary>
    internal long OpenObjectBytes => _objects.HeldBytes;

    /// <summary>
    /// What a row of the dataset weighed decoded in the last merge this handle ran, over the batches its inputs held: what
    /// the next job's plan prices an input's batch by; 0 before any merge measured one.
    /// </summary>
    internal long DecodedRowBytes => Volatile.Read(ref _decodedRowBytes);

    private long _decodedRowBytes;

    /// <summary>Keeps <paramref name="bytes"/> as what a decoded row weighs, as a merge measured it.</summary>
    internal void LearnDecodedRowBytes(long bytes)
    {
        if (bytes > 0)
        {
            Volatile.Write(ref _decodedRowBytes, bytes);
        }
    }

    /// <summary>Its levels, where level 0 is the one an append lands in.</summary>
    internal DatasetLevels Levels => Snapshot.Levels;

    /// <summary>Where this version's tree pages are read from, for a caller walking the levels.</summary>
    internal IPageSource Pages => Snapshot.Pages;

    /// <summary>How many data keys this handle has sealed under (<see cref="SealedObjectStore.KeyGeneration"/>); 0 for a plain dataset.</summary>
    internal long KeyGeneration => _store is SealedObjectStore { Seals: true } sealedStore ? sealedStore.KeyGeneration : 0;

    /// <summary>The levels of level 0's tree: 0 when it is empty, 1 when one leaf page holds it all.</summary>
    internal int Depth => Snapshot.Levels[0].Depth;

    /// <summary>The seed every boundary of its trees is decided under.</summary>
    internal ulong Seed => Snapshot.Header.Seed;

    /// <summary>The compaction settings its header carries, zero-valued when it states none.</summary>
    internal CompactionSettings Compaction => Snapshot.Header.Compaction;

    /// <summary>Whether the dataset lives on a store that locks what it keeps, as the header carries it.</summary>
    internal bool LockedStore => Snapshot.Header.LockedStore;

    /// <summary>Its sort key, or null when it is ordered by arrival.</summary>
    internal DatasetKey? Key => Snapshot.Schema.Key;

    /// <summary>
    /// Creates a dataset in a store that holds none, and returns a handle on its first version.
    /// </summary>
    /// <param name="store">The store; the dataset is everything under its <c>commit/</c> and <c>data/</c> keys.</param>
    /// <param name="schema">The columns every object of the dataset holds.</param>
    /// <param name="options">The sort key, the retention and how objects are written; null for the defaults.</param>
    /// <param name="cancellationToken">Cancels the requests.</param>
    /// <returns>The handle, on version 1; the caller disposes it.</returns>
    /// <remarks>
    /// Two requests: a listing that finds no version, and the conditional creation of the first. A
    /// creation is exclusive: of several that race for one store, one creates the dataset and every
    /// other throws, rather than handing its caller a dataset of another's schema and settings.
    /// </remarks>
    /// <exception cref="ObjectStoreException">The store already holds a dataset, or another creation made it first.</exception>
    /// <exception cref="ArgumentException">A sort key column is not in the schema, or may hold a null.</exception>
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
    /// <param name="options">The sort key, the retention, how objects are written and the session; null for the defaults.</param>
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
    /// The bytes of an identifier's values a place of its hashed postings spans (spec C, 5): the chunk bytes of the dataset's
    /// objects, a mebibyte when they are chunked with the object, under <see cref="IdentifierLocation.Chunk"/>; none, a
    /// place a granule, under <see cref="IdentifierLocation.Granule"/>.
    /// </summary>
    internal long HashedUnitBytes => _options.IdentifierLocation == IdentifierLocation.Granule ? 0 : _options.ChunkBytes > 0 ? _options.ChunkBytes : 1L << 20;

    /// <summary>
    /// The blocks a place of the hashed postings of each of <paramref name="identifiers"/> spans in an object of
    /// <paramref name="schema"/>, as its writer places them (<see cref="Indexes.KeyRunOptions.HashedUnitBlocks"/>): by the
    /// width of a column's values, or of a variant key's class.
    /// </summary>
    internal int[] UnitBlocksOf(DType schema, IReadOnlyList<string> identifiers)
    {
        int[] units = new int[identifiers.Count];
        for (int i = 0; i < units.Length; i++)
        {
            int field = schema.IndexOfField(identifiers[i]);
            int width = field >= 0
                ? Indexes.KeyLayout.TryOf(schema.GetField(field), out Indexes.KeyLayout layout) ? layout.Width : 0
                : IdentifierPath(identifiers[i], schema) is { Class: Types.Variants.VariantClass.Integer } ? sizeof(long) : 0;
            units[i] = Indexes.KeyRunOptions.HashedUnitBlocks(width, HashedUnitBytes, _options.Write.BlockRows);
        }

        return units;
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
    /// <remarks>
    /// A writer learns of the new key when it next reads a commit, which its next commit does: a commit of
    /// objects begun before then, by a writer in flight or a handle idle since before the rekey, is refused
    /// with <see cref="DatasetIntegrityException"/>, since they may be sealed under the replaced key, and its
    /// rows written again go under the new one. The replaced key serves no version once every object sealed
    /// before the rekey is rewritten and a vacuum has passed the retention window.
    /// </remarks>
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
    /// The commit is read whole and checked from its preamble to its trailer first, then deleted by its
    /// key. A store that claims a key before it writes the object, as <see cref="FileObjectStore"/>
    /// does on a file system that can neither rename exclusively nor link files, lets a read meet a
    /// commit being written: the read
    /// waits for the writer's lock, but one that opens the key between the claim and that lock reads it
    /// torn, and the commit is deleted under its writer. Run this only while no writer commits to the
    /// dataset. A store that shows an object only once whole, as every other store here does, never
    /// lets it meet one. An encrypted dataset's commit is checked by its envelope, which needs no key:
    /// it is whole when it ends with the trailer its writer writes last.
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
    /// Asks <paramref name="question"/> over the values of the column the partition reads from <paramref name="from"/>
    /// up to <paramref name="to"/> part by part, a part a partition (<see cref="DatasetLayout.Partition"/>), the last
    /// part first unless <paramref name="descending"/> is false: each part's answer delivered once it and every part before
    /// it are in, with the share of the range they cover (<see cref="ValuePart{TValue, TAnswer}.Covered"/>).
    /// </summary>
    /// <typeparam name="TValue">The values of the partition's column: an instant, a date, an integer.</typeparam>
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
    /// The dataset declares no partition over a column, or one over a column whose next value no function tells, a text's
    /// or a float's.
    /// </exception>
    /// <exception cref="ArgumentException">The range holds no value the column stores.</exception>
    /// <remarks>
    /// A part's answer is final for its values, so a page that shows a range part by part shows the first part asked while
    /// the rest is read. Every object holds rows of one partition, so a part's question reads whole objects but at the
    /// range's ends. The parts are asked several at once, sixteen at most and no more than the session
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
    /// Starts a draft: rows in the dataset's schema, held by partition and written as objects sorted by the dataset's key
    /// (<see cref="DatasetWriter"/>).
    /// </summary>
    /// <returns>The draft; write its rows, then append it, or dispose it to abandon it.</returns>
    /// <exception cref="NotSupportedException">The store cannot create a key conditionally, which a commit needs: it hosts readers alone.</exception>
    public ObjectDraft StartObject()
    {
        // Refused before a row is taken, as the commit the rows wait for would be.
        RequireConditionalCreation(_store);
        DatasetSchema schema = Snapshot.Schema;
        return new ObjectDraft(this, new DatasetWriter(this, schema, _options.ShredOnAppend ? DeclaredShredding(schema) : null));
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
    /// orders the object by itself takes no run. An object keeps a table of contents with
    /// <paramref name="contents"/>, its sparse index declared as its rows come, and a job's the
    /// synopses of its columns with <paramref name="synopses"/>: their sheets, counts and statistics
    /// over its blocks. A rewrite announces with <paramref name="byteBound"/> the most bytes the object
    /// takes should it stream (<see cref="ObjectBound"/>).
    /// </summary>
    internal ObjectOutput StartObjectUnder(
        DatasetSchema schema, bool inKeyOrder = false, long rowBound = 0, bool contents = false, bool synopses = false,
        IReadOnlyDictionary<string, Types.Variants.VariantShredding>? shredded = null, IReadOnlyDictionary<string, int>? variantChunks = null,
        IReadOnlyDictionary<string, Indexes.VariantDirectory>? directories = null, long byteBound = -1, PartitionPostings? postings = null,
        bool countsPaths = false)
    {
        RequireConditionalCreation(_store);
        DateTimeOffset started = _options.TimeProvider.GetUtcNow();
        long generation = KeyGeneration;
        Guid identity = Guid.NewGuid();
        string key = CommitKey.ForData(identity.ToString("N", CultureInfo.InvariantCulture));
        ObjectSegmentSink sink = new ObjectSegmentSink(
            _store, key, _options.MaxObjectBytes, _options.Session.Options.MemoryPool, _options.StreamedObjectBytes, byteBound);

        VortexWriteOptions write = _options.Write.WithIdentity(identity) with
        {
            RowBound = rowBound,
            CarriesContents = contents,
            GatheredChunkBytes = _options.ChunkBytes,
            GatheredGroupRows = _options.GroupRows,

            // A job's object keeps a sheet of each column its table of contents names, when it is large enough for a scan
            // to read the sheets before it, and the counts of each column over its rows; an append's, which a job merges
            // soon, keeps none.
            ColumnSheetBytes = contents && synopses ? _options.SheetObjectBytes : null,
            Counted = contents && synopses,

            // Such an object, which a job writes in its key's order, in blocks, keeps each column's statistics over them.
            Granulated = contents && synopses,
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

        // Every path and class of the variant columns, which a job counted from the object's rows, or which the writer of an
        // append counts as it takes them (DatasetOptions.PathDirectoryOnAppend).
        if (directories is { Count: > 0 })
        {
            write = write with { VariantDirectories = directories };
        }
        else if (countsPaths)
        {
            write = write with { CountsPaths = true };
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

        // Each declared identifier's hashed postings, required and spared by the budget: a lookup of one value reads the objects that hold it and almost no other, one
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

        // The identifiers of an object the index of its partition covers hand their hashes to it, and the object keeps none;
        // where its columns' values lie, it hands too, and keeps.
        if (postings is not null)
        {
            write = write with { HashTaps = postings.TapsOf(Uid(identity)), Located = postings.LocatesOf(Uid(identity)) };
        }

        // Their places, units of granules that hold about a chunk's bytes of each (spec C, 5).
        write = write with { HashedUnitBytes = HashedUnitBytes };

        // The session's degree and its lanes, which every writer of the session shares.
        return new ObjectOutput(this, identity, key, sink, VortexFileWriter.Create(sink, schema.DType, write, _options.Session), started, generation);
    }

    /// <summary>
    /// Writes the rows of a draft from <see cref="StartObject"/> as objects, one a partition, puts them in the store and
    /// adds them to the dataset in one commit.
    /// </summary>
    /// <param name="draft">The draft; the rows its partitions hold are written here.</param>
    /// <param name="cancellationToken">Cancels the put and the commit.</param>
    /// <returns>
    /// The version the commit created; the current one when the draft holds no row and nothing was
    /// committed. The handle reads that version, or the later one a compaction of level 0 run after
    /// the commit created (<see cref="DatasetOptions.InlineCompactionBytes"/>), which
    /// <see cref="Version"/> then says.
    /// </returns>
    /// <remarks>
    /// The objects are created before the commit, so a crash between the two leaves objects no
    /// commit references, which vacuum collects. Without a sort key their rows follow every
    /// other object's.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The draft belongs to another handle, or was already committed or disposed.</exception>
    public async ValueTask<ulong> AppendAsync(ObjectDraft draft, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        List<WrittenObject> written = await WrittenAsync(draft, cancellationToken).ConfigureAwait(false);
        if (written.Count == 0)
        {
            return Version;
        }

        return await ApplyAsync(Added(written), CommitKind.Append, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The objects an append of <paramref name="draft"/> adds: the draft's own, written and put.</summary>
    private async ValueTask<List<WrittenObject>> WrittenAsync(ObjectDraft draft, CancellationToken cancellationToken)
    {
        Taken(draft);
        return await draft.Writer.FinishAsync(await EndAsync(cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Takes <paramref name="draft"/> for a commit, once.</summary>
    /// <exception cref="InvalidOperationException">The draft belongs to another handle, or was already committed or disposed.</exception>
    private void Taken(ObjectDraft draft)
    {
        if (!ReferenceEquals(draft.Owner, this))
        {
            throw new InvalidOperationException("The draft was started by another dataset handle.");
        }

        draft.Take();
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
            operations[i] = new DatasetOperation.AddObject(written[i].Key, written[i].Entry)
            {
                FirstPutAt = written[i].StartedAt,
                KeyGeneration = written[i].KeyGeneration,
            };
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

        return await ApplyAsync(Added(written), CommitKind.Append, cancellationToken).ConfigureAwait(false);
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

                // Its tree key leads with its first row's key, or its partition and its position when the dataset has no key.
                (byte[] First, byte[] Last)? keys = await CheckLayoutAsync(file, schema, objectKey, cancellationToken).ConfigureAwait(false);
                entry = new ObjectEntry(
                    objectKey, Identity(file), file.RowCount, head.Length, UInt128.Zero, Summaries(file))
                    .WithFooterBytes(file.TailBytes)
                    .WithArrival(Math.Max(_options.TimeProvider.GetUtcNow().ToUnixTimeSeconds(), 1));
                if (keys is { } bounds && schema.Keyed)
                {
                    entry = entry.WithLastKey(bounds.Last);
                    treeKey = KeyOf(bounds.First, entry);
                }
                else
                {
                    treeKey = keys is { } partition
                        ? KeyOf([.. partition.First, .. PositionOf(end)], entry)
                        : await TreeKeyAsync(file, entry, end, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        return await ApplyAsync(
            [new DatasetOperation.AddObject(treeKey, entry)], CommitKind.Import, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Refuses a file whose rows are not one partition's, sorted by the dataset's key with nulls last, as every object's
    /// are: the columns the partition and the key read, read once as the dataset's, and each row's key of the tree worked
    /// out and compared with the row's before. The keys of its first and last rows, or its partition alone when the dataset
    /// has no key; null when the layout states neither, or the file holds no row. Its sparse index waits for its first
    /// merge, which writes one.
    /// </summary>
    /// <exception cref="ArgumentException">The file holds rows of several partitions, or rows out of the key's order.</exception>
    private static async ValueTask<(byte[] First, byte[] Last)?> CheckLayoutAsync(
        VortexFile file, DatasetSchema schema, string objectKey, CancellationToken cancellationToken)
    {
        if (schema.Tree is not { } tree || schema.Layout is not { } plan)
        {
            return null;
        }

        SortedSet<int> fields = [];
        foreach (LayoutColumn component in plan.Components)
        {
            fields.Add(component.Field);
        }

        if (plan.Partition is { } leading)
        {
            fields.Add(leading.Field);
        }

        Layouts.FieldMask projection = Layouts.FieldMasks.Of([.. fields]);
        IAsyncEnumerable<RecordBatch> rows = file.ScanBuilder().ProjectMask(projection).ExecuteAsync();
        if (schema.ColumnsOf(file.DType, objectKey) is { } columns)
        {
            ObjectReadPlan read = columns.Read(null, projection);
            rows = ObjectColumns.ReshapeAsync(
                file.ScanBuilder().ProjectMask(read.Shape.Source).WithEncodings(false, false).ExecuteAsync(), read.Shape, null, 0, compact: true, cancellationToken);
        }

        byte[]? first = null;
        byte[] last = [];
        int lastLength = 0;
        long row = 0;
        await foreach (RecordBatch batch in rows.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            using (batch)
            {
                using RowKeys keys = tree.Encode(batch);
                for (int i = 0; i < batch.RowCount; i++, row++)
                {
                    ReadOnlySpan<byte> key = keys.Row(i);
                    if (first is null)
                    {
                        first = key.ToArray();
                    }
                    else if (schema.Partitioned && !tree.SamePrefix(first, key, 1))
                    {
                        throw Unordered(objectKey, $"its row {row} lies in another partition than its first");
                    }
                    else if (schema.Keyed && key.SequenceCompareTo(last.AsSpan(0, lastLength)) < 0)
                    {
                        throw Unordered(objectKey, $"its row {row} comes before the row before it in the key's order");
                    }

                    lastLength = SortedRows.Keep(ref last, key);
                }
            }
        }

        return first is null ? null : (first, last[..lastLength]);

        static ArgumentException Unordered(string objectKey, string why) => new ArgumentException(
            $"The object '{objectKey}' is not one partition's rows sorted by the dataset's key, as every object is: {why}. " +
            "Append its rows instead, which sorts them.",
            nameof(objectKey));
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
    /// The added objects land in level 0, from where compaction moves them. Without a sort key
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
        DateTimeOffset? firstPut = null;
        long? generation = null;
        for (int i = 0; i < added.Count; i++)
        {
            ObjectDraft draft = added[i] ?? throw new ArgumentNullException(nameof(added));
            List<WrittenObject> written;
            try
            {
                Taken(draft);
                written = await draft.Writer.FinishAsync(position, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                for (int rest = i + 1; rest < added.Count; rest++)
                {
                    await added[rest].DisposeAsync().ConfigureAwait(false);
                }

                throw;
            }

            foreach (WrittenObject produced in written)
            {
                outputs.Add((0, produced.Key, produced.Entry));
                position += produced.Entry.Rows;
                firstPut = DatasetOperation.Earlier(firstPut, produced.StartedAt);
                generation = DatasetOperation.Lower(generation, produced.KeyGeneration);
            }
        }

        if (inputs.Count == 0 && outputs.Count == 0 && consumed is null)
        {
            return new ReplaceResult { Version = Version, Outcome = OperationOutcome.AlreadyThere };
        }

        DatasetOperation[] operations = inputs.Count == 0 && outputs.Count == 0
            ? []
            : [new DatasetOperation.ReplaceObjects(inputs, outputs) { Expected = expected, FirstPutAt = firstPut, KeyGeneration = generation }];
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
    /// objects it opens. Under a partition (<see cref="DatasetLayout.Partition"/>), every object above level 0
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
    /// appended rows do, since a change may move a row's key, and without a sort key they come
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
    /// is part of the sort key; renamed; its numbers widened within their kind, a signed
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
    /// Consolidates every partition whose level 0 holds an object, calm or not, whatever its share of what it meets: its
    /// objects merged with the consolidated ones they meet into objects disjoint by key, of at most the layout's top each,
    /// or moved there whole when they meet none. What a dataset asks once a partition receives no more rows, or after a load
    /// of history, rather than wait for its calm.
    /// </summary>
    /// <param name="cancellationToken">Cancels the rewrites and the commits.</param>
    /// <returns>What each consolidation did, a partition each; empty when the dataset is not tiered with a key, or none is due.</returns>
    /// <exception cref="DatasetIntegrityException">A guard found that a consolidation would lose rows; nothing was committed for it.</exception>
    public async ValueTask<IReadOnlyList<CompactionResult>> ConsolidateAsync(CancellationToken cancellationToken = default)
    {
        List<CompactionResult> results = [];
        while (true)
        {
            // A partition whose level 0 the jobs' budget does not hold at once is merged by size class first, a pass at a
            // time, each pass committed, until its consolidation fits: a pass with no merge due consolidates it anyway.
            CompactionPlan plan = await CompactionPolicy.PlanConsolidationsAsync(this, cancellationToken).ConfigureAwait(false);
            HashSet<string> passes = [];
            foreach (CompactionJob job in plan.Jobs)
            {
                if (job.Trigger == CompactionTrigger.Consolidation && !DatasetCompactor.Fits(this, job))
                {
                    passes.Add(CompactionPolicy.PartitionOf(this, job));
                }
            }

            List<CompactionJob> merges = [];
            if (passes.Count > 0)
            {
                CompactionPlan tiers = await CompactionPolicy.PlanAsync(this, null, int.MaxValue, cancellationToken).ConfigureAwait(false);
                foreach (CompactionJob merge in tiers.Jobs)
                {
                    if (merge.Trigger == CompactionTrigger.Tier && passes.Contains(CompactionPolicy.PartitionOf(this, merge)))
                    {
                        merges.Add(merge);
                    }
                }
            }

            if (merges.Count == 0)
            {
                foreach (CompactionJob job in plan.Jobs)
                {
                    if (job.Trigger == CompactionTrigger.Consolidation)
                    {
                        results.Add(await DatasetCompactor.RunAsync(this, job, cancellationToken).ConfigureAwait(false));
                    }
                }

                return results;
            }

            foreach (CompactionJob merge in merges)
            {
                results.Add(await DatasetCompactor.RunAsync(this, merge, cancellationToken).ConfigureAwait(false));
            }
        }
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
    /// Deletes what no version inside the retention window references, and the partials an earlier version learned
    /// under <c>learned/</c>. It marks from the store's latest version, not from
    /// this handle's: a handle on an older version outlives the window at its own risk, and is told so
    /// by the object it can no longer read.
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
    /// promise, and names every problem rather than throwing; a full verification checks its objects against the layout's
    /// invariants too: a partition an object, rows in the key's order, the sparse index, the entries' keys, the levels'
    /// disjunction, the chunks' geometry and the order of the object dictionaries (spec A, 3.1).
    /// </summary>
    /// <param name="since">A version already verified, whose shared pages and entries are not checked again, nor its layout; null verifies everything.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>What was checked and every problem found.</returns>
    public async ValueTask<DatasetVerification> VerifyAsync(ulong? since = null, CancellationToken cancellationToken = default)
    {
        DatasetVerification verification = await DatasetVerifier.VerifyAsync(
            _store, new VerifyOptions { Version = Version, Since = since }, cancellationToken).ConfigureAwait(false);
        if (since is not null)
        {
            return verification;
        }

        List<string> layout = await DatasetLayoutVerifier.CheckAsync(this, cancellationToken).ConfigureAwait(false);
        return layout.Count == 0 ? verification : verification with { Problems = [.. verification.Problems, .. layout] };
    }

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

        // Resolved once here, so that a value the schema cannot make is refused before anything is written.
        _ = LayoutPlan.Of(options.Layout, schema);
        string[] declared = [];
        if (options.Identifiers is { Count: > 0 } identifiers)
        {
            declared = RequireIdentifiers(identifiers, schema, nameof(DatasetOptions.Identifiers));
        }

        string[] shredded = [];
        if (options.ShreddedPaths is { Count: > 0 } paths)
        {
            shredded = RequireShreddedPaths(paths, schema, nameof(DatasetOptions.ShreddedPaths));
        }

        CommitHeader template = new CommitHeader
        {
            Version = 1,
            Seed = DatasetOptions.SeedOf(options),
            Schema = DTypeProtobuf.Serialize(schema),
            // As every header carries it: the organization and the prefix resolved, never left to a later library's defaults.
            Layout = options.Layout is not { } layout || layout is { Partition: null, SortKey.Count: 0 }
                ? null
                : layout with { Organization = layout.Organized, IndexedPrefix = layout.SortKey.Count == 0 ? null : layout.Prefix },
            Retention = options.Retention,
            Compaction = options.LockedStore && options.Compaction.Fanout == 0
                ? options.Compaction with { Fanout = DatasetOptions.LockedFanout }
                : options.Compaction,
            LockedStore = options.LockedStore,
            ErasureDelay = options.ErasureDelay,
            Identifiers = declared,
            ShreddedPaths = shredded,
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
        // Under tiers, the merges due of any partition whose inputs stay under the budget; under levels, level 0's exit.
        long budget = _options.InlineCompactionBytes;
        bool tiered = Snapshot.Header.Layout?.Organized == LayoutOrganization.Tiered && (Snapshot.Schema.Keyed || Snapshot.Schema.Partitioned);
        if (budget <= 0 || (!tiered && Lag == 0) || Interlocked.CompareExchange(ref _compactingInline, 1, 0) != 0)
        {
            return;
        }

        try
        {
            // A tier merge is cut to the budget, its fan-out at least, rather than left to another driver.
            CompactionPlan plan = await CompactionPolicy.PlanAsync(this, null, tiered ? int.MaxValue : 1, cancellationToken, inputBytes: budget).ConfigureAwait(false);
            foreach (CompactionJob job in plan.Jobs)
            {
                if (job.Trigger is CompactionTrigger.LevelZeroCeiling or CompactionTrigger.Tier && job.Bytes <= budget)
                {
                    await DatasetCompactor.RunAsync(this, job, cancellationToken).ConfigureAwait(false);
                }
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
    /// A job's commit: it rebases for as long as <paramref name="patience"/> past its attempts, half the retention window at
    /// most, and reports its operations abandoned once every attempt lost, rather than throwing.
    /// </summary>
    internal ValueTask<CommitResult> CommitJobAsync(
        IReadOnlyList<DatasetOperation> operations, TimeSpan patience, CancellationToken cancellationToken) =>
        CommitAsync(operations, CommitKind.Compaction, null, null, cancellationToken, patience, abandonsWhenLost: true);

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
        CancellationToken cancellationToken,
        TimeSpan patience = default,
        bool abandonsWhenLost = false)
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
                        Patience = patience < TrustSpan(held) ? patience : TrustSpan(held),
                        AbandonsWhenLost = abandonsWhenLost,
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
    /// How many rows hold a key below <paramref name="key"/> on the sort key; a null key is
    /// below nothing. Answered by the objects' entries wherever an object lies wholly inside the
    /// range, and by an exact count only of the objects the key cuts.
    /// </summary>
    /// <exception cref="InvalidOperationException">The dataset has no sort key, or a composite one.</exception>
    internal ValueTask<long> RankAsync(FilterLiteral key, CancellationToken cancellationToken = default)
    {
        if (Key is not { IsComposite: false } sorted)
        {
            throw new InvalidOperationException(
                "A rank is on the sort key, and this dataset declares " +
                (Key is null ? "none." : "a composite one: rank a tuple's leading column with a count instead."));
        }

        return ScanBuilder()
            .Where(Expr.Lt(Expr.Field(sorted.Paths[0]), Expr.Literal(key)))
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
    /// What orders an object in its tree: its tree key without the uid that ends it, the tree's key of its first row, its
    /// partition leading it (<see cref="DatasetSchema.Tree"/>), or its partition and position when the dataset has no key.
    /// </summary>
    internal static ReadOnlyMemory<byte> OrderOf(ReadOnlyMemory<byte> treeKey) => treeKey[..^UidBytes];

    /// <summary>The position an object of a dataset without a key is ordered by in its partition: the eight bytes before the uid.</summary>
    internal static long PositionAt(ReadOnlySpan<byte> treeKey) => BinaryPrimitives.ReadInt64BigEndian(treeKey[^(UidBytes + sizeof(long))..]);

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
            _store is SealedObjectStore { Seals: true } ? SealedSynopsisCache.Over(Session.Options.SynopsisCache, entry.Uid, contents) : null,
            footerless: true);

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
    /// read, by the columns it names, their first field from the root; none on the root itself, the chunks' directory,
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
    /// The rows of the places <paramref name="blocks"/> of the object <paramref name="entry"/> names, places of
    /// <paramref name="blockRows"/> rows, ascending, which the index of its partition located (<see cref="PartitionLookup"/>):
    /// read with no open of it, the columns <paramref name="columns"/> names or every one, as <see cref="FoundRowsAsync"/>
    /// reads the rows <paramref name="filter"/> is looked up in. None for no place; null when the object keeps no table of
    /// contents.
    /// </summary>
    internal async ValueTask<List<RecordBatch>?> ReadBlocksAsync(
        ObjectEntry entry, uint[] blocks, int blockRows, VortexExpr filter, int[]? columns, CancellationToken cancellationToken)
    {
        if (blocks.Length == 0)
        {
            return [];
        }

        if (entry.Contents is not { } contents || blockRows <= 0)
        {
            return null;
        }

        ISegmentReader source = await OpenFooterlessAsync(entry, contents, cancellationToken).ConfigureAwait(false);
        await using (source.ConfigureAwait(false))
        {
            if (Indexes.SpanReader.Open(Footerless(source, entry, contents), null, contents) is not { } opened)
            {
                return null;
            }

            using Indexes.SpanReader rowsOf = opened;
            DType schema = contents.SchemaType;

            // Each run of neighbouring places one span of rows.
            List<RowRange> ranges = [];
            for (int i = 0; i < blocks.Length;)
            {
                int first = i;
                while (i + 1 < blocks.Length && blocks[i + 1] == blocks[i] + 1)
                {
                    i++;
                }

                long start = (long)blocks[first] * blockRows;
                long end = Math.Min(((long)blocks[i] + 1) * blockRows, entry.PhysicalRows);
                if (end > start)
                {
                    ranges.Add(new RowRange(start, end));
                }

                i++;
            }

            return await FoundRowsAsync(entry, rowsOf, ranges, filter, schema, columns, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The rows of <paramref name="ranges"/>, spans of an object's rows its locating indexes left for <paramref name="filter"/>,
    /// ascending, read by <paramref name="rowsOf"/>, the columns <paramref name="columns"/> names or every one. Spans of a
    /// granule or two are read whole. Wider ones, the units hashed postings place a value in (spec C, 4 and 5), are read in
    /// two steps: the filter's columns over them, then the columns at the rows the filter keeps alone, rows closer than two
    /// FastLanes blocks one span, which a granule of each column holds: about a chunk of the identifier and a granule of
    /// every other column a value, where reading every column over its units would read a chunk of each. In an object that
    /// keeps the marks of its granules, the second step reads them alone, and pays when the other columns over the spans,
    /// as its table of contents weighs them, weigh more than the request it costs at the store's profile; in one that keeps
    /// none, spans that lie within a chunk of every other column, whose chunks the rows kept would read whole again, are read
    /// whole in one. The rows a span holds but the filter does not keep are the caller's to leave out. None when nothing is
    /// kept.
    /// </summary>
    private async ValueTask<List<RecordBatch>> FoundRowsAsync(
        ObjectEntry entry, Indexes.SpanReader rowsOf, List<RowRange> ranges, VortexExpr filter, DType schema, int[]? columns, CancellationToken cancellationToken)
    {
        if (ranges.Count == 0)
        {
            return [];
        }

        long rows = 0;
        foreach (RowRange range in ranges)
        {
            rows += range.Length;
        }

        long granuleRows = Math.Max(_options.Write.BlockRows, 1);
        if (rows <= 2 * granuleRows * ranges.Count || FilterFields(filter, schema, columns) is not { } filtered
            || !(rowsOf.ReadsGranules
                ? Spanned(entry, Others(filtered, columns, schema), rows) > Profile.RequestBytes
                : await rowsOf.CutsAsync(Others(filtered, columns, schema), ranges, cancellationToken).ConfigureAwait(false)))
        {
            return rows <= SpanRowsAtOnce
                ? [.. await rowsOf.ReadSpansAsync(ranges, columns, cancellationToken).ConfigureAwait(false)]
                : await ReadGroupsAsync(rowsOf, ranges, columns, cancellationToken).ConfigureAwait(false);
        }

        // The first step: the filter's columns over the units, and the rows it keeps there, a group of spans at a time, each
        // let go once noted; the other columns' directories beside it, which the second reads in hand.
        List<RowRange> kept = [];
        Task others = rowsOf.LoadAsync(Others(filtered, columns, schema), ranges, cancellationToken).AsTask();
        try
        {
            foreach (List<RowRange> group in Groups(ranges))
            {
                RecordBatch[] first = await rowsOf.ReadSpansAsync(group, filtered, cancellationToken).ConfigureAwait(false);
                try
                {
                    foreach (RecordBatch batch in first)
                    {
                        Compute.FilterEvaluator.Select(batch, filter);
                        Kept(batch, kept);
                    }
                }
                finally
                {
                    foreach (RecordBatch batch in first)
                    {
                        batch.Dispose();
                    }
                }
            }
        }
        finally
        {
            await others.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }

        await others.ConfigureAwait(false);

        // The second: every column at those rows.
        return kept.Count == 0 ? []
            : RowsOf(kept) <= SpanRowsAtOnce ? [.. await rowsOf.ReadSpansAsync(kept, columns, cancellationToken).ConfigureAwait(false)]
            : await ReadGroupsAsync(rowsOf, kept, columns, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The rows a read of spans decodes at once, at most: an object whose units hold a value sought nearly everywhere leaves
    /// spans of half its rows, which a read decoded in one batch each, every one at once. A user of a week of S2's logs (T9)
    /// lies in 600 units of 32 768 rows of each object of 42.8 M: 19.6 M rows an object, its variant decoded whole for the
    /// filter, past 16 GB.
    /// </summary>
    private const long SpanRowsAtOnce = 1 << 20;

    /// <summary>The rows <paramref name="ranges"/> hold together.</summary>
    private static long RowsOf(List<RowRange> ranges)
    {
        long rows = 0;
        foreach (RowRange range in ranges)
        {
            rows += range.Length;
        }

        return rows;
    }

    /// <summary>The rows of <paramref name="ranges"/>, a group of spans of <see cref="SpanRowsAtOnce"/> rows at most at a time.</summary>
    private static async ValueTask<List<RecordBatch>> ReadGroupsAsync(
        Indexes.SpanReader rowsOf, List<RowRange> ranges, int[]? columns, CancellationToken cancellationToken)
    {
        if (RowsOf(ranges) <= SpanRowsAtOnce)
        {
            return [.. await rowsOf.ReadSpansAsync(ranges, columns, cancellationToken).ConfigureAwait(false)];
        }

        List<RecordBatch> batches = [];
        try
        {
            foreach (List<RowRange> group in Groups(ranges))
            {
                batches.AddRange(await rowsOf.ReadSpansAsync(group, columns, cancellationToken).ConfigureAwait(false));
            }
        }
        catch
        {
            foreach (RecordBatch batch in batches)
            {
                batch.Dispose();
            }

            throw;
        }

        return batches;
    }

    /// <summary>
    /// The spans <paramref name="ranges"/> in groups of <see cref="SpanRowsAtOnce"/> rows at most, a span longer than that cut
    /// into pieces: a chunk a cut falls in is read by both pieces.
    /// </summary>
    private static List<List<RowRange>> Groups(List<RowRange> ranges)
    {
        if (RowsOf(ranges) <= SpanRowsAtOnce)
        {
            return [ranges];
        }

        List<List<RowRange>> groups = [];
        List<RowRange> group = [];
        long rows = 0;
        foreach (RowRange range in ranges)
        {
            for (long start = range.Start; start < range.End;)
            {
                long end = Math.Min(range.End, start + (SpanRowsAtOnce - rows));
                group.Add(new RowRange(start, end));
                rows += end - start;
                start = end;
                if (rows >= SpanRowsAtOnce)
                {
                    groups.Add(group);
                    group = [];
                    rows = 0;
                }
            }
        }

        if (group.Count > 0)
        {
            groups.Add(group);
        }

        return groups;
    }

    /// <summary>
    /// Adds the rows <paramref name="batch"/> keeps to <paramref name="kept"/>, by their places in the object, ascending: a row
    /// closer than two FastLanes blocks to the span before joins it, as the flat reader's clusters do.
    /// </summary>
    private static void Kept(RecordBatch batch, List<RowRange> kept)
    {
        if (batch.SelectedRows == 0)
        {
            return;
        }

        ReadOnlySpan<ulong> words = batch.SelectionWords;
        if (words.IsEmpty)
        {
            Keep(kept, batch.StartRow, batch.StartRow + batch.RowCount);
            return;
        }

        for (int word = 0; word < words.Length; word++)
        {
            for (ulong bits = words[word]; bits != 0; bits &= bits - 1)
            {
                int row = (word << 6) + System.Numerics.BitOperations.TrailingZeroCount(bits);
                if (row >= batch.RowCount)
                {
                    return;
                }

                Keep(kept, batch.StartRow + row, batch.StartRow + row + 1);
            }
        }

        static void Keep(List<RowRange> kept, long start, long end)
        {
            if (kept.Count > 0 && start - kept[^1].End < Layouts.FlatLayoutReader.ClusterGap)
            {
                kept[^1] = new RowRange(kept[^1].Start, Math.Max(kept[^1].End, end));
                return;
            }

            kept.Add(new RowRange(start, end));
        }
    }

    /// <summary>
    /// The top-level columns <paramref name="filter"/> reads, ascending, when they are fewer than those a read keeps
    /// (<paramref name="columns"/>, or every one of <paramref name="schema"/>'s): what the first of two steps reads. Null
    /// when the filter reads a part of a column, or all the read keeps, and one step reads as much.
    /// </summary>
    private static int[]? FilterFields(VortexExpr filter, DType schema, int[]? columns)
    {
        HashSet<string> named = new HashSet<string>(StringComparer.Ordinal);
        filter.CollectFields(named);
        List<int> fields = new List<int>(named.Count);
        foreach (string path in named)
        {
            int field = schema.IndexOfField(path);
            if (field < 0)
            {
                return null;
            }

            fields.Add(field);
        }

        fields.Sort();
        return fields.Count == 0 || fields.Count >= (columns?.Length ?? schema.FieldCount) ? null : [.. fields];
    }

    /// <summary>The bytes of columns <paramref name="fields"/> of the object <paramref name="entry"/> names over <paramref name="rows"/> of its rows, as its table of contents weighs them.</summary>
    private static long Spanned(ObjectEntry entry, int[] fields, long rows)
    {
        IReadOnlyList<long> bytes = entry.Contents!.ColumnBytes;
        long sum = 0;
        foreach (int field in fields)
        {
            sum += field < bytes.Count ? bytes[field] : 0;
        }

        return entry.PhysicalRows <= 0 ? sum : (long)(sum * Math.Min(1.0, (double)rows / entry.PhysicalRows));
    }

    /// <summary>The columns a read keeps (<paramref name="columns"/>, or every one of <paramref name="schema"/>'s) but <paramref name="filtered"/>, ascending.</summary>
    private static int[] Others(int[] filtered, int[]? columns, DType schema)
    {
        int count = columns?.Length ?? schema.FieldCount;
        List<int> others = new List<int>(count);
        for (int i = 0; i < count; i++)
        {
            int field = columns?[i] ?? i;
            if (Array.BinarySearch(filtered, field) < 0)
            {
                others.Add(field);
            }
        }

        return [.. others];
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

        // The directory's entries on the columns the filter reads alone: its runs' segments were a list each, every index of
        // the object parsed at every lookup, three fifths of what a question that leaves an identifier out allocated. Read
        // alone, those of the locating indexes, when the contents name them apart: the whole directory of a day's object, when
        // it named every chunk of the synopsis tables of the time, was 11.5 MB a lookup read for a few runs.
        DType schema = contents.SchemaType;
        Func<uint, bool>? indexed = ColumnsOf(filter, schema);
        List<Indexes.IndexSegment>? named = Named(contents, indexed);
        if (named is { Count: 0 })
        {
            return null;
        }

        ISegmentReader source = await OpenFooterlessAsync(entry, contents, cancellationToken).ConfigureAwait(false);
        await using (source.ConfigureAwait(false))
        {
            ISegmentReader reader = Footerless(source, entry, contents);
            if (Indexes.SpanReader.Open(reader, null, contents) is not { } opened)
            {
                return null;
            }

            using Indexes.SpanReader rowsOf = opened;

            // The first round trip: those entries, or the directory, beside every column's chunk directory.
            Task<Indexes.IndexDirectory?> read = LocatingAsync(entry, region, reader, indexed, named, cancellationToken);

            // The chunk directories of the columns the rows are read for: each read and parsed for every column, at every
            // lookup, was a fifth of what a question that leaves an identifier out allocated.
            Task[] chunks = new Task[columns?.Length ?? schema.FieldCount];
            for (int c = 0; c < chunks.Length; c++)
            {
                chunks[c] = rowsOf.LoadDirectoryAsync(columns?[c] ?? c, cancellationToken).AsTask();
            }

            Indexes.IndexDirectory? directory;
            try
            {
                directory = await read.ConfigureAwait(false);
            }
            catch
            {
                // The chunk directories are read into the span reader the method lets go on its way out: they end first.
                await ((Task)Task.WhenAll(chunks)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                throw;
            }

            await Task.WhenAll(chunks).ConfigureAwait(false);
            if (directory is null)
            {
                return null;
            }

            // The second: the runs' segments that may hold a value the filter asks (LiveRangesAsync); the third, every run of
            // blocks left, their granules together, a granule two runs share read once; the fourth, when a unit of hashed
            // postings left them, the columns at the rows the filter keeps there.
            List<RowRange>? ranges = await LiveRangesAsync(entry, reader, directory, filter, cancellationToken).ConfigureAwait(false);
            return ranges is null ? null : await FoundRowsAsync(entry, rowsOf, ranges, filter, schema, columns, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The rows of the object <paramref name="entry"/> names that its locating indexes, its identifiers' runs among them, may
    /// hold for <paramref name="filter"/>, as runs of their blocks, with no open of the object and no row read: its index
    /// directory's entries on the filter's columns, then the runs' segments that may hold the values, two round trips cold.
    /// Null when the entry carries no table of contents this library reads, or the object no locating index the filter asks.
    /// </summary>
    /// <param name="entry">The object.</param>
    /// <param name="filter">What the rows are placed for.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    internal async ValueTask<List<RowRange>?> PlacesAsync(ObjectEntry entry, VortexExpr filter, CancellationToken cancellationToken)
    {
        if (entry.Contents is not { DirectoryRegion: { } region } contents)
        {
            return null;
        }

        Func<uint, bool>? indexed = ColumnsOf(filter, contents.SchemaType);
        List<Indexes.IndexSegment>? named = Named(contents, indexed);
        if (named is { Count: 0 })
        {
            return null;
        }

        ISegmentReader source = await OpenFooterlessAsync(entry, contents, cancellationToken).ConfigureAwait(false);
        await using (source.ConfigureAwait(false))
        {
            ISegmentReader reader = Footerless(source, entry, contents);
            return await LocatingAsync(entry, region, reader, indexed, named, cancellationToken).ConfigureAwait(false) is { } directory
                ? await LiveRangesAsync(entry, reader, directory, filter, cancellationToken).ConfigureAwait(false)
                : null;
        }
    }

    /// <summary>
    /// The runs of blocks of the object <paramref name="entry"/> names that its locating indexes, the entries of
    /// <paramref name="directory"/>, leave for <paramref name="filter"/>, read through <paramref name="reader"/>: the runs'
    /// segments that may hold the values, and the blocks whose Bloom filters do not refuse them. Null when the object keeps
    /// no locating index the filter asks.
    /// </summary>
    private static async Task<List<RowRange>?> LiveRangesAsync(
        ObjectEntry entry, ISegmentReader reader, Indexes.IndexDirectory directory, VortexExpr filter, CancellationToken cancellationToken)
    {
        Indexes.ObjectContents contents = entry.Contents!;

        // The runs' segments that may hold a value the filter asks, over the blocks of the finest of the locating entries, a
        // place of hashed postings spanning a unit of them.
        long blockRows = 0;
        foreach (Indexes.IndexEntry index in directory.Entries)
        {
            if (index.Kind is Indexes.IndexKinds.SortedRuns or Indexes.IndexKinds.PostingsBlocks or Indexes.IndexKinds.PostingsHashed or Indexes.IndexKinds.BloomSbbf
                && index.BlockLength > 0 && (blockRows == 0 || (long)index.BlockLength < blockRows))
            {
                blockRows = (long)index.BlockLength;
            }
        }

        Indexes.FooterlessIndexes host = new Indexes.FooterlessIndexes(reader, contents.SchemaType, directory, [.. contents.Encodings]);
        // The Bloom filters where no locating index answers: postings place a value's rows, the filters only refuse blocks,
        // a round trip a level, which a lookup that postings answer would pay for nothing.
        Indexes.KeyIndexPruner? keys = blockRows <= 0 ? null : await Indexes.KeyIndexPruner.BuildAsync(host, filter, blockRows, cancellationToken).ConfigureAwait(false);
        using Indexes.BloomPruner? blooms = blockRows <= 0 || keys is not null ? null : await Indexes.BloomPruner.BuildAsync(host, filter, blockRows, cancellationToken).ConfigureAwait(false);
        if (keys is null && blooms is null)
        {
            return null;
        }

        Compute.BlockMask live = new Compute.BlockMask(host.RowCount, blockRows);
        if (keys is not null)
        {
            await keys.RefineAsync(host, live, cancellationToken).ConfigureAwait(false);
        }

        if (blooms is not null)
        {
            await blooms.RefineAsync(host, live, cancellationToken).ConfigureAwait(false);
        }

        List<RowRange> ranges = [];
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
            ranges.Add(new RowRange(start, end));
        }

        return ranges;
    }

    /// <summary>
    /// The index directory of the object <paramref name="entry"/> names, read through <paramref name="reader"/>: the entries
    /// <paramref name="named"/> of its locating indexes alone, or, when null, the directory whole, its entries on the columns
    /// <paramref name="indexed"/> keeps; null when it does not parse.
    /// </summary>
    private Task<Indexes.IndexDirectory?> LocatingAsync(
        ObjectEntry entry, Indexes.IndexSegment region, ISegmentReader reader, Func<uint, bool>? indexed, List<Indexes.IndexSegment>? named,
        CancellationToken cancellationToken)
    {
        return named is null ? WholeAsync() : EntriesAsync(named);

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

        // The entries alone, a directory of them over the object's rows: each checked against its own checksum, one that fails
        // costing itself, as in a directory read whole.
        // The entries alone, each parsed once for the object it lies in and kept (IndexEntries), read only when none is.
        async Task<Indexes.IndexDirectory?> EntriesAsync(List<Indexes.IndexSegment> regions)
        {
            Indexes.IndexEntry?[] found = new Indexes.IndexEntry?[regions.Count];
            int missing = 0;
            for (int i = 0; i < regions.Count; i++)
            {
                missing += _indexEntries.TryGet((entry.Uid, regions[i].Offset), out Indexes.IndexEntry known) ? 0 : 1;
                found[i] = known;
            }

            if (missing > 0)
            {
                using SegmentRequestSet requests = new SegmentRequestSet(missing);
                int[] slots = new int[regions.Count];
                for (int i = 0; i < regions.Count; i++)
                {
                    slots[i] = found[i] is null
                        ? requests.Add(new Serialization.Schemas.SegmentSpec(regions[i].Offset, regions[i].Length, regions[i].AlignmentExponent, 0, 0))
                        : -1;
                }

                await reader.ReadManyAsync(requests, cancellationToken).ConfigureAwait(false);
                for (int i = 0; i < regions.Count; i++)
                {
                    if (slots[i] < 0)
                    {
                        continue;
                    }

                    ReadOnlySpan<byte> bytes = requests.GetBuffer(slots[i]).Span;
                    if (regions[i].Holds(bytes) && Indexes.IndexDirectory.TryParseEntry(bytes, region.Offset, out Indexes.IndexEntry? index))
                    {
                        _indexEntries.Keep((entry.Uid, regions[i].Offset), index, IndexEntryBytes + (2L * regions[i].Length), out index);
                        found[i] = index;
                    }
                }
            }

            List<Indexes.IndexEntry> kept = new List<Indexes.IndexEntry>(regions.Count);
            foreach (Indexes.IndexEntry? index in found)
            {
                if (index is not null)
                {
                    kept.Add(index);
                }
            }

            return new Indexes.IndexDirectory((ulong)entry.PhysicalRows, 0, Indexes.WritePolicy.None, kept);
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

    /// <summary>The bytes the indexes of the partitions this handle read may hold, their directories, last bytes and pages (<see cref="PartitionIndexes"/>).</summary>
    internal const long PartitionIndexBytes = 64L << 20;

    /// <summary>The indexes of the partitions this handle read, kept for the questions after (<see cref="PartitionIndexCache"/>).</summary>
    internal PartitionIndexCache PartitionIndexes { get; } = new PartitionIndexCache(PartitionIndexBytes);

    /// <summary>The granules where the filters this handle evaluated on its objects' rows kept a row (<see cref="PredicatePages"/>).</summary>
    internal PredicatePages Predicates { get; }

    /// <summary>
    /// What reading the dataset costs (spec B, 2.3): its options' profile when they give one, its store's otherwise, a remote
    /// object store's unless the store says it is a disk or memory.
    /// </summary>
    internal CostProfile Profile => _options.CostProfile ?? _store.CostProfile;

    /// <summary>What a path costs an object, at <see cref="Profile"/>: a region this handle's memo holds is in memory, one the session's cache holds on its disk.</summary>
    internal PathCosts Costs => _costs ??= new PathCosts(Profile, _options.ChunkBytes, Resident, _options.Write.BlockRows);

    private PathCosts? _costs;

    /// <summary>The tier a cache holds the region of object <paramref name="uid"/> at: memory for this handle's memo, the disk for the session's cache; null when none holds it.</summary>
    private CostTier? Resident(UInt128 uid, Indexes.IndexSegment region) =>
        _synopses.Holds(uid, region.Offset) ? Profile.MemoryCache
        : Session.Options.SynopsisCache?.Holds(uid, region) == true ? Profile.DiskCache
        : null;

    /// <summary>What an order of the rows by each value the filters of this handle's questions read would have spared them (<see cref="LayoutAdvisor"/>).</summary>
    internal LayoutAdvisor Advisor { get; } = new LayoutAdvisor();


    /// <summary>The rows of a granule the advice reckons with, a dataset writer's block.</summary>
    private const int AdvisedGranuleRows = 8192;

    /// <summary>
    /// The key the questions this handle asked call for (spec A, 9): the columns and variant keys their filters read, each
    /// with the bytes an order by it would have spared them, the objects read as rows credited as
    /// <see cref="ComponentAdvice.BytesAvoided"/> says; ordered as a key would take them, the fewest distinct values a
    /// partition first, by the column sheets of the version's objects, each partition's merged; and where the sparse index
    /// would stop. A diagnostic, which the telemetry counts too (<c>vortex.dataset.layout.avoidable_bytes</c>, by value):
    /// the library never lays a dataset out on its own, a layout being declared when the dataset is created, and the
    /// partition stays the application's choice.
    /// </summary>
    /// <param name="cancellationToken">Cancels the reads of the sheets.</param>
    /// <returns>The advice; no component before a question read an object as rows under a filter.</returns>
    /// <remarks>The sheets of every object are read, a request an object, none the session's cache or this handle holds.</remarks>
    public async ValueTask<LayoutAdvice> RecommendLayoutAsync(CancellationToken cancellationToken = default)
    {
        List<(string Path, long Bytes, long Objects)> credited = Advisor.Ranked();
        if (credited.Count == 0)
        {
            return new LayoutAdvice([], 0);
        }

        // The columns credited, by their place in the schema; a variant key's read has none, and no sheet.
        DatasetSnapshot version = Snapshot;
        DType schema = version.Schema.DType;
        List<int> fields = [];
        int[] place = new int[credited.Count];
        for (int i = 0; i < credited.Count; i++)
        {
            int field = schema.IndexOfField(credited[i].Path);
            place[i] = field < 0 ? -1 : fields.Count;
            if (field >= 0)
            {
                fields.Add(field);
            }
        }

        // Each partition's rows, and of each column its sheets' registers merged.
        Dictionary<string, (long Rows, byte[]?[] Registers)> partitions = new Dictionary<string, (long, byte[]?[])>(StringComparer.Ordinal);
        await foreach (PositionedObject held in version.WalkAsync(null, 0, long.MaxValue, null, cancellationToken).ConfigureAwait(false))
        {
            string partition = version.Schema.Partitioned && held.TreeKey.Length > UidBytes
                ? Convert.ToHexString(OrderOf(held.TreeKey).Span[..version.Schema.Tree!.PrefixLength(OrderOf(held.TreeKey).Span, 1)])
                : string.Empty;
            if (!partitions.TryGetValue(partition, out (long Rows, byte[]?[] Registers) known))
            {
                known = (0, new byte[]?[fields.Count]);
            }

            known.Rows += held.Entry.Rows;
            Indexes.ColumnSheet?[] sheets = fields.Count == 0 ? [] : await ReadSheetsAsync(held.Entry, fields, cancellationToken).ConfigureAwait(false);
            for (int c = 0; c < sheets.Length; c++)
            {
                if (sheets[c] is not { } sheet)
                {
                    continue;
                }

                byte[] merged = known.Registers[c] ??= new byte[Indexes.ColumnSheet.Registers];
                ReadOnlySpan<byte> ranks = sheet.Ranks;
                for (int r = 0; r < merged.Length; r++)
                {
                    merged[r] = Math.Max(merged[r], ranks[r]);
                }
            }

            partitions[partition] = known;
        }

        // Each value's distinct values a partition, the partitions that count it averaged.
        double rows = 0;
        foreach ((long count, byte[]?[] _) in partitions.Values)
        {
            rows += count;
        }

        rows /= Math.Max(partitions.Count, 1);
        List<ComponentAdvice> components = [];
        for (int i = 0; i < credited.Count; i++)
        {
            double distinct = double.NaN;
            if (place[i] >= 0)
            {
                double sum = 0;
                int counted = 0;
                foreach ((long _, byte[]?[] registers) in partitions.Values)
                {
                    if (registers[place[i]] is { } merged)
                    {
                        sum += Math.Max(1, Indexes.HyperLogLog.Estimate(merged, Indexes.ColumnSheet.Precision));
                        counted++;
                    }
                }

                distinct = counted == 0 ? double.NaN : sum / counted;
            }

            components.Add(new ComponentAdvice(credited[i].Path, credited[i].Bytes, credited[i].Objects, distinct, double.NaN));
        }

        // The fewest distinct values first, the uncounted last, the most bytes avoided first among equals.
        components.Sort(static (left, right) =>
        {
            bool leftCounted = !double.IsNaN(left.DistinctPerPartition);
            bool rightCounted = !double.IsNaN(right.DistinctPerPartition);
            if (leftCounted != rightCounted)
            {
                return leftCounted ? -1 : 1;
            }

            int order = leftCounted ? left.DistinctPerPartition.CompareTo(right.DistinctPerPartition) : 0;
            return order != 0 ? order : right.BytesAvoided.CompareTo(left.BytesAvoided);
        });

        // Each component's runs at its place: the distinct values up to it multiplied, at most a partition's rows, at a value
        // and a length each; the index stops past the last whose runs still hold a granule's rows.
        double runs = 1;
        int prefix = 0;
        for (int i = 0; i < components.Count; i++)
        {
            ComponentAdvice component = components[i];
            int field = schema.IndexOfField(component.Path);
            double width = field >= 0 ? WidthOf(schema.GetField(field)) : 16;
            runs = double.IsNaN(component.DistinctPerPartition) ? double.NaN : Math.Min(rows, runs * component.DistinctPerPartition);
            components[i] = component with { BytesPerRow = double.IsNaN(runs) || rows <= 0 ? double.NaN : runs * (width + 1) / rows };
            if (!double.IsNaN(runs) && rows / runs >= AdvisedGranuleRows)
            {
                prefix = i + 1;
            }
        }

        return new LayoutAdvice(components, components.Count > 0 ? Math.Max(prefix, 1) : 0);

        // A value's bytes in a run: a fixed width's, about sixteen for a text or anything else.
        static double WidthOf(DType type)
        {
            while (type.Kind == DTypeKind.Extension)
            {
                type = type.StorageType;
            }

            return type.Kind switch
            {
                DTypeKind.Primitive => type.PType.ByteWidth(),
                DTypeKind.Bool => 1,
                _ => 16,
            };
        }
    }

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
    /// Where the values of columns <paramref name="fields"/> of the object <paramref name="entry"/> names lie, granule by
    /// granule (spec D, 3), by their position in <paramref name="fields"/>: the regions its entry's table of contents names,
    /// read with no open of the object in one round trip, none once this handle read them. A column the object keeps no
    /// such index of has none.
    /// </summary>
    internal async ValueTask<Indexes.ColumnLocate?[]> ReadLocatesAsync(ObjectEntry entry, IReadOnlyList<int> fields, CancellationToken cancellationToken)
    {
        Indexes.ColumnLocate?[] located = new Indexes.ColumnLocate?[fields.Count];
        if (entry.Contents is not { Locates.Count: > 0 } contents)
        {
            return located;
        }

        Indexes.IndexSegment?[] regions = new Indexes.IndexSegment?[fields.Count];
        for (int i = 0; i < fields.Count; i++)
        {
            regions[i] = contents.LocateOf(fields[i]);
        }

        object?[] read = new object?[fields.Count];
        await ReadSynopsesAsync(entry, contents, regions, read, static (_, bytes) => (Indexes.ColumnLocate.From(bytes.ToArray())!, bytes.Length), cancellationToken)
            .ConfigureAwait(false);
        for (int i = 0; i < fields.Count; i++)
        {
            located[i] = read[i] as Indexes.ColumnLocate;
        }

        return located;
    }

    /// <summary>
    /// The geometry of columns <paramref name="fields"/> of the object <paramref name="entry"/> names (spec C, 2.1), by their
    /// position in <paramref name="fields"/>: each chunk's encoding tree and marks, the regions its entry's table of contents
    /// names, read with no open of the object in one round trip, none once this handle read them; through the object
    /// <paramref name="opened"/> once open, whose tail serves those lying in it with no request. A column the object keeps
    /// no marks of has none.
    /// </summary>
    internal async ValueTask<Indexes.ColumnMarks?[]> ReadMarksAsync(
        ObjectEntry entry, IReadOnlyList<int> fields, CancellationToken cancellationToken, ISegmentReader? opened = null)
    {
        Indexes.ColumnMarks?[] marks = new Indexes.ColumnMarks?[fields.Count];
        if (entry.Contents is not { Marks.Count: > 0 } contents)
        {
            return marks;
        }

        Indexes.IndexSegment?[] regions = new Indexes.IndexSegment?[fields.Count];
        for (int i = 0; i < fields.Count; i++)
        {
            regions[i] = fields[i] < contents.Marks.Count ? contents.Marks[fields[i]] : null;
        }

        object?[] read = new object?[fields.Count];
        await ReadSynopsesAsync(entry, contents, regions, read, static (_, bytes) => (Indexes.ColumnMarks.From(bytes.ToArray())!, bytes.Length), cancellationToken, opened)
            .ConfigureAwait(false);
        for (int i = 0; i < fields.Count; i++)
        {
            marks[i] = read[i] as Indexes.ColumnMarks;
        }

        return marks;
    }

    /// <summary>
    /// The counts of column <paramref name="field"/> of the object <paramref name="entry"/> names, and when
    /// <paramref name="sketches"/> asks their sketches, read with no open of the object: the regions its entry's table of
    /// contents names, side by side in one round trip, none once this handle read them. A column the object keeps no
    /// counts or sketches of, or an object with no table of contents this library reads, has none.
    /// </summary>
    internal ValueTask<(Indexes.ColumnCounts? Counts, Indexes.ColumnSketches? Sketches)> ReadCountsAsync(
        ObjectEntry entry, int field, bool sketches, CancellationToken cancellationToken)
    {
        if (entry.Contents is not { Counts.Count: > 0 } contents)
        {
            return new((null, null));
        }

        // Both regions in the memo, or not asked: no read, and no arrays to read them by, which every object of a question
        // its counts answer took once warm.
        Indexes.IndexSegment? counted = contents.CountsOf(field);
        Indexes.IndexSegment? sketched = sketches ? contents.SketchesOf(field) : null;
        Indexes.ColumnCounts? counts = null;
        Indexes.ColumnSketches? held = null;
        return (counted is not { } region || _synopses.TryGet(entry.Uid, region.Offset, out counts))
            && (sketched is not { } sketchRegion || _synopses.TryGet(entry.Uid, sketchRegion.Offset, out held))
                ? new((counts, held))
                : ReadCountsUnheldAsync(entry, contents, counted, sketched, cancellationToken);
    }

    /// <summary><see cref="ReadCountsAsync"/>'s regions, one at least not in the memo, read side by side in one round trip.</summary>
    private async ValueTask<(Indexes.ColumnCounts? Counts, Indexes.ColumnSketches? Sketches)> ReadCountsUnheldAsync(
        ObjectEntry entry, Indexes.ObjectContents contents, Indexes.IndexSegment? counted, Indexes.IndexSegment? sketched, CancellationToken cancellationToken)
    {
        object?[] read = new object?[2];
        Indexes.IndexSegment?[] regions = [counted, sketched];
        await ReadSynopsesAsync(entry, contents, regions, read, CountsOrSketches, cancellationToken).ConfigureAwait(false);
        return (read[0] as Indexes.ColumnCounts, read[1] as Indexes.ColumnSketches);
    }

    /// <summary>
    /// The counts of every column the object <paramref name="entry"/> names keeps them of, with their sketches, as
    /// <see cref="ReadCountsAsync"/> reads one column's, every column's regions side by side in one round trip where a column
    /// at a time took one each: a column whose counts do not read is left out.
    /// </summary>
    internal async ValueTask<List<(int Field, Indexes.ColumnCounts Counts, Indexes.ColumnSketches? Sketches)>> ReadCountsWithSketchesAsync(
        ObjectEntry entry, CancellationToken cancellationToken)
    {
        List<(int Field, Indexes.ColumnCounts Counts, Indexes.ColumnSketches? Sketches)> counted = [];
        if (entry.Contents is not { Counts.Count: > 0 } contents)
        {
            return counted;
        }

        int fields = contents.Counts.Count;
        Indexes.IndexSegment?[] regions = new Indexes.IndexSegment?[2 * fields];
        for (int i = 0; i < fields; i++)
        {
            regions[2 * i] = contents.Counts[i].Region;
            regions[(2 * i) + 1] = contents.SketchesOf(contents.Counts[i].Field);
        }

        object?[] read = new object?[regions.Length];
        await ReadSynopsesAsync(entry, contents, regions, read, static (position, bytes) => CountsOrSketches(position % 2, bytes), cancellationToken)
            .ConfigureAwait(false);
        for (int i = 0; i < fields; i++)
        {
            if (read[2 * i] is Indexes.ColumnCounts counts)
            {
                counted.Add((contents.Counts[i].Field, counts, read[(2 * i) + 1] as Indexes.ColumnSketches));
            }
        }

        return counted;
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
    /// Where the values of the shredded keys <paramref name="paths"/>, each a variant column, a key and a class, lie in the
    /// object <paramref name="entry"/> names (spec E, 3.3), by their position in <paramref name="paths"/>, read with no open
    /// of the object: the regions its table of contents names, side by side in one round trip, none once this handle read
    /// them. A key the object keeps none of has none.
    /// </summary>
    internal async ValueTask<Indexes.ColumnLocate?[]> ReadPathLocatesAsync(
        ObjectEntry entry, IReadOnlyList<(int Field, string Key, Types.Variants.PathClass Class)> paths, CancellationToken cancellationToken)
    {
        Indexes.ColumnLocate?[] locates = new Indexes.ColumnLocate?[paths.Count];
        if (entry.Contents is not { PathLocates.Count: > 0 } contents)
        {
            return locates;
        }

        object?[] read = new object?[paths.Count];
        Indexes.IndexSegment?[] regions = new Indexes.IndexSegment?[read.Length];
        for (int i = 0; i < paths.Count; i++)
        {
            regions[i] = contents.PathLocateOf(paths[i].Field, paths[i].Key, paths[i].Class);
        }

        await ReadSynopsesAsync(entry, contents, regions, read, static (_, bytes) => (Indexes.ColumnLocate.From(bytes.ToArray())!, bytes.Length), cancellationToken)
            .ConfigureAwait(false);
        for (int i = 0; i < paths.Count; i++)
        {
            locates[i] = read[i] as Indexes.ColumnLocate;
        }

        return locates;
    }

    /// <summary>
    /// The statistics of columns <paramref name="fields"/> of the object <paramref name="entry"/> names over its granules
    /// (<see cref="Indexes.GranuleStats"/>), and the histograms of those of them <paramref name="histogrammed"/> names
    /// (<see cref="Indexes.GranuleHistograms"/>), by their position in <paramref name="fields"/>, read with no open of the
    /// object: the regions its entry's table of contents names, side by side in one round trip, none once this handle read
    /// them. A column the object keeps none of has none. With them, the values the key's components
    /// <paramref name="components"/> take in each granule (<see cref="Indexes.GranuleKeyRuns"/>), by their position there. A
    /// column <paramref name="paths"/> names at its position is the shredded key it names, its statistics the key's; column
    /// <paramref name="skip"/> has none read.
    /// </summary>
    internal async ValueTask<(Indexes.GranuleStats?[] Stats, Indexes.GranuleHistograms?[] Histograms, Indexes.GranuleKeyRuns?[] Runs)> ReadGranuleStatsAsync(
        ObjectEntry entry, IReadOnlyList<int> fields, IReadOnlyList<int> histogrammed, IReadOnlyList<int> components, CancellationToken cancellationToken,
        IReadOnlyList<(int Field, string Key, Types.Variants.PathClass Class)?>? paths = null, int skip = -1)
    {
        Indexes.GranuleStats?[] columns = new Indexes.GranuleStats?[fields.Count];
        Indexes.GranuleHistograms?[] histograms = new Indexes.GranuleHistograms?[fields.Count];
        Indexes.GranuleKeyRuns?[] runs = components.Count == 0 ? [] : new Indexes.GranuleKeyRuns?[components.Count];
        if (entry.Contents is not { GranuleStats.Count: > 0 } contents)
        {
            return (columns, histograms, runs);
        }

        // A column's statistics at a position a multiple of three, its histograms at the one after, a component's values at
        // the one after that.
        int slots = Math.Max(fields.Count, components.Count);
        object?[] read = new object?[3 * slots];
        Indexes.IndexSegment?[] regions = new Indexes.IndexSegment?[read.Length];
        for (int i = 0; i < fields.Count; i++)
        {
            regions[3 * i] = fields[i] == skip ? null
                : paths?[i] is { } path ? contents.PathGranuleStatsOf(path.Field, path.Key, path.Class)?.Region
                : contents.GranuleStatsOf(fields[i]);
            regions[(3 * i) + 1] = Contains(histogrammed, fields[i]) ? contents.GranuleHistogramsOf(fields[i]) : null;
        }

        for (int i = 0; i < components.Count; i++)
        {
            regions[(3 * i) + 2] = contents.KeyRunsOf(components[i])?.Region;
        }

        await ReadSynopsesAsync(
            entry, contents, regions, read, static (position, bytes) => (position % 3) switch { 0 => Granules(bytes), 1 => Histograms(bytes), _ => Runs(bytes) }, cancellationToken)
            .ConfigureAwait(false);
        for (int i = 0; i < fields.Count; i++)
        {
            columns[i] = read[3 * i] as Indexes.GranuleStats;
            histograms[i] = read[(3 * i) + 1] as Indexes.GranuleHistograms;
        }

        for (int i = 0; i < components.Count; i++)
        {
            runs[i] = read[(3 * i) + 2] as Indexes.GranuleKeyRuns;
        }

        return (columns, histograms, runs);

        static (object Synopsis, long Bytes) Granules(ReadOnlySpan<byte> bytes)
        {
            Indexes.GranuleStats stats = Indexes.GranuleStats.From(bytes)
                ?? throw new VortexFormatException("A column's statistics over an object's granules do not decode as statistics.");
            return (stats, stats.Bytes);
        }

        static (object Synopsis, long Bytes) Histograms(ReadOnlySpan<byte> bytes)
        {
            Indexes.GranuleHistograms histograms = Indexes.GranuleHistograms.From(bytes)
                ?? throw new VortexFormatException("A measure's histograms over an object's granules do not decode as histograms.");
            return (histograms, histograms.Bytes);
        }

        static (object Synopsis, long Bytes) Runs(ReadOnlySpan<byte> bytes)
        {
            Indexes.GranuleKeyRuns runs = Indexes.GranuleKeyRuns.From(bytes)
                ?? throw new VortexFormatException("A key's values over an object's granules do not decode as values.");
            return (runs, runs.Bytes);
        }

        static bool Contains(IReadOnlyList<int> fields, int field)
        {
            for (int i = 0; i < fields.Count; i++)
            {
                if (fields[i] == field)
                {
                    return true;
                }
            }

            return false;
        }
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
    /// <param name="entry">The object's entry.</param>
    /// <param name="contents">Its table of contents.</param>
    /// <param name="regions">The regions to read.</param>
    /// <param name="synopses">Receives what each region reads, by its position.</param>
    /// <param name="reader">What a region's bytes read as.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <param name="opened">
    /// The object, open, to read the regions through in place of a reader with no open: the tail its open read serves those
    /// lying in it with no request.
    /// </param>
    private ValueTask ReadSynopsesAsync(
        ObjectEntry entry, Indexes.ObjectContents contents, Indexes.IndexSegment?[] regions, object?[] synopses, SynopsisReader reader,
        CancellationToken cancellationToken, ISegmentReader? opened = null) =>
        Recalled(entry.Uid, regions, synopses) == 0
            ? default
            : opened is null
                ? ReadUnheldAsync(entry, contents, regions, synopses, reader, cancellationToken)
                : ReadThroughAsync(entry.Uid, opened, regions, synopses, reader, cancellationToken);

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
        ISegmentReader source = await open(cancellationToken).ConfigureAwait(false);
        await using (source.ConfigureAwait(false))
        {
            await ReadThroughAsync(uid, wrap(source), regions, synopses, reader, cancellationToken, keyed).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// <see cref="ReadRegionsAsync"/>'s read, through <paramref name="through"/>, which the caller keeps: the regions not null
    /// side by side in one round trip, each as <paramref name="reader"/> reads its bytes, kept under their offsets with
    /// <paramref name="keyed"/>.
    /// </summary>
    private async ValueTask ReadThroughAsync(
        UInt128 uid, ISegmentReader through, Indexes.IndexSegment?[] regions, object?[] synopses, SynopsisReader reader, CancellationToken cancellationToken,
        ulong keyed = 0)
    {
        int asked = 0;
        foreach (Indexes.IndexSegment? region in regions)
        {
            asked += region is null ? 0 : 1;
        }

        using SegmentRequestSet requests = new SegmentRequestSet(asked);
        int[] slots = new int[regions.Length];
        for (int i = 0; i < regions.Length; i++)
        {
            slots[i] = regions[i] is { } region
                ? requests.Add(new Serialization.Schemas.SegmentSpec(region.Offset, region.Length, region.AlignmentExponent, 0, 0))
                : -1;
        }

        await through.ReadManyAsync(requests, cancellationToken).ConfigureAwait(false);
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

    /// <summary>
    /// The path directory of variant column <paramref name="field"/> of the object <paramref name="entry"/>
    /// names, read with no open of the object: the region its entry's table of contents names, in one round
    /// trip, none once the session's cache holds it. Null when the entry carries no table of contents this
    /// library reads, or the column no directory.
    /// </summary>
    internal ValueTask<Indexes.VariantDirectory?> ReadPathDirectoryAsync(ObjectEntry entry, int field, CancellationToken cancellationToken) =>
        WithPathDirectoryAsync(entry, field, static (region, _) => Indexes.VariantDirectory.From(region), (object?)null, cancellationToken);

    /// <summary>
    /// The path directory of variant column <paramref name="field"/> of the object <paramref name="entry"/> names, opened
    /// (<see cref="Indexes.VariantDirectory.Open"/>) and read with no open of the object, as
    /// <see cref="ReadPathDirectoryAsync"/> reads it; null when the entry names none, or it does not decode.
    /// </summary>
    internal ValueTask<Indexes.VariantDirectory.DirectoryBody?> OpenPathDirectoryAsync(ObjectEntry entry, int field, CancellationToken cancellationToken) =>
        WithPathDirectoryAsync(entry, field, static (region, _) => Indexes.VariantDirectory.Open(region), (object?)null, cancellationToken);

    /// <summary>What a span of the bytes of a region gives.</summary>
    private delegate TResult RegionReader<TState, TResult>(ReadOnlySpan<byte> region, TState state);

    /// <summary>
    /// What <paramref name="read"/> gives of the path directory of variant column <paramref name="field"/> of the object
    /// <paramref name="entry"/> names, read with no open of the object: the region its entry's table of contents names, in one
    /// round trip, none once the session's cache holds it; the default when the entry names none, or its bytes are not the
    /// region's.
    /// </summary>
    private async ValueTask<TResult?> WithPathDirectoryAsync<TState, TResult>(
        ObjectEntry entry, int field, RegionReader<TState, TResult> read, TState state, CancellationToken cancellationToken)
    {
        if (entry.Contents is not { } contents)
        {
            return default;
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
                return region.Holds(bytes.Buffer.Span) ? read(bytes.Buffer.Span, state) : default;
            }
        }

        return default;
    }

    /// <summary>
    /// The variant column <paramref name="field"/> of the groups of rows of the object <paramref name="entry"/> names
    /// that may hold a value at <paramref name="key"/>, a top-level key, read with no open of the object: its
    /// path directory and the column's chunk directory together, then those groups' chunks together, two
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
            if (Indexes.SpanReader.Open(reader, null, contents) is not { } opened)
            {
                return null;
            }

            using Indexes.SpanReader rowsOf = opened;

            // The first round trip: the path directory beside the column's chunk directory.
            ValueTask<Indexes.VariantDirectory?> directory = ReadPathDirectoryAsync(entry, field, cancellationToken);
            Task chunks = rowsOf.LoadDirectoryAsync(field, cancellationToken).AsTask();
            Indexes.VariantDirectory? paths;
            try
            {
                paths = await directory.ConfigureAwait(false);
            }
            catch
            {
                // The chunk directory is read into the span reader the method lets go on its way out: it ends first.
                await ((Task)chunks).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                throw;
            }

            await chunks.ConfigureAwait(false);
            if (paths is null)
            {
                return null;
            }

            // The second: every group of rows the path may hold a value in, at once, neighbours joined; the whole object when
            // it counts none.
            ReadOnlySpan<Indexes.PathEntry> held = paths.Of([key]);
            List<RowRange> spans = [];
            long rows = rowsOf.Rows;
            int groups = Math.Max(paths.GroupCount, 1);
            long groupRows = paths.GroupCount > 0 ? paths.GroupRows : rows;
            for (int group = 0; group < groups; group++)
            {
                foreach (Indexes.PathEntry path in held)
                {
                    if (path.In(paths.GroupCount > 0 ? group : -1))
                    {
                        RowRange span = new RowRange(group * groupRows, Math.Min((group + 1) * groupRows, rows));
                        if (spans.Count > 0 && spans[^1].End == span.Start)
                        {
                            spans[^1] = new RowRange(spans[^1].Start, span.End);
                        }
                        else
                        {
                            spans.Add(span);
                        }

                        break;
                    }
                }
            }

            return [.. await rowsOf.ReadSpansAsync(spans, [field], cancellationToken).ConfigureAwait(false)];
        }
    }

    /// <summary>
    /// The sparse index of the object <paramref name="entry"/> names, for one search, which disposes it: its table, then its
    /// pages in one request, some forty kilobytes for an object of a gibibyte. Read through <paramref name="opened"/>, the
    /// object's open, whose tail serves what it holds; with no open of the object otherwise. Nothing of it is kept past
    /// its dispose: its bytes stay where an index's regions stay, in the session's segment cache and disk cache. Null
    /// when the object keeps none, or what was read does not decode.
    /// </summary>
    internal async ValueTask<Indexes.GranuleKeys?> KeyIndexAsync(
        ObjectEntry entry, Indexes.ObjectContents contents, CancellationToken cancellationToken, ISegmentReader? opened = null)
    {
        if (contents.KeyIndex is not { } table)
        {
            return null;
        }

        if (opened is not null)
        {
            return await KeysThroughAsync(opened, contents, table, cancellationToken).ConfigureAwait(false);
        }

        ISegmentReader source = await OpenFooterlessAsync(entry, contents, cancellationToken).ConfigureAwait(false);
        await using (source.ConfigureAwait(false))
        {
            return await KeysThroughAsync(Footerless(source, entry, contents), contents, table, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The sparse index of the object whose open is <paramref name="opened"/>, read through it for one walk or search, which
    /// disposes it; null when the object keeps none, or what was read does not decode.
    /// </summary>
    internal static ValueTask<Indexes.GranuleKeys?> KeyIndexThroughAsync(
        ISegmentReader opened, Indexes.ObjectContents contents, CancellationToken cancellationToken) =>
        contents.KeyIndex is { } table ? KeysThroughAsync(opened, contents, table, cancellationToken) : default;

    /// <summary>
    /// <see cref="KeyIndexAsync"/>'s reads through <paramref name="reader"/>: the table of <paramref name="contents"/>'s
    /// sparse index, checked against its checksum, then its pages, which a search checks a page at a time as it reaches them.
    /// </summary>
    private static async ValueTask<Indexes.GranuleKeys?> KeysThroughAsync(
        ISegmentReader reader, Indexes.ObjectContents contents, Indexes.IndexSegment table, CancellationToken cancellationToken)
    {
        Buffers.SegmentOwner? bytes = await reader.ReadAsync(SpecOf(table), cancellationToken).ConfigureAwait(false);
        Indexes.GranuleKeys? keys = null;
        try
        {
            if (!table.Holds(bytes.Buffer.Span) || Indexes.GranuleKeys.Over(bytes) is not { } read)
            {
                return null;
            }

            keys = read;
            bytes = null;
            if (keys.WantsPages)
            {
                if (contents.KeyPages is not { } pages)
                {
                    return null;
                }

                bytes = await reader.ReadAsync(SpecOf(pages), cancellationToken).ConfigureAwait(false);
                if (!keys.TakePages(bytes))
                {
                    return null;
                }

                bytes = null;
            }

            Indexes.GranuleKeys whole = keys;
            keys = null;
            return whole;
        }
        finally
        {
            bytes?.Dispose();
            keys?.Dispose();
        }

        static Serialization.Schemas.SegmentSpec SpecOf(Indexes.IndexSegment region) =>
            new Serialization.Schemas.SegmentSpec(region.Offset, region.Length, region.AlignmentExponent, 0, 0);
    }

    /// <summary>
    /// The object dictionaries the object <paramref name="entry"/> names writes its columns against, read with no open of
    /// it: each column's values' segment, a request each, null for a column whose chunks hold its values.
    /// </summary>
    internal async ValueTask<Writing.ObjectDictionary?[]> DictionariesAsync(
        ObjectEntry entry, Indexes.ObjectContents contents, CancellationToken cancellationToken)
    {
        Writing.ObjectDictionary?[] dictionaries = new Writing.ObjectDictionary?[contents.Dictionaries.Count];
        if (dictionaries.Length == 0)
        {
            return dictionaries;
        }

        ISegmentReader source = await OpenFooterlessAsync(entry, contents, cancellationToken).ConfigureAwait(false);
        await using (source.ConfigureAwait(false))
        {
            using Indexes.SpanReader reader = Indexes.SpanReader.Open(Footerless(source, entry, contents), null, contents)
                ?? throw new VortexFormatException($"The table of contents of object {entry.Key} names no chunk directory.");
            for (int field = 0; field < dictionaries.Length; field++)
            {
                dictionaries[field] = await reader.DictionaryAsync(field, cancellationToken).ConfigureAwait(false);
            }
        }

        return dictionaries;
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
        ObjectOutput draft, long rows, long firstRow, CancellationToken cancellationToken)
    {
        if (rows == 0)
        {
            draft.Sink.Discard();
            return null;
        }

        UInt128 hash = draft.Sink.ContentHash;

        // Read out of the chunks the sink still holds, before the put: the entry's bounds and the
        // smallest key its leaf is ordered by then cost no request at all. An object that went to the
        // store as it was written is read out of the tail its sink kept, or, when its description reads
        // before the tail, from the store once put, its footer in a request or two. Should that read
        // fail, the object is left unnamed, as an abandoned job's are, for a vacuum to take.
        ObjectSummaries summaries;
        byte[] prefix;
        byte[] last;
        bool streamed = draft.Sink.IsStreamed;
        bool described = !streamed;
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

                // A streamed object's tail, which the sink kept: its statistics are read out of it, with no request, unless
                // its description reads before it, a key's bounds read from its rows.
                if (draft.Sink.Tail(_options.Session.Options.EnginePool) is { } kept)
                {
                    try
                    {
                        (summaries, prefix, last) = await DescribeAsync(kept, firstRow, draft, cancellationToken).ConfigureAwait(false);
                        described = true;
                    }
                    catch (Exception missed) when (Missed(missed))
                    {
                    }
                }
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
        if (!described)
        {
            ISegmentReader stored = await SealedObjectStore.OpenDataAsync(_store, draft.Key, Uid(draft.Identity), bytes, cancellationToken).ConfigureAwait(false);
            (summaries, prefix, last) = await DescribeAsync(stored, firstRow, draft, cancellationToken, draft.Writer.TailBytes).ConfigureAwait(false);
        }
        ObjectEntry entry = new ObjectEntry(draft.Key, Uid(draft.Identity), rows, bytes, hash, summaries)
            .WithFooterBytes(draft.Writer.TailBytes)
            .WithTableOfContents(draft.Writer.TableOfContents)
            .WithLastKey(last)
            .WithArrival(draft.Arrival > 0 ? draft.Arrival : Math.Max(_options.TimeProvider.GetUtcNow().ToUnixTimeSeconds(), 1));
        return new WrittenObject(entry, KeyOf(prefix, entry), draft.Writer.DataBytes, draft.StartedAt, draft.KeyGeneration);
    }

    /// <summary>Whether <paramref name="failure"/> is a read before a streamed object's kept tail, however wrapped.</summary>
    private static bool Missed(Exception failure)
    {
        for (Exception? at = failure; at is not null; at = at.InnerException)
        {
            if (at is ObjectSegmentSink.TailMissedException)
            {
                return true;
            }
        }

        return false;
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
        if (version.Schema.Keyed)
        {
            return end;
        }

        foreach ((int _, DatasetTree tree) in version.Levels.Occupied())
        {
            // The partitions lead the tree, and the positions of each run on from those of the others: every leaf's, a
            // subtree's end kept by its page, which no version changes, so that an append reads the pages its commit
            // wrote again, not every page.
            if (version.Schema.Partitioned)
            {
                end = Math.Max(end, await SubtreeEndAsync(version, tree.Root, tree.Depth, cancellationToken).ConfigureAwait(false));
                continue;
            }

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

    /// <summary>
    /// The end of the positions of the rows the subtree under <paramref name="page"/>, of <paramref name="depth"/> levels of
    /// pages, holds: the greatest of its leaves' places past their rows; kept by the page, which is never rewritten in place.
    /// </summary>
    private async ValueTask<long> SubtreeEndAsync(DatasetSnapshot version, PageReference page, int depth, CancellationToken cancellationToken)
    {
        if (_subtreeEnds.TryGet(page, out long known))
        {
            return known;
        }

        ReadOnlyMemory<byte> node = await version.Pages.ReadPageAsync(page, cancellationToken).ConfigureAwait(false);
        long end = 0;
        if (depth > 1)
        {
            foreach (InternalEntry child in TreePage.ReadInternal(node))
            {
                end = Math.Max(end, await SubtreeEndAsync(version, child.Child, depth - 1, cancellationToken).ConfigureAwait(false));
            }
        }
        else
        {
            foreach (TreeEntry leaf in TreePage.ReadLeaf(node))
            {
                end = Math.Max(end, PositionAt(leaf.Key.Span) + leaf.Rows);
            }
        }

        _subtreeEnds.Keep(page, end, SubtreeEndBytes, out _);
        return end;
    }

    /// <summary>The bytes a subtree's end is counted at in <see cref="_subtreeEnds"/>: its page's reference, its value and a slot.</summary>
    private const int SubtreeEndBytes = 96;

    // The end of each subtree a keyless partitioned dataset's appends found (SubtreeEndAsync), by its page: a megabyte's worth.
    private readonly ClockMemo<PageReference, long> _subtreeEnds = new ClockMemo<PageReference, long>(1L << 20);

    /// <summary>
    /// The entries of objects' index directories a lookup read, parsed, by object and region, which do not change: each
    /// locating index's entry of every object a lookup reached was read and parsed at every question, 15 % of what a search
    /// of one identifier allocated. Within 4 MiB, an entry weighed twice its bytes.
    /// </summary>
    private readonly ClockMemo<(UInt128 Uid, ulong Offset), Indexes.IndexEntry> _indexEntries =
        new ClockMemo<(UInt128 Uid, ulong Offset), Indexes.IndexEntry>(4L << 20);

    /// <summary>What an entry kept weighs besides its bytes: the record, its lists and the memo's slot.</summary>
    private const long IndexEntryBytes = 256;

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

            next ??= new DatasetSnapshot(commit, PagesOf(version, commit), _objects, knownAt, Costs, current.Schema);
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
    /// Completes an object's writer and puts the object in the store, keyed after <paramref name="firstRow"/> when the
    /// dataset is ordered by arrival; null when it took no row and nothing was put.
    /// </summary>
    internal async ValueTask<WrittenObject?> CompleteAsync(ObjectOutput output, long firstRow, CancellationToken cancellationToken)
    {
        try
        {
            // A writer that took no row is not completed: an object of no row is not put.
            if (!output.Writer.IsFinished && output.Writer.RowCount > 0)
            {
                await output.Writer.CompleteAsync(cancellationToken).ConfigureAwait(false);
            }

            await output.Writer.DisposeAsync().ConfigureAwait(false);
            return await SealAsync(output, output.Writer.RowCount, firstRow, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            output.Sink.Discard();
            throw;
        }
    }

    /// <summary>
    /// An object of a dataset without a key, its rows placed from <paramref name="firstRow"/> on in
    /// <paramref name="partition"/>, row-encoded, empty for none: its key in the tree.
    /// </summary>
    internal static WrittenObject Placed(WrittenObject written, ReadOnlySpan<byte> partition, long firstRow) =>
        written with { Key = KeyOf([.. partition, .. PositionOf(firstRow)], written.Entry) };

    /// <summary>
    /// The bounds an entry carries, read out of a file's own statistics. The columns the layout's partition and key read
    /// are always among them, whatever the limit or the declared list says: a scan prunes the partitions a filter on the
    /// partition's column leaves out by them, whole subtrees at once since the partition leads the tree, and the key's are
    /// the columns a reader is most likely to filter by.
    /// </summary>
    /// <param name="file">The object, open.</param>
    private ObjectSummaries Summaries(VortexFile file)
    {
        IReadOnlyList<ColumnSummary> columns = _options.SummaryColumns is { } declared
            ? ColumnSummaries.Of(file, declared)
            : ColumnSummaries.Of(file, _options.SummaryColumnLimit);
        if (Snapshot.Schema.Layout is not { } plan)
        {
            return ObjectSummaries.From(columns);
        }

        // A variant column's own bounds bound nothing a component reads in one class.
        List<string> sources = [];
        foreach (LayoutColumn read in plan.Partition is { } partition ? [partition, .. plan.Components] : plan.Components)
        {
            if (read.Read is null && !sources.Contains(read.Value.SourcePath))
            {
                sources.Add(read.Value.SourcePath);
            }
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

        foreach (ColumnSummary column in ColumnSummaries.Of(file, sources))
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
    /// <param name="firstRow">The position of its first row, for a dataset without a sort key.</param>
    /// <param name="draft">The object's draft, which says what walks it by key without a run: its rows in key order, which is checked.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <param name="tailBytes">The bytes of its footer when they pass an open's first read, so that the open reads them at once; 0 otherwise.</param>
    private async ValueTask<(ObjectSummaries Summaries, byte[] Prefix, byte[] Last)> DescribeAsync(
        ISegmentReader source, long firstRow, ObjectOutput draft, CancellationToken cancellationToken, long tailBytes = 0)
    {
        VortexOpenOptions options = tailBytes > ObjectEntry.FirstReadBytes
            ? ObjectCache.OpenOptions with { InitialReadBytes = (int)Math.Min(tailBytes, int.MaxValue) }
            : ObjectCache.OpenOptions;
        VortexFile file = await VortexFile
            .OpenAsync(source, options, cancellationToken).ConfigureAwait(false);
        await using (file.ConfigureAwait(false))
        {
            // The keys of its first and last rows when its writer took them in the key's order; its position after its
            // partition's when the dataset has no key.
            ObjectSummaries summaries = Summaries(file);
            if (draft.Keys is { } keys)
            {
                return (summaries, keys.First, keys.Last);
            }

            if (draft.Partition is { } partition)
            {
                return (summaries, [.. partition, .. PositionOf(firstRow)], []);
            }

            (byte[] prefix, byte[] last) = await BoundsAsync(file, summaries, firstRow, cancellationToken).ConfigureAwait(false);
            return (summaries, prefix, last);
        }
    }

    /// <summary>
    /// What orders an object's leaf, its smallest key or its position, and its largest key, which its entry states when
    /// its statistics do not bound the key (<see cref="DatasetKey.StatesLastKey"/>): empty otherwise, without a key, or
    /// without a cursor to say it.
    /// </summary>
    private async ValueTask<(byte[] Prefix, byte[] Last)> BoundsAsync(
        VortexFile file, ObjectSummaries summaries, long firstRow, CancellationToken cancellationToken) =>
        Key is not { } sorted ? (PositionOf(firstRow), [])
        : sorted.StatesLastKey ? await sorted.BoundsAsync(file, summaries, cancellationToken).ConfigureAwait(false)
        : (await sorted.MinimumAsync(file, summaries, cancellationToken).ConfigureAwait(false), []);

    /// <summary>What orders an object's leaf: its smallest key, or its position.</summary>
    private async ValueTask<byte[]> PrefixAsync(
        VortexFile file, ObjectSummaries summaries, long firstRow, CancellationToken cancellationToken) =>
        Key is { } sorted
            ? await sorted.MinimumAsync(file, summaries, cancellationToken).ConfigureAwait(false)
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
            Fold = ObjectSummaryFold.For(template.Layout),
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
    /// An object's order when the dataset declares no sort key: a position. Big-endian, so
    /// that the <c>memcmp</c> order the tree compares by is numeric order.
    /// </summary>
    private static byte[] PositionOf(long row)
    {
        byte[] prefix = new byte[sizeof(long)];
        BinaryPrimitives.WriteInt64BigEndian(prefix, row);
        return prefix;
    }

    internal static UInt128 Uid(Guid identity)
    {
        Span<byte> bytes = stackalloc byte[16];
        identity.TryWriteBytes(bytes);
        return ((UInt128)BinaryPrimitives.ReadUInt64LittleEndian(bytes) << 64)
            | BinaryPrimitives.ReadUInt64LittleEndian(bytes[8..]);
    }
}
