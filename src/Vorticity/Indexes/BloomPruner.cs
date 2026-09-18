// The read side of the split-block Bloom filters (docs/10-indexes.md §5.1, docs/13-dataset.md §6.2):
// a pruner in the scan's block-mask chain (docs/11-write-strategy.md §6.1), after the zone maps.
//
// ONLY EQUALITY PROVES ANYTHING. `x = v` kills a block whose filter does not hold v; `x IN (...)`
// one that holds none of them; an AND kills what any conjunct kills, an OR what every arm kills.
// Nothing else -- `!=`, an ordering, a NOT -- claims anything; a string match claims through the
// trigram filters. A LIST'S FILTER HOLDS ITS ELEMENTS (10 §5.1, step 28b): `list_contains(x, v)`
// kills a block whose filter does not hold v, and an equality on a list column claims nothing.
//
// THE LITERAL IS HASHED AS THE COLUMN STORES IT, and only when that is exact. The kernels compare in
// three domains -- i64, u64, f64 -- so `x = 5` on an i32 column is the four bytes of 5, and `x = 5.0`
// on an f32 column is the four bytes of 5.0f. A literal that does not convert exactly makes no
// claim, rather than a proof built on a rounding: past 2^53 several integers share a double, and
// hashing one of them would lose the others. A float zero asks for BOTH zeros, since the filter
// stores bit patterns and the scan's equality is IEEE; a NaN asks for nothing.
//
// A PROBE DESCENDS EACH TREE A LEVEL AT A TIME (13 §6.2). Every run's root first, in one read; then,
// level by level, the children of the nodes that still cover a live block -- a node's children are
// one region, and a level's regions one coalesced read. A node whose filter proves the predicate
// false kills its blocks, so its children are never read; a node without a filter proves nothing,
// and the probe goes through it. A value present in one block costs 1 + 16 × depth filters, depth
// being log16 of the blocks, for as long as the nodes fit their ceiling.
//
// WHAT DOES NOT CHECK OUT CLAIMS NOTHING: a region whose bytes are not its checksum's, a node that is
// not what its parent says, words that do not decode. Its blocks stay live, and nothing beneath it
// is read: a lying index may cost pruning, never rows.
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Compute;
using Vorticity.Expressions;
using Vorticity.File;
using Vorticity.IO;
using Vorticity.Serialization.Schemas;
using Vorticity.Types;

namespace Vorticity.Indexes;

/// <summary>Kills the blocks a column's Bloom filters prove cannot hold an equality's value.</summary>
internal sealed class BloomPruner
{
    private readonly VortexExpr _filter;
    private readonly Dictionary<string, Column> _columns;

    private BloomPruner(VortexExpr filter, Dictionary<string, Column> columns)
    {
        _filter = filter;
        _columns = columns;
    }

    /// <summary>What consulting the filters cost: segments and bytes read.</summary>
    internal int Segments { get; private set; }

    /// <summary>The bytes of those segments.</summary>
    internal long Bytes { get; private set; }

