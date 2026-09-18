// Verify - docs/13-dataset.md §10: "hashes every object against the leaf entries, walks the page
// hashes, checks every fragment segment; offline; the only reader of the content hash. Between two
// versions it is incremental, by the diff of their trees (§13.J), whatever their lineage."
//
// EVERY PAGE FROM THE STORE, NONE FROM THE HEADER. A reader takes the pages a header inlines, which
// the header's own checksum covers, and never reads their stored copies: a stored root torn under
// its inlined twin is invisible to every reader, and would surface only the day a later commit
// stopped inlining it. Verify reads the stored copy of every page it checks, and checks the inlined
// copies against their references besides.
//
// THE DIFF IS BY REFERENCE, HEIGHT BY HEIGHT. A reference names its content, so a page two versions
// share is one subtree checked once, by the verify of the older one. Walking both trees from their
// roots, one height at a time, the pages of the newer version that the older one does not hold at
// that height are the ones read and checked, and only their children go on to the next height; the
// older version's pages are read only where they differ too, and only to learn which leaf entries it
// already vouched for. That is O(changed pages), whatever the lineage: two versions a rebase or a
// compaction separates share what they share and nothing is assumed about how they got there. An
// entry both versions hold byte for byte is not verified again; its object is immutable (§3).
//
// A PROBLEM IS REPORTED, NEVER THROWN. The point of an offline check is the whole list: a torn page
// stops the walk below it and nothing else, a missing object is one line among the others.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO.Hashing;
using System.Threading;
using System.Threading.Tasks;
using Vorticity;
using Vorticity.File;

namespace Vorticity.Dataset;

/// <summary>What to verify (§10).</summary>
public sealed record VerifyOptions
{
    /// <summary>The version to verify, or null for the latest.</summary>
    public ulong? Version { get; init; }

    /// <summary>
    /// A version already verified: what the two share is not checked again. Null, the default,
    /// verifies everything.
    /// </summary>
    public ulong? Since { get; init; }
}

/// <summary>What <see cref="DatasetVerifier.VerifyAsync"/> found.</summary>
/// <param name="Version">The version verified.</param>
/// <param name="Since">The version it was verified against, or 0 for a full verify.</param>
/// <param name="Pages">The pages read and checked.</param>
/// <param name="Objects">The data objects hashed and opened.</param>
/// <param name="Fragments">The index fragments read and checked.</param>
/// <param name="Commits">The commit objects read whole and checked against their own checksum.</param>
/// <param name="Unhashed">Objects whose entry records no content hash: imported ones (§7).</param>
/// <param name="Problems">Each thing that does not hold, named.</param>
public sealed record DatasetVerification(
    ulong Version,
    ulong Since,
    long Pages,
    long Objects,
    long Fragments,
    long Commits,
    long Unhashed,
    IReadOnlyList<string> Problems)
{
    /// <summary>Whether everything checked holds.</summary>
    public bool Holds => Problems.Count == 0;
}

/// <summary>Checks a version against everything its references and entries promise (§10).</summary>
public static class DatasetVerifier
{
    private const int HashChunk = 1 << 20;

    /// <summary>Verifies one version, whole or against one already verified.</summary>
    /// <param name="store">The dataset's store.</param>
    /// <param name="options">Which version, and since which; null for the latest, whole.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>What held, and every problem named.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="store"/> is null.</exception>
    /// <exception cref="ObjectNotFoundException">The store holds no dataset, or not that version.</exception>
    public static async ValueTask<DatasetVerification> VerifyAsync(
        IObjectStore store, VerifyOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        options ??= new VerifyOptions();
        ulong version = options.Version ?? (await DatasetCommitter.LatestAsync(store, cancellationToken).ConfigureAwait(false)).Version;
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
            return new DatasetVerification(
                version, options.Since ?? 0, 0, 0, 0, 1, 0, [$"'{CommitKey.For(version)}' does not open: {torn.Message}"]);
        }

        run.CheckInlined(target);
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
        return new DatasetVerification(
            version, options.Since ?? 0, run.Pages, run.Objects, run.Fragments, run.Commits, run.Unhashed, run.Problems);
    }

    /// <summary>One verify's state: the page source, what it has seen and what it found.</summary>
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

        /// <summary>The pages a header carries, against the references they are carried under.</summary>
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
        /// One level's tree against the same level of the older version, height by height: the pages
        /// the target holds and the older does not are checked, and only theirs are walked further.
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
                    // entries it vouched for, and one that no longer reads vouches for nothing.
                    ReadOnlyMemory<byte> bytes;
                    try
                    {
                        bytes = await _pages.ReadPageAsync(reference, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception unreadable) when (unreadable is CommitFormatException or ObjectNotFoundException)
                    {
                        continue;
                    }

                    Expand(bytes, height, nextTheirs, null, _vouched);
                }

                ours = nextOurs;
                theirs = nextTheirs;
            }
        }

        /// <summary>A page's children, or its leaf entries, into the sets the walk keeps.</summary>
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
                    entries!.Add(ObjectEntry.FromBytes(leaf.Value.Span));
                }
            }
        }

        /// <summary>Reads a page of the target from the store; a page that does not hold is a problem.</summary>
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
            catch (Exception unreadable) when (unreadable is CommitFormatException or ObjectNotFoundException or ArgumentOutOfRangeException)
            {
                Problems.Add(Invariant($"the page of version {reference.Version} at {reference.Offset}+{reference.Length}: {unreadable.Message}"));
                return null;
            }
        }

        /// <summary>Every entry the walk reached that the older version did not already vouch for.</summary>
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

        /// <summary>
        /// One object against its entry: its length, its content hash, what it says it is, and every
        /// index region it carries or its fragments carry.
        /// </summary>
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

        /// <summary>Opens the object with its fragments and checks what only an open can.</summary>
        private async ValueTask CheckFileAsync(ObjectEntry entry, List<ReadOnlyMemory<byte>> fragments)
        {
            VortexOpenOptions options = fragments.Count == 0
                ? new VortexOpenOptions()
                : new VortexOpenOptions { Read = new VortexReadOptions { IndexFragments = fragments } };
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
                    if (file.RowCount != entry.Rows)
                    {
                        Problems.Add(Invariant($"'{entry.Key}' holds {file.RowCount} rows and its entry says {entry.Rows}"));
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
        /// Every commit object this verify read from, whole: its trailer, its table and its checksum,
        /// which a reader checks only when its one open read happens to cover the whole object (§3).
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
                    range.Bytes.Span.CopyTo(bytes.AsSpan((int)at));
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

        /// <summary>The XXH3-128 of a whole object, a mebibyte at a time.</summary>
        private async ValueTask<UInt128> HashAsync(string key, long length)
        {
            XxHash128 hash = new XxHash128();
            for (long at = 0; at < length; at += HashChunk)
            {
                using ObjectRange range = await store
                    .GetRangeAsync(key, at, (int)Math.Min(HashChunk, length - at), cancellationToken).ConfigureAwait(false);
                hash.Append(range.Bytes.Span);
            }

            return hash.GetCurrentHashAsUInt128();
        }

        private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
    }
}
