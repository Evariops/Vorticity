using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text;
using Vorticity.Buffers;
using Vorticity.File;
using Vorticity.IO;
using Vorticity.Types;

namespace Vorticity.Arrays;

/// <summary>
/// Everything one decode flow owns: the three per-batch arenas, the scalar store, the dtype
/// arena, the read options and the file's encoding table.
/// </summary>
/// <remarks>
/// <para>
/// Not thread-safe, deliberately. A context is affine to a single consumer and is never shared:
/// concurrent scans over one open <see cref="VortexFile"/> are supported and expected, and each
/// concurrent split gets its own context, with its own arenas.
/// </para>
/// <para>
/// Every index this context hands out - an <see cref="ArrayNodeArena"/> node index, a
/// <see cref="CanonicalArena"/> node index - is meaningful only until the next
/// <see cref="ResetBatch"/>. Never store one in a field or across a batch boundary.
/// </para>
/// </remarks>
internal sealed class ScanContext : IDisposable
{
    private readonly VortexFile? _file;
    private readonly ArrayEncodingId[] _arrayEncodings;
    private readonly string[] _arrayEncodingIds;
    private bool _disposed;

    /// <summary>The arena capacity a context that will decode batches starts from.</summary>
    private const int ScanCapacity = 64;

    /// <summary>
    /// The arena capacity for a context that reads metadata and nothing else.
    /// </summary>
    /// <remarks>
    /// A zone map is a struct of a few aggregate columns with one row per zone, and the context
    /// that reads it is thrown away straight afterwards. Sized for a batch instead, the three
    /// arenas that take a capacity -- <see cref="ArrayNodeArena"/>, <see cref="CanonicalArena"/>
    /// and <see cref="DTypeArena"/> -- dominate everything pruning costs over not pruning.
    ///
    /// Eight rather than four, because the arenas double when they fill and a zone map of a struct
    /// with three aggregates already needs five or six nodes: undersizing would trade one
    /// allocation for two. Nothing about correctness depends on the number -- an arena that fills
    /// grows -- so this is a starting point and not a bound.
    /// </remarks>
    internal const int MetadataCapacity = 8;

    /// <summary>Creates a scan context over an open file.</summary>
    /// <param name="file">The file being scanned; its encoding table and read options are copied in.</param>
    /// <exception cref="ArgumentNullException"><paramref name="file"/> is null.</exception>
    public ScanContext(VortexFile file)
        : this(file, ScanCapacity)
    {
    }

    /// <summary>Creates a scan context whose arenas start at <paramref name="capacity"/>.</summary>
    /// <param name="file">The file being scanned.</param>
    /// <param name="capacity">Initial node capacity; see <see cref="MetadataCapacity"/>.</param>
    internal ScanContext(VortexFile file, int capacity)
    {
        ArgumentNullException.ThrowIfNull(file);
        _file = file;
        Options = file.ReadOptions;

        int count = file.ArrayEncodingCount;
        _arrayEncodings = count == 0 ? [] : new ArrayEncodingId[count];

        // Deliberately not materialized. VortexFile keeps only each id's location in the footer,
        // because FlatBuffers strings may be shared: a footer may legally point a great many spec
        // entries at one long id, so interning them all would allocate orders of magnitude more
        // than reading the file costs. Interning them here instead would move that cost onto the
        // first scan and multiply it by the degree of parallelism, because every lane builds its
        // own context. The four-byte resolved ids below are bounded; the text is fetched from the
        // file only by a throw site.
        _arrayEncodingIds = [];
        for (int i = 0; i < count; i++)
        {
            _arrayEncodings[i] = file.GetArrayEncoding(i);
        }

        // `DTypeArena`'s own default is narrower than a scan capacity, so the value is capped
        // rather than widened: a full scan keeps the dtype capacity the arena chooses for itself.
        Types = new DTypeArena(Math.Min(capacity, 16));
        Scalars = new ScalarStore();

        // A context that decodes batches starts its tree buffer at the largest array tree such a
        // context of the file has loaded: a lane whose first decode of its own comes late in the
        // scan, every earlier chunk having been decoded by another lane, would otherwise grow it
        // then. A context that reads metadata loads other trees and is thrown away, so it neither
        // takes the hint nor gives one.
        _notesTrees = capacity >= ScanCapacity;
        Nodes = new ArrayNodeArena(capacity, _notesTrees ? file.LargestArrayTree : 0);
        _batchCanonical = new CanonicalArena(capacity);

        // Deliberately not sized from the arena capacity. A batch registers one segment per leaf it
        // reads, which the set's own default already covers, so widening it to the arena capacity
        // would cost the extra slots on every read and avoid no growth. Sizing it from the
        // projection's leaf count would only pay off on a file wide enough to grow it.
        Segments = new SegmentRequestSet();
        Decode = new ArrayDecodeContext(this);
    }

