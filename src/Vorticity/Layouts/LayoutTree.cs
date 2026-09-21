using System;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

using Vorticity.Arrays;
using Vorticity.Arrays.Metadata;
using Vorticity.File;
using Vorticity.Serialization.Schemas;
using Vorticity.Types;

namespace Vorticity.Layouts;

/// <summary>
/// The whole layout tree of one file, flattened and parsed a single time when the file is opened.
/// Immutable after <see cref="Parse(VortexFile)"/> and safe for concurrent scans.
/// </summary>
/// <remarks>
/// A layout node carries no dtype on the wire, so every node's dtype is derived top-down at parse
/// time. How a file splits into row groups and columns is the writer's choice, so a reader
/// interprets whatever tree it is given rather than expecting a fixed nesting of layouts.
/// </remarks>
internal sealed class LayoutTree
{
    private readonly VortexFile? _file;
    private readonly string[]? _detachedEncodingIds;
    private readonly byte[] _layoutBytes;
    private readonly LayoutNodeRecord[] _records;
    private readonly int[] _childIndices;
    private readonly ZoneMap[] _zoneMaps;
    private readonly long[] _chunkOffsets;

    private LayoutTree(
        VortexFile? file,
        string[]? detachedEncodingIds,
        DTypeArena types,
        byte[] layoutBytes,
        LayoutNodeRecord[] records,
        int nodeCount,
        int[] childIndices,
        ZoneMap[] zoneMaps,
        long[] chunkOffsets)
    {
        _file = file;
        _detachedEncodingIds = detachedEncodingIds;
        DerivedTypes = types;
        _layoutBytes = layoutBytes;
        _records = records;
        NodeCount = nodeCount;
        _childIndices = childIndices;
        _zoneMaps = zoneMaps;
        _chunkOffsets = chunkOffsets;
    }

    /// <summary>
    /// Parses <see cref="VortexFile.RootLayoutBytes"/> against <see cref="VortexFile.DType"/>.
    /// </summary>
    /// <param name="file">The open file.</param>
    /// <returns>The parsed tree.</returns>
    /// <remarks>
    /// Charges <see cref="VortexLimits.MaxLayoutDepth"/> and a FlatBuffers table budget seeded with
    /// <see cref="VortexLimits.MaxFlatBufferTables"/>, and never throws for an unknown layout id —
    /// an unknown id becomes <see cref="LayoutEncodingId.Unknown"/> and is fatal only when a
    /// projection puts it on the path to data.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="file"/> is null.</exception>
    /// <exception cref="VortexFormatException">The layout tree is malformed.</exception>
    public static LayoutTree Parse(VortexFile file)
    {
        ArgumentNullException.ThrowIfNull(file);

        LayoutTree tree = ParseCore(
            file,
            detachedEncodingIds: null,
            file.RootLayoutBytes.Span,
            file.DType,
            file.SegmentSpecs.Length);

        long rootRows = tree.GetNode(0).RowCount;
        if (rootRows != file.RowCount)
        {
            LayoutsThrow.Format(
                $"The root layout covers {rootRows.ToString(CultureInfo.InvariantCulture)} rows but " +
                $"the file reports {file.RowCount.ToString(CultureInfo.InvariantCulture)}.");
        }

        return tree;
    }