    /// <summary>
    /// The pruner for <paramref name="filter"/>, or <see langword="null"/> when no column it tests
    /// for equality carries a usable Bloom entry.
    /// </summary>
    /// <param name="file">The open file.</param>
    /// <param name="filter">The scan's predicate.</param>
    /// <param name="blockRows">The scan's block length; an entry at another length is ignored.</param>
    /// <param name="cancellationToken">Cancels the directory read.</param>
    internal static async ValueTask<BloomPruner?> BuildAsync(
        VortexFile file, VortexExpr filter, long blockRows, CancellationToken cancellationToken)
    {
        // A FILE WITHOUT A DIRECTORY PAYS NOTHING, not even the collectors: the read-path
        // allocation ceilings hold a filtered scan to the byte, and every file written before
        // step 12 is such a file.
        if (!file.HasIndexDirectory)
        {
            return null;
        }

        HashSet<string> equalities = new HashSet<string>(StringComparer.Ordinal);
        HashSet<string> matches = new HashSet<string>(StringComparer.Ordinal);
        CollectEqualities(filter, equalities, matches);
        if (equalities.Count == 0 && matches.Count == 0)
        {
            return null;
        }

        IndexDirectory? directory = await file.ReadIndexDirectoryAsync(cancellationToken).ConfigureAwait(false);
        if (directory is null)
        {
            return null;
        }

        DType schema = file.Schema;
        ulong blocks = (ulong)((file.RowCount + blockRows - 1) / blockRows);
        Dictionary<string, Column> columns = new Dictionary<string, Column>(StringComparer.Ordinal);
        foreach (IndexEntry entry in directory.Entries)
        {
            bool trigrams = entry.Kind == IndexKinds.BloomNgram3;
            if ((!trigrams && entry.Kind != IndexKinds.BloomSbbf) || entry.BlockLength != (ulong)blockRows
                || !KeyIndexPruner.TryResolve(schema, entry.ColumnPath, out string path, out DType dtype)
                || !(trigrams ? matches : equalities).Contains(path)
                || !BloomIndexOptions.TryParse(entry.Options, out BloomIndexOptions? options))
            {
                continue;
            }

            string key = trigrams ? TrigramKey(path) : path;
            if (!columns.TryGetValue(key, out Column? column))
            {
                column = new Column(dtype, trigrams, options!.CaseInsensitive);
                columns[key] = column;
            }

            foreach (IndexRun run in entry.Runs)
            {
                if (run.Payload.Count == 1 && run.EndBlock <= blocks && BloomTreeRun.RootWords(run.OptionBytes) > 0)
                {
                    column.Trees.Add(new Tree(run, options!.Hash));
                }
            }
        }

        List<string> empty = [];
        foreach ((string key, Column column) in columns)
        {
            column.Trees.Sort((a, b) => a.Run.FirstBlock.CompareTo(b.Run.FirstBlock));
            if (column.Trees.Count == 0)
            {
                empty.Add(key);
            }
        }

        foreach (string key in empty)
        {
            columns.Remove(key);
        }

        return columns.Count == 0 ? null : new BloomPruner(filter, columns);
    }

    /// <summary>
    /// Whether the roots leave room for a match (10 §5.4): the multi-file question, answered with one
    /// read per filtered column and no zone map.
    /// </summary>
    /// <param name="file">The open file.</param>
    /// <param name="filter">The predicate.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns><see langword="false"/> only when the roots prove no row can match.</returns>
    internal static async ValueTask<bool> FileMayMatchAsync(
        VortexFile file, VortexExpr filter, CancellationToken cancellationToken)
    {
        long blockRows = Scan.SplitPlan.NaturalBatchRows(file.LayoutTree);
        if (file.RowCount <= 0 || blockRows <= 0)
        {
            return true;
        }

        BloomPruner? pruner = await BuildAsync(file, filter, blockRows, cancellationToken).ConfigureAwait(false);
        if (pruner is null)
        {
            return true;
        }

        BlockMask live = new BlockMask(file.RowCount, blockRows);
        await pruner.RefineAsync(file, live, cancellationToken, rootsOnly: true).ConfigureAwait(false);
        return !live.IsEmpty;
    }

    /// <summary>Refines <paramref name="live"/>, descending the trees a level at a time.</summary>
    /// <param name="file">The open file.</param>
    /// <param name="live">The mask, which only loses blocks.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <param name="rootsOnly">Whether to stop at the roots.</param>
    internal async ValueTask RefineAsync(
        VortexFile file, BlockMask live, CancellationToken cancellationToken, bool rootsOnly = false)
    {
        int top = await LoadRootsAsync(file, live, cancellationToken).ConfigureAwait(false);
        for (int level = top; level >= 0 && !live.IsEmpty; level--)
        {
            if (!rootsOnly && level < top)
            {
                await LoadLevelAsync(file, live, level, cancellationToken).ConfigureAwait(false);
            }

            for (int block = 0; block < live.BlockCount; block++)
            {
                if (live.IsLive(block) && ProvesAbsent(_filter, block, level))
                {
                    live.Kill(block);
                }
            }
        }
    }

