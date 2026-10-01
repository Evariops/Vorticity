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
internal readonly record struct TreeChange(ReadOnlyMemory<byte> Key, ReadOnlyMemory<byte>? Value, long Rows)
{
    /// <summary>Adds or replaces an entry.</summary>
    public static TreeChange Put(ReadOnlyMemory<byte> key, ReadOnlyMemory<byte> value, long rows) =>
        new TreeChange(key, value, rows);

    /// <summary>Removes an entry.</summary>
    public static TreeChange Remove(ReadOnlyMemory<byte> key) => new TreeChange(key, null, 0);

    /// <summary>Whether this change removes the key.</summary>
    public bool IsRemoval => Value is null;
}

/// <summary>A tree, named by its root page.</summary>
/// <param name="Root">The top page, or <see cref="PageReference.None"/> for an empty tree.</param>
/// <param name="Depth">The levels; 0 for an empty tree, 1 when the root is a leaf.</param>
/// <param name="Entries">The data objects it holds.</param>
/// <param name="Rows">Their rows, summed.</param>
internal sealed record DatasetTree(PageReference Root, int Depth, long Entries, long Rows)
{
    /// <summary>A tree with nothing in it.</summary>
    public static DatasetTree Empty { get; } = new DatasetTree(PageReference.None, 0, 0, 0);

    /// <summary>Whether it holds nothing.</summary>
    public bool IsEmpty => Depth == 0;

    /// <summary>
    /// How many pages a walk reads ahead of the one it is on: the round trips a walk over siblings
    /// saves, against the pages an early stop may have read for nothing.
    /// </summary>
    public const int PrefetchWindow = 8;

    /// <summary>Builds a tree from every entry, which must be sorted by key and unique.</summary>
    public static DatasetTree Build(IReadOnlyList<TreeEntry> entries, IBoundaryRule rule, IPageSink sink) =>
        Build(entries, rule, NoSummary.Instance, sink);

    /// <summary>Builds a tree from every entry, which must be sorted by key and unique.</summary>
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

    /// <summary>
    /// Applies a batch of changes, sorted by key and unique, reusing every page it does not have to
    /// rewrite. The rule must be the one the tree was built under.
    /// </summary>
    public ValueTask<DatasetTree> CommitAsync(
        IReadOnlyList<TreeChange> changes,
        IBoundaryRule rule,
        IPageSource source,
        IPageSink sink,
        CancellationToken cancellationToken) =>
        CommitAsync(changes, rule, NoSummary.Instance, source, sink, cancellationToken);

    /// <summary>
    /// Applies a batch of changes, sorted by key and unique, reusing every page it does not have to
    /// rewrite. The rule must be the one the tree was built under.
    /// </summary>
    /// <remarks>
    /// Only the pages on the paths of the changes are read, and past them only as far as the pages
    /// cut anew run on before a cut falls where an old page began: O(c × D) pages for c changes,
    /// whatever the tree holds. A subtree the walk leaves unread is reused whole at every level,
    /// since nothing under it changed and the cutting reaches it between pages.
    /// </remarks>
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

        // Level 0: the leaves. An old page began at a boundary and the rule resets at every
        // boundary, so an untouched page, or an untouched subtree, can be reused by reference
        // exactly when the emitter sits between pages; that condition is what makes re-chunking
        // converge back onto old cuts.
        Rechunk tree = new Rechunk(this, source, cancellationToken);
        PageEmitter emitter = new PageEmitter(rule.Fresh(), fold, sink, leaf: true, reusing: true);
        long added = 0;
        int change = 0;
        tree.Restart();
        while (tree.TryNext(out Node node))
        {
            bool last = !tree.TryUpper(out ReadOnlyMemory<byte> upper);
            if (!Touches(changes, change, upper, last) && emitter.IsEmpty)
            {
                emitter.Reuse(node);
                continue;
            }

            if (node.Height > 1)
            {
                await tree.EnterAsync(node).ConfigureAwait(false);
                continue;
            }

            tree.Rewritten(node);
            IReadOnlyList<TreeEntry> page = TreePage.ReadLeaf(
                await source.ReadPageAsync(node.Descriptor.Child, cancellationToken).ConfigureAwait(false));
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
        if (emitter.Items!.Count == 0)
        {
            return Empty;
        }

        // An old page none of whose children changed, reached between pages, is reused, which is
        // what keeps a commit's writes near `depth`. A level built at the old root's height is all
        // new pages, since the root holds a rewritten page.
        int depth = 1;
        InternalEntry? single = await tree.SinglePageAsync(emitter, depth).ConfigureAwait(false);
        while (single is null && depth < Depth)
        {
            depth++;
            emitter = await tree.RewriteAsync(emitter, depth, rule, fold, sink).ConfigureAwait(false);
            single = await tree.SinglePageAsync(emitter, depth).ConfigureAwait(false);
        }

        List<InternalEntry> level = single is { } top ? [top] : emitter.Emitted;
        while (level.Count > 1)
        {
            level = Chunk(level, rule, fold, sink);
            depth++;
        }

        // The entries are not summed anywhere, so the merge counted the difference: every change
        // falls inside some page's range, and a page a change falls in is read.
        return new DatasetTree(level[0].Child, depth, Entries + added, level[0].Rows);
    }