    /// <summary>
    /// Parses a layout tree detached from any file, for tests and tools.
    /// </summary>
    /// <param name="layoutBytes">The <c>Layout</c> FlatBuffer, starting at its root uoffset.</param>
    /// <param name="schema">The dtype the root layout produces.</param>
    /// <param name="layoutEncodingIds">
    /// The footer's <c>layout_specs</c>, in order: entry <c>i</c> is what a node's
    /// <c>encoding == i</c> names.
    /// </param>
    /// <param name="segmentCount">How many entries the footer's <c>segment_specs</c> holds.</param>
    /// <returns>The parsed tree. Its <see cref="File"/> throws; no reader can execute against it.</returns>
    /// <remarks>
    /// The structural rules are worth exercising without forging a whole container, which is why
    /// <c>ScanContext</c> offers a detached constructor for the same reason.
    /// </remarks>
    /// <exception cref="ArgumentNullException">An entry of <paramref name="layoutEncodingIds"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="segmentCount"/> is negative.</exception>
    /// <exception cref="VortexFormatException">The layout tree is malformed.</exception>
    public static LayoutTree Parse(
        ReadOnlySpan<byte> layoutBytes,
        DType schema,
        ReadOnlySpan<string> layoutEncodingIds,
        int segmentCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(segmentCount);

        string[] ids = layoutEncodingIds.Length == 0 ? [] : new string[layoutEncodingIds.Length];
        for (int i = 0; i < layoutEncodingIds.Length; i++)
        {
            ids[i] = layoutEncodingIds[i] ?? throw new ArgumentNullException(nameof(layoutEncodingIds));
        }

        return ParseCore(file: null, ids, layoutBytes, schema, segmentCount);
    }

    /// <summary>The file this tree was parsed from.</summary>
    /// <exception cref="InvalidOperationException">The tree was parsed detached from a file.</exception>
    public VortexFile File => _file ?? DetachedFile();

    /// <summary><see langword="true"/> when this tree is backed by an open file.</summary>
    public bool HasFile => _file is not null;

    /// <summary>How many nodes the flattened tree holds.</summary>
    public int NodeCount { get; }

    /// <summary>The root node, always index 0.</summary>
    public LayoutNode Root => new LayoutNode(this, 0);

    /// <summary>One node of the tree.</summary>
    /// <param name="index">0-based, below <see cref="NodeCount"/>.</param>
    /// <exception cref="VortexFormatException"><paramref name="index"/> is out of range.</exception>
    public LayoutNode GetNode(int index)
    {
        if ((uint)index >= (uint)NodeCount)
        {
            LayoutsThrow.NodeIndex(index, NodeCount);
        }

        return new LayoutNode(this, index);
    }

    /// <summary>
    /// The arena holding every dtype this tree derived: the struct layout's validity <c>Bool</c>,
    /// the dict layout's codes, and the zoned/stats zones tables.
    /// </summary>
    /// <remarks>
    /// Deliberately not the file's arena. A <see cref="DTypeArena"/> mutates when asked for a node
    /// it does not hold, and the file's is shared by every scan; this one is written only during
    /// <c>Parse</c> and read-only afterwards, which is what makes the tree thread-safe. Structural
    /// equality works across arenas, so these compare equal to the schema's nodes.
    /// </remarks>
    public DTypeArena DerivedTypes { get; }

    internal ref readonly LayoutNodeRecord RecordRef(int index) => ref _records[index];

    internal int ChildAt(int slot) => _childIndices[slot];

    /// <summary>
    /// A node's segment ids, reinterpreted in place out of the retained layout buffer.
    /// </summary>
    /// <remarks>
    /// Neither this nor <see cref="MetadataAt"/> copies. A FlatBuffers vector may legally be shared
    /// by several tables, so copying per node would let a 1 MB layout buffer with one large shared
    /// vector cost O(bytes squared) - the same shape of trap as the DAG the node budget bounds.
    /// </remarks>
    internal ReadOnlySpan<uint> SegmentsAt(int byteOffset, int count) =>
        count == 0
            ? default
            : System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(
                _layoutBytes.AsSpan(byteOffset, count * sizeof(uint)));

    internal ReadOnlySpan<byte> MetadataAt(int start, int length) =>
        length == 0 ? default : _layoutBytes.AsSpan(start, length);

    internal ZoneMap ZoneMapAt(int index) => _zoneMaps[index];

    internal ReadOnlySpan<long> ChunkOffsetsAt(int start, int count) =>
        count == 0 ? default : _chunkOffsets.AsSpan(start, count);

