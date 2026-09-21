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
using Vorticity.File;

namespace Vorticity;

/// <summary>How an index's regions are laid out.</summary>
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

/// <summary>What one index of a file is, as its directory says, for tooling.</summary>
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

/// <summary>What <see cref="VortexFile.VerifyIndexesAsync"/> found.</summary>
/// <param name="Held">Listed regions whose bytes hold their checksum.</param>
/// <param name="Torn">The regions whose bytes do not, named: entry, origin, offset and length.</param>
/// <param name="Bare">Listed regions that carry no checksum (a version 1 directory).</param>
/// <param name="FileHashHolds">
/// Whether the file's XXH3-128 is the one every fragment that recorded one recorded; null when none did.
/// </param>
public sealed record VortexIndexVerification(long Held, IReadOnlyList<string> Torn, long Bare, bool? FileHashHolds)
{
    /// <summary>Whether everything checked holds.</summary>
    public bool Holds => Torn.Count == 0 && FileHashHolds != false;
}

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
    /// <param name="Source">The file itself, or an index fragment given by the read options.</param>
    /// <param name="Encodings">The array encodings its payloads name; null for the file's own footer.</param>
    /// <param name="Owned">Whether the file disposes the source with itself.</param>
    /// <param name="FileHash">The file's XXH3-128 a fragment recorded, which only a verification reads.</param>
    private sealed record IndexOrigin(
        ISegmentSource Source, IReadOnlyList<string>? Encodings, bool Owned, UInt128? FileHash = null);

    /// <summary>
    /// Where a run's regions are read: the file itself, or the fragment it came from.
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
    /// run's fragment carries.
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
    /// Whether the postscript names an index directory at all, or the read options name fragments
    /// to add (<see cref="VortexReadOptions.IndexFragments"/>).
    /// </summary>
    internal bool HasIndexDirectory =>
        TryGetMetadataIndex(IndexDirectory.MetadataKeyUtf8, out _) || ReadOptions.IndexFragments.Count > 0;

    /// <summary>
    /// Why the file's index directory was not used, when it was present and refused; otherwise
    /// <see langword="null"/>. Meaningful after <see cref="ReadIndexDirectoryAsync"/>.
    /// </summary>
    internal string? IndexDirectoryRefusal => _indexState?.Refusal;

    /// <summary>
    /// Why each fragment of <see cref="VortexReadOptions.IndexFragments"/> was not used, in the
    /// order they were given: <see langword="null"/> for one whose every entry was taken, otherwise
    /// the reason it was refused whole or the entries that were left out. Empty before
    /// <see cref="ReadIndexDirectoryAsync"/>, and when no fragment was given.
    /// </summary>
    internal IReadOnlyList<string?> IndexFragmentRefusals => _indexState?.FragmentRefusals ?? [];

    /// <summary>
    /// What the file's indexes are, once its directory has been read -- by a scan, by
    /// <see cref="ReadIndexesAsync"/> or at the open (<see cref="VortexOpenOptions.PreloadIndexes"/>);
    /// <see langword="null"/> before, and empty for a file with none. Reading it costs no request.
    /// </summary>
    /// <exception cref="ObjectDisposedException">The file has been disposed.</exception>
    internal IReadOnlyList<VortexIndexInfo>? Indexes
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
    internal async ValueTask<IReadOnlyList<VortexIndexInfo>> ReadIndexesAsync(CancellationToken cancellationToken = default) =>
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
    /// The directory is read the way every metadata value is, lazily on the first request and never
    /// at open: the first call reads one metadata segment, later calls read nothing. Nothing here
    /// can fail the file. A directory whose row count is not the file's is refused whole, because a
    /// stale directory left by a failed append proves nothing; an entry is dropped on its own when
    /// its kind is unknown or its runs step outside the file. A file whose directory is absent,
    /// stale, malformed or of an unknown version simply reads as one written without indexes, its
    /// reason kept for tooling, and the only exceptions that escape are the caller's cancellation
    /// and a disposed file.
    /// </remarks>
    internal async ValueTask<IndexDirectory?> ReadIndexDirectoryAsync(CancellationToken cancellationToken = default)
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

    /// <summary>The directory the file names, then every fragment the read options add to it.</summary>
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
            // Bound by the identity or the store token the fragment records: a fragment of another
            // object, or of another version of this one, is refused whole.
            MemorySegmentSource source = new MemorySegmentSource(blobs[i]);
            (IndexDirectory? fragment, string? why) = await IndexContainer
                .ReadAsync(source, blobs[i].Length, this, cancellationToken).ConfigureAwait(false);
            origins.Add(new IndexOrigin(source, fragment?.ArrayEncodings, Owned: true, fragment?.FileHash));
            fragments.Add(fragment);
            refusals[i] = why;
        }

        IndexDirectory? merged = IndexFragmentMerge.Merge(named, fragments, refusals, (ulong)RowCount, EntryName);
        return new IndexState(merged, refusal, origins, refusals);
    }

    /// <summary>The file's own directory, or none.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    private async ValueTask<(IndexDirectory? Directory, string? Refusal, IndexOrigin Origin)> ReadNamedDirectoryAsync(
        CancellationToken cancellationToken)
    {
        IndexOrigin file = new IndexOrigin(_source, null, Owned: false);
        if (!TryGetMetadataIndex(IndexDirectory.MetadataKeyUtf8, out int index))
        {
            return (null, null, file);
        }

        // A run always lies before the directory, because runs are written out before the zone maps
        // and the directory after the statistics, so the directory's own offset bounds every run
        // from above.
        SegmentSpec spec = GetMetadataSegment(index);
        using SegmentOwner owner = await ReadMetadataAsync(index, cancellationToken).ConfigureAwait(false);
        return IndexDirectory.TryParse(
            owner.Buffer.Span, (ulong)RowCount, spec.Offset, out IndexDirectory? directory, out string? reason)
            ? (directory, null, file)
            : (null, reason, file);
    }

    /// <summary>
    /// Checks every index region the directory lists against its checksum, wherever it is read — the
    /// file or a fragment — and the file's bytes against the XXH3-128 a fragment recorded: the hash
    /// no reader computes, computed here, offline.
    /// </summary>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>What held and what did not.</returns>
    /// <exception cref="ObjectDisposedException">The file has been disposed.</exception>
    /// <remarks>
    /// Every region listed is read once, and the file whole once when a fragment recorded its hash:
    /// this is the tool's check, `vxdump --verify`, never a scan's. The pages and tree nodes under a
    /// listed region are checked as they are read, by the reader that reads them.
    /// </remarks>
    public async ValueTask<VortexIndexVerification> VerifyIndexesAsync(CancellationToken cancellationToken = default)
    {
        IndexDirectory? directory = await ReadIndexDirectoryAsync(cancellationToken).ConfigureAwait(false);
        long held = 0;
        long bare = 0;
        List<string> torn = [];
        foreach (IndexEntry entry in directory?.Entries ?? [])
        {
            foreach (IndexRun run in entry.Runs)
            {
                foreach (IndexSegment segment in run.Payload)
                {
                    if (segment.Checksum is null)
                    {
                        bare++;
                        continue;
                    }

                    using SegmentOwner bytes = await IndexSourceOf(run)
                        .ReadRangeAsync((long)segment.Offset, (int)segment.Length, 1, cancellationToken).ConfigureAwait(false);
                    if (segment.Holds(bytes.Buffer.Span))
                    {
                        held++;
                    }
                    else
                    {
                        torn.Add(FormattableString.Invariant(
                            $"{entry.Kind} on '{ColumnOf(entry)}', origin {run.Origin}, region {segment.Offset}+{segment.Length}"));
                    }
                }
            }
        }

        bool? hashHolds = null;
        UInt128? actual = null;
        foreach (IndexOrigin origin in _indexState!.Origins)
        {
            if (origin.FileHash is not { } recorded)
            {
                continue;
            }

            actual ??= await IndexContainer.HashAsync(_source, FileLength, cancellationToken).ConfigureAwait(false);
            hashHolds = (hashHolds ?? true) && actual == recorded;
        }

        return new VortexIndexVerification(held, torn, bare, hashHolds);
    }

    /// <summary>An entry as a refusal names it: its kind and its column.</summary>
    private string EntryName(IndexEntry entry) => $"{entry.Kind} on '{ColumnOf(entry)}'";

    /// <summary>Closes the fragment sources a state opened; the file's own is not its to close.</summary>
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
