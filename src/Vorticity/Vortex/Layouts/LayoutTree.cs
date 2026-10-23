using System;
using System.Buffers;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

using Vorticity.Arrays;
using Vorticity.Arrays.Metadata;
using Vorticity.File;
using Vorticity.Serialization.Schemas;
using Vorticity.Types;

namespace Vorticity.Layouts;

/// <summary>
/// The whole layout tree of one file, flattened, parsed once per open file and shared by its scans.
/// Immutable once parsed and safe for concurrent scans.
/// </summary>
/// <remarks>
/// <para>
/// A layout node carries no dtype on the wire, so every node's dtype is derived top-down at parse
/// time. How a file splits into row groups and columns is the writer's choice, so a reader
/// interprets whatever tree it is given rather than expecting a fixed nesting of layouts.
/// </para>
/// <para>
/// A struct root's columns are each a tree of their own, parsed when a walk first reaches them: a
/// scan of one column of a wide file parses that column, where it parsed every chunk of every
/// column. A column's nodes are numbered after the nodes of the columns published before it, so an
/// index names one node of the file whatever order the columns were parsed in, and the order of the
/// tree when they were parsed in order.
/// </para>
/// </remarks>
internal sealed class LayoutTree
{
    // The file, or for a tree detached from any the layout ids its footer would hold.
    private readonly object? _source;
    private readonly byte[] _layoutBytes;
    private readonly LayoutNodeRecord[] _records;
    private readonly int _count;
    private readonly int[] _childIndices;
    private readonly ZoneMap[] _zoneMaps;
    private readonly long[] _chunkOffsets;

    /// <summary>A struct root's columns, parsed as they are reached; null in a column's tree and under any other root.</summary>
    private readonly Columns? _columns;

    /// <summary>The index of this tree's first node in the file's: 0 for the root's, set when a column's is published.</summary>
    private int _indexBase;

    private LayoutTree(
        VortexFile? file,
        string[]? detachedEncodingIds,
        byte[] layoutBytes,
        LayoutNodeRecord[] records,
        int nodeCount,
        int[] childIndices,
        ZoneMap[] zoneMaps,
        long[] chunkOffsets,
        Columns? columns)
    {
        _source = (object?)file ?? detachedEncodingIds;
        _layoutBytes = layoutBytes;
        _records = records;
        _count = nodeCount;
        _childIndices = childIndices;
        _zoneMaps = zoneMaps;
        _chunkOffsets = chunkOffsets;
        _columns = columns;
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

        // The file's own copy of the layout is retained as it is: it lives as long as the file,
        // which the tree holds, and nothing writes it.
        LayoutTree tree = ParseCore(
            file,
            detachedEncodingIds: null,
            file.RootLayoutBytes.Span,
            file.DType,
            file.SegmentSpecs.Length,
            MemoryMarshal.TryGetArray(file.RootLayoutBytes, out ArraySegment<byte> owned) &&
            owned.Offset == 0 && owned.Count == owned.Array!.Length
                ? owned.Array
                : null);

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
        // Parsed whole, in order: a tree detached from a file is one a test or a tool looks at
        // entire, and a malformed column fails here rather than when it is first reached.
        LayoutTree tree = ParseDeferred(layoutBytes, schema, layoutEncodingIds, segmentCount);
        tree.ParseColumns();
        return tree;
    }

