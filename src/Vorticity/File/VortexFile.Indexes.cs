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
    // equal. The pair is published as one object so a reader never sees a directory from one parse
    // and a refusal from another.
    private sealed record IndexState(IndexDirectory? Directory, string? Refusal, ISegmentSource? Sidecar = null);

    /// <summary>
    /// Where the index payloads are read from: the file itself, or its sidecar once
    /// <see cref="ReadIndexDirectoryAsync"/> took the directory from one.
    /// </summary>
    internal ISegmentSource IndexSource => _indexState?.Sidecar ?? _source;

    /// <summary>
    /// A context to decode index payloads in: over the file's encoding table, or over the sidecar's
    /// own (docs/10-indexes.md §8).
    /// </summary>
    internal ScanContext CreateIndexContext() =>
        _indexState is { Sidecar: not null, Directory.ArrayEncodings: { } ids }
            ? new ScanContext(ids.ToArray(), ReadOptions)
            : new ScanContext(this, ScanContext.MetadataCapacity);

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
    /// to take one from (<see cref="VortexReadOptions.IndexSidecarPath"/>).
    /// </summary>
    public bool HasIndexDirectory =>
        TryGetMetadataIndex(IndexDirectory.MetadataKeyUtf8, out _) || ReadOptions.IndexSidecarPath is not null;

    /// <summary>
    /// Why the file's index directory was not used, when it was present and refused; otherwise
    /// <see langword="null"/>. Meaningful after <see cref="ReadIndexDirectoryAsync"/>.
    /// </summary>
    public string? IndexDirectoryRefusal => _indexState?.Refusal;

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

        if (!TryGetMetadataIndex(IndexDirectory.MetadataKeyUtf8, out int index))
        {
            // No directory of its own: the sidecar, when the caller named one (§8).
            if (ReadOptions.IndexSidecarPath is not { } sidecar)
            {
                _indexState = new IndexState(null, null);
                return null;
            }

            (ISegmentSource? source, IndexDirectory? found, string? why) =
                await IndexSidecar.OpenAsync(sidecar, this, cancellationToken).ConfigureAwait(false);
            IndexState opened = new IndexState(found, why, source);
            if (Interlocked.CompareExchange(ref _indexState, opened, null) is not null && source is not null)
            {
                // Another reader won the race; its sidecar is the one kept.
                await source.DisposeAsync().ConfigureAwait(false);
            }

            return _indexState!.Directory;
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
