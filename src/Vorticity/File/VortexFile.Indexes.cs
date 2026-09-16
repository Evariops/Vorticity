// The index directory of docs/10-indexes.md §4.1, read the way every metadata value is read:
// lazily, on the first request, and never at open (docs/02-format.md §2).
//
// NOTHING HERE CAN FAIL THE FILE. A directory that is absent, stale, malformed or of an unknown
// version reads as "no index" with its reason kept for tooling; the scan is then exactly the scan of
// a file written without indexes. The only exceptions that escape are the caller's own
// cancellation and a disposed file.
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Buffers;
using Vorticity.Indexes;
using Vorticity.Serialization.Schemas;

namespace Vorticity.File;

public sealed partial class VortexFile
{
    // Benign race, as for the layout tree: two first readers may both parse, and the results are
    // equal. The pair is published as one object so a reader never sees a directory from one parse
    // and a refusal from another.
    private sealed record IndexState(IndexDirectory? Directory, string? Refusal);

    private IndexState? _indexState;
    private IndexRunCache? _runCache;

    /// <summary>
    /// The decoded runs this file keeps for its cursors, created on first use and bounded by
    /// <see cref="VortexReadOptions.IndexCacheBytes"/>.
    /// </summary>
    internal IndexRunCache RunCache
    {
        get
        {
            IndexRunCache? cache = _runCache;
            if (cache is null)
            {
                Interlocked.CompareExchange(ref _runCache, new IndexRunCache(ReadOptions.IndexCacheBytes), null);
                cache = _runCache!;
            }

            return cache;
        }
    }

    /// <summary>Whether the postscript names an index directory at all.</summary>
    public bool HasIndexDirectory => TryGetMetadataIndex(IndexDirectory.MetadataKeyUtf8, out _);

    /// <summary>
    /// Why the file's index directory was not used, when it was present and refused; otherwise
    /// <see langword="null"/>. Meaningful after <see cref="ReadIndexDirectoryAsync"/>.
    /// </summary>
    public string? IndexDirectoryRefusal => _indexState?.Refusal;

    /// <summary>
    /// The file's index directory, holding only the entries that passed the reader's checks, or
    /// <see langword="null"/> when there is none or it was refused.
    /// </summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The directory, or <see langword="null"/>.</returns>
    /// <remarks>
    /// A directory is refused WHOLE when its row count is not the file's -- a stale directory after
    /// a failed append proves nothing -- and an entry is dropped ALONE when its kind is unknown or
    /// its runs step outside the file (docs/10-indexes.md §4.1). The first call reads one metadata
    /// segment; later calls read nothing.
    /// </remarks>
    public async ValueTask<IndexDirectory?> ReadIndexDirectoryAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (_indexState is { } known)
        {
            return known.Directory;
        }

        if (!TryGetMetadataIndex(IndexDirectory.MetadataKeyUtf8, out int index))
        {
            _indexState = new IndexState(null, null);
            return null;
        }

        // A run lies before the directory: runs go out before the zone maps, the directory after
        // the statistics (§7.2), so the directory's own offset bounds every run from above.
        SegmentSpec spec = GetMetadataSegment(index);
        using SegmentOwner owner = await ReadMetadataAsync(index, cancellationToken).ConfigureAwait(false);
        IndexState state = IndexDirectory.TryParse(
            owner.Buffer.Span, (ulong)RowCount, spec.Offset, out IndexDirectory? directory, out string? reason)
            ? new IndexState(directory, null)
            : new IndexState(null, reason);
        _indexState = state;
        return state.Directory;
    }
}
