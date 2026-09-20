using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Vorticity.Arrays;
using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Buffers;
using Vorticity.Indexes;
using Vorticity.Types;

namespace Vorticity.Writing;

/// <summary>A closed node, waiting for its level's group to go out.</summary>
/// <param name="level">Its level.</param>
/// <param name="leaves">The blocks under it.</param>
/// <param name="childWords">Each child's words.</param>
/// <param name="filter">Its filter's words; empty for none.</param>
/// <param name="children">The region of its children, or null when none has a word.</param>
internal sealed class PendingBloomNode(int level, long leaves, int[] childWords, uint[] filter, PendingPayload? children)
    : IPayloadLayout
{
    internal int Level { get; } = level;

    internal long Leaves { get; } = leaves;

    internal int[] ChildWords { get; } = childWords;

    /// <summary>Its filter's words; the root's may be replaced by the file-wide one.</summary>
    internal uint[] Filter { get; set; } = filter;

    internal PendingPayload? Children { get; } = children;

    /// <summary>The words the node takes: a bare node lists no child.</summary>
    internal int Words => BloomNode.WordsOf(
        BloomNode.IsBare(Level, ChildWords) ? 0 : ChildWords.Length, Filter.Length / SplitBlockBloom.WordsPerBlock);

    /// <summary>Writes the node, once its children's region is placed.</summary>
    /// <param name="destination">Exactly its words.</param>
    /// <exception cref="InvalidOperationException">Its children were not written first.</exception>
    internal void WriteTo(Span<uint> destination)
    {
        IndexSegment? region = null;
        if (Children is not null)
        {
            region = Children.Segment
                ?? throw new InvalidOperationException("A filter node goes out after the region of its children.");
        }

        BloomNode.Write(destination, Level, Leaves, region, ChildWords, Filter);
    }

    /// <summary>Lays the node alone: the root's region.</summary>
    public int Lay(CanonicalArena arena, DTypeArena types) => BloomTreeWriter.Lay(arena, types, this, null);
}

/// <summary>A tree the builder finished: its root and where its blocks start.</summary>
/// <param name="FirstBlock">The first block under it.</param>
/// <param name="Root">The root node.</param>
/// <param name="Payload">The root's region, the run's payload.</param>
/// <param name="Nodes">The nodes above the leaves that carry a filter.</param>
internal sealed record BloomTree(int FirstBlock, PendingBloomNode Root, PendingPayload Payload, int Nodes)
{
    /// <summary>The blocks under the tree.</summary>
    internal long Leaves => Root.Leaves;

    /// <summary>The root's words, which the run's options state.</summary>
    internal int RootWords => Root.Words;
}

/// <summary>
/// Assembles one column's filter tree, a generation at a time: a closing generation becomes a
/// level-1 node over the region of its leaves, a full group of nodes becomes one region and one
/// node above it, and at the end of the data the partial groups close from the bottom until a
/// level is left holding a single node, the root.
/// </summary>
/// <remarks>
/// A node's filter is the union of its children's values, kept as one hash set per open level. A
/// closed node's set moves into the node above, handed over when it is the first child and folded
/// in and recycled otherwise, so a short tree copies nothing and a long one reuses the same few
/// tables. A set that grows past what a node of the ceiling can hold at the target rate is dropped
/// and its node gets no filter, nor does any node above it, which also bounds what a set may hold.
/// </remarks>
internal sealed class BloomTreeWriter : IDisposable
{
    private readonly int _fpp;
    private readonly int _maxBlocks;
    private readonly int _minDistinct;
    private readonly bool _filters;
    private readonly IndexBuilder _owner;

    /// <summary>[level − 1]: the closed nodes of that level still waiting to go out, a fanout at most.</summary>
    private readonly List<List<PendingBloomNode>> _groups = [];

    /// <summary>[level − 1]: the union under the open node of that level; null until something comes, or once passed.</summary>
    private readonly List<HashSet64?> _sets = [];

