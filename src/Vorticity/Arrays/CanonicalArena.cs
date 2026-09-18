// The canonical model of Phase 1 contract §8.4: the nine forms `Canonical` in
// vortex-array-0.86.1/src/canonical.rs has, minus Map, Union and Variant, which are out of scope
// and reach the caller as VortexUnsupportedException with kind "dtype".
//
// `vortex.list` and `vortex.varbin` have no canonical form of their own - their decoders produce
// ListView and VarBinView, the same choice upstream makes.
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Vorticity.Buffers;
using Vorticity.Types;
using Vorticity.Types.Numerics;

namespace Vorticity.Arrays;

/// <summary>The nine canonical forms Phase 1 produces.</summary>
public enum CanonicalKind : byte
{
    /// <summary>All rows null; no buffers.</summary>
    Null = 0,

    /// <summary>A bit-packed, LSB-first boolean bitmap with a bit offset below 8.</summary>
    Bool = 1,

    /// <summary>Fixed-width primitive values.</summary>
    Primitive = 2,

    /// <summary>Fixed-point decimal values, little-endian two's complement.</summary>
    Decimal = 3,

    /// <summary>Arrow-style 16-byte views over zero or more data buffers.</summary>
    VarBinView = 4,

    /// <summary>Offsets plus sizes over a canonical elements child.</summary>
    ListView = 5,

    /// <summary>A canonical elements child read <c>FixedSize</c> at a time.</summary>
    FixedSizeList = 6,

    /// <summary>Named fields, each a canonical child.</summary>
    Struct = 7,

    /// <summary>An extension dtype wrapping a canonical storage child.</summary>
    Extension = 8,

    /// <summary>One element and a row count: every row resolves to the same window.</summary>
    /// <remarks>
    /// PERF-AUDIT-v2.md Z1b-c2a. `ConstantCanonicalizer` tiles today -- it writes the element once
    /// and doubles it over the whole column, so a million rows of eight bytes cost eight megabytes
    /// to say one number. `ConstantFormBenchmarks` priced the three arms: build **~3 900x** at eight
    /// bytes and **~8 400x** at sixteen, a scattered take **60x**, and the consumer -- the arm that
    /// could have said no -- reads **6,5 % FASTER**, because one cache line is re-read where 512 KiB
    /// were walked.
    /// <para>
    /// PRODUCED ONLY BEHIND AN INTERNAL SWITCH. `ConstantCanonicalizer` emits it when
    /// `VortexReadOptions.ConstantForm` is on (Z1b-c2b), which only `ConstantFormTests` does; the
    /// default path still tiles. The consumers that have their arm -- the filter, the slice, the
    /// concatenation, the rewrap, the writer's passes, `VortexColumn`'s typed primitive path --
    /// handle it; the ones that do not yet are what keeps the switch off (Z1b-c2c2, 89 tests).
    /// </para>
    /// </remarks>
    Constant = 9,
}

