// The running distinct table of docs/11-write-strategy.md §3.2.2: one per column, opened with a
// chunk's first row and closed with the chunk, mapping each value to the CODE it was handed in
// order of first appearance -- which is exactly what `ColumnCompressor.Dictionary` computed by
// walking the assembled chunk at emit time, moved to where the rows arrive.
//
// WHY IT MOVES. The probe was 21 % to 57 % of the write on every string-bearing encoding of the axis
// (57 % of `dict`, 50 % of `onpair`, 43 % of `varbinview`), measured by running it twice. Not because
// hashing is slow but because it was a WHOLE SECOND WALK over a column the ingest pass had just read
// -- and, on the columns where the dictionary loses, a walk to discover that it loses. Here the
// value is already in a register when the statistics pass loads it; probing it is the hash and one
// compare, on rows that are in cache for free. §3.2.2's other consumers -- the Bloom sizing, the
// postings and sorted-run builders of docs/10-indexes.md §7 -- are fed from this same table, which
// is why 10 §7.3 can say that no index builder adds a pass over the column.
//
// IT OWNS ITS KEYS AND NEVER REFERENCES A ROW OF THE BATCH, because a chunk straddles batches and
// the previous batch's arena is recycled the moment the next one is decoded. A string's bytes are
// copied into the key heap the first time the value is seen, one copy per DISTINCT value, and the
// heap in code order is what the dictionary's values child becomes (§3.5): `BuildValues` lays the
// entries out as a canonical column straight from the table, and the gather over the chunk's rows
// that used to produce that child is not made. What the table does keep per row is the row's
// chunk-relative POSITION: `FirstRows[code]` is where a code first occurred, kept for the reference
// chooser and the differential that compares the two.
//
// THE LIFETIME IS THE CHUNK'S, AND THE CHUNK IS DECIDED LATE. The writer emits all the WHOLE blocks
// pending when the byte threshold is crossed and carries the partial tail into the next chunk
// (`VortexFileWriter.EmitBlockAsync`), so by the time a chunk closes this table has already probed
// rows that belong to the next one. First-seen order makes half of that harmless: the codes the
// chunk's rows use are exactly [0, C) where C is the count when its last block closed, so the tail's
// entries sit above C and are simply not part of the chunk. The other half is not harmless -- a tail
// row may hold a value whose first occurrence was EMITTED, and in the next chunk that value is new
// -- so the tail is re-probed into a fresh table after every emission. It is less than a block.
//
// CODES ARE BYTE-IDENTICAL TO TODAY'S BY CONSTRUCTION: `code = distinct++` in row order is the rule
// of the walk this replaces, and the hash decides which slot a value lands in and nothing else. The
// one place the plan could differ is the entry cap, which the old walk did not have: a chunk with
// more than `MaxEntries` distinct values loses its dictionary candidate here where the old walk
// would have priced it. On this corpus it does not happen, and `WrittenSizeTests` is what says so.
//
// EVERY BUFFER IS RENTED PER CHUNK AND RETURNED AT RESET, as the walk's were: the old form rented
// three `int[rows]` per chunk and returned them, so a thousand columns shared one pool, and
// `WriteAllocationTests` holds the writer to that. A first version allocated the slot arrays with
// `new` and held them per column, and a high-cardinality column of 8 193 rows cost a megabyte of
// garbage against a 70 kB ceiling.
using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;
using Vorticity.Arrays;
using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Buffers;
using Vorticity.Types;
using Vorticity.Types.Numerics;

namespace Vorticity.Writing;

/// <summary>A column's distinct values over one chunk, with a code per row, built as rows arrive.</summary>
internal sealed class DistinctTable
{
    /// <summary>Slots at open, doubled as entries grow; a low-cardinality column stays here.</summary>
    private const int InitialCapacity = 64;

    /// <summary>
    /// Entries beyond which the chunk refuses its dictionary candidate rather than keep growing.
    /// </summary>
    /// <remarks>
    /// The writer's budget of §3.2.2, in entries rather than bytes for now. A dictionary with a
    /// million entries wins only on a chunk with many more rows than that, which
    /// <c>DataBlockTargetBytes</c> does not produce; the cap is a safety bound on memory, not a
    /// pricing rule, and the pricing rule -- the exact size formula -- is the chooser's.
    /// </remarks>
    private const int MaxEntries = 1 << 20;

