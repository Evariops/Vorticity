using System;
using System.Collections.Concurrent;
using System.IO.Hashing;
using System.Threading;
using System.Threading.Tasks;

namespace Vorticity.Dataset;

/// <summary>
/// An <see cref="IPageSource"/> over a dataset's commit objects: a page inlined in a header or
/// still in the builder's buffer costs no request, anything else is a ranged read of the commit
/// object its reference names. Every page is checked against the reference that led here.
/// </summary>
internal sealed class CommitPageSource : IPageSource
{
    private readonly IObjectStore _store;
    // Concurrent because a walk reads a window of siblings at once; two reads of one page race to
    // the same bytes, and either may keep them.
    private readonly ConcurrentDictionary<PageReference, ReadOnlyMemory<byte>> _known = [];

    private readonly ConcurrentDictionary<ulong, long> _starts = [];
    private long _reads;
    private CommitObjectBuilder? _builder;
    private ulong _building;
    private byte[]? _built;
    private long _builtStart;

    public CommitPageSource(IObjectStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

    /// <summary>How many pages were read through the store rather than found in hand.</summary>
    public long Reads => Interlocked.Read(ref _reads);

    /// <summary>
    /// The version this source reads for, which a missing commit object is reported against; 0 when
    /// it reads for none.
    /// </summary>
    public ulong Reading { get; init; }

    /// <summary>The bytes of a page this source already holds, without any request.</summary>
    public bool TryGetKnown(PageReference reference, out ReadOnlyMemory<byte> page) =>
        _known.TryGetValue(reference, out page);

    /// <summary>Records where a version's pages region starts, learned without a request.</summary>
    public void Know(ulong version, long pagesStart) => _starts[version] = pagesStart;

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

    /// <summary>Takes the bytes the builder produced, so the new version's pages can be read back.</summary>
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
            return Check(reference, bytes.AsMemory((int)(_builtStart + reference.Offset), reference.Length));
        }

        if (reference.Version == _building && _builder is { } builder
            && builder.TryGetPage(reference, out ReadOnlyMemory<byte> fresh))
        {
            return fresh;
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

    /// <summary>Where a version's body starts: one small read per version, remembered.</summary>
    private async ValueTask<long> StartAsync(ulong version, string key, CancellationToken cancellationToken)
    {
        if (!_starts.TryGetValue(version, out long start))
        {
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
