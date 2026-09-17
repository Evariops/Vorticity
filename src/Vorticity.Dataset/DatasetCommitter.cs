// The commit protocol - docs/13-dataset.md §8.1 and §8.2.
//
// THREE DEPENDENT REQUESTS, uncontended (§8.1): the `List` that finds the latest version, the read
// of its header, the `PutIfAbsent` that creates the next one. The data objects were uploaded before
// and in parallel; they cost the commit nothing. Nothing else is on the critical path, which is why
// there is no lease, no lock and no external service: put-if-absent linearises commits by itself.
//
// AND ON A LOSS, A REBASE RATHER THAN A MERGE (§8.2). A writer that had produced a TREE would have
// to merge two trees, which is either wrong -- whose page wins? -- or expensive. This writer kept
// its INTENTIONS, so it re-reads the winner and re-applies them to the winner's tree. Each
// intention knows what to do when the ground moved: the seven rows of §8.2's matrix are the
// branches in `Apply`, and the rebase matrix test walks them.
//
// WHAT AN ITERATION COSTS: "the header of N+1, the touched leaves, the creation", `depth + 2`
// dependent requests. The counting store measures it rather than this comment asserting it.
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Vorticity.Dataset;

/// <summary>What a commit needs to know beyond its operations.</summary>
public sealed record CommitOptions
{
    /// <summary>The dataset's chunking seed (§4.1), fixed at creation and carried by every header.</summary>
    public required ulong Seed { get; init; }

    /// <summary>The header a first commit starts from: schema, clustering key, settings.</summary>
    /// <remarks>Ignored once the dataset exists; the winner's header is then the one that carries on.</remarks>
    public CommitHeader? Template { get; init; }

    /// <summary>How many times to rebase before giving up.</summary>
    public int MaxAttempts { get; init; } = 8;

    /// <summary>The boundary rule, or null for the prolly rule at this dataset's seed.</summary>
    public IBoundaryRule? Rule { get; init; }

    /// <summary>A rule in its starting state.</summary>
    /// <returns>The rule.</returns>
    public IBoundaryRule NewRule() => Rule?.Fresh() ?? new ProllyBoundaryRule(Seed);
}

/// <summary>What a commit did.</summary>
/// <param name="Version">The version created.</param>
/// <param name="Key">Its commit object's key.</param>
/// <param name="Tree">The tree that version names.</param>
/// <param name="Outcomes">What each operation decided, in the caller's order.</param>
/// <param name="Attempts">How many times the writer had to rebase, 1 when it won first time.</param>
/// <param name="Pages">A source that can read the new version's pages, new and old.</param>
public sealed record CommitResult(
    ulong Version,
    string Key,
    DatasetTree Tree,
    IReadOnlyList<OperationOutcome> Outcomes,
    int Attempts,
    IPageSource Pages);

/// <summary>Creates versions of a dataset, one conditional creation at a time.</summary>
public static class DatasetCommitter
{
    /// <summary>Applies the operations to the dataset's latest version, rebasing until it wins.</summary>
    /// <param name="store">The dataset's store.</param>
    /// <param name="operations">What to do, in the caller's order.</param>
    /// <param name="options">The seed and the rest.</param>
    /// <param name="cancellationToken">Cancels the requests.</param>
    /// <returns>What the commit did.</returns>
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

