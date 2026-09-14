// Phase 1 contract §2.2 and §8.3: everything one decode flow owns, with one owner and one
// lifetime. A ScanContext is AFFINE TO A SINGLE CONSUMER (docs/09-contracts.md §1) and is never
// shared - WithDegreeOfParallelism gives each concurrent split its own, with its own arenas.
//
// ResetBatch() releases segments BEFORE resetting the arenas. The other order would let a decoder
// that ran during the reset read freed memory.
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text;
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
/// Not thread-safe, deliberately. Concurrent scans over one open <see cref="VortexFile"/> are
/// supported and expected, and each of them holds its own <see cref="ScanContext"/>.
/// </para>
/// <para>
/// Every index this context hands out - an <see cref="ArrayNodeArena"/> node index, a
/// <see cref="CanonicalArena"/> node index - is meaningful only until the next
/// <see cref="ResetBatch"/>. Never store one in a field or across a batch boundary.
/// </para>
/// </remarks>
public sealed class ScanContext : IDisposable
{
    private readonly VortexFile? _file;
    private readonly ArrayEncodingId[] _arrayEncodings;
    private readonly string[] _arrayEncodingIds;
    private bool _disposed;

    /// <summary>The arena capacity a context that will decode batches starts from.</summary>
    private const int ScanCapacity = 64;

    /// <summary>
    /// The arena capacity for a context that reads METADATA and nothing else.
    /// </summary>
    /// <remarks>
    /// PERF-AUDIT-v2.md R30. `ZonePruningPlan` builds a context to read one zone map -- a struct of
    /// a few aggregate columns, one row per zone -- and then throws it away. Sized for a batch, that
    /// context allocated <b>18 192 bytes</b>, which the F-9 probe measured as <b>97,3 %</b> of
    /// everything pruning costs over not pruning. Three of its seven parts carry 87,8 % of that and
    /// all three take a capacity: <see cref="ArrayNodeArena"/> 7 368 o, <see cref="CanonicalArena"/>
    /// 6 760, <see cref="DTypeArena"/> 1 840.
    ///
    /// Eight rather than four, because the arenas DOUBLE when they fill and a zone map of a struct
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

        // Deliberately NOT materialized. VortexFile keeps only each id's UTF-8 location for the
        // reason its GetArrayEncodingId documents: FlatBuffers strings may be shared, so a 300 KB
        // footer can legally point 20 000 spec entries at one 40 KB id, and interning them all
        // would allocate 1.6 GB from a file that costs a single read. Interning them HERE instead
        // would simply move that cost onto the first scan - and multiply it by the degree of
        // parallelism, because every lane builds its own context. The four-byte resolved ids below
        // are bounded; the text is fetched from the file only by a throw site.
        _arrayEncodingIds = [];
        for (int i = 0; i < count; i++)
        {
            _arrayEncodings[i] = file.GetArrayEncoding(i);
        }