    /// <summary>
    /// Creates a scan context detached from any file, over an explicit encoding table.
    /// </summary>
    /// <param name="arrayEncodingIds">
    /// The footer's <c>array_specs</c>, in order: entry <c>i</c> is what a node's
    /// <c>encoding == i</c> names. Over-reporting is normal and never an error: a writer may
    /// pre-populate every id its edition permits, whether or not the file uses them.
    /// </param>
    /// <param name="options">Read-time policy; <see cref="VortexReadOptions.Default"/> when null.</param>
    /// <remarks>
    /// This is the constructor tests and tools use. <see cref="File"/> throws for a context built
    /// this way; everything else behaves identically.
    /// </remarks>
    /// <exception cref="ArgumentNullException">An entry of <paramref name="arrayEncodingIds"/> is null.</exception>
    public ScanContext(ReadOnlySpan<string> arrayEncodingIds, VortexReadOptions? options = null)
    {
        _file = null;
        Options = options ?? VortexReadOptions.Default;

        int count = arrayEncodingIds.Length;
        _arrayEncodings = count == 0 ? [] : new ArrayEncodingId[count];
        _arrayEncodingIds = count == 0 ? [] : new string[count];

        // Hoisted out of the loop: a stackalloc inside one would grow the frame per iteration.
        Span<byte> scratch = stackalloc byte[64];
        for (int i = 0; i < count; i++)
        {
            string id = arrayEncodingIds[i] ?? throw new ArgumentNullException(nameof(arrayEncodingIds));
            _arrayEncodingIds[i] = id;

            // One transcode per entry, once per scan: the ids are tens of entries and the matcher
            // itself never allocates.
            int byteCount = Encoding.UTF8.GetByteCount(id);
            Span<byte> utf8 = byteCount <= scratch.Length ? scratch : new byte[byteCount];
            int written = Encoding.UTF8.GetBytes(id, utf8);
            _arrayEncodings[i] = EncodingRegistry.ResolveArray(utf8[..written]);
        }

        Types = new DTypeArena();
        Scalars = new ScalarStore();
        Nodes = new ArrayNodeArena();
        _batchCanonical = new CanonicalArena();
        Segments = new SegmentRequestSet();
        Decode = new ArrayDecodeContext(this);
    }

    /// <summary><see langword="true"/> when this context was built over a <see cref="VortexFile"/>.</summary>
    public bool HasFile => _file is not null;

    private readonly bool _notesTrees;

    /// <summary>Tells the file how large an array tree this context has loaded, for the contexts made after it.</summary>
    internal void NoteArrayTree()
    {
        if (_notesTrees)
        {
            _file!.NoteArrayTree(Nodes.TreeCapacity);
        }
    }

    private int[]? _selection;
    private int _selectionCount;

    /// <summary>The file being scanned.</summary>
    /// <exception cref="InvalidOperationException">The context was built detached from a file.</exception>
    public VortexFile File => _file ?? ThrowDetached();

    /// <summary>Read-time policy for this scan.</summary>
    public VortexReadOptions Options { get; }

    /// <summary>
    /// The arena every DType derived during decoding is built in: the validity child's
    /// non-nullable <c>Bool</c>, a <c>Primitive(ptype, NonNullable)</c> read out of metadata, and
    /// so on.
    /// </summary>
    /// <remarks>
    /// It is deliberately <em>not</em> the file's arena. A <see cref="DTypeArena"/> mutates when it
    /// is asked for a node it does not already hold, and two concurrent scans deriving into one
    /// arena would race on its arrays. Structural equality and hashing work across arenas, so a
    /// DType built here compares equal to the schema's. It is not cleared per batch: the arena
    /// deduplicates, and the set of derivable dtypes is bounded by the schema's shape.
    /// </remarks>
    public DTypeArena Types { get; }

    /// <summary>
    /// The batch's scalar store. Cleared by <see cref="ResetBatch"/>, so a
    /// <see cref="ScalarValue"/> read out of one batch is meaningless in the next - the same
    /// lifetime rule the arenas follow.
    /// </summary>
    public ScalarStore Scalars { get; }

    /// <summary>One <see cref="ArrayNodeRecord"/> per serialized node, plus the resolved buffer table.</summary>
    public ArrayNodeArena Nodes { get; }

    /// <summary>One record per decoded node.</summary>
    /// <remarks>
    /// Usually the batch's arena, and briefly not. A chunk larger than a batch is decoded into the
    /// arena that will retain it, so no copy is needed to move it there --
    /// <see cref="BeginRetainedDecode"/> redirects this property for the duration of that one
    /// decode and <see cref="EndRetainedDecode"/> puts it back. The redirect is never observable
    /// from outside a single <c>LayoutReader.Execute</c> call, and every node the redirected decode
    /// produces is reachable only through the index that call returns.
    /// </remarks>
    public CanonicalArena Canonical => _building?.Arena ?? _batchCanonical;

    /// <summary>The batch's own arena, which <see cref="ResetBatch"/> clears.</summary>
    private readonly CanonicalArena _batchCanonical;

