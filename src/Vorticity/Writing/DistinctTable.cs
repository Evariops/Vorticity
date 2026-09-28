using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
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

    /// <summary>The slots a table of few values gives each, up to <see cref="SparseCapacity"/>.</summary>
    private const int SparseSpread = 64;

    /// <summary>The most slots a table of few values is spread over; past it, the load alone sizes the table.</summary>
    private const int SparseCapacity = 4096;

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
        int entries = Math.Max(
            InitialCapacity, (int)BitOperations.RoundUpToPowerOf2((uint)((_lastDistinct * 2) + 1)));
        int capacity = Math.Max(entries, Sparse(_lastDistinct));
        _slotCode = ArrayPool<int>.Shared.Rent(capacity);
        _slotKey = ArrayPool<ulong>.Shared.Rent(capacity);
        _mask = capacity - 1;
        Array.Clear(_slotCode, 0, capacity);
        if (_shape == Shape.Bytes)
        {
            _slotOffset = ArrayPool<int>.Shared.Rent(capacity);
            _slotLength = ArrayPool<int>.Shared.Rent(capacity);
            _codeOffset = ArrayPool<int>.Shared.Rent(entries);
            _codeLength = ArrayPool<int>.Shared.Rent(entries);
            _heap = ArrayPool<byte>.Shared.Rent(Math.Max(1024, _lastHeap));
        }

        _firstRows = ArrayPool<int>.Shared.Rent(entries);
        _codeKey = ArrayPool<ulong>.Shared.Rent(entries);
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
        else if (_mask + 1 < Sparse(_distinct))
        {
            Resize(Sparse(_distinct));
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
            // A row equal to the one before takes its code without the hash or the probe, and so
            // does the run it starts, measured a vector at a time: a sorted column or one of runs
            // is mostly such rows. The one compare that decides it is all a column without runs
            // pays. The run is measured before the row that starts it is inserted, so that the hash
            // of the row after the run is taken first: a probe whose branch was guessed wrong throws
            // away what came after it, never the hash waiting for it.
            int i = 0;
            ulong hash = values.IsEmpty ? 0 : KeyHash.Mix(ulong.CreateTruncating(values[0]));
            while (i < values.Length && !_abandoned)
            {
                T value = values[i];
                int run = i + 1 < values.Length && values[i + 1] == value ? RunLength(values[(i + 1)..], value) : 0;
                int next = i + 1 + run;
                ulong nextHash = next < values.Length ? KeyHash.Mix(ulong.CreateTruncating(values[next])) : 0;
                InsertFixed(ulong.CreateTruncating(value), hash);
                if (run > 0 && !_abandoned)
                {
                    _codes.AsSpan(_rows, run).Fill(_codes[_rows - 1]);
                    _rows += run;
                }

                i = next;
                hash = nextHash;
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

    /// <summary>How many of <paramref name="values"/>, from the first, equal <paramref name="value"/>.</summary>
    /// <remarks>A vector of them at a time, the first that differs found by a count of trailing ones.</remarks>
    private static int RunLength<T>(ReadOnlySpan<T> values, T value)
        where T : unmanaged, IBinaryInteger<T>
    {
        ref T first = ref MemoryMarshal.GetReference(values);
        int k = 0;
        if (Vector256.IsHardwareAccelerated && Vector256<T>.IsSupported)
        {
            Vector256<T> repeated = Vector256.Create(value);
            int lanes = Vector256<T>.Count;
            for (; k <= values.Length - lanes; k += lanes)
            {
                uint same = Vector256.Equals(Vector256.LoadUnsafe(ref first, (nuint)k), repeated).ExtractMostSignificantBits();
                if (same != (lanes == 32 ? uint.MaxValue : (1u << lanes) - 1))
                {
                    return k + BitOperations.TrailingZeroCount(~same);
                }
            }
        }
        else if (Vector128.IsHardwareAccelerated && Vector128<T>.IsSupported)
        {
            Vector128<T> repeated = Vector128.Create(value);
            int lanes = Vector128<T>.Count;
            for (; k <= values.Length - lanes; k += lanes)
            {
                uint same = Vector128.Equals(Vector128.LoadUnsafe(ref first, (nuint)k), repeated).ExtractMostSignificantBits();
                if (same != (1u << lanes) - 1)
                {
                    return k + BitOperations.TrailingZeroCount(~same);
                }
            }
        }

        while (k < values.Length && Unsafe.Add(ref first, k) == value)
        {
            k++;
        }

        return k;
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

    /// <summary>The bits of a recent view's slot.</summary>
    /// <remarks>
    /// Two views in one slot take each other's place row after row, so there are enough slots to
    /// keep a few dozen labels apart, and no more, as each costs the rows whose views never come back.
    /// </remarks>
    private const int RecentBits = 8;

    /// <summary>Recent inline views a probe remembers, with their codes.</summary>
    private const int RecentViews = 1 << RecentBits;

    /// <remarks>
    /// <para>
    /// A string of twelve bytes or fewer is its view, so a row whose sixteen bytes are a recent
    /// row's is that row's value and takes its code, without the hash, the probe or the byte
    /// compare: the few short labels a dictionary column is made of come back row after row. The
    /// recent views are a small direct-mapped table on the stack, kept for one call, and a view
    /// that is not in it -- or whose padding differs from an equal value's -- goes to the table as
    /// before, which is still what decides.
    /// </para>
    /// <para>
    /// The rows whose views are recent are coded by <see cref="Recall"/>, a loop that calls
    /// nothing and writes nothing but their codes, so that what it holds stays in registers. Any
    /// other row -- a null, a miss, an out-of-line value -- stops it and is probed here, with the
    /// row count set from the index before the call that reads it and once at the end, never
    /// incremented through the object row after row.
    /// </para>
    /// </remarks>
    private void ProbeViews(CanonicalNode node, in ValidityMask mask, int start, int count)
    {
        ReadOnlySpan<byte> views = node.Views.Span;
        ViewValues values = new ViewValues(node);
        ReadOnlySpan<ulong> pairs = MemoryMarshal.Cast<byte, ulong>(views).Slice(start * 2, count * 2);
        Span<ulong> recent = stackalloc ulong[RecentViews * 2];
        Span<int> recentCode = stackalloc int[RecentViews];
        recentCode.Fill(-1);
        ReadOnlySpan<byte> bits = mask.AllValid ? default : mask.Bits;
        int first = _rows;
        Span<int> codes = _codes.AsSpan(first, count);
        for (int i = 0; i < count; i++)
        {
            if (!mask.AllInvalid)
            {
                i = Recall(pairs, codes, recent, recentCode, bits, mask.BitOffset + start, i);
                if (i == count)
                {
                    break;
                }
            }

            int row = start + i;
            _rows = first + i;
            if (!mask.IsValid(row))
            {
                NullRow();
            }
            else
            {
                ulong low = pairs[i * 2];
                ulong high = pairs[(i * 2) + 1];
                int size = (int)(uint)low;
                if (size <= 12)
                {
                    InsertInline(low, high, size, views.Slice((row * ViewSize) + 4, size));
                    if (!_abandoned)
                    {
                        int slot = RecentSlot(low, high);
                        recent[slot * 2] = low;
                        recent[(slot * 2) + 1] = high;
                        recentCode[slot] = codes[i];
                    }
                }
                else
                {
                    InsertBytes(values.At(row));
                }
            }

            if (_abandoned)
            {
                return;
            }
        }

        _rows = first + count;
    }

    /// <summary>
    /// Codes the rows from <paramref name="i"/> on whose views are recent, and returns the first
    /// that is not -- null, not recent, or out of line -- or the row count.
    /// </summary>
    /// <remarks>
    /// Only inline views are remembered, and a view's first word holds its size, so a row that
    /// matches a recent view is inline without being asked.
    /// </remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Recall(
        ReadOnlySpan<ulong> pairs, Span<int> codes, ReadOnlySpan<ulong> recent,
        ReadOnlySpan<int> recentCode, ReadOnlySpan<byte> bits, int bitOffset, int i)
    {
        for (; i < codes.Length; i++)
        {
            if (!bits.IsEmpty && !CanonicalSupport.BitAt(bits, bitOffset + i))
            {
                return i;
            }

            ulong low = pairs[i * 2];
            ulong high = pairs[(i * 2) + 1];
            int slot = RecentSlot(low, high);
            int known = recentCode[slot];
            if (known < 0 || recent[slot * 2] != low || recent[(slot * 2) + 1] != high)
            {
                return i;
            }

            codes[i] = known;
        }

        return codes.Length;
    }

    /// <summary>Where a view sits among the recent ones.</summary>
    /// <remarks>
    /// A collision here costs a miss and nothing more, so the slot takes no seed: the top bits of
    /// one multiply of the view's two words folded, which a row pays in <see cref="Recall"/>
    /// before anything else.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int RecentSlot(ulong low, ulong high) =>
        (int)(((low ^ (high * 0x9E3779B97F4A7C15UL)) * 0x9E3779B97F4A7C15UL) >> (64 - RecentBits));

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

    private void InsertFixed(ulong key) => InsertFixed(key, KeyHash.Mix(key));

    /// <param name="key">The value's bits.</param>
    /// <param name="hash">The key's <see cref="KeyHash.Mix"/>, which a loop takes a row ahead.</param>
    private void InsertFixed(ulong key, ulong hash)
    {
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
                && Same(ref _heap![offsets[slot]], ref MemoryMarshal.GetReference(bytes), bytes.Length))
            {
                _codes[_rows++] = occupant - 1;
                return;
            }

            slot = (slot + 1) & _mask;
        }
    }

    /// <summary>
    /// <see cref="InsertBytes"/> for a value of twelve bytes or fewer, which a view always holds
    /// inline: hashed from the view's two words and matched against a stored value by two masked
    /// reads of it, with no call.
    /// </summary>
    /// <remarks>
    /// A value this short is never out of line, and a longer one never inline, so no value hashed
    /// here can equal one <see cref="InsertBytes"/> hashed: the length rejects the pair before any
    /// byte. The heap keeps <see cref="HeapSlack"/> bytes past its last value, so a stored value's
    /// twelve bytes can be read as words whatever its own length.
    /// </remarks>
    private void InsertInline(ulong low, ulong high, int size, ReadOnlySpan<byte> bytes)
    {
        // The payload as two words, the padding past the size masked off: bytes 0 to 7 and 8 to 11.
        ulong headMask = size >= 8 ? ulong.MaxValue : (1UL << (8 * size)) - 1;
        ulong tailMask = size <= 8 ? 0 : (1UL << (8 * (size - 8))) - 1;
        ulong head = ((low >> 32) | (high << 32)) & headMask;
        ulong tail = (high >> 32) & tailMask;
        ulong hash = KeyHash.Pair(head, tail, size);

        int[] offsets = _slotOffset!;
        int[] lengths = _slotLength!;
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
                lengths[slot] = size;
                _codeOffset![code] = offset;
                _codeLength![code] = size;
                _codes[_rows++] = code;
                GrowIfLoaded();
                return;
            }

            if (_slotKey[slot] == hash && lengths[slot] == size)
            {
                ref byte stored = ref _heap![offsets[slot]];
                if ((Unsafe.ReadUnaligned<ulong>(ref stored) & headMask) == head
                    && (Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref stored, 8)) & tailMask) == tail)
                {
                    _codes[_rows++] = occupant - 1;
                    return;
                }
            }

            slot = (slot + 1) & _mask;
        }
    }

    /// <summary>Bytes the heap keeps free past its last value, for <see cref="InsertInline"/>'s reads.</summary>
    private const int HeapSlack = 16;

    /// <summary>Whether <paramref name="length"/> bytes at two places are equal, sixteen at a time and no call.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool Same(ref byte a, ref byte b, int length)
    {
        int i = 0;
        for (; i <= length - 16; i += 16)
        {
            if (Vector128.LoadUnsafe(ref a, (nuint)i) != Vector128.LoadUnsafe(ref b, (nuint)i))
            {
                return false;
            }
        }

        if (i < length && length >= 16)
        {
            // The last sixteen bytes, overlapping those already compared.
            return Vector128.LoadUnsafe(ref a, (nuint)(length - 16)) == Vector128.LoadUnsafe(ref b, (nuint)(length - 16));
        }

        for (; i < length; i++)
        {
            if (Unsafe.Add(ref a, i) != Unsafe.Add(ref b, i))
            {
                return false;
            }
        }

        return true;
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
        if (_heapUsed + bytes.Length + HeapSlack > heap.Length)
        {
            int grown = heap.Length * 2;
            while (grown < _heapUsed + bytes.Length + HeapSlack)
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

    /// <summary>Doubles the slots once the load passes one half.</summary>
    private void GrowIfLoaded()
    {
        int capacity = _mask + 1;
        if (_distinct * 2 > capacity)
        {
            Resize(capacity * 2);
        }
    }

    /// <summary>
    /// The fewest slots for <paramref name="distinct"/> values: spread out while they are few, so
    /// that rows drawing them in no order rarely find another value on their slot, which makes the
    /// compare that finds them a branch the processor cannot guess.
    /// </summary>
    private static int Sparse(int distinct) => distinct == 0
        ? 0
        : distinct >= SparseCapacity / SparseSpread
            ? SparseCapacity
            : (int)BitOperations.RoundUpToPowerOf2((uint)(distinct * SparseSpread));

    /// <summary>
    /// Moves the held values to <paramref name="grown"/> slots, re-inserting from the stored key or
    /// hash rather than from the bytes, which is the point of storing the hash.
    /// </summary>
    private void Resize(int grown)
    {
        int capacity = _mask + 1;
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
