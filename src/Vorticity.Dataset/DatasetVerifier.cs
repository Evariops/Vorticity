using System;
using System.Buffers;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO.Hashing;
using System.Threading;
using System.Threading.Tasks;
using Vorticity;
using Vorticity.File;

namespace Vorticity.Dataset;

/// <summary>What to verify.</summary>
internal sealed record VerifyOptions
{
    /// <summary>The version to verify, or null for the latest.</summary>
    public ulong? Version { get; init; }

    /// <summary>
    /// A version already verified: what the two share is not checked again. Null, the default,
    /// verifies everything.
    /// </summary>
    public ulong? Since { get; init; }
}

/// <summary>What a verification checked and found.</summary>
public sealed record DatasetVerification
{
    /// <summary>The version verified.</summary>
    public ulong Version { get; init; }

    /// <summary>The version it was verified against, or 0 for a full verification.</summary>
    public ulong Since { get; init; }

    /// <summary>The tree pages read from the store and checked against their references.</summary>
    public long Pages { get; init; }

    /// <summary>The data objects hashed and opened.</summary>
    public long Objects { get; init; }

    /// <summary>The index fragments read and checked.</summary>
    public long Fragments { get; init; }

    /// <summary>The commit objects read whole and checked against their own checksum.</summary>
    public long Commits { get; init; }

    /// <summary>The objects whose entry records no content hash, as an imported one does not; their length is checked instead.</summary>
    public long Unhashed { get; init; }

    /// <summary>Each thing that does not hold, named.</summary>
    public ImmutableArray<string> Problems { get; init; } = [];

    /// <summary>Whether everything checked holds.</summary>
    public bool Holds => Problems.IsDefaultOrEmpty;
}

/// <summary>
/// Checks a version against everything its references and entries promise, offline. Every page is
/// read from its stored copy and never from the copy a header inlines, so that a torn stored page
/// hiding under a sound inlined twin is found; each problem is reported rather than thrown, so one
/// torn page stops only the walk below it.
/// </summary>
internal static class DatasetVerifier
{
    private const int HashChunk = 1 << 20;

    /// <summary>
    /// Verifies one version, whole or against one already verified: what the two share by reference
    /// is not checked again.
    /// </summary>
    /// <exception cref="ObjectNotFoundException">The store holds no dataset, or not that version.</exception>
    public static async ValueTask<DatasetVerification> VerifyAsync(
        IObjectStore store, VerifyOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        options ??= new VerifyOptions();
        ulong version = options.Version ?? await DatasetCommitter.NewestVersionAsync(store, cancellationToken).ConfigureAwait(false);
        if (version == 0)
        {
            throw ObjectNotFoundException.For(CommitKey.Prefix);
        }

        Verification run = new Verification(store, cancellationToken);
        CommitHeader target;
        try
        {
            target = (await CommitObject.OpenAsync(store, CommitKey.For(version), cancellationToken).ConfigureAwait(false)).Header;
        }
        catch (CommitFormatException torn)
        {
            // A header that does not open names no page: that is the whole report.
            return new DatasetVerification
            {
                Version = version,
                Since = options.Since ?? 0,
                Commits = 1,
                Problems = [$"'{CommitKey.For(version)}' does not open: {torn.Message}"],
            };
        }

        run.CheckInlined(target);
        await run.CheckStartsAsync(target).ConfigureAwait(false);
        CommitHeader? since = options.Since is { } older and not 0
            ? (await CommitObject.OpenAsync(store, CommitKey.For(older), cancellationToken).ConfigureAwait(false)).Header
            : null;

        DatasetLevels targetLevels = DatasetLevels.Of(target);
        DatasetLevels sinceLevels = since is null ? DatasetLevels.Empty : DatasetLevels.Of(since);
        int levels = Math.Max(targetLevels.Count, sinceLevels.Count);
        for (int level = 0; level < levels; level++)
        {
            await run.DiffAsync(targetLevels[level], sinceLevels[level]).ConfigureAwait(false);
        }

        await run.CheckEntriesAsync().ConfigureAwait(false);
        await run.CheckCommitsAsync(version).ConfigureAwait(false);
        return new DatasetVerification
        {
            Version = version,
            Since = options.Since ?? 0,
            Pages = run.Pages,
            Objects = run.Objects,
            Fragments = run.Fragments,
            Commits = run.Commits,
            Unhashed = run.Unhashed,
            Problems = [.. run.Problems],
        };
    }

