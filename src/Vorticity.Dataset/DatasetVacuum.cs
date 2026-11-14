using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.IO;

namespace Vorticity.Dataset;

/// <summary>How a vacuum runs.</summary>
public sealed record VacuumOptions
{
    /// <summary>
    /// The clock the window counts against; the system's by default. It must agree with the
    /// store's, since the ages it compares are the store's timestamps: ahead of it by less than half
    /// the window, less what a writer's clock may fall behind and the store's listing may lag. A clock
    /// set ahead on purpose, to see what a later vacuum takes, is for a store no writer commits to
    /// meanwhile, since it takes the objects of a writer in flight.
    /// </summary>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    /// <summary>Marks and reports what would be deleted, and deletes nothing.</summary>
    public bool DryRun { get; init; }

    /// <summary>
    /// The share of a kept commit object that live pages and fragments must reach for it not to be
    /// reported <see cref="VacuumResult.Sparse"/>: 0.25 by default.
    /// </summary>
    internal double RepackBelow { get; init; } = 0.25;
}

/// <summary>What a vacuum found and did.</summary>
public sealed record VacuumResult
{
    /// <summary>The version it marked from, which it always keeps; 0 when the store holds no dataset.</summary>
    public ulong Latest { get; init; }

    /// <summary>The versions inside the window, newest first.</summary>
    public ImmutableArray<ulong> Retained { get; init; } = [];

    /// <summary>The window it applied: the dataset's retention, or seven days when it sets none.</summary>
    public TimeSpan Window { get; init; }

    /// <summary>The distinct tree pages the marking read.</summary>
    public long PagesRead { get; init; }

    /// <summary>The objects it deleted, or would have on a dry run: commit objects first.</summary>
    public ImmutableArray<string> Deleted { get; init; } = [];

    /// <summary>The unreferenced objects it kept because they are younger than the window: writers in flight.</summary>
    public ImmutableArray<string> Young { get; init; } = [];

    /// <summary>
    /// The objects past the window that the store still keeps under a retention lock or a legal hold,
    /// which vacuum leaves for a later run: a store under a lock refuses to delete them before.
    /// </summary>
    public ImmutableArray<string> Locked { get; init; } = [];

    /// <summary>When the first of <see cref="Locked"/> may go, by the store's clock: the earliest of their retention dates; null when none has one.</summary>
    public DateTimeOffset? NextUnlock { get; init; }

    /// <summary>
    /// The commit objects kept alive only by references into them, whose live pages and fragments are
    /// under <see cref="VacuumOptions.RepackBelow"/> of their body: what a repack would free at the
    /// next vacuum past the window.
    /// </summary>
    internal IReadOnlyList<ulong> Sparse { get; init; } = [];

    /// <summary>
    /// The most pages and regions of open reads the marking held at once: those of one version, however
    /// many versions it marked.
    /// </summary>
    internal int MostInHand { get; init; }
}

/// <summary>
/// Deletes what no version inside the retention window references, and never runs by itself. Ages
/// are the store's timestamps, not a header's, since writers' clocks need not agree; and nothing is
/// deleted until every retained version is marked, because a missed mark would delete a live
/// object. A reader that outlives the window loses its objects, and a writer whose objects began
/// half the window ago or more refuses to commit them, since it may have lost them. A store may date
/// an object written in parts by the start of its upload, as S3 does, so a writer measures from before
/// its first byte leaves. The vacuum's clock running ahead of the store's, a writer's clock falling
/// behind its own earlier reading, and a listing that lags, as a store may let it, must stay within
/// the other half window together, or a version may name an object vacuum took.
/// </summary>
internal static class DatasetVacuum
{
    /// <summary>The retention window applied when the dataset sets none.</summary>
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromDays(7);

    /// <summary>
    /// The exception a reader of <paramref name="version"/> throws for an object the store no longer
    /// holds, the store's own as its cause.
    /// </summary>
    public static ObjectNotFoundException Lost(string key, ulong version, ObjectNotFoundException cause) =>
        new ObjectNotFoundException(
            $"Version {version} of the dataset names '{key}', which the store no longer holds. A reader that " +
            "outlives the retention window loses its objects to vacuum: refresh to read the latest version.",
            cause)
        {
            Key = key,
        };

    /// <summary>Whether <paramref name="missing"/> is one <see cref="Lost"/> made, which names its version already.</summary>
    public static bool IsLost(ObjectNotFoundException missing) => missing.InnerException is ObjectNotFoundException;