    /// <summary>
    /// Parses a layout tree detached from any file as a file's is parsed: a struct root's columns
    /// when a walk first reaches them.
    /// </summary>
    /// <inheritdoc cref="Parse(ReadOnlySpan{byte}, DType, ReadOnlySpan{string}, int)"/>
    internal static LayoutTree ParseDeferred(
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
    public VortexFile File => _source as VortexFile ?? DetachedFile();

    /// <summary><see langword="true"/> when this tree is backed by an open file.</summary>
    public bool HasFile => _source is VortexFile;

    /// <summary>How many nodes the flattened tree holds; the columns not yet parsed are parsed to count them.</summary>
    public int NodeCount
    {
        get
        {
            if (_columns is not { } columns)
            {
                return _count;
            }

            ParseColumns();
            return Volatile.Read(ref columns.NextIndex);
        }
    }

    /// <summary>The root node, always index 0.</summary>
    public LayoutNode Root => new LayoutNode(this, 0);

    /// <summary>The index of this tree's first node among the file's.</summary>
    internal int IndexBase => _indexBase;

    /// <summary>One node of the tree, by its index among the file's; the columns not yet parsed are parsed to find it.</summary>
    /// <param name="index">0-based, below <see cref="NodeCount"/>.</param>
    /// <exception cref="VortexFormatException"><paramref name="index"/> is out of range.</exception>
    public LayoutNode GetNode(int index)
    {
        if ((uint)index < (uint)_count)
        {
            return new LayoutNode(this, index);
        }

        if (_columns is { } columns)
        {
            ParseColumns();
            foreach (LayoutTree? column in columns.Trees)
            {
                int local = index - column!._indexBase;
                if ((uint)local < (uint)column._count)
                {
                    return new LayoutNode(column, local);
                }
            }
        }

        LayoutsThrow.NodeIndex(index, NodeCount);
        return default;
    }

    /// <summary>
    /// The zone length of the first zone map in the tree's order that has one, or 0 when none does.
    /// Under a struct root the columns are parsed in order until one does, and the answer kept.
    /// </summary>
    internal long FirstZoneLength
    {
        get
        {
            if (_columns is not { } columns)
            {
                return FirstZoneLengthUnder(Root);
            }

            long found = Volatile.Read(ref columns.FirstZoneLength);
            if (found < 0)
            {
                found = FirstZoneLengthUnder(Root);
                Volatile.Write(ref columns.FirstZoneLength, found);
            }

            return found;
        }
    }

    // The parse bounded the depth of every tree it built, so the walk is bounded by it too.
    private static long FirstZoneLengthUnder(in LayoutNode node)
    {
        if (node.TryGetZoneMap(out ZoneMap map) && map.ZoneLength > 0)
        {
            return map.ZoneLength;
        }

        for (int i = 0; i < node.ChildCount; i++)
        {
            long found = FirstZoneLengthUnder(node.GetChild(i));
            if (found > 0)
            {
                return found;
            }
        }

        return 0;
    }

    /// <summary>Whether child <paramref name="slot"/> of a struct root was parsed: for the tests of what a walk parses.</summary>
    internal bool ColumnParsed(int slot) => _columns is null || Volatile.Read(ref _columns.Trees[slot]) is not null;

    /// <summary>The root of column <paramref name="slot"/>'s tree, parsed when first asked for.</summary>
    internal LayoutNode Column(int slot) =>
        new LayoutNode(Volatile.Read(ref _columns!.Trees[slot]) ?? ParseColumnOnce(slot), 0);

    /// <summary>
    /// Parses column <paramref name="slot"/> unless a walk is parsing it: that walk's tree, once published, is the one taken.
    /// </summary>
    /// <remarks>
    /// The walks of a file's lanes ask for its columns at once. Each parsed a column of its own, and all but the first
    /// went to the collector, a large array each for a column of a day's object: nine gigabytes of them in twenty seconds
    /// of a question read cold at 32 lanes. One parse a column at a time, the others waiting for it, costs them what they
    /// would have spent parsing; two columns still parse side by side.
    /// </remarks>
    private LayoutTree ParseColumnOnce(int slot)
    {
        lock (_columns!.ParsingGate(slot))
        {
            return Volatile.Read(ref _columns.Trees[slot]) ?? ParseColumn(slot);
        }
    }

    /// <summary>Parses every column not yet parsed, in order.</summary>
    private void ParseColumns()
    {
        if (_columns is { } columns)
        {
            for (int slot = 0; slot < columns.Trees.Length; slot++)
            {
                _ = Column(slot);
            }
        }
    }

    /// <summary>
    /// Parses column <paramref name="slot"/> into a tree of its own and publishes it, numbered after
    /// the columns published before it; a column a racing walk published first is taken instead.
    /// </summary>
    /// <remarks>
    /// The column spends what the root's parse left of its budgets, and what it spent is taken from
    /// them as it is published: the columns of a file parsed one by one spend no more than the
    /// tree parsed whole would, so a layout whose tables are shared between parents is refused as
    /// it was. A parse that loses the race spends nothing of them, and was bounded by them all the
    /// same.
    /// </remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private LayoutTree ParseColumn(int slot)
    {
        Columns columns = _columns!;
        int tables = Volatile.Read(ref columns.TableBudget);
        long inspection = Volatile.Read(ref columns.InspectionBudget);
        int tablesLeft = tables;
        long inspected;
        LayoutTree column;
        Builder builder = Builder.Take(_source as VortexFile, _source as string[], columns.SegmentCount, _layoutBytes);
        try
        {
            builder.Limit(Volatile.Read(ref columns.NodeBudget), inspection);
            builder.Shared = columns;
            LayoutView root = LayoutView.Root(_layoutBytes, ref tablesLeft);
            LayoutParser.ParseColumn(builder, in root, _records[0].DType, slot);
            inspected = inspection - builder.InspectionBudget;
            column = builder.Freeze();
        }
        finally
        {
            builder.Give();
        }

        lock (columns.Gate)
        {
            if (columns.Trees[slot] is { } published)
            {
                return published;
            }

            column._indexBase = columns.NextIndex;
            columns.NextIndex += column._count;
            columns.NodeBudget -= column._count;
            columns.TableBudget -= tables - tablesLeft;
            columns.InspectionBudget -= inspected;
            Volatile.Write(ref columns.Trees[slot], column);
            return column;
        }
    }

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
        if (_source is VortexFile file)
        {
            return file.GetLayoutEncodingId(specIndex);
        }

        string[] ids = (string[])_source!;
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
        int segmentCount,
        byte[]? owned = null)
    {
        if (schema.IsDefault)
        {
            LayoutsThrow.Format("A layout tree cannot be parsed without a schema dtype.");
        }

        // The tree retains the buffer, so every node's metadata and segment ids stay as offsets
        // into it and the parse copies nothing per node. A caller's span is copied once, which
        // decouples the tree from it; an array the caller owns for the tree's life is not.
        byte[] retained = owned ?? layoutBytes.ToArray();
        Builder builder = Builder.Take(file, detachedEncodingIds, segmentCount, retained);
        try
        {
            // The budget lives for the whole traversal, the columns parsed later included:
            // forward-only uoffsets exclude cycles but not sharing, and a small buffer whose
            // children are shared between parents is an acyclic graph with exponentially many
            // root-to-leaf paths.
            int tableBudget = VortexLimits.MaxFlatBufferTables;
            LayoutView root = LayoutView.Root(retained, ref tableBudget);
            builder.DefersColumns = true;
            LayoutParser.ParseNode(builder, in root, schema, depth: 1);

            return builder.Freeze(tableBudget);
        }
        finally
        {
            builder.Give();
        }
    }