    /// <summary>A view is sixteen bytes: length, then twelve of value inline or a buffer and an offset.</summary>
    private const int ViewSize = 16;

    private enum Shape : byte
    {
        /// <summary>A bool column: the key is the bit.</summary>
        Bits,

        /// <summary>One, two, four or eight bytes: the key is the value's bits, zero-extended.</summary>
        Fixed,

        /// <summary>
        /// The key is a byte string owned in the heap: a view column's values, or a decimal wider
        /// than a word -- i128 and i256 are sixteen and thirty-two bytes, and a key that does not
        /// fit a slot is a key that lives in the heap, whatever its dtype.
        /// </summary>
        Bytes,
    }

    private readonly Shape _shape;
    private readonly CanonicalKind _kind;

    /// <summary>Bytes per value for a fixed-width column, 0 for a view column.</summary>
    private readonly int _width;

    // THE SLOTS, as parallel arrays rather than a struct array so that the probe touches only the
    // arrays its shape needs. `_slotCode` is code + 1 so that zero means empty and the clear at
    // reset is one memset. The capacity is `_mask + 1`, never an array's length: a rented array is
    // as long as the pool felt like.
    private int[] _slotCode;
    private ulong[] _slotKey;
    private int[]? _slotOffset;
    private int[]? _slotLength;
    private int _mask;

    private byte[]? _heap;
    private int _heapUsed;

    // PER CODE, what the values child needs: the row of first occurrence, and the key itself --
    // the word for a fixed-width kind, the heap span for a byte kind -- in code order, which is
    // the order the entries are written in.
    private int[] _firstRows;
    private ulong[] _codeKey;
    private int[]? _codeOffset;
    private int[]? _codeLength;

    private int[] _codes;
    private int _rows;
    private int _distinct;
    private int _nullCode = -1;
    private bool _abandoned;

    /// <summary>What the previous chunk needed, so the next one is rented at that size in one go.</summary>
    private int _lastDistinct;
    private int _lastRows;
    private int _lastHeap;

    private DistinctTable(Shape shape, CanonicalKind kind, int width)
    {
        _shape = shape;
        _kind = kind;
        _width = width;
        _slotCode = [];
        _slotKey = [];
        _firstRows = [];
        _codeKey = [];
        _codes = [];
        _mask = -1;
    }

    /// <summary>Rows probed since the last <see cref="Reset"/>, i.e. the chunk so far.</summary>
    internal int Rows => _rows;

    /// <summary>Distinct values seen, the null counting as one.</summary>
    internal int Distinct => _distinct;

    /// <summary>For a view column, the bytes of the distinct non-null values: the heap's size.</summary>
    internal long HeapBytes => _heapUsed;

    /// <summary>
    /// Whether the chunk exceeded <see cref="MaxEntries"/>, after which nothing here is a summary.
    /// </summary>
    internal bool Abandoned => _abandoned;

    /// <summary>One code per probed row, in row order.</summary>
    internal ReadOnlySpan<int> Codes => _codes.AsSpan(0, _rows);

    /// <summary>Per code, the chunk-relative row where it first occurred.</summary>
    internal ReadOnlySpan<int> FirstRows => _firstRows.AsSpan(0, _distinct);

