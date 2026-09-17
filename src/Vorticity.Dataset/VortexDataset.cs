// The dataset itself - docs/13-dataset.md §3 and §9: "a dataset is a store prefix" with two kinds
// of immutable object, and every answer it gives must equal the answer a single file would give
// (§14's acceptance).
//
// WHAT THIS STEP DELIVERS, and what it leaves to the next. Creation, opening, the import of a file
// that is already in the store WITHOUT copying it (§3: "A single existing Vortex file becomes a
// dataset of one leaf: one commit object, no copy"), an append that writes one data object and one
// commit, and a scan that reads every object in key order and hands its batches on. What it does
// not do yet: the mandatory run on the clustering key, the summaries a scan prunes with, the
// cursors and `InKeyOrder` across objects, and `Rows(a, b)`. They are the rest of the plan's step
// 39, and each of them needs the leaf entry to carry more than it carries here (§4.2).
//
// THE KEY, FOR NOW, IS THE FIRST ROW POSITION -- §4.1's own alternative: "ordered by the clustering
// key when the dataset declares one and by first row position otherwise". Eight big-endian bytes,
// whose `memcmp` order is their numeric order, so the tree needs no row encoding and the dataset
// needs no dependency on the 0.x package that provides it. A declared clustering key changes this
// line and the entry's key range, and nothing else in this file.
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Columns;
using Vorticity.File;
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

    /// <summary>How many times a commit rebases before giving up (§8.2).</summary>
    public int MaxAttempts { get; init; } = 8;

    /// <summary>The most bytes one appended data object may buffer before the store takes it.</summary>
    public long MaxObjectBytes { get; init; } = ObjectSegmentSink.DefaultMaxBytes;
}

/// <summary>A versioned dataset over an object store.</summary>
public sealed class VortexDataset : IAsyncDisposable
{
    private readonly IObjectStore _store;
    private readonly DatasetOptions _options;
    private readonly DTypeArena _types = new DTypeArena();
    private CommitHeader _header;
    private DatasetTree _tree;
    private CommitPageSource _pages;

    private VortexDataset(
        IObjectStore store, DatasetOptions options, CommitHeader header, DatasetTree tree, CommitPageSource pages)
    {
        _store = store;
        _options = options;
        _header = header;
        _tree = tree;
        _pages = pages;
        Schema = header.Schema.IsEmpty
            ? default
            : DTypeProtobuf.Read(header.Schema.Span, _types);
    }

    /// <summary>The version this handle reads.</summary>
    public ulong Version => _header.Version;

    /// <summary>The dataset's schema.</summary>
    public DType Schema { get; }

    /// <summary>The rows of every object it holds.</summary>
    public long RowCount => _tree.Rows;

    /// <summary>The data objects it holds.</summary>
    public long ObjectCount => _tree.Entries;

    /// <summary>The seed every boundary of its trees is decided under (§4.1).</summary>
    public ulong Seed => _header.Seed;

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

