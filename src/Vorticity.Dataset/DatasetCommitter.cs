using System;
using System.Buffers;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Vorticity.Dataset;

/// <summary>What a commit needs to know beyond its operations.</summary>
internal sealed record CommitOptions
{
    /// <summary>The dataset's chunking seed, fixed at creation and carried by every header.</summary>
    public required ulong Seed { get; init; }

    /// <summary>The header a first commit starts from: schema, clustering key, settings. Ignored
    /// once the dataset exists, the winner's header carrying on instead.</summary>
    public CommitHeader? Template { get; init; }

    /// <summary>How many times to rebase before giving up.</summary>
    public int MaxAttempts { get; init; } = 8;

    /// <summary>
    /// A version the writer has seen, from which the latest is found by asking for the ones after
    /// it; 0 lists the commits instead.
    /// </summary>
    public ulong Known { get; init; }

    /// <summary>The boundary rule, or null for the prolly rule at this dataset's seed.</summary>
    public IBoundaryRule? Rule { get; init; }

    /// <summary>What a node's summary is, or null for the one the object entries carry.</summary>
    public ISummaryFold? Fold { get; init; }

    /// <summary>The clock a header's creation time is read from.</summary>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    /// <summary>A rule in its starting state.</summary>
    public IBoundaryRule NewRule() => Rule?.Fresh() ?? new ProllyBoundaryRule(Seed);

    /// <summary>The fold this commit folds its pages' summaries with.</summary>
    public ISummaryFold NewFold() => Fold ?? ObjectSummaryFold.Instance;
}

/// <summary>What a commit did.</summary>
/// <param name="Version">The version created, or the latest one when no operation applied and nothing was created.</param>
/// <param name="Key">Its commit object's key.</param>
/// <param name="Levels">The trees that version names, one per level.</param>
/// <param name="Outcomes">What each operation decided, in the caller's order.</param>
/// <param name="Attempts">How many times the writer had to rebase, 1 when it won first time.</param>
/// <param name="Pages">A source that can read the new version's pages, new and old.</param>
internal sealed record CommitResult(
    ulong Version,
    string Key,
    DatasetLevels Levels,
    IReadOnlyList<OperationOutcome> Outcomes,
    int Attempts,
    IPageSource Pages)
{
    /// <summary>Level 0's tree, where appends land.</summary>
    public DatasetTree Tree => Levels[0];
}

/// <summary>
/// Creates versions of a dataset, one conditional creation at a time: put-if-absent linearises
/// commits by itself, so there is no lease, no lock and no external service. A writer that loses
/// re-reads the winner and re-applies its operations rather than merging two trees.
/// </summary>
internal static class DatasetCommitter
{
    /// <summary>Applies the operations to the dataset's latest version, rebasing until it wins.</summary>
    /// <exception cref="ObjectStoreException">The writer lost <see cref="CommitOptions.MaxAttempts"/> times.</exception>
    public static async ValueTask<CommitResult> CommitAsync(
        IObjectStore store,
        IReadOnlyList<DatasetOperation> operations,
        CommitOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(operations);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaxAttempts);