    /// <summary>Mutable state for one parse, which the tree copies what it needs out of.</summary>
    /// <remarks>
    /// Parses share one builder and the scratch it grew, taken for a parse and given back after it
    /// holding nothing of it: the tree keeps no reference to its builder, and a parse runs to its
    /// end on the thread that began it. A parse that finds the builder taken makes its own, whose
    /// scratch goes back to the shared pools.
    /// </remarks>
    internal sealed class Builder
    {
        /// <summary>The scratch the shared builder keeps from one parse to the next: what an ordinary tree needs.</summary>
        internal const long KeptScratchBytes = 1L << 20;

        /// <summary>The builder parses share; null while a parse holds it.</summary>
        private static Builder? Cached;

        private LayoutEncodingId[]? _detachedEncodings;

        /// <summary>The scratch the shared builder keeps now, or 0 while a parse holds it.</summary>
        internal static long CachedScratchBytes => Volatile.Read(ref Cached)?.ScratchBytes ?? 0;

        private long ScratchBytes =>
            ((long)Records.Length * Unsafe.SizeOf<LayoutNodeRecord>())
            + ((long)ChildIndices.Length * sizeof(int))
            + ((long)ZoneMaps.Length * Unsafe.SizeOf<ZoneMap>())
            + ((long)ChunkOffsets.Length * sizeof(long))
            + ((long)ZoneAggregates.Length * sizeof(int));