    /// <summary>
    /// Set while a chunk is being decoded straight into the arena that will retain it: the entry it
    /// will become, which gathers the segments the decode depends on as it runs.
    /// </summary>
    private RetainedChunk? _building;

    /// <summary>
    /// The <see cref="Vorticity.Buffers.SegmentOwner"/> references for this batch's segments. Exactly one refcount
    /// per segment per batch: the source hands back an owner whose count already includes this
    /// reference, and <see cref="SegmentRequestSet.Release"/> drops it once. Decoders never
    /// <c>Retain</c> or <c>Release</c>.
    /// </summary>
    public SegmentRequestSet Segments { get; }

    /// <summary>The decode facade handed to every decoder.</summary>
    public ArrayDecodeContext Decode { get; }

    /// <summary>Maps a wire <c>u16</c> encoding spec index to a resolved id.</summary>
    public ReadOnlySpan<ArrayEncodingId> ArrayEncodings => _arrayEncodings;

    /// <summary>How many entries the footer's <c>array_specs</c> declared.</summary>
    public int ArrayEncodingCount => _arrayEncodings.Length;

    /// <summary>
    /// The id text of encoding spec <paramref name="specIndex"/>, for a diagnostic message.
    /// </summary>
    /// <remarks>
    /// Materialized on demand, never at construction: see the file-backed constructor. The file
    /// owns its footer window for its whole life, so a throw site can always ask it for one id -
    /// which is exactly what <see cref="VortexFile.GetArrayEncodingId"/> exists to do. Callers on
    /// a decode path must therefore reach the decoder through
    /// <c>ArrayDecoderTable.Require</c>, which asks for the text only when it is about to throw.
    /// </remarks>
    /// <param name="specIndex">The wire <c>u16</c>.</param>
    /// <returns>The id, or a placeholder when the index is outside the declared table.</returns>
    public string GetArrayEncodingIdText(int specIndex)
    {
        if (_file is not null)
        {
            return (uint)specIndex < (uint)_arrayEncodings.Length
                ? _file.GetArrayEncodingId(specIndex)
                : Placeholder(specIndex);
        }

        return (uint)specIndex < (uint)_arrayEncodingIds.Length
            ? _arrayEncodingIds[specIndex]
            : Placeholder(specIndex);
    }

    private static string Placeholder(int specIndex) =>
        "<encoding spec " + specIndex.ToString(System.Globalization.CultureInfo.InvariantCulture) + ">";

    /// <summary>
    /// The rows wanted out of the batch, in the coordinate space of the <c>Execute</c> call that is
    /// running, or empty for "every row".
    /// </summary>
    /// <remarks>
    /// Carried on the context rather than passed as a parameter, because `LayoutReader.Execute` is
    /// a public extension point: threading a new argument through it would be a breaking change
    /// for an out-of-tree reader, and every reader already receives the context.
    ///
    /// The coordinate space is the invariant. The selection lives in exactly the space its sibling
    /// `rows` argument lives in, so a reader that re-bases `rows` must re-base this too, and a
    /// reader that passes `rows` through unchanged passes this through by doing nothing. Only
    /// `vortex.chunked` re-partitions rows; struct, zoned and flat do not.
    /// </remarks>
    internal ReadOnlySpan<int> Selection =>
        _selection is null ? default : _selection.AsSpan(0, _selectionCount);

    /// <summary>Whether a selection is in force for this batch.</summary>
    internal bool HasSelection => _selection is not null;

    /// <summary>
    /// The scan's mask of live blocks, in the file's row coordinates, or
    /// <see langword="null"/> when every block is live.
    /// </summary>
    /// <remarks>
    /// Per scan, not per batch: set once by the enumerator and never touched by
    /// <see cref="ResetBatch"/>. Carried on the context for the same reason the selection is --
    /// <c>LayoutReader.Execute</c> is a public extension point. Unlike the selection it is not
    /// re-based: a reader that re-partitions rows (<c>vortex.chunked</c>) clears it for its
    /// children and expresses what it means in the selection instead, so that a reader below it
    /// never reads file coordinates as its own. A reader that sees it set may take its
    /// <c>rows</c> to be file rows.
    /// </remarks>
    internal Compute.BlockMask? LiveBlocks { get; set; }

    /// <summary>
    /// The scan's metrics sink, or <see langword="null"/> when nobody asked. Per
    /// scan like <see cref="LiveBlocks"/>: set once per lane, never by <see cref="ResetBatch"/>,
    /// and the readers add to it what they materialize.
    /// </summary>
    internal Scanning.ScanMetrics? Metrics { get; set; }

    /// <summary>
    /// Whether a column's own node may stay a dictionary or run-end node rather than be decoded to
    /// canonical form. Per scan like <see cref="Metrics"/>, false unless the consumer reads the
    /// encoded form; a layout reader clears it for a child that is not its rows, since a codes or
    /// offsets child is read as plain values.
    /// </summary>
    internal bool KeepEncodings { get; set; }

