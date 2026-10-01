using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Expressions;
using Vorticity.Scanning;

namespace Vorticity.Dataset;

/// <summary>
/// One version of a dataset as a reader holds it: its header, its levels, and where their pages are
/// read from. Immutable, so a scan built over it reads that version whatever its handle does next.
/// </summary>
internal sealed class DatasetSnapshot
{
    private readonly ObjectCache _objects;
    private long _knownAt;

    internal DatasetSnapshot(
        CommitObject commit, CommitPageSource pages, ObjectCache objects, DateTimeOffset knownAt, DatasetSchema? previous = null)
    {
        Commit = commit;
        Header = commit.Header;
        Levels = DatasetLevels.Of(Header);
        Pages = pages;
        Schema = DatasetSchema.Of(Header, previous);
        _objects = objects;
        _knownAt = knownAt.UtcTicks;
    }

    /// <summary>The version's commit object, as the handle read it or wrote it.</summary>
    internal CommitObject Commit { get; }

    /// <summary>The version's header.</summary>
    internal CommitHeader Header { get; }

    /// <summary>
    /// The last time the handle knew this version to be the latest, by its own clock. A version after
    /// it was created later, and vacuum takes a commit only a retention window after the next one
    /// superseded it: until then every version after this one is still in the store.
    /// </summary>
    internal DateTimeOffset KnownAt => new DateTimeOffset(Interlocked.Read(ref _knownAt), TimeSpan.Zero);

    /// <summary>The version's schema, which every object it holds reads as.</summary>
    internal DatasetSchema Schema { get; }

    /// <summary>The version.</summary>
    internal ulong Version => Header.Version;

    /// <summary>Its levels, where level 0 is the one an append lands in.</summary>
    internal DatasetLevels Levels { get; }

    /// <summary>Where its tree pages are read from.</summary>
    internal CommitPageSource Pages { get; }

    /// <summary>The rows of every object it holds.</summary>
    internal long RowCount => Levels.Rows;

    /// <summary>Records that the handle found, at <paramref name="at"/>, that this version was still the latest.</summary>
    internal void Confirm(DateTimeOffset at)
    {
        long ticks = at.UtcTicks;
        long seen = Interlocked.Read(ref _knownAt);
        while (ticks > seen)
        {
            long found = Interlocked.CompareExchange(ref _knownAt, ticks, seen);
            if (found == seen)
            {
                return;
            }

            seen = found;
        }
    }

    /// <summary>
    /// The objects inside <c>[from, to)</c> whose summaries, and their ancestors', do not refute
    /// the pruner, in key order. A null pruner keeps every object.
    /// </summary>
    internal async IAsyncEnumerable<PositionedObject> WalkAsync(
        SummaryPruner? pruner,
        long from,
        long to,
        DatasetScanMetrics? metrics,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        Func<InternalEntry, bool>? mayMatch = pruner is null ? null : node =>
            node.Summary.IsEmpty
            || ObjectSummaries.FromBytes(ObjectSummaryFold.SummariesOf(node.Summary.Span, out _)).MayMatch(pruner, node.Rows);

        await foreach ((TreeEntry held, long firstRow, int level) in
            EntriesAsync(from, to, mayMatch, metrics, cancellationToken).ConfigureAwait(false))
        {
            ObjectEntry entry = ObjectEntry.FromBytes(held.Value);
            if (metrics is { } counters)
            {
                counters.ObjectsConsidered++;
            }

            if (pruner is not null && !entry.Summaries.MayMatch(pruner, entry.Rows))
            {
                if (metrics is { } skipped)
                {
                    skipped.ObjectsSkipped++;
                }

                continue;
            }

            yield return new PositionedObject(entry, firstRow) { Level = level, TreeKey = held.Key };
        }
    }

