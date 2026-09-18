// The index directory of docs/10-indexes.md §4.1, read the way every metadata value is read:
// lazily, on the first request, and never at open (docs/02-format.md §2).
//
// NOTHING HERE CAN FAIL THE FILE. A directory that is absent, stale, malformed or of an unknown
// version reads as "no index" with its reason kept for tooling; the scan is then exactly the scan of
// a file written without indexes. The only exceptions that escape are the caller's own
// cancellation and a disposed file.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Indexes;
using Vorticity.IO;
using Vorticity.Serialization.Schemas;
using Vorticity.Types;

namespace Vorticity.File;

/// <summary>How an index's regions are laid out (docs/10-indexes.md §4.2, docs/13-dataset.md §6).</summary>
public enum VortexIndexLayout
{
    /// <summary>No payload: the index is its runs (a dictionary probe).</summary>
    None,

    /// <summary>Every region is listed in the directory.</summary>
    Listed,

    /// <summary>A locating run's segment table is in fence pages below the listed root children.</summary>
    FencePages,

    /// <summary>Bloom filters in a tree below the listed roots.</summary>
    FilterTree,
}

/// <summary>What one index of a file is, as its directory says, for tooling (docs/12-index-reads.md §8.1).</summary>
/// <param name="Kind">The kind (<c>vorticity.bloom.sbbf.v1</c>, ...).</param>
/// <param name="Column">The column it indexes: a dotted path, or the columns of a composite key in parentheses.</param>
/// <param name="BlockLength">Rows per block.</param>
/// <param name="Runs">Its runs.</param>
/// <param name="Blocks">The blocks its runs cover.</param>
/// <param name="Entries">The entries of a locating index; 0 for a skipping one.</param>
/// <param name="ListedBytes">The bytes of the regions the directory lists; a paged or tree layout holds more below them.</param>
/// <param name="Layout">How its regions are laid out.</param>
public sealed record VortexIndexInfo(
    string Kind, string Column, long BlockLength, int Runs, long Blocks, long Entries, long ListedBytes,
    VortexIndexLayout Layout);

public sealed partial class VortexFile
{
    // Benign race, as for the layout tree: two first readers may both parse, and the results are
    // equal. The state is published as one object so a reader never sees a directory from one parse
    // and a refusal from another.
    private sealed record IndexState(
        IndexDirectory? Directory,
        string? Refusal,
        IReadOnlyList<IndexOrigin> Origins,
        IReadOnlyList<string?> FragmentRefusals);

    /// <summary>Where one origin's index bytes are read, and the encoding table its payloads name.</summary>
    /// <param name="Source">The file itself, its sidecar, or a fragment (docs/13-dataset.md §6.4).</param>
    /// <param name="Encodings">The array encodings its payloads name; null for the file's own footer.</param>
    /// <param name="Owned">Whether the file disposes the source with itself.</param>
    private sealed record IndexOrigin(ISegmentSource Source, IReadOnlyList<string>? Encodings, bool Owned);

    /// <summary>
    /// Where a run's regions are read: the file itself, its sidecar, or the fragment it came from.
    /// </summary>
    /// <param name="run">A run of the directory <see cref="ReadIndexDirectoryAsync"/> returned.</param>
    /// <returns>The source its offsets count in.</returns>
    internal ISegmentSource IndexSourceOf(IndexRun run) => IndexSourceOf(run.Origin);

    /// <summary>Where the regions of the runs of one origin are read.</summary>
    /// <param name="origin">A run's <see cref="IndexRun.Origin"/>.</param>
    /// <returns>The source their offsets count in.</returns>
    internal ISegmentSource IndexSourceOf(int origin) => OriginOf(origin).Source;

    /// <summary>
    /// A context to decode a run's payloads in: over the file's encoding table, or over the table the
    /// run's sidecar or fragment carries (docs/10-indexes.md §8, docs/13-dataset.md §6.4).
    /// </summary>
    /// <param name="run">A run of the directory <see cref="ReadIndexDirectoryAsync"/> returned.</param>
    /// <returns>The context, which the caller disposes.</returns>
    internal ScanContext CreateIndexContext(IndexRun run) => CreateIndexContext(run.Origin);

    /// <summary>A context to decode the payloads of the runs of one origin in.</summary>
    /// <param name="origin">A run's <see cref="IndexRun.Origin"/>.</param>
    /// <returns>The context, which the caller disposes.</returns>
    internal ScanContext CreateIndexContext(int origin) =>
        OriginOf(origin).Encodings is { } ids
            ? new ScanContext(ids.ToArray(), ReadOptions)
            : new ScanContext(this, ScanContext.MetadataCapacity);