        CommitPageSource pages = new CommitPageSource(store);
        pages.Inline(commit.Header);
        pages.Know(version, commit.HeaderEnd);
        return new VortexDataset(
            store,
            (options ?? new DatasetOptions()) with { Seed = commit.Header.Seed },
            commit.Header,
            DatasetCommitter.TreeOf(commit.Header),
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

        CommitPageSource pages = new CommitPageSource(_store);
        pages.Inline(commit.Header);
        pages.Know(version, commit.HeaderEnd);
        _header = commit.Header;
        _tree = DatasetCommitter.TreeOf(commit.Header);
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
    /// did not write) is bound by the store's token instead, as §7 allows.
    /// </remarks>
    public async ValueTask<ulong> ImportAsync(string objectKey, CancellationToken cancellationToken = default)
    {
        ObjectKey.Check(objectKey);
        ObjectHead head = await _store.HeadAsync(objectKey, cancellationToken).ConfigureAwait(false)
            ?? throw ObjectNotFoundException.For(objectKey);

        ObjectSegmentSource source = new ObjectSegmentSource(_store, objectKey);
        ObjectEntry entry;
        await using (source.ConfigureAwait(false))
        {
            VortexFile file = await VortexFile
                .OpenAsync(source, new VortexOpenOptions(), cancellationToken).ConfigureAwait(false);
            await using (file.ConfigureAwait(false))
            {
                entry = new ObjectEntry(objectKey, Identity(file), file.RowCount, head.Length, UInt128.Zero);
            }
        }

        return await ApplyAsync(
            [new DatasetOperation.AddObject(KeyOf(RowCount), entry)], cancellationToken).ConfigureAwait(false);
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
        Guid identity = Guid.NewGuid();
        string key = CommitKey.ForData(identity.ToString("N", CultureInfo.InvariantCulture));
        ObjectSegmentSink sink = new ObjectSegmentSink(_store, key, _options.MaxObjectBytes);
        long rows = 0;
        VortexFileWriter writer = VortexFileWriter.Create(sink, Schema, _options.Write.WithIdentity(identity));
        await using (writer.ConfigureAwait(false))
        {
            await foreach (RecordBatch batch in batches.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                rows += batch.RowCount;
                await writer.WriteAsync(batch, cancellationToken).ConfigureAwait(false);
            }

            await writer.CompleteAsync(cancellationToken).ConfigureAwait(false);
        }

        if (rows == 0)
        {
            sink.Discard();
            return _header.Version;
        }

        long bytes = sink.Position;
        UInt128 hash = sink.ContentHash;
        if (await sink.CommitAsync(cancellationToken).ConfigureAwait(false) != PutOutcome.Created)
        {
            throw new ObjectStoreException($"'{key}' was taken; a fresh uid cannot collide (13 §3).");
        }

        ObjectEntry entry = new ObjectEntry(key, Uid(identity), rows, bytes, hash);
        return await ApplyAsync(
            [new DatasetOperation.AddObject(KeyOf(RowCount), entry)], cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Applies operations and moves this handle to the version they created.</summary>
    /// <param name="operations">What to do (§8.2).</param>
    /// <param name="cancellationToken">Cancels the requests.</param>
    /// <returns>The version created.</returns>
    public async ValueTask<ulong> ApplyAsync(
        IReadOnlyList<DatasetOperation> operations, CancellationToken cancellationToken = default)
    {
        CommitResult result = await DatasetCommitter
            .CommitAsync(_store, operations, Commit(_options, _header), cancellationToken).ConfigureAwait(false);
        await RefreshAsync(cancellationToken).ConfigureAwait(false);
        return result.Version;
    }

    /// <summary>Every data object of this version, in key order.</summary>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>The entries.</returns>
    public async IAsyncEnumerable<ObjectEntry> ObjectsAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (TreeEntry entry in _tree.EnumerateAsync(_pages, cancellationToken).ConfigureAwait(false))
        {
            yield return ObjectEntry.FromBytes(entry.Value.Span);
        }
    }

    /// <summary>A scan over every object of this version.</summary>
    /// <returns>The builder.</returns>
    public DatasetScanBuilder Scan() => new DatasetScanBuilder(this);

    /// <inheritdoc/>
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>Opens one of the dataset's data objects as a Vortex file.</summary>
    /// <param name="entry">Its leaf entry.</param>
    /// <param name="cancellationToken">Cancels the open.</param>
    /// <returns>The file and the source under it, which the caller disposes.</returns>
    internal async ValueTask<(VortexFile File, ObjectSegmentSource Source)> OpenObjectAsync(
        ObjectEntry entry, CancellationToken cancellationToken)
    {
        ObjectSegmentSource source = new ObjectSegmentSource(_store, entry.Key);
        try
        {
            VortexFile file = await VortexFile
                .OpenAsync(source, new VortexOpenOptions(), cancellationToken).ConfigureAwait(false);
            return (file, source);
        }
        catch
        {
            await source.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static CommitOptions Commit(DatasetOptions options, CommitHeader template) => new CommitOptions
    {
        Seed = template.Seed == 0 ? options.Seed : template.Seed,
        Template = template,
        MaxAttempts = options.MaxAttempts,
    };

    /// <summary>The tree key of an object that starts at row <paramref name="row"/>.</summary>
    /// <param name="row">Its first row in the dataset.</param>
    /// <remarks>
    /// Big-endian, so that `memcmp` order is numeric order: the tree compares keys as bytes and
    /// nothing else (06), and a little-endian key would order 256 before 2.
    /// </remarks>
    private static ReadOnlyMemory<byte> KeyOf(long row)
    {
        byte[] key = new byte[sizeof(long)];
        BinaryPrimitives.WriteInt64BigEndian(key, row);
        return key;
    }

    /// <summary>A file's identity as §7 mints it, or zero when it has none.</summary>
    private static UInt128 Identity(VortexFile file) =>
        file.Identity is { } identity ? Uid(identity) : UInt128.Zero;

    private static UInt128 Uid(Guid identity)
    {
        Span<byte> bytes = stackalloc byte[16];
        identity.TryWriteBytes(bytes);
        return ((UInt128)BinaryPrimitives.ReadUInt64LittleEndian(bytes) << 64)
            | BinaryPrimitives.ReadUInt64LittleEndian(bytes[8..]);
    }
}