    /// <summary>Finds the entry with this key, or null when the tree does not hold it.</summary>
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

    /// <summary>
    /// The first entry whose key is past <paramref name="key"/>, or with <paramref name="wrap"/> the
    /// first of all when none is; null when the tree is empty, or nothing is past the key and the
    /// search does not wrap. One path down: a page's entry bounds the keys of its subtree, so the
    /// first child whose largest key is past this one holds the entry.
    /// </summary>
    public async ValueTask<TreeEntry?> NextAsync(
        ReadOnlyMemory<byte> key, bool wrap, IPageSource source, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (IsEmpty)
        {
            return null;
        }

        PageReference reference = Root;
        bool wrapped = false;
        for (int level = Depth; level > 1; level--)
        {
            IReadOnlyList<InternalEntry> page = TreePage.ReadInternal(
                await source.ReadPageAsync(reference, cancellationToken).ConfigureAwait(false));
            int child = 0;
            while (!wrapped && child < page.Count && TreePage.Compare(page[child].MaxKey.Span, key.Span) <= 0)
            {
                child++;
            }

            if (child == page.Count)
            {
                // Only the top page can hold no child past the key: one below was entered for
                // holding a larger one.
                if (!wrap)
                {
                    return null;
                }

                wrapped = true;
                child = 0;
            }

            reference = page[child].Child;
        }

        IReadOnlyList<TreeEntry> leaf = TreePage.ReadLeaf(
            await source.ReadPageAsync(reference, cancellationToken).ConfigureAwait(false));
        if (!wrapped)
        {
            foreach (TreeEntry entry in leaf)
            {
                if (TreePage.Compare(entry.Key.Span, key.Span) > 0)
                {
                    return entry;
                }
            }
        }

        return wrap ? leaf[0] : null;
    }

    /// <summary>Every entry, in key order.</summary>
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
    /// The entries whose rows meet <c>[from, to)</c> and whose ancestors <paramref name="descend"/>
    /// could not rule out, in key order, each with its first row in the dataset. Both skips are
    /// decided from the parent's entry, before the child page is read, so a node ruled out costs no
    /// read at all. A null <paramref name="descend"/> reads every subtree.
    /// </summary>
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