    /// <summary>
    /// Replaces the selection and returns what was there, for a reader that re-bases it per child.
    /// </summary>
    /// <param name="rows">The new selection, or <see langword="null"/> to clear it.</param>
    /// <param name="count">How many entries of <paramref name="rows"/> are live.</param>
    /// <returns>The previous buffer and count, to be restored by the caller.</returns>
    internal (int[]? Buffer, int Count) ExchangeSelection(int[]? rows, int count)
    {
        (int[]? Buffer, int Count) previous = (_selection, _selectionCount);
        _selection = rows;
        _selectionCount = rows is null ? 0 : count;
        return previous;
    }

    /// <summary>The pushed comparison, held behind one reference rather than in three fields.</summary>
    /// <remarks>
    /// A <see cref="Expressions.FilterLiteral"/> is a wide value and this context is allocated once
    /// per lane of every scan, filtered or not: inline, the three fields cost thirty-two bytes on
    /// every scan in the process to serve the few that push. Behind a reference they cost eight,
    /// and the holder is allocated once for a scan that pushes and reused by each of its splits.
    /// </remarks>
    private sealed class Pushed
    {
        internal byte[]? Field;
        internal Expressions.ComparisonOp Op;
        internal Expressions.FilterLiteral Literal;
        internal Layouts.FieldMask Fields;
        internal bool FieldsHonoured;
    }

    private Pushed? _pushed;

    /// <summary>
    /// The comparison this pass may let an encoding answer instead of decoding, or
    /// <see langword="null"/> when there is none.
    /// </summary>
    /// <remarks>
    /// Carried here for the selection's reason: <c>LayoutReader.Execute</c> is a public extension
    /// point and the readers are the only place that holds a serialized node and its decoder at the
    /// same time. It names the field rather than resolving it, because which layout child is that
    /// field is the struct reader's business and no one else's.
    /// </remarks>
    internal ReadOnlySpan<byte> PushedField => _pushed?.Field;

    /// <summary>The comparison of <see cref="PushedField"/>.</summary>
    internal Expressions.ComparisonOp PushedOp => _pushed!.Op;

    /// <summary>The right-hand side of <see cref="PushedField"/>'s comparison.</summary>
    internal Expressions.FilterLiteral PushedLiteral => _pushed!.Literal;

    /// <summary>
    /// Whether the reader running right now is inside the column <see cref="PushedField"/> names.
    /// </summary>
    /// <remarks>
    /// Set by the struct reader around the matching child and cleared again after it, so that the
    /// leaf below it knows the node it holds is the predicate's without re-deriving the path. A
    /// reader that executes a child which is not part of the column's values -- a zone map, say --
    /// clears it for that child, or the leaf would answer the predicate over a table of statistics.
    /// </remarks>
    internal bool PredicateAtNode { get; set; }

    /// <summary>
    /// Whether an encoding answered <see cref="PushedField"/>'s comparison during this pass.
    /// </summary>
    /// <remarks>
    /// The driver reads it to know which of the two shapes the pass produced, and it is the
    /// encoding's own answer rather than a guess: a column can only be asked once its blob is
    /// parsed, which happens inside the reader and not where the pass is decided.
    /// </remarks>
    internal bool PredicateAnswered { get; set; }

    /// <summary>
    /// The projection a struct decode may honour itself, instead of decoding every field and
    /// narrowing afterwards.
    /// </summary>
    /// <remarks>
    /// A struct layout reads only the fields a projection names. A struct stored as one array node
    /// has no layout to do that, so without this the whole of it is decoded and all but one column
    /// thrown away, which on a wide file costs slightly more than not projecting at all. It rides
    /// on the holder the pushed comparison already allocates, so a scan that projects nothing pays
    /// nothing for it.
    /// </remarks>
    internal Layouts.FieldMask PushedFields =>
        _pushed is null ? Layouts.FieldMask.All : _pushed.Fields;

    /// <summary>Whether a struct decode consumed <see cref="PushedFields"/> and narrowed itself.</summary>
    internal bool FieldsHonoured
    {
        get => _pushed is not null && _pushed.FieldsHonoured;
        set => (_pushed ??= new Pushed()).FieldsHonoured = value;
    }

    /// <summary>Replaces the pushed projection and returns what was there, for the caller to restore.</summary>
    /// <param name="fields">The projection, or <see cref="Layouts.FieldMask.All"/> to clear it.</param>
    /// <returns>The previous projection.</returns>
    internal Layouts.FieldMask ExchangePushedFields(Layouts.FieldMask fields)
    {
        Pushed held = _pushed ??= new Pushed();
        Layouts.FieldMask previous = held.Fields;
        held.Fields = fields;
        return previous;
    }

