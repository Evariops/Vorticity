using System;
using System.Collections.Concurrent;
using System.IO.Hashing;
using System.Threading;
using System.Threading.Tasks;

namespace Vorticity.Dataset;

/// <summary>
/// An <see cref="IPageSource"/> over a dataset's commit objects: a page inlined in a header, held
/// by the read that opened its version, kept by the handle's cache or still in the builder's buffer
/// costs no request, anything else is a ranged read of the commit object its reference names. Every
/// page is checked against the reference that led here.
/// </summary>
internal sealed class CommitPageSource : IPageSource
{
    private readonly IObjectStore _store;

    // The handle's, shared by every version it reads and every commit it makes: what one read, the
    // next finds there.
    private readonly PageCache? _cache;

    // Concurrent because a walk reads a window of siblings at once; two reads of one page race to
    // the same bytes, and either may keep them.
    private readonly ConcurrentDictionary<PageReference, ReadOnlyMemory<byte>> _known = [];

    private readonly ConcurrentDictionary<ulong, long> _starts = [];

    // The pages regions open reads brought back, by version: the pages a commit wrote and left out of
    // its header lie there when they fit the read.
    private readonly ConcurrentDictionary<ulong, ReadOnlyMemory<byte>> _held = [];
    private long _reads;
    private CommitObjectBuilder? _builder;
    private ulong _building;
    private byte[]? _built;
    private long _builtStart;

    /// <summary>A source reading through <paramref name="store"/>, and through the handle's cache when it has one.</summary>
    public CommitPageSource(IObjectStore store, PageCache? cache = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
        _cache = cache;
    }

    /// <summary>How many pages were read through the store rather than found in hand.</summary>
    public long Reads => Interlocked.Read(ref _reads);

    /// <summary>
    /// The version this source reads for, which a missing commit object is reported against; 0 when
    /// it reads for none.
    /// </summary>
    public ulong Reading { get; init; }

    /// <summary>The bytes of a page this source already holds, without any request.</summary>
    /// <exception cref="TornCommitException">A page the open read holds does not hash to its reference.</exception>
    public bool TryGetKnown(PageReference reference, out ReadOnlyMemory<byte> page)
    {
        if (_known.TryGetValue(reference, out page))
        {
            return true;
        }

        if (_held.TryGetValue(reference.Version, out ReadOnlyMemory<byte> region) && TryHeld(reference, region, out page))
        {
            _known[reference] = page;
            return true;
        }

        return false;
    }

    /// <summary>
    /// The bytes of a page in hand, without any request: one this source holds, or one the handle
    /// kept from an earlier version.
    /// </summary>
    /// <exception cref="TornCommitException">A page an open read holds does not hash to its reference.</exception>
    public bool TryGetInHand(PageReference reference, out ReadOnlyMemory<byte> page)
    {
        if (TryGetKnown(reference, out page))
        {
            return true;
        }

        if (!TryGetKept(reference, out page))
        {
            return false;
        }

        _known[reference] = page;
        return true;
    }

    /// <summary>
    /// Takes what the open read of a version's commit object holds, which costs no further request:
    /// the pages its header inlines, where its pages region starts and where those of the earlier
    /// versions it names start, and the start of that region, where the pages the version wrote
    /// without inlining them lie.
    /// </summary>
    public void Open(ulong version, CommitObject commit)
    {
        ArgumentNullException.ThrowIfNull(commit);
        Inline(commit.Header);
        Know(version, commit.HeaderEnd);
        foreach (PagesStart start in commit.Header.Starts)
        {
            Know(start.Version, start.Offset);
        }

        if (!commit.Held.IsEmpty)
        {
            _held[version] = commit.Held;
            _cache?.Hold(version, commit.Held);
        }
    }

    /// <summary>
    /// Where the pages region of <paramref name="version"/> starts, when this source learned it: from
    /// a header it opened, or by reading a page of that version.
    /// </summary>
    public bool TryGetStart(ulong version, out long start) => _starts.TryGetValue(version, out start);