        // The stack's top is the walk's future in order, so the pages it names are read a window
        // ahead: one dependent round trip per window rather than one per page. The window is
        // bounded so that a walk which stops early has read at most a window more than it used.
        List<(PageReference Reference, int Level, long Row, Task<ReadOnlyMemory<byte>>? Read)> stack = [(Root, Depth, 0, null)];
        try
        {
            while (stack.Count > 0)
            {
                for (int ahead = stack.Count - 1; ahead >= Math.Max(0, stack.Count - PrefetchWindow); ahead--)
                {
                    if (stack[ahead].Read is null)
                    {
                        stack[ahead] = stack[ahead] with
                        {
                            Read = source.ReadPageAsync(stack[ahead].Reference, cancellationToken).AsTask(),
                        };
                    }
                }

                (_, int level, long row, Task<ReadOnlyMemory<byte>>? read) = stack[^1];
                stack.RemoveAt(stack.Count - 1);
                ReadOnlyMemory<byte> bytes = await read!.ConfigureAwait(false);
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

                // Pushed in reverse so that popping walks the children in key order; each child's
                // first row is then reached by subtracting from the page's end, which needs no
                // array to hold the offsets in.
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
                        stack.Add((page[i].Child, level - 1, start, null));
                    }

                    at = start;
                }
            }
        }
        finally
        {
            // A walk stopped early leaves reads in flight: each is observed, so that one that fails
            // after nobody wants it is not an unobserved exception.
            foreach ((_, _, _, Task<ReadOnlyMemory<byte>>? pending) in stack)
            {
                _ = pending?.ContinueWith(
                    static read => read.Exception, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }
    }

    /// <summary>
    /// The tree's identity: a Merkle hash over the pages' content, with a child's content hash in
    /// place of its reference, or zero for an empty tree. A page reference carries placement as
    /// well as content, so this is the only identity two identical trees laid out in two commit
    /// objects share.
    /// </summary>
    public async ValueTask<UInt128> ContentHashAsync(IPageSource source, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        return IsEmpty ? UInt128.Zero : await HashAsync(Root, Depth, source, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The same tree with every page <paramref name="move"/> selects written again into
    /// <paramref name="sink"/>, and every page above one of them with it, plus how many pages were
    /// written.
    /// </summary>
    /// <remarks>
    /// Only the placement changes, so <see cref="ContentHashAsync"/> is the same before and after.
    /// A moved page's parent must move too, because the reference it holds names the old placement.
    /// Every page is read to find the ones to move, since a reference says where a page lies but
    /// not where its children do.
    /// </remarks>
    public async ValueTask<(DatasetTree Tree, int Moved)> RelocateAsync(
        Func<PageReference, bool> move, IPageSource source, IPageSink sink, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(move);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(sink);
        if (IsEmpty)
        {
            return (this, 0);
        }

        int moved = 0;
        PageReference root = await RelocateAsync(Root, Depth).ConfigureAwait(false);
        return (moved == 0 ? this : this with { Root = root }, moved);

        async ValueTask<PageReference> RelocateAsync(PageReference reference, int level)
        {
            ReadOnlyMemory<byte> bytes = await source.ReadPageAsync(reference, cancellationToken).ConfigureAwait(false);
            if (level == 1)
            {
                return move(reference) ? Write(bytes.Span) : reference;
            }

            IReadOnlyList<InternalEntry> page = TreePage.ReadInternal(bytes);
            InternalEntry[]? rewritten = null;
            for (int i = 0; i < page.Count; i++)
            {
                PageReference child = await RelocateAsync(page[i].Child, level - 1).ConfigureAwait(false);
                if (child != page[i].Child)
                {
                    rewritten ??= [.. page];
                    rewritten[i] = page[i] with { Child = child };
                }
            }

            return rewritten is not null ? Write(TreePage.WriteInternal(rewritten))
                : move(reference) ? Write(bytes.Span)
                : reference;
        }

        PageReference Write(ReadOnlySpan<byte> page)
        {
            moved++;
            return sink.WritePage(page);
        }
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

    /// <summary>Whether any change from <c>from</c> on falls below <c>upper</c>, the next page's first key.</summary>
    private static bool Touches(
        IReadOnlyList<TreeChange> changes, int from, ReadOnlyMemory<byte> upper, bool last) =>
        from < changes.Count && (last || TreePage.Compare(changes[from].Key.Span, upper.Span) < 0);

    /// <summary>
    /// Feeds one old page's entries and the changes in its range to the emitter, returning how many
    /// entries the page gained: inserts minus removals.
    /// </summary>
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

    /// <summary>
    /// The child whose range holds the key. A linear walk and not a binary search, because the
    /// boundary rule keeps an internal page to a few dozen entries.
    /// </summary>
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

        internal PageEmitter(IBoundaryRule rule, ISummaryFold fold, IPageSink sink, bool leaf, bool reusing = false)
        {
            _rule = rule;
            _fold = fold;
            _sink = sink;
            _leaf = leaf;
            Items = reusing ? [] : null;
            _rule.Reset();
        }

        /// <summary>The pages it cut, in order.</summary>
        internal List<InternalEntry> Emitted { get; } = [];

        /// <summary>
        /// For an emitter that reuses old pages, the whole level in order: the pages it cut, by
        /// their place in <see cref="Emitted"/>, and the old pages and subtrees it reused.
        /// </summary>
        internal List<Item>? Items { get; }

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

        /// <summary>
        /// Emits an old page, or every page of an old subtree, unchanged, by reference. Only legal
        /// on a boundary.
        /// </summary>
        internal void Reuse(Node node)
        {
            if (!IsEmpty)
            {
                throw new InvalidOperationException("A page is reused only between pages.");
            }

            Items!.Add(new Item(node.Page, node.Index));
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
            ReadOnlyMemory<byte> summary = _fold.Union(_summaries, _leaf);
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

            Items?.Add(new Item(null, Emitted.Count - 1));
            _summaries.Clear();
            _rule.Reset();
        }
    }

    /// <summary>An old internal page a commit read: the entries it holds, and whether it holds a rewritten page.</summary>
    private sealed class Opened
    {
        internal Opened(int height, Opened? parent, IReadOnlyList<InternalEntry> entries)
        {
            Height = height;
            Parent = parent;
            Entries = entries;
        }

        /// <summary>Its height: 2 for a page whose entries are leaves.</summary>
        internal int Height { get; }

        /// <summary>The page above it, or null for the page that holds the root alone.</summary>
        internal Opened? Parent { get; }

        internal IReadOnlyList<InternalEntry> Entries { get; }

        /// <summary>Whether a page under it was rewritten, which forbids reusing it.</summary>
        internal bool Dirty { get; set; }
    }

    /// <summary>An old page, by its place among the entries of the page above it.</summary>
    private readonly record struct Node(Opened Page, int Index)
    {
        internal InternalEntry Descriptor => Page.Entries[Index];

        /// <summary>Its height: 1 for a leaf page.</summary>
        internal int Height => Page.Height - 1;
    }

    /// <summary>
    /// A page of a level a commit builds: a new one, by its place among the pages its emitter cut,
    /// when <paramref name="Page"/> is null; else an old page or subtree reused whole, by its place
    /// in the page above it.
    /// </summary>
    private readonly record struct Item(Opened? Page, int Index);

    /// <summary>A page being walked, and the next of its entries.</summary>
    private struct Frame(Opened page, int next)
    {
        internal readonly Opened Page = page;
        internal int Next = next;
    }

    /// <summary>
    /// The old tree as a commit reads it, one level at a time: a walk in key order that opens a page
    /// only when it must, the pages opened so far, and which of them hold a rewritten page.
    /// </summary>
    private sealed class Rechunk
    {
        private readonly IPageSource _source;
        private readonly CancellationToken _cancellationToken;
        private readonly Opened _top;
        private readonly Dictionary<Node, Opened> _opened = [];
        private readonly Frame[] _walk;
        private readonly Frame[] _below;
        private int _walking;
        private int _belowOpen;
        private PageEmitter? _level;
        private int _next;

        internal Rechunk(DatasetTree tree, IPageSource source, CancellationToken cancellationToken)
        {
            _source = source;
            _cancellationToken = cancellationToken;

            // The root, as the one entry of a page above it, so that every old page is a node.
            _top = new Opened(tree.Depth + 1, null, [new InternalEntry(default, default, tree.Rows, tree.Root)]);
            _walk = new Frame[tree.Depth + 1];
            _below = new Frame[tree.Depth + 1];
        }

        /// <summary>Starts a walk from the root.</summary>
        internal void Restart()
        {
            _walk[0] = new Frame(_top, 0);
            _walking = 1;
        }

        /// <summary>The next page or subtree of the walk, in key order.</summary>
        internal bool TryNext(out Node node)
        {
            while (_walking > 0)
            {
                ref Frame frame = ref _walk[_walking - 1];
                if (frame.Next < frame.Page.Entries.Count)
                {
                    node = new Node(frame.Page, frame.Next++);
                    return true;
                }

                _walking--;
            }

            node = default;
            return false;
        }

        /// <summary>The first key of what the walk reaches next, or false when it has reached the end.</summary>
        internal bool TryUpper(out ReadOnlyMemory<byte> upper)
        {
            for (int i = _walking - 1; i >= 0; i--)
            {
                Frame frame = _walk[i];
                if (frame.Next < frame.Page.Entries.Count)
                {
                    upper = frame.Page.Entries[frame.Next].MinKey;
                    return true;
                }
            }

            upper = default;
            return false;
        }

        /// <summary>Opens the node the walk just reached, which it then walks into.</summary>
        internal async ValueTask EnterAsync(Node node)
        {
            Opened page = await OpenAsync(node).ConfigureAwait(false);
            _walk[_walking++] = new Frame(page, 0);
        }

        /// <summary>Marks every page above a rewritten one, none of which can be reused.</summary>
        internal void Rewritten(Node node)
        {
            for (Opened? page = node.Page; page is { Dirty: false }; page = page.Parent)
            {
                page.Dirty = true;
            }
        }

        /// <summary>
        /// Builds the pages of <paramref name="height"/> over the level <paramref name="below"/>
        /// built. An old page is reused when the emitter sits between pages and no page under it was
        /// rewritten: its children are then the old ones, in the same places. Any other page of the
        /// level is cut anew from what the level below holds in its range.
        /// </summary>
        internal async ValueTask<PageEmitter> RewriteAsync(
            PageEmitter below, int height, IBoundaryRule rule, ISummaryFold fold, IPageSink sink)
        {
            PageEmitter emitter = new PageEmitter(rule.Fresh(), fold, sink, leaf: false, reusing: true);
            _level = below;
            _next = 0;
            _belowOpen = 0;
            Restart();
            while (TryNext(out Node node))
            {
                if (emitter.IsEmpty && !IsDirty(node))
                {
                    TakeWhole(node);
                    emitter.Reuse(node);
                    continue;
                }

                if (node.Height > height)
                {
                    bool whole = !IsDirty(node);
                    Opened page = await OpenAsync(node).ConfigureAwait(false);
                    if (whole)
                    {
                        // The level below reused it whole: what follows there is what it holds.
                        TakeWhole(node);
                        _below[_belowOpen++] = new Frame(page, 0);
                    }

                    _walk[_walking++] = new Frame(page, 0);
                    continue;
                }

                Rewritten(node);
                bool last = !TryUpper(out ReadOnlyMemory<byte> upper);
                while (await TakeBelowAsync(height, upper, last).ConfigureAwait(false) is { } child)
                {
                    emitter.Add(child);
                }
            }

            while (await TakeBelowAsync(height, default, last: true).ConfigureAwait(false) is { } rest)
            {
                emitter.Add(rest);
            }

            emitter.Flush();
            return emitter;
        }

        /// <summary>
        /// The one page of <paramref name="height"/> a level holds, or null when it holds more. A
        /// subtree reused whole is one page only when each page down to that height holds one entry.
        /// </summary>
        internal async ValueTask<InternalEntry?> SinglePageAsync(PageEmitter level, int height)
        {
            if (level.Items!.Count != 1)
            {
                return null;
            }

            Item item = level.Items[0];
            if (item.Page is null)
            {
                return level.Emitted[item.Index];
            }

            Node node = new Node(item.Page, item.Index);
            while (node.Height > height)
            {
                Opened page = await OpenAsync(node).ConfigureAwait(false);
                if (page.Entries.Count != 1)
                {
                    return null;
                }

                node = new Node(page, 0);
            }

            return node.Descriptor;
        }

        private bool IsDirty(Node node) => _opened.TryGetValue(node, out Opened? page) && page.Dirty;

        private async ValueTask<Opened> OpenAsync(Node node)
        {
            if (!_opened.TryGetValue(node, out Opened? page))
            {
                page = new Opened(node.Height, node.Page, TreePage.ReadInternal(
                    await _source.ReadPageAsync(node.Descriptor.Child, _cancellationToken).ConfigureAwait(false)));
                _opened.Add(node, page);
            }

            return page;
        }

        /// <summary>
        /// The next entry of the level below whose first key is under <paramref name="upper"/>, or
        /// null. A subtree that level reused whole is opened into the entries it holds at that level.
        /// </summary>
        private async ValueTask<InternalEntry?> TakeBelowAsync(int height, ReadOnlyMemory<byte> upper, bool last)
        {
            while (TryPeek(out InternalEntry descriptor, out Opened? page, out int index))
            {
                if (!last && TreePage.Compare(descriptor.MinKey.Span, upper.Span) >= 0)
                {
                    return null;
                }

                Take();
                if (page is not null && page.Height - 1 >= height)
                {
                    _below[_belowOpen++] = new Frame(await OpenAsync(new Node(page, index)).ConfigureAwait(false), 0);
                    continue;
                }

                return descriptor;
            }

            return null;
        }

        /// <summary>Takes the next entry of the level below, which is the old node reused whole.</summary>
        private void TakeWhole(Node node)
        {
            if (!IsNext(node))
            {
                throw new InvalidOperationException("The level below does not hold a page reused whole where the walk reached it.");
            }

            Take();
        }

        private bool IsNext(Node node) =>
            TryPeek(out _, out Opened? page, out int index) && ReferenceEquals(page, node.Page) && index == node.Index;

        private bool TryPeek(out InternalEntry descriptor, out Opened? page, out int index)
        {
            while (_belowOpen > 0)
            {
                Frame frame = _below[_belowOpen - 1];
                if (frame.Next < frame.Page.Entries.Count)
                {
                    page = frame.Page;
                    index = frame.Next;
                    descriptor = page.Entries[index];
                    return true;
                }

                _belowOpen--;
            }

            if (_next < _level!.Items!.Count)
            {
                Item item = _level.Items[_next];
                page = item.Page;
                index = item.Index;
                descriptor = page is null ? _level.Emitted[index] : page.Entries[index];
                return true;
            }

            descriptor = default;
            page = null;
            index = 0;
            return false;
        }

        /// <summary>Moves past the entry <see cref="TryPeek"/> returned.</summary>
        private void Take()
        {
            if (_belowOpen > 0)
            {
                _below[_belowOpen - 1].Next++;
            }
            else
            {
                _next++;
            }
        }
    }
}