    /// <summary>
    /// Replaces the pushed comparison and returns what was there, for the driver to restore.
    /// </summary>
    /// <param name="field">The field's name, UTF-8, or <see langword="null"/> to clear it.</param>
    /// <param name="op">The comparison.</param>
    /// <param name="literal">Its right-hand side.</param>
    /// <returns>The previous triple.</returns>
    internal (byte[]? Field, Expressions.ComparisonOp Op, Expressions.FilterLiteral Literal)
        ExchangePushedPredicate(
            byte[]? field, Expressions.ComparisonOp op, Expressions.FilterLiteral literal)
    {
        Pushed held = _pushed ??= new Pushed();
        (byte[]? Field, Expressions.ComparisonOp Op, Expressions.FilterLiteral Literal) previous =
            (held.Field, held.Op, held.Literal);
        held.Field = field;
        held.Op = op;
        held.Literal = literal;
        return previous;
    }

    /// <summary>
    /// Decoded chunks held across the batches carved out of them. Never cleared by
    /// <see cref="ResetBatch"/>; that is the whole point.
    /// </summary>
    /// <remarks>
    /// The context's own unless a scan <see cref="ShareRetained"/>s one table between every context
    /// it runs -- its lanes and its key source -- so that a chunk is decoded once per scan rather
    /// than once per context that meets it. Created at the first claim: a file whose chunk is its
    /// batch never retains anything, and the scan paths hold tight allocation ceilings.
    /// </remarks>
    private RetainedChunks? _retained;

    /// <summary>Whether this context owns <see cref="_retained"/>, and so advances its floor and disposes it.</summary>
    private bool _ownsRetained = true;

    /// <summary>The claim this context holds on a chunk it has not decoded yet, or null.</summary>
    private RetainedChunk? _claim;

    /// <summary>
    /// The number of the batch in flight, in the order the scan walks its rows. A retained chunk
    /// records the last batch that borrowed it, and is evicted only once the scan has moved past it.
    /// </summary>
    /// <remarks>
    /// Set by the scan that drives this context, per split; a context running alone counts its own
    /// batches through <see cref="ResetBatch"/>.
    /// </remarks>
    internal long Batch { get; set; }

    /// <summary>The table of retained chunks, created at the first claim; a context running alone reads a few columns.</summary>
    private RetainedChunks Retained => _retained ??= new RetainedChunks(columns: 3, lanes: 1);

    /// <summary>The retained decode for <paramref name="key"/>, if one is published; no claim is made.</summary>
    /// <param name="key">From <see cref="SegmentKey"/>, <see cref="LayoutKey"/> or <see cref="ChildKey"/>.</param>
    /// <param name="arena">The arena holding it. The caller may borrow from this until eviction.</param>
    /// <param name="nodeIndex">The node's index in <paramref name="arena"/>.</param>
    /// <returns><see langword="true"/> when this key is retained.</returns>
    /// <remarks>
    /// For a reader that decides only after a look at the node whether it decodes the chunk at all:
    /// a claim made and given back on every batch of a chunk the encoding serves without a decode
    /// would be an entry churned per batch.
    /// </remarks>
    internal bool TryPeekRetained(long key, out CanonicalArena arena, out int nodeIndex)
    {
        if (_retained is not null)
        {
            return _retained.Peek(key, Batch, out arena, out nodeIndex);
        }

        arena = null!;
        nodeIndex = -1;
        return false;
    }

    /// <summary>
    /// Makes this context retain into <paramref name="shared"/>, a table another context of the same
    /// scan may retain into too. The scan that shares it advances its floor and disposes it.
    /// </summary>
    /// <param name="shared">The scan's table.</param>
    internal void ShareRetained(RetainedChunks shared)
    {
        ArgumentNullException.ThrowIfNull(shared);
        if (_retained is not null)
        {
            throw new InvalidOperationException("This scan context already retains chunks of its own.");
        }

        _retained = shared;
        _ownsRetained = false;
    }

    /// <summary>
    /// The retention key for a flat layout, which its segment identifies within the file.
    /// </summary>
    /// <param name="segmentId">The segment.</param>
    internal static long SegmentKey(uint segmentId) => segmentId;

    /// <summary>
    /// How many validated nodes are remembered at once. One per column that carries a side table
    /// needing an O(n) check, which the schema bounds; past it, nodes are simply re-checked.
    /// </summary>
    private const int CheckedNodeSlots = 8;

    /// <summary>
    /// Keys of the nodes whose O(n) validation has already passed: the open scope in slot 0 and the
    /// passed checks in slots 1 and up, each stored as its value plus one so that 0 means empty.
    /// Null until a scope is opened.
    /// </summary>
    /// <remarks>
    /// One field, and allocated on first use, because the scan paths hold tight allocation
    /// ceilings. An inline array of keys, a count and a nullable segment id would be three struct
    /// fields on every <see cref="ScanContext"/> whether or not a scan ever opens a scope, and
    /// moving one of them to <c>ArrayDecodeContext</c> only shifts the cost onto a smaller object.
    /// As one reference, allocated only when a reader opens a scope -- which only a take on a node
    /// bigger than its batch does -- a full scan pays nothing at all.
    /// </remarks>
    private long[]? _nodeChecks;