    /// <summary>
    /// A table for the column <paramref name="node"/> is an instance of, or <see langword="null"/>
    /// for a kind that has no row equality and is offered no dictionary.
    /// </summary>
    /// <param name="node">Any node of the column; only its kind and width are read.</param>
    internal static DistinctTable? For(CanonicalNode node) => node.Kind switch
    {
        // NO TABLE BELOW THREE BYTES OF WIDTH, and it is arithmetic, not policy: a column of `w`
        // bytes has at most 2^(8w) distinct values, so its codes are `w` bytes wide too -- as wide
        // as the values they replace -- and the entries and the framing come on top. A dictionary
        // of a bool, a byte or a short can never be smaller than the column, so a table over one
        // is a hash and a probe per row for a verdict the chooser reaches without it. The end-of-
        // refactor measurement priced that at ×6 to ×9,5 on the bool family.
        CanonicalKind.Bool => null,
        CanonicalKind.Primitive => node.PType.ByteWidth() <= 2
            ? null
            : new DistinctTable(Shape.Fixed, node.Kind, node.PType.ByteWidth()),

        // A constant batch wears the canonical constant form, whose dtype is Primitive by
        // construction (ConstantCanonicalizer builds it for no other kind); the next batch of the
        // same column may well be an ordinary primitive, so the table is the PRIMITIVE one and the
        // constant is a way of feeding it, not a kind of its own.
        CanonicalKind.Constant => node.DType.PType.ByteWidth() <= 2
            ? null
            : new DistinctTable(Shape.Fixed, CanonicalKind.Primitive, node.DType.PType.ByteWidth()),
        CanonicalKind.Decimal => DecimalStorage.ByteWidth(node.Storage) is int w && w <= 8
            ? (w <= 2 ? null : new DistinctTable(Shape.Fixed, node.Kind, w))
            : new DistinctTable(Shape.Bytes, node.Kind, w),
        CanonicalKind.VarBinView => new DistinctTable(Shape.Bytes, node.Kind, 0),
        _ => null,
    };

    /// <summary>
    /// Whether a column of this kind gets a table at all — the kinds the chooser offers a
    /// dictionary, so that the writer can count a chunk the table failed to serve.
    /// </summary>
    /// <param name="kind">The chunk's canonical kind, as the emitter sees it.</param>
    internal static bool Serves(CanonicalKind kind) => kind is CanonicalKind.Bool
        or CanonicalKind.Primitive or CanonicalKind.Constant or CanonicalKind.Decimal
        or CanonicalKind.VarBinView;

    /// <summary>
    /// Rents the chunk's buffers on its first probe, sized by the previous chunk's needs.
    /// </summary>
    /// <remarks>
    /// RENTED PER CHUNK AND RETURNED AT RESET, not held per column for the writer's lifetime -- the
    /// discipline the walk this replaces had, and the one docs/11-write-strategy.md §3.7 asks for:
    /// a thousand columns share one pool, not a thousand tables. Sized from the last chunk so that
    /// a steady column rents once and grows never; the very first chunk of a column starts small
    /// and doubles, which is the price of not knowing.
    /// </remarks>
    /// <param name="rows">Rows the first range brings, a floor for the codes buffer.</param>
    private void Open(int rows)
    {
        int capacity = Math.Max(
            InitialCapacity, (int)BitOperations.RoundUpToPowerOf2((uint)((_lastDistinct * 2) + 1)));
        _slotCode = ArrayPool<int>.Shared.Rent(capacity);
        _slotKey = ArrayPool<ulong>.Shared.Rent(capacity);
        _mask = capacity - 1;
        Array.Clear(_slotCode, 0, capacity);
        if (_shape == Shape.Bytes)
        {
            _slotOffset = ArrayPool<int>.Shared.Rent(capacity);
            _slotLength = ArrayPool<int>.Shared.Rent(capacity);
            _codeOffset = ArrayPool<int>.Shared.Rent(capacity);
            _codeLength = ArrayPool<int>.Shared.Rent(capacity);
            _heap = ArrayPool<byte>.Shared.Rent(Math.Max(1024, _lastHeap));
        }

        _firstRows = ArrayPool<int>.Shared.Rent(capacity);
        _codeKey = ArrayPool<ulong>.Shared.Rent(capacity);
        _codes = ArrayPool<int>.Shared.Rent(Math.Max(Math.Max(256, rows), _lastRows));
    }

