using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Vorticity.Dataset;

/// <summary>How a vacuum runs.</summary>
internal sealed record VacuumOptions
{
    /// <summary>
    /// The clock the window counts against; the system's by default. It must agree with the
    /// store's, since the ages it compares are the store's timestamps.
    /// </summary>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    /// <summary>Marks and reports what would be deleted, and deletes nothing.</summary>
    public bool DryRun { get; init; }

    /// <summary>
    /// The share of a kept commit object that live pages and fragments must reach for it not to be
    /// reported <see cref="VacuumResult.Sparse"/>: 0.25 by default.
    /// </summary>
    public double RepackBelow { get; init; } = 0.25;
}

/// <summary>What a vacuum found and did.</summary>
/// <param name="Latest">The version it marked from, which it always keeps.</param>
/// <param name="Retained">The versions inside the window, newest first.</param>
/// <param name="Window">The window it applied: the dataset's retention, or seven days when unset.</param>
/// <param name="PagesRead">The distinct pages the marking read.</param>
/// <param name="Deleted">The objects it deleted, or would have, on a dry run: commits first.</param>
/// <param name="Young">The unmarked objects it kept because they are younger than the window.</param>
/// <param name="Sparse">
/// The commit objects kept alive only by references into them, whose live pages and fragments are
/// under <see cref="VacuumOptions.RepackBelow"/> of their bytes: what
/// <see cref="VortexDataset.RepackAsync"/> would free at the next vacuum past the window.
/// </param>
internal sealed record VacuumResult(
    ulong Latest,
    IReadOnlyList<ulong> Retained,
    TimeSpan Window,
    long PagesRead,
    IReadOnlyList<string> Deleted,
    IReadOnlyList<string> Young,
    IReadOnlyList<ulong> Sparse);

/// <summary>
/// Deletes what no version inside the retention window references, and never runs by itself. Ages
/// are the store's timestamps, not a header's, since writers' clocks need not agree; and nothing is
/// deleted until every retained version is marked, because a missed mark would delete a live
/// object. A writer or reader that outlives the window loses its objects.
/// </summary>
internal static class DatasetVacuum
{
    /// <summary>The retention window applied when the dataset sets none.</summary>
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromDays(7);

    /// <summary>Marks from every version inside the window, then sweeps what is unmarked and old.</summary>
    /// <exception cref="CommitFormatException">A page it must mark from is not what its reference says.</exception>
    /// <exception cref="ObjectNotFoundException">A page it must mark from is missing; nothing was deleted.</exception>
    public static async ValueTask<VacuumResult> RunAsync(
        IObjectStore store, VacuumOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        options ??= new VacuumOptions();
        DateTimeOffset now = options.TimeProvider.GetUtcNow();

        (ulong latest, CommitObject? head) = await DatasetCommitter.LatestAsync(store, cancellationToken).ConfigureAwait(false);
        if (head is null)
        {
            return new VacuumResult(0, [], DefaultWindow, 0, [], [], []);
        }

        RetentionSettings retention = head.Header.Retention;
        TimeSpan window = retention.Seconds > 0 ? TimeSpan.FromSeconds(retention.Seconds) : DefaultWindow;

        // A commit newer than the one marked from is not this vacuum's to judge.
        List<(ulong Version, string Key, DateTimeOffset Created)> commits = [];
        Dictionary<ulong, long> lengths = [];
        foreach (string key in await store.ListAllAsync(CommitKey.Prefix, cancellationToken).ConfigureAwait(false))
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

        // Each distinct page is walked once, however many retained versions share it.
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

        List<string> deleted = [];
        List<string> young = [];
        List<string> batch = [];
        for (int i = commits.Count - 1; i >= 0; i--)
        {
            (ulong version, string key, DateTimeOffset created) = commits[i];
            if (!markedCommits.Contains(version))
            {
                Sweep(key, created);
            }
        }

        await DeleteAsync().ConfigureAwait(false);
        foreach (string key in await store.ListAllAsync(CommitKey.DataPrefix, cancellationToken).ConfigureAwait(false))
        {
            if (!markedData.Contains(key)
                && await store.HeadAsync(key, cancellationToken).ConfigureAwait(false) is { } found)
            {
                Sweep(key, found.LastModified);
            }
        }

        await DeleteAsync().ConfigureAwait(false);

        // The ratio is over the body, past the header: a header inlines copies of the pages it has
        // in hand, so taken over the whole object every superseded commit would look half dead
        // and each repack would make the next one due.
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

        void Sweep(string key, DateTimeOffset created)
        {
            if (now - created < window)
            {
                young.Add(key);
                return;
            }

            batch.Add(key);
            deleted.Add(key);
        }

        // Commit objects in one batch, then data objects in another, so the commits go first as
        // the marking requires, and each kind costs the store a request per batch rather than per key.
        async ValueTask DeleteAsync()
        {
            if (batch.Count > 0 && !options.DryRun)
            {
                await store.DeleteAsync(batch, cancellationToken).ConfigureAwait(false);
            }

            batch = [];
        }
    }
}