    /// <summary>The segment whose blob is being decoded, while a node-check scope is open.</summary>
    internal uint? NodeCheckScope =>
        _nodeChecks is { } slots && slots[0] != 0 ? (uint)(slots[0] - 1) : null;

    /// <summary>Opens a node-check scope. See <see cref="ArrayDecodeContext.IsNodeChecked"/>.</summary>
    /// <param name="segmentId">The segment whose blob is being decoded.</param>
    /// <returns>What was in force, for <see cref="EndNodeCheckScope"/>.</returns>
    internal uint? BeginNodeCheckScope(uint segmentId)
    {
        uint? previous = NodeCheckScope;
        (_nodeChecks ??= new long[CheckedNodeSlots + 1])[0] = (long)segmentId + 1;
        return previous;
    }

    /// <summary>Restores what <see cref="BeginNodeCheckScope"/> displaced.</summary>
    /// <param name="previous">Its return value.</param>
    internal void EndNodeCheckScope(uint? previous)
    {
        if (_nodeChecks is { } slots)
        {
            slots[0] = previous is uint segment ? (long)segment + 1 : 0;
        }
    }

    /// <summary>
    /// Whether a per-node validation walk has already been run and passed during this scan.
    /// </summary>
    /// <param name="key">From <see cref="NodeCheckKey"/>.</param>
    /// <returns><see langword="true"/> when the walk may be skipped.</returns>
    /// <remarks>
    /// <para>
    /// What is remembered is a verdict, not a result, and that is the whole point. `vortex.runend`
    /// checks that its run ends ascend and `Patches` checks that its indices do -- O(side table)
    /// walks over bytes that belong to the node, not to the batch. `FlatLayoutReader` serves a take
    /// one batch at a time, so without this the same walk runs again for every batch and dominates
    /// a selective take.
    /// </para>
    /// <para>
    /// Caching the decode instead would cost a retained <see cref="CanonicalArena"/> per entry,
    /// which the scan paths' allocation ceilings do not leave room for. A verdict is one long in a
    /// small array allocated the first time a reader opens a scope, so a scan that never opens one
    /// allocates nothing for it.
    /// </para>
    /// <para>
    /// Never cleared by <see cref="ResetBatch"/>, and it must not be: the fact it records is about
    /// the file's bytes, which do not change between batches. Overflow is not an error and not a
    /// correctness problem -- past <see cref="CheckedNodeSlots"/> entries a node is simply
    /// re-checked.
    /// </para>
    /// </remarks>
    internal bool IsNodeChecked(long key)
    {
        if (_nodeChecks is not { } slots)
        {
            return false;
        }

        long stored = key + 1;
        for (int i = 1; i <= CheckedNodeSlots; i++)
        {
            long slot = slots[i];
            if (slot == stored)
            {
                return true;
            }

            if (slot == 0)
            {
                return false;
            }
        }

        return false;
    }

    /// <summary>Records that the walk named by <paramref name="key"/> ran and passed.</summary>
    /// <param name="key">From <see cref="NodeCheckKey"/>.</param>
    /// <remarks>
    /// Past <see cref="CheckedNodeSlots"/> entries a node is simply re-checked: this may only ever
    /// save work, never skip a check that has not passed.
    /// </remarks>
    internal void MarkNodeChecked(long key)
    {
        if (_nodeChecks is not { } slots)
        {
            return;
        }

        for (int i = 1; i <= CheckedNodeSlots; i++)
        {
            if (slots[i] == 0)
            {
                slots[i] = key + 1;
                return;
            }
        }
    }

    /// <summary>
    /// Names one serialized node of one segment's array blob, or <see langword="null"/> when the
    /// pair cannot be named without collision.
    /// </summary>
    /// <remarks>
    /// A node index is stable across batches, which is what makes this a key rather than a guess:
    /// <c>ArrayBlobReader.LoadCore</c> resets the node arena before every parse, so the arena holds
    /// one blob at a time and a node's index is a deterministic function of that blob's bytes. Parse
    /// the same segment in any batch, next to any other column, and the same node lands at the same
    /// index. The null return is a proof rather than a hope: 31 bits each is far above any real
    /// file, and one that exceeded it would re-check rather than confuse two nodes.
    /// </remarks>
    /// <param name="segmentId">The segment whose blob was parsed.</param>
    /// <param name="nodeIndex">The node's index in the node arena.</param>
    internal static long? NodeCheckKey(uint segmentId, int nodeIndex) =>
        segmentId < (1u << 31) && nodeIndex >= 0
            ? ((long)segmentId << 31) | (uint)nodeIndex
            : null;

    /// <summary>
    /// The retention key for a whole layout node, which its index identifies within the tree.
    /// </summary>
    /// <remarks>
    /// A second namespace above the segment ids rather than a second cache: the eviction rule is
    /// the load-bearing part of this mechanism and there should be exactly one of it. A dict
    /// layout's values child and a list layout's elements child are re-requested whole on every
    /// batch, so they need the same "decoded once, borrowed by many batches" treatment a chunk
    /// larger than a batch needs -- but they are named by a position in the layout tree, not by a
    /// segment.
    /// </remarks>
    /// <param name="layoutNodeIndex">The node's index in the layout tree.</param>
    internal static long LayoutKey(int layoutNodeIndex) => (1L << 32) | (uint)layoutNodeIndex;