    private sealed class Verification(IObjectStore store, CancellationToken cancellationToken)
    {
        // No header is ever inlined into it: every page it hands back was read from the store.
        private readonly CommitPageSource _pages = new CommitPageSource(store);
        private readonly HashSet<PageReference> _checked = [];
        private readonly HashSet<UInt128> _vouched = [];
        private readonly List<ObjectEntry> _entries = [];

        // The commit objects whose pages or fragments this verify read: each is checked whole once.
        private readonly SortedSet<ulong> _commits = [];

        internal List<string> Problems { get; } = [];

        internal long Commits => _commits.Count;

        internal long Pages { get; private set; }

        internal long Objects { get; private set; }

        internal long Fragments { get; private set; }

        internal long Unhashed { get; private set; }

        internal void CheckInlined(CommitHeader header)
        {
            foreach (CommitLevel level in header.Levels)
            {
                foreach (InlinedPage page in level.Inlined)
                {
                    if (XxHash128.HashToUInt128(page.Bytes.Span) != page.Reference.Hash)
                    {
                        Problems.Add(Invariant($"the header inlines a page of version {page.Reference.Version} at {page.Reference.Offset} whose bytes do not hash to its reference"));
                    }
                }
            }
        }

        /// <summary>
        /// Every place the header says an earlier version's pages start, against that version's own
        /// preamble: a reader reads the pages there without asking, and a wrong one would make every
        /// such page unreadable through this version while each verified whole.
        /// </summary>
        internal async ValueTask CheckStartsAsync(CommitHeader header)
        {
            foreach (PagesStart start in header.Starts)
            {
                long actual;
                try
                {
                    actual = await CommitObject.PagesStartAsync(store, CommitKey.For(start.Version), cancellationToken).ConfigureAwait(false);
                }
                catch (Exception unreadable) when (unreadable is CommitFormatException or ObjectNotFoundException)
                {
                    Problems.Add(Invariant($"the header says where version {start.Version}'s pages start, and that version does not say: {unreadable.Message}"));
                    continue;
                }

                if (actual != start.Offset)
                {
                    Problems.Add(Invariant($"the header says version {start.Version}'s pages start at {start.Offset}, and they start at {actual}"));
                }
            }
        }

