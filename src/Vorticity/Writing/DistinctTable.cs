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

/// <summary>
/// A column's distinct values over one chunk, with a code per row in order of first appearance,
/// built as rows arrive. The table owns its keys and never references a row of the batch, because a
/// chunk straddles batches and the previous batch's arena is recycled as soon as the next one is
/// decoded; a chunk's tail carried into the next chunk is re-probed into a fresh table.
/// </summary>
internal sealed class DistinctTable
{
    private const int InitialCapacity = 64;

    /// <summary>
    /// Entries beyond which the chunk refuses its dictionary candidate rather than keep growing.
    /// This is a bound on memory, not a pricing rule; pricing is the chooser's.
    /// </summary>
    private const int MaxEntries = 1 << 20;

    /// <summary>A view is sixteen bytes: length, then twelve of value inline or a buffer and an offset.</summary>
    private const int ViewSize = 16;

    private enum Shape : byte
    {
        Bits,

        /// <summary>One, two, four or eight bytes: the key is the value's bits, zero-extended.</summary>
        Fixed,

        /// <summary>A key that does not fit a slot, whatever its dtype: it lives in the heap.</summary>
        Bytes,
    }

    private readonly Shape _shape;
    private readonly CanonicalKind _kind;

    /// <summary>Bytes per value for a fixed-width column, 0 for a view column.</summary>
    private readonly int _width;

    // `_slotCode` holds code + 1 so that zero means empty. The capacity is `_mask + 1`, never an
    // array's length: a rented array is as long as the pool felt like.
    private int[] _slotCode;
    private ulong[] _slotKey;
    private int[]? _slotOffset;
    private int[]? _slotLength;
    private int _mask;

    private byte[]? _heap;
    private int _heapUsed;

    // Per code, in code order: the row of first occurrence and the key itself -- the word for a
    // fixed-width kind, the heap span for a byte kind.
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

    /// <summary>For a view column, the bytes of the distinct non-null values.</summary>
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
    internal static DistinctTable? For(CanonicalNode node) => node.Kind switch
    {
        // No table below three bytes of width, by arithmetic rather than policy: a column of `w`
        // bytes has at most 2^(8w) distinct values, so its codes are `w` bytes wide too and the
        // entries and framing come on top. Such a dictionary can never be smaller than the column.
        CanonicalKind.Bool => null,
        CanonicalKind.Primitive => node.PType.ByteWidth() <= 2
            ? null
            : new DistinctTable(Shape.Fixed, node.Kind, node.PType.ByteWidth()),

        // The next batch of the same column may be an ordinary primitive, so a constant batch feeds
        // the primitive table rather than getting a table of its own.
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
    /// Whether a column of this kind gets a table at all, so that the writer can count a chunk the
    /// table failed to serve.
    /// </summary>
    internal static bool Serves(CanonicalKind kind) => kind is CanonicalKind.Bool
        or CanonicalKind.Primitive or CanonicalKind.Constant or CanonicalKind.Decimal
        or CanonicalKind.VarBinView;

    /// <summary>
    /// Rents the chunk's buffers on its first probe, sized by the previous chunk's needs. Buffers
    /// are rented per chunk and returned at reset rather than held per column, so that a thousand
    /// columns share one pool.
    /// </summary>
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

    /// <summary>Forgets the chunk: every buffer back to the pool, its size kept for the next one.</summary>
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
    internal void Probe(CanonicalArena arena, CanonicalNode node, int start, int count)
    {
        if (_abandoned || count <= 0)
        {
            return;
        }

        bool constant = node.Kind == CanonicalKind.Constant;
        if ((constant ? CanonicalKind.Primitive : node.Kind) != _kind)
        {
            // The schema is the file's and does not change between batches, so a node of another
            // kind is not a state this can be in; abandoning costs a pass rather than a wrong plan.
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
    /// with the null entry null. <paramref name="entries"/> is the code count at the chunk's last
    /// block close; codes above it belong to the carried tail. The child's validity has to be
    /// chosen exactly as a filter over the first row of each code would choose it, since the wire
    /// form of the child depends on which of the three forms it takes.
    /// </summary>
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
                // Uninitialized because the loop below writes every byte. Only the bitmaps, where a
                // clear bit is a value, are still zeroed.
                VortexBuffer values = arena.AllocateUninitialized(
                    entries * _width, _width, out Span<byte> destination);
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
                    // A wide decimal: the key heap holds each value at its full width. Every slot is
                    // written, copied or cleared for the null entry, so nothing is zeroed up front.
                    VortexBuffer values = arena.AllocateUninitialized(
                        entries * _width, _width, out Span<byte> wide);
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

                // Strings: the heap is the data buffer and the views point into it. It is copied
                // into the arena because the key heap is rented and goes back to the pool at reset,
                // which the child has to outlive.
                int heapBytes = 0;
                for (int code = 0; code < entries; code++)
                {
                    heapBytes += lengths[code] > 12 ? lengths[code] : 0;
                }

                // Both buffers uninitialized: the copies write the heap end to end, and every one
                // of a view's bytes is written below, the inline padding explicitly included.
                VortexBuffer heap = VortexBuffer.Empty;
                Span<byte> data = default;
                if (heapBytes > 0)
                {
                    heap = arena.AllocateUninitialized(heapBytes, 1, out data);
                }

                VortexBuffer views = arena.AllocateUninitialized(
                    entries * ViewSize, ViewSize, out Span<byte> viewBytes);
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
                        view[(4 + length)..].Clear();
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

    /// <summary>The values child's validity, chosen as a filter over the entries would choose it.</summary>
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
    /// A constant range: the element is probed once and its code repeated, so the column is never
    /// materialized to be read row by row.
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

    /// <summary>
    /// The physical width resolved once; the loop reads the bits, whatever they mean. A float
    /// column is keyed by its raw bits, so <c>-0.0</c> and <c>+0.0</c> are two values and every NaN
    /// payload is its own.
    /// </summary>
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
    /// The null is a value like any other, with a code of its own, so a nullable dictionary has at
    /// most one null entry and every row still has a code.
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

            // Equality is on bytes: the stored hash and the length are cheap rejections in front of
            // the compare, never a substitute for it.
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
    /// Doubles the slots once the load passes one half, re-inserting from the stored key or hash
    /// rather than from the bytes, which is the point of storing the hash.
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