    /// <summary>
    /// The retention key for one window of a flat layout's chunk, the <paramref name="windowIndex"/>th
    /// run of the reader's window length; <see langword="null"/> when the pair cannot be named.
    /// </summary>
    /// <remarks>
    /// A namespace of its own under bit 61, apart from the whole chunk under the bare segment id
    /// and from the shared children under bit 62: a chunk decoded whole and a window of the same
    /// chunk are different entries with different lifetimes, and a lookup that confused them would
    /// hand a batch a node of the wrong length.
    /// </remarks>
    /// <param name="segmentId">The chunk's segment.</param>
    /// <param name="windowIndex">The window's position in the chunk.</param>
    internal static long? WindowKey(uint segmentId, int windowIndex) =>
        segmentId < (1u << 31) && windowIndex >= 0 && windowIndex < (1 << 20)
            ? (1L << 61) | ((long)segmentId << 20) | (uint)windowIndex
            : null;

    /// <summary>
    /// The retention key for one serialized array node inside a segment's blob, or
    /// <see langword="null"/> when the pair cannot be named without collision.
    /// </summary>
    /// <remarks>
    /// A third namespace, above the two below 2^33, for the children an <em>array</em> encoding
    /// shares between batches rather than the ones a <em>layout</em> does. A dictionary stored as
    /// one array node has no layout to hold its values child, so its key is the pair
    /// <c>(segment, node)</c> -- the same pair <see cref="NodeCheckKey"/> names, and stable for the
    /// same reason: the node arena holds one blob at a time, so a node's index is a function of
    /// that blob's bytes.
    /// </remarks>
    /// <param name="segmentId">The segment whose blob was parsed.</param>
    /// <param name="nodeIndex">The node's index in the node arena.</param>
    internal static long? ChildKey(uint segmentId, int nodeIndex) =>
        segmentId < (1u << 31) && nodeIndex >= 0
            ? (1L << 62) | ((long)segmentId << 31) | (uint)nodeIndex
            : null;

    /// <summary>Whether a retained decode is already in flight, so another may not be opened.</summary>
    internal bool IsRetaining => _building is not null;

    /// <summary>
    /// Records that a layout reader loaded <paramref name="owner"/>'s segment into the node arena
    /// while a retained decode runs, so the entry keeps that segment alive.
    /// </summary>
    /// <param name="owner">The owner, held by the batch's request set; no reference taken.</param>
    internal void NoteSegment(SegmentOwner owner) => _building?.Depend(owner);

    /// <summary>
    /// The retained decode for <paramref name="key"/>, if one is held; otherwise a claim on the key,
    /// which the caller closes with <see cref="BeginRetainedDecode"/> and
    /// <see cref="EndRetainedDecode"/>, or gives up with <see cref="AbandonRetained"/>.
    /// </summary>
    /// <param name="key">From <see cref="SegmentKey"/>, <see cref="LayoutKey"/> or <see cref="ChildKey"/>.</param>
    /// <param name="arena">The arena holding it. The caller may borrow from this until eviction.</param>
    /// <param name="nodeIndex">The node's index in <paramref name="arena"/>.</param>
    /// <returns><see langword="true"/> when this key is retained.</returns>
    /// <remarks>
    /// Touching an entry is what keeps it from eviction while the batch in flight borrows it, so it
    /// happens on the hit path and not only when the entry is created. A miss claims the key: when
    /// another context of the scan is decoding it, the call waits for that decode and returns it,
    /// so a chunk is decoded once per scan. A claim still held from an earlier miss is given up
    /// first, since this context has moved on without decoding it; inside a retained decode the
    /// table is only looked at, never claimed, because the context already holds one claim.
    /// </remarks>
    internal bool TryGetRetained(long key, out CanonicalArena arena, out int nodeIndex)
    {
        if (_building is not null)
        {
            if (_retained is not null)
            {
                return _retained.Peek(key, Batch, out arena, out nodeIndex);
            }

            arena = null!;
            nodeIndex = -1;
            return false;
        }

        AbandonRetained();
        if (Retained.TryGet(key, this, Batch, out RetainedChunk? claim, out arena, out nodeIndex))
        {
            return true;
        }

        _claim = claim;
        return false;
    }

    /// <summary>Gives up the claim a miss of <see cref="TryGetRetained"/> made, for a path that decodes nothing under it.</summary>
    internal void AbandonRetained()
    {
        if (_claim is { } claim)
        {
            _claim = null;
            _retained!.Abandon(claim);
        }
    }

