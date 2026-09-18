// Vacuum - docs/13-dataset.md §10: "walks the trees of every commit inside the retention window and
// marks the data objects and the commit objects their page and fragment references point into, then
// deletes the unmarked data objects whose store timestamp is older than the window, and the unmarked
// commit objects older than the window, keeping the latest".
//
// WHICH VERSIONS ARE INSIDE THE WINDOW. A version can be read for as long as a reader that opened it
// while it was the latest is still reading, so what ages is the moment it was SUPERSEDED: the store
// timestamp of the next commit object. A version is kept while that moment is younger than the
// window, and the `Versions` setting keeps that many more whatever their age. The latest is always
// kept. Every timestamp is the store's (`ObjectHead.LastModified`), never a header's: the writers'
// clocks need not agree, and the window is a promise about the store.
//
// MARKING IS O(DISTINCT PAGES), NOT O(VERSIONS × PAGES). A page reference names its content, so two
// versions that share a subtree share its root reference, and a walk that has seen a reference has
// seen everything under it. Consecutive versions differ by O(depth) pages (§3), so the kept versions
// cost barely more than one tree.
//
// NOTHING IS DELETED UNTIL EVERYTHING IS MARKED. A page that cannot be read fails the vacuum before
// its first delete: a missing mark would delete a live object, and that is the one failure this
// operation must not have. What it sweeps is the dataset's own: `commit/` and `data/`. An object an
// import took without copying lives wherever its owner put it, and is its owner's to delete.
//
// WHAT IT DOES NOT PROTECT. A commit newer than the one it marked from is left alone, and so is
// everything younger than the window: a writer in flight. A writer that takes longer than the window
// to commit breaks the rule §10 states -- "a writer must commit within the window" -- and loses its
// objects. A reader that outlives the window loses its version's objects, and says so with
// `ObjectNotFoundException.Version` rather than answering from what is left.
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Vorticity.Dataset;

/// <summary>How a vacuum runs (§10).</summary>
public sealed record VacuumOptions
{
    /// <summary>
    /// The clock the window is measured against; the system's by default. It must be the store's:
    /// the ages it compares are the store's timestamps.
    /// </summary>
    public TimeProvider Clock { get; init; } = TimeProvider.System;

    /// <summary>Marks and reports what would be deleted, and deletes nothing.</summary>
    public bool DryRun { get; init; }

    /// <summary>
    /// The share of a kept commit object that live pages and fragments must reach for it not to be
    /// reported <see cref="VacuumResult.Sparse"/>: 0.25 by default, so an object three quarters dead
    /// is worth a repack (§10's "a threshold on the dead ratio").
    /// </summary>
    public double RepackBelow { get; init; } = 0.25;
}

/// <summary>What a vacuum found and did (§10).</summary>
/// <param name="Latest">The version it marked from, which it always keeps.</param>
/// <param name="Retained">The versions inside the window, newest first.</param>
/// <param name="Window">The window it applied: the dataset's retention, or seven days when unset.</param>
/// <param name="PagesRead">The distinct pages the marking read.</param>
/// <param name="Deleted">The objects it deleted, or would have, on a dry run: commits first.</param>
/// <param name="Young">The unmarked objects it kept because they are younger than the window.</param>
/// <param name="Sparse">
/// The commit objects kept alive only by references into them, whose live pages and fragments are
/// under <see cref="VacuumOptions.RepackBelow"/> of their bytes: what a repack
/// (<see cref="VortexDataset.RepackAsync"/>) would free at the next vacuum past the window.
/// </param>
public sealed record VacuumResult(
    ulong Latest,
    IReadOnlyList<ulong> Retained,
    TimeSpan Window,
    long PagesRead,
    IReadOnlyList<string> Deleted,
    IReadOnlyList<string> Young,
    IReadOnlyList<ulong> Sparse);

/// <summary>Deletes what no version inside the retention window references (§10). Never automatic.</summary>
public static class DatasetVacuum
{
    /// <summary>§10: "7 days by default".</summary>
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromDays(7);

    private const int ListPage = 1_000;

