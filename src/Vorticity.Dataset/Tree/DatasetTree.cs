// The dataset tree - docs/13-dataset.md §4: "one tree per level, each a copy-on-write search tree
// over data objects, ordered by the clustering key".
//
// WHAT A COMMIT DOES, §4.3 in one sentence: "load the touched leaves, merge the changes, re-emit
// the leaves, rebuild the internal nodes over the changed range up to the top". The boundary rule
// decides where the re-emitted pages end, and §4.1's third bullet is what makes the result a
// function of the key set rather than of the history: "after an edit, re-chunking continues past
// the change until a new boundary coincides with an old one".
//
// HOW THAT COINCIDENCE IS DETECTED HERE, and it is simpler than it sounds: the rule's state resets
// at every boundary, and every old page began at a boundary. So an old page whose keys no change
// touches can be REUSED BY REFERENCE -- not read, not rewritten -- exactly when the emitter is
// between pages. After a change, the emitter is mid-page and the following old pages are read and
// re-chunked until a cut lands where a page began; from there, reuse resumes. That single
// condition, `untouched && emitter.IsEmpty`, is the convergence rule.
//
// WHAT THIS COMMIT COSTS, honestly, and what it does not yet do. Pages are WRITTEN only where
// something changed, at every level: an untouched page is a reference. Pages are READ, at the
// levels above the leaves, in full -- the descriptors of a level live in the level above it, and
// this implementation materialises them rather than descending to the changes alone. At a million
// objects that is four internal pages a commit; the growth is measured in
// `DatasetTreeTests.WhatACommitReads`, and the top-down descent that would make it O(depth) is a
// step of its own, with that measurement to beat.
using System;
using System.Collections.Generic;
using System.IO.Hashing;
using System.Threading;
using System.Threading.Tasks;

namespace Vorticity.Dataset;

/// <summary>One change a commit applies to the tree.</summary>
/// <param name="Key">The entry's key.</param>
/// <param name="Value">The entry's value, or null to remove the key.</param>
/// <param name="Rows">The object's rows; ignored for a removal.</param>
public readonly record struct TreeChange(ReadOnlyMemory<byte> Key, ReadOnlyMemory<byte>? Value, long Rows)
{
    /// <summary>Adds or replaces an entry.</summary>
    /// <param name="key">Its key.</param>
    /// <param name="value">Its value.</param>
    /// <param name="rows">Its rows.</param>
    /// <returns>The change.</returns>
    public static TreeChange Put(ReadOnlyMemory<byte> key, ReadOnlyMemory<byte> value, long rows) =>
        new TreeChange(key, value, rows);

    /// <summary>Removes an entry.</summary>
    /// <param name="key">Its key.</param>
    /// <returns>The change.</returns>
    public static TreeChange Remove(ReadOnlyMemory<byte> key) => new TreeChange(key, null, 0);

    /// <summary>Whether this change removes the key.</summary>
    public bool IsRemoval => Value is null;
}

/// <summary>A tree, named by its root page.</summary>
/// <param name="Root">The top page, or <see cref="PageReference.None"/> for an empty tree.</param>
/// <param name="Depth">The levels; 0 for an empty tree, 1 when the root is a leaf.</param>
/// <param name="Entries">The data objects it holds.</param>
/// <param name="Rows">Their rows, summed.</param>
public sealed record DatasetTree(PageReference Root, int Depth, long Entries, long Rows)
{
    /// <summary>A tree with nothing in it.</summary>
    public static DatasetTree Empty { get; } = new DatasetTree(PageReference.None, 0, 0, 0);

    /// <summary>Whether it holds nothing.</summary>
    public bool IsEmpty => Depth == 0;

    /// <summary>Builds a tree from every entry, in key order (§14's rebuild oracle).</summary>
    /// <param name="entries">The entries, sorted by key and unique.</param>
    /// <param name="rule">The boundary rule.</param>
    /// <param name="sink">Where the pages go.</param>
    /// <returns>The tree.</returns>
    /// <exception cref="ArgumentException">The entries are not sorted, or hold a duplicate key.</exception>
    public static DatasetTree Build(IReadOnlyList<TreeEntry> entries, IBoundaryRule rule, IPageSink sink) =>
        Build(entries, rule, NoSummary.Instance, sink);

