using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Vorticity.Aggregating;
using Vorticity.Arrays;
using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Buffers;
using Vorticity.Types;

namespace Vorticity.Writing;

/// <summary>
/// One column's dictionary for a whole object: its distinct values sorted in the order of their type, each coded by its
/// rank, which every chunk of the column holds in place of its values (the <c>vortex.dict</c> layout).
/// </summary>
/// <remarks>
/// <para>
/// Written only where the values are known before the rows: those of a sorted buffer, the union of the dictionaries a
/// merge reads. A smaller code is a smaller value, so a range of values is a range of codes; a merge translates each
/// input's codes by a table (<see cref="Translation"/>) instead of decoding and encoding its text again.
/// </para>
/// <para>
/// The values lie end to end in pinned memory the dictionary owns, text with an offset each, numbers at their width. An
/// open-addressing table over them finds a value's code, built once. A chunk's rows are coded through it, a row whose
/// view or bits equal the row before's taking that row's code without a probe, which a column sorted by its value pays
/// for nearly every row.
/// </para>
/// <para>
/// Text, binary and integers of 32 or 64 bits; a narrower integer has as few values as codes, and a float's order and
/// its patterns are ALP's to exploit.
/// </para>
/// </remarks>
internal sealed class ObjectDictionary
{
    /// <summary>The most values a dictionary holds: its codes fit sixteen bits, as upstream's do.</summary>
    internal const int MostValues = 65_535;

    /// <summary>The most bytes its values take, as upstream's: what a reader holds of the object for the column.</summary>
    internal const int MostBytes = 1 << 20;

    private const int ViewSize = 16;
    private const int InlineBytes = 12;

    // The values end to end, pinned: text's bytes, an offset each in `_offsets`; numbers at `_width`.
    private readonly byte[] _bytes;
    private readonly int[] _offsets;
    private readonly int _width;

    // Per code, the value's hash for text and its bits for a number; the table holds code + 1, zero empty.
    private readonly ulong[] _keys;
    private readonly int[] _slots;
    private readonly int _mask;

    private ObjectDictionary(DTypeKind kind, PType ptype, byte[] bytes, int[] offsets, ulong[] keys, int count, bool allReferenced)
    {
        Kind = kind;
        PType = ptype;
        _bytes = bytes;
        _offsets = offsets;
        _width = kind == DTypeKind.Primitive ? ptype.ByteWidth() : 0;
        _keys = keys;
        Count = count;
        AllReferenced = allReferenced;

        int capacity = (int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(64, (count * 2) + 1));
        _slots = new int[capacity];
        _mask = capacity - 1;
        for (int code = 0; code < count; code++)
        {
            int slot = Slot(_width == 0 ? keys[code] : KeyHash.Mix(keys[code]));
            while (_slots[slot] != 0)
            {
                slot = (slot + 1) & _mask;
            }

            _slots[slot] = code + 1;
        }
    }

    /// <summary>The values' kind: <see cref="DTypeKind.Utf8"/>, <see cref="DTypeKind.Binary"/> or <see cref="DTypeKind.Primitive"/>.</summary>
    internal DTypeKind Kind { get; }

    /// <summary>A number's type; meaningless for text.</summary>
    internal PType PType { get; }

    /// <summary>The values, as many as the codes.</summary>
    internal int Count { get; }

    /// <summary>The codes' type: a byte up to 256 values, two above.</summary>
    internal PType CodesPType => Count <= 256 ? PType.U8 : PType.U16;

    /// <summary>The bits a code takes packed: what a code costs a row.</summary>
    internal int CodeBits => Count <= 1 ? 0 : 32 - BitOperations.LeadingZeroCount((uint)(Count - 1));

    /// <summary>Whether every value is some row's: a dictionary of the rows themselves, not of a merge's inputs.</summary>
    internal bool AllReferenced { get; }