    internal string EncodingIdText(ushort specIndex)
    {
        if (_file is not null)
        {
            return _file.GetLayoutEncodingId(specIndex);
        }

        string[] ids = _detachedEncodingIds!;
        return specIndex < ids.Length
            ? ids[specIndex]
            : "<layout spec " + specIndex.ToString(CultureInfo.InvariantCulture) + ">";
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static VortexFile DetachedFile() => LayoutsThrow.Detached<VortexFile>();

    private static LayoutTree ParseCore(
        VortexFile? file,
        string[]? detachedEncodingIds,
        ReadOnlySpan<byte> layoutBytes,
        DType schema,
        int segmentCount)
    {
        if (schema.IsDefault)
        {
            LayoutsThrow.Format("A layout tree cannot be parsed without a schema dtype.");
        }

        // The tree retains the buffer, so every node's metadata and segment ids stay as offsets
        // into it and the parse copies nothing per node. One copy also decouples the tree from the
        // caller's span, which the detached overload does not own.
        byte[] retained = layoutBytes.ToArray();
        Builder builder = new Builder(file, detachedEncodingIds, segmentCount, retained);

        // The budget lives for the whole traversal: forward-only uoffsets exclude cycles but not
        // sharing, and a small buffer whose children are shared between parents is an acyclic graph
        // with exponentially many root-to-leaf paths.
        int tableBudget = VortexLimits.MaxFlatBufferTables;
        LayoutView root = LayoutView.Root(retained, ref tableBudget);
        LayoutParser.ParseNode(builder, in root, schema, depth: 1);

        return builder.Freeze();
    }

    /// <summary>Mutable state for one parse. Discarded once <see cref="Freeze"/> has run.</summary>
    internal sealed class Builder
    {
        private readonly LayoutEncodingId[]? _detachedEncodings;

        internal Builder(VortexFile? file, string[]? detachedEncodingIds, int segmentSpecCount, byte[] layoutBytes)
        {
            File = file;
            DetachedEncodingIds = detachedEncodingIds;
            SegmentSpecCount = segmentSpecCount;
            Buffer = layoutBytes;
            LayoutBytes = layoutBytes.Length;
            InspectionBudget = ((long)layoutBytes.Length * 4) + 4096;

            if (detachedEncodingIds is not null)
            {
                _detachedEncodings = detachedEncodingIds.Length == 0
                    ? []
                    : new LayoutEncodingId[detachedEncodingIds.Length];

                Span<byte> scratch = stackalloc byte[64];
                for (int i = 0; i < detachedEncodingIds.Length; i++)
                {
                    string id = detachedEncodingIds[i];
                    int byteCount = Encoding.UTF8.GetByteCount(id);
                    Span<byte> utf8 = byteCount <= scratch.Length ? scratch : new byte[byteCount];
                    int written = Encoding.UTF8.GetBytes(id, utf8);
                    _detachedEncodings[i] = EncodingRegistry.ResolveLayout(utf8[..written]);
                }
            }

            // Every materialized node but the root is reached through a 4-byte uoffset in some
            // parent's `children` vector, and a distinct visit needs a distinct slot unless a
            // layout table is shared between parents. Bounding the node count by the buffer length
            // therefore bounds the blow-up the FlatBuffers table budget alone would let
            // through - a million materialized records from a three-kilobyte file - while admitting
            // every tree a real writer produces, which spends 40 bytes or more per node.
            MaxNodes = ((long)layoutBytes.Length / 4) + 1;
        }

        internal VortexFile? File { get; }

        internal string[]? DetachedEncodingIds { get; }

        internal int SegmentSpecCount { get; }

        internal int LayoutBytes { get; }

        internal byte[] Buffer { get; }

        internal long MaxNodes { get; }

        /// <summary>
        /// Bytes of file-supplied vectors this parse may still inspect.
        /// </summary>
        /// <remarks>
        /// The node budget bounds how many nodes a shared-children graph can materialize, but not
        /// how much work each one does: a thousand tables may all point at one 500 KB metadata vector,
        /// and parsing it a thousand times is quadratic in the buffer. Charging every metadata and
        /// segment vector against a budget derived from the buffer length keeps the whole parse
        /// linear in it. Four times the buffer is generous: an honest tree's vectors sum to less
        /// than the buffer that contains them.
        /// </remarks>
        internal long InspectionBudget { get; private set; }

        internal void Charge(long bytes)
        {
            InspectionBudget -= bytes;
            if (InspectionBudget < 0)
            {
                LayoutsThrow.TooManyInspectedBytes(LayoutBytes);
            }
        }

        /// <summary>The byte offset of <paramref name="slice"/> within the retained buffer.</summary>
        internal int OffsetOf(ReadOnlySpan<byte> slice)
        {
            if (slice.IsEmpty)
            {
                return 0;
            }

            nint offset = System.Runtime.CompilerServices.Unsafe.ByteOffset(
                ref System.Runtime.InteropServices.MemoryMarshal.GetArrayDataReference(Buffer),
                ref System.Runtime.InteropServices.MemoryMarshal.GetReference(slice));

            if (offset < 0 || offset + slice.Length > Buffer.Length)
            {
                LayoutsThrow.Format("A layout vector does not lie inside the layout buffer.");
            }

            return (int)offset;
        }

        /// <summary>The byte offset of a <c>uint</c> vector within the retained buffer.</summary>
        internal int OffsetOf(ReadOnlySpan<uint> slice) =>
            OffsetOf(System.Runtime.InteropServices.MemoryMarshal.AsBytes(slice));

        internal DTypeArena Types { get; } = new DTypeArena();

        internal AggregateSpecList Specs { get; } = new AggregateSpecList();

        internal LayoutNodeRecord[] Records = new LayoutNodeRecord[16];
        internal int RecordCount;
        internal int[] ChildIndices = new int[16];
        internal int ChildIndexCount;
        internal ZoneMap[] ZoneMaps = new ZoneMap[2];
        internal int ZoneMapCount;
        internal long[] ChunkOffsets = new long[8];
        internal int ChunkOffsetCount;

        /// <summary>
        /// Resolves a wire <c>u16</c> to a layout encoding. An id this build does not know is
        /// <see cref="LayoutEncodingId.Unknown"/> and is not an error; an index outside the
        /// footer's dictionary is a format error, because the file named a spec that does not
        /// exist.
        /// </summary>
        internal LayoutEncodingId ResolveEncoding(ushort specIndex)
        {
            if (File is not null)
            {
                return File.GetLayoutEncoding(specIndex);
            }

            LayoutEncodingId[] encodings = _detachedEncodings!;
            if (specIndex >= encodings.Length)
            {
                LayoutsThrow.Format(
                    $"A layout names spec {specIndex.ToString(CultureInfo.InvariantCulture)}, but " +
                    $"the footer declares {encodings.Length.ToString(CultureInfo.InvariantCulture)}.");
            }

            return encodings[specIndex];
        }

        internal int NewRecord()
        {
            if (RecordCount >= MaxNodes)
            {
                LayoutsThrow.TooManyNodes(MaxNodes, LayoutBytes);
            }

            if (RecordCount == Records.Length)
            {
                Array.Resize(ref Records, Records.Length * 2);
            }

            Records[RecordCount] = default;
            return RecordCount++;
        }

        internal int ReserveChildren(int count)
        {
            if (count == 0)
            {
                return 0;
            }

            int start = ChildIndexCount;
            int needed = ChildIndexCount + count;
            if (needed > ChildIndices.Length)
            {
                int capacity = ChildIndices.Length;
                while (capacity < needed)
                {
                    capacity *= 2;
                }

                Array.Resize(ref ChildIndices, capacity);
            }

            ChildIndexCount = needed;
            return start;
        }

        internal int AddZoneMap(in ZoneMap zoneMap)
        {
            if (ZoneMapCount == ZoneMaps.Length)
            {
                Array.Resize(ref ZoneMaps, ZoneMaps.Length * 2);
            }

            ZoneMaps[ZoneMapCount] = zoneMap;
            return ZoneMapCount++;
        }

        /// <summary>Reserves <paramref name="count"/> cumulative chunk offsets and returns their start.</summary>
        internal int ReserveChunkOffsets(int count)
        {
            int start = ChunkOffsetCount;
            int needed = ChunkOffsetCount + count;
            if (needed > ChunkOffsets.Length)
            {
                int capacity = ChunkOffsets.Length;
                while (capacity < needed)
                {
                    capacity *= 2;
                }

                Array.Resize(ref ChunkOffsets, capacity);
            }

            ChunkOffsetCount = needed;
            return start;
        }

        internal LayoutTree Freeze() =>
            new LayoutTree(
                File,
                DetachedEncodingIds,
                Types,
                Buffer,
                Records,
                RecordCount,
                ChildIndices,
                ZoneMaps,
                ChunkOffsets);
    }
}

/// <summary>One flattened layout node. Managed, because it carries its derived <see cref="DType"/>.</summary>
internal struct LayoutNodeRecord
{
    /// <summary>Derived top-down from the parent; never read from the node.</summary>
    internal DType DType;

