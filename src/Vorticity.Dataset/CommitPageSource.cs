// Reads a tree's pages wherever they lie - docs/13-dataset.md §3: "A commit references the pages it
// did not change where they already are, in older commit objects".
//
// THREE PLACES, CHEAPEST FIRST. A page a commit INLINED in its header arrived with the header and
// costs nothing (§3: "per level, its top page inlined, and the pages below it too while the header
// stays under 256 KiB"). A page THIS commit is writing is in the builder's buffer, not yet in any
// store. Everything else is a ranged read of the commit object its reference names -- and the
// reference says which version that is, which is the whole point of carrying it (§3).
//
// AND EVERY PAGE IS CHECKED against the reference that sent the reader here, wherever it came from:
// that is §7's rule and it is the only defence a torn or misdirected page meets.
using System;
using System.Collections.Concurrent;
using System.IO.Hashing;
using System.Threading;
using System.Threading.Tasks;

namespace Vorticity.Dataset;

/// <summary>An <see cref="IPageSource"/> over a dataset's commit objects.</summary>
public sealed class CommitPageSource : IPageSource
{
    private readonly IObjectStore _store;
    // CONCURRENT, because a walk reads a window of siblings at once (DatasetTree.WalkAsync): the
    // pages it caches and the pages regions it learns arrive from several reads in flight. Two reads
    // of one page or one region race to the same bytes, and either may keep them.
    private readonly ConcurrentDictionary<PageReference, ReadOnlyMemory<byte>> _known = [];

    /// <summary>Per version, where its pages region starts. Asked once, remembered.</summary>
    private readonly ConcurrentDictionary<ulong, long> _starts = [];
    private long _reads;
    private CommitObjectBuilder? _builder;
    private ulong _building;
    private byte[]? _built;
    private long _builtStart;

    /// <summary>Reads through <paramref name="store"/>.</summary>
    /// <param name="store">The dataset's store.</param>
    /// <exception cref="ArgumentNullException"><paramref name="store"/> is null.</exception>
    public CommitPageSource(IObjectStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

    /// <summary>How many pages were read through the store rather than found in hand.</summary>
    public long Reads => Interlocked.Read(ref _reads);

    /// <summary>
    /// The version this source reads for, which a missing commit object is reported against (§10);
    /// 0 when it reads for none.
    /// </summary>
    public ulong Reading { get; init; }

    /// <summary>The bytes of a page this source already holds, without any request.</summary>
    /// <param name="reference">The reference.</param>
    /// <param name="page">Receives its bytes.</param>
    /// <returns>Whether it was in hand.</returns>
    public bool TryGetKnown(PageReference reference, out ReadOnlyMemory<byte> page) =>
        _known.TryGetValue(reference, out page);

    /// <summary>Records where a version's pages region starts, learned without a request.</summary>
    /// <param name="version">The version.</param>
    /// <param name="pagesStart">Where its pages region starts.</param>
    public void Know(ulong version, long pagesStart) => _starts[version] = pagesStart;

    /// <summary>Takes the pages a header carries, which cost no request (§3).</summary>
    /// <param name="header">The commit header.</param>
    public void Inline(CommitHeader header)
    {
        ArgumentNullException.ThrowIfNull(header);
        foreach (CommitLevel level in header.Levels)
        {
            foreach (InlinedPage page in level.Inlined)
            {
                _known[page.Reference] = page.Bytes;
            }
        }
    }

    /// <summary>Follows the commit object being built, whose pages are not in the store yet.</summary>
    /// <param name="builder">The builder.</param>
    /// <param name="version">The version it commits.</param>
    public void Writing(CommitObjectBuilder builder, ulong version)
    {
        _builder = builder;
        _building = version;
        _built = null;
    }

    /// <summary>Takes the bytes the builder produced, so the new version's pages can be read back.</summary>
    /// <param name="bytes">The commit object, as it was created.</param>
    public void Placed(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        _built = bytes;
        _builtStart = CommitFormat.PreambleBytes
            + System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(12));
        _starts[_building] = _builtStart;
    }

    /// <inheritdoc/>
    public async ValueTask<ReadOnlyMemory<byte>> ReadPageAsync(
        PageReference reference, CancellationToken cancellationToken)
    {
        if (_known.TryGetValue(reference, out ReadOnlyMemory<byte> inlined))
        {
            return inlined;
        }

        if (reference.Version == _building && _built is { } bytes)
        {
            // The commit we just created: its pages are in the bytes we put, at their offset from
            // that object's own pages region.
            return Check(reference, bytes.AsMemory((int)(_builtStart + reference.Offset), reference.Length));
        }

        if (reference.Version == _building && _builder is { } builder
            && builder.TryGetPage(reference, out ReadOnlyMemory<byte> fresh))
        {
            return fresh;
        }

        byte[] page = await ReadStoredAsync(reference, cancellationToken).ConfigureAwait(false);

        // KEPT, because a page is immutable and a commit reads the same ones twice by construction:
        // the descent that looks a key up and the merge that rewrites its leaf are the same page.
        _known[reference] = page;
        return page;
    }

    /// <summary>The bytes of an index fragment (§6.4), read where its reference says and checked against it.</summary>
    /// <param name="reference">The fragment's reference, from its object's leaf entry.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>The fragment.</returns>
    /// <remarks>
    /// NOT KEPT, unlike a page. A fragment is read once per open of its object, and the object cache
    /// keeps the open file that holds it; keeping it here as well would hold its bytes twice for as
    /// long as this version is read, and a fragment is index bytes, not a 128 KiB page.
    /// </remarks>
    public async ValueTask<ReadOnlyMemory<byte>> ReadFragmentAsync(
        PageReference reference, CancellationToken cancellationToken) =>
        await ReadStoredAsync(reference, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// A page or a fragment read out of the commit object its reference names. A missing object is
    /// reported against the version being read: vacuum took it (§10), and a reader says so rather
    /// than answering from what is left.
    /// </summary>
    private async ValueTask<byte[]> ReadStoredAsync(PageReference reference, CancellationToken cancellationToken)
    {
        string key = CommitKey.For(reference.Version);
        try
        {
            long start = await StartAsync(reference.Version, key, cancellationToken).ConfigureAwait(false);
            Interlocked.Increment(ref _reads);
            return await CommitObject
                .ReadPageAsync(_store, key, reference, start, cancellationToken).ConfigureAwait(false);
        }
        catch (ObjectNotFoundException missing) when (Reading != 0 && missing.Version == 0)
        {
            throw ObjectNotFoundException.InVersion(key, Reading, missing);
        }
    }

    /// <summary>Where a version's body starts: one sixteen-byte read per version, remembered.</summary>
    private async ValueTask<long> StartAsync(ulong version, string key, CancellationToken cancellationToken)
    {
        if (!_starts.TryGetValue(version, out long start))
        {
            // Every offset of that object is relative to it, pages and fragments alike.
            Interlocked.Increment(ref _reads);
            start = await CommitObject.PagesStartAsync(_store, key, cancellationToken).ConfigureAwait(false);
            _starts[version] = start;
        }

        return start;
    }

    private static ReadOnlyMemory<byte> Check(PageReference reference, ReadOnlyMemory<byte> page)
    {
        UInt128 hash = XxHash128.HashToUInt128(page.Span);
        if (hash != reference.Hash)
        {
            throw new CommitFormatException(
                $"The page at {reference.Offset} hashes to {hash:x32} and its reference says {reference.Hash:x32}.");
        }

        return page;
    }
}