    /// <summary>
    /// Keeps bytes this source's writer put at <paramref name="reference"/>, for itself and for the
    /// handle's later versions: a reference names its bytes by their hash, so bytes kept for a commit
    /// that then loses name nothing another writer's commit holds under other bytes.
    /// </summary>
    public void Keep(PageReference reference, ReadOnlyMemory<byte> bytes)
    {
        _known[reference] = bytes;
        _cache?.Add(reference, bytes);
    }

    /// <summary>Records where a version's pages region starts, learned without a request.</summary>
    public void Know(ulong version, long pagesStart)
    {
        _starts[version] = pagesStart;
        _cache?.AddStart(version, pagesStart);
    }

    /// <summary>Takes the pages a header carries, which cost no request.</summary>
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
    public void Writing(CommitObjectBuilder builder, ulong version)
    {
        _builder = builder;
        _building = version;
        _built = null;
    }

    /// <summary>
    /// Takes the bytes the builder produced, so the new version's pages can be read back; and hands
    /// the handle's cache the pages that lie past what the read opening the version brings back,
    /// which a walk of the version would otherwise ask the store for, though this writer holds them.
    /// </summary>
    public void Placed(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        _built = bytes;
        _builtStart = CommitFormat.PreambleBytes
            + System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(12));
        Know(_building, _builtStart);
        if (_cache is not null && _builder is { } builder && bytes.Length > CommitFormat.OpenBytes)
        {
            for (int i = 0; i < builder.PageCount; i++)
            {
                (PageReference reference, byte[] page) = builder.PageAt(i);
                if (_builtStart + reference.Offset + reference.Length > CommitFormat.OpenBytes)
                {
                    _cache.Add(reference, page);
                }
            }
        }
    }

    /// <inheritdoc/>
    public async ValueTask<ReadOnlyMemory<byte>> ReadPageAsync(
        PageReference reference, CancellationToken cancellationToken)
    {
        if (TryGetKnown(reference, out ReadOnlyMemory<byte> inlined))
        {
            return inlined;
        }

        if (reference.Version == _building && _built is { } bytes)
        {
            return Check(reference, bytes.AsMemory((int)(_builtStart + reference.Offset), reference.Length));
        }

        if (reference.Version == _building && _builder is { } builder
            && builder.TryGetPage(reference, out ReadOnlyMemory<byte> fresh))
        {
            return fresh;
        }

        if (TryGetKept(reference, out ReadOnlyMemory<byte> kept))
        {
            _known[reference] = kept;
            return kept;
        }

        byte[] page;
        try
        {
            page = await ReadStoredAsync(reference, cancellationToken).ConfigureAwait(false);
        }
        catch (CommitFormatException torn)
        {
            throw TornCommitException.Of(reference.Version, torn);
        }

        // Kept because a page is immutable and a commit reads the same ones twice by construction:
        // the descent that looks a key up and the merge that rewrites its leaf are the same page.
        _known[reference] = page;
        _cache?.Add(reference, page);
        return page;
    }

    /// <summary>
    /// The bytes of an index fragment, read where its reference says and checked against it. Not
    /// cached, unlike a page: the object cache already keeps the open file that holds it, so
    /// keeping it here too would hold its bytes twice.
    /// </summary>
    public async ValueTask<ReadOnlyMemory<byte>> ReadFragmentAsync(
        PageReference reference, CancellationToken cancellationToken) =>
        await ReadStoredAsync(reference, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// A page or a fragment read out of the commit object its reference names. A missing object —
    /// one vacuum has taken — is reported against the version being read rather than answered from
    /// what is left.
    /// </summary>
    private async ValueTask<byte[]> ReadStoredAsync(PageReference reference, CancellationToken cancellationToken)
    {
        string key = CommitKey.For(reference.Version);
        try
        {
            if (!TryGetLearnedStart(reference.Version, out long start))
            {
                start = await ReadStartAsync(reference.Version, key, cancellationToken).ConfigureAwait(false);
                Interlocked.Increment(ref _reads);
                return await CommitObject
                    .ReadPageAsync(_store, key, reference, start, cancellationToken).ConfigureAwait(false);
            }

            Interlocked.Increment(ref _reads);
            try
            {
                return await CommitObject
                    .ReadPageAsync(_store, key, reference, start, cancellationToken).ConfigureAwait(false);
            }
            catch (CommitFormatException)
            {
                // A start learned before, from a header or an open, is where the region of the object
                // there then started: one removed as torn and written again under its version may
                // start elsewhere. Its preamble says where it starts now, and the page is read again
                // only when that moved; a page that does not hash where the object says is torn.
                long now = await ReadStartAsync(reference.Version, key, cancellationToken).ConfigureAwait(false);
                if (now == start)
                {
                    throw;
                }

                Interlocked.Increment(ref _reads);
                return await CommitObject
                    .ReadPageAsync(_store, key, reference, now, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (ObjectNotFoundException missing) when (Reading != 0 && missing.Version == 0)
        {
            throw ObjectNotFoundException.InVersion(key, Reading, missing);
        }
    }

    /// <summary>Where a version's body starts, as this source or the handle learned it without asking.</summary>
    private bool TryGetLearnedStart(ulong version, out long start)
    {
        if (_starts.TryGetValue(version, out start))
        {
            return true;
        }

        if (_cache is not null && _cache.TryGetStart(version, out start))
        {
            _starts[version] = start;
            return true;
        }

        return false;
    }

    /// <summary>Where a version's body starts, read from its preamble: one small read, remembered by the handle.</summary>
    private async ValueTask<long> ReadStartAsync(ulong version, string key, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _reads);
        long start = await CommitObject.PagesStartAsync(_store, key, cancellationToken).ConfigureAwait(false);
        Know(version, start);
        return start;
    }

    /// <summary>
    /// A page the handle kept from an earlier version: read from the store then, or lying in the
    /// region the read opening its own version brought back, checked as it is taken from there.
    /// </summary>
    private bool TryGetKept(PageReference reference, out ReadOnlyMemory<byte> page)
    {
        page = default;
        if (_cache is null)
        {
            return false;
        }

        if (_cache.TryGet(reference, out page))
        {
            return true;
        }

        if (!_cache.TryGetHeld(reference.Version, out ReadOnlyMemory<byte> region) || !Within(reference, region))
        {
            return false;
        }

        page = region.Slice((int)reference.Offset, reference.Length);
        if (XxHash128.HashToUInt128(page.Span) == reference.Hash)
        {
            return true;
        }

        // Kept from an open of the version whose object is not the one there now, as a torn object
        // removed and written again is not: the store says what it holds.
        _cache.Forget(reference.Version);
        page = default;
        return false;
    }

    /// <summary>Whether the page <paramref name="reference"/> names lies inside <paramref name="region"/>.</summary>
    private static bool Within(PageReference reference, ReadOnlyMemory<byte> region) =>
        reference.Offset >= 0 && reference.Offset + reference.Length <= region.Length;

    /// <summary>The page <paramref name="reference"/> names, when it lies inside a region an open read held.</summary>
    /// <exception cref="TornCommitException">It lies there and does not hash to its reference.</exception>
    private static bool TryHeld(PageReference reference, ReadOnlyMemory<byte> region, out ReadOnlyMemory<byte> page)
    {
        if (!Within(reference, region))
        {
            page = default;
            return false;
        }

        try
        {
            page = Check(reference, region.Slice((int)reference.Offset, reference.Length));
        }
        catch (CommitFormatException torn)
        {
            // Held once, as any page a header does not inline: a byte torn in it is refused by its
            // hash, never read as another page.
            throw TornCommitException.Of(reference.Version, torn);
        }

        return true;
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