/// <summary>
/// One decoded node: a view into a <see cref="CanonicalArena"/>.
/// </summary>
/// <remarks>
/// A <c>ref struct</c> for the same reason <see cref="ArrayNode"/> is: an index is meaningless once
/// its arena is <see cref="CanonicalArena.Reset"/>, and every buffer it names belongs to the
/// batch's segments (Phase 1 contract §2.2 rules 4 and 5).
/// </remarks>
public readonly ref struct CanonicalNode
{
    private readonly CanonicalArena _arena;
    private readonly int _index;

    internal CanonicalNode(CanonicalArena arena, int index)
    {
        _arena = arena;
        _index = index;
    }

    /// <summary>This node's index in its arena.</summary>
    public int Index => _index;

    /// <summary>The arena this node belongs to.</summary>
    public CanonicalArena Arena => _arena;

    /// <summary>Which canonical form this node is in.</summary>
    public CanonicalKind Kind => _arena.RecordRef(_index).Kind;

    /// <summary>The dtype the node was decoded against, supplied top-down by its parent.</summary>
    public DType DType => _arena.RecordRef(_index).DType;

    /// <summary>Row count, also supplied top-down.</summary>
    public int Length => _arena.RecordRef(_index).Length;

    /// <summary>Per-row validity (contract §2.6).</summary>
    public Validity Validity => _arena.RecordRef(_index).Validity;

    // ---------------------------------------------------------------------------------- Bool

    /// <summary>Bit-packed, LSB-first boolean bits.</summary>
    /// <exception cref="VortexFormatException">The kind is not <see cref="CanonicalKind.Bool"/>.</exception>
    public VortexBuffer Bits => Require(CanonicalKind.Bool).BufferA;

    /// <summary>
    /// The bit position of row 0 inside the first byte, 0..7 - <c>vortex.bool</c>'s
    /// <c>BoolMetadata.offset</c>. Consumers must apply it; the bitmap is never shifted, because
    /// that would be an allocation and a copy per batch (contract §2.6 rule 5).
    /// </summary>
    /// <exception cref="VortexFormatException">The kind is not <see cref="CanonicalKind.Bool"/>.</exception>
    public int BitOffset => Require(CanonicalKind.Bool).BitOffset;

    // ----------------------------------------------------------------------------- Primitive

    /// <summary>The values' physical type.</summary>
    /// <exception cref="VortexFormatException">The kind is not <see cref="CanonicalKind.Primitive"/>.</exception>
    public PType PType => Require(CanonicalKind.Primitive).PType;

    /// <summary>
    /// <c>Length * PType.ByteWidth()</c> bytes for a Primitive, or
    /// <c>Length * DecimalStorage.ByteWidth(Storage)</c> little-endian bytes for a Decimal.
    /// </summary>
    /// <exception cref="VortexFormatException">The kind is neither Primitive nor Decimal.</exception>
    public VortexBuffer Values
    {
        get
        {
            ref readonly CanonicalRecord r = ref _arena.RecordRef(_index);

            // THE CONSTANT FORM MATERIALIZES HERE, AND ONLY HERE (PERF-AUDIT-v2.md Z1b-c2b2). This
            // property promises a CONTIGUOUS span of `Length` values, and one element plus a count
            // cannot honour that without expanding. Doing it at this one boundary is what spares the
            // 171 local guards §2.4bis counted: every consumer that reads values -- the columns, the
            // comparison kernels, the literal reader, row encoding, the zone summariser -- goes
            // through here and needs no case of its own.
            //
            // The gain survives for everyone who never asks: a scan that filters, takes, prunes or
            // writes back never materializes, and those are the paths the column was tiled for. The
            // cost falls exactly on the caller who demands a contiguous span, which is what such a
            // caller is asking for. And it is paid ONCE -- the twin is memoized on the record.
            if (r.Kind == CanonicalKind.Constant)
            {
                return _arena.RecordRef(_arena.MaterializeConstant(_index)).BufferA;
            }

            if (r.Kind is not (CanonicalKind.Primitive or CanonicalKind.Decimal))
            {
                ArraysThrow.Kind(r.Kind, "Primitive or Decimal");
            }

            return r.BufferA;
        }
    }

    // ------------------------------------------------------------------------------- Decimal

    /// <summary>The decimal storage width.</summary>
    /// <exception cref="VortexFormatException">The kind is not <see cref="CanonicalKind.Decimal"/>.</exception>
    public DecimalStorageType Storage => Require(CanonicalKind.Decimal).Storage;

    /// <summary>The decimal precision, 1..76.</summary>
    /// <exception cref="VortexFormatException">The kind is not <see cref="CanonicalKind.Decimal"/>.</exception>
    public byte Precision => Require(CanonicalKind.Decimal).Precision;

    /// <summary>The decimal scale.</summary>
    /// <exception cref="VortexFormatException">The kind is not <see cref="CanonicalKind.Decimal"/>.</exception>
    public sbyte Scale => Require(CanonicalKind.Decimal).Scale;

    // ---------------------------------------------------------------------------- VarBinView

    /// <summary><c>Length * 16</c> bytes of Arrow-style views, 16-byte aligned.</summary>
    /// <exception cref="VortexFormatException">The kind is not <see cref="CanonicalKind.VarBinView"/>.</exception>
    public VortexBuffer Views => Require(CanonicalKind.VarBinView).BufferA;

    /// <summary>How many data buffers the views may reference.</summary>
    /// <exception cref="VortexFormatException">The kind is not <see cref="CanonicalKind.VarBinView"/>.</exception>
    public int DataBufferCount => Require(CanonicalKind.VarBinView).DataBufferCount;

    /// <summary>Data buffer <paramref name="index"/>.</summary>
    /// <param name="index">0-based, below <see cref="DataBufferCount"/>.</param>
    /// <exception cref="VortexFormatException">The kind is wrong or the index is out of range.</exception>
    public VortexBuffer GetDataBuffer(int index)
    {
        ref readonly CanonicalRecord r = ref Require(CanonicalKind.VarBinView);
        if ((uint)index >= (uint)r.DataBufferCount)
        {
            return ArraysThrow.BufferIndex(index, r.DataBufferCount);
        }

        return _arena.DataBufferAt(r.DataBufferStart + index);
    }

    // ------------------------------------------------------------------------------ ListView

    /// <summary>The canonical child holding the flattened elements.</summary>
    /// <exception cref="VortexFormatException">The kind is neither ListView nor FixedSizeList.</exception>
    public int ElementsIndex
    {
        get
        {
            ref readonly CanonicalRecord r = ref _arena.RecordRef(_index);
            if (r.Kind is not (CanonicalKind.ListView or CanonicalKind.FixedSizeList))
            {
                ArraysThrow.Kind(r.Kind, "ListView or FixedSizeList");
            }

            return _arena.ChildAt(r.ChildStart);
        }
    }

    /// <summary><see cref="Length"/> elements of <see cref="OffsetPType"/>.</summary>
    /// <exception cref="VortexFormatException">The kind is not <see cref="CanonicalKind.ListView"/>.</exception>
    public VortexBuffer Offsets => Require(CanonicalKind.ListView).BufferA;

    /// <summary><see cref="Length"/> elements of <see cref="SizePType"/>.</summary>
    /// <exception cref="VortexFormatException">The kind is not <see cref="CanonicalKind.ListView"/>.</exception>
    public VortexBuffer Sizes => Require(CanonicalKind.ListView).BufferB;

    /// <summary>Physical type of <see cref="Offsets"/>.</summary>
    /// <exception cref="VortexFormatException">The kind is not <see cref="CanonicalKind.ListView"/>.</exception>
    public PType OffsetPType => Require(CanonicalKind.ListView).PType;

    /// <summary>Physical type of <see cref="Sizes"/>.</summary>
    /// <exception cref="VortexFormatException">The kind is not <see cref="CanonicalKind.ListView"/>.</exception>
    public PType SizePType => Require(CanonicalKind.ListView).SizePType;

    // ------------------------------------------------------------------------- FixedSizeList

    /// <summary>
    /// Elements per row; the elements child holds <c>Length * FixedSize</c> of them. Zero is legal
    /// and upstream special-cases it, so never divide by it.
    /// </summary>
    /// <exception cref="VortexFormatException">The kind is not <see cref="CanonicalKind.FixedSizeList"/>.</exception>
    public uint FixedSize => Require(CanonicalKind.FixedSizeList).FixedSize;

    // -------------------------------------------------------------------------------- Struct

    /// <summary>Number of fields.</summary>
    /// <exception cref="VortexFormatException">The kind is not <see cref="CanonicalKind.Struct"/>.</exception>
    public int FieldCount => Require(CanonicalKind.Struct).ChildCount;

    /// <summary>The canonical child index of field <paramref name="field"/>.</summary>
    /// <param name="field">0-based, below <see cref="FieldCount"/>.</param>
    /// <exception cref="VortexFormatException">The kind is wrong or the index is out of range.</exception>
    public int GetFieldIndex(int field)
    {
        ref readonly CanonicalRecord r = ref Require(CanonicalKind.Struct);
        if ((uint)field >= (uint)r.ChildCount)
        {
            ArraysThrow.ChildIndex(field, r.ChildCount);
        }

        return _arena.ChildAt(r.ChildStart + field);
    }

    // ----------------------------------------------------------------------------- Extension

    /// <summary>The canonical storage child; the extension's validity is the storage's.</summary>
    /// <summary>The one element a <see cref="CanonicalKind.Constant"/> node repeats.</summary>
    /// <exception cref="VortexFormatException">The kind is not <see cref="CanonicalKind.Constant"/>.</exception>
    public ReadOnlySpan<byte> ConstantElement
    {
        get
        {
            ref readonly CanonicalRecord r = ref Require(CanonicalKind.Constant);
            return r.BufferA.Span[..(int)r.FixedSize];
        }
    }

    /// <exception cref="VortexFormatException">The kind is not <see cref="CanonicalKind.Extension"/>.</exception>
    public int StorageIndex
    {
        get
        {
            ref readonly CanonicalRecord r = ref Require(CanonicalKind.Extension);
            return _arena.ChildAt(r.ChildStart);
        }
    }

    private ref readonly CanonicalRecord Require(CanonicalKind kind)
    {
        ref readonly CanonicalRecord r = ref _arena.RecordRef(_index);
        if (r.Kind != kind)
        {
            ArraysThrow.Kind(r.Kind, kind.ToString());
        }

        return ref r;
    }
}

/// <summary>
/// The pooled store behind <see cref="CanonicalNode"/>. Owned by a <see cref="ScanContext"/>;
/// <see cref="Reset"/> per batch.
/// </summary>
public sealed class CanonicalArena
{
    private CanonicalRecord[] _records;
    private int _recordCount;

    private int[] _children;
    private int _childCount;

    private VortexBuffer[] _dataBuffers;
    private int _dataBufferCount;

