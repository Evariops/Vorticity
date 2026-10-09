using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Vorticity.Arrays;
using Vorticity.Types;
using Vorticity.Writing;

namespace Vorticity.Aggregating;

/// <summary>
/// Where each column of a <see cref="RawKeys{TWord}"/> key lies in its word: its value's bits, from
/// the widest column down so that none straddles the two halves of a word of 128 bits, then a bit a
/// nullable column for its null.
/// </summary>
internal sealed class RawLayout
{
    private RawLayout(ColumnShape[] shapes, int[] widths, int[] shifts, int[] nullBits, int bits)
    {
        Shapes = shapes;
        Widths = widths;
        Shifts = shifts;
        NullBits = nullBits;
        Bits = bits;
    }

    internal ColumnShape[] Shapes { get; }

    /// <summary>The bits of each column's value: 8, 16, 32 or 64.</summary>
    internal int[] Widths { get; }

    /// <summary>The first bit of each column's value.</summary>
    internal int[] Shifts { get; }

    /// <summary>The bit of each column's null, -1 for a column that holds none.</summary>
    internal int[] NullBits { get; }

    /// <summary>The bits the word takes.</summary>
    internal int Bits { get; }

    /// <summary>
    /// The layout of a key of two to four primitive columns, integers or floats, whose values and
    /// nulls fit 128 bits; null for any other.
    /// </summary>
    internal static RawLayout? Of(ColumnShape[] shapes)
    {
        if (shapes.Length is < 2 or > 4)
        {
            return null;
        }

        int[] widths = new int[shapes.Length];
        for (int p = 0; p < shapes.Length; p++)
        {
            if (shapes[p].Kind != StorageKind.Primitive || Width(shapes[p].PType) is not int width)
            {
                return null;
            }

            widths[p] = width;
        }

        // From the widest down, each value starts at a multiple of its width: a word of 128 bits
        // holds a value in one half or the other, never across.
        int[] shifts = new int[shapes.Length];
        int bits = 0;
        foreach (int width in (int[])[64, 32, 16, 8])
        {
            for (int p = 0; p < shapes.Length; p++)
            {
                if (widths[p] == width)
                {
                    shifts[p] = bits;
                    bits += width;
                }
            }
        }

        int[] nullBits = new int[shapes.Length];
        for (int p = 0; p < shapes.Length; p++)
        {
            nullBits[p] = shapes[p].Type.IsNullable ? bits++ : -1;
        }

        return bits <= 128 ? new RawLayout(shapes, widths, shifts, nullBits, bits) : null;
    }

    private static int? Width(PType type) => type switch
    {
        PType.I8 or PType.U8 => 8,
        PType.I16 or PType.U16 or PType.F16 => 16,
        PType.I32 or PType.U32 or PType.F32 => 32,
        PType.I64 or PType.U64 or PType.F64 => 64,
        _ => null,
    };
}