    /// <summary>Builds a tree from every entry, in key order (§14's rebuild oracle).</summary>
    /// <param name="entries">The entries, sorted by key and unique.</param>
    /// <param name="rule">The boundary rule.</param>
    /// <param name="fold">What a page's summary is, from what it holds (§4.2).</param>
    /// <param name="sink">Where the pages go.</param>
    /// <returns>The tree.</returns>
    /// <exception cref="ArgumentException">The entries are not sorted, or hold a duplicate key.</exception>
    public static DatasetTree Build(
        IReadOnlyList<TreeEntry> entries, IBoundaryRule rule, ISummaryFold fold, IPageSink sink)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(fold);
        ArgumentNullException.ThrowIfNull(sink);
        if (entries.Count == 0)
        {
            return Empty;
        }

        PageEmitter leaves = new PageEmitter(rule.Fresh(), fold, sink, leaf: true);
        long rows = 0;
        for (int i = 0; i < entries.Count; i++)
        {
            if (i > 0 && TreePage.Compare(entries[i - 1].Key.Span, entries[i].Key.Span) >= 0)
            {
                throw new ArgumentException(
                    $"Entry {i} does not come after entry {i - 1}; a tree is built from sorted, unique keys.",
                    nameof(entries));
            }

            rows += entries[i].Rows;
            leaves.Add(entries[i]);
        }