    /// <summary>Bit level − 1: whether that union passed the ceiling. The word holds every level a tree can reach.</summary>
    private uint _passed;

    /// <summary>Sets emptied and kept with their tables, for the next node that needs one.</summary>
    private Stack<HashSet64>? _spare;

    /// <param name="fppPpm">The target rate.</param>
    /// <param name="maxBlocks">A node's ceiling.</param>
    /// <param name="minDistinct">The floor below which a node gets no filter.</param>
    /// <param name="filters">Whether the nodes carry filters: a policy of one resolution has leaves alone.</param>
    /// <param name="owner">The builder whose queue the regions join.</param>
    internal BloomTreeWriter(int fppPpm, int maxBlocks, int minDistinct, bool filters, IndexBuilder owner)
    {
        _fpp = fppPpm;
        _maxBlocks = maxBlocks;
        _minDistinct = minDistinct;
        _filters = filters;
        _owner = owner;
        Capacity = CapacityOf(fppPpm, maxBlocks);
    }

    /// <summary>The distinct values a node of the ceiling holds at the target rate.</summary>
    internal int Capacity { get; }

    /// <summary>The nodes closed so far that carry a filter.</summary>
    internal int Nodes { get; private set; }

    /// <summary>The filter bytes waiting in the groups.</summary>
    internal long OpenBytes { get; private set; }

    /// <summary>
    /// The open generation's union, for the builder to hash into; null when the nodes carry no
    /// filter or the generation passed the ceiling.
    /// </summary>
    internal HashSet64? Generation
    {
        get
        {
            if (!_filters || Passed(1))
            {
                return null;
            }

            Ensure(1);
            return _sets[0] ??= Take();
        }
    }

    /// <summary>The open generation's distinct values so far, 0 when it has no set.</summary>
    internal int GenerationCount => _sets.Count > 0 ? _sets[0]?.Count ?? 0 : 0;

    /// <summary>Whether the open generation passed the ceiling.</summary>
    internal bool GenerationPassed => Passed(1);

    /// <summary>The most distinct values a filter of <paramref name="maxBlocks"/> blocks holds at <paramref name="fppPpm"/>.</summary>
    /// <param name="fppPpm">The target rate.</param>
    /// <param name="maxBlocks">The ceiling.</param>
    internal static int CapacityOf(int fppPpm, int maxBlocks)
    {
        if (SplitBlockBloom.BlocksFor(1, fppPpm, maxBlocks + 1) > maxBlocks)
        {
            return 0;
        }

        int low = 1;
        int high = 1 << 30;
        while (low < high)
        {
            int mid = low + ((high - low + 1) >> 1);
            if (SplitBlockBloom.BlocksFor(mid, fppPpm, maxBlocks + 1) <= maxBlocks)
            {
                low = mid;
            }
            else
            {
                high = mid - 1;
            }
        }

        return low;
    }

    /// <summary>The open generation passed the ceiling: its node gets no filter, nor the nodes above it.</summary>
    internal void PassGeneration()
    {
        Ensure(1);
        Drop(1);
    }

    /// <summary>
    /// The filter of <paramref name="set"/> under <paramref name="maxBlocks"/>, or none when the set
    /// is under the floor or needs more blocks than that: a node past its ceiling is not built.
    /// </summary>
    /// <param name="set">The union.</param>
    /// <param name="maxBlocks">The ceiling.</param>
    internal uint[] Filter(HashSet64 set, int maxBlocks)
    {
        int distinct = set.Count;
        if (distinct == 0 || distinct < _minDistinct)
        {
            return [];
        }

        int blocks = SplitBlockBloom.BlocksFor(distinct, _fpp, maxBlocks + 1);
        if (blocks > maxBlocks)
        {
            return [];
        }

        uint[] words = new uint[blocks * SplitBlockBloom.WordsPerBlock];
        set.InsertInto(words);
        return words;
    }