    /// <summary>
    /// Whether a row <paramref name="filter"/> selects may lie in this version, from the summaries
    /// of the pages already in hand, its header's and those the handle kept: false is a proof, and a
    /// page that would have to be read makes the answer true.
    /// </summary>
    internal bool MayMatch(VortexExpr filter)
    {
        SummaryPruner pruner = new SummaryPruner(filter);
        foreach ((int _, DatasetTree tree) in Levels.Occupied())
        {
            if (MayMatch(tree.Root, tree.Depth, pruner))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Borrows one of the version's data objects, open with the index fragments its entry names.
    /// The caller disposes the lease when it is done reading.
    /// </summary>
    /// <exception cref="ObjectNotFoundException">
    /// The object is gone: this version fell out of the retention window and vacuum took it.
    /// </exception>
    internal async ValueTask<ObjectLease> RentAsync(ObjectEntry entry, CancellationToken cancellationToken)
    {
        try
        {
            if (entry.IsResolved)
            {
                return await _objects.RentAsync(entry, Pages, cancellationToken).ConfigureAwait(false);
            }

            // A vector out of line is read beside the object's open, which does not wait on it.
            Task<ObjectLease> renting = _objects.RentAsync(entry, Pages, cancellationToken).AsTask();
            try
            {
                await entry.ResolveAsync(Pages, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // The lease the rent hands back goes back at once; a rent that failed as well has
                // nothing to return, and the failure already raised says more.
                try
                {
                    await (await renting.ConfigureAwait(false)).DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception) when (renting.IsFaulted || renting.IsCanceled)
                {
                }

                throw;
            }

            return await renting.ConfigureAwait(false);
        }
        catch (ObjectNotFoundException missing) when (missing.Version == 0)
        {
            throw ObjectNotFoundException.InVersion(entry.Key, Version, missing);
        }
    }

    private bool MayMatch(PageReference reference, int depth, SummaryPruner pruner)
    {
        if (!Pages.TryGetInHand(reference, out ReadOnlyMemory<byte> page))
        {
            return true;
        }

        if (depth == 1)
        {
            foreach (TreeEntry leaf in TreePage.ReadLeaf(page))
            {
                ObjectEntry entry = ObjectEntry.FromBytes(leaf.Value);
                if (entry.Summaries.MayMatch(pruner, entry.Rows))
                {
                    return true;
                }
            }

            return false;
        }

        foreach (InternalEntry child in TreePage.ReadInternal(page))
        {
            bool refuted = !child.Summary.IsEmpty
                && !ObjectSummaries.FromBytes(ObjectSummaryFold.SummariesOf(child.Summary.Span, out _)).MayMatch(pruner, child.Rows);
            if (!refuted && MayMatch(child.Child, depth - 1, pruner))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Every level's entries, merged into one key order, each with its first row in the dataset,
    /// past the subtrees <paramref name="mayMatch"/> rules out. A single level takes its own walk,
    /// which places a subtree ruled out by its parent's row sum without reading it.
    /// </summary>
    /// <remarks>
    /// Across levels a row's place depends on the entries of every level below it in key order, those
    /// of a subtree ruled out included: each level is walked with the subtrees it rules out standing
    /// as single steps, whose rows are counted whole when no step of another level falls inside their
    /// key span, and which are read after all when one does, since their rows then interleave with
    /// that level's. Only the parts of a level that interleave with another are read for nothing.
    /// </remarks>
    private async IAsyncEnumerable<(TreeEntry Entry, long FirstRow, int Level)> EntriesAsync(
        long from,
        long to,
        Func<InternalEntry, bool>? mayMatch,
        DatasetScanMetrics? metrics,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        List<DatasetTree> trees = [];
        List<int> levels = [];
        foreach ((int level, DatasetTree tree) in Levels.Occupied())
        {
            trees.Add(tree);
            levels.Add(level);
        }

        if (trees.Count <= 1)
        {
            DatasetTree only = trees.Count == 1 ? trees[0] : DatasetTree.Empty;
            int level = trees.Count == 1 ? levels[0] : 0;
            Func<InternalEntry, bool>? descend = mayMatch is null ? null : node =>
            {
                if (mayMatch(node))
                {
                    return true;
                }

                if (metrics is { } counters)
                {
                    counters.SubtreesSkipped++;
                }

                return false;
            };

            await foreach (PositionedEntry positioned in
                only.WalkAsync(Pages, from, to, descend, cancellationToken).ConfigureAwait(false))
            {
                yield return (positioned.Entry, positioned.FirstRow, level);
            }

            yield break;
        }

        TreeWalk[] walks = new TreeWalk[trees.Count];
        bool[] live = new bool[trees.Count];
        try
        {
            for (int i = 0; i < trees.Count; i++)
            {
                walks[i] = new TreeWalk(trees[i], Pages, mayMatch, cancellationToken);
                live[i] = await walks[i].MoveNextAsync().ConfigureAwait(false);
            }

            long row = 0;
            while (row < to)
            {
                // Ties go to the lower level, which is the order the merge gives every key.
                int smallest = -1;
                for (int i = 0; i < walks.Length; i++)
                {
                    if (live[i] && (smallest < 0 || TreePage.Compare(walks[i].Key, walks[smallest].Key) < 0))
                    {
                        smallest = i;
                    }
                }

                if (smallest < 0)
                {
                    yield break;
                }

                TreeWalk walk = walks[smallest];
                if (walk.IsRuledOut)
                {
                    if (Interleaves(walks, live, smallest))
                    {
                        walk.Expand();
                        live[smallest] = await walk.MoveNextAsync().ConfigureAwait(false);
                        continue;
                    }

                    if (metrics is { } counters)
                    {
                        counters.SubtreesSkipped++;
                    }
                }
                else if (row + walk.Entry.Rows > from)
                {
                    yield return (walk.Entry, row, levels[smallest]);
                }

                row += walk.Rows;
                live[smallest] = await walk.MoveNextAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            foreach (TreeWalk? walk in walks)
            {
                if (walk is not null)
                {
                    await walk.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
    }

    /// <summary>
    /// Whether a step of another level falls inside the key span of the subtree level
    /// <paramref name="ruledOut"/> stands at, which is the smallest step: its rows and that step's then
    /// interleave in the merge. A step at the subtree's largest key comes before it from a lower level only.
    /// </summary>
    private static bool Interleaves(TreeWalk[] walks, bool[] live, int ruledOut)
    {
        ReadOnlySpan<byte> largest = walks[ruledOut].Subtree.MaxKey.Span;
        for (int i = 0; i < walks.Length; i++)
        {
            if (i == ruledOut || !live[i])
            {
                continue;
            }

            int order = TreePage.Compare(walks[i].Key, largest);
            if (order < 0 || (order == 0 && i < ruledOut))
            {
                return true;
            }
        }

        return false;
    }
}