/// <summary>
/// A key of two to four fixed-width columns as the tuple of their values, packed into one word: one
/// probe a row, where <see cref="PackedKeys{TKey}"/> probes an index
/// a column, then the tuple of their numbers. It is the form a key of several columns takes by itself,
/// with no index of a column to share.
/// </summary>
/// <remarks>
/// <para>
/// A float's bits are made one pattern a value first, every NaN one and both zeros one, and a null's
/// bits zero beside its null bit: equal tuples, equal words.
/// </para>
/// <para>
/// The groups are found by open addressing, a slot from the top bits of the word times an odd number
/// drawn once a process (<see cref="MergeHash.Seed"/>), in a table at most half full whose slot of
/// four bytes holds a group number plus one in its low bits, as many as the slots', and the hash's
/// bits below the home's in the rest; the words are kept once, by group. A slot of four bytes rather
/// than one holding the word: measured on 2026-10-06, a pair of ints at 1.8M groups, nine rows in ten
/// a new one, ran 1.75 times as long in the engine's own table, whose slot of sixteen bytes made the
/// table 74 MB where this one takes 16.
/// </para>
/// <para>
/// A key a statistics prove sorted, which hands its rows by range, and a product of bounded columns
/// small enough for a table of groups, stay with <see cref="PackedKeys{TKey}"/>.
/// </para>
/// </remarks>
/// <typeparam name="TWord">The word: <see cref="ulong"/> for 64 bits at most, <see cref="UInt128"/> for 128.</typeparam>
internal sealed class RawKeys<TWord> : GroupKeys
    where TWord : unmanaged, IEquatable<TWord>
{
    private readonly RawLayout _layout;
    private TWord[] _keys = new TWord[16];

    // The groups by open addressing, the slots a power of two at most half full, zero for none: a
    // group's number plus one in the low bits, as many as the slots' (a table at most half full numbers
    // fewer groups), under the bits of the word's hash below those of its home, compared before the
    // group's word is read. One array: a new word reads and writes one line of it, where a byte of tag
    // and a group number in two arrays made two lines, the numbers written at random chasing the tags
    // out of the cache.
    private uint[] _slots = new uint[32];

    // A batch's words, built a column at a time in two halves.
    private ulong[] _low = [];
    private ulong[] _high = [];
    private TWord[] _words = [];

    // Every word a group of its own, no slot looked up (Appending); the shelf a sub-table's arrays come from (ForTable).
    private readonly bool _appending;
    private readonly ArrayShelf? _shelf;

    internal RawKeys(RawLayout layout, bool appending = false, ArrayShelf? shelf = null)
    {
        _layout = layout;
        _appending = appending;
        _shelf = shelf;
    }

    internal override bool Assign(CanonicalArena arena, ReadOnlySpan<int> nodes, int rows, ReadOnlySpan<ulong> selection, int[] rowGroups, GroupRanges ranges)
    {
        ReadOnlySpan<TWord> words = Words(arena, nodes, rows);
        bool hasLast = false;
        TWord last = default;
        int lastGroup = -1;
        for (int start = 0; start < rows; start += Window)
        {
            int end = Math.Min(rows, start + Window);
            if (!_appending)
            {
                Touch(words[start..end]);
            }

            RowCursor selected = new RowCursor(selection, start, end);
            while (selected.Next(out int row))
            {
                // A row whose tuple is the row before's is a comparison of words.
                TWord word = words[row];
                if (!hasLast || !word.Equals(last))
                {
                    last = word;
                    lastGroup = Lookup(word);
                    hasLast = true;
                }

                rowGroups[row] = lastGroup;
            }
        }

        return false;
    }

    /// <summary>The rows whose home slots a window reads at once before their lookups.</summary>
    private const int Window = 256;

    // What the reads ahead found, kept so that they stay.
    private uint _sink;

    /// <summary>
    /// Reads the home slot of each word of a window, with no branch on what it holds: the reads go out
    /// together, and the lookups that follow find their slots' lines in cache, where each waited on its
    /// own, its branch on the slot unknown until the line came.
    /// </summary>
    private void Touch(ReadOnlySpan<TWord> words)
    {
        uint[] slots = _slots;
        int bits = System.Numerics.BitOperations.Log2((uint)slots.Length);
        uint mask = (uint)slots.Length - 1;
        ref uint first = ref MemoryMarshal.GetArrayDataReference(slots);
        ref TWord keys = ref MemoryMarshal.GetArrayDataReference(_keys);
        uint sink = 0;
        foreach (TWord word in words)
        {
            // The slot at home, and when its bits agree, the word of its group: a word met before is
            // compared without waiting on its group's word either.
            uint top = (uint)(Hash(word) >> 32);
            uint seen = Unsafe.Add(ref first, (nint)(top >> (32 - bits)));
            uint agrees = (uint)-Unsafe.BitCast<bool, byte>((seen & ~mask) == (top << bits));
            uint number = seen & mask & agrees;
            uint group = number - Unsafe.BitCast<bool, byte>(number != 0);
            sink ^= Unsafe.As<TWord, uint>(ref Unsafe.Add(ref keys, (nint)group));
        }

        _sink ^= sink;
    }

    internal override GroupKeys Fresh() => new RawKeys<TWord>(_layout);

    /// <summary>The words of the groups, the slots that find them, and a batch's words.</summary>
    internal override long Footprint =>
        ((long)(_keys.Length + _words.Length) * Unsafe.SizeOf<TWord>()) + ((long)_slots.Length * sizeof(uint))
        + ((long)(_low.Length + _high.Length) * sizeof(ulong));

    internal override void Reserve(int groups)
    {
        if (_keys.Length < groups)
        {
            Grow(groups);
        }

        int slots = (int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)Math.Max(32, groups * 2));
        if (_slots.Length < slots)
        {
            Rehash(slots);
        }
    }

    /// <summary>A sub-table of the core: its arrays from the query's shelf.</summary>
    internal override GroupKeys ForTable(ArrayShelf shelf) => new RawKeys<TWord>(_layout, shelf: shelf);

    internal override void Release()
    {
        _shelf?.Give(_keys);
        _shelf?.Give(_slots);
        _keys = [];
        _slots = new uint[32];
        Count = 0;
    }

    /// <summary>The words' array grown to <paramref name="length"/>: from the shelf of a sub-table, the old one given back.</summary>
    private void Grow(int length)
    {
        TWord[] grown = _shelf is null ? new TWord[length] : _shelf.Take<TWord>(length, zeroed: false);
        _keys.AsSpan(0, Count).CopyTo(grown);
        _shelf?.Give(_keys);
        _keys = grown;
    }

    internal override void Keep(ReadOnlySpan<int> groups)
    {
        for (int i = 0; i < groups.Length; i++)
        {
            _keys[i] = _keys[groups[i]];
        }

        Count = groups.Length;
        Rehash(_slots.Length);
    }

    internal override void MergeInto(GroupKeys target, ReadOnlySpan<int> groups, Span<int> map)
    {
        RawKeys<TWord> into = (RawKeys<TWord>)target;
        for (int i = 0; i < groups.Length; i++)
        {
            map[i] = into.Lookup(_keys[groups[i]]);
        }

        MergeSeen(target);
    }

    internal override void Parts(ulong seed, int shift, Span<byte> parts)
    {
        for (int g = 0; g < Count; g++)
        {
            parts[g] = (byte)(EntryKeys.Hash(_keys[g], seed) >> shift);
        }
    }

    internal override bool Spills => !_appending;

    internal override GroupKeys ForSpill(ArrayShelf? shelf) => new RawKeys<TWord>(_layout, shelf: shelf);

    /// <summary>The words' array, and the slots, at most half full.</summary>
    internal override long GrowthFor(int more) =>
        TableGrowth.Of(Count, more, _keys.Length, _keys.Length, Unsafe.SizeOf<TWord>()) + TableGrowth.Of(Count, more, _slots.Length, _slots.Length / 2, sizeof(uint));

    internal override void Hashes(Span<ulong> hashes)
    {
        for (int g = 0; g < Count; g++)
        {
            hashes[g] = EntryKeys.Hash(_keys[g], MergeHash.Seed);
        }
    }

    /// <summary>Each tuple's word, its nulls bits of it.</summary>
    internal override void WriteKeys(ReadOnlySpan<int> groups, SpillBuffer buffer)
    {
        Span<byte> words = buffer.Take(groups.Length * Unsafe.SizeOf<TWord>());
        for (int i = 0; i < groups.Length; i++)
        {
            MemoryMarshal.Write(words[(i * Unsafe.SizeOf<TWord>())..], in _keys[groups[i]]);
        }
    }

    internal override void ReadKeys(ref SpillReader reader, Span<int> groups)
    {
        for (int i = 0; i < groups.Length; i++)
        {
            groups[i] = Lookup(reader.Read<TWord>());
        }
    }

    /// <summary>The word: a tuple's nulls are bits of it, so no group is apart from the others.</summary>
    internal override int EntryBytes => Unsafe.SizeOf<TWord>();

    internal override GroupKeys? Appending() => new RawKeys<TWord>(_layout, appending: true);

    internal override void Scatter(ReadOnlySpan<ulong> records, LaneCore lane) => EntryKeys.Scatter<TWord>(_keys.AsSpan(0, Count), -1, records, lane);

    internal override void CountParts(Span<int> counts) => EntryKeys.CountParts<TWord>(_keys.AsSpan(0, Count), -1, counts);

    internal override int CopyEntries(ReadOnlySpan<ulong> records, EntryShape shape, int from, Span<ulong> entries, out int written) =>
        EntryKeys.Copy<TWord>(_keys.AsSpan(0, Count), -1, records, shape, from, entries, out written);

    internal override void TablesOf(PartBatch batch, EntryShape shape, int shift, int mask, Span<int> tables) =>
        EntryKeys.TablesOf<TWord>(batch, shape, shift, mask, tables);

    internal override ulong HashAt(PartBatch batch, EntryShape shape, int entry) => EntryKeys.Hash(EntryKeys.KeyAt<TWord>(batch, shape, entry), MergeHash.Seed);

    internal override void GroupsOf(PartBatch batch, EntryShape shape, ReadOnlySpan<int> entries, Span<int> groups, Span<ulong> scratch)
    {
        for (int i = 0; i < entries.Length; i++)
        {
            groups[i] = Lookup(EntryKeys.KeyAt<TWord>(batch, shape, entries[i]));
        }
    }

    internal override int[] Order(bool sorted) => Identity(Count);

    internal override bool Orders(int component) => !IsFloat(_layout.Shapes[component].PType);

    internal override int CompareKeys(int a, int b, int component) => Compare(_keys[a], _keys[b], component);

    internal override int CompareKeys(GroupKeys other, int a, int b, int component) =>
        Compare(_keys[a], ((RawKeys<TWord>)other)._keys[b], component);

    internal override Func<int, T> Reader<T>(int component)
    {
        ColumnShape shape = _layout.Shapes[component];
        return shape.PType switch
        {
            PType.I8 => group => Read<sbyte, T>(group, component, shape),
            PType.I16 => group => Read<short, T>(group, component, shape),
            PType.I32 => group => Read<int, T>(group, component, shape),
            PType.I64 => group => Read<long, T>(group, component, shape),
            PType.U8 => group => Read<byte, T>(group, component, shape),
            PType.U16 => group => Read<ushort, T>(group, component, shape),
            PType.U32 => group => Read<uint, T>(group, component, shape),
            PType.U64 => group => Read<ulong, T>(group, component, shape),
            PType.F16 => group => Read<Half, T>(group, component, shape),
            PType.F32 => group => Read<float, T>(group, component, shape),
            _ => group => Read<double, T>(group, component, shape),
        };
    }

    internal override void Append(int component, ColumnStore store, ReadOnlySpan<int> groups)
    {
        switch (_layout.Shapes[component].PType)
        {
            case PType.I8: Append<sbyte>(component, store, groups); break;
            case PType.I16: Append<short>(component, store, groups); break;
            case PType.I32: Append<int>(component, store, groups); break;
            case PType.I64: Append<long>(component, store, groups); break;
            case PType.U8: Append<byte>(component, store, groups); break;
            case PType.U16: Append<ushort>(component, store, groups); break;
            case PType.U32: Append<uint>(component, store, groups); break;
            case PType.U64: Append<ulong>(component, store, groups); break;
            case PType.F16: Append<Half>(component, store, groups); break;
            case PType.F32: Append<float>(component, store, groups); break;
            default: Append<double>(component, store, groups); break;
        }
    }

    /// <summary>The group of a word, added when it is new.</summary>
    private int Lookup(TWord word)
    {
        if (_appending)
        {
            if (Count == _keys.Length)
            {
                Grow(Doubled(Count));
            }

            _keys[Count] = word;
            return Count++;
        }

        // A slot's hash bits first: a new word, nine rows in ten of 1.8M pairs, finds its free slot
        // without reading a word, where it read the word of each group its chain passed.
        uint[] slots = _slots;
        int bits = System.Numerics.BitOperations.Log2((uint)slots.Length);
        uint mask = (uint)slots.Length - 1;
        uint top = (uint)(Hash(word) >> 32);
        uint at = top >> (32 - bits);
        uint tagged = top << bits;
        while (true)
        {
            uint seen = slots[at];
            if (seen == 0)
            {
                break;
            }

            if ((seen & ~mask) == tagged)
            {
                int group = (int)(seen & mask) - 1;
                if (_keys[group].Equals(word))
                {
                    return group;
                }
            }

            at = (at + 1) & mask;
        }

        if (Count == _keys.Length)
        {
            Grow(Doubled(Count));
        }

        int added = Count++;
        _keys[added] = word;
        slots[at] = tagged | (uint)Count;
        if (Count * 2 > slots.Length)
        {
            Rehash(Doubled(slots.Length));
        }

        return added;
    }

    /// <summary>The slots at <paramref name="length"/>, every group placed again from its word.</summary>
    private void Rehash(int length)
    {
        uint[] slots = _slots;
        if (slots.Length == length)
        {
            slots.AsSpan().Clear();
        }
        else
        {
            slots = _shelf is null ? new uint[length] : _shelf.Take<uint>(length, zeroed: true);
            _shelf?.Give(_slots);
        }

        int bits = System.Numerics.BitOperations.Log2((uint)length);
        uint mask = (uint)length - 1;
        for (int g = 0; g < Count; g++)
        {
            uint top = (uint)(Hash(_keys[g]) >> 32);
            uint at = top >> (32 - bits);
            while (slots[at] != 0)
            {
                at = (at + 1) & mask;
            }

            slots[at] = (top << bits) | (uint)(g + 1);
        }

        _slots = slots;
    }

    /// <summary>A word's hash, whose top bits are its home slot: the word times the process's odd number, the halves of a wide one mixed first.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong Hash(TWord word)
    {
        if (typeof(TWord) == typeof(ulong))
        {
            return Unsafe.BitCast<TWord, ulong>(word) * MergeHash.Seed;
        }

        UInt128 wide = Unsafe.BitCast<TWord, UInt128>(word);
        return (((ulong)wide * MergeHash.Seed) ^ (ulong)(wide >> 64)) * MergeHash.Seed;
    }

    /// <summary>The words of a batch's rows: each column's bits shifted into their place, a half of the word at a time, and its nulls.</summary>
    private ReadOnlySpan<TWord> Words(CanonicalArena arena, ReadOnlySpan<int> nodes, int rows)
    {
        bool wide = Unsafe.SizeOf<TWord>() > sizeof(ulong);
        Scratch.Grow(ref _low, rows);
        Span<ulong> low = _low.AsSpan(0, rows);
        low.Clear();
        Span<ulong> high = default;
        if (wide)
        {
            Scratch.Grow(ref _high, rows);
            high = _high.AsSpan(0, rows);
            high.Clear();
        }

        for (int p = 0; p < nodes.Length; p++)
        {
            int canonical = EncodedForms.Canonical(arena, nodes[p]);
            ReadOnlySpan<byte> bytes = arena.RecordRef(canonical).BufferA.Span;
            int shift = _layout.Shifts[p];
            Span<ulong> into = shift < 64 ? low : high;
            int at = shift & 63;
            switch (_layout.Shapes[p].PType)
            {
                case PType.I8 or PType.U8: Or<byte>(bytes, into, at); break;
                case PType.I16 or PType.U16: Or<ushort>(bytes, into, at); break;
                case PType.I32 or PType.U32: Or<uint>(bytes, into, at); break;
                case PType.I64 or PType.U64: Or<ulong>(bytes, into, at); break;
                case PType.F16: Or<Half>(bytes, into, at); break;
                case PType.F32: Or<float>(bytes, into, at); break;
                default: Or<double>(bytes, into, at); break;
            }

            int nullBit = _layout.NullBits[p];
            ReadOnlySpan<ulong> validity = ArenaWords.Validity(arena, canonical);
            if (nullBit >= 0 && !validity.IsEmpty)
            {
                Span<ulong> flags = nullBit < 64 ? low : high;
                ulong flag = 1UL << (nullBit & 63);
                int width = _layout.Widths[p];
                ulong value = (width == 64 ? ulong.MaxValue : (1UL << width) - 1) << at;
                for (int row = 0; row < rows; row++)
                {
                    if (!StorageValues.IsValid(validity, row))
                    {
                        into[row] &= ~value;
                        flags[row] |= flag;
                    }
                }
            }
        }

        Scratch.Grow(ref _words, rows);
        Span<TWord> words = _words.AsSpan(0, rows);
        if (wide)
        {
            Span<UInt128> wideWords = MemoryMarshal.Cast<TWord, UInt128>(words);
            for (int row = 0; row < rows; row++)
            {
                wideWords[row] = new UInt128(high[row], low[row]);
            }
        }
        else
        {
            low.CopyTo(MemoryMarshal.Cast<TWord, ulong>(words));
        }

        return words;
    }

    /// <summary>Ors each value of a column, as its bits, into its place in the words: a float's made one pattern a value.</summary>
    private static void Or<TValue>(ReadOnlySpan<byte> bytes, Span<ulong> into, int at)
        where TValue : unmanaged
    {
        ReadOnlySpan<TValue> values = MemoryMarshal.Cast<byte, TValue>(bytes)[..into.Length];
        for (int row = 0; row < values.Length; row++)
        {
            into[row] |= KeyWords.Of(values[row]).Low << at;
        }
    }

    /// <summary>Component <paramref name="component"/> of a word: whether it is null, and its value's bits.</summary>
    private (bool Null, ulong Bits) Component(TWord word, int component)
    {
        (ulong low, ulong high) = KeyWords.Of(word);
        int shift = _layout.Shifts[component];
        int width = _layout.Widths[component];
        ulong bits = (shift < 64 ? low : high) >> (shift & 63);
        if (width < 64)
        {
            bits &= (1UL << width) - 1;
        }

        int nullBit = _layout.NullBits[component];
        bool isNull = nullBit >= 0 && (((nullBit < 64 ? low : high) >> (nullBit & 63)) & 1) != 0;
        return (isNull, bits);
    }

    private T Read<TValue, T>(int group, int component, ColumnShape shape)
        where TValue : unmanaged
    {
        (bool isNull, ulong bits) = Component(_keys[group], component);
        return isNull ? default! : StorageValues.ToClr<TValue, T>(Value<TValue>(bits), shape);
    }

    private void Append<TValue>(int component, ColumnStore store, ReadOnlySpan<int> groups)
        where TValue : unmanaged
    {
        StorageKind kind = _layout.Shapes[component].Kind;
        if (kind == StorageKind.Primitive && store.Leaf is FixedStore leaf && leaf.Width == Unsafe.SizeOf<TValue>())
        {
            AppendValues<TValue>(component, store.IsNullable, leaf, groups);
            return;
        }

        foreach (int group in groups)
        {
            (bool isNull, ulong bits) = Component(_keys[group], component);
            if (isNull)
            {
                KeyStores.AppendNull(store);
            }
            else
            {
                KeyStores.AppendFixed(store, Value<TValue>(bits), kind);
            }
        }
    }

    /// <summary>
    /// Component <paramref name="component"/> of each group's word written to <paramref name="leaf"/> a chunk at
    /// a time, with its nulls when the column takes them: one value a call took a sixth of a group by of 1.8M
    /// pairs. A null's bits are zero, the default a column without nulls takes.
    /// </summary>
    private void AppendValues<TValue>(int component, bool nullable, FixedStore leaf, ReadOnlySpan<int> groups)
        where TValue : unmanaged
    {
        TWord[] keys = _keys;
        int shift = _layout.Shifts[component];
        int nullBit = nullable ? _layout.NullBits[component] : -1;
        if (nullBit < 0)
        {
            Span<TValue> values = MemoryMarshal.Cast<byte, TValue>(leaf.Reserve(groups.Length));
            for (int i = 0; i < groups.Length; i++)
            {
                values[i] = Value<TValue>(Bits(keys[groups[i]], shift));
            }

            return;
        }

        const int Chunk = 256;
        Span<TValue> chunk = stackalloc TValue[Chunk];
        Span<ulong> validity = stackalloc ulong[Chunk / 64];
        for (int from = 0; from < groups.Length; from += Chunk)
        {
            int count = Math.Min(Chunk, groups.Length - from);
            validity.Clear();
            for (int i = 0; i < count; i++)
            {
                TWord word = keys[groups[from + i]];
                chunk[i] = Value<TValue>(Bits(word, shift));
                validity[i >> 6] |= (~Bits(word, nullBit) & 1) << (i & 63);
            }

            leaf.Append<TValue>(chunk[..count], validity);
        }
    }

    /// <summary>A word's bits from <paramref name="shift"/> up, in the low ones of the result.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong Bits(TWord word, int shift)
    {
        (ulong low, ulong high) = KeyWords.Of(word);
        return (shift < 64 ? low : high) >> (shift & 63);
    }

    /// <summary>A value back from its bits, the low ones of <paramref name="bits"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static TValue Value<TValue>(ulong bits)
        where TValue : unmanaged => Unsafe.SizeOf<TValue>() switch
        {
            1 => Unsafe.BitCast<byte, TValue>((byte)bits),
            2 => Unsafe.BitCast<ushort, TValue>((ushort)bits),
            4 => Unsafe.BitCast<uint, TValue>((uint)bits),
            _ => Unsafe.BitCast<ulong, TValue>(bits),
        };

    /// <summary>Two words by component <paramref name="component"/>, ascending, a null last.</summary>
    private int Compare(TWord left, TWord right, int component)
    {
        (bool leftNull, ulong leftBits) = Component(left, component);
        (bool rightNull, ulong rightBits) = Component(right, component);
        if (leftNull || rightNull)
        {
            return leftNull == rightNull ? 0 : leftNull ? 1 : -1;
        }

        return _layout.Shapes[component].PType switch
        {
            PType.I8 => ((sbyte)leftBits).CompareTo((sbyte)rightBits),
            PType.I16 => ((short)leftBits).CompareTo((short)rightBits),
            PType.I32 => ((int)leftBits).CompareTo((int)rightBits),
            PType.I64 => ((long)leftBits).CompareTo((long)rightBits),
            PType.F16 => Value<Half>(leftBits).CompareTo(Value<Half>(rightBits)),
            PType.F32 => Value<float>(leftBits).CompareTo(Value<float>(rightBits)),
            PType.F64 => Value<double>(leftBits).CompareTo(Value<double>(rightBits)),
            _ => leftBits.CompareTo(rightBits),
        };
    }

    private static bool IsFloat(PType type) => type is PType.F16 or PType.F32 or PType.F64;
}