    /// <summary>Marks from every version inside the window, then sweeps what is unmarked and old.</summary>
    /// <param name="store">The dataset's store.</param>
    /// <param name="options">The clock and the dry run, or null for the defaults.</param>
    /// <param name="cancellationToken">Cancels the reads and the deletes.</param>
    /// <returns>What it kept and what it deleted.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="store"/> is null.</exception>
    /// <exception cref="CommitFormatException">A page it must mark from is not what its reference says.</exception>
    /// <exception cref="ObjectNotFoundException">A page it must mark from is missing; nothing was deleted.</exception>
    public static async ValueTask<VacuumResult> RunAsync(
        IObjectStore store, VacuumOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        options ??= new VacuumOptions();
        DateTimeOffset now = options.Clock.GetUtcNow();

        (ulong latest, CommitObject? head) = await DatasetCommitter.LatestAsync(store, cancellationToken).ConfigureAwait(false);
        if (head is null)
        {
            return new VacuumResult(0, [], DefaultWindow, 0, [], [], []);
        }

        RetentionSettings retention = head.Header.Retention;
        TimeSpan window = retention.Seconds > 0 ? TimeSpan.FromSeconds(retention.Seconds) : DefaultWindow;

        // The commits, newest first, from the one marked from down. A newer one landed meanwhile and
        // is not this vacuum's to judge.
        List<(ulong Version, string Key, DateTimeOffset Created)> commits = [];
        Dictionary<ulong, long> lengths = [];
        foreach (string key in await ListAllAsync(store, CommitKey.Prefix, cancellationToken).ConfigureAwait(false))
        {
            if (CommitKey.TryParse(key, out ulong version) && version <= latest
                && await store.HeadAsync(key, cancellationToken).ConfigureAwait(false) is { } found)
            {
                commits.Add((version, key, found.LastModified));
                lengths[version] = found.Length;
            }
        }

        List<ulong> retained = [latest];
        for (int i = 1; i < commits.Count; i++)
        {
            // Superseded when the next commit that survives was created.
            DateTimeOffset superseded = commits[i - 1].Created;
            if (i <= retention.Versions || now - superseded < window)
            {
                retained.Add(commits[i].Version);
            }
        }

        // Mark: every page of every level of every retained version, each distinct page once.
        HashSet<ulong> markedCommits = [.. retained];
        HashSet<string> markedData = new HashSet<string>(StringComparer.Ordinal);
        HashSet<PageReference> seen = [];
        HashSet<PageReference> fragmentsSeen = [];
        Dictionary<ulong, long> live = [];
        CommitPageSource pages = new CommitPageSource(store);
        foreach (ulong version in retained)
        {
            CommitObject commit = version == latest
                ? head
                : await CommitObject.OpenAsync(store, CommitKey.For(version), cancellationToken).ConfigureAwait(false);
            pages.Inline(commit.Header);
            pages.Know(version, commit.HeaderEnd);
            foreach ((int _, DatasetTree tree) in DatasetLevels.Of(commit.Header).Occupied())
            {
                await MarkAsync(tree.Root, tree.Depth).ConfigureAwait(false);
            }
        }

        // Sweep: commits oldest first, then data objects, each unmarked one only once it is old.
        List<string> deleted = [];
        List<string> young = [];
        for (int i = commits.Count - 1; i >= 0; i--)
        {
            (ulong version, string key, DateTimeOffset created) = commits[i];
            if (!markedCommits.Contains(version))
            {
                await SweepAsync(key, created).ConfigureAwait(false);
            }
        }

        foreach (string key in await ListAllAsync(store, CommitKey.DataPrefix, cancellationToken).ConfigureAwait(false))
        {
            if (!markedData.Contains(key)
                && await store.HeadAsync(key, cancellationToken).ConfigureAwait(false) is { } found)
            {
                await SweepAsync(key, found.LastModified).ConfigureAwait(false);
            }
        }

        // Sparse: kept by references alone -- a retained version's own header keeps its object
        // whatever a repack moves out of it -- and mostly dead. The ratio is over the BODY, past the
        // header: a header inlines copies of the pages it has in hand, so over the whole object every
        // superseded commit would look half dead, a repack's own commit included, and each repack
        // would make the next one due.
        List<ulong> sparse = [];
        foreach ((ulong version, long bytes) in live)
        {
            if (retained.Contains(version) || !lengths.TryGetValue(version, out long length))
            {
                continue;
            }

            long body = length - await CommitObject.PagesStartAsync(store, CommitKey.For(version), cancellationToken).ConfigureAwait(false);
            if (body > 0 && (double)bytes / body < options.RepackBelow)
            {
                sparse.Add(version);
            }
        }

        sparse.Sort();
        return new VacuumResult(latest, retained, window, seen.Count, deleted, young, sparse);

        async ValueTask MarkAsync(PageReference reference, int depth)
        {
            if (!reference.Exists || !seen.Add(reference))
            {
                return;
            }

            markedCommits.Add(reference.Version);
            live[reference.Version] = live.GetValueOrDefault(reference.Version) + reference.Length;
            ReadOnlyMemory<byte> page = await pages.ReadPageAsync(reference, cancellationToken).ConfigureAwait(false);
            if (depth == 1)
            {
                foreach (TreeEntry leaf in TreePage.ReadLeaf(page))
                {
                    ObjectEntry entry = ObjectEntry.FromBytes(leaf.Value.Span);
                    markedData.Add(entry.Key);
                    foreach (PageReference fragment in entry.Fragments)
                    {
                        markedCommits.Add(fragment.Version);
                        if (fragmentsSeen.Add(fragment))
                        {
                            live[fragment.Version] = live.GetValueOrDefault(fragment.Version) + fragment.Length;
                        }
                    }
                }

                return;
            }

            foreach (InternalEntry child in TreePage.ReadInternal(page))
            {
                await MarkAsync(child.Child, depth - 1).ConfigureAwait(false);
            }
        }

        async ValueTask SweepAsync(string key, DateTimeOffset created)
        {
            if (now - created < window)
            {
                young.Add(key);
                return;
            }

            if (!options.DryRun)
            {
                await store.DeleteAsync(key, cancellationToken).ConfigureAwait(false);
            }

            deleted.Add(key);
        }
    }

    /// <summary>Every key under a prefix, in the store's order, a page of listing at a time.</summary>
    private static async ValueTask<List<string>> ListAllAsync(
        IObjectStore store, string prefix, CancellationToken cancellationToken)
    {
        List<string> keys = [];
        string? after = null;
        while (true)
        {
            IReadOnlyList<string> page = await store.ListAsync(prefix, after, ListPage, cancellationToken).ConfigureAwait(false);
            if (page.Count == 0)
            {
                return keys;
            }

            keys.AddRange(page);
            after = page[^1];
        }
    }
}