    /// <summary>The open generation closed into a level-1 node over its union.</summary>
    /// <param name="leafWords">Each block's filter words, 0 for none.</param>
    /// <param name="leaves">The leaves' region, already queued; null when no block has a filter.</param>
    internal void CloseGeneration(int[] leafWords, PendingPayload? leaves) =>
        Add(new PendingBloomNode(1, leafWords.Length, leafWords, NodeFilter(1), leaves));

    /// <summary>
    /// Closes the partial levels from the bottom and queues the root: the first level left with one
    /// node. <paramref name="rootFilter"/>, when it gives one, replaces the root's filter.
    /// </summary>
    /// <param name="firstBlock">The first block under the tree.</param>
    /// <param name="rootFilter">The file-wide filter, or null.</param>
    /// <returns>The tree, or null when no generation closed.</returns>
    internal BloomTree? Finish(int firstBlock, Func<uint[]?>? rootFilter)
    {
        for (int level = 1; level <= _groups.Count; level++)
        {
            List<PendingBloomNode> group = _groups[level - 1];
            if (group.Count == 0)
            {
                continue;
            }

            if (group.Count > 1 || Above(level))
            {
                Close(level + 1);
                continue;
            }

            PendingBloomNode root = group[0];
            group.Clear();
            OpenBytes -= (long)root.Filter.Length * sizeof(uint);
            if (rootFilter?.Invoke() is { Length: > 0 } file)
            {
                Nodes += root.Filter.Length == 0 ? 1 : 0;
                root.Filter = file;
            }

            PendingPayload payload = new PendingPayload(root, compress: false, (long)root.Words * sizeof(uint));
            _owner.Enqueue(payload);
            return new BloomTree(firstBlock, root, payload, Nodes);
        }

        return null;
    }

    /// <summary>Drops everything the tree holds: the builder gave up.</summary>
    internal void Abandon()
    {
        foreach (List<PendingBloomNode> group in _groups)
        {
            group.Clear();
        }

        for (int at = 0; at < _sets.Count; at++)
        {
            _sets[at]?.Dispose();
            _sets[at] = null;
        }

        while (_spare is { Count: > 0 })
        {
            _spare.Pop().Dispose();
        }

        OpenBytes = 0;
    }

    public void Dispose() => Abandon();

    private bool Above(int level)
    {
        for (int at = level; at < _groups.Count; at++)
        {
            if (_groups[at].Count > 0)
            {
                return true;
            }
        }

        return false;
    }

    private List<PendingBloomNode> Group(int level)
    {
        while (_groups.Count < level)
        {
            _groups.Add([]);
        }

        return _groups[level - 1];
    }

    private void Ensure(int level)
    {
        while (_sets.Count < level)
        {
            _sets.Add(null);
        }
    }

    private bool Passed(int level) => (_passed & (1u << (level - 1))) != 0;

    private void SetPassed(int level, bool passed) =>
        _passed = passed ? _passed | (1u << (level - 1)) : _passed & ~(1u << (level - 1));

    /// <summary>The filter of the open node of <paramref name="level"/>, from its union.</summary>
    private uint[] NodeFilter(int level) =>
        _filters && !Passed(level) && level <= _sets.Count && _sets[level - 1] is { } union
            ? Filter(union, _maxBlocks)
            : [];

    /// <summary>
    /// Adds a closed node to its level's group, moves its union into the node above, and closes that
    /// node when the group is full.
    /// </summary>
    private void Add(PendingBloomNode node)
    {
        List<PendingBloomNode> group = Group(node.Level);
        group.Add(node);
        OpenBytes += (long)node.Filter.Length * sizeof(uint);
        Nodes += node.Filter.Length > 0 ? 1 : 0;
        if (_filters)
        {
            Promote(node.Level);
        }

        if (group.Count == BloomIndexOptions.Fanout)
        {
            Close(node.Level + 1);
        }
    }