    /// <summary>
    /// Forgets the chunk: every buffer back to the pool, its size remembered for the next one.
    /// </summary>
    internal void Reset()
    {
        _lastDistinct = _distinct;
        _lastRows = _rows;
        _lastHeap = _heapUsed;

        if (_mask >= 0)
        {
            ArrayPool<int>.Shared.Return(_slotCode);
            ArrayPool<ulong>.Shared.Return(_slotKey);
            ArrayPool<int>.Shared.Return(_firstRows);
            ArrayPool<ulong>.Shared.Return(_codeKey);
            ArrayPool<int>.Shared.Return(_codes);
            if (_slotOffset is not null)
            {
                ArrayPool<int>.Shared.Return(_slotOffset);
                ArrayPool<int>.Shared.Return(_slotLength!);
                ArrayPool<int>.Shared.Return(_codeOffset!);
                ArrayPool<int>.Shared.Return(_codeLength!);
                ArrayPool<byte>.Shared.Return(_heap!);
            }
        }

        _slotCode = [];
        _slotKey = [];
        _slotOffset = null;
        _slotLength = null;
        _codeOffset = null;
        _codeLength = null;
        _heap = null;
        _firstRows = [];
        _codeKey = [];
        _codes = [];
        _mask = -1;
        _rows = 0;
        _distinct = 0;
        _nullCode = -1;
        _heapUsed = 0;
        _abandoned = false;
    }

    /// <summary>
    /// Probes rows <c>[start, start + count)</c> of <paramref name="node"/>, in order, as the next
    /// rows of the chunk.
    /// </summary>
    /// <param name="arena">The arena holding the batch.</param>
    /// <param name="node">The column inside the batch.</param>
    /// <param name="start">First row of the range, inside the node.</param>
    /// <param name="count">How many rows.</param>
    internal void Probe(CanonicalArena arena, CanonicalNode node, int start, int count)
    {
        if (_abandoned || count <= 0)
        {
            return;
        }

        bool constant = node.Kind == CanonicalKind.Constant;
        if ((constant ? CanonicalKind.Primitive : node.Kind) != _kind)
        {
            // The schema is the file's and does not change between batches; a node of another
            // kind is not a state this can be in, and abandoning is the answer that costs a pass
            // rather than a wrong plan.
            _abandoned = true;
            return;
        }

        if (_mask < 0)
        {
            Open(count);
        }

        EnsureCodes(_rows + count);
        ValidityMask mask = ValidityMask.From(arena, node.Validity);
        if (constant)
        {
            ProbeConstant(node, in mask, start, count);
            return;
        }

        switch (_shape)
        {
            case Shape.Bits:
                ProbeBits(node, in mask, start, count);
                return;

            case Shape.Fixed:
                switch (_width)
                {
                    case 1: ProbeFixed<byte>(node, in mask, start, count); return;
                    case 2: ProbeFixed<ushort>(node, in mask, start, count); return;
                    case 4: ProbeFixed<uint>(node, in mask, start, count); return;
                    default: ProbeFixed<ulong>(node, in mask, start, count); return;
                }

            default:
                if (_width == 0)
                {
                    ProbeViews(node, in mask, start, count);
                }
                else
                {
                    ProbeWide(node, in mask, start, count);
                }

                return;
        }
    }