    /// <summary>
    /// Redirects <see cref="Canonical"/> at the arena of the chunk this context has claimed, so the
    /// decode lands there instead of being copied there afterwards.
    /// </summary>
    /// <returns>The arena, which the caller must close with <see cref="EndRetainedDecode"/>.</returns>
    /// <remarks>
    /// <para>
    /// Decoding straight into the destination is what keeps a large chunk from being walked twice:
    /// decoding into the batch's arena and then deep-copying every buffer into an arena that
    /// outlives the batch is a second full pass over the whole chunk. Nothing about the lifetime
    /// argument changes, because the arena was always the retained one.
    /// </para>
    /// <para>
    /// Each entry owns its own arena, and there is an entry per key rather than a single slot,
    /// because <b>a batch reads several columns, so several flat nodes, each with its own
    /// segment</b>. One slot would let one column evict another mid-batch; one shared arena would
    /// make evicting anything free everything, since <see cref="CanonicalArena.Reset"/> returns
    /// every block it owns. The working set is therefore one chunk per column, which the schema
    /// bounds, and the eviction rule is <see cref="RetainedChunks"/>'s.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">A retained decode is in flight, or no chunk is claimed.</exception>
    internal CanonicalArena BeginRetainedDecode()
    {
        if (_building is not null)
        {
            // A flat layout is a leaf: its decode never re-enters Execute, so a nested redirect
            // would mean the reader tree changed shape under an assumption this method makes.
            throw new InvalidOperationException(
                "A retained decode is already in flight on this scan context.");
        }

        RetainedChunk claim = _claim ?? throw new InvalidOperationException(
            "No chunk is claimed on this scan context: a retained decode follows a miss of TryGetRetained.");
        _building = claim;

        // The arena is made for the first decode the entry holds, not for the claim: a claim given
        // back without a decode, an answer an encoding declined, never needs one.
        CanonicalArena arena = claim.Arena ??= new CanonicalArena();

        // What the decode views is the blob in the node arena, which a flat reader loads just
        // before it begins; a child decoded inside the redirect notes its own segment as it loads.
        claim.Depend(Segments.OwnerHolding(Nodes.FirstBlobBuffer()));
        return arena;
    }

    /// <summary>
    /// Ends the redirect and publishes the decoded node under the claimed key, or discards the
    /// arena when the decode failed.
    /// </summary>
    /// <param name="nodeIndex">The decoded node's index in the redirected arena, or -1.</param>
    /// <remarks>
    /// Called from a <c>finally</c>, so it has to be correct for the throwing path too: a decode
    /// that raised leaves an arena full of half-built records, which is reset and returned to the
    /// spares rather than published.
    /// </remarks>
    internal void EndRetainedDecode(int nodeIndex)
    {
        RetainedChunk? entry = _building;
        _building = null;
        _claim = null;
        if (entry is null)
        {
            return;
        }

        if (nodeIndex < 0)
        {
            // Nothing was retained on the gathered segments yet, so there is nothing to release.
            _retained!.Abandon(entry);
            return;
        }

        _retained!.Publish(entry, nodeIndex, Batch);
    }

    /// <summary>
    /// Releases every segment held for the previous batch and resets every arena.
    /// </summary>
    /// <remarks>
    /// The order is fixed: release segments, then the node arena, then the canonical arena. A
    /// canonical node holds non-owning views into segment memory, so resetting the arenas first
    /// would leave a window in which a live view names released pages.
    /// </remarks>
    public void ResetBatch()
    {
        // Before anything else: the batch that was borrowing retained arenas is now dead. A context
        // that counts its own batches evicts what the batch before the dead one was the last to
        // borrow -- the dead one touched nothing of it, so no later batch will -- and a context a
        // scan drives leaves the floor to the scan, whose other contexts may still borrow.
        Batch++;
        AbandonRetained();
        if (_ownsRetained)
        {
            _retained?.Release(Batch - 1);
        }

        _selection = null;
        _selectionCount = 0;
        if (_pushed is not null)
        {
            _pushed.Field = null;
            _pushed.Fields = Layouts.FieldMask.All;

            // FieldsHonoured is NOT cleared here, and that is the point: a scan has one projection
            // from beginning to end, so whether the struct decodes take it is a fact about the
            // scan. The retained chunks outlive the batch and are narrowed once; a flag that reset
            // per batch would make the second batch narrow an already-narrowed node.
        }

        PredicateAtNode = false;
        PredicateAnswered = false;
        Segments.Release();
        Nodes.Reset();
        _batchCanonical.Reset();
        Scalars.Clear();
        Decode.ResetBatch();
    }

    /// <summary>Releases the batch's segments and the blocks the canonical arena rented.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _batchCanonical.Reset();
        AbandonRetained();
        if (_ownsRetained)
        {
            _retained?.Dispose();
        }

        Segments.Dispose();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    [DoesNotReturn]
    private static VortexFile ThrowDetached() =>
        throw new InvalidOperationException(
            "This ScanContext was created without a VortexFile; only its arenas and encoding " +
            "table are available.");
}
