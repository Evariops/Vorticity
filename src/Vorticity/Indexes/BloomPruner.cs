using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Buffers;
using Vorticity.Compute;
using Vorticity.Expressions;
using Vorticity.File;
using Vorticity.IO;
using Vorticity.Serialization.Schemas;
using Vorticity.Types;

namespace Vorticity.Indexes;

/// <summary>
/// Kills the blocks a column's Bloom filters prove cannot hold an equality's value; a probe reads
/// every tree's root in one read, then descends a level at a time through the nodes that still cover
/// a live block.
/// </summary>
/// <remarks>
/// Only equality, membership, list containment and a string match claim anything, and only when the
/// literal converts exactly to the bytes the column stores; a conversion that rounds claims nothing
/// rather than proving something on the wrong value. Anything that does not check out -- a region
/// whose bytes are not its checksum's, a node that is not what its parent says -- leaves its blocks
/// live and stops the descent there, so a lying index may cost pruning but never rows.
/// </remarks>
internal sealed class BloomPruner
{
    private readonly VortexExpr _filter;
    private readonly Dictionary<string, Column> _columns;

    /// <summary>Each question of the filter a column's filters answer, by reference.</summary>
    private readonly Dictionary<VortexExpr, Question> _questions =
        new Dictionary<VortexExpr, Question>(ReferenceEqualityComparer.Instance);

    private BloomPruner(VortexExpr filter, Dictionary<string, Column> columns)
    {
        _filter = filter;
        _columns = columns;
        Plan(filter);
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
        // A file without a directory pays nothing, not even the collectors: a filtered scan's
        // allocation ceiling is held to the byte.
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

        DType schema = file.DType;
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
    /// Whether the roots leave room for a match: the whole-file question, answered with one read per
    /// filtered column and no zone map.
    /// </summary>
    /// <param name="file">The open file.</param>
    /// <param name="filter">The predicate.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns><see langword="false"/> only when the roots prove no row can match.</returns>
    internal static async ValueTask<bool> FileMayMatchAsync(
        VortexFile file, VortexExpr filter, CancellationToken cancellationToken)
    {
        long blockRows = Scanning.SplitPlan.NaturalBatchRows(file.LayoutTree);
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

            Kill(live, level);
        }
    }

