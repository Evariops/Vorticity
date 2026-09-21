using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Vorticity.Buffers;

namespace Vorticity.Arrays;

/// <summary>
/// One serialized array node, flattened out of the FlatBuffer so the decode traversal never
/// re-walks vtables. 32 bytes.
/// </summary>
internal readonly struct ArrayNodeRecord
{
    private readonly ushort _encodingSpecIndex;
    private readonly ArrayEncodingId _encoding;
    private readonly int _metadataOffset;
    private readonly int _metadataLength;
    private readonly int _firstChild;
    private readonly int _childCount;
    private readonly int _firstBuffer;
    private readonly int _bufferCount;
    private readonly int _statsIndex;

    internal ArrayNodeRecord(
        ushort encodingSpecIndex,
        ArrayEncodingId encoding,
        int metadataOffset,
        int metadataLength,
        int firstChild,
        int childCount,
        int firstBuffer,
        int bufferCount,
        int statsIndex)
    {
        _encodingSpecIndex = encodingSpecIndex;
        _encoding = encoding;
        _metadataOffset = metadataOffset;
        _metadataLength = metadataLength;
        _firstChild = firstChild;
        _childCount = childCount;
        _firstBuffer = firstBuffer;
        _bufferCount = bufferCount;
        _statsIndex = statsIndex;
    }

    /// <summary>The raw <c>u16</c> from <c>ArrayNode.encoding</c>: an index into <c>Footer.array_specs</c>.</summary>
    public ushort EncodingSpecIndex => _encodingSpecIndex;

    /// <summary>The id resolved at load. <see cref="ArrayEncodingId.Unknown"/> is legal here.</summary>
    public ArrayEncodingId Encoding => _encoding;

    /// <summary>Offset into the arena's metadata region; -1 when the field is absent.</summary>
    public int MetadataOffset => _metadataOffset;

    /// <summary>Length in bytes of the encoding metadata.</summary>
    public int MetadataLength => _metadataLength;

    /// <summary>Index of the first child in the arena; -1 when <see cref="ChildCount"/> is zero.</summary>
    public int FirstChild => _firstChild;

    /// <summary>Number of children.</summary>
    public int ChildCount => _childCount;

    /// <summary>Index into the arena's per-node buffer index list.</summary>
    public int FirstBuffer => _firstBuffer;

    /// <summary>Number of buffers this node claims.</summary>
    public int BufferCount => _bufferCount;

    /// <summary>Index into the arena's stats list; -1 when the node carries no <c>stats</c>.</summary>
    public int StatsIndex => _statsIndex;
}

