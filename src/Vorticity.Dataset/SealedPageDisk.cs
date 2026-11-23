using System.IO.Hashing;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.IO;

namespace Vorticity.Dataset;

/// <summary>
/// A sealed dataset's tree pages on the session's synopsis cache: each page as the header of the commit object that holds it
/// and the frames that hold it, as the store holds them, under the hash of its plaintext. A handle opened later, by this
/// process or the next, reads its tree from the disk where its own cache of pages starts empty, and decrypts each page as a
/// read from the store does. No plaintext and no key reach the disk.
/// </summary>
internal sealed class SealedPageDisk
{
    /// <summary>The most bytes a kept form of a page takes beyond the page: the header, the kept form's own, and a frame at each end.</summary>
    private const int MaxOverheadBytes = (64 << 10) + (2 * ((1 << SealedObjectStore.FrameLog2) + Sealing.SealedFormat.TagBytes));

    private readonly SealedObjectStore _store;
    private readonly SynopsisCache _cache;

    private SealedPageDisk(SealedObjectStore store, SynopsisCache cache)
    {
        _store = store;
        _cache = cache;
    }

    /// <summary>The store the pages are read from, which seals the dataset's objects.</summary>
    internal SealedObjectStore Store => _store;

    /// <summary>The pages of a dataset read through <paramref name="store"/> on <paramref name="cache"/>; null for a plain dataset, and without a cache.</summary>
    internal static SealedPageDisk? Of(IObjectStore store, SynopsisCache? cache) =>
        store is SealedObjectStore { Seals: true } sealedStore && cache is not null ? new SealedPageDisk(sealedStore, cache) : null;

    /// <summary>
    /// The page <paramref name="reference"/> names, from the disk: opened through the store's checks and hashed to the
    /// reference. Null when the disk does not hold it; what does not open, or does not hash to it, is dropped.
    /// </summary>
    internal async ValueTask<byte[]?> TryReadAsync(PageReference reference, CancellationToken cancellationToken)
    {
        if (_cache.TryReadSealedPage(reference.Hash, reference.Length, reference.Length + MaxOverheadBytes) is not { } kept)
        {
            return null;
        }

        byte[]? page = await _store.OpenKeptPageAsync(reference.Version, kept, reference.Length, cancellationToken).ConfigureAwait(false);
        if (page is null || XxHash128.HashToUInt128(page) != reference.Hash)
        {
            _cache.DropSealedPage(reference.Hash, reference.Length);
            return null;
        }

        return page;
    }

    /// <summary>Keeps <paramref name="kept"/>, what the store's read of the page <paramref name="reference"/> names made, once the page hashed to it.</summary>
    internal void Admit(PageReference reference, byte[] kept) => _cache.AdmitSealedPage(reference.Hash, reference.Length, kept);
}