        leaves.Flush();
        return OverLevels(leaves.Emitted, rule, fold, sink, entries.Count, rows, depth: 1);
    }

    /// <summary>Applies a sorted batch of changes, reusing every page it does not have to rewrite.</summary>
    /// <param name="changes">The changes, sorted by key and unique.</param>
    /// <param name="rule">The boundary rule, the same one the tree was built under.</param>
    /// <param name="source">Where the old pages are read from.</param>
    /// <param name="sink">Where the new pages go.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>The new tree.</returns>
    /// <exception cref="ArgumentException">The changes are not sorted, or hold a duplicate key.</exception>
    public ValueTask<DatasetTree> CommitAsync(
        IReadOnlyList<TreeChange> changes,
        IBoundaryRule rule,
        IPageSource source,
        IPageSink sink,
        CancellationToken cancellationToken) =>
        CommitAsync(changes, rule, NoSummary.Instance, source, sink, cancellationToken);

    /// <summary>Applies a sorted batch of changes, reusing every page it does not have to rewrite.</summary>
    /// <param name="changes">The changes, sorted by key and unique.</param>
    /// <param name="rule">The boundary rule, the same one the tree was built under.</param>
    /// <param name="fold">What a page's summary is, from what it holds (§4.2).</param>
    /// <param name="source">Where the old pages are read from.</param>
    /// <param name="sink">Where the new pages go.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>The new tree.</returns>
    /// <exception cref="ArgumentException">The changes are not sorted, or hold a duplicate key.</exception>
    public async ValueTask<DatasetTree> CommitAsync(
        IReadOnlyList<TreeChange> changes,
        IBoundaryRule rule,
        ISummaryFold fold,
        IPageSource source,
        IPageSink sink,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(changes);
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(fold);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(sink);
        for (int i = 1; i < changes.Count; i++)
        {
            if (TreePage.Compare(changes[i - 1].Key.Span, changes[i].Key.Span) >= 0)
            {
                throw new ArgumentException(
                    $"Change {i} does not come after change {i - 1}; a batch is sorted and unique.", nameof(changes));
            }
        }

        if (changes.Count == 0)
        {
            return this;
        }

        if (IsEmpty)
        {
            List<TreeEntry> fresh = [];
            foreach (TreeChange first in changes)
            {
                if (!first.IsRemoval)
                {
                    fresh.Add(new TreeEntry(first.Key, first.Value!.Value, first.Rows));
                }
            }

            return Build(fresh, rule, fold, sink);
        }

        // THE LEVELS ABOVE THE LEAVES, read once: a level's pages are described by the entries of
        // the level above, so this is what the walk needs to know which leaf pages exist at all.
        List<List<InternalEntry>> descriptors = await DescribeAsync(source, cancellationToken).ConfigureAwait(false);

        // Level 0: the leaves. Untouched pages are reused without being read.
        PageEmitter emitter = new PageEmitter(rule.Fresh(), fold, sink, leaf: true);
        List<InternalEntry> pages = descriptors[0];
        long added = 0;
        int change = 0;
        for (int i = 0; i < pages.Count; i++)
        {
            ReadOnlyMemory<byte> upper = i + 1 < pages.Count ? pages[i + 1].MinKey : default;
            bool last = i + 1 == pages.Count;
            if (!Touches(changes, change, upper, last) && emitter.IsEmpty)
            {
                emitter.Reuse(pages[i]);
                continue;
            }

            IReadOnlyList<TreeEntry> page = TreePage.ReadLeaf(
                await source.ReadPageAsync(pages[i].Child, cancellationToken).ConfigureAwait(false));
            added += Merge(page, changes, ref change, upper, last, emitter);
        }

        // Changes past the last page's range: appended keys, which no old page held.
        while (change < changes.Count)
        {
            if (!changes[change].IsRemoval)
            {
                emitter.Add(new TreeEntry(changes[change].Key, changes[change].Value!.Value, changes[change].Rows));
                added++;
            }

            change++;
        }

        emitter.Flush();
        List<InternalEntry> level = emitter.Emitted;
        if (level.Count == 0)
        {
            return Empty;
        }

        // The levels above, rebuilt over the changed range: an old page whose children are exactly
        // the same references is reused, which is what keeps a commit's writes near `depth`.
        int depth = 1;
        for (int above = 1; above < descriptors.Count && level.Count > 1; above++)
        {
            level = Rewrite(descriptors[above], descriptors[above - 1], level, rule, fold, sink);
            depth++;
        }

        while (level.Count > 1)
        {
            level = Chunk(level, rule, fold, sink);
            depth++;
        }

        // The rows are summed by the emitters and carried by the reused descriptors, so the root's
        // are the tree's. The entries are not summed anywhere, so the merge counted the difference:
        // every change falls inside some page's range, and a page a change falls in is read.
        return new DatasetTree(level[0].Child, depth, Entries + added, level[0].Rows);
    }

    /// <summary>Finds the entry with this key.</summary>
    /// <param name="key">The key.</param>
    /// <param name="source">Where the pages are read from.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>The entry, or null when the tree does not hold that key.</returns>
    public async ValueTask<TreeEntry?> FindAsync(
        ReadOnlyMemory<byte> key, IPageSource source, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (IsEmpty)
        {
            return null;
        }

        PageReference reference = Root;
        for (int level = Depth; level > 1; level--)
        {
            IReadOnlyList<InternalEntry> page = TreePage.ReadInternal(
                await source.ReadPageAsync(reference, cancellationToken).ConfigureAwait(false));
            int child = Descend(page, key.Span);
            if (child < 0)
            {
                return null;
            }

            reference = page[child].Child;
        }

        IReadOnlyList<TreeEntry> leaf = TreePage.ReadLeaf(
            await source.ReadPageAsync(reference, cancellationToken).ConfigureAwait(false));
        foreach (TreeEntry entry in leaf)
        {
            int order = TreePage.Compare(entry.Key.Span, key.Span);
            if (order == 0)
            {
                return entry;
            }

            if (order > 0)
            {
                return null;
            }
        }

        return null;
    }

    /// <summary>Every entry, in key order.</summary>
    /// <param name="source">Where the pages are read from.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>The entries.</returns>
    public async IAsyncEnumerable<TreeEntry> EnumerateAsync(
        IPageSource source,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (PositionedEntry positioned in
            WalkAsync(source, 0, long.MaxValue, null, cancellationToken).ConfigureAwait(false))
        {
            yield return positioned.Entry;
        }
    }

    /// <summary>
    /// The entries a walk keeps: those whose rows meet <c>[from, to)</c>, and those whose ancestors
    /// a predicate could not rule out.
    /// </summary>
    /// <param name="source">Where the pages are read from.</param>
    /// <param name="from">The first row of the dataset to reach, in the tree's own order.</param>
    /// <param name="to">One past the last; <see cref="long.MaxValue"/> for all of them.</param>
    /// <param name="descend">
    /// Whether a subtree is worth reading, from its node's summaries (§4.2); null to read all of
    /// them. A node the predicate refutes costs no read at all, which is the whole point of the
    /// summary: <em>"a predicate that the node's summaries refute skips the whole subtree"</em>.
    /// </param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>The entries, in key order, each with its first row in the dataset.</returns>
    /// <remarks>
    /// TWO SKIPS, ONE WALK, because they are the same walk: a node is not descended into when its
    /// rows fall outside the range or when its summaries refute the predicate, and both decisions
    /// are made from the parent's entry, before the child page is read. The row skip is what §6.6
    /// prices at O(log N) for <c>Rows(a, b)</c> -- "the insertion-order tree, nodes carrying row
    /// sums" -- and with the first-row-position key of §4.1 this IS that tree, since key order and
    /// insertion order are then the same order.
    /// </remarks>
    public async IAsyncEnumerable<PositionedEntry> WalkAsync(
        IPageSource source,
        long from,
        long to,
        Func<InternalEntry, bool>? descend,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegative(from);
        if (IsEmpty || to <= from)
        {
            yield break;
        }

        Stack<(PageReference Reference, int Level, long Row)> stack = new Stack<(PageReference, int, long)>();
        stack.Push((Root, Depth, 0));
        while (stack.Count > 0)
        {
            (PageReference reference, int level, long row) = stack.Pop();
            ReadOnlyMemory<byte> bytes = await source.ReadPageAsync(reference, cancellationToken).ConfigureAwait(false);
            if (level == 1)
            {
                foreach (TreeEntry entry in TreePage.ReadLeaf(bytes))
                {
                    if (row < to && row + entry.Rows > from)
                    {
                        yield return new PositionedEntry(entry, row);
                    }

                    row += entry.Rows;
                }

                continue;
            }

            // Pushed in reverse so that popping walks the children in key order, which means each
            // child's first row is reached by SUBTRACTING from the page's end rather than adding
            // from its start: the same arithmetic, and no array to hold the offsets in.
            IReadOnlyList<InternalEntry> page = TreePage.ReadInternal(bytes);
            long at = row;
            for (int i = 0; i < page.Count; i++)
            {
                at += page[i].Rows;
            }

            for (int i = page.Count - 1; i >= 0; i--)
            {
                long start = at - page[i].Rows;
                if (start < to && at > from && (descend is null || descend(page[i])))
                {
                    stack.Push((page[i].Child, level - 1, start));
                }

                at = start;
            }
        }
    }

    /// <summary>
    /// The tree's identity: a Merkle hash over the pages' CONTENT, with a child's content hash in
    /// place of its reference.
    /// </summary>
    /// <param name="source">Where the pages are read from.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>The hash, or zero for an empty tree.</returns>
    /// <remarks>
    /// §4.1 promises "one root hash" for a key set, and §13.J makes "equality of two datasets: one
    /// root hash" the prolly tree's advantage. A page reference carries PLACEMENT as well as
    /// content (§3: version, offset, length, hash), so two identical trees laid out in two commit
    /// objects differ in their bytes and their root reference. The identity that does not depend on
    /// placement is this one, and it is what the oracles compare.
    /// </remarks>
    public async ValueTask<UInt128> ContentHashAsync(IPageSource source, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        return IsEmpty ? UInt128.Zero : await HashAsync(Root, Depth, source, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<UInt128> HashAsync(
        PageReference reference, int level, IPageSource source, CancellationToken cancellationToken)
    {
        ReadOnlyMemory<byte> bytes = await source.ReadPageAsync(reference, cancellationToken).ConfigureAwait(false);
        if (level == 1)
        {
            return XxHash128.HashToUInt128(bytes.Span);
        }

        IReadOnlyList<InternalEntry> page = TreePage.ReadInternal(bytes);
        XxHash128 hash = new XxHash128();
        byte[] scratch = new byte[16];
        foreach (InternalEntry entry in page)
        {
            hash.Append(entry.MinKey.Span);
            hash.Append(entry.MaxKey.Span);
            System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(scratch, entry.Rows);
            hash.Append(scratch.AsSpan(0, 8));
            UInt128 child = await HashAsync(entry.Child, level - 1, source, cancellationToken).ConfigureAwait(false);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(scratch, (ulong)(child >> 64));
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(scratch.AsSpan(8), (ulong)child);
            hash.Append(scratch);
        }

        return hash.GetCurrentHashAsUInt128();
    }

    /// <summary>
    /// The descriptors of every level's pages: index 0 is the leaves, index <c>Depth − 1</c> the root.
    /// </summary>
    /// <param name="source">Where the pages are read from.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <remarks>
    /// A level's pages are described by the entries stored one level above, so this reads every
    /// INTERNAL page and no leaf. The root has no level above it, so it gets a descriptor of its
    /// own, whose key range is never consulted: a single page is always the last of its level.
    /// </remarks>
    private async ValueTask<List<List<InternalEntry>>> DescribeAsync(
        IPageSource source, CancellationToken cancellationToken)
    {
        List<List<InternalEntry>>? descriptors = new List<List<InternalEntry>>(Depth);
        for (int i = 0; i < Depth; i++)
        {
            descriptors.Add([]);
        }

        descriptors[Depth - 1].Add(new InternalEntry(default, default, Rows, Root));
        for (int level = Depth - 1; level > 0; level--)
        {
            foreach (InternalEntry entry in descriptors[level])
            {
                descriptors[level - 1].AddRange(TreePage.ReadInternal(
                    await source.ReadPageAsync(entry.Child, cancellationToken).ConfigureAwait(false)));
            }
        }

        return descriptors;
    }

    /// <summary>Whether any change falls in the page's range.</summary>
    /// <param name="changes">The batch.</param>
    /// <param name="from">The first change not yet consumed.</param>
    /// <param name="upper">The next page's first key, or empty when this is the last page.</param>
    /// <param name="last">Whether this is the last page of its level.</param>
    private static bool Touches(
        IReadOnlyList<TreeChange> changes, int from, ReadOnlyMemory<byte> upper, bool last) =>
        from < changes.Count && (last || TreePage.Compare(changes[from].Key.Span, upper.Span) < 0);

    /// <summary>Feeds one old page's entries and the changes that fall in its range to the emitter.</summary>
    /// <returns>How many entries the page gained: inserts minus removals.</returns>
    private static long Merge(
        IReadOnlyList<TreeEntry> page,
        IReadOnlyList<TreeChange> changes,
        ref int change,
        ReadOnlyMemory<byte> upper,
        bool last,
        PageEmitter emitter)
    {
        long added = 0;
        int i = 0;
        while (i < page.Count)
        {
            if (change < changes.Count
                && (last || TreePage.Compare(changes[change].Key.Span, upper.Span) < 0))
            {
                int order = TreePage.Compare(changes[change].Key.Span, page[i].Key.Span);
                if (order < 0)
                {
                    // A key no entry holds: an insert, or a removal of something that is not there.
                    if (!changes[change].IsRemoval)
                    {
                        emitter.Add(new TreeEntry(
                            changes[change].Key, changes[change].Value!.Value, changes[change].Rows));
                        added++;
                    }

                    change++;
                    continue;
                }

                if (order == 0)
                {
                    // The key is there: a replacement keeps the count, a removal drops the entry.
                    if (!changes[change].IsRemoval)
                    {
                        emitter.Add(new TreeEntry(
                            changes[change].Key, changes[change].Value!.Value, changes[change].Rows));
                    }
                    else
                    {
                        added--;
                    }

                    change++;
                    i++;
                    continue;
                }
            }

            emitter.Add(page[i]);
            i++;
        }

        // Changes after the page's last entry but still inside its range: inserts at its tail.
        while (change < changes.Count && (last || TreePage.Compare(changes[change].Key.Span, upper.Span) < 0))
        {
            if (!changes[change].IsRemoval)
            {
                emitter.Add(new TreeEntry(changes[change].Key, changes[change].Value!.Value, changes[change].Rows));
                added++;
            }

            change++;
        }

        return added;
    }

    /// <summary>
    /// Rewrites one level above the leaves: an old page whose children are exactly the same
    /// references is reused; everything else goes through the chunker.
    /// </summary>
    /// <param name="oldPages">The descriptors of this level's old pages.</param>
    /// <param name="oldChildren">The descriptors of the level below's old pages, in order.</param>
    /// <param name="children">The level below's new descriptors, in order.</param>
    /// <param name="rule">The boundary rule.</param>
    /// <param name="fold">What a page's summary is.</param>
    /// <param name="sink">Where the pages go.</param>
    private static List<InternalEntry> Rewrite(
        List<InternalEntry> oldPages,
        List<InternalEntry> oldChildren,
        List<InternalEntry> children,
        IBoundaryRule rule,
        ISummaryFold fold,
        IPageSink sink)
    {
        PageEmitter emitter = new PageEmitter(rule.Fresh(), fold, sink, leaf: false);
        int cursor = 0;
        int oldCursor = 0;
        for (int i = 0; i < oldPages.Count; i++)
        {
            int count = CountChildren(oldChildren, oldCursor, oldPages, i);
            bool same = emitter.IsEmpty && cursor + count <= children.Count;
            for (int k = 0; same && k < count; k++)
            {
                same = children[cursor + k].Child == oldChildren[oldCursor + k].Child;
            }

            if (same && count > 0)
            {
                emitter.Reuse(oldPages[i]);
                cursor += count;
                oldCursor += count;
                continue;
            }

            // Not reusable: feed every new child whose key belongs to this old page's range.
            ReadOnlyMemory<byte> upper = i + 1 < oldPages.Count ? oldPages[i + 1].MinKey : default;
            bool last = i + 1 == oldPages.Count;
            while (cursor < children.Count
                && (last || TreePage.Compare(children[cursor].MinKey.Span, upper.Span) < 0))
            {
                emitter.Add(children[cursor++]);
            }

            oldCursor += count;
        }

        while (cursor < children.Count)
        {
            emitter.Add(children[cursor++]);
        }

        emitter.Flush();
        return emitter.Emitted;
    }

    /// <summary>How many of the level below's old pages belong to old page <paramref name="index"/>.</summary>
    private static int CountChildren(
        List<InternalEntry> oldChildren, int from, List<InternalEntry> oldPages, int index)
    {
        if (index + 1 == oldPages.Count)
        {
            return oldChildren.Count - from;
        }

        ReadOnlyMemory<byte> upper = oldPages[index + 1].MinKey;
        int count = 0;
        while (from + count < oldChildren.Count
            && TreePage.Compare(oldChildren[from + count].MinKey.Span, upper.Span) < 0)
        {
            count++;
        }

        return count;
    }

    /// <summary>Chunks a level's entries into pages.</summary>
    private static List<InternalEntry> Chunk(
        List<InternalEntry> entries, IBoundaryRule rule, ISummaryFold fold, IPageSink sink)
    {
        PageEmitter emitter = new PageEmitter(rule.Fresh(), fold, sink, leaf: false);
        foreach (InternalEntry entry in entries)
        {
            emitter.Add(entry);
        }

        emitter.Flush();
        return emitter.Emitted;
    }

    /// <summary>Builds the levels above a set of leaf pages until one page is left.</summary>
    private static DatasetTree OverLevels(
        List<InternalEntry> level,
        IBoundaryRule rule,
        ISummaryFold fold,
        IPageSink sink,
        long entries,
        long rows,
        int depth)
    {
        while (level.Count > 1)
        {
            level = Chunk(level, rule, fold, sink);
            depth++;
        }

        return new DatasetTree(level[0].Child, depth, entries, rows);
    }

    /// <summary>The child whose range holds the key, by the usual descent.</summary>
    private static int Descend(IReadOnlyList<InternalEntry> page, ReadOnlySpan<byte> key)
    {
        int child = -1;
        for (int i = 0; i < page.Count; i++)
        {
            if (TreePage.Compare(page[i].MinKey.Span, key) <= 0)
            {
                child = i;
            }
            else
            {
                break;
            }
        }

        return child < 0 ? (page.Count > 0 ? 0 : -1) : child;
    }

    /// <summary>Accumulates entries, cuts where the rule says, and writes the pages.</summary>
    private sealed class PageEmitter
    {
        private readonly IBoundaryRule _rule;
        private readonly ISummaryFold _fold;
        private readonly IPageSink _sink;
        private readonly bool _leaf;
        private readonly List<TreeEntry> _leaves = [];
        private readonly List<InternalEntry> _internals = [];
        private readonly List<ReadOnlyMemory<byte>> _summaries = [];

        internal PageEmitter(IBoundaryRule rule, ISummaryFold fold, IPageSink sink, bool leaf)
        {
            _rule = rule;
            _fold = fold;
            _sink = sink;
            _leaf = leaf;
            _rule.Reset();
        }

        /// <summary>The descriptors of the pages emitted so far, in order.</summary>
        internal List<InternalEntry> Emitted { get; } = [];

        /// <summary>Whether nothing is accumulated: the emitter sits on a boundary.</summary>
        internal bool IsEmpty => _leaves.Count == 0 && _internals.Count == 0;

        internal void Add(TreeEntry entry)
        {
            _leaves.Add(entry);
            _summaries.Add(_fold.OfLeaf(entry));
            if (_rule.IsBoundary(entry.Key.Span, entry.Bytes))
            {
                Cut();
            }
        }

        internal void Add(InternalEntry entry)
        {
            _internals.Add(entry);
            _summaries.Add(entry.Summary);
            if (_rule.IsBoundary(entry.MinKey.Span, entry.Bytes))
            {
                Cut();
            }
        }

        /// <summary>Emits an old page unchanged, by reference. Only legal on a boundary.</summary>
        /// <param name="descriptor">The old page's descriptor, kept as it is.</param>
        internal void Reuse(InternalEntry descriptor)
        {
            if (!IsEmpty)
            {
                throw new InvalidOperationException("A page is reused only between pages.");
            }

            Emitted.Add(descriptor);
        }

        internal void Flush()
        {
            if (!IsEmpty)
            {
                Cut();
            }
        }

        private void Cut()
        {
            ReadOnlyMemory<byte> summary = _fold.Union(_summaries);
            if (_leaf)
            {
                long rows = 0;
                foreach (TreeEntry entry in _leaves)
                {
                    rows += entry.Rows;
                }

                PageReference reference = _sink.WritePage(TreePage.WriteLeaf(_leaves));
                Emitted.Add(new InternalEntry(_leaves[0].Key, _leaves[^1].Key, rows, reference, summary));
                _leaves.Clear();
            }
            else
            {
                long rows = 0;
                foreach (InternalEntry entry in _internals)
                {
                    rows += entry.Rows;
                }

                PageReference reference = _sink.WritePage(TreePage.WriteInternal(_internals));
                Emitted.Add(new InternalEntry(
                    _internals[0].MinKey, _internals[^1].MaxKey, rows, reference, summary));
                _internals.Clear();
            }

            _summaries.Clear();
            _rule.Reset();
        }
    }
}