        for (int attempt = 1; attempt <= options.MaxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // 1. The latest version, in one request (§8.3).
            (ulong parent, CommitObject? commit) = await LatestAsync(store, cancellationToken).ConfigureAwait(false);
            CommitPageSource pages = new CommitPageSource(store);
            DatasetTree tree = DatasetTree.Empty;
            CommitHeader template = options.Template ?? new CommitHeader { Version = 1 };
            if (commit is not null)
            {
                template = commit.Header;
                pages.Inline(commit.Header);
                pages.Know(parent, commit.HeaderEnd);
                tree = TreeOf(commit.Header);
            }

            // 2. The operations, re-applied to whatever is there now (§8.2).
            (List<TreeChange> changes, List<OperationOutcome> outcomes) =
                await ApplyAsync(tree, operations, pages, cancellationToken).ConfigureAwait(false);

            ulong version = parent + 1;
            CommitObjectBuilder builder = new CommitObjectBuilder(version);
            pages.Writing(builder, version);
            DatasetTree next = changes.Count == 0
                ? tree
                : await tree.CommitAsync(changes, options.NewRule(), pages, builder, cancellationToken)
                    .ConfigureAwait(false);

            CommitHeader header = template with
            {
                Version = version,
                Parent = parent,
                Seed = options.Seed,
                CreatedAtUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Levels = next.IsEmpty
                    ? []
                    : [new CommitLevel(0, next.Entries, next.Root, Inline(builder, pages, next))
                    {
                        Depth = next.Depth,
                        Rows = next.Rows,
                    }],
            };

            // 3. One conditional creation (§8.1).
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
            "answer to contention, not more attempts (docs/13-dataset.md §8.2).");
    }

    /// <summary>The latest version and its commit object, in one listing and one read (§8.3).</summary>
    /// <param name="store">The store.</param>
    /// <param name="cancellationToken">Cancels the requests.</param>
    /// <returns>The version and its commit, or (0, null) when the dataset has none.</returns>
    public static async ValueTask<(ulong Version, CommitObject? Commit)> LatestAsync(
        IObjectStore store, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        IReadOnlyList<string> newest = await store
            .ListAsync(CommitKey.Prefix, null, 1, cancellationToken).ConfigureAwait(false);
        if (newest.Count == 0 || !CommitKey.TryParse(newest[0], out ulong version))
        {
            return (0, null);
        }

        return (version, await CommitObject.OpenAsync(store, newest[0], cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// The pages §3 asks a header to carry: "its top page inlined, and the pages below it too while
    /// the header stays under 256 KiB".
    /// </summary>
    /// <param name="builder">The commit being built, which holds the pages it just wrote.</param>
    /// <param name="pages">The source, which holds every page this commit read or was handed.</param>
    /// <param name="tree">The new tree.</param>
    /// <returns>The pages to inline, top first.</returns>
    /// <remarks>
    /// ONLY THE PAGES ALREADY IN HAND — the ones this commit wrote, the ones its predecessor's
    /// header carried, and the ones it read on the way. A commit that READ a page just to inline it
    /// would trade the thing §8.1 counts, requests, for the thing it does not. What is in hand,
    /// though, is most of what matters: the levels above the leaves are read by the rewrite itself,
    /// so a header ends up carrying the top of the tree without one extra request, and the next
    /// commit's descent finds it there.
    /// </remarks>
    private static IReadOnlyList<InlinedPage> Inline(
        CommitObjectBuilder builder, CommitPageSource pages, DatasetTree tree)
    {
        List<InlinedPage> inlined = [];
        long bytes = 0;
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


            if (bytes + page.Length > InlineBudget)
            {
                break;
            }

            inlined.Add(new InlinedPage(reference, page));
            bytes += page.Length;
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

    /// <summary>
    /// What the inlined pages may take, under §3's "while the header stays under 256 KiB" with room
    /// left for everything else the header carries.
    /// </summary>
    private const int InlineBudget = 192 << 10;

    /// <summary>The tree a header names, at level 0.</summary>
    /// <param name="header">The header.</param>
    /// <returns>The tree, empty when the header names no level.</returns>
    /// <remarks>
    /// One level for now: §5's levels arrive with compaction, and the header already carries a list
    /// so that adding them changes this line and nothing else.
    /// </remarks>
    public static DatasetTree TreeOf(CommitHeader header)
    {
        ArgumentNullException.ThrowIfNull(header);
        foreach (CommitLevel level in header.Levels)
        {
            if (level.Level == 0 && level.Top.Exists)
            {
                return new DatasetTree(level.Top, level.Depth, level.Entries, level.Rows);
            }
        }

        return DatasetTree.Empty;
    }

    /// <summary>Re-applies the operations to the tree as it is now, by §8.2's rules.</summary>
    private static async ValueTask<(List<TreeChange> Changes, List<OperationOutcome> Outcomes)> ApplyAsync(
        DatasetTree tree,
        IReadOnlyList<DatasetOperation> operations,
        IPageSource pages,
        CancellationToken cancellationToken)
    {
        // Sorted and unique by key, which is what a batch has to be; an operation that touches a key
        // another already touched sees the pending value, so two fragments on one object both land.
        SortedDictionary<byte[], TreeChange> changes = new SortedDictionary<byte[], TreeChange>(KeyOrder.Instance);
        Dictionary<string, ObjectEntry?> pending = new Dictionary<string, ObjectEntry?>(StringComparer.Ordinal);
        List<OperationOutcome> outcomes = new List<OperationOutcome>(operations.Count);

        foreach (DatasetOperation operation in operations)
        {
            switch (operation)
            {
                case DatasetOperation.AddObject add:
                {
                    ObjectEntry? current = await CurrentAsync(add.Key).ConfigureAwait(false);
                    if (current is { } held && held.Uid == add.Entry.Uid)
                    {
                        // The winner already added this very object: the same uid is the same bytes.
                        outcomes.Add(OperationOutcome.AlreadyThere);
                        break;
                    }

                    Put(add.Key, add.Entry);
                    outcomes.Add(OperationOutcome.Applied);
                    break;
                }

                case DatasetOperation.AddFragment fragment:
                {
                    ObjectEntry? current = await CurrentAsync(fragment.Key).ConfigureAwait(false);
                    if (current is not { } entry || entry.Uid != fragment.Uid)
                    {
                        // The object is gone, or it is not the object the fragment was built
                        // against: the fragment is dropped and never written (§8.2, row 4).
                        outcomes.Add(OperationOutcome.Dropped);
                        break;
                    }

                    if (entry.Holds(fragment.Fragment))
                    {
                        // Another indexer got there first with the same fragment (§8.2, row 3).
                        outcomes.Add(OperationOutcome.AlreadyThere);
                        break;
                    }

                    Put(fragment.Key, entry.With(fragment.Fragment));
                    outcomes.Add(OperationOutcome.Applied);
                    break;
                }

                case DatasetOperation.DropFragment drop:
                {
                    ObjectEntry? current = await CurrentAsync(drop.Key).ConfigureAwait(false);
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

                    List<PageReference> kept = [];
                    foreach (PageReference held in entry.Fragments)
                    {
                        if (held != drop.Fragment)
                        {
                            kept.Add(held);
                        }
                    }

                    Put(drop.Key, entry with { Fragments = kept });
                    outcomes.Add(OperationOutcome.Applied);
                    break;
                }

                case DatasetOperation.ReplaceObjects replace:
                {
                    bool complete = true;
                    foreach (ReadOnlyMemory<byte> input in replace.Inputs)
                    {
                        complete &= await CurrentAsync(input).ConfigureAwait(false) is not null;
                    }

                    if (!complete)
                    {
                        // An input is missing: another compaction took it, and this one's outputs
                        // are garbage (§8.2, row 5).
                        outcomes.Add(OperationOutcome.Abandoned);
                        break;
                    }

                    foreach (ReadOnlyMemory<byte> input in replace.Inputs)
                    {
                        Remove(input);
                    }

                    foreach ((ReadOnlyMemory<byte> key, ObjectEntry entry) in replace.Outputs)
                    {
                        Put(key, entry);
                    }

                    outcomes.Add(OperationOutcome.Applied);
                    break;
                }

                default:
                    throw new ArgumentException(
                        $"An operation of type {operation.GetType().Name} is not one of §8.2's.", nameof(operations));
            }
        }

        return ([.. changes.Values], outcomes);

        async ValueTask<ObjectEntry?> CurrentAsync(ReadOnlyMemory<byte> key)
        {
            string text = Convert.ToHexString(key.Span);
            if (pending.TryGetValue(text, out ObjectEntry? held))
            {
                return held;
            }

            TreeEntry? entry = await tree.FindAsync(key, pages, cancellationToken).ConfigureAwait(false);
            return entry is { } found ? ObjectEntry.FromBytes(found.Value.Span) : null;
        }

        void Put(ReadOnlyMemory<byte> key, ObjectEntry entry)
        {
            changes[key.ToArray()] = TreeChange.Put(key, entry.ToBytes(), entry.Rows);
            pending[Convert.ToHexString(key.Span)] = entry;
        }

        void Remove(ReadOnlyMemory<byte> key)
        {
            changes[key.ToArray()] = TreeChange.Remove(key);
            pending[Convert.ToHexString(key.Span)] = null;
        }
    }

    /// <summary>`memcmp` order over keys, which is the tree's (06).</summary>
    private sealed class KeyOrder : IComparer<byte[]>
    {
        internal static KeyOrder Instance { get; } = new KeyOrder();

        public int Compare(byte[]? x, byte[]? y) => TreePage.Compare(x, y);
    }
}