        // `DTypeArena`'s own default is 16, not 64, so it is capped rather than widened: a full
        // scan must keep exactly the capacity it had before this parameter existed.
        Types = new DTypeArena(Math.Min(capacity, 16));
        Scalars = new ScalarStore();
        Nodes = new ArrayNodeArena(capacity);
        _batchCanonical = new CanonicalArena(capacity);
        Segments = new SegmentRequestSet();
        Decode = new ArrayDecodeContext(this);
    }

    /// <summary>
    /// Creates a scan context detached from any file, over an explicit encoding table.
    /// </summary>
    /// <param name="arrayEncodingIds">
    /// The footer's <c>array_specs</c>, in order: entry <c>i</c> is what a node's
    /// <c>encoding == i</c> names. Over-reporting is normal and never an error - the writer
    /// pre-populates every id its edition permits (corpus manifest, last caveat).
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
    /// USUALLY THE BATCH'S ARENA, AND BRIEFLY NOT. A chunk larger than a batch is decoded into the
    /// arena that will RETAIN it, so no copy is needed to move it there --
    /// <see cref="BeginRetainedDecode"/> redirects this property for the duration of that one
    /// decode and <see cref="EndRetainedDecode"/> puts it back. The redirect is never observable
    /// from outside a single <c>LayoutReader.Execute</c> call, and every node the redirected decode
    /// produces is reachable only through the index that call returns.
    /// </remarks>
    public CanonicalArena Canonical => _redirect ?? _batchCanonical;

    /// <summary>The batch's own arena, which <see cref="ResetBatch"/> clears.</summary>
    private readonly CanonicalArena _batchCanonical;

    /// <summary>Set while a chunk is being decoded straight into the arena that will retain it.</summary>
    private CanonicalArena? _redirect;

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
    /// Releases every segment held for the previous batch and resets every arena.
    /// </summary>
    /// <remarks>
    /// The order is fixed: release segments, then the node arena, then the canonical arena. A
    /// canonical node holds non-owning views into segment memory, so resetting the arenas first
    /// would leave a window in which a live view names released pages.
    /// </remarks>
    /// <summary>
    /// The rows wanted out of the batch, in the coordinate space of the <c>Execute</c> call that is
    /// running, or empty for "every row".
    /// </summary>
    /// <remarks>
    /// CARRIED ON THE CONTEXT RATHER THAN PASSED AS A PARAMETER because `LayoutReader.Execute` is a
    /// public extension point: threading a new argument through it would be a breaking change for
    /// an out-of-tree reader, and every reader already receives the context.
    ///
    /// THE COORDINATE SPACE IS THE INVARIANT. The selection lives in exactly the space its sibling
    /// `rows` argument lives in, so a reader that re-bases `rows` must re-base this too, and a
    /// reader that passes `rows` through unchanged passes this through by doing nothing. Only
    /// `vortex.chunked` re-partitions rows; struct, zoned and flat do not.
    /// </remarks>
    internal ReadOnlySpan<int> Selection =>
        _selection is null ? default : _selection.AsSpan(0, _selectionCount);

    /// <summary>Whether a selection is in force for this batch.</summary>
    internal bool HasSelection => _selection is not null;

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

    /// <summary>One retained chunk: the arena that owns it, the node, and when it was last used.</summary>
    private sealed class RetainedChunk
    {
        internal required CanonicalArena Arena { get; init; }

        internal long Key;

        internal int NodeIndex;

        /// <summary>The batch number that last asked for this entry.</summary>
        internal long LastTouched;
    }

    /// <summary>
    /// Decoded chunks held across the batches carved out of them. Never cleared by
    /// <see cref="ResetBatch"/>; that is the whole point.
    /// </summary>
    /// <remarks>
    /// LAZY, and the ratchets are why. A file whose chunk is its batch never retains anything, which
    /// is every conformance fixture and everything this library writes at the default edition. Two
    /// eagerly-constructed collections cost 88 bytes on every one of those scans -
    /// <c>PathAllocationTests</c> turned red by 56 bytes over its tightest ceiling - so the common
    /// path allocates nothing for a feature it does not use.
    /// </remarks>
    private List<RetainedChunk>? _retained;

    /// <summary>Arenas whose entry was evicted, kept to be refilled rather than reallocated.</summary>
    private Stack<CanonicalArena>? _spareArenas;

    /// <summary>Counts batches, so an entry can say whether the CURRENT batch has used it.</summary>
    private long _batchNumber;

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
    /// Keys of the nodes whose O(n) validation has already passed, or null when none has.
    /// </summary>
    /// <remarks>
    /// LAZY, AND A RATCHET IS WHY - the same lesson the retained-chunk list learned two fields up.
    /// Written first as an <c>[InlineArray]</c> struct field on the belief that a struct field
    /// allocates nothing: it allocates nothing SEPARATELY, and it makes this object 64 bytes bigger
    /// on every scan that constructs one. `PathAllocationTests` charged exactly that and turned red
    /// by 64 bytes on `scan, fastlanes.delta`, an axis with EIGHT bytes of headroom. Allocated on
    /// first use instead, a scan that never takes a row pays nothing at all.
    /// </remarks>
    /// <summary>
    /// The open scope in slot 0 and the passed checks in slots 1.., each stored as its value PLUS
    /// ONE so that 0 means empty. Null until a scope is opened.
    /// </summary>
    /// <remarks>
    /// ONE FIELD, AND THREE MEASUREMENTS FORCED IT. This began as three -- an inline array of keys,
    /// a count, and a nullable segment id -- which is 24 bytes on every <see cref="ScanContext"/>,
    /// and `scan, fastlanes.delta` has SIXTEEN bytes of headroom over its ceiling. Moving one field
    /// to <c>ArrayDecodeContext</c> changed nothing: that object is smaller, so the same field
    /// crossed an alignment boundary there too. Two fields would land the axis at exactly zero
    /// headroom, which is a ratchet the next unrelated change breaks.
    ///
    /// So the state is one reference, allocated only when a reader opens a scope -- which only a
    /// take on a node bigger than its batch does. A full scan never allocates it and never pays a
    /// byte, which is why `scan, fastlanes.delta` is unchanged while the take path pays 88 bytes of
    /// its 128 (v2 R26).
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
    /// Whether a per-NODE validation walk has already been run and passed during this scan.
    /// </summary>
    /// <param name="key">From <see cref="NodeCheckKey"/>.</param>
    /// <returns><see langword="true"/> when the walk may be skipped.</returns>
    /// <remarks>
    /// <para>
    /// A VERDICT, NOT A RESULT, AND THAT IS THE WHOLE POINT. `vortex.runend` checks that its run
    /// ends ascend and `Patches` checks that its indices do - O(side table) walks over bytes that
    /// belong to the NODE, not to the batch. `FlatLayoutReader` serves a take one batch at a time,
    /// so they ran once per batch: 93% of a selective run-end take was that one walk (v2 R26),
    /// 1 086 µs against 72 with it short-circuited.
    /// </para>
    /// <para>
    /// R25 TRIED TO CACHE THE DECODE AND COULD NOT AFFORD IT: an entry costs a retained
    /// <see cref="CanonicalArena"/>, and `PathAllocationTests` leaves 216 bytes of headroom on the
    /// take axis and EIGHT on `scan, fastlanes.delta`. A verdict is a long in an inline array -- a
    /// struct field of this object, so the whole mechanism allocates nothing, on any path, ever.
    /// That is why this one fits where that one did not.
    /// </para>
    /// <para>
    /// NEVER CLEARED BY <see cref="ResetBatch"/>, and it must not be: the fact it records is about
    /// the file's bytes, which do not change between batches. Overflow is not an error and not a
    /// correctness problem - past <see cref="CheckedNodeSlots"/> entries a node is re-checked, which
    /// is exactly today's behaviour.
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
    /// Past <see cref="CheckedNodeSlots"/> entries a node is simply re-checked, which is exactly
    /// today's behaviour: this may only ever save work, never skip a check that has not passed.
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
    /// A NODE INDEX IS STABLE ACROSS BATCHES, which is what makes this a key rather than a guess:
    /// <c>ArrayBlobReader.LoadCore</c> RESETS the node arena before every parse, so the arena holds
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
    /// The retention key for a whole LAYOUT NODE, which its index identifies within the tree.
    /// </summary>
    /// <remarks>
    /// A second namespace above the segment ids rather than a second cache: the eviction rule is
    /// the load-bearing part of this mechanism and there should be exactly one of it. A dict
    /// layout's values child and a list layout's elements child are re-requested WHOLE on every
    /// batch, so they need the same "decoded once, borrowed by many batches" treatment a chunk
    /// larger than a batch needs -- but they are named by a position in the layout tree, not by a
    /// segment.
    /// </remarks>
    /// <param name="layoutNodeIndex">The node's index in the layout tree.</param>
    internal static long LayoutKey(int layoutNodeIndex) => (1L << 32) | (uint)layoutNodeIndex;

    /// <summary>The retained decode for <paramref name="key"/>, if one is held.</summary>
    /// <param name="key">From <see cref="SegmentKey"/> or <see cref="LayoutKey"/>.</param>
    /// <param name="arena">The arena holding it. The caller may borrow from this until eviction.</param>
    /// <param name="nodeIndex">The node's index in <paramref name="arena"/>.</param>
    /// <returns><see langword="true"/> when this key is retained.</returns>
    internal bool TryGetRetained(long key, out CanonicalArena arena, out int nodeIndex)
    {
        if (_retained is null)
        {
            arena = null!;
            nodeIndex = -1;
            return false;
        }

        foreach (RetainedChunk entry in _retained)
        {
            if (entry.Key == key)
            {
                // Touching it is what makes it un-evictable for the rest of this batch, so it has to
                // happen on the hit path and not only when the entry is created.
                entry.LastTouched = _batchNumber;
                arena = entry.Arena;
                nodeIndex = entry.NodeIndex;
                return true;
            }
        }

        arena = null!;
        nodeIndex = -1;
        return false;
    }

    /// <summary>
    /// Picks the arena a chunk will be RETAINED in and redirects <see cref="Canonical"/> at it, so
    /// the decode lands there instead of being copied there afterwards.
    /// </summary>
    /// <returns>The arena, which the caller must close with <see cref="EndRetainedDecode"/>.</returns>
    /// <remarks>
    /// <para>
    /// THE COPY WAS THE SECOND FULL PASS OVER EVERY LARGE CHUNK. This method used to be
    /// <c>Retain(segmentId, source, nodeIndex)</c>, which decoded into the batch's arena and then
    /// deep-copied the result -- every buffer, every byte -- into an arena that outlives the batch.
    /// It was 26% of a 1M-row `vortex.sequence` scan and a proportional share of every other
    /// encoding on this path. Decoding into the destination removes it entirely; nothing else about
    /// the lifetime argument changes, because the arena was always the retained one.
    /// </para>
    /// </remarks>
    /// <remarks>
    /// <para>
    /// THE EVICTION RULE IS A PROOF, NOT A HEURISTIC, and the first version of this cache had no
    /// such rule and was wrong for it. Callers BORROW a retained arena: a batch's records hold views
    /// onto its memory rather than copies. So an entry may be freed only when no live batch can be
    /// looking at it, and there is exactly one fact that establishes that - a batch is invalid once
    /// the next <c>MoveNextAsync</c> begins, so an entry whose <see cref="RetainedChunk.LastTouched"/>
    /// is below the current batch number is borrowed by nobody.
    /// </para>
    /// <para>
    /// THE FIRST VERSION HELD ONE ENTRY AND SHARED ONE ARENA, and both were wrong for the same
    /// reason: <b>a batch reads several columns, so several flat nodes, each with its own segment</b>.
    /// One entry meant column B evicted column A mid-batch; one arena meant evicting anything freed
    /// everything, since <see cref="CanonicalArena.Reset"/> returns every block it owns. That cost 25
    /// tests and is why each entry owns its arena.
    /// </para>
    /// <para>
    /// The working set is therefore one chunk per column, which the schema bounds. Evicted arenas are
    /// kept and refilled rather than reallocated.
    /// </para>
    /// </remarks>
    internal CanonicalArena BeginRetainedDecode()
    {
        if (_redirect is not null)
        {
            // A flat layout is a leaf: its decode never re-enters Execute, so a nested redirect
            // would mean the reader tree changed shape under an assumption this method makes.
            throw new InvalidOperationException(
                "A retained decode is already in flight on this scan context.");
        }

        _retained ??= [];
        for (int i = _retained.Count - 1; i >= 0; i--)
        {
            RetainedChunk entry = _retained[i];
            if (entry.LastTouched >= _batchNumber)
            {
                continue;
            }

            entry.Arena.Reset();
            (_spareArenas ??= new Stack<CanonicalArena>()).Push(entry.Arena);
            _retained.RemoveAt(i);
        }

        CanonicalArena fresh = _spareArenas is { Count: > 0 }
            ? _spareArenas.Pop()
            : new CanonicalArena();
        _redirect = fresh;
        return fresh;
    }

    /// <summary>
    /// Ends the redirect and records the decoded node, or discards the arena when the decode
    /// failed.
    /// </summary>
    /// <param name="key">What the node is retained under, or 0 when discarding.</param>
    /// <param name="nodeIndex">The decoded node's index in the redirected arena, or -1.</param>
    /// <remarks>
    /// Called from a <c>finally</c>, so it has to be correct for the throwing path too: a decode
    /// that raised leaves an arena full of half-built records, which is reset and returned to the
    /// spares rather than published.
    /// </remarks>
    internal void EndRetainedDecode(long key, int nodeIndex)
    {
        CanonicalArena? fresh = _redirect;
        _redirect = null;
        if (fresh is null)
        {
            return;
        }

        if (nodeIndex < 0)
        {
            fresh.Reset();
            (_spareArenas ??= new Stack<CanonicalArena>()).Push(fresh);
            return;
        }

        _retained ??= [];
        _retained.Add(new RetainedChunk
        {
            Arena = fresh,
            Key = key,
            NodeIndex = nodeIndex,
            LastTouched = _batchNumber,
        });
    }

    public void ResetBatch()
    {
        // Before anything else: the batch that was borrowing retained arenas is now dead, which is
        // what makes the eviction in `Retain` safe.
        _batchNumber++;
        _selection = null;
        _selectionCount = 0;
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
        if (_retained is not null)
        {
            foreach (RetainedChunk entry in _retained)
            {
                entry.Arena.Reset();
            }

            _retained.Clear();
        }

        while (_spareArenas is { Count: > 0 })
        {
            _spareArenas.Pop().Reset();
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