        private Builder()
        {
        }

        /// <summary>The shared builder, or a new one when another parse holds it, ready for a parse.</summary>
        internal static Builder Take(VortexFile? file, string[]? detachedEncodingIds, int segmentSpecCount, byte[] layoutBytes)
        {
            Builder builder = Interlocked.Exchange(ref Cached, null) ?? new Builder();
            builder.Begin(file, detachedEncodingIds, segmentSpecCount, layoutBytes);
            return builder;
        }

        /// <summary>
        /// Forgets the parse and keeps the builder and its scratch for the next one, or gives the
        /// scratch back to the shared pools when another builder is kept already.
        /// </summary>
        internal void Give()
        {
            // The records hold the tree's dtypes and the zone maps its aggregates: cleared, so that
            // the kept scratch keeps neither alive.
            Records.AsSpan(0, RecordCount).Clear();
            ZoneMaps.AsSpan(0, ZoneMapCount).Clear();
            RecordCount = 0;
            ChildIndexCount = 0;
            ZoneMapCount = 0;
            ChunkOffsetCount = 0;
            ZoneAggregateCount = 0;
            DefersColumns = false;
            DeferredColumns = false;
            Shared = null;
            File = null;
            DetachedEncodingIds = null;
            Buffer = [];
            _detachedEncodings = null;
            _types = null;

            // A large tree, or a hostile one refused midway, leaves its scratch to the shared pools,
            // which trim what no rent asks for: kept here, it would stay for the process's life.
            if (ScratchBytes > KeptScratchBytes)
            {
                ReturnScratch();
            }

            if (Interlocked.CompareExchange(ref Cached, this, null) is not null)
            {
                ReturnScratch();
            }
        }