    /// <summary>
    /// The dictionary's values child, laid out in code order straight from what the table owns:
    /// the entries <c>[0, entries)</c> as a canonical column of <paramref name="column"/>'s kind,
    /// with the null entry null.
    /// </summary>
    /// <remarks>
    /// THE SAME COLUMN THE GATHER USED TO PRODUCE, without the gather. `CanonicalFilter.Apply` over
    /// the first rows of each code read every entry back out of the chunk and, for strings, copied
    /// its view while sharing the chunk's whole heap; this writes the entry from the key the table
    /// already owns, and for strings the data buffer is the key heap itself -- exactly the entries'
    /// bytes, in code order, which is what the varbin form of the child writes either way.
    /// <para>
    /// THE VALIDITY IS THE FILTER'S, RULE FOR RULE: a column whose validity is not a bitmap keeps
    /// it; a bitmap column whose entries hold no null becomes all-valid, one whose only entry is
    /// the null all-invalid, and any other gets a bitmap with one bit clear. The wire form of the
    /// child depends on which of the three it is, so the choice has to be the same choice.
    /// </para>
    /// <para>
    /// A NULL ENTRY'S PAYLOAD IS ZERO. The gather copied whatever bytes the null row happened to
    /// hold; the format says nothing about them and nothing reads them. Sizes are identical.
    /// </para>
    /// </remarks>
    /// <param name="arena">The arena the child is built in — the chunk's.</param>
    /// <param name="column">The chunk's own node, for its dtype, width and validity.</param>
    /// <param name="entries">How many codes are the chunk's: the count at its last block's close.</param>
    /// <returns>The child's node index.</returns>
    internal int BuildValues(CanonicalArena arena, in CanonicalNode column, int entries)
    {
        Validity validity = ValuesValidity(arena, column.Validity, entries);
        switch (_shape)
        {
            case Shape.Bits:
            {
                int bytes = CanonicalSupport.BitmapByteCount(entries);
                VortexBuffer bits = arena.Allocate(Math.Max(bytes, 1), 1, out Span<byte> destination);
                for (int code = 0; code < entries; code++)
                {
                    if (_codeKey[code] != 0)
                    {
                        CanonicalSupport.SetBit(destination, code);
                    }
                }

                return arena.AddBool(column.DType, entries, validity, bits, 0);
            }

            case Shape.Fixed:
            {
                VortexBuffer values = arena.Allocate(entries * _width, _width, out Span<byte> destination);
                for (int code = 0; code < entries; code++)
                {
                    Span<byte> slot = destination.Slice(code * _width, _width);
                    switch (_width)
                    {
                        case 1: slot[0] = (byte)_codeKey[code]; break;
                        case 2: BinaryPrimitives.WriteUInt16LittleEndian(slot, (ushort)_codeKey[code]); break;
                        case 4: BinaryPrimitives.WriteUInt32LittleEndian(slot, (uint)_codeKey[code]); break;
                        default: BinaryPrimitives.WriteUInt64LittleEndian(slot, _codeKey[code]); break;
                    }
                }

                return column.Kind == CanonicalKind.Decimal
                    ? arena.AddDecimal(
                        column.DType, entries, validity, column.Storage, column.Precision,
                        column.Scale, values)
                    : arena.AddPrimitive(column.DType, entries, validity, column.PType, values);
            }

            default:
            {
                int[] offsets = _codeOffset!;
                int[] lengths = _codeLength!;
                if (_width > 0)
                {
                    // A wide decimal: the key heap holds each value at its full width, so the
                    // child's buffer is the entries gathered from it by code.
                    VortexBuffer values = arena.Allocate(entries * _width, _width, out Span<byte> wide);
                    for (int code = 0; code < entries; code++)
                    {
                        Span<byte> slot = wide.Slice(code * _width, _width);
                        if (lengths[code] == _width)
                        {
                            _heap.AsSpan(offsets[code], _width).CopyTo(slot);
                        }
                        else
                        {
                            slot.Clear();
                        }
                    }

                    return arena.AddDecimal(
                        column.DType, entries, validity, column.Storage, column.Precision,
                        column.Scale, values);
                }

                // Strings: the heap is the data buffer, the views point into it. The heap is copied
                // into the arena once -- it is a rented buffer that goes back to the pool at reset,
                // and the child has to outlive that.
                int heapBytes = 0;
                for (int code = 0; code < entries; code++)
                {
                    heapBytes += lengths[code] > 12 ? lengths[code] : 0;
                }

                VortexBuffer heap = VortexBuffer.Empty;
                Span<byte> data = default;
                if (heapBytes > 0)
                {
                    heap = arena.Allocate(heapBytes, 1, out data);
                }

                VortexBuffer views = arena.Allocate(entries * ViewSize, ViewSize, out Span<byte> viewBytes);
                viewBytes.Clear();
                int written = 0;
                for (int code = 0; code < entries; code++)
                {
                    Span<byte> view = viewBytes.Slice(code * ViewSize, ViewSize);
                    int length = lengths[code];
                    BinaryPrimitives.WriteInt32LittleEndian(view, length);
                    ReadOnlySpan<byte> value = _heap.AsSpan(offsets[code], length);
                    if (length <= 12)
                    {
                        value.CopyTo(view.Slice(4, length));
                        continue;
                    }

                    value[..4].CopyTo(view.Slice(4, 4));
                    BinaryPrimitives.WriteInt32LittleEndian(view[8..12], 0);
                    BinaryPrimitives.WriteInt32LittleEndian(view[12..16], written);
                    value.CopyTo(data.Slice(written, length));
                    written += length;
                }

                return heapBytes > 0
                    ? arena.AddVarBinView(column.DType, entries, validity, views, [heap])
                    : arena.AddVarBinView(column.DType, entries, validity, views, default);
            }
        }
    }