        ulong known = options.Known;
        for (int attempt = 1; attempt <= options.MaxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // 1. The latest version: asked for from the one the writer last saw, which a lost
            // attempt moves up to the one it built on.
            (ulong parent, CommitObject? commit) = await LatestAsync(store, known, cancellationToken).ConfigureAwait(false);
            known = parent;
            CommitPageSource pages = new CommitPageSource(store);
            DatasetLevels levels = DatasetLevels.Empty;
            CommitHeader template = options.Template ?? new CommitHeader { Version = 1 };
            if (commit is not null)
            {
                template = commit.Header;
                pages.Inline(commit.Header);
                pages.Know(parent, commit.HeaderEnd);
                levels = DatasetLevels.Of(commit.Header);
            }

            // 2. The operations, re-applied to whatever is there now, a batch per level. The commit
            // object exists before they are applied, because an indexer's fragment is written into
            // it and its entry names it there; a lost attempt throws both away.
            ulong version = parent + 1;
            CommitObjectBuilder builder = new CommitObjectBuilder(version);
            (Dictionary<int, List<TreeChange>> changes, List<OperationOutcome> outcomes, Relocation repack) =
                await ApplyAsync(levels, operations, pages, builder, cancellationToken).ConfigureAwait(false);

            pages.Writing(builder, version);
            DatasetLevels next = levels;
            foreach ((int level, List<TreeChange> batch) in changes)
            {
                if (batch.Count > 0)
                {
                    next = next.With(
                        level,
                        await next[level]
                            .CommitAsync(batch, options.NewRule(), options.NewFold(), pages, builder, cancellationToken)
                            .ConfigureAwait(false));
                }
            }

            // A repack moves the pages last, over the trees the changes produced: a leaf the batch
            // rewrote is already in this commit, and what is left in the named objects moves now.
            if (repack.Versions.Count > 0)
            {
                foreach ((int level, DatasetTree tree) in next.Occupied())
                {
                    (DatasetTree relocated, int moved) = await tree
                        .RelocateAsync(reference => repack.Versions.Contains(reference.Version), pages, builder, cancellationToken)
                        .ConfigureAwait(false);
                    next = next.With(level, relocated);
                    repack.Moved += moved;
                }

                foreach (int at in repack.At)
                {
                    outcomes[at] = repack.Moved > 0 ? OperationOutcome.Applied : OperationOutcome.AlreadyThere;
                }
            }

            // A batch in which nothing applied would publish a copy of its parent under a new
            // number; it publishes nothing, and names the version it was decided against.
            if (operations.Count > 0 && commit is not null && !outcomes.Contains(OperationOutcome.Applied))
            {
                return new CommitResult(parent, CommitKey.For(parent), levels, outcomes, attempt, pages);
            }

            CommitHeader header = template with
            {
                Version = version,
                Parent = parent,
                Seed = options.Seed,
                CreatedAtUnixMilliseconds = options.TimeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
                Levels = LevelsOf(next, builder, pages),
            };

            // 3. One conditional creation.
            string key = CommitKey.For(version);
            byte[] bytes = builder.Build(header);
            if (await store.PutIfAbsentAsync(key, bytes, cancellationToken).ConfigureAwait(false)
                == PutOutcome.Created)
            {
                pages.Placed(bytes);
                return new CommitResult(version, key, next, outcomes, attempt, pages);
            }
        }