    /// <summary>The values' bytes, end to end.</summary>
    internal int ValueBytes => _width == 0 ? _offsets[Count] : Count * _width;

    /// <summary>Whether a column of <paramref name="type"/> may have an object dictionary.</summary>
    internal static bool Holds(DType type) =>
        type.Kind is DTypeKind.Utf8 or DTypeKind.Binary
        || (type.Kind == DTypeKind.Primitive && type.PType is PType.I32 or PType.U32 or PType.I64 or PType.U64);

    /// <summary>The bytes of the value of <paramref name="code"/>.</summary>
    internal ReadOnlySpan<byte> Value(int code) =>
        _width == 0 ? _bytes.AsSpan(_offsets[code], _offsets[code + 1] - _offsets[code]) : _bytes.AsSpan(code * _width, _width);

    /// <summary>
    /// The dictionary of the values the rows of <paramref name="node"/> hold, nulls aside, every one of them some row's;
    /// null when the column is of no kind a dictionary holds, or its values pass <see cref="MostValues"/> or
    /// <see cref="MostBytes"/>.
    /// </summary>
    internal static ObjectDictionary? Of(CanonicalArena arena, int node)
    {
        ArgumentNullException.ThrowIfNull(arena);
        if (!Holds(arena.GetNode(node).DType))
        {
            return null;
        }

        using Collector collector = new Collector(arena.GetNode(node).DType);
        return collector.Add(arena, node) ? collector.Finish(allReferenced: true) : null;
    }

    /// <summary>
    /// The union of <paramref name="inputs"/>' values, each a canonical column of distinct values in any order, as a
    /// merge's output takes them: not every value is some row's. Each input's <paramref name="translations"/> gives its
    /// values' codes in the union, -1 for a null. Null when the union passes the limits, or the values are of no kind a
    /// dictionary holds.
    /// </summary>
    internal static ObjectDictionary? Union(ReadOnlySpan<(CanonicalArena Arena, int Node)> inputs, out int[][] translations)
    {
        translations = [];
        if (inputs.IsEmpty || !Holds(inputs[0].Arena.GetNode(inputs[0].Node).DType))
        {
            return null;
        }

        using Collector collector = new Collector(inputs[0].Arena.GetNode(inputs[0].Node).DType);
        foreach ((CanonicalArena arena, int node) in inputs)
        {
            if (!collector.Add(arena, node))
            {
                return null;
            }
        }

        ObjectDictionary union = collector.Finish(allReferenced: false);
        translations = new int[inputs.Length][];
        for (int i = 0; i < inputs.Length; i++)
        {
            (CanonicalArena arena, int node) = inputs[i];
            translations[i] = new int[arena.GetNode(node).Length];
            union.Translation(arena, node, translations[i]);
        }

        return union;
    }

    /// <summary>The code of the text or binary value <paramref name="value"/>, or -1 when the dictionary lacks it.</summary>
    internal int Find(ReadOnlySpan<byte> value)
    {
        ulong hash = KeyHash.Bytes(value);
        int slot = Slot(hash);
        while (true)
        {
            int occupant = _slots[slot];
            if (occupant == 0)
            {
                return -1;
            }

            int code = occupant - 1;
            if (_keys[code] == hash)
            {
                int start = _offsets[code];
                if (_offsets[code + 1] - start == value.Length && _bytes.AsSpan(start, value.Length).SequenceEqual(value))
                {
                    return code;
                }
            }

            slot = (slot + 1) & _mask;
        }
    }