/// <summary>
/// A view over one node in an <see cref="ArrayNodeArena"/>.
/// </summary>
/// <remarks>
/// A <c>ref struct</c> deliberately: a node index means nothing without the arena that issued it
/// and nothing after that arena is <see cref="ArrayNodeArena.Reset"/>, so making the view
/// unstorable turns "never outlive the arena" into a compile error in most of the cases where
/// someone would break it.
/// </remarks>
internal readonly ref struct ArrayNode
{
    private readonly ArrayNodeArena _arena;
    private readonly int _index;

    internal ArrayNode(ArrayNodeArena arena, int index)
    {
        _arena = arena;
        _index = index;
    }

    /// <summary>This node's index in its arena.</summary>
    public int Index => _index;

    /// <summary>The arena this node belongs to.</summary>
    public ArrayNodeArena Arena => _arena;

    /// <summary>The resolved encoding. <see cref="ArrayEncodingId.Unknown"/> is legal.</summary>
    public ArrayEncodingId Encoding => _arena.RecordRef(_index).Encoding;

    /// <summary>The raw <c>u16</c> index into <c>Footer.array_specs</c>.</summary>
    public ushort EncodingSpecIndex => _arena.RecordRef(_index).EncodingSpecIndex;

    /// <summary>
    /// The encoding-specific Protobuf metadata, pointing into the arena's own copy of the
    /// <c>Array</c> FlatBuffer. Empty when the field is absent.
    /// </summary>
    public ReadOnlySpan<byte> Metadata
    {
        get
        {
            ref readonly ArrayNodeRecord r = ref _arena.RecordRef(_index);
            return r.MetadataLength == 0 ? default : _arena.TreeSpan.Slice(r.MetadataOffset, r.MetadataLength);
        }
    }

    /// <summary>Number of children.</summary>
    public int ChildCount => _arena.RecordRef(_index).ChildCount;

    /// <summary>Child <paramref name="index"/>, in wire order.</summary>
    /// <param name="index">0-based, below <see cref="ChildCount"/>.</param>
    /// <exception cref="VortexFormatException">The index is out of range.</exception>
    public ArrayNode GetChild(int index)
    {
        ref readonly ArrayNodeRecord r = ref _arena.RecordRef(_index);
        Debug.Assert(
            (r.FirstChild == -1) == (r.ChildCount == 0),
            "a node addresses a first child exactly when it has children");
        if ((uint)index >= (uint)r.ChildCount)
        {
            ArraysThrow.ChildIndex(index, r.ChildCount);
        }

        return new ArrayNode(_arena, r.FirstChild + index);
    }

    /// <summary>Number of data buffers this node claims.</summary>
    public int BufferCount => _arena.RecordRef(_index).BufferCount;

    /// <summary>
    /// The node's <paramref name="index"/>-th buffer, already resolved to a slice of the segment.
    /// </summary>
    /// <param name="index">0-based, below <see cref="BufferCount"/>.</param>
    /// <exception cref="VortexFormatException">The index is out of range.</exception>
    public VortexBuffer GetBuffer(int index)
    {
        ref readonly ArrayNodeRecord r = ref _arena.RecordRef(_index);
        if ((uint)index >= (uint)r.BufferCount)
        {
            return ArraysThrow.BufferIndex(index, r.BufferCount);
        }

        return _arena.ResolveBuffer(r.FirstBuffer + index);
    }

    /// <summary><see langword="true"/> when the node carried a <c>stats</c> table.</summary>
    public bool HasStats => _arena.RecordRef(_index).StatsIndex >= 0;

    /// <summary>The node's statistics; <see cref="ArrayStatsSet.IsEmpty"/> when absent.</summary>
    public ArrayStatsSet Stats
    {
        get
        {
            int statsIndex = _arena.RecordRef(_index).StatsIndex;
            return statsIndex < 0 ? default : new ArrayStatsSet(_arena, statsIndex);
        }
    }

    /// <summary>The flattened record behind this view.</summary>
    public ref readonly ArrayNodeRecord Record => ref _arena.RecordRef(_index);
}

/// <summary>
/// A pooled arena of <see cref="ArrayNodeRecord"/>s plus the resolved buffer table for one array
/// blob. Owned by a <see cref="ScanContext"/> and <see cref="Reset"/> per batch.
/// </summary>
/// <remarks>
/// <para>
/// The backing arrays are allocated once, grow by doubling and are never freed; a reset only
/// zeroes the counts, which is what makes a batch cost no managed allocation.
/// </para>
/// <para>
/// The arena never owns segment memory: a resolved buffer is a non-owning pointer into bytes the
/// batch's segment request set keeps alive. The one exception is its own copy of the array
/// FlatBuffer, into which node metadata and statistics point, so a node view never depends on the
/// segment outliving it and an inlined encoding tree needs no pinning of the caller's span.
/// </para>
/// <para>
/// Single-threaded by construction: a <see cref="ScanContext"/> is affine to one decode flow.
/// </para>
/// </remarks>
internal sealed class ArrayNodeArena
{
    private ArrayNodeRecord[] _records;
    private int _recordCount;

    // Per-node buffer index lists, concatenated. Each entry indexes _globalBuffers.
    private int[] _nodeBufferIndices;
    private int _nodeBufferIndexCount;

    // The blob's flat, global buffer list, resolved once at load.
    private VortexBuffer[] _globalBuffers;
    private int _globalBufferCount;