    /// <summary>Reads every root over a live block, in one read; returns the deepest root's level.</summary>
    private async ValueTask<int> LoadRootsAsync(VortexFile file, BlockMask live, CancellationToken cancellationToken)
    {
        using IndexBatches<(Tree Tree, int Slot)> batches = new();
        foreach (Column column in _columns.Values)
        {
            foreach (Tree tree in column.Trees)
            {
                if (tree.Root is null && AnyLive(live, (long)tree.Run.FirstBlock, (long)tree.Run.EndBlock))
                {
                    IndexSegment root = tree.Run.Payload[0];
                    IndexBatches<(Tree Tree, int Slot)>.Batch batch = batches.For(tree.Run);
                    batch.Wanted.Add((tree, batch.Requests.Add(
                        new SegmentSpec(root.Offset, root.Length, root.AlignmentExponent, 0, 0))));
                }
            }
        }

        foreach ((int origin, IndexBatches<(Tree Tree, int Slot)>.Batch batch) in batches.ByOrigin)
        {
            await ReadAsync(file, origin, batch.Requests, cancellationToken).ConfigureAwait(false);
            using ScanContext context = file.CreateIndexContext(origin);
            foreach ((Tree tree, int slot) in batch.Wanted)
            {
                VortexBuffer bytes = batch.Requests.GetBuffer(slot);
                uint[]? words = tree.Run.Payload[0].Holds(bytes.Span)
                    ? Decode(context, bytes, BloomTreeRun.RootWords(tree.Run.OptionBytes))
                    : null;
                tree.Root = words is null ? null : ParseRoot(words, tree.Run);
            }
        }

        int top = 0;
        foreach (Column column in _columns.Values)
        {
            foreach (Tree tree in column.Trees)
            {
                top = Math.Max(top, tree.Root?.Level ?? 0);
            }
        }

        return top;
    }

    /// <summary>A root, checked against its own header and its run: it covers at least the run's blocks.</summary>
    private static BloomNode? ParseRoot(uint[] words, IndexRun run)
    {
        if (words.Length < BloomNode.HeaderWords)
        {
            return null;
        }

        int level = (int)((words[0] >> 8) & 0xFF);
        long leaves = words[1];
        if (leaves < run.BlockCount || leaves == 0 || level != BloomNode.LevelFor(leaves))
        {
            return null;
        }

        return BloomNode.TryRead(words, 0, words.Length, level, leaves, out BloomNode? root) ? root : null;
    }

    /// <summary>
    /// Reads the children, at <paramref name="level"/>, of every node one level up that still covers
    /// a live block: one region per node, one read for the level.
    /// </summary>
    private async ValueTask LoadLevelAsync(VortexFile file, BlockMask live, int level, CancellationToken cancellationToken)
    {
        using IndexBatches<(Tree Tree, long Index, BloomNode Parent, int Slot)> batches = new();
        foreach (Column column in _columns.Values)
        {
            foreach (Tree tree in column.Trees)
            {
                if (tree.Root is null || tree.Root.Level <= level)
                {
                    continue;
                }

                foreach ((long index, BloomNode parent) in tree.NodesAt(level + 1))
                {
                    if (parent.Children is not { } region || tree.IsOpened(level + 1, index))
                    {
                        continue;
                    }

                    long start = (long)tree.Run.FirstBlock + (index << (4 * (level + 1)));
                    long end = Math.Min(start + parent.Leaves, (long)tree.Run.EndBlock);
                    if (!AnyLive(live, start, end))
                    {
                        continue;
                    }

                    tree.Open(level + 1, index);
                    IndexBatches<(Tree Tree, long Index, BloomNode Parent, int Slot)>.Batch batch = batches.For(tree.Run);
                    int slot = batch.Requests.Add(new SegmentSpec(region.Offset, region.Length, region.AlignmentExponent, 0, 0));
                    batch.Wanted.Add((tree, index, parent, slot));
                }
            }
        }

        foreach ((int origin, IndexBatches<(Tree Tree, long Index, BloomNode Parent, int Slot)>.Batch batch) in batches.ByOrigin)
        {
            await ReadAsync(file, origin, batch.Requests, cancellationToken).ConfigureAwait(false);
            using ScanContext context = file.CreateIndexContext(origin);
            foreach ((Tree tree, long index, BloomNode parent, int slot) in batch.Wanted)
            {
                VortexBuffer bytes = batch.Requests.GetBuffer(slot);
                ReadOnlySpan<uint> childWords = parent.ChildWords;
                long total = 0;
                foreach (uint child in childWords)
                {
                    total += child;
                }

                uint[]? words = parent.Children!.Value.Holds(bytes.Span) && total <= int.MaxValue
                    ? Decode(context, bytes, (int)total)
                    : null;
                if (words is not null)
                {
                    tree.Adopt(level, index, parent, words);
                }
            }
        }
    }