    private IndexOrigin OriginOf(int origin) =>
        (_indexState ?? throw new InvalidOperationException("A run is read before the directory that lists it."))
        .Origins[origin];

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

    /// <summary>
    /// Whether the postscript names an index directory at all, or the read options name a sidecar
    /// to take one from (<see cref="VortexReadOptions.IndexSidecarPath"/>) or fragments to add
    /// (<see cref="VortexReadOptions.IndexFragments"/>).
    /// </summary>
    public bool HasIndexDirectory =>
        TryGetMetadataIndex(IndexDirectory.MetadataKeyUtf8, out _)
        || ReadOptions.IndexSidecarPath is not null
        || ReadOptions.IndexFragments.Count > 0;

    /// <summary>
    /// Why the file's index directory was not used, when it was present and refused; otherwise
    /// <see langword="null"/>. Meaningful after <see cref="ReadIndexDirectoryAsync"/>.
    /// </summary>
    public string? IndexDirectoryRefusal => _indexState?.Refusal;

    /// <summary>
    /// Why each fragment of <see cref="VortexReadOptions.IndexFragments"/> was not used, in the
    /// order they were given: <see langword="null"/> for one whose every entry was taken, otherwise
    /// the reason it was refused whole or the entries that were left out. Empty before
    /// <see cref="ReadIndexDirectoryAsync"/>, and when no fragment was given.
    /// </summary>
    public IReadOnlyList<string?> IndexFragmentRefusals => _indexState?.FragmentRefusals ?? [];

    /// <summary>
    /// What the file's indexes are, once its directory has been read -- by a scan, by
    /// <see cref="ReadIndexesAsync"/> or at the open (<see cref="VortexOpenOptions.PreloadIndexes"/>);
    /// <see langword="null"/> before, and empty for a file with none. Reading it costs no request.
    /// </summary>
    /// <exception cref="ObjectDisposedException">The file has been disposed.</exception>
    public IReadOnlyList<VortexIndexInfo>? Indexes
    {
        get
        {
            ThrowIfDisposed();
            return _indexState is { } state ? Describe(state.Directory) : null;
        }
    }

    /// <summary>What the file's indexes are, reading the directory when it has not been read.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>One description per index the reader kept; empty for a file with none.</returns>
    /// <exception cref="ObjectDisposedException">The file has been disposed.</exception>
    public async ValueTask<IReadOnlyList<VortexIndexInfo>> ReadIndexesAsync(CancellationToken cancellationToken = default) =>
        Describe(await ReadIndexDirectoryAsync(cancellationToken).ConfigureAwait(false));

    private IReadOnlyList<VortexIndexInfo> Describe(IndexDirectory? directory)
    {
        if (directory is null)
        {
            return [];
        }

        List<VortexIndexInfo> infos = new List<VortexIndexInfo>(directory.Entries.Count);
        foreach (IndexEntry entry in directory.Entries)
        {
            long blocks = 0;
            long entries = 0;
            long bytes = 0;
            bool paged = false;
            foreach (IndexRun run in entry.Runs)
            {
                blocks += run.BlockCount;
                entries += (long)run.EntryCount;
                paged |= KeyRunOptions.IsPaged(run.OptionBytes);
                foreach (IndexSegment segment in run.Payload)
                {
                    bytes += segment.Length;
                }
            }

            VortexIndexLayout layout = entry.Kind switch
            {
                IndexKinds.BloomSbbf or IndexKinds.BloomNgram3 => VortexIndexLayout.FilterTree,
                _ when !IndexKinds.HasPayload(entry.Kind) => VortexIndexLayout.None,
                _ when paged => VortexIndexLayout.FencePages,
                _ => VortexIndexLayout.Listed,
            };
            infos.Add(new VortexIndexInfo(
                entry.Kind, ColumnOf(entry), (long)entry.BlockLength, entry.Runs.Count, blocks, entries, bytes, layout));
        }

        return infos;
    }

    /// <summary>An entry's column as a caller names it: a dotted path, or a composite key's columns.</summary>
    private string ColumnOf(IndexEntry entry)
    {
        if (entry.ColumnPath.Count > 0)
        {
            return PathOf(entry.ColumnPath);
        }

        if (KeyRunOptions.TryParseEntry(entry.Options, out _, out _, out List<uint[]> keyColumns) && keyColumns.Count > 0)
        {
            return "(" + string.Join(", ", keyColumns.Select(PathOf)) + ")";
        }

        return string.Empty;
    }