    /// <summary>
    /// The values child's validity, chosen as <c>CanonicalFilter.FilterValidity</c> chooses it.
    /// </summary>
    private Validity ValuesValidity(CanonicalArena arena, Validity validity, int entries)
    {
        if (validity.Kind != ValidityKind.Bitmap)
        {
            return validity;
        }

        // A null whose first occurrence is in the carried tail has a code at or above `entries`
        // and is not among the chunk's entries at all.
        if (_nullCode < 0 || _nullCode >= entries)
        {
            return Validity.AllValid;
        }

        if (entries == 1)
        {
            return Validity.AllInvalid;
        }

        int bytes = CanonicalSupport.BitmapByteCount(entries);
        VortexBuffer bits = arena.Allocate(Math.Max(bytes, 1), 1, out Span<byte> destination);
        for (int code = 0; code < entries; code++)
        {
            if (code != _nullCode)
            {
                CanonicalSupport.SetBit(destination, code);
            }
        }

        int node = arena.AddBool(
            arena.GetNode(validity.CanonicalNodeIndex).DType, entries, Validity.NonNullable, bits, 0);
        return Validity.Bitmap(node);
    }

    /// <summary>
    /// A constant range: one element, <paramref name="count"/> rows. The element is probed once and
    /// its code repeated, which is the whole point of the form -- the column is never materialized
    /// to be read row by row.
    /// </summary>
    private void ProbeConstant(CanonicalNode node, in ValidityMask mask, int start, int count)
    {
        ReadOnlySpan<byte> element = node.ConstantElement;
        ulong key = _width switch
        {
            1 => element[0],
            2 => BinaryPrimitives.ReadUInt16LittleEndian(element),
            4 => BinaryPrimitives.ReadUInt32LittleEndian(element),
            _ => BinaryPrimitives.ReadUInt64LittleEndian(element),
        };

        if (mask.AllValid)
        {
            InsertFixed(key);
            if (_abandoned)
            {
                return;
            }

            int code = _codes[_rows - 1];
            _codes.AsSpan(_rows, count - 1).Fill(code);
            _rows += count - 1;
            return;
        }

        for (int i = 0; i < count && !_abandoned; i++)
        {
            if (!mask.IsValid(start + i))
            {
                NullRow();
                continue;
            }

            InsertFixed(key);
        }
    }

    private void ProbeBits(CanonicalNode node, in ValidityMask mask, int start, int count)
    {
        ReadOnlySpan<byte> bits = node.Bits.Span;
        int offset = node.BitOffset + start;
        for (int i = 0; i < count && !_abandoned; i++)
        {
            if (!mask.IsValid(start + i))
            {
                NullRow();
                continue;
            }

            InsertFixed(CanonicalSupport.BitAt(bits, offset + i) ? 1UL : 0UL);
        }
    }

    /// <summary>The physical width resolved once; the loop reads the bits, whatever they mean.</summary>
    /// <remarks>
    /// A float column is probed by its raw bits (§3.2.4), which is also what the row comparer this
    /// replaces did: <c>-0.0</c> and <c>+0.0</c> are two values, and every NaN payload is its own.
    /// That is what makes the codes identical rather than merely equivalent.
    /// </remarks>
    private void ProbeFixed<T>(CanonicalNode node, in ValidityMask mask, int start, int count)
        where T : unmanaged, IBinaryInteger<T>, IUnsignedNumber<T>
    {
        ReadOnlySpan<T> values = MemoryMarshal.Cast<byte, T>(node.Values.Span).Slice(start, count);
        if (mask.AllValid)
        {
            for (int i = 0; i < values.Length && !_abandoned; i++)
            {
                InsertFixed(ulong.CreateTruncating(values[i]));
            }

            return;
        }

        for (int i = 0; i < values.Length && !_abandoned; i++)
        {
            if (!mask.IsValid(start + i))
            {
                NullRow();
                continue;
            }

            InsertFixed(ulong.CreateTruncating(values[i]));
        }
    }