        /// <summary>
        /// One level's tree against the same level of the older version, height by height: only the
        /// pages the target holds and the older does not are checked and walked further.
        /// </summary>
        internal async ValueTask DiffAsync(DatasetTree target, DatasetTree since)
        {
            HashSet<PageReference> ours = [];
            HashSet<PageReference> theirs = [];
            int height = Math.Max(target.Depth, since.Depth);
            for (; height >= 1; height--)
            {
                if (height == target.Depth)
                {
                    ours.Add(target.Root);
                }

                if (height == since.Depth)
                {
                    theirs.Add(since.Root);
                }

                HashSet<PageReference> nextOurs = [];
                HashSet<PageReference> nextTheirs = [];
                foreach (PageReference reference in ours)
                {
                    if (theirs.Contains(reference) || !_checked.Add(reference))
                    {
                        continue;
                    }

                    ReadOnlyMemory<byte>? page = await ReadAsync(reference, height).ConfigureAwait(false);
                    if (page is { } bytes)
                    {
                        Expand(bytes, height, nextOurs, _entries, null);
                    }
                }

                foreach (PageReference reference in theirs)
                {
                    if (ours.Contains(reference))
                    {
                        continue;
                    }

                    // The older version was verified before: its pages are read only to learn which
                    // entries it vouched for, and one that fails to read vouches for nothing.
                    ReadOnlyMemory<byte> bytes;
                    try
                    {
                        bytes = await _pages.ReadPageAsync(reference, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception unreadable) when (unreadable is TornCommitException or ObjectNotFoundException)
                    {
                        continue;
                    }

                    Expand(bytes, height, nextTheirs, null, _vouched);
                }

                ours = nextOurs;
                theirs = nextTheirs;
            }
        }

        private static void Expand(
            ReadOnlyMemory<byte> page, int height, HashSet<PageReference> children, List<ObjectEntry>? entries, HashSet<UInt128>? vouched)
        {
            if (height > 1)
            {
                foreach (InternalEntry child in TreePage.ReadInternal(page))
                {
                    children.Add(child.Child);
                }

                return;
            }

            foreach (TreeEntry leaf in TreePage.ReadLeaf(page))
            {
                if (vouched is not null)
                {
                    vouched.Add(XxHash128.HashToUInt128(leaf.Value.Span));
                }
                else
                {
                    entries!.Add(ObjectEntry.FromBytes(leaf.Value));
                }
            }
        }

        private async ValueTask<ReadOnlyMemory<byte>?> ReadAsync(PageReference reference, int height)
        {
            _commits.Add(reference.Version);
            try
            {
                ReadOnlyMemory<byte> page = await _pages.ReadPageAsync(reference, cancellationToken).ConfigureAwait(false);
                if (height > 1 ? TreePage.KindOf(page.Span) != TreePageKind.Internal : TreePage.KindOf(page.Span) != TreePageKind.Leaf)
                {
                    Problems.Add(Invariant($"the page of version {reference.Version} at {reference.Offset} is not the kind its height needs"));
                    return null;
                }

                Pages++;
                return page;
            }
            catch (Exception unreadable) when (unreadable is TornCommitException or ObjectNotFoundException or ArgumentOutOfRangeException)
            {
                Problems.Add(Invariant($"the page of version {reference.Version} at {reference.Offset}+{reference.Length}: {(unreadable is TornCommitException { InnerException: { } cause } ? cause : unreadable).Message}"));
                return null;
            }
        }

        internal async ValueTask CheckEntriesAsync()
        {
            foreach (ObjectEntry entry in _entries)
            {
                if (!_vouched.Contains(XxHash128.HashToUInt128(entry.ToBytes())))
                {
                    await CheckObjectAsync(entry).ConfigureAwait(false);
                }
            }
        }

        private async ValueTask CheckObjectAsync(ObjectEntry entry)
        {
            Objects++;
            ObjectHead? head = await store.HeadAsync(entry.Key, cancellationToken).ConfigureAwait(false);
            if (head is not { } found)
            {
                Problems.Add($"'{entry.Key}' is not in the store");
                return;
            }

            if (found.Length != entry.Bytes)
            {
                Problems.Add(Invariant($"'{entry.Key}' is {found.Length} bytes and its entry says {entry.Bytes}"));
                return;
            }

            if (entry.Hash == UInt128.Zero)
            {
                Unhashed++;
            }
            else if (await HashAsync(entry.Key, found.Length).ConfigureAwait(false) is var hash && hash != entry.Hash)
            {
                Problems.Add(Invariant($"'{entry.Key}' hashes to {hash:x32} and its entry says {entry.Hash:x32}"));
            }

            List<ReadOnlyMemory<byte>> fragments = [];
            foreach (PageReference reference in entry.Fragments)
            {
                Fragments++;
                _commits.Add(reference.Version);
                try
                {
                    fragments.AddRange(FragmentBundle.Unpack(
                        await _pages.ReadFragmentAsync(reference, cancellationToken).ConfigureAwait(false)));
                }
                catch (Exception unreadable) when (unreadable is CommitFormatException or ObjectNotFoundException or ArgumentOutOfRangeException)
                {
                    Problems.Add(Invariant($"'{entry.Key}': the fragment in version {reference.Version} at {reference.Offset}+{reference.Length}: {unreadable.Message}"));
                }
            }

            await CheckFileAsync(entry, fragments).ConfigureAwait(false);
        }

        private async ValueTask CheckFileAsync(ObjectEntry entry, List<ReadOnlyMemory<byte>> fragments)
        {
            VortexOpenOptions options = ObjectCache.OpenOptionsWith(fragments);
            ObjectSegmentSource source = new ObjectSegmentSource(store, entry.Key);
            await using (source.ConfigureAwait(false))
            {
                VortexFile file;
                try
                {
                    file = await VortexFile.OpenAsync(source, options, cancellationToken).ConfigureAwait(false);
                }
                catch (VortexFormatException unreadable)
                {
                    Problems.Add($"'{entry.Key}' does not open: {unreadable.Message}");
                    return;
                }

                await using (file.ConfigureAwait(false))
                {
                    // The file holds its deleted rows too, which the entry counts apart.
                    if (file.RowCount != entry.PhysicalRows)
                    {
                        Problems.Add(entry.HasDeletions
                            ? Invariant($"'{entry.Key}' holds {file.RowCount} rows and its entry says {entry.Rows} and {entry.DeletedRows} deleted")
                            : Invariant($"'{entry.Key}' holds {file.RowCount} rows and its entry says {entry.Rows}"));
                    }

                    // A read decodes the vector when it opens the object; verify decodes every one.
                    if (entry.HasDeletions)
                    {
                        try
                        {
                            _ = entry.Deletions;
                        }
                        catch (CommitFormatException malformed)
                        {
                            Problems.Add($"'{entry.Key}': its deleted rows are not a vector of its own rows: {malformed.Message}");
                        }
                    }

                    if (entry.Uid != UInt128.Zero && VortexDataset.Identity(file) != entry.Uid)
                    {
                        Problems.Add($"'{entry.Key}' is not the object its entry names: another identity");
                    }

                    VortexIndexVerification indexes = await file.VerifyIndexesAsync(cancellationToken).ConfigureAwait(false);
                    foreach (string torn in indexes.Torn)
                    {
                        Problems.Add($"'{entry.Key}': the index region {torn} does not hold its checksum");
                    }

                    if (indexes.FileHashHolds == false)
                    {
                        Problems.Add($"'{entry.Key}': the bytes are not the ones a fragment was built over");
                    }

                    for (int i = 0; i < file.IndexFragmentRefusals.Count; i++)
                    {
                        if (file.IndexFragmentRefusals[i] is { } refusal)
                        {
                            Problems.Add(Invariant($"'{entry.Key}': fragment {i} is refused: {refusal}"));
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Checks every commit object this verify read from whole, including the checksum a reader
        /// only covers when its one open read happens to span the whole object.
        /// </summary>
        internal async ValueTask CheckCommitsAsync(ulong version)
        {
            _commits.Add(version);
            foreach (ulong commit in _commits)
            {
                string key = CommitKey.For(commit);
                if (await store.HeadAsync(key, cancellationToken).ConfigureAwait(false) is not { } head)
                {
                    Problems.Add($"'{key}' is not in the store");
                    continue;
                }

                byte[] bytes = new byte[head.Length];
                for (long at = 0; at < head.Length; at += HashChunk)
                {
                    using ObjectRange range = await store
                        .GetRangeAsync(key, at, (int)Math.Min(HashChunk, head.Length - at), cancellationToken).ConfigureAwait(false);
                    range.Bytes.CopyTo(bytes.AsSpan((int)at));
                }

                try
                {
                    CommitObject.Open(bytes, bytes.Length);
                }
                catch (CommitFormatException torn)
                {
                    Problems.Add($"'{key}': {torn.Message}");
                }
            }
        }

        private async ValueTask<UInt128> HashAsync(string key, long length)
        {
            XxHash128 hash = new XxHash128();
            for (long at = 0; at < length; at += HashChunk)
            {
                using ObjectRange range = await store
                    .GetRangeAsync(key, at, (int)Math.Min(HashChunk, length - at), cancellationToken).ConfigureAwait(false);
                foreach (ReadOnlyMemory<byte> segment in range.Bytes)
                {
                    hash.Append(segment.Span);
                }
            }

            return hash.GetCurrentHashAsUInt128();
        }

        private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
    }
}