    /// <summary>Narrowed from the wire <c>u64</c> at parse time.</summary>
    internal long RowCount;

    /// <summary>Byte offset of the metadata vector inside the retained layout buffer.</summary>
    internal int MetadataStart;

    internal int MetadataLength;

    /// <summary>Index into the tree's child-index list.</summary>
    internal int ChildStart;

    /// <summary>Children in the parsed tree, which is 0 for an unknown layout.</summary>
    internal int ChildCount;

    /// <summary>Byte offset of the segment-id vector inside the retained layout buffer.</summary>
    internal int SegmentStart;

    /// <summary>Segment ids, i.e. <c>uint</c> elements, not bytes.</summary>
    internal int SegmentCount;

    /// <summary>Index into the tree's zone-map list; -1 for every layout but zoned and stats.</summary>
    internal int ZoneMapIndex;

    /// <summary>Index into the tree's cumulative chunk-offset list; -1 unless chunked.</summary>
    internal int ChunkOffsetStart;

    internal ushort EncodingSpecIndex;

    internal LayoutEncodingId Encoding;
}

/// <summary>
/// One node of a <see cref="LayoutTree"/>. A plain readonly struct, not a <c>ref struct</c>: the
/// tree outlives every batch.
/// </summary>
internal readonly struct LayoutNode
{
    private readonly LayoutTree _tree;
    private readonly int _index;

    internal LayoutNode(LayoutTree tree, int index)
    {
        _tree = tree;
        _index = index;
    }

    /// <summary>The tree this node belongs to.</summary>
    public LayoutTree Tree => _tree;

    /// <summary>This node's index in <see cref="Tree"/>.</summary>
    public int Index => _index;

    /// <summary>The resolved layout encoding, or <see cref="LayoutEncodingId.Unknown"/>.</summary>
    public LayoutEncodingId Encoding => _tree.RecordRef(_index).Encoding;

    /// <summary>The raw <c>u16</c> index into the footer's <c>layout_specs</c>.</summary>
    public ushort EncodingSpecIndex => _tree.RecordRef(_index).EncodingSpecIndex;

    /// <summary>The id text, for <see cref="VortexUnsupportedException"/>. Allocates.</summary>
    public string EncodingIdText => _tree.EncodingIdText(_tree.RecordRef(_index).EncodingSpecIndex);

    /// <summary>The dtype this node produces, derived top-down from the parent. Never read from the node.</summary>
    public DType DType => _tree.RecordRef(_index).DType;

    /// <summary>Rows this node covers, narrowed from the wire <c>u64</c> at parse time.</summary>
    public long RowCount => _tree.RecordRef(_index).RowCount;

    /// <summary>The layout-specific metadata, opaque at this layer.</summary>
    public ReadOnlySpan<byte> Metadata
    {
        get
        {
            ref readonly LayoutNodeRecord r = ref _tree.RecordRef(_index);
            return _tree.MetadataAt(r.MetadataStart, r.MetadataLength);
        }
    }

    /// <summary>
    /// How many children this node has <em>in the parsed tree</em>.
    /// </summary>
    /// <remarks>
    /// Zero for a node whose encoding this build does not know: an unknown layout's children have
    /// no derivable dtypes, so they are not materialized. The node is unusable anyway — reaching
    /// it through a projection throws <see cref="VortexUnsupportedException"/>. A
    /// <c>vortex.chunked</c> node whose legacy metadata flag marks child 0 as a statistics table
    /// likewise omits that child, so every child of a chunked node is a chunk.
    /// </remarks>
    public int ChildCount => _tree.RecordRef(_index).ChildCount;

    /// <summary>One child of this node.</summary>
    /// <param name="index">0-based, below <see cref="ChildCount"/>.</param>
    /// <exception cref="VortexFormatException"><paramref name="index"/> is out of range.</exception>
    public LayoutNode GetChild(int index)
    {
        ref readonly LayoutNodeRecord r = ref _tree.RecordRef(_index);
        if ((uint)index >= (uint)r.ChildCount)
        {
            LayoutsThrow.ChildIndex(index, r.ChildCount);
        }

        return new LayoutNode(_tree, _tree.ChildAt(r.ChildStart + index));
    }

    /// <summary>
    /// Indices into <see cref="VortexFile.SegmentSpecs"/>. Every entry was bounds-checked against
    /// the footer's segment count at parse time, so a reader never has to re-check one.
    /// </summary>
    public ReadOnlySpan<uint> Segments
    {
        get
        {
            ref readonly LayoutNodeRecord r = ref _tree.RecordRef(_index);
            return _tree.SegmentsAt(r.SegmentStart, r.SegmentCount);
        }
    }

    /// <summary>The zone map of a <c>vortex.zoned</c> or <c>vortex.stats</c> node.</summary>
    /// <param name="zoneMap">The parsed zone map's shape.</param>
    /// <returns><see langword="false"/> for every other layout.</returns>
    /// <remarks>
    /// The map is exposed but never acted upon here: pruning on it is the caller's business.
    /// </remarks>
    public bool TryGetZoneMap(out ZoneMap zoneMap)
    {
        int slot = _tree.RecordRef(_index).ZoneMapIndex;
        if (slot < 0)
        {
            zoneMap = default;
            return false;
        }

        zoneMap = _tree.ZoneMapAt(slot);
        return true;
    }

    /// <summary>
    /// Cumulative row offsets of a <c>vortex.chunked</c> node's chunks: <c>ChildCount + 1</c>
    /// entries, the first 0 and the last exactly <see cref="RowCount"/>. Empty for every other
    /// layout. Derived once at parse time, because recomputing the prefix sum per batch would be
    /// linear in the chunk count.
    /// </summary>
    internal ReadOnlySpan<long> ChunkOffsets
    {
        get
        {
            ref readonly LayoutNodeRecord r = ref _tree.RecordRef(_index);
            return r.Encoding == LayoutEncodingId.Chunked
                ? _tree.ChunkOffsetsAt(r.ChunkOffsetStart, r.ChildCount + 1)
                : default;
        }
    }

    /// <summary>A short description, for diagnostics. Allocates.</summary>
    public override string ToString()
    {
        StringBuilder text = new StringBuilder();
        text.Append(EncodingIdText)
            .Append(" rows=")
            .Append(RowCount.ToString(CultureInfo.InvariantCulture))
            .Append(" children=")
            .Append(ChildCount.ToString(CultureInfo.InvariantCulture))
            .Append(" dtype=")
            .Append(DType.IsDefault ? "<none>" : DType.ToString());
        return text.ToString();
    }
}