    /// <summary>The code of the number whose bits, zero-extended, are <paramref name="bits"/>, or -1 when the dictionary lacks it.</summary>
    internal int Find(ulong bits)
    {
        int slot = Slot(KeyHash.Mix(bits));
        while (true)
        {
            int occupant = _slots[slot];
            if (occupant == 0 || _keys[occupant - 1] == bits)
            {
                return occupant - 1;
            }

            slot = (slot + 1) & _mask;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int Slot(ulong hash) => (int)hash & _mask;

    /// <summary>
    /// Each value of the canonical column <paramref name="values"/> as its code here, into <paramref name="table"/>: -1
    /// for a null, or a value the dictionary lacks. What a merge reads an input's codes through.
    /// </summary>
    internal void Translation(CanonicalArena arena, int values, Span<int> table)
    {
        CanonicalNode node = arena.GetNode(values);
        if (node.Kind is CanonicalKind.Constant or CanonicalKind.Dictionary or CanonicalKind.RunEnd)
        {
            node = arena.GetNode(arena.Decoded(node.Kind == CanonicalKind.Constant ? arena.MaterializeConstant(values) : values));
        }

        ValidityMask mask = ValidityMask.From(arena, node.Validity);
        if (_width == 0)
        {
            ViewValues views = new ViewValues(node);
            for (int i = 0; i < node.Length; i++)
            {
                table[i] = mask.IsValid(i) ? Find(views.At(i)) : -1;
            }

            return;
        }

        ReadOnlySpan<byte> bytes = node.Values.Span;
        for (int i = 0; i < node.Length; i++)
        {
            table[i] = mask.IsValid(i) ? Find(Bits(bytes, i, _width)) : -1;
        }
    }

    /// <summary>
    /// Codes <paramref name="codes"/>, of type <paramref name="from"/>, through <paramref name="table"/> into
    /// <paramref name="into"/>, of type <paramref name="to"/>; the rows <paramref name="valid"/> leaves out take code 0.
    /// The first valid row whose code the table has no entry for, or -1: the caller reports it.
    /// </summary>
    internal static int Translate(
        ReadOnlySpan<byte> codes, PType from, ReadOnlySpan<int> table, in ValidityMask valid, Span<byte> into, PType to, int rows) =>
        to.ByteWidth() == 1
            ? Translate(codes, from, table, in valid, MemoryMarshal.Cast<byte, byte>(into)[..rows])
            : Translate(codes, from, table, in valid, MemoryMarshal.Cast<byte, ushort>(into)[..rows]);

    private static int Translate<TTo>(ReadOnlySpan<byte> codes, PType from, ReadOnlySpan<int> table, in ValidityMask valid, Span<TTo> into)
        where TTo : unmanaged, IBinaryInteger<TTo> => from.ByteWidth() switch
        {
            1 => Translate(MemoryMarshal.Cast<byte, byte>(codes), table, in valid, into),
            2 => Translate(MemoryMarshal.Cast<byte, ushort>(codes), table, in valid, into),
            4 => Translate(MemoryMarshal.Cast<byte, uint>(codes), table, in valid, into),
            _ => Translate(MemoryMarshal.Cast<byte, ulong>(codes), table, in valid, into),
        };

    private static int Translate<TFrom, TTo>(ReadOnlySpan<TFrom> codes, ReadOnlySpan<int> table, in ValidityMask valid, Span<TTo> into)
        where TFrom : unmanaged, IBinaryInteger<TFrom>, IUnsignedNumber<TFrom>
        where TTo : unmanaged, IBinaryInteger<TTo>
    {
        // Read unsigned, a signed code that reads negative lies past every entry.
        codes = codes[..into.Length];
        for (int row = 0; row < into.Length; row++)
        {
            if (!valid.AllValid && !valid.IsValid(row))
            {
                into[row] = TTo.Zero;
                continue;
            }

            ulong code = ulong.CreateTruncating(codes[row]);
            int mapped = code < (ulong)table.Length ? table[(int)code] : -1;
            if (mapped < 0)
            {
                return row;
            }

            into[row] = TTo.CreateTruncating(mapped);
        }

        return -1;
    }

    /// <summary>
    /// The codes of the rows of <paramref name="node"/>, a column of this dictionary's values, as a primitive node of
    /// <paramref name="codesType"/> in <paramref name="arena"/> with the column's validity: what a chunk of the column
    /// holds. A null row takes code 0.
    /// </summary>
    /// <exception cref="InvalidOperationException">A row holds a value the dictionary lacks.</exception>
    internal int Code(CanonicalArena arena, int node, DType codesType)
    {
        ArgumentNullException.ThrowIfNull(arena);
        CanonicalNode column = arena.GetNode(node);
        if (column.Kind == CanonicalKind.Constant)
        {
            node = arena.MaterializeConstant(node);
            column = arena.GetNode(node);
        }
        else if (column.Kind == CanonicalKind.RunEnd)
        {
            node = arena.MaterializeEncoded(node);
            column = arena.GetNode(node);
        }

        int rows = column.Length;
        PType codesPType = CodesPType;
        VortexBuffer codes = arena.AllocateUninitialized(rows * codesPType.ByteWidth(), codesPType.ByteWidth(), out Span<byte> into);
        ValidityMask valid = ValidityMask.From(arena, column.Validity);
        int missing;
        if (column.Kind == CanonicalKind.Dictionary)
        {
            // An encoded column's values, each looked up once, then its codes through them.
            int values = column.EncodedValuesIndex;
            int[] table = ArrayPool<int>.Shared.Rent(Math.Max(arena.GetNode(values).Length, 1));
            try
            {
                Span<int> translation = table.AsSpan(0, arena.GetNode(values).Length);
                Translation(arena, values, translation);
                missing = Translate(column.Codes.Span, PType.U32, translation, in valid, into, codesPType, rows);
            }
            finally
            {
                ArrayPool<int>.Shared.Return(table);
            }
        }
        else if (_width == 0)
        {
            missing = codesPType == PType.U8
                ? CodeViews(column, in valid, MemoryMarshal.Cast<byte, byte>(into))
                : CodeViews(column, in valid, MemoryMarshal.Cast<byte, ushort>(into));
        }
        else
        {
            missing = codesPType == PType.U8
                ? CodeNumbers(column, in valid, MemoryMarshal.Cast<byte, byte>(into))
                : CodeNumbers(column, in valid, MemoryMarshal.Cast<byte, ushort>(into));
        }

        if (missing >= 0)
        {
            throw new InvalidOperationException(
                $"Row {missing} of a chunk holds a value its column's object dictionary of {Count} values lacks.");
        }

        return arena.AddPrimitive(codesType, rows, column.Validity, codesPType, codes);
    }

    /// <summary>A text column's codes; the first row whose value the dictionary lacks, or -1.</summary>
    private int CodeViews<T>(CanonicalNode column, in ValidityMask valid, Span<T> into)
        where T : unmanaged, IBinaryInteger<T>
    {
        ViewValues values = new ViewValues(column);
        ReadOnlySpan<ulong> views = MemoryMarshal.Cast<byte, ulong>(column.Views.Span);

        // Two equal views are two equal values: the same bytes inline, or the same place in the same buffer.
        ulong low = 0;
        ulong high = 0;
        T last = T.Zero;
        bool held = false;
        for (int row = 0; row < into.Length; row++)
        {
            if (!valid.AllValid && !valid.IsValid(row))
            {
                into[row] = T.Zero;
                continue;
            }

            ulong rowLow = views[row * 2];
            ulong rowHigh = views[(row * 2) + 1];
            if (held && rowLow == low && rowHigh == high)
            {
                into[row] = last;
                continue;
            }

            int code = Find(values.At(row));
            if (code < 0)
            {
                return row;
            }

            last = T.CreateTruncating(code);
            into[row] = last;
            (low, high, held) = (rowLow, rowHigh, true);
        }

        return -1;
    }

    /// <summary>A column of numbers' codes; the first row whose value the dictionary lacks, or -1.</summary>
    private int CodeNumbers<T>(CanonicalNode column, in ValidityMask valid, Span<T> into)
        where T : unmanaged, IBinaryInteger<T>
    {
        ReadOnlySpan<byte> bytes = column.Values.Span;
        ulong previous = 0;
        T last = T.Zero;
        bool held = false;
        for (int row = 0; row < into.Length; row++)
        {
            if (!valid.AllValid && !valid.IsValid(row))
            {
                into[row] = T.Zero;
                continue;
            }

            ulong bits = Bits(bytes, row, _width);
            if (held && bits == previous)
            {
                into[row] = last;
                continue;
            }

            int code = Find(bits);
            if (code < 0)
            {
                return row;
            }

            last = T.CreateTruncating(code);
            into[row] = last;
            (previous, held) = (bits, true);
        }

        return -1;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong Bits(ReadOnlySpan<byte> bytes, int row, int width) =>
        width == 4 ? BinaryPrimitives.ReadUInt32LittleEndian(bytes[(row * 4)..]) : BinaryPrimitives.ReadUInt64LittleEndian(bytes[(row * 8)..]);

    /// <summary>
    /// The values as a canonical column of <paramref name="dtype"/> in <paramref name="arena"/>, every one valid, viewing
    /// the dictionary's own memory: the values child of the column's <c>vortex.dict</c> layout.
    /// </summary>
    internal int ValuesNode(CanonicalArena arena, DType dtype)
    {
        ArgumentNullException.ThrowIfNull(arena);
        Validity validity = dtype.IsNullable ? Validity.AllValid : Validity.NonNullable;
        if (_width != 0)
        {
            return arena.AddPrimitive(
                dtype, Count, validity, PType, VortexBuffer.FromPinned(_bytes.AsSpan(0, Count * _width), BitOperations.Log2((uint)_width)));
        }

        // A view of twelve bytes or fewer holds them inline, zero past them; a longer one its first four, and where it lies.
        VortexBuffer views = arena.Allocate(Count * ViewSize, ViewSize, out Span<byte> written);
        for (int code = 0; code < Count; code++)
        {
            Span<byte> view = written.Slice(code * ViewSize, ViewSize);
            int start = _offsets[code];
            int length = _offsets[code + 1] - start;
            BinaryPrimitives.WriteInt32LittleEndian(view, length);
            if (length <= InlineBytes)
            {
                _bytes.AsSpan(start, length).CopyTo(view[4..]);
                continue;
            }

            _bytes.AsSpan(start, 4).CopyTo(view[4..]);
            BinaryPrimitives.WriteInt32LittleEndian(view[8..], 0);
            BinaryPrimitives.WriteInt32LittleEndian(view[12..], start);
        }

        ReadOnlySpan<VortexBuffer> heap = [VortexBuffer.FromPinned(_bytes.AsSpan(0, Math.Max(_offsets[Count], 0)), 0)];
        return arena.AddVarBinView(dtype, Count, validity, views, heap, padded: true);
    }

    /// <summary>
    /// The distinct values of one or more columns, as rows come: text into a heap of its own, numbers as their bits, each
    /// found again through a table that grows. Then sorted, and laid out as a dictionary.
    /// </summary>
    /// <remarks>
    /// Its working arrays come from the process's shelf and go back when it is disposed: a collector a column and a part
    /// written, they were made new as they grew, 4 bytes a row of a build of the client's log records.
    /// </remarks>
    private sealed class Collector : IDisposable
    {
        private readonly DTypeKind _kind;
        private readonly PType _ptype;
        private readonly int _width;
        private byte[] _heap = ArrayShelf.Retained.Take<byte>(1024, zeroed: false);
        private int _heapUsed;
        private int[] _offsets = ArrayShelf.Retained.Take<int>(65, zeroed: true);
        private ulong[] _keys = ArrayShelf.Retained.Take<ulong>(64, zeroed: false);
        private int[] _slots = ArrayShelf.Retained.Take<int>(128, zeroed: true);
        private int _mask = 127;
        private int _count;

        internal Collector(DType type)
        {
            _kind = type.Kind;
            _ptype = type.Kind == DTypeKind.Primitive ? type.PType : default;
            _width = type.Kind == DTypeKind.Primitive ? type.PType.ByteWidth() : 0;
        }

        /// <summary>Adds the values of the column <paramref name="node"/>; false past the limits, or a column of another kind.</summary>
        internal bool Add(CanonicalArena arena, int node)
        {
            CanonicalNode column = arena.GetNode(node);
            if (column.Kind is CanonicalKind.Constant or CanonicalKind.Dictionary or CanonicalKind.RunEnd)
            {
                // A dictionary's values are its distinct ones, which spare a pass over its rows.
                int values = column.Kind == CanonicalKind.Dictionary
                    ? column.EncodedValuesIndex
                    : arena.Decoded(column.Kind == CanonicalKind.Constant ? arena.MaterializeConstant(node) : node);
                column = arena.GetNode(values);
            }

            if (column.DType.Kind != _kind || (_width != 0 && (column.Kind != CanonicalKind.Primitive || column.PType != _ptype))
                || (_width == 0 && column.Kind != CanonicalKind.VarBinView))
            {
                return false;
            }

            ValidityMask valid = ValidityMask.From(arena, column.Validity);
            if (_width == 0)
            {
                ViewValues views = new ViewValues(column);
                for (int row = 0; row < column.Length; row++)
                {
                    if (valid.IsValid(row) && !AddBytes(views.At(row)))
                    {
                        return false;
                    }
                }

                return true;
            }

            ReadOnlySpan<byte> bytes = column.Values.Span;
            for (int row = 0; row < column.Length; row++)
            {
                if (valid.IsValid(row) && !AddBits(Bits(bytes, row, _width)))
                {
                    return false;
                }
            }

            return true;
        }

        private bool AddBytes(ReadOnlySpan<byte> value)
        {
            ulong hash = KeyHash.Bytes(value);
            int slot = (int)hash & _mask;
            while (_slots[slot] != 0)
            {
                int code = _slots[slot] - 1;
                if (_keys[code] == hash && _offsets[code + 1] - _offsets[code] == value.Length
                    && _heap.AsSpan(_offsets[code], value.Length).SequenceEqual(value))
                {
                    return true;
                }

                slot = (slot + 1) & _mask;
            }

            if (_count == MostValues || _heapUsed + value.Length > MostBytes)
            {
                return false;
            }

            if (_heapUsed + value.Length > _heap.Length)
            {
                ArrayShelf.Resize(ArrayShelf.Retained, ref _heap, ArrayShelf.Grown(_heapUsed + value.Length, _heap.Length));
            }

            value.CopyTo(_heap.AsSpan(_heapUsed));
            _heapUsed += value.Length;
            Insert(slot, hash);
            _offsets[_count] = _heapUsed;
            return true;
        }

        private bool AddBits(ulong bits)
        {
            int slot = (int)KeyHash.Mix(bits) & _mask;
            while (_slots[slot] != 0)
            {
                if (_keys[_slots[slot] - 1] == bits)
                {
                    return true;
                }

                slot = (slot + 1) & _mask;
            }

            if (_count == MostValues || (_count + 1) * _width > MostBytes)
            {
                return false;
            }

            Insert(slot, bits);
            return true;
        }

        /// <summary>Takes the next code at <paramref name="slot"/>, growing the table past half full.</summary>
        private void Insert(int slot, ulong key)
        {
            // A key a code, and an offset a code and one past the last.
            if (_count == _keys.Length)
            {
                ArrayShelf.Resize(ArrayShelf.Retained, ref _keys, _keys.Length * 2);
                ArrayShelf.Resize(ArrayShelf.Retained, ref _offsets, _keys.Length + 1);
            }

            _keys[_count] = key;
            _slots[slot] = ++_count;
            if (_count * 2 > _mask)
            {
                int slots = _slots.Length * 2;
                ArrayShelf.Retained.Give(_slots);
                _slots = ArrayShelf.Retained.Take<int>(slots, zeroed: true);
                _mask = _slots.Length - 1;
                for (int code = 0; code < _count; code++)
                {
                    int at = (int)(_width == 0 ? _keys[code] : KeyHash.Mix(_keys[code])) & _mask;
                    while (_slots[at] != 0)
                    {
                        at = (at + 1) & _mask;
                    }

                    _slots[at] = code + 1;
                }
            }
        }

        /// <summary>The values sorted in their type's order, as a dictionary.</summary>
        internal ObjectDictionary Finish(bool allReferenced)
        {
            if (_width == 0)
            {
                // Text and binary order by their bytes, unsigned, the shorter first on a tie.
                int[] held = ArrayShelf.Retained.Take<int>(ArrayShelf.Grown(_count, 0, least: 64), zeroed: false);
                Span<int> order = held.AsSpan(0, _count);
                for (int i = 0; i < order.Length; i++)
                {
                    order[i] = i;
                }

                order.Sort(new ByteOrder(_heap, _offsets));
                byte[] bytes = GC.AllocateUninitializedArray<byte>(Math.Max(_heapUsed, 1), pinned: true);
                int[] offsets = new int[_count + 1];
                ulong[] hashes = new ulong[_count];
                int used = 0;
                for (int code = 0; code < _count; code++)
                {
                    int from = order[code];
                    int start = _offsets[from];
                    int length = _offsets[from + 1] - start;
                    _heap.AsSpan(start, length).CopyTo(bytes.AsSpan(used));
                    offsets[code] = used;
                    hashes[code] = _keys[from];
                    used += length;
                }

                offsets[_count] = used;
                ArrayShelf.Retained.Give(held);
                return new ObjectDictionary(_kind, default, bytes, offsets, hashes, _count, allReferenced);
            }

            // Numbers order by their value: a signed one's sign bit flipped makes its bits order as unsigned do.
            ulong flip = _ptype.IsSignedInteger() ? 1UL << ((_width * 8) - 1) : 0;
            Span<ulong> sorted = _keys.AsSpan(0, _count);
            for (int i = 0; i < sorted.Length; i++)
            {
                sorted[i] ^= flip;
            }

            sorted.Sort();
            byte[] values = GC.AllocateUninitializedArray<byte>(Math.Max(_count * _width, 1), pinned: true);
            ulong[] keys = new ulong[_count];
            for (int code = 0; code < _count; code++)
            {
                ulong bits = sorted[code] ^ flip;
                keys[code] = bits;
                if (_width == 4)
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(values.AsSpan(code * 4), (uint)bits);
                }
                else
                {
                    BinaryPrimitives.WriteUInt64LittleEndian(values.AsSpan(code * 8), bits);
                }
            }

            return new ObjectDictionary(_kind, _ptype, values, [], keys, _count, allReferenced);
        }

        /// <summary>Its working arrays back to the process's shelf, once: what it finished holds arrays of its own.</summary>
        public void Dispose()
        {
            ArrayShelf.Retained.Give(_heap);
            ArrayShelf.Retained.Give(_offsets);
            ArrayShelf.Retained.Give(_keys);
            ArrayShelf.Retained.Give(_slots);
            _heap = [];
            _offsets = [];
            _keys = [];
            _slots = [];
        }

        /// <summary>The order of two collected values' bytes; a value starts at its offset and ends at the next.</summary>
        private readonly struct ByteOrder(byte[] heap, int[] offsets) : IComparer<int>
        {
            public int Compare(int x, int y) => Bytes(x).SequenceCompareTo(Bytes(y));

            private ReadOnlySpan<byte> Bytes(int code) => heap.AsSpan(offsets[code], offsets[code + 1] - offsets[code]);
        }
    }
}