        throw new ObjectStoreException(
            $"The commit lost {options.MaxAttempts} times; a coordinator that batches commits is the " +
            "answer to contention, not more attempts.");
    }

    /// <summary>
    /// The latest version and its commit object; (0, null) when the dataset has none. From
    /// <paramref name="known"/>, a version the caller has seen, the versions after it are asked
    /// for; from 0, or when the answer is gone by the time it is read, the commits are listed.
    /// </summary>
    public static async ValueTask<(ulong Version, CommitObject? Commit)> LatestAsync(
        IObjectStore store, ulong known, CancellationToken cancellationToken)
    {
        if (known > 0)
        {
            ulong newest = await NewestFromAsync(store, known, cancellationToken).ConfigureAwait(false);
            try
            {
                return (newest, await CommitObject.OpenAsync(store, CommitKey.For(newest), cancellationToken).ConfigureAwait(false));
            }
            catch (ObjectNotFoundException)
            {
                // Vacuumed since it was seen: the listing finds what is there now.
            }
            catch (CommitFormatException torn)
            {
                throw TornCommitException.Of(newest, torn);
            }
        }

        return await LatestAsync(store, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The newest version from <paramref name="known"/> on: the versions after it asked for in
    /// doubling steps, then bisected. The commits a dataset keeps run without a gap above any that
    /// is still there, since vacuum takes the oldest first, so a version that is missing ends the
    /// run; a single request answers when nothing is newer.
    /// </summary>
    private static async ValueTask<ulong> NewestFromAsync(IObjectStore store, ulong known, CancellationToken cancellationToken)
    {
        ulong present = known;
        ulong step = 1;
        ulong absent;
        while (true)
        {
            ulong probe = present + step;
            if (await store.HeadAsync(CommitKey.For(probe), cancellationToken).ConfigureAwait(false) is null)
            {
                absent = probe;
                break;
            }

            present = probe;
            step <<= 1;
        }

        while (absent - present > 1)
        {
            ulong middle = present + ((absent - present) >> 1);
            if (await store.HeadAsync(CommitKey.For(middle), cancellationToken).ConfigureAwait(false) is null)
            {
                absent = middle;
            }
            else
            {
                present = middle;
            }
        }

        return present;
    }

    /// <summary>The latest version and its commit object, in one listing and one read; (0, null)
    /// when the dataset has none.</summary>
    public static async ValueTask<(ulong Version, CommitObject? Commit)> LatestAsync(
        IObjectStore store, CancellationToken cancellationToken)
    {
        ulong version = await NewestVersionAsync(store, cancellationToken).ConfigureAwait(false);
        if (version == 0)
        {
            return (0, null);
        }

        try
        {
            return (version, await CommitObject.OpenAsync(store, CommitKey.For(version), cancellationToken).ConfigureAwait(false));
        }
        catch (CommitFormatException torn)
        {
            throw TornCommitException.Of(version, torn);
        }
    }

    /// <summary>The version of the newest commit object, without opening it; 0 when there is none.</summary>
    public static async ValueTask<ulong> NewestVersionAsync(IObjectStore store, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        IReadOnlyList<string> newest = await store
            .ListAsync(CommitKey.Prefix, null, 1, cancellationToken).ConfigureAwait(false);
        return newest.Count == 1 && CommitKey.TryParse(newest[0], out ulong version) ? version : 0;
    }

    /// <summary>
    /// Whether the commit object of <paramref name="version"/> is whole: read in full and checked from
    /// its preamble to its trailer, which a writer that stopped early never wrote.
    /// </summary>
    public static async ValueTask<bool> IsWholeAsync(IObjectStore store, ulong version, CancellationToken cancellationToken)
    {
        string key = CommitKey.For(version);
        ObjectHead head = await store.HeadAsync(key, cancellationToken).ConfigureAwait(false)
            ?? throw ObjectNotFoundException.For(key);
        if (head.Length > int.MaxValue)
        {
            return false;
        }

        const int Chunk = 1 << 20;
        byte[] bytes = new byte[head.Length];
        for (int at = 0; at < bytes.Length; at += Chunk)
        {
            int length = Math.Min(Chunk, bytes.Length - at);
            using ObjectRange range = await store.GetRangeAsync(key, at, length, cancellationToken).ConfigureAwait(false);
            if (range.Length != length)
            {
                return false;
            }

            range.Bytes.CopyTo(bytes.AsSpan(at));
        }

        try
        {
            CommitObject.Open(bytes, bytes.Length);
            return true;
        }
        catch (CommitFormatException)
        {
            return false;
        }
    }

    /// <summary>
    /// The pages a header carries, top first, spending what is left of its inline room. Only pages
    /// already in hand are inlined: reading one just to inline it would spend a request to save
    /// bytes, and the rewrite already holds the levels above the leaves.
    /// </summary>
    private static IReadOnlyList<InlinedPage> Inline(
        CommitObjectBuilder builder, CommitPageSource pages, DatasetTree tree, ref long budget)
    {
        List<InlinedPage> inlined = [];
        Queue<(PageReference Reference, int Level)> queue = new Queue<(PageReference, int)>();
        queue.Enqueue((tree.Root, tree.Depth));
        while (queue.Count > 0)
        {
            (PageReference reference, int level) = queue.Dequeue();
            if (!builder.TryGetPage(reference, out ReadOnlyMemory<byte> page)
                && !pages.TryGetKnown(reference, out page))
            {
                continue;
            }

            if (page.Length > budget)
            {
                break;
            }

            inlined.Add(new InlinedPage(reference, page));
            budget -= page.Length;
            if (level > 1)
            {
                foreach (InternalEntry entry in TreePage.ReadInternal(page))
                {
                    queue.Enqueue((entry.Child, level - 1));
                }
            }
        }

        return inlined;
    }

    /// <summary>What the inlined pages may take, leaving room under the header's size cap for
    /// everything else it carries.</summary>
    private const int InlineBudget = 192 << 10;

    /// <summary>The tree a header names at level 0, where appends land; empty when it names
    /// none.</summary>
    public static DatasetTree TreeOf(CommitHeader header) => DatasetLevels.Of(header)[0];

    /// <summary>
    /// What a header records for each occupied level, with the pages it can inline. The levels share
    /// one inline budget and spend it from level 0 up, since every lookup descends level 0; a level
    /// whose top does not fit costs one request to read instead.
    /// </summary>
    private static IReadOnlyList<CommitLevel> LevelsOf(
        DatasetLevels levels, CommitObjectBuilder builder, CommitPageSource pages)
    {
        List<CommitLevel> recorded = [];
        long budget = InlineBudget;
        foreach ((int level, DatasetTree tree) in levels.Occupied())
        {
            IReadOnlyList<InlinedPage> inlined = Inline(builder, pages, tree, ref budget);
            recorded.Add(new CommitLevel(level, tree.Entries, tree.Root, inlined)
            {
                Depth = tree.Depth,
                Rows = tree.Rows,
            });
        }

        return recorded;
    }

    /// <summary>Re-applies the operations to the levels as they are now.</summary>
    private static async ValueTask<(Dictionary<int, List<TreeChange>> Changes, List<OperationOutcome> Outcomes, Relocation Repack)>
        ApplyAsync(
            DatasetLevels levels,
            IReadOnlyList<DatasetOperation> operations,
            CommitPageSource pages,
            CommitObjectBuilder builder,
            CancellationToken cancellationToken)
    {
        Relocation repack = new Relocation();
        // Sorted and unique by key within a level, which is what a batch has to be; an operation
        // that touches a key another already touched sees the pending value, so two fragments on
        // one object both land. One batch per level, because one tree per level.
        Dictionary<int, SortedDictionary<byte[], TreeChange>> changes = [];
        Dictionary<string, ObjectEntry?> pending = new Dictionary<string, ObjectEntry?>(StringComparer.Ordinal);
        List<OperationOutcome> outcomes = new List<OperationOutcome>(operations.Count);

        foreach (DatasetOperation operation in operations)
        {
            switch (operation)
            {
                case DatasetOperation.AddObject add:
                {
                    ObjectEntry? current = await CurrentAsync(add.Level, add.Key).ConfigureAwait(false);
                    if (current is { } held && held.Uid == add.Entry.Uid)
                    {
                        // The same uid is the same bytes, so the winner already added this object.
                        outcomes.Add(OperationOutcome.AlreadyThere);
                        break;
                    }

                    Put(add.Level, add.Key, add.Entry);
                    outcomes.Add(OperationOutcome.Applied);
                    break;
                }

                case DatasetOperation.AddFragment fragment:
                {
                    ObjectEntry? current = await CurrentAsync(fragment.Level, fragment.Key).ConfigureAwait(false);
                    if (current is not { } entry || entry.Uid != fragment.Uid)
                    {
                        // The object is gone, or is not the one the fragment was built against.
                        outcomes.Add(OperationOutcome.Dropped);
                        break;
                    }

                    if (entry.Holds(fragment.Fragment.Span))
                    {
                        // Another indexer got there first with the same fragment; its bytes are not
                        // written a second time.
                        outcomes.Add(OperationOutcome.AlreadyThere);
                        break;
                    }

                    PageReference written = builder.AddFragment(fragment.Fragment.Span);
                    Put(fragment.Level, fragment.Key, entry.With(written));
                    outcomes.Add(OperationOutcome.Applied);
                    break;
                }

                case DatasetOperation.DropFragment drop:
                {
                    ObjectEntry? current = await CurrentAsync(drop.Level, drop.Key).ConfigureAwait(false);
                    if (current is not { } entry)
                    {
                        outcomes.Add(OperationOutcome.Dropped);
                        break;
                    }

                    if (!entry.Holds(drop.Fragment))
                    {
                        outcomes.Add(OperationOutcome.AlreadyThere);
                        break;
                    }

                    Put(drop.Level, drop.Key, entry.Without(drop.Fragment));
                    outcomes.Add(OperationOutcome.Applied);
                    break;
                }

                case DatasetOperation.ReplaceObjects replace:
                {
                    bool complete = true;
                    foreach ((int level, ReadOnlyMemory<byte> input) in replace.Inputs)
                    {
                        complete &= await CurrentAsync(level, input).ConfigureAwait(false) is not null;
                    }

                    if (!complete)
                    {
                        // Another compaction took an input, so this one's outputs are garbage.
                        outcomes.Add(OperationOutcome.Abandoned);
                        break;
                    }

                    foreach ((int level, ReadOnlyMemory<byte> input) in replace.Inputs)
                    {
                        Remove(level, input);
                    }

                    foreach ((int level, ReadOnlyMemory<byte> key, ObjectEntry entry) in replace.Outputs)
                    {
                        Put(level, key, entry);
                    }

                    outcomes.Add(OperationOutcome.Applied);
                    break;
                }

                case DatasetOperation.Repack move:
                {
                    // The fragments move here, as leaf changes: an entry names its fragments, so a
                    // moved fragment is a changed entry. The pages move after the batches.
                    repack.Versions.UnionWith(move.Versions);
                    repack.At.Add(outcomes.Count);
                    outcomes.Add(OperationOutcome.AlreadyThere);
                    foreach ((int level, DatasetTree tree) in levels.Occupied())
                    {
                        await foreach (TreeEntry leaf in tree.EnumerateAsync(pages, cancellationToken).ConfigureAwait(false))
                        {
                            ObjectEntry entry = await CurrentAsync(level, leaf.Key).ConfigureAwait(false)
                                ?? ObjectEntry.FromBytes(leaf.Value.Span);
                            PageReference[] fragments = [.. entry.Fragments];
                            bool changed = false;
                            for (int i = 0; i < fragments.Length; i++)
                            {
                                if (repack.Versions.Contains(fragments[i].Version))
                                {
                                    ReadOnlyMemory<byte> bytes = await pages
                                        .ReadFragmentAsync(fragments[i], cancellationToken).ConfigureAwait(false);
                                    fragments[i] = builder.AddFragment(bytes.Span);
                                    changed = true;
                                    repack.Moved++;
                                }
                            }

                            if (changed)
                            {
                                Put(level, leaf.Key, entry with { Fragments = fragments });
                            }
                        }
                    }

                    break;
                }

                default:
                    throw new ArgumentException(
                        $"An operation of type {operation.GetType().Name} is not one a commit knows.", nameof(operations));
            }
        }

        Dictionary<int, List<TreeChange>> batches = [];
        foreach ((int level, SortedDictionary<byte[], TreeChange> sorted) in changes)
        {
            batches[level] = [.. sorted.Values];
        }

        return (batches, outcomes, repack);

        async ValueTask<ObjectEntry?> CurrentAsync(int level, ReadOnlyMemory<byte> key)
        {
            string text = Named(level, key);
            if (pending.TryGetValue(text, out ObjectEntry? held))
            {
                return held;
            }

            TreeEntry? entry = await levels[level].FindAsync(key, pages, cancellationToken).ConfigureAwait(false);
            return entry is { } found ? ObjectEntry.FromBytes(found.Value.Span) : null;
        }

        void Put(int level, ReadOnlyMemory<byte> key, ObjectEntry entry)
        {
            Batch(level)[key.ToArray()] = TreeChange.Put(key, entry.ToBytes(), entry.Rows);
            pending[Named(level, key)] = entry;
        }

        void Remove(int level, ReadOnlyMemory<byte> key)
        {
            Batch(level)[key.ToArray()] = TreeChange.Remove(key);
            pending[Named(level, key)] = null;
        }

        SortedDictionary<byte[], TreeChange> Batch(int level)
        {
            if (!changes.TryGetValue(level, out SortedDictionary<byte[], TreeChange>? batch))
            {
                batch = new SortedDictionary<byte[], TreeChange>(KeyOrder.Instance);
                changes[level] = batch;
            }

            return batch;
        }

        // A key is unique inside a level and a compaction moves one object from one level to
        // another, so the pending map is keyed by both: the same key at two levels is two objects.
        static string Named(int level, ReadOnlyMemory<byte> key) =>
            level.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + Convert.ToHexString(key.Span);
    }

    private sealed class Relocation
    {
        internal HashSet<ulong> Versions { get; } = [];

        internal List<int> At { get; } = [];

        internal int Moved { get; set; }
    }

    /// <summary>The tree's own byte order over keys.</summary>
    private sealed class KeyOrder : IComparer<byte[]>
    {
        internal static KeyOrder Instance { get; } = new KeyOrder();

        public int Compare(byte[]? x, byte[]? y) => TreePage.Compare(x, y);
    }
}