    /// <summary>
    /// Moves the union of the node of <paramref name="level"/> that just closed into the open node
    /// above it, and leaves the level empty for its next node.
    /// </summary>
    private void Promote(int level)
    {
        Ensure(level + 1);
        HashSet64? set = _sets[level - 1];
        bool passed = Passed(level);
        _sets[level - 1] = null;
        SetPassed(level, false);
        if (Passed(level + 1))
        {
            Recycle(set);
            return;
        }

        if (passed)
        {
            Recycle(set);
            Drop(level + 1);
            return;
        }

        if (set is null)
        {
            return;
        }

        if (_sets[level] is not { } above)
        {
            // The first child hands its union over: nothing is copied.
            _sets[level] = set;
            above = set;
        }
        else
        {
            above.AddAll(set);
            Recycle(set);
        }

        if (above.Count > Capacity)
        {
            Drop(level + 1);
        }
    }

    private void Drop(int level)
    {
        Recycle(_sets[level - 1]);
        _sets[level - 1] = null;
        SetPassed(level, true);
    }

    private void Recycle(HashSet64? set)
    {
        if (set is null)
        {
            return;
        }

        // The table is kept for the next node, which on the columns where this matters holds about
        // as much: growing every set again from nothing is a large share of such a column's write.
        set.Clear();
        (_spare ??= new Stack<HashSet64>()).Push(set);
    }

    private HashSet64 Take() => _spare is { Count: > 0 } spare ? spare.Pop() : new HashSet64();

    /// <summary>Closes the open node of <paramref name="level"/> over the group below it, which goes out as its children.</summary>
    private void Close(int level)
    {
        List<PendingBloomNode> group = Group(level - 1);
        PendingBloomNode[] children = [.. group];
        group.Clear();
        long leaves = 0;
        int[] childWords = new int[children.Length];
        for (int i = 0; i < children.Length; i++)
        {
            leaves += children[i].Leaves;
            childWords[i] = children[i].Words;
            OpenBytes -= (long)children[i].Filter.Length * sizeof(uint);
        }

        PendingPayload region = Region(children);
        _owner.Enqueue(region);
        Add(new PendingBloomNode(level, leaves, childWords, NodeFilter(level), region));
    }

    /// <summary>A region holding <paramref name="nodes"/>' words, laid once the regions they name are placed.</summary>
    private static PendingPayload Region(PendingBloomNode[] nodes)
    {
        int words = 0;
        foreach (PendingBloomNode node in nodes)
        {
            words = checked(words + node.Words);
        }

        // Left uncompressed, like every filter: the words are uniform bits and the headers are few.
        return new PendingPayload(new NodeGroup(nodes), compress: false, (long)words * sizeof(uint));
    }

    /// <summary>
    /// Lays <paramref name="nodes"/>, or <paramref name="single"/> when there is one, as one u32
    /// array, each node's words after the last's.
    /// </summary>
    internal static int Lay(CanonicalArena arena, DTypeArena types, PendingBloomNode? single, PendingBloomNode[]? nodes)
    {
        ReadOnlySpan<PendingBloomNode> all = nodes is null ? new ReadOnlySpan<PendingBloomNode>(in single!) : nodes;
        int words = 0;
        foreach (PendingBloomNode node in all)
        {
            words = checked(words + node.Words);
        }

        VortexBuffer buffer = arena.AllocateUninitialized(words * sizeof(uint), sizeof(uint), out Span<byte> bytes);
        Span<uint> into = MemoryMarshal.Cast<byte, uint>(bytes);
        int at = 0;
        foreach (PendingBloomNode node in all)
        {
            int length = node.Words;
            node.WriteTo(into.Slice(at, length));
            at += length;
        }

        return arena.AddPrimitive(
            types.Primitive(PType.U32, Nullability.NonNullable), words, Validity.NonNullable, PType.U32, buffer);
    }

    /// <summary>The children of a node, laid as one region.</summary>
    private sealed class NodeGroup(PendingBloomNode[] nodes) : IPayloadLayout
    {
        public int Lay(CanonicalArena arena, DTypeArena types) => BloomTreeWriter.Lay(arena, types, null, nodes);
    }
}