    /// <summary>Reads one origin's regions, counting them.</summary>
    private async ValueTask ReadAsync(
        VortexFile file, int origin, SegmentRequestSet requests, CancellationToken cancellationToken)
    {
        Segments += requests.Count;
        for (int i = 0; i < requests.Count; i++)
        {
            Bytes += requests.GetSpec(i).Length;
        }

        await file.IndexSourceOf(origin).ReadManyAsync(requests, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// A region's words, copied out of the segment, or <see langword="null"/> when the payload is
    /// not the u32 array of the length its parent declares.
    /// </summary>
    private static uint[]? Decode(ScanContext context, VortexBuffer segment, int expected)
    {
        if (expected <= 0)
        {
            return null;
        }

        try
        {
            context.ResetBatch();
            context.Decode.LoadBlob(segment);
            DTypeArena types = new DTypeArena();
            DType u32 = types.Primitive(PType.U32, Nullability.NonNullable);
            ArrayNode root = context.Nodes.Root;
            int node = context.Decode.DecodeRoot(in root, u32, expected);
            CanonicalNode array = context.Canonical.GetNode(node);
            if (array.Kind != CanonicalKind.Primitive || array.PType != PType.U32 || array.Length != expected)
            {
                return null;
            }

            return array.Values.Cast<uint>()[..expected].ToArray();
        }
        catch (VortexFormatException)
        {
            return null;
        }
    }

    private static bool AnyLive(BlockMask live, long start, long end)
    {
        end = Math.Min(end, live.BlockCount);
        for (long block = Math.Max(start, 0); block < end; block++)
        {
            if (live.IsLive((int)block))
            {
                return true;
            }
        }

        return false;
    }

    // ------------------------------------------------------------------------------ the proof

    private bool ProvesAbsent(VortexExpr expr, int block, int level)
    {
        switch (expr)
        {
            case LogicalExpr { IsAnd: true } and:
                return ProvesAbsent(and.Left, block, level) || ProvesAbsent(and.Right, block, level);

            case LogicalExpr or:
                return ProvesAbsent(or.Left, block, level) && ProvesAbsent(or.Right, block, level);

            case ComparisonExpr { Op: ComparisonOp.Equal } equal:
                return Absent(equal.Field.Path, equal.Value, block, level, elements: false);

            case ListContainsExpr contains:
                // A list's filter holds its elements, each in its row's block (10 §5.1).
                return Absent(contains.Field.Path, contains.Value, block, level, elements: true);

            case StringMatchExpr match:
                return AbsentTrigrams(match, block, level);

            case InExpr @in:
                foreach (FilterLiteral value in @in.Values)
                {
                    if (!Absent(@in.Field.Path, value, block, level, elements: false))
                    {
                        return false;
                    }
                }

                return @in.Values.Count > 0;

            default:
                return false;
        }
    }

    /// <summary>
    /// Whether the filter over <paramref name="block"/> proves <paramref name="value"/> absent from
    /// the column, or from its lists' elements when <paramref name="elements"/> — and only when the
    /// column is what the question takes it for: an equality on a list column, or an element on a
    /// scalar one, claims nothing.
    /// </summary>
    private bool Absent(string path, FilterLiteral value, int block, int level, bool elements)
    {
        if (!_columns.TryGetValue(path, out Column? column)
            || column.IsList != elements
            || column.TreeOf(block) is not { } tree
            || !tree.TryFilter(level, block, out ReadOnlySpan<uint> words))
        {
            return false;
        }

        if (!column.Hashes(value, tree.Hash, out ulong first, out ulong second, out bool two))
        {
            return false;
        }

        return !SplitBlockBloom.Contains(words, first) && !(two && SplitBlockBloom.Contains(words, second));
    }

    /// <summary>
    /// A string predicate is absent from a block when one trigram it requires is absent from the
    /// filter over the block (10 §5.2); a predicate that requires none claims nothing.
    /// </summary>
    private bool AbsentTrigrams(StringMatchExpr match, int block, int level)
    {
        if (!_columns.TryGetValue(TrigramKey(match.Field.Path), out Column? column)
            || column.TreeOf(block) is not { } tree
            || !tree.TryFilter(level, block, out ReadOnlySpan<uint> words))
        {
            return false;
        }

        foreach (byte[] trigram in column.Required(match))
        {
            if (!SplitBlockBloom.Contains(words, SplitBlockBloom.Hash(trigram, tree.Hash)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The trigram entries of a column live beside its value entries, under their own key.</summary>
    private static string TrigramKey(string path) => path + "\0ngram3";

    private static void CollectEqualities(VortexExpr expr, HashSet<string> equalities, HashSet<string> matches)
    {
        switch (expr)
        {
            case LogicalExpr logical:
                CollectEqualities(logical.Left, equalities, matches);
                CollectEqualities(logical.Right, equalities, matches);
                break;
            case ComparisonExpr { Op: ComparisonOp.Equal } equal:
                equalities.Add(equal.Field.Path);
                break;
            case InExpr @in:
                equalities.Add(@in.Field.Path);
                break;
            case ListContainsExpr contains:
                equalities.Add(contains.Field.Path);
                break;
            case StringMatchExpr match:
                matches.Add(match.Field.Path);
                break;
            default:
                break;
        }
    }

    // ------------------------------------------------------------------------------ state

    /// <summary>One column's trees and how its values hash.</summary>
    /// <remarks>
    /// The literal's bytes come from <see cref="KeyLayout.TryEncode"/>, the one conversion every
    /// value index shares; a decimal has no layout, so a decimal filter claims nothing here.
    /// </remarks>
    private sealed class Column
    {
        private readonly bool _keyed;
        private readonly KeyLayout _layout;
        private readonly bool _fold;
        private readonly Dictionary<StringMatchExpr, List<byte[]>> _required = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<(FilterLiteral Value, BloomHash Hash), (bool Ok, ulong First, ulong Second, bool Two)> _hashes = [];

        internal Column(DType dtype, bool trigrams, bool fold)
        {
            // A list's filter holds its elements, so its values are keyed as the elements are.
            DType storage = dtype;
            while (storage.Kind == DTypeKind.Extension)
            {
                storage = storage.StorageType;
            }

            IsList = storage.Kind is DTypeKind.List or DTypeKind.FixedSizeList;
            _keyed = !trigrams && KeyLayout.TryOf(IsList ? storage.ElementType : dtype, out _layout);
            _fold = fold;
        }

        /// <summary>Whether the column is a list, whose filter answers for its elements.</summary>
        internal bool IsList { get; }

        /// <summary>The column's trees, in block order; their runs are disjoint (10 §4.1).</summary>
        internal List<Tree> Trees { get; } = [];

        /// <summary>The tree whose run covers <paramref name="block"/>, or null.</summary>
        internal Tree? TreeOf(int block)
        {
            int low = 0;
            int high = Trees.Count - 1;
            while (low <= high)
            {
                int mid = (low + high) >>> 1;
                IndexRun run = Trees[mid].Run;
                if ((ulong)block < run.FirstBlock)
                {
                    high = mid - 1;
                }
                else if ((ulong)block >= run.EndBlock)
                {
                    low = mid + 1;
                }
                else
                {
                    return Trees[mid];
                }
            }

            return null;
        }

        /// <summary>The trigrams a predicate requires, folded as this column's filters were, computed once.</summary>
        internal List<byte[]> Required(StringMatchExpr match)
        {
            if (!_required.TryGetValue(match, out List<byte[]>? trigrams))
            {
                trigrams = Trigrams.Required(match, _fold);
                _required[match] = trigrams;
            }

            return trigrams;
        }

        /// <summary>
        /// The hash (or the two, for a float zero) of <paramref name="value"/> as this column stores
        /// it, computed once per query; <see langword="false"/> when the conversion is not exact and
        /// nothing can be claimed.
        /// </summary>
        internal bool Hashes(FilterLiteral value, BloomHash hash, out ulong first, out ulong second, out bool two)
        {
            if (!_hashes.TryGetValue((value, hash), out (bool Ok, ulong First, ulong Second, bool Two) known))
            {
                known.Ok = Compute(value, hash, out known.First, out known.Second, out known.Two);
                _hashes[(value, hash)] = known;
            }

            (first, second, two) = (known.First, known.Second, known.Two);
            return known.Ok;
        }

        private bool Compute(FilterLiteral value, BloomHash hash, out ulong first, out ulong second, out bool two)
        {
            first = 0;
            second = 0;
            two = false;
            Span<byte> scratch = stackalloc byte[sizeof(ulong)];
            Span<byte> zero = stackalloc byte[sizeof(ulong)];
            if (!_keyed || !_layout.TryEncode(value, scratch, out ReadOnlySpan<byte> key, zero, out bool hasOtherZero))
            {
                return false;
            }

            first = SplitBlockBloom.Hash(key, hash);
            if (hasOtherZero)
            {
                second = SplitBlockBloom.Hash(zero[.._layout.Width], hash);
                two = true;
            }

            return true;
        }
    }

    /// <summary>One run's tree: its root, and the nodes and leaves read so far.</summary>
    private sealed class Tree(IndexRun run, BloomHash hash)
    {
        /// <summary>[level]: node index at that level to node; [0] is unused, leaves live apart.</summary>
        private readonly List<Dictionary<long, BloomNode>> _nodes = [];
        private readonly Dictionary<long, (uint[] Words, int Start, int Length)> _leaves = [];
        private readonly HashSet<(int Level, long Index)> _opened = [];
        private BloomNode? _root;

        internal IndexRun Run { get; } = run;

        internal BloomHash Hash { get; } = hash;

        /// <summary>The root, once read; null while unread or when it did not check out.</summary>
        internal BloomNode? Root
        {
            get => _root;
            set
            {
                _root = value;
                if (value is not null)
                {
                    Level(value.Level)[0] = value;
                }
            }
        }

        internal IEnumerable<KeyValuePair<long, BloomNode>> NodesAt(int level) =>
            level < _nodes.Count ? _nodes[level] : [];

        internal bool IsOpened(int level, long index) => _opened.Contains((level, index));

        internal void Open(int level, long index) => _opened.Add((level, index));

        /// <summary>Takes the children of <paramref name="parent"/>, whose words are <paramref name="words"/>.</summary>
        /// <param name="level">The children's level.</param>
        /// <param name="index">The parent's index at the level above.</param>
        /// <param name="parent">The parent.</param>
        /// <param name="words">The children's words, concatenated.</param>
        internal void Adopt(int level, long index, BloomNode parent, uint[] words)
        {
            ReadOnlySpan<uint> sizes = parent.ChildWords;
            int at = 0;
            for (int child = 0; child < sizes.Length; child++)
            {
                int length = (int)sizes[child];
                long position = (index * BloomIndexOptions.Fanout) + child;
                if (level == 0)
                {
                    if (length > 0)
                    {
                        _leaves[position] = (words, at, length);
                    }
                }
                else if (BloomNode.TryRead(words, at, length, level, BloomNode.ChildLeaves(parent.Level, parent.Leaves, child), out BloomNode? node))
                {
                    Level(level)[position] = node!;
                }

                at += length;
            }
        }

        /// <summary>The filter over <paramref name="block"/> at <paramref name="level"/>, when one was read.</summary>
        internal bool TryFilter(int level, int block, out ReadOnlySpan<uint> words)
        {
            long relative = block - (long)Run.FirstBlock;
            if (level == 0)
            {
                if (_leaves.TryGetValue(relative, out (uint[] Words, int Start, int Length) leaf))
                {
                    words = leaf.Words.AsSpan(leaf.Start, leaf.Length);
                    return true;
                }

                words = default;
                return false;
            }

            if (level < _nodes.Count && _nodes[level].TryGetValue(relative >> (4 * level), out BloomNode? node)
                && node.FilterBlocks > 0)
            {
                words = node.Filter;
                return true;
            }

            words = default;
            return false;
        }

        private Dictionary<long, BloomNode> Level(int level)
        {
            while (_nodes.Count <= level)
            {
                _nodes.Add([]);
            }

            return _nodes[level];
        }
    }
}