        private void Begin(VortexFile? file, string[]? detachedEncodingIds, int segmentSpecCount, byte[] layoutBytes)
        {
            File = file;
            DetachedEncodingIds = detachedEncodingIds;
            SegmentSpecCount = segmentSpecCount;
            Buffer = layoutBytes;
            LayoutBytes = layoutBytes.Length;
            InspectionBudget = ((long)layoutBytes.Length * 4) + 4096;
            if (Records.Length == 0)
            {
                Records = ArrayPool<LayoutNodeRecord>.Shared.Rent(64);
                ChildIndices = ArrayPool<int>.Shared.Rent(64);
                ZoneMaps = ArrayPool<ZoneMap>.Shared.Rent(16);
                ChunkOffsets = ArrayPool<long>.Shared.Rent(64);
                ZoneAggregates = ArrayPool<int>.Shared.Rent(32);
            }

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

        internal VortexFile? File { get; private set; }

        /// <summary>Whether a struct at the root leaves its children to be parsed as columns of their own.</summary>
        internal bool DefersColumns { get; set; }

        /// <summary>Whether the root left its children to be parsed as columns: the parse's tree carries them.</summary>
        internal bool DeferredColumns { get; set; }

        internal string[]? DetachedEncodingIds { get; private set; }

        internal int SegmentSpecCount { get; private set; }

        internal int LayoutBytes { get; private set; }

        internal byte[] Buffer { get; private set; } = [];

        internal long MaxNodes { get; private set; }

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

        /// <summary>Holds a column's parse to what the parses of the file's tree before it left of the budgets.</summary>
        /// <param name="maxNodes">The nodes it may still make.</param>
        /// <param name="inspectionBudget">The bytes of vectors it may still inspect.</param>
        internal void Limit(long maxNodes, long inspectionBudget)
        {
            MaxNodes = maxNodes;
            InspectionBudget = inspectionBudget;
        }

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

        /// <summary>
        /// The arena every dtype the tree derives is built in: the struct layout's validity
        /// <c>Bool</c>, the dict layout's codes, the list layout's offsets and the zones tables.
        /// Made by the first of them, since many trees derive none.
        /// </summary>
        /// <remarks>
        /// Deliberately not the file's arena. A <see cref="DTypeArena"/> mutates when asked for a
        /// node it does not hold, and the file's is shared by every scan; this one is written only
        /// while the tree is parsed and read-only afterwards, which is what makes the tree
        /// thread-safe. Structural equality works across arenas, so these compare equal to the
        /// schema's nodes. The dtypes keep it alive: the tree holds no reference to it.
        /// </remarks>
        internal DTypeArena Types => _types ??= new DTypeArena();

        private DTypeArena? _types;

        /// <summary>For a column's parse, the columns of its tree, whose zone tables it shares; null for any other parse.</summary>
        internal Columns? Shared { get; set; }

        /// <summary>
        /// The table of the zone map of <paramref name="metadata"/> over <paramref name="column"/>,
        /// whose specs <see cref="Specs"/> holds, and its aggregates packed into <paramref name="aggregates"/>.
        /// </summary>
        /// <returns>Whether the aggregates derive a table at all.</returns>
        /// <remarks>
        /// A column's parse takes the table another column of its tree derived for the same dtype
        /// and zone map, or derives it in an arena of its own and shares it: a table is read by
        /// whatever parses or scans the columns after, on any thread, so nothing may write its
        /// arena once it is shared, and the arena of the parse is written until its column is done.
        /// </remarks>
        internal bool ZoneTable(DType column, ReadOnlySpan<byte> metadata, Span<int> aggregates, out DType table)
        {
            if (Shared is not { } shared)
            {
                return ZoneMapSchema.TryBuildAggregateTable(Types, column, Specs, out table, aggregates);
            }

            if (shared.FindZoneTable(Buffer, column, metadata) is { } known)
            {
                known.Aggregates.CopyTo(aggregates);
                table = known.Table;
                return known.Derived;
            }

            bool derived = ZoneMapSchema.TryBuildAggregateTable(new DTypeArena(8), column, Specs, out table, aggregates);
            shared.AddZoneTable(new ZoneTable(column, OffsetOf(metadata), metadata.Length, derived, table, aggregates.ToArray()));
            return derived;
        }

        /// <summary>The specs of the zone map being parsed, each zone map's read and used up before the next one's.</summary>
        internal AggregateSpecList Specs => _specs ??= new AggregateSpecList();

        private AggregateSpecList? _specs;

        // The parse grows these in rented arrays, and the tree keeps exact copies of them: a tree's
        // size is known only once it is parsed, and growing its own arrays by doubling left three
        // or four discarded generations of each behind every open.
        internal LayoutNodeRecord[] Records = [];
        internal int RecordCount;
        internal int[] ChildIndices = [];
        internal int ChildIndexCount;
        internal ZoneMap[] ZoneMaps = [];
        internal int ZoneMapCount;
        internal long[] ChunkOffsets = [];
        internal int ChunkOffsetCount;

        /// <summary>The aggregates of every zone map, packed as <see cref="ZoneMap.Pack"/> packs them.</summary>
        internal int[] ZoneAggregates = [];
        internal int ZoneAggregateCount;

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
                Grow(ref Records, RecordCount + 1);
            }

            Records[RecordCount] = default;
            return RecordCount++;
        }