    /// <summary>A fixed-width value wider than a word, keyed by its bytes.</summary>
    private void ProbeWide(CanonicalNode node, in ValidityMask mask, int start, int count)
    {
        ReadOnlySpan<byte> values = node.Values.Span;
        for (int i = 0; i < count && !_abandoned; i++)
        {
            if (!mask.IsValid(start + i))
            {
                NullRow();
                continue;
            }

            InsertBytes(values.Slice((start + i) * _width, _width));
        }
    }

    private void ProbeViews(CanonicalNode node, in ValidityMask mask, int start, int count)
    {
        ReadOnlySpan<byte> views = node.Views.Span;
        for (int i = 0; i < count && !_abandoned; i++)
        {
            if (!mask.IsValid(start + i))
            {
                NullRow();
                continue;
            }

            // The same resolution `RowComparer.Bytes` makes: a view is the length, then either the
            // value inline or a (buffer, offset) pair into the column's data buffers.
            ReadOnlySpan<byte> view = views.Slice((start + i) * ViewSize, ViewSize);
            int size = BinaryPrimitives.ReadInt32LittleEndian(view);
            if (size <= 12)
            {
                InsertBytes(view.Slice(4, size));
                continue;
            }

            int buffer = BinaryPrimitives.ReadInt32LittleEndian(view[8..12]);
            int offset = BinaryPrimitives.ReadInt32LittleEndian(view[12..16]);
            InsertBytes(node.GetDataBuffer(buffer).Span.Slice(offset, size));
        }
    }

    /// <summary>
    /// A null row: the null is a value like any other, with a code of its own, so that a nullable
    /// dictionary has at most one null entry and every row still has a code.
    /// </summary>
    private void NullRow()
    {
        if (_nullCode < 0)
        {
            _nullCode = NewCode();
            if (_abandoned)
            {
                return;
            }

            _codeKey[_nullCode] = 0;
            if (_codeOffset is not null)
            {
                _codeOffset[_nullCode] = 0;
                _codeLength![_nullCode] = 0;
            }
        }

        _codes[_rows++] = _nullCode;
    }

    private void InsertFixed(ulong key)
    {
        int slot = (int)KeyHash.Mix(key) & _mask;
        while (true)
        {
            int occupant = _slotCode[slot];
            if (occupant == 0)
            {
                int code = NewCode();
                if (_abandoned)
                {
                    return;
                }

                _slotCode[slot] = code + 1;
                _slotKey[slot] = key;
                _codeKey[code] = key;
                _codes[_rows++] = code;
                GrowIfLoaded();
                return;
            }

            if (_slotKey[slot] == key)
            {
                _codes[_rows++] = occupant - 1;
                return;
            }

            slot = (slot + 1) & _mask;
        }
    }

    private void InsertBytes(ReadOnlySpan<byte> bytes)
    {
        int[] offsets = _slotOffset!;
        int[] lengths = _slotLength!;
        ulong hash = KeyHash.Bytes(bytes);
        int slot = (int)hash & _mask;
        while (true)
        {
            int occupant = _slotCode[slot];
            if (occupant == 0)
            {
                int code = NewCode();
                if (_abandoned)
                {
                    return;
                }

                // COPIED ONCE, HERE, and never again: this is the one copy per distinct value the
                // header promises, and the heap it lands in is the values child's data buffer in
                // waiting.
                int offset = Append(bytes);
                _slotCode[slot] = code + 1;
                _slotKey[slot] = hash;
                offsets[slot] = offset;
                lengths[slot] = bytes.Length;
                _codeOffset![code] = offset;
                _codeLength![code] = bytes.Length;
                _codes[_rows++] = code;
                GrowIfLoaded();
                return;
            }

            // EQUALITY IS ON BYTES, never on the hash alone: the stored hash and the length are
            // the two cheap rejections in front of the compare, not a substitute for it.
            if (_slotKey[slot] == hash && lengths[slot] == bytes.Length
                && _heap.AsSpan(offsets[slot], bytes.Length).SequenceEqual(bytes))
            {
                _codes[_rows++] = occupant - 1;
                return;
            }

            slot = (slot + 1) & _mask;
        }
    }