    private ArrayStatsRecord[] _stats;
    private int _statsCount;

    // The arena's own copy of the `Array` FlatBuffer. Node metadata and statistics are (offset,
    // length) pairs into it, so a node view never outlives-and-dereferences borrowed memory.
    private byte[] _tree;
    private int _treeLength;

    private int _rootIndex = -1;

    /// <summary>Creates an arena.</summary>
    /// <param name="initialNodeCapacity">Hint for the node array's initial size. Must be positive.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="initialNodeCapacity"/> is not positive.</exception>
    public ArrayNodeArena(int initialNodeCapacity = 64)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(initialNodeCapacity);
        _records = new ArrayNodeRecord[initialNodeCapacity];
        _nodeBufferIndices = new int[initialNodeCapacity];
        _globalBuffers = new VortexBuffer[initialNodeCapacity];
        _stats = new ArrayStatsRecord[initialNodeCapacity];
        _tree = AllocateTree(256);
    }

    /// <summary>Number of serialized nodes currently held.</summary>
    public int NodeCount => _recordCount;

    /// <summary>Index of the root node, or -1 before a successful <c>Load</c>.</summary>
    public int RootIndex => _rootIndex;

    /// <summary>
    /// The first non-empty buffer of the blob held, which names the segment every buffer of the
    /// blob is a view of; empty when the blob has no bytes outside its tree.
    /// </summary>
    internal VortexBuffer FirstBlobBuffer()
    {
        for (int i = 0; i < _globalBufferCount; i++)
        {
            if (_globalBuffers[i].Length > 0)
            {
                return _globalBuffers[i];
            }
        }

        return VortexBuffer.Empty;
    }

    /// <summary>Number of entries in the blob's flat, global buffer list.</summary>
    public int GlobalBufferCount => _globalBufferCount;

    /// <summary>Node <paramref name="index"/>.</summary>
    /// <param name="index">0-based, below <see cref="NodeCount"/>.</param>
    /// <exception cref="VortexFormatException">The index is out of range.</exception>
    public ArrayNode GetNode(int index)
    {
        if ((uint)index >= (uint)_recordCount)
        {
            ArraysThrow.NodeIndex(index, _recordCount);
        }

        return new ArrayNode(this, index);
    }

    /// <summary>The root node.</summary>
    /// <exception cref="VortexFormatException">No array has been loaded.</exception>
    public ArrayNode Root
    {
        get
        {
            if (_rootIndex < 0)
            {
                ArraysThrow.NotLoaded();
            }

            return new ArrayNode(this, _rootIndex);
        }
    }

    /// <summary>
    /// Clears the counts, leaving the backing arrays allocated and every
    /// <see cref="SegmentOwner"/> untouched: the arena has never owned segment memory.
    /// </summary>
    public void Reset()
    {
        _recordCount = 0;
        _nodeBufferIndexCount = 0;
        _globalBufferCount = 0;
        _statsCount = 0;
        _treeLength = 0;
        _rootIndex = -1;
    }

    // ------------------------------------------------------------------ internals used by the loader

    internal ReadOnlySpan<byte> TreeSpan => _tree.AsSpan(0, _treeLength);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ref readonly ArrayNodeRecord RecordRef(int index)
    {
        if ((uint)index >= (uint)_recordCount)
        {
            ArraysThrow.NodeIndex(index, _recordCount);
        }

        return ref _records[index];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ref readonly ArrayStatsRecord StatsRef(int index)
    {
        if ((uint)index >= (uint)_statsCount)
        {
            ArraysThrow.NodeIndex(index, _statsCount);
        }

        return ref _stats[index];
    }

    internal VortexBuffer ResolveBuffer(int nodeBufferSlot)
    {
        // Both indirections were validated at load, so this is a pair of array reads on the hot
        // path; the guards below cost one predicted branch each and keep a corrupted arena from
        // ever reaching a raw pointer.
        if ((uint)nodeBufferSlot >= (uint)_nodeBufferIndexCount)
        {
            return ArraysThrow.BufferIndex(nodeBufferSlot, _nodeBufferIndexCount);
        }

        int global = _nodeBufferIndices[nodeBufferSlot];
        if ((uint)global >= (uint)_globalBufferCount)
        {
            return ArraysThrow.BufferIndex(global, _globalBufferCount);
        }

        return _globalBuffers[global];
    }

    /// <summary>Copies the <c>Array</c> FlatBuffer into arena-owned storage and returns it.</summary>
    internal Span<byte> BeginTree(int length)
    {
        if (_tree.Length < length)
        {
            _tree = AllocateTree(Grow(_tree.Length, length));
        }

        _treeLength = length;
        return _tree.AsSpan(0, length);
    }

    /// <summary>
    /// The FlatBuffer copy lives on the pinned object heap. Two reasons, both structural:
    /// FlatBufferTable.GetStructVector reinterprets a struct vector in place and tests the address
    /// of its elements, so the copy's base must really be 8-aligned; and a pinned array cannot be
    /// moved out from under a span a node view is still holding.
    /// </summary>
    private static byte[] AllocateTree(int capacity) => GC.AllocateArray<byte>(capacity, pinned: true);

    internal void AddGlobalBuffer(VortexBuffer buffer)
    {
        if (_globalBufferCount == _globalBuffers.Length)
        {
            Array.Resize(ref _globalBuffers, Grow(_globalBuffers.Length, _globalBufferCount + 1));
        }

        _globalBuffers[_globalBufferCount++] = buffer;
    }

    /// <summary>Appends <paramref name="count"/> placeholder node slots and returns the first index.</summary>
    /// <param name="count">How many slots to reserve; zero is legal and reserves nothing.</param>
    /// <exception cref="VortexFormatException">The request cannot be represented as an index.</exception>
    internal int ReserveNodes(int count)
    {
        int first = _recordCount;
        long needed = (long)first + count;
        if (count < 0 || needed > MaxCapacity)
        {
            ArraysThrow.Format($"An array tree of {needed} nodes cannot be addressed.");
        }

        if (needed > _records.Length)
        {
            Array.Resize(ref _records, Grow(_records.Length, (int)needed));
        }

        _recordCount = (int)needed;
        return count == 0 ? -1 : first;
    }

    /// <summary>
    /// Largest array this arena will grow to. Below <c>int.MaxValue</c> so that the doubling in
    /// <see cref="Grow"/> can never overflow into a negative capacity and spin.
    /// </summary>
    private const int MaxCapacity = int.MaxValue / 2;

    /// <summary>Doubles <paramref name="capacity"/> until it reaches <paramref name="needed"/>.</summary>
    private static int Grow(int capacity, int needed)
    {
        if (needed > MaxCapacity)
        {
            ArraysThrow.Format($"An array arena of {needed} entries cannot be allocated.");
        }

        int grown = Math.Max(capacity, 4);
        while (grown < needed)
        {
            grown *= 2;
        }

        return grown;
    }

    internal void SetRecord(int index, in ArrayNodeRecord record) => _records[index] = record;

    internal int AddNodeBufferIndex(int globalIndex)
    {
        if (_nodeBufferIndexCount == _nodeBufferIndices.Length)
        {
            Array.Resize(ref _nodeBufferIndices, Grow(_nodeBufferIndices.Length, _nodeBufferIndexCount + 1));
        }

        int slot = _nodeBufferIndexCount;
        _nodeBufferIndices[slot] = globalIndex;
        _nodeBufferIndexCount = slot + 1;
        return slot;
    }

    internal int NodeBufferIndexCount => _nodeBufferIndexCount;

    internal int AddStats(in ArrayStatsRecord stats)
    {
        if (_statsCount == _stats.Length)
        {
            Array.Resize(ref _stats, Grow(_stats.Length, _statsCount + 1));
        }

        int slot = _statsCount;
        _stats[slot] = stats;
        _statsCount = slot + 1;
        return slot;
    }

    internal void SetRoot(int index) => _rootIndex = index;
}