    private string PathOf(IReadOnlyList<uint> fields)
    {
        List<string> names = [];
        DType dtype = _schema;
        foreach (uint field in fields)
        {
            if (dtype.Kind != DTypeKind.Struct || field >= (uint)dtype.FieldCount)
            {
                names.Add("?");
                break;
            }

            names.Add(dtype.GetFieldName((int)field));
            dtype = dtype.GetField((int)field);
        }

        return string.Join('.', names);
    }

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

        IndexState state = await ReadIndexStateAsync(cancellationToken).ConfigureAwait(false);
        if (Interlocked.CompareExchange(ref _indexState, state, null) is not null)
        {
            // Another reader won the race; its sources are the ones kept.
            await DisposeIndexSourcesAsync(state).ConfigureAwait(false);
        }

        return _indexState!.Directory;
    }

    /// <summary>The directory the file names, then every fragment the read options add to it (13 §6.4).</summary>
    /// <param name="cancellationToken">Cancels the reads.</param>
    private async ValueTask<IndexState> ReadIndexStateAsync(CancellationToken cancellationToken)
    {
        (IndexDirectory? named, string? refusal, IndexOrigin origin) =
            await ReadNamedDirectoryAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<ReadOnlyMemory<byte>> blobs = ReadOptions.IndexFragments;
        if (blobs.Count == 0)
        {
            return new IndexState(named, refusal, [origin], []);
        }

        List<IndexOrigin> origins = new List<IndexOrigin>(blobs.Count + 1) { origin };
        List<IndexDirectory?> fragments = new List<IndexDirectory?>(blobs.Count);
        string?[] refusals = new string?[blobs.Count];
        for (int i = 0; i < blobs.Count; i++)
        {
            // Bound like a sidecar, by the identity or the store token the fragment records: a
            // fragment of another object, or of another version of this one, is refused whole.
            MemorySegmentSource source = new MemorySegmentSource(blobs[i]);
            (IndexDirectory? fragment, string? why) = await IndexSidecar
                .ReadAsync(source, blobs[i].Length, this, "fragment", cancellationToken).ConfigureAwait(false);
            origins.Add(new IndexOrigin(source, fragment?.ArrayEncodings, Owned: true));
            fragments.Add(fragment);
            refusals[i] = why;
        }

        IndexDirectory? merged = IndexFragmentMerge.Merge(named, fragments, refusals, (ulong)RowCount, EntryName);
        return new IndexState(merged, refusal, origins, refusals);
    }

    /// <summary>The file's own directory, else the sidecar the read options name, else none.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    private async ValueTask<(IndexDirectory? Directory, string? Refusal, IndexOrigin Origin)> ReadNamedDirectoryAsync(
        CancellationToken cancellationToken)
    {
        IndexOrigin file = new IndexOrigin(_source, null, Owned: false);
        if (!TryGetMetadataIndex(IndexDirectory.MetadataKeyUtf8, out int index))
        {
            // No directory of its own: the sidecar, when the caller named one (§8).
            if (ReadOptions.IndexSidecarPath is not { } sidecar)
            {
                return (null, null, file);
            }

            (ISegmentSource? source, IndexDirectory? found, string? why) =
                await IndexSidecar.OpenAsync(sidecar, this, cancellationToken).ConfigureAwait(false);
            return source is null
                ? (null, why, file)
                : (found, why, new IndexOrigin(source, found?.ArrayEncodings, Owned: true));
        }

        // A run lies before the directory: runs go out before the zone maps, the directory after
        // the statistics (§7.2), so the directory's own offset bounds every run from above.
        SegmentSpec spec = GetMetadataSegment(index);
        using SegmentOwner owner = await ReadMetadataAsync(index, cancellationToken).ConfigureAwait(false);
        return IndexDirectory.TryParse(
            owner.Buffer.Span, (ulong)RowCount, spec.Offset, out IndexDirectory? directory, out string? reason)
            ? (directory, null, file)
            : (null, reason, file);
    }

    /// <summary>An entry as a refusal names it: its kind and its column.</summary>
    private string EntryName(IndexEntry entry) => $"{entry.Kind} on '{ColumnOf(entry)}'";

    /// <summary>Closes the sidecar and fragment sources a state opened; the file's own is not its to close.</summary>
    private static async ValueTask DisposeIndexSourcesAsync(IndexState state)
    {
        foreach (IndexOrigin origin in state.Origins)
        {
            if (origin.Owned)
            {
                await origin.Source.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