    /// <summary>The window a header's retention sets: its seconds, or <see cref="DefaultWindow"/> when it sets none.</summary>
    public static TimeSpan WindowOf(RetentionSettings retention) =>
        retention.Seconds > 0 ? TimeSpan.FromSeconds(retention.Seconds) : DefaultWindow;

    /// <summary>The keys a vacuum deletes in one request at most while it sweeps what an earlier version left, which may be many.</summary>
    private const int DeleteBatch = 1_000;

    /// <summary>Marks from every version inside the window, then sweeps what is unmarked and old.</summary>
    /// <param name="store">The dataset's store.</param>
    /// <param name="options">The clock and whether to delete; null for the defaults.</param>
    /// <param name="location">Where the dataset lives, as its handle was told, which tags the vacuum's activity; null when unknown.</param>
    /// <param name="cancellationToken">Cancels the marking and the deletes.</param>
    /// <exception cref="CommitFormatException">A page it must mark from is not what its reference says.</exception>
    /// <exception cref="ObjectNotFoundException">A page it must mark from is missing; nothing was deleted.</exception>
    public static async ValueTask<VacuumResult> RunAsync(
        IObjectStore store, VacuumOptions? options = null, string? location = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        options ??= new VacuumOptions();
        Activity? activity = DatasetTelemetry.StartVacuum(location, options.DryRun);
        VacuumResult result;
        try
        {
            result = await SweepAsync(store, options, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception failure)
        {
            DatasetTelemetry.VacuumFailed(activity, failure);
            throw;
        }

        DatasetTelemetry.VacuumEnded(activity, result, options.DryRun);
        return result;
    }

    private static async ValueTask<VacuumResult> SweepAsync(IObjectStore store, VacuumOptions options, CancellationToken cancellationToken)
    {
        DateTimeOffset now = options.TimeProvider.GetUtcNow();

        (ulong latest, CommitObject? head) = await DatasetCommitter.LatestAsync(store, cancellationToken).ConfigureAwait(false);
        if (head is null)
        {
            return new VacuumResult { Window = DefaultWindow };
        }

        RetentionSettings retention = head.Header.Retention;
        TimeSpan window = WindowOf(retention);

        // A commit newer than the one marked from is not this vacuum's to judge. The listing says each
        // object's length, token and date, and on a store that reports them its lock, so no head is
        // asked but for a lock the listing does not say, of an object about to be deleted.
        List<(ulong Version, string Key, ObjectListing Listed)> commits = [];
        Dictionary<ulong, long> lengths = [];
        foreach (ObjectListing listed in await store.ListEntriesAsync(CommitKey.Prefix, cancellationToken).ConfigureAwait(false))
        {
            if (CommitKey.TryParse(listed.Key, out ulong version) && version <= latest)
            {
                commits.Add((version, listed.Key, listed));
                lengths[version] = listed.Head.Length;
            }
        }

        List<ulong> retained = [latest];
        for (int i = 1; i < commits.Count; i++)
        {
            // Superseded when the next commit that survives was created.
            DateTimeOffset superseded = commits[i - 1].Listed.Head.LastModified;
            if (i <= retention.Versions || now - superseded < window)
            {
                retained.Add(commits[i].Version);
            }
        }

        // Each distinct page is walked once, however many retained versions share it. The versions are marked from the
        // oldest retained to the latest, each read whole in one request, from the length its listing gave: every page a
        // version wrote lies in its own commit object, held while that version is marked, and a later version names only
        // pages an earlier one walked already and pages it wrote itself. So the pages read on their own are those of the
        // versions past the window that the oldest retained still names. Marked from the latest, a walk of its tree asked
        // for each page of every older version it named, one after the other, before their objects were opened: 1,032
        // requests over 802 versions, 22 seconds at 20 ms a request. The marking keeps no page it reads, and lets go of
        // what a version's read brought once that version is marked: it holds what the reads in flight hold.
        HashSet<ulong> markedCommits = [.. retained];
        HashSet<string> markedData = new HashSet<string>(StringComparer.Ordinal);
        HashSet<UInt128> markedUids = [];
        HashSet<PageReference> seen = [];
        HashSet<PageReference> fragmentsSeen = [];
        Dictionary<ulong, long> live = [];
        CommitPageSource pages = new CommitPageSource(store) { ReadsOnce = true };
        int mostInHand = 0;

        // The versions opened ahead side by side, as many as fit in what 32 opens of their first 256 KiB held before they
        // were read whole, and one at least: the commit object of a large job weighs megabytes.
        const long OpenedAheadBytes = 32L * CommitFormat.OpenBytes;
        List<ulong> order = [.. retained];
        order.Reverse();
        Task<CommitObject>?[] opening = new Task<CommitObject>?[order.Count];
        long inFlight = 0;
        int next = 0;
        try
        {
            for (int at = 0; at < order.Count; at++)
            {
                while (next < order.Count && (next == at || inFlight + WeightOf(order[next]) <= OpenedAheadBytes))
                {
                    ulong ahead = order[next];
                    opening[next] = ahead == latest && WeightOf(ahead) <= CommitFormat.OpenBytes
                        ? null
                        : CommitObject.OpenWholeAsync(store, CommitKey.For(ahead), WeightOf(ahead), cancellationToken).AsTask();
                    inFlight += WeightOf(ahead);
                    next++;
                }

                ulong version = order[at];
                CommitObject commit = opening[at] is { } open ? await open.ConfigureAwait(false) : head;
                opening[at] = null;
                inFlight -= WeightOf(version);
                pages.Open(version, commit);
                DatasetLevels trees = DatasetLevels.Of(commit.Header);
                foreach ((int _, DatasetTree tree) in trees.Occupied())
                {
                    await MarkAsync(tree.Root, tree.Depth).ConfigureAwait(false);
                }

                // The tree of rollups, whose leaves name the rollups' objects; the tree of partition indexes, theirs.
                if (!trees.Rollups.IsEmpty)
                {
                    await MarkAsync(trees.Rollups.Root, trees.Rollups.Depth, Leaves.Rollups).ConfigureAwait(false);
                }

                if (!trees.PartitionIndexes.IsEmpty)
                {
                    await MarkAsync(trees.PartitionIndexes.Root, trees.PartitionIndexes.Depth, Leaves.PartitionIndexes).ConfigureAwait(false);
                }

                mostInHand = Math.Max(mostInHand, pages.InHand);
                pages.Release();
            }
        }
        catch (Exception)
        {
            // The commits opened ahead and not yet marked, the batch's that failed, are waited for, their failures observed:
            // the vacuum fails with no request of its own still in flight.
            List<Task> pending = [];
            foreach (Task<CommitObject>? open in opening)
            {
                if (open is not null)
                {
                    pending.Add(open);
                }
            }

            await Task.WhenAll(pending).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            throw;
        }

        List<string> deleted = [];
        List<string> young = [];
        List<string> locked = [];
        DateTimeOffset? unlock = null;
        List<string> batch = [];
        bool lockedStore = head.Header.LockedStore;
        for (int i = commits.Count - 1; i >= 0; i--)
        {
            (ulong version, string key, ObjectListing listed) = commits[i];
            if (!markedCommits.Contains(version) && await HeadToSweepAsync(listed).ConfigureAwait(false) is { } found)
            {
                Sweep(key, found);
            }
        }

        await DeleteAsync().ConfigureAwait(false);
        foreach (string prefix in (string[])[CommitKey.DataPrefix, CommitKey.RollupPrefix])
        {
            foreach (ObjectListing listed in await store.ListEntriesAsync(prefix, cancellationToken).ConfigureAwait(false))
            {
                if (!markedData.Contains(listed.Key) && await HeadToSweepAsync(listed).ConfigureAwait(false) is { } found)
                {
                    Sweep(listed.Key, found);
                }
            }
        }

        await DeleteAsync().ConfigureAwait(false);

        // The partials of question families an earlier version learned and kept under learned/, which nothing reads now,
        // streamed, for there may be one an object and a family: each deleted whatever its age, unless under the store's lock.
        await foreach (ObjectListing listed in store.ListAsync(CommitKey.LearnedPrefix, null, cancellationToken).ConfigureAwait(false))
        {
            if (await HeadToSweepAsync(listed, evicting: true).ConfigureAwait(false) is { } found)
            {
                Evict(listed.Key, found);
                await DeleteFullAsync().ConfigureAwait(false);
            }
        }

        await DeleteAsync().ConfigureAwait(false);

        // The ratio is over the body, past the header: a header inlines copies of the pages it has
        // in hand, so taken over the whole object every superseded commit would look half dead
        // and each repack would make the next one due.
        // Where each body starts, as the walk learned it for the versions it read pages of, and read side by side, 32 at a
        // time, for the others: read in turn, one a version, they took a round trip each after the walk.
        List<(ulong Version, long Bytes, long Length)> judged = [];
        foreach ((ulong version, long bytes) in live)
        {
            if (!retained.Contains(version) && lengths.TryGetValue(version, out long length))
            {
                judged.Add((version, bytes, length));
            }
        }

        const int StartsAtOnce = 32;
        List<ulong> sparse = [];
        Task<long>?[] reading = new Task<long>?[Math.Min(judged.Count, StartsAtOnce)];
        for (int from = 0; from < judged.Count; from += reading.Length)
        {
            int count = Math.Min(reading.Length, judged.Count - from);
            for (int i = 0; i < count; i++)
            {
                ulong version = judged[from + i].Version;
                reading[i] = pages.TryGetStart(version, out _)
                    ? null
                    : CommitObject.PagesStartAsync(store, CommitKey.For(version), cancellationToken).AsTask();
            }

            try
            {
                for (int i = 0; i < count; i++)
                {
                    (ulong version, long bytes, long length) = judged[from + i];
                    pages.TryGetStart(version, out long start);
#pragma warning disable VSTHRD003 // The read was started just above, for this version.
                    start = reading[i] is { } read ? await read.ConfigureAwait(false) : start;
#pragma warning restore VSTHRD003
                    reading[i] = null;
                    long body = length - start;
                    if (body > 0 && (double)bytes / body < options.RepackBelow)
                    {
                        sparse.Add(version);
                    }
                }
            }
            catch (Exception)
            {
                List<Task> pending = [];
                foreach (Task<long>? read in reading)
                {
                    if (read is not null)
                    {
                        pending.Add(read);
                    }
                }

                await Task.WhenAll(pending).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                throw;
            }
        }

        sparse.Sort();
        return new VacuumResult
        {
            Latest = latest,
            Retained = [.. retained],
            Window = window,
            PagesRead = seen.Count,
            MostInHand = mostInHand,
            Deleted = [.. deleted],
            Young = [.. young],
            Locked = [.. locked],
            NextUnlock = unlock,
            Sparse = sparse,
        };

        // A page's read, started by the walk of its parent, side by side with its siblings', or started here.
        async ValueTask MarkAsync(PageReference reference, int depth, Leaves leaves = Leaves.Objects, Task<ReadOnlyMemory<byte>>? read = null)
        {
            if (!reference.Exists || !seen.Add(reference))
            {
                return;
            }

            markedCommits.Add(reference.Version);
            live[reference.Version] = live.GetValueOrDefault(reference.Version) + reference.Length;
#pragma warning disable VSTHRD003 // The read was started by this walk, at the page's parent.
            ReadOnlyMemory<byte> page = read is not null
                ? await read.ConfigureAwait(false)
                : await pages.ReadPageAsync(reference, cancellationToken).ConfigureAwait(false);
#pragma warning restore VSTHRD003
            if (depth == 1 && leaves == Leaves.Rollups)
            {
                foreach (TreeEntry leaf in TreePage.ReadLeaf(page))
                {
                    markedData.Add(RollupEntry.FromBytes(leaf.Key.Span, leaf.Value.Span).Key);
                }

                return;
            }

            if (depth == 1 && leaves == Leaves.PartitionIndexes)
            {
                foreach (TreeEntry leaf in TreePage.ReadLeaf(page))
                {
                    markedData.Add(PartitionIndexEntry.FromBytes(leaf.Key.Span, leaf.Value.Span).ObjectKey);
                }

                return;
            }

            if (depth == 1)
            {
                foreach (TreeEntry leaf in TreePage.ReadLeaf(page))
                {
                    ObjectEntry entry = ObjectEntry.FromBytes(leaf.Value);
                    markedData.Add(entry.Key);
                    markedUids.Add(entry.Uid);
                    foreach (PageReference fragment in entry.Fragments)
                    {
                        markedCommits.Add(fragment.Version);
                        if (fragmentsSeen.Add(fragment))
                        {
                            live[fragment.Version] = live.GetValueOrDefault(fragment.Version) + fragment.Length;
                        }
                    }

                    // A vector out of line keeps the commit object it lies in, as a fragment does.
                    if (entry.VectorAt is { Exists: true } vector)
                    {
                        markedCommits.Add(vector.Version);
                        if (fragmentsSeen.Add(vector))
                        {
                            live[vector.Version] = live.GetValueOrDefault(vector.Version) + vector.Length;
                        }
                    }

                    // So do the counts of its deleted rows.
                    if (entry.NegativesAt is { Exists: true } negatives)
                    {
                        markedCommits.Add(negatives.Version);
                        if (fragmentsSeen.Add(negatives))
                        {
                            live[negatives.Version] = live.GetValueOrDefault(negatives.Version) + negatives.Length;
                        }
                    }
                }

                return;
            }

            // The children walked in turn, their pages read side by side a window at a time: with the versions marked from the
            // oldest, the pages read here are those of versions past the window, and the region of a version this vacuum read
            // whole is in hand. Where a version's pages start, which a page's read needs, is learned first for the window's
            // versions this source does not know, side by side and once a version: read with each page, every sibling of that
            // version would ask for it again, and read in turn, each would wait for the one before.
            IReadOnlyList<InternalEntry> children = TreePage.ReadInternal(page);
            Task<ReadOnlyMemory<byte>>?[] window = new Task<ReadOnlyMemory<byte>>?[Math.Min(children.Count, DatasetTree.PrefetchWindow)];
            for (int from = 0; from < children.Count; from += window.Length)
            {
                int count = Math.Min(window.Length, children.Count - from);
                await LearnStartsAsync(children, from, count).ConfigureAwait(false);
                for (int i = 0; i < count; i++)
                {
                    PageReference child = children[from + i].Child;
                    window[i] = Unread(child) ? pages.ReadPageAsync(child, cancellationToken).AsTask() : null;
                }

                try
                {
                    for (int i = 0; i < count; i++)
                    {
                        Task<ReadOnlyMemory<byte>>? childRead = window[i];
                        window[i] = null;
                        await MarkAsync(children[from + i].Child, depth - 1, leaves, childRead).ConfigureAwait(false);
                    }
                }
                catch (Exception)
                {
                    // The window's reads still in flight are waited for, their failures observed.
                    List<Task> pending = [];
                    foreach (Task<ReadOnlyMemory<byte>>? inFlightRead in window)
                    {
                        if (inFlightRead is not null)
                        {
                            pending.Add(inFlightRead);
                        }
                    }

                    await Task.WhenAll(pending).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                    throw;
                }
            }
        }

        // What a version's read holds while it is in flight or marked: its whole object, as the listing measured it.
        long WeightOf(ulong version) => lengths.TryGetValue(version, out long length) ? length : CommitFormat.OpenBytes;

        // Whether the walk has a page still to read from the store: one it has not walked, that no read in hand holds.
        bool Unread(PageReference child) => child.Exists && !seen.Contains(child) && !pages.TryGetInHand(child, out _);

        // Where the pages of the versions of children[from..from + count] lie, for those the walk must read and whose start
        // the source has not learned: read side by side, once a version.
        async ValueTask LearnStartsAsync(IReadOnlyList<InternalEntry> children, int from, int count)
        {
            List<ulong>? unknown = null;
            for (int i = from; i < from + count; i++)
            {
                PageReference child = children[i].Child;
                if (Unread(child) && !pages.TryGetStart(child.Version, out _) && (unknown is null || !unknown.Contains(child.Version)))
                {
                    (unknown ??= []).Add(child.Version);
                }
            }

            if (unknown is null)
            {
                return;
            }

            Task<long>[] starts = new Task<long>[unknown.Count];
            for (int i = 0; i < unknown.Count; i++)
            {
                starts[i] = CommitObject.PagesStartAsync(store, CommitKey.For(unknown[i]), cancellationToken).AsTask();
            }

            long[] learned = await Task.WhenAll(starts).ConfigureAwait(false);
            for (int i = 0; i < unknown.Count; i++)
            {
                pages.Know(unknown[i], learned[i]);
            }
        }

        // The head an unmarked object is judged by: the listing's, unless the store keeps locks the
        // listing does not report and the object is old enough to go, or is evicted whatever its age,
        // when its head says the lock. Null when the object went meanwhile.
        async ValueTask<ObjectHead?> HeadToSweepAsync(ObjectListing listed, bool evicting = false)
        {
            if (listed.LockReported || !lockedStore || (!evicting && now - listed.Head.LastModified < window))
            {
                return listed.Head;
            }

            return await store.HeadAsync(listed.Key, cancellationToken).ConfigureAwait(false);
        }

        void Sweep(string key, ObjectHead found)
        {
            if (now - found.LastModified < window)
            {
                young.Add(key);
                return;
            }

            Evict(key, found);
        }

        // Deleted whatever its age, unless still under the store's lock: the next vacuum after the date takes it.
        void Evict(string key, ObjectHead found)
        {
            if (found.IsLockedAt(now))
            {
                locked.Add(key);
                if (!found.LegalHold && found.RetainUntil is { } until && (unlock is not { } first || until < first))
                {
                    unlock = until;
                }

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

        // The batch deleted once it holds a request's worth, while the partials an earlier version learned are swept.
        async ValueTask DeleteFullAsync()
        {
            if (batch.Count >= DeleteBatch)
            {
                await DeleteAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>What the leaves of a tree the vacuum marks name: data objects, rollups, or the indexes of partitions.</summary>
    private enum Leaves
    {
        Objects,
        Rollups,
        PartitionIndexes,
    }
}
