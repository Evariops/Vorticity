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

    internal DatasetSnapshot(CommitHeader header, CommitPageSource pages, ObjectCache objects, DatasetSchema? previous = null)
    {
        Header = header;
        Levels = DatasetLevels.Of(header);
        Pages = pages;
        Schema = DatasetSchema.Of(header, previous);
        _objects = objects;
    }

    /// <summary>The version's header.</summary>
    internal CommitHeader Header { get; }

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
        Func<InternalEntry, bool>? descend = pruner is null ? null : node =>
        {
            if (node.Summary.IsEmpty
                || ObjectSummaries.FromBytes(ObjectSummaryFold.SummariesOf(node.Summary.Span, out _)).MayMatch(pruner, node.Rows))
            {
                return true;
            }

            if (metrics is { } counters)
            {
                counters.SubtreesSkipped++;
            }

            return false;
        };

        await foreach ((TreeEntry held, long firstRow, int level) in
            EntriesAsync(from, to, descend, cancellationToken).ConfigureAwait(false))
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
            return await _objects.RentAsync(entry, Pages, cancellationToken).ConfigureAwait(false);
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
    /// Every level's entries, merged into one key order, each with its first row in the dataset. A
    /// single level takes its own walk, which tests a subtree's row sum before descending into it;
    /// across levels the row offsets are not known until the merge has produced them, so the range
    /// is applied per entry instead and the cost is the objects rather than the rows.
    /// </summary>
    private async IAsyncEnumerable<(TreeEntry Entry, long FirstRow, int Level)> EntriesAsync(
        long from,
        long to,
        Func<InternalEntry, bool>? descend,
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
            await foreach (PositionedEntry positioned in
                only.WalkAsync(Pages, from, to, descend, cancellationToken).ConfigureAwait(false))
            {
                yield return (positioned.Entry, positioned.FirstRow, level);
            }

            yield break;
        }

        IAsyncEnumerator<PositionedEntry>[] walks = new IAsyncEnumerator<PositionedEntry>[trees.Count];
        bool[] live = new bool[trees.Count];
        try
        {
            for (int i = 0; i < trees.Count; i++)
            {
                walks[i] = trees[i]
                    .WalkAsync(Pages, 0, long.MaxValue, descend, cancellationToken)
                    .GetAsyncEnumerator(cancellationToken);
                live[i] = await walks[i].MoveNextAsync().ConfigureAwait(false);
            }

            long row = 0;
            while (true)
            {
                int smallest = -1;
                for (int i = 0; i < walks.Length; i++)
                {
                    if (live[i]
                        && (smallest < 0
                            || TreePage.Compare(
                                walks[i].Current.Entry.Key.Span, walks[smallest].Current.Entry.Key.Span) < 0))
                    {
                        smallest = i;
                    }
                }

                if (smallest < 0)
                {
                    yield break;
                }

                TreeEntry entry = walks[smallest].Current.Entry;
                if (row < to && row + entry.Rows > from)
                {
                    yield return (entry, row, levels[smallest]);
                }

                row += entry.Rows;
                live[smallest] = await walks[smallest].MoveNextAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            foreach (IAsyncEnumerator<PositionedEntry>? walk in walks)
            {
                if (walk is not null)
                {
                    await walk.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
    }
}