    /// <summary>
    /// Kills the blocks the filters read at <paramref name="level"/> prove the filter false in: each
    /// node's filter is asked once, for all the blocks it covers, a word of blocks at a time.
    /// </summary>
    private void Kill(BlockMask live, int level)
    {
        int words = live.Words.Length;
        ulong[] rented = ArrayPool<ulong>.Shared.Rent(words);
        try
        {
            Span<ulong> kept = rented.AsSpan(0, words);
            Absent(_filter, kept, level);
            for (int i = 0; i < kept.Length; i++)
            {
                kept[i] = ~kept[i];
            }

            live.Keep(kept);
        }
        finally
        {
            ArrayPool<ulong>.Shared.Return(rented);
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
                if (tree.Root is null || tree.Root.Level <= level || tree.NodesAt(level + 1) is not { } parents)
                {
                    continue;
                }

                foreach ((long index, BloomNode parent) in parents)
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

    /// <summary>Reads one origin's regions, counting those its source is asked for.</summary>
    private async ValueTask ReadAsync(
        VortexFile file, int origin, SegmentRequestSet requests, CancellationToken cancellationToken)
    {
        ISegmentReader reader = file.IndexSourceOf(origin);
        Segments += Scanning.ScanCounters.Unread(requests, reader, out long bytes);
        Bytes += bytes;
        await reader.ReadManyAsync(requests, cancellationToken).ConfigureAwait(false);
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

    private static bool AnyLive(BlockMask live, long start, long end) => live.AnyLiveBlocks(start, end);

    // ------------------------------------------------------------------------------ the proof

    /// <summary>
    /// Records, once, the questions of the filter a column's filters answer: an equality, an
    /// <c>IN</c> or a list's element on a column that is what the question takes it for, and a text
    /// match on a column with trigram filters.
    /// </summary>
    private void Plan(VortexExpr expr)
    {
        switch (expr)
        {
            case LogicalExpr logical:
                Plan(logical.Left);
                Plan(logical.Right);
                break;

            // A function of a column is not the column: its filter holds the column's values.
            case ComparisonExpr { Op: ComparisonOp.Equal, Field: not Compute.FunctionFieldExpr } equal
                when _columns.TryGetValue(equal.Field.Path, out Column? column) && !column.IsList:
                _questions[expr] = new Question(column, [equal.Value]);
                break;

            case ListContainsExpr contains
                when _columns.TryGetValue(contains.Field.Path, out Column? column) && column.IsList:
                // A list's filter holds its elements, each in its row's block.
                _questions[expr] = new Question(column, [contains.Value]);
                break;

            case InExpr { Field: not Compute.FunctionFieldExpr } @in when _columns.TryGetValue(@in.Field.Path, out Column? column) && !column.IsList:
                _questions[expr] = new Question(column, @in.Values);
                break;

            case StringMatchExpr match when _columns.TryGetValue(TrigramKey(match.Field.Path), out Column? text):
                _questions[expr] = new Question(text, Trigrams.Required(match, text.Fold));
                break;

            default:
                break;
        }
    }

    /// <summary>
    /// Writes into <paramref name="absent"/> the blocks the filters read at <paramref name="level"/>
    /// prove <paramref name="expr"/> false in: a question's where a node's filter answers no, a
    /// conjunction's where either side is, a disjunction's where both are.
    /// </summary>
    private void Absent(VortexExpr expr, Span<ulong> absent, int level)
    {
        if (expr is LogicalExpr logical)
        {
            Absent(logical.Left, absent, level);
            ulong[] rented = ArrayPool<ulong>.Shared.Rent(absent.Length);
            try
            {
                Span<ulong> right = rented.AsSpan(0, absent.Length);
                Absent(logical.Right, right, level);
                for (int i = 0; i < absent.Length; i++)
                {
                    absent[i] = logical.IsAnd ? absent[i] | right[i] : absent[i] & right[i];
                }
            }
            finally
            {
                ArrayPool<ulong>.Shared.Return(rented);
            }

            return;
        }

        absent.Clear();
        if (_questions.TryGetValue(expr, out Question? question))
        {
            question.Absent(level, absent);
        }
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
            case ComparisonExpr { Op: ComparisonOp.Equal, Field: not Compute.FunctionFieldExpr } equal:
                equalities.Add(equal.Field.Path);
                break;
            case InExpr { Field: not Compute.FunctionFieldExpr } @in:
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
            Fold = fold;
        }

        /// <summary>Whether the column is a list, whose filter answers for its elements.</summary>
        internal bool IsList { get; }

        /// <summary>For trigram filters, whether their trigrams were folded.</summary>
        internal bool Fold { get; }

        /// <summary>The column's trees, in block order; their runs are disjoint.</summary>
        internal List<Tree> Trees { get; } = [];

        /// <summary>
        /// The hash (or the two, for a float zero) of <paramref name="value"/> as this column stores
        /// it; <see langword="false"/> when the conversion is not exact and nothing can be claimed.
        /// </summary>
        internal bool TryHash(FilterLiteral value, BloomHash hash, out ulong first, out ulong second, out bool two)
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

    /// <summary>
    /// A question of the filter a column's filters answer, its keys hashed once per hash the
    /// column's trees use.
    /// </summary>
    /// <remarks>
    /// An equality, an <c>IN</c> or a list's element is false under a filter that holds none of its
    /// keys, and claims nothing unless every key converts exactly; a text match is false under a
    /// filter that lacks one trigram it requires, and one that requires none claims nothing.
    /// </remarks>
    private sealed class Question
    {
        private readonly Column _column;
        private readonly IReadOnlyList<FilterLiteral>? _values;
        private readonly List<byte[]>? _trigrams;
        private readonly ulong[] _first;
        private readonly ulong[] _second;
        private readonly bool[] _two;
        private BloomHash _hash;
        private bool _hashed;
        private bool _claims;

        internal Question(Column column, IReadOnlyList<FilterLiteral> values)
        {
            _column = column;
            _values = values;
            _first = new ulong[values.Count];
            _second = new ulong[values.Count];
            _two = new bool[values.Count];
        }

        internal Question(Column column, List<byte[]> trigrams)
        {
            _column = column;
            _trigrams = trigrams;
            _first = new ulong[trigrams.Count];
            _second = [];
            _two = [];
        }

        /// <summary>
        /// Sets in <paramref name="absent"/> the blocks under every node read at
        /// <paramref name="level"/> whose filter answers no, each node asked once.
        /// </summary>
        internal void Absent(int level, Span<ulong> absent)
        {
            foreach (Tree tree in _column.Trees)
            {
                if (!Hash(tree.Hash))
                {
                    continue;
                }

                long first = (long)tree.Run.FirstBlock;
                if (level == 0)
                {
                    foreach ((long relative, (uint[] Words, int Start, int Length) leaf) in tree.Leaves)
                    {
                        if (Proves(leaf.Words.AsSpan(leaf.Start, leaf.Length)))
                        {
                            long block = first + relative;
                            absent[(int)(block >> 6)] |= 1UL << (int)(block & 63);
                        }
                    }
                }
                else if (tree.NodesAt(level) is { } nodes)
                {
                    foreach ((long index, BloomNode node) in nodes)
                    {
                        if (node.FilterBlocks > 0 && Proves(node.Filter))
                        {
                            long start = first + (index << (4 * level));
                            long end = Math.Min(start + (1L << (4 * level)), (long)tree.Run.EndBlock);
                            BitmapKernels.FillRange(MemoryMarshal.AsBytes(absent), (int)start, (int)(end - start), true);
                        }
                    }
                }
            }
        }

        /// <summary>Hashes the keys for <paramref name="hash"/> unless they already are; whether the question claims anything.</summary>
        private bool Hash(BloomHash hash)
        {
            if (_hashed && _hash == hash)
            {
                return _claims;
            }

            _hashed = true;
            _hash = hash;
            if (_trigrams is not null)
            {
                for (int i = 0; i < _trigrams.Count; i++)
                {
                    _first[i] = SplitBlockBloom.Hash(_trigrams[i], hash);
                }

                _claims = _trigrams.Count > 0;
                return _claims;
            }

            _claims = _values!.Count > 0;
            for (int i = 0; i < _values.Count && _claims; i++)
            {
                _claims = _column.TryHash(_values[i], hash, out _first[i], out _second[i], out _two[i]);
            }

            return _claims;
        }

        /// <summary>Whether a filter's words prove the question false for the blocks under it.</summary>
        private bool Proves(ReadOnlySpan<uint> words)
        {
            if (_trigrams is not null)
            {
                for (int i = 0; i < _first.Length; i++)
                {
                    if (!SplitBlockBloom.Contains(words, _first[i]))
                    {
                        return true;
                    }
                }

                return false;
            }

            for (int i = 0; i < _first.Length; i++)
            {
                if (SplitBlockBloom.Contains(words, _first[i]) || (_two[i] && SplitBlockBloom.Contains(words, _second[i])))
                {
                    return false;
                }
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

        /// <summary>The nodes read at <paramref name="level"/>, by index at that level; null when none were.</summary>
        internal Dictionary<long, BloomNode>? NodesAt(int level) => level < _nodes.Count ? _nodes[level] : null;

        /// <summary>The leaves read, by block relative to the run: where each one's filter lies.</summary>
        internal Dictionary<long, (uint[] Words, int Start, int Length)> Leaves => _leaves;

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