        /// <summary>Moves <paramref name="array"/> into a rented array of at least <paramref name="needed"/> entries and returns the old one.</summary>
        private static void Grow<T>(ref T[] array, int needed)
        {
            int capacity = Math.Max(array.Length, 1);
            while (capacity < needed)
            {
                capacity *= 2;
            }

            T[] grown = ArrayPool<T>.Shared.Rent(capacity);
            array.AsSpan().CopyTo(grown);
            ArrayPool<T>.Shared.Return(array, clearArray: RuntimeHelpers.IsReferenceOrContainsReferences<T>());
            array = grown;
        }

        /// <summary>Gives the rented arrays back; the tree holds copies of what they held. Safe to call twice.</summary>
        private void ReturnScratch()
        {
            Return(ref Records);
            Return(ref ChildIndices);
            Return(ref ZoneMaps);
            Return(ref ChunkOffsets);
            Return(ref ZoneAggregates);
        }

        private static void Return<T>(ref T[] array)
        {
            if (array.Length != 0)
            {
                ArrayPool<T>.Shared.Return(array, clearArray: RuntimeHelpers.IsReferenceOrContainsReferences<T>());
                array = [];
            }
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
                Grow(ref ChildIndices, needed);
            }

            ChildIndexCount = needed;
            return start;
        }

        internal int AddZoneMap(in ZoneMap zoneMap)
        {
            if (ZoneMapCount == ZoneMaps.Length)
            {
                Grow(ref ZoneMaps, ZoneMapCount + 1);
            }

            ZoneMaps[ZoneMapCount] = zoneMap;
            return ZoneMapCount++;
        }

        /// <summary>Reserves the <paramref name="count"/> aggregates of a zone map and returns their start.</summary>
        internal int ReserveZoneAggregates(int count)
        {
            int start = ZoneAggregateCount;
            int needed = start + count;
            if (needed > ZoneAggregates.Length)
            {
                Grow(ref ZoneAggregates, needed);
            }

            ZoneAggregateCount = needed;
            return start;
        }

        /// <summary>Reserves <paramref name="count"/> cumulative chunk offsets and returns their start.</summary>
        internal int ReserveChunkOffsets(int count)
        {
            int start = ChunkOffsetCount;
            int needed = ChunkOffsetCount + count;
            if (needed > ChunkOffsets.Length)
            {
                Grow(ref ChunkOffsets, needed);
            }

            ChunkOffsetCount = needed;
            return start;
        }

        /// <summary>The tree, holding exact copies of what the parse gathered.</summary>
        /// <param name="tablesLeft">What the parse left of its table budget, for the columns it left to parse.</param>
        internal LayoutTree Freeze(int tablesLeft = 0)
        {
            // The zone maps read their aggregates from the tree's copy of the table, which only
            // exists now.
            int[] aggregates = ZoneAggregates.AsSpan(0, ZoneAggregateCount).ToArray();
            ZoneMap[] zoneMaps = ZoneMapCount == 0 ? [] : new ZoneMap[ZoneMapCount];
            for (int i = 0; i < zoneMaps.Length; i++)
            {
                zoneMaps[i] = ZoneMaps[i].WithAggregates(aggregates);
            }

            Columns? columns = DeferredColumns
                ? new Columns(Records[0].ChildCount, RecordCount, SegmentSpecCount, tablesLeft, MaxNodes - RecordCount, InspectionBudget)
                : null;

            return new LayoutTree(
                File,
                DetachedEncodingIds,
                Buffer,
                Records.AsSpan(0, RecordCount).ToArray(),
                RecordCount,
                ChildIndices.AsSpan(0, ChildIndexCount).ToArray(),
                zoneMaps,
                ChunkOffsets.AsSpan(0, ChunkOffsetCount).ToArray(),
                columns);
        }
    }

    /// <summary>A struct root's columns, and what the parse of the root left them of its budgets.</summary>
    internal sealed class Columns(int count, int firstIndex, int segmentCount, int tableBudget, long nodeBudget, long inspectionBudget)
    {
        /// <summary>Per child of the root, its tree once parsed.</summary>
        internal readonly LayoutTree?[] Trees = new LayoutTree?[count];

        /// <summary>Taken to publish a column, or a zone table one derived: its numbering and the budgets it spent are the tree's.</summary>
        internal readonly Lock Gate = new();

        // The lock each column's parse is taken under, made the first time the column is parsed.
        private readonly object?[] _parsing = new object?[count];

        /// <summary>The lock the parse of column <paramref name="slot"/> is taken under, the same for every walk.</summary>
        internal object ParsingGate(int slot) =>
            Volatile.Read(ref _parsing[slot]) ?? Interlocked.CompareExchange(ref _parsing[slot], new object(), null) ?? _parsing[slot]!;

        internal readonly int SegmentCount = segmentCount;

        /// <summary>The index the next column published numbers its nodes from.</summary>
        internal int NextIndex = firstIndex;

        internal int TableBudget = tableBudget;
        internal long NodeBudget = nodeBudget;
        internal long InspectionBudget = inspectionBudget;

        /// <summary>The tree's <see cref="LayoutTree.FirstZoneLength"/>; -1 until asked.</summary>
        internal long FirstZoneLength = -1;

        /// <summary>The zone tables the columns derived, replaced whole by each one added.</summary>
        private ZoneTable[] _zoneTables = [];

        /// <summary>
        /// The zone table a column derived for the zone map of <paramref name="metadata"/> over
        /// <paramref name="column"/>, or null when none has: the columns of a file share a handful
        /// of them, where each derived its own in an arena of its own.
        /// </summary>
        internal ZoneTable? FindZoneTable(byte[] buffer, DType column, ReadOnlySpan<byte> metadata)
        {
            foreach (ZoneTable table in Volatile.Read(ref _zoneTables))
            {
                if (buffer.AsSpan(table.MetadataStart, table.MetadataLength).SequenceEqual(metadata) && table.Column.Equals(column))
                {
                    return table;
                }
            }

            return null;
        }

        /// <summary>Shares a zone table a column derived with the columns parsed after it.</summary>
        internal void AddZoneTable(ZoneTable table)
        {
            lock (Gate)
            {
                _zoneTables = [.. _zoneTables, table];
            }
        }
    }

    /// <summary>
    /// The zone table one zone map derived: its column's dtype and the zone map's metadata, which
    /// derive it whole, and what they derived. Its dtype is built in an arena nothing writes after.
    /// </summary>
    internal sealed class ZoneTable(DType column, int metadataStart, int metadataLength, bool derived, DType table, int[] aggregates)
    {
        internal DType Column { get; } = column;

        internal int MetadataStart { get; } = metadataStart;

        internal int MetadataLength { get; } = metadataLength;

        /// <summary>Whether the aggregates derived a table at all.</summary>
        internal bool Derived { get; } = derived;

        internal DType Table { get; } = table;

        /// <summary>The aggregates as <see cref="ZoneMap.Pack"/> packs them, each with its column in the table.</summary>
        internal int[] Aggregates { get; } = aggregates;
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

    /// <summary>A <c>vortex.flat</c> whose inlined array tree declares no buffer; see <see cref="LayoutNode.Bufferless"/>.</summary>
    internal bool Bufferless;
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

    /// <summary>This node's index among the file's: what names it to the caches a scan keeps by node.</summary>
    public int Index => _tree.IndexBase + _index;

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

    /// <summary>
    /// Whether this is a <c>vortex.flat</c> whose metadata inlines an array tree that declares no
    /// buffer: a sequence, say, which its tree holds whole. A read of it needs no byte of its
    /// segment, so a scan never asks for one.
    /// </summary>
    public bool Bufferless => _tree.RecordRef(_index).Bufferless;

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

        // A struct root's children are columns of their own, which their slot names: ~slot.
        int child = _tree.ChildAt(r.ChildStart + index);
        return child >= 0 ? new LayoutNode(_tree, child) : _tree.Column(~child);
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