    // Blocks handed out by Allocate, returned to the pool on Reset. Never handed to a caller as an
    // owner: contract §2.2 rule 3 says decoders never Retain or Release anything.
    private NativeSegmentOwner[] _owned;
    private int _ownedCount;

    private readonly AlignedBufferPool _pool;

    /// <summary>Creates an arena backed by <see cref="AlignedBufferPool.Shared"/>.</summary>
    /// <param name="initialCapacity">Hint for the record array's initial size. Must be positive.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="initialCapacity"/> is not positive.</exception>
    public CanonicalArena(int initialCapacity = 64)
        : this(initialCapacity, AlignedBufferPool.Shared)
    {
    }

    /// <summary>Creates an arena backed by an explicit pool. Tests use this to observe rentals.</summary>
    /// <param name="initialCapacity">Hint for the record array's initial size. Must be positive.</param>
    /// <param name="pool">The pool <see cref="Allocate(int, int)"/> rents from.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="initialCapacity"/> is not positive.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="pool"/> is null.</exception>
    public CanonicalArena(int initialCapacity, AlignedBufferPool pool)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(initialCapacity);
        ArgumentNullException.ThrowIfNull(pool);
        _records = new CanonicalRecord[initialCapacity];
        _children = new int[initialCapacity];
        _dataBuffers = new VortexBuffer[8];
        _owned = new NativeSegmentOwner[8];
        _pool = pool;
    }

    /// <summary>
    /// Reserves <paramref name="count"/> contiguous child slots and returns the first one.
    /// </summary>
    /// <param name="count">How many child slots this node needs; zero is legal.</param>
    /// <returns>The first reserved slot in the arena's child array.</returns>
    /// <remarks>
    /// <para>
    /// Both <see cref="CopyFrom"/> and <see cref="ReferenceFrom"/> have to place a node's new
    /// child indices CONTIGUOUSLY, and cannot know them until each child has been copied -- which
    /// appends records, and children, of its own. They used to gather into
    /// <c>new int[src.ChildCount]</c> and replay it afterwards: one managed allocation per node
    /// with children, on a path a batch walks.
    /// </para>
    /// <para>
    /// The arena already owns a growable child array, and reserving in it needs no second
    /// buffer: this node takes [start, start + count), and every deeper copy reserves ABOVE that,
    /// so the block is contiguous by construction rather than by replay. Nothing is shared between
    /// calls, between arenas or between threads -- which a thread-static scratch would have been,
    /// and an <c>await</c> introduced anywhere in this recursion would then have made two logical
    /// flows share one stack silently.
    /// </para>
    /// <para>
    /// The slots hold whatever they held; the caller fills every one of them before the node that
    /// names them is committed.
    /// </para>
    /// </remarks>
    private int ReserveChildren(int count)
    {
        int start = _childCount;
        int needed = start + count;
        if (needed > _children.Length)
        {
            int capacity = _children.Length;
            while (capacity < needed)
            {
                capacity = Grow(capacity);
            }

            Array.Resize(ref _children, capacity);
        }

        _childCount = needed;
        return start;
    }

    /// <summary>Number of decoded nodes currently held.</summary>
    public int NodeCount => _recordCount;

    /// <summary>Node <paramref name="index"/>.</summary>
    /// <param name="index">0-based, below <see cref="NodeCount"/>.</param>
    /// <exception cref="VortexFormatException">The index is out of range.</exception>
    public CanonicalNode GetNode(int index)
    {
        if ((uint)index >= (uint)_recordCount)
        {
            ArraysThrow.CanonicalIndex(index, _recordCount);
        }

        return new CanonicalNode(this, index);
    }

    /// <summary>
    /// Clears counts and returns every block <see cref="Allocate(int, int)"/> handed out. Does NOT free the
    /// backing arrays.
    /// </summary>
    public void Reset()
    {
        for (int i = 0; i < _ownedCount; i++)
        {
            NativeSegmentOwner owner = _owned[i];
            _owned[i] = null!;
            _pool.Return(owner);
        }

        _ownedCount = 0;
        _recordCount = 0;
        _childCount = 0;
        _dataBufferCount = 0;
    }

    // ------------------------------------------------------------------------------- builders

    /// <summary>Adds an all-null node.</summary>
    /// <param name="dtype">The dtype this node produces.</param>
    /// <param name="length">Row count.</param>
    /// <returns>The new node's index.</returns>
    public int AddNull(DType dtype, int length)
    {
        CanonicalRecord r = New(CanonicalKind.Null, dtype, length, Validity.AllInvalid);
        return Commit(ref r);
    }

    /// <summary>Adds a boolean node.</summary>
    /// <param name="dtype">The dtype this node produces.</param>
    /// <param name="length">Row count.</param>
    /// <param name="validity">Per-row validity.</param>
    /// <param name="bits">Bit-packed, LSB-first values.</param>
    /// <param name="bitOffset">Bit position of row 0 inside the first byte, 0..7.</param>
    /// <returns>The new node's index.</returns>
    /// <exception cref="VortexFormatException">
    /// <paramref name="bitOffset"/> is outside 0..7, or <paramref name="bits"/> is too short for
    /// <paramref name="length"/> bits at that offset. Both are class I.
    /// </exception>
    public int AddBool(DType dtype, int length, Validity validity, VortexBuffer bits, int bitOffset)
    {
        if ((uint)bitOffset > 7)
        {
            ArraysThrow.Format($"Bool bit offset {bitOffset} is outside [0, 7].");
        }

        long neededBytes = ((long)bitOffset + length + 7) / 8;
        if (neededBytes > bits.Length)
        {
            ArraysThrow.Format(
                $"A Bool array of {length} bits at offset {bitOffset} needs {neededBytes} bytes; " +
                $"the buffer holds {bits.Length}.");
        }

        CanonicalRecord r = New(CanonicalKind.Bool, dtype, length, validity);
        r.BufferA = bits;
        r.BitOffset = bitOffset;
        return Commit(ref r);
    }

    /// <summary>Adds a fixed-width primitive node.</summary>
    /// <param name="dtype">The dtype this node produces.</param>
    /// <param name="length">Row count.</param>
    /// <param name="validity">Per-row validity.</param>
    /// <param name="ptype">The values' physical type.</param>
    /// <param name="values">Exactly <c>length * ptype.ByteWidth()</c> bytes.</param>
    /// <returns>The new node's index.</returns>
    /// <exception cref="VortexFormatException">
    /// <paramref name="ptype"/> is undefined or <paramref name="values"/> is the wrong length.
    /// </exception>
    public int AddPrimitive(DType dtype, int length, Validity validity, PType ptype, VortexBuffer values)
    {
        if (!PTypeExtensions.IsDefined(ptype))
        {
            ArraysThrow.Format($"PType tag {(byte)ptype} is not defined.");
        }

        RequireExactLength(values.Length, length, ptype.ByteWidth(), "Primitive");
        CanonicalRecord r = New(CanonicalKind.Primitive, dtype, length, validity);
        r.PType = ptype;
        r.BufferA = values;
        return Commit(ref r);
    }

    /// <summary>Adds a constant node: one element, repeated <paramref name="length"/> times.</summary>
    /// <param name="dtype">The dtype this node produces, which is the ELEMENT's dtype.</param>
    /// <param name="length">Row count.</param>
    /// <param name="validity">Per-row validity, as for any other node.</param>
    /// <param name="element">The one value's bytes, in the canonical form its dtype implies.</param>
    /// <returns>The new node's index.</returns>
    /// <remarks>
    /// The element's WIDTH is kept in <c>FixedSize</c> rather than derived from the dtype, so that
    /// one record shape serves a primitive (8 bytes), a decimal (16) and a view (16) without this
    /// method having to know the table of widths. PERF-AUDIT-v2.md Z1b-c2a.
    /// </remarks>
    /// <exception cref="VortexFormatException"><paramref name="element"/> is empty.</exception>
    public int AddConstant(DType dtype, int length, Validity validity, ReadOnlySpan<byte> element)
    {
        if (element.IsEmpty)
        {
            ArraysThrow.Format("A constant node needs an element; none was given.");
        }

        VortexBuffer stored = AllocateUninitialized(element.Length, 1, out Span<byte> into);
        element.CopyTo(into);

        CanonicalRecord r = New(CanonicalKind.Constant, dtype, length, validity);
        r.BufferA = stored;
        r.FixedSize = (uint)element.Length;
        return Commit(ref r);
    }

    /// <summary>Expands a constant node into a materialized twin, once. PERF-AUDIT-v2.md Z1b-c2b2.</summary>
    /// <param name="nodeIndex">A node of kind <see cref="CanonicalKind.Constant"/>.</param>
    /// <returns>The twin's index: a Primitive or Decimal node holding `Length` copies.</returns>
    /// <remarks>
    /// Memoized on the record, so a caller that walks a column row by row through
    /// <c>PrimitiveColumn.this[int]</c> pays the expansion once rather than per access. The twin is
    /// a NEW node: records are referenced by index all over the arena, and rewriting this one in
    /// place would change what every holder of that index sees.
    /// </remarks>
    internal int MaterializeConstant(int nodeIndex)
    {
        ref CanonicalRecord source = ref RecordRefMutable(nodeIndex);
        if (source.Kind != CanonicalKind.Constant)
        {
            ArraysThrow.Kind(source.Kind, "Constant");
        }

        if (source.Materialized >= 0)
        {
            return source.Materialized;
        }

        int width = (int)source.FixedSize;
        ReadOnlySpan<byte> element = source.BufferA.Span[..width];
        DType dtype = source.DType;
        int rows = source.Length;
        Validity validity = source.Validity;

        VortexBuffer values = AllocateUninitialized(
            checked(rows * width), width, out Span<byte> writable);
        Decoders.Compressed.RowKernels.Tile(writable, element);

        int twin = dtype.Kind == DTypeKind.Decimal
            ? AddDecimal(dtype, rows, validity, source.Storage, source.Precision, source.Scale, values)
            : AddPrimitive(dtype, rows, validity, dtype.PType, values);

        // Re-taken after the Add: committing a record may have grown the backing array, so the
        // earlier `ref` can be pointing at a block nobody reads any more.
        RecordRefMutable(nodeIndex).Materialized = twin;
        return twin;
    }

    /// <summary>Adds a decimal node.</summary>
    /// <param name="dtype">The dtype this node produces.</param>
    /// <param name="length">Row count.</param>
    /// <param name="validity">Per-row validity.</param>
    /// <param name="storage">The storage width.</param>
    /// <param name="precision">The dtype's precision.</param>
    /// <param name="scale">The dtype's scale.</param>
    /// <param name="values">Exactly <c>length * DecimalStorage.ByteWidth(storage)</c> bytes, little-endian.</param>
    /// <returns>The new node's index.</returns>
    /// <exception cref="VortexFormatException">
    /// <paramref name="storage"/> is undefined or <paramref name="values"/> is the wrong length.
    /// </exception>
    public int AddDecimal(
        DType dtype,
        int length,
        Validity validity,
        DecimalStorageType storage,
        byte precision,
        sbyte scale,
        VortexBuffer values)
    {
        if (!DecimalStorage.IsDefined(storage))
        {
            ArraysThrow.Format($"Decimal storage tag {(byte)storage} is not defined; 0..5 are.");
        }

        RequireExactLength(values.Length, length, DecimalStorage.ByteWidth(storage), "Decimal");
        CanonicalRecord r = New(CanonicalKind.Decimal, dtype, length, validity);
        r.Storage = storage;
        r.Precision = precision;
        r.Scale = scale;
        r.BufferA = values;
        return Commit(ref r);
    }

    /// <summary>Adds an Arrow-style varbin view node.</summary>
    /// <param name="dtype">The dtype this node produces.</param>
    /// <param name="length">Row count.</param>
    /// <param name="validity">Per-row validity.</param>
    /// <param name="views">Exactly <c>length * 16</c> bytes.</param>
    /// <param name="dataBuffers">The buffers the views may reference.</param>
    /// <returns>The new node's index.</returns>
    /// <exception cref="VortexFormatException"><paramref name="views"/> is the wrong length.</exception>
    public int AddVarBinView(
        DType dtype,
        int length,
        Validity validity,
        VortexBuffer views,
        ReadOnlySpan<VortexBuffer> dataBuffers)
    {
        RequireExactLength(views.Length, length, 16, "VarBinView");
        CanonicalRecord r = New(CanonicalKind.VarBinView, dtype, length, validity);
        r.BufferA = views;
        r.DataBufferStart = _dataBufferCount;
        r.DataBufferCount = dataBuffers.Length;
        for (int i = 0; i < dataBuffers.Length; i++)
        {
            AddDataBuffer(dataBuffers[i]);
        }

        return Commit(ref r);
    }

    /// <summary>Adds a list-view node.</summary>
    /// <param name="dtype">The dtype this node produces.</param>
    /// <param name="length">Row count.</param>
    /// <param name="validity">Per-row validity.</param>
    /// <param name="elementsIndex">The canonical child holding the flattened elements.</param>
    /// <param name="offsets">Exactly <c>length * offsetPType.ByteWidth()</c> bytes.</param>
    /// <param name="offsetPType">Physical type of <paramref name="offsets"/>.</param>
    /// <param name="sizes">Exactly <c>length * sizePType.ByteWidth()</c> bytes.</param>
    /// <param name="sizePType">Physical type of <paramref name="sizes"/>.</param>
    /// <returns>The new node's index.</returns>
    /// <exception cref="VortexFormatException">A ptype is undefined or a buffer is the wrong length.</exception>
    public int AddListView(
        DType dtype,
        int length,
        Validity validity,
        int elementsIndex,
        VortexBuffer offsets,
        PType offsetPType,
        VortexBuffer sizes,
        PType sizePType)
    {
        if (!PTypeExtensions.IsDefined(offsetPType) || !PTypeExtensions.IsDefined(sizePType))
        {
            ArraysThrow.Format("ListView offset and size ptypes must be defined tags.");
        }

        RequireExactLength(offsets.Length, length, offsetPType.ByteWidth(), "ListView offsets");
        RequireExactLength(sizes.Length, length, sizePType.ByteWidth(), "ListView sizes");
        RequireChild(elementsIndex);

        CanonicalRecord r = New(CanonicalKind.ListView, dtype, length, validity);
        r.PType = offsetPType;
        r.SizePType = sizePType;
        r.BufferA = offsets;
        r.BufferB = sizes;
        r.ChildStart = AddChild(elementsIndex);
        r.ChildCount = 1;
        return Commit(ref r);
    }

    /// <summary>Adds a fixed-size-list node.</summary>
    /// <param name="dtype">The dtype this node produces.</param>
    /// <param name="length">Row count.</param>
    /// <param name="validity">Per-row validity.</param>
    /// <param name="elementsIndex">The canonical child holding <c>length * size</c> elements.</param>
    /// <param name="size">Elements per row; zero is legal.</param>
    /// <returns>The new node's index.</returns>
    public int AddFixedSizeList(DType dtype, int length, Validity validity, int elementsIndex, uint size)
    {
        RequireChild(elementsIndex);
        CanonicalRecord r = New(CanonicalKind.FixedSizeList, dtype, length, validity);
        r.FixedSize = size;
        r.ChildStart = AddChild(elementsIndex);
        r.ChildCount = 1;
        return Commit(ref r);
    }

    /// <summary>Adds a struct node.</summary>
    /// <param name="dtype">The dtype this node produces.</param>
    /// <param name="length">Row count.</param>
    /// <param name="validity">Per-row validity.</param>
    /// <param name="fieldIndices">One canonical child index per field, in dtype order.</param>
    /// <returns>The new node's index.</returns>
    public int AddStruct(DType dtype, int length, Validity validity, ReadOnlySpan<int> fieldIndices)
    {
        CanonicalRecord r = New(CanonicalKind.Struct, dtype, length, validity);
        r.ChildStart = _childCount;
        r.ChildCount = fieldIndices.Length;
        for (int i = 0; i < fieldIndices.Length; i++)
        {
            RequireChild(fieldIndices[i]);
            AddChild(fieldIndices[i]);
        }

        return Commit(ref r);
    }

    /// <summary>Adds an extension node. Its validity is the storage child's.</summary>
    /// <param name="dtype">The extension dtype.</param>
    /// <param name="length">Row count.</param>
    /// <param name="storageIndex">The canonical storage child.</param>
    /// <returns>The new node's index.</returns>
    public int AddExtension(DType dtype, int length, int storageIndex)
    {
        RequireChild(storageIndex);
        Validity validity = GetNode(storageIndex).Validity;
        CanonicalRecord r = New(CanonicalKind.Extension, dtype, length, validity);
        r.ChildStart = AddChild(storageIndex);
        r.ChildCount = 1;
        return Commit(ref r);
    }

    /// <summary>
    /// Adds a node of <paramref name="kind"/> with only the four common fields set. Contract §8.3
    /// exposes this through <see cref="ArrayDecodeContext.NewCanonical"/>; prefer the typed
    /// builders above, which validate their buffers.
    /// </summary>
    /// <param name="kind">The canonical form.</param>
    /// <param name="dtype">The dtype this node produces.</param>
    /// <param name="length">Row count.</param>
    /// <param name="validity">Per-row validity.</param>
    /// <returns>The new node's index.</returns>
    /// <exception cref="VortexFormatException"><paramref name="kind"/> is not a defined value.</exception>
    public int AddBare(CanonicalKind kind, DType dtype, int length, Validity validity)
    {
        if ((uint)kind > (uint)CanonicalKind.Constant)
        {
            ArraysThrow.Format($"CanonicalKind {(byte)kind} is not defined; 0..9 are.");
        }

        CanonicalRecord r = New(kind, dtype, length, validity);
        return Commit(ref r);
    }

    /// <summary>
    /// Materializes <paramref name="byteLength"/> zeroed bytes owned by this arena, for decoders
    /// that must produce values rather than borrow them.
    /// </summary>
    /// <param name="byteLength">
    /// Size in bytes. It must ALREADY have been validated against the decoded row count: this
    /// method has no way to tell a legitimate 8 MiB column from a file-supplied length that was
    /// never capped.
    /// </param>
    /// <param name="alignment">A power of two in <c>[1, VortexLimits.MaxAlignment]</c>.</param>
    /// <returns>A non-owning view over the block, valid until the next <see cref="Reset"/> call.</returns>
    /// <remarks>
    /// The block is ZEROED. The pool hands back recycled native memory, and letting a decoder that
    /// writes only part of a buffer - a bitmap's trailing bits, a short last FastLanes block -
    /// publish the rest would leak whatever the previous batch, or the previous process activity,
    /// left there.
    /// </remarks>
    /// <exception cref="VortexFormatException">
    /// <paramref name="byteLength"/> is negative or <paramref name="alignment"/> is out of range.
    /// </exception>
    public VortexBuffer Allocate(int byteLength, int alignment) => Allocate(byteLength, alignment, out _);

    /// <summary>
    /// <see cref="Allocate(int, int, out Span{byte})"/> WITHOUT the zero-fill. The caller must write
    /// every byte of <paramref name="destination"/> before anything reads it.
    /// </summary>
    /// <param name="byteLength">Size in bytes, already validated against the row count.</param>
    /// <param name="alignment">A power of two in <c>[1, VortexLimits.MaxAlignment]</c>.</param>
    /// <param name="destination">The writable block, holding WHATEVER WAS THERE BEFORE.</param>
    /// <returns>A non-owning view over the same bytes.</returns>
    /// <remarks>
    /// <para>
    /// SEPARATE METHOD RATHER THAN A FLAG, because the failure mode is silent. The blocks come from
    /// a pool that has held other files' bytes, so a buffer this hands out and the caller does not
    /// completely fill exposes those bytes AS COLUMN VALUES. That is data disclosure, not a wrong
    /// answer, and it passes every differential test whose oracle is another run of this reader over
    /// the same file. A boolean argument would let a call site acquire the fast path by accident; a
    /// distinct name cannot be typed by mistake.
    /// </para>
    /// <para>
    /// The oracle that CAN catch it is the Rust cross-check, which reads what this library wrote
    /// with an implementation that did not produce the bytes.
    /// </para>
    /// <para>
    /// <b>Eligibility is "provably writes every byte", not "probably".</b> A decoder whose zero-width
    /// or zero-length branch falls through to the buffer's existing contents is NOT eligible, however
    /// rare that branch is - <c>fastlanes.bitpacked</c> at bit width 0 qualifies only because it
    /// clears the span itself rather than inheriting a cleared one.
    /// </para>
    /// </remarks>
    public VortexBuffer AllocateUninitialized(int byteLength, int alignment, out Span<byte> destination)
    {
        NativeSegmentOwner owner = _pool.Rent(byteLength, alignment);
        if (_ownedCount == _owned.Length)
        {
            Array.Resize(ref _owned, Grow(_owned.Length));
        }

        _owned[_ownedCount++] = owner;
        destination = owner.WritableSpan;
        return owner.Buffer;
    }
    /// <summary>
    /// <see cref="Allocate(int, int)"/>, additionally handing back a writable span over the block.
    /// This is the only way a decoder gets writable memory: a decoder that news up a
    /// <c>byte[]</c> breaks the per-batch zero-allocation invariant, and one that writes into a
    /// <see cref="VortexBuffer"/> that came from the file corrupts the mapped file.
    /// </summary>
    /// <param name="byteLength">Size in bytes, already validated against the row count.</param>
    /// <param name="alignment">A power of two in <c>[1, VortexLimits.MaxAlignment]</c>.</param>
    /// <param name="destination">The writable block, zero-filled.</param>
    /// <returns>A non-owning view over the same bytes.</returns>
    /// <exception cref="VortexFormatException">
    /// <paramref name="byteLength"/> is negative or <paramref name="alignment"/> is out of range.
    /// </exception>
    public VortexBuffer Allocate(int byteLength, int alignment, out Span<byte> destination)
    {
        NativeSegmentOwner owner = _pool.Rent(byteLength, alignment);
        if (_ownedCount == _owned.Length)
        {
            Array.Resize(ref _owned, Grow(_owned.Length));
        }

        _owned[_ownedCount++] = owner;
        destination = owner.WritableSpan;
        destination.Clear();
        return owner.Buffer;
    }

    // ------------------------------------------------------------------------------ internals

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ref readonly CanonicalRecord RecordRef(int index)
    {
        if ((uint)index >= (uint)_recordCount)
        {
            ArraysThrow.CanonicalIndex(index, _recordCount);
        }

        return ref _records[index];
    }

    /// <summary>The same record, writable: only the constant memo uses it. Z1b-c2b2.</summary>
    /// <param name="index">The node's index.</param>
    /// <returns>A mutable reference into the record array.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ref CanonicalRecord RecordRefMutable(int index)
    {
        if ((uint)index >= (uint)_recordCount)
        {
            ArraysThrow.CanonicalIndex(index, _recordCount);
        }

        return ref _records[index];
    }

    internal int ChildAt(int slot)
    {
        if ((uint)slot >= (uint)_childCount)
        {
            ArraysThrow.CanonicalIndex(slot, _childCount);
        }

        return _children[slot];
    }

    internal VortexBuffer DataBufferAt(int slot)
    {
        if ((uint)slot >= (uint)_dataBufferCount)
        {
            return ArraysThrow.BufferIndex(slot, _dataBufferCount);
        }

        return _dataBuffers[slot];
    }

    private static CanonicalRecord New(CanonicalKind kind, DType dtype, int length, Validity validity)
    {
        if (length < 0)
        {
            ArraysThrow.Format($"A canonical node cannot have {length} rows.");
        }

        return new CanonicalRecord
        {
            Kind = kind,
            DType = dtype,
            Length = length,
            Validity = validity,
            Materialized = -1,
            ChildStart = -1,
            DataBufferStart = -1,
        };
    }

    private static void RequireExactLength(int actual, int length, int width, string what)
    {
        long expected = (long)length * width;
        if (actual != expected)
        {
            ArraysThrow.Format(
                $"A canonical {what} of {length} rows needs exactly {expected} bytes; the buffer " +
                $"holds {actual}.");
        }
    }

    private void RequireChild(int index)
    {
        if ((uint)index >= (uint)_recordCount)
        {
            ArraysThrow.CanonicalIndex(index, _recordCount);
        }
    }

    private int AddChild(int nodeIndex)
    {
        if (_childCount == _children.Length)
        {
            Array.Resize(ref _children, Grow(_children.Length));
        }

        int slot = _childCount;
        _children[slot] = nodeIndex;
        _childCount = slot + 1;
        return slot;
    }

    private void AddDataBuffer(VortexBuffer buffer)
    {
        if (_dataBufferCount == _dataBuffers.Length)
        {
            Array.Resize(ref _dataBuffers, Grow(_dataBuffers.Length));
        }

        _dataBuffers[_dataBufferCount++] = buffer;
    }

    /// <summary>Doubles a full array, refusing to overflow into a negative capacity.</summary>
    private static int Grow(int capacity)
    {
        if (capacity > int.MaxValue / 2)
        {
            ArraysThrow.Format($"A canonical arena of more than {capacity} entries cannot be allocated.");
        }

        return Math.Max(capacity * 2, 4);
    }

    /// <summary>
    /// The bytes a node and everything under it occupy, counted once per buffer.
    /// </summary>
    /// <param name="nodeIndex">The node.</param>
    /// <returns>The total, in bytes.</returns>
    /// <exception cref="VortexFormatException"><paramref name="nodeIndex"/> is out of range.</exception>
    /// <remarks>
    /// The reference's <c>nbytes()</c>, and it exists for the same caller: the writer's
    /// repartitioner decides when a block is large enough, and "large enough" is a size in
    /// UNCOMPRESSED bytes because that is the only size available before the block is compressed.
    /// A shared buffer is counted once per reference, as upstream's is -- the figure is a budget,
    /// not an allocation report.
    /// </remarks>
    internal long ByteSize(int nodeIndex)
    {
        if ((uint)nodeIndex >= (uint)_recordCount)
        {
            ArraysThrow.CanonicalIndex(nodeIndex, _recordCount);
        }

        CanonicalRecord record = _records[nodeIndex];
        long total = record.BufferA.Length + record.BufferB.Length;

        for (int i = 0; i < record.DataBufferCount; i++)
        {
            total += _dataBuffers[record.DataBufferStart + i].Length;
        }

        if (record.Validity.Kind == ValidityKind.Bitmap)
        {
            total += ByteSize(record.Validity.CanonicalNodeIndex);
        }

        for (int i = 0; i < record.ChildCount; i++)
        {
            total += ByteSize(_children[record.ChildStart + i]);
        }

        return total;
    }

    /// <summary>
    /// Re-creates a node and everything under it in this arena as records whose buffers are still
    /// <b>views</b> onto <paramref name="source"/>'s storage. Nothing is copied but the records.
    /// </summary>
    /// <param name="source">The arena holding the node, whose memory the result borrows.</param>
    /// <param name="sourceIndex">The node to reference.</param>
    /// <returns>The reference's index in this arena.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is <see langword="null"/>.</exception>
    /// <exception cref="VortexFormatException"><paramref name="sourceIndex"/> is out of range.</exception>
    /// <remarks>
    /// <para>
    /// THE RESULT BORROWS, SO THE CALLER OWES A LIFETIME ARGUMENT -- the same one every other
    /// cross-arena slice already owes. <see cref="CopyFrom"/> materializes bytes because its three
    /// callers keep the result past the arena it came from; this one exists for the caller that
    /// does not, and for which materializing is the whole cost.
    /// </para>
    /// <para>
    /// WHY IT EXISTS AT ALL, WHEN A SLICE IS ALREADY A VIEW. Every canonical kind but one narrows
    /// across arenas for free, because everything a narrowed record needs is a
    /// <see cref="VortexBuffer"/> and a buffer is a window, not an owner. <c>ListView</c> is the
    /// exception: its offsets are absolute into an elements CHILD named by an arena index, and an
    /// index means nothing in another arena. The child therefore has to EXIST here -- but existing
    /// is a record, not a copy of its bytes. Copying them made a batch of a large list chunk cost
    /// the whole child, which is the scan quadratic again, restricted to one dtype.
    /// </para>
    /// </remarks>
    internal int ReferenceFrom(CanonicalArena source, int sourceIndex)
    {
        ArgumentNullException.ThrowIfNull(source);
        if ((uint)sourceIndex >= (uint)source._recordCount)
        {
            ArraysThrow.CanonicalIndex(sourceIndex, source._recordCount);
        }

        // By value up front, for CopyFrom's reason: the recursive calls append to this arena and
        // may resize `_records`.
        CanonicalRecord src = source._records[sourceIndex];

        Validity validity = src.Validity;
        if (validity.Kind == ValidityKind.Bitmap)
        {
            validity = Validity.Bitmap(ReferenceFrom(source, validity.CanonicalNodeIndex));
        }

        int childCount = src.ChildCount;
        int childStart = ReserveChildren(childCount);
        for (int i = 0; i < childCount; i++)
        {
            int child = ReferenceFrom(source, source._children[src.ChildStart + i]);

            // The field is re-read after the recursion on purpose: a deeper copy may have grown
            // the child array, and `Array.Resize` leaves the old one behind.
            _children[childStart + i] = child;
        }

        CanonicalRecord copy = src;
        copy.Validity = validity;
        copy.ChildStart = childCount > 0 ? childStart : -1;
        copy.ChildCount = childCount;
        copy.DataBufferStart = -1;
        copy.DataBufferCount = 0;

        int index = Commit(ref copy);

        if (src.DataBufferCount > 0)
        {
            _records[index].DataBufferStart = _dataBufferCount;
            _records[index].DataBufferCount = src.DataBufferCount;
            for (int i = 0; i < src.DataBufferCount; i++)
            {
                AddDataBuffer(source._dataBuffers[src.DataBufferStart + i]);
            }
        }

        return index;
    }

    /// <summary>
    /// Copies a node and everything under it out of <paramref name="source"/> and into this arena,
    /// materializing every byte so the result outlives the arena it came from.
    /// </summary>
    /// <param name="source">The arena holding the node. May be this one.</param>
    /// <param name="sourceIndex">The node to copy.</param>
    /// <returns>The copy's index in this arena.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is <see langword="null"/>.</exception>
    /// <exception cref="VortexFormatException"><paramref name="sourceIndex"/> is out of range.</exception>
    /// <remarks>
    /// <para>
    /// THE POINT IS THE BYTES, NOT THE RECORD. A <see cref="CanonicalRecord"/> is mostly indices and
    /// <see cref="VortexBuffer"/> views, and a view is a window onto memory this arena's
    /// <see cref="Reset"/> hands back to the pool. Copying the record alone would produce something
    /// that reads correctly until the next batch refills the storage underneath it and then reads
    /// live, plausible, wrong data - which is not a hypothetical: it is what
    /// <c>ArenaLifetimeTests</c> demonstrates, and it is what made the shared-dictionary layout come
    /// back permuted twice with three unrelated causes eliminated in between.
    /// </para>
    /// <para>
    /// THREE SEPARATE ITEMS WANT THIS ONE FUNCTION. A dictionary shared across chunks must keep its
    /// entries past the batch that first saw them (§3a); <c>CanonicalConcat</c> cannot coalesce
    /// chunks across arenas without it (§3c); and a chunk larger than a batch is decoded once per
    /// batch because the decoded node cannot be kept (the scan quadratic, 79x-129x). None of them is
    /// about dictionaries, concatenation or scanning: all three are about lifetime.
    /// </para>
    /// <para>
    /// Recursive, over the schema rather than over the rows - depth is the nesting of the dtype, so
    /// a struct of lists of structs recurses three times whatever its row count.
    /// </para>
    /// <para>
    /// INTERNAL UNTIL A CONSUMER JUSTIFIES THE SHAPE. <see cref="CanonicalArena"/> is public and a
    /// caller building batches could plausibly want this, but the three items that asked for it have
    /// not landed yet, and each could want a different signature - a subtree, a row range, a
    /// retained handle. Publishing before one of them exists is how an API becomes permanent by
    /// accident; promoting later costs nothing and un-shipping costs everything.
    /// </para>
    /// </remarks>
    internal int CopyFrom(CanonicalArena source, int sourceIndex)
    {
        ArgumentNullException.ThrowIfNull(source);
        if ((uint)sourceIndex >= (uint)source._recordCount)
        {
            ArraysThrow.CanonicalIndex(sourceIndex, source._recordCount);
        }

        // Read by value up front. The recursive calls below append to THIS arena, which may resize
        // `_records`, and holding a `ref` into the array across that would be a use-after-move when
        // source and destination are the same arena.
        CanonicalRecord src = source._records[sourceIndex];

        // Validity first: its bitmap is a node like any other, and the copy must name the new index.
        Validity validity = src.Validity;
        if (validity.Kind == ValidityKind.Bitmap)
        {
            validity = Validity.Bitmap(CopyFrom(source, validity.CanonicalNodeIndex));
        }

        // A VARBINVIEW IS COPIED COMPACT, and that is not an optimization but the difference between
        // linear and quadratic. WRITE-AUDIT.md §3.3: a slice of a VarBinView keeps its data buffers
        // WHOLE -- rightly, because a slice is a view and that is what makes the string encodings
        // reach their scan ceiling (CanonicalSlice.cs:50-57). Copying such a slice buffer by buffer
        // therefore materializes every byte of the array it was cut from, and the writer's carried
        // remainder, re-copied once per block, dragged the whole heap of every block already
        // emitted: `fsst` copied 384 255 MB to write 929 140 bytes at a million rows.
        if (src.Kind == CanonicalKind.VarBinView)
        {
            return CompactVarBinView(source, in src, validity);
        }

        // Children next, gathered before anything is committed so the block stays contiguous: a
        // child's own copy appends records, and interleaving those with this node's child slots
        // would scatter them.
        int childCount = src.ChildCount;
        int childStart = ReserveChildren(childCount);
        for (int i = 0; i < childCount; i++)
        {
            int child = CopyFrom(source, source._children[src.ChildStart + i]);
            _children[childStart + i] = child;
        }

        CanonicalRecord copy = src;
        copy.Validity = validity;
        copy.BufferA = CopyBuffer(src.BufferA);
        copy.BufferB = CopyBuffer(src.BufferB);
        copy.ChildStart = childCount > 0 ? childStart : -1;
        copy.ChildCount = childCount;
        copy.DataBufferStart = -1;
        copy.DataBufferCount = 0;

        int index = Commit(ref copy);

        if (src.DataBufferCount > 0)
        {
            _records[index].DataBufferStart = _dataBufferCount;
            _records[index].DataBufferCount = src.DataBufferCount;
            for (int i = 0; i < src.DataBufferCount; i++)
            {
                AddDataBuffer(CopyBuffer(source._dataBuffers[src.DataBufferStart + i]));
            }
        }

        return index;
    }

    /// <summary>
    /// The <see cref="CanonicalKind.VarBinView"/> arm of <see cref="CopyFrom"/>: the views travel,
    /// and of the data buffers only the bytes those views actually name.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The result holds ONE data buffer, in row order, and every buffered view points at it. That
    /// is a gather -- one memcpy per non-inline row -- against a copy of whole buffers, so it is
    /// cheaper exactly when the node is a window and never more than a constant factor dearer when
    /// it is not. Inline views (65 % of them on `chunked_varbinview`) carry their bytes inside the
    /// view and cost nothing here.
    /// </para>
    /// <para>
    /// THE BOUNDS ARE CHECKED ON EVERY BUFFERED VIEW, Class I, for
    /// <see cref="Decoders.Canonical.CanonicalConcat"/>'s reason: these views may have been rebased
    /// by a concat since the decoder validated them, and a copy is not a place to start trusting
    /// them.
    /// </para>
    /// </remarks>
    private int CompactVarBinView(CanonicalArena source, in CanonicalRecord src, Validity validity)
    {
        const int ViewSize = Decoders.Canonical.CanonicalSupport.ViewSize;
        const uint MaxInline = Decoders.Canonical.CanonicalSupport.MaxInlineViewLength;

        int rows = src.Length;
        int viewBytes = rows * ViewSize;
        ReadOnlySpan<uint> incoming = MemoryMarshal.Cast<byte, uint>(src.BufferA.Span[..viewBytes]);

        // Sized before anything is written: one pass to learn how many bytes the rows name, so the
        // gather below writes into a buffer it never has to grow.
        long referenced = 0;
        for (int j = 0; j < rows; j++)
        {
            uint size = incoming[j * 4];
            if (size > MaxInline)
            {
                referenced += size;
            }
        }

        VortexBuffer views = VortexBuffer.Empty;
        Span<byte> writable = default;
        if (rows > 0)
        {
            views = AllocateUninitialized(viewBytes, 1, out writable);
            src.BufferA.Span[..viewBytes].CopyTo(writable);
        }

        Span<byte> into = default;
        VortexBuffer data = referenced == 0
            ? VortexBuffer.Empty
            : AllocateUninitialized(checked((int)referenced), 1, out into);

        Span<uint> words = MemoryMarshal.Cast<byte, uint>(writable);
        int at = 0;
        for (int j = 0; j < rows; j++)
        {
            int w = j * 4;
            uint size = words[w];
            if (size <= MaxInline)
            {
                continue;
            }

            uint index = words[w + 2];
            if (index >= (uint)src.DataBufferCount)
            {
                ArraysThrow.Format(
                    $"Row {j} references data buffer {index}; the node has {src.DataBufferCount}.");
            }

            ReadOnlySpan<byte> from = source._dataBuffers[src.DataBufferStart + (int)index].Span;
            uint offset = words[w + 3];
            if ((ulong)offset + size > (ulong)from.Length)
            {
                ArraysThrow.Format(
                    $"Row {j} names bytes {offset}..{offset + size} of a {from.Length}-byte buffer.");
            }

            from.Slice((int)offset, (int)size).CopyTo(into[at..]);
            words[w + 2] = 0;
            words[w + 3] = (uint)at;
            at += (int)size;
        }

        System.Threading.Interlocked.Add(ref BytesMaterialized, viewBytes + at);

        CanonicalRecord copy = New(CanonicalKind.VarBinView, src.DType, rows, validity);
        copy.BufferA = views;
        int index2 = Commit(ref copy);
        if (at > 0)
        {
            _records[index2].DataBufferStart = _dataBufferCount;
            _records[index2].DataBufferCount = 1;
            AddDataBuffer(data);
        }

        return index2;
    }

    /// <summary>
    /// Bytes <see cref="CopyFrom"/> has materialized, across every scan in the process.
    /// </summary>
    /// <remarks>
    /// Internal and diagnostic, for FlatLayoutReader.ValuesDecoded's reason: the quantity that
    /// catches a per-batch copy is exact and machine-independent, where the timing that catches it
    /// needs a million rows and a quiet machine. A cross-arena window is supposed to cost records
    /// and nothing else; this is what lets a test assert that rather than hope it.
    /// </remarks>
    internal static long BytesMaterialized;

    /// <summary>Materializes one buffer's bytes into storage this arena owns.</summary>
    /// <remarks>
    /// An empty buffer copies to an empty buffer rather than to a zero-length allocation: renting a
    /// block to hold nothing would charge every copy of a non-nullable column for a validity buffer
    /// that does not exist.
    /// </remarks>
    private VortexBuffer CopyBuffer(VortexBuffer source)
    {
        if (source.Length == 0)
        {
            return VortexBuffer.Empty;
        }

        // Uninitialized: the copy below writes every byte of it. See AllocateUninitialized for why
        // that bar is where it is.
        System.Threading.Interlocked.Add(ref BytesMaterialized, source.Length);
        VortexBuffer copy = AllocateUninitialized(source.Length, 1, out Span<byte> destination);
        source.Span.CopyTo(destination);
        return copy;
    }

    private int Commit(ref CanonicalRecord record)
    {
        if (_recordCount == _records.Length)
        {
            Array.Resize(ref _records, Grow(_records.Length));
        }

        int index = _recordCount;
        _records[index] = record;
        _recordCount = index + 1;
        return index;
    }
}

/// <summary>One decoded node, flattened. Never public: <see cref="CanonicalNode"/> is the view.</summary>
[StructLayout(LayoutKind.Auto)]
internal struct CanonicalRecord
{
    internal DType DType;
    internal Validity Validity;
    internal VortexBuffer BufferA;
    internal VortexBuffer BufferB;
    internal int Length;
    internal int BitOffset;
    internal int ChildStart;
    internal int ChildCount;
    internal int DataBufferStart;
    internal int DataBufferCount;
    internal uint FixedSize;

    /// <summary>
    /// For a <see cref="CanonicalKind.Constant"/> node: the materialized twin, once someone has
    /// asked for a contiguous span. -1 until then. PERF-AUDIT-v2.md Z1b-c2b2.
    /// </summary>
    internal int Materialized;

    internal CanonicalKind Kind;
    internal PType PType;
    internal PType SizePType;
    internal DecimalStorageType Storage;
    internal byte Precision;
    internal sbyte Scale;
}