    /// <summary>Hands out the next code, recording where it first occurred, or abandons at the cap.</summary>
    private int NewCode()
    {
        if (_distinct >= MaxEntries)
        {
            _abandoned = true;
            return -1;
        }

        int code = _distinct++;
        if (code == _firstRows.Length)
        {
            int grown = _firstRows.Length * 2;
            Grow(ref _firstRows, grown, code);
            Grow(ref _codeKey, grown, code);
            if (_codeOffset is not null)
            {
                Grow(ref _codeOffset, grown, code);
                Grow(ref _codeLength!, grown, code);
            }
        }

        _firstRows[code] = _rows;
        return code;
    }

    private int Append(ReadOnlySpan<byte> bytes)
    {
        byte[] heap = _heap!;
        if (_heapUsed + bytes.Length > heap.Length)
        {
            int grown = heap.Length * 2;
            while (grown < _heapUsed + bytes.Length)
            {
                grown *= 2;
            }

            byte[] larger = ArrayPool<byte>.Shared.Rent(grown);
            heap.AsSpan(0, _heapUsed).CopyTo(larger);
            ArrayPool<byte>.Shared.Return(heap);
            _heap = heap = larger;
        }

        int offset = _heapUsed;
        bytes.CopyTo(heap.AsSpan(offset));
        _heapUsed += bytes.Length;
        return offset;
    }

    private void EnsureCodes(int rows)
    {
        if (rows <= _codes.Length)
        {
            return;
        }

        int grown = _codes.Length * 2;
        while (grown < rows)
        {
            grown *= 2;
        }

        Grow(ref _codes, grown, _rows);
    }

    /// <summary>Rents the next size up, keeps the first <paramref name="used"/>, returns the old.</summary>
    private static void Grow<T>(ref T[] array, int length, int used)
    {
        T[] larger = ArrayPool<T>.Shared.Rent(length);
        array.AsSpan(0, used).CopyTo(larger);
        ArrayPool<T>.Shared.Return(array);
        array = larger;
    }

    /// <summary>
    /// Doubles the slots once the load passes one half, re-inserting from the stored key or hash --
    /// never from the bytes, which is the point of storing the hash.
    /// </summary>
    private void GrowIfLoaded()
    {
        int capacity = _mask + 1;
        if (_distinct * 2 <= capacity)
        {
            return;
        }

        int grown = capacity * 2;
        int[] codes = ArrayPool<int>.Shared.Rent(grown);
        Array.Clear(codes, 0, grown);
        ulong[] keys = ArrayPool<ulong>.Shared.Rent(grown);
        int[]? offsets = _slotOffset is null ? null : ArrayPool<int>.Shared.Rent(grown);
        int[]? lengths = _slotLength is null ? null : ArrayPool<int>.Shared.Rent(grown);
        int mask = grown - 1;

        for (int old = 0; old < capacity; old++)
        {
            int occupant = _slotCode[old];
            if (occupant == 0)
            {
                continue;
            }

            ulong key = _slotKey[old];
            ulong hash = _shape == Shape.Bytes ? key : KeyHash.Mix(key);
            int slot = (int)hash & mask;
            while (codes[slot] != 0)
            {
                slot = (slot + 1) & mask;
            }

            codes[slot] = occupant;
            keys[slot] = key;
            if (offsets is not null)
            {
                offsets[slot] = _slotOffset![old];
                lengths![slot] = _slotLength![old];
            }
        }

        ArrayPool<int>.Shared.Return(_slotCode);
        ArrayPool<ulong>.Shared.Return(_slotKey);
        if (_slotOffset is not null)
        {
            ArrayPool<int>.Shared.Return(_slotOffset);
            ArrayPool<int>.Shared.Return(_slotLength!);
        }

        _slotCode = codes;
        _slotKey = keys;
        _slotOffset = offsets;
        _slotLength = lengths;
        _mask = mask;
    }
}
