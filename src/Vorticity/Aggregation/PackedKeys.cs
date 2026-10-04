using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using Vorticity.Arrays;
using Vorticity.Writing;

namespace Vorticity.Aggregating;

/// <summary>What the statistics say of each column of a key: whether it is sorted, and what bounds an integer.</summary>
/// <param name="Sorted">Whether each column is sorted, in the key's order.</param>
/// <param name="Bounds">The values each integer column holds, when the statistics hold them exactly.</param>
internal readonly record struct KeyFacts(bool[] Sorted, KeyBounds?[] Bounds);

/// <summary>
/// A key of two to four columns as the numbers its parts have in indexes of their own, packed into
/// one word: each column is grouped by the index of a key of one column, with that index's paths by
/// run, by code and by value, its nulls in its null group, and the tuple of the parts' groups is the
/// key. No row is encoded into bytes; a row whose parts are the row before's is a comparison of
/// words.
/// </summary>
/// <remarks>
/// <para>
/// When every part hands its rows as ranges, constant, run-end or sorted, the key's ranges are the
/// parts' cut at the union of their boundaries, one lookup a range.
/// </para>
/// <para>
/// The parts' numbers are dense, from 0 in the order their values are met: while the product of
/// their counts, each rounded up to a power of two, stays under 2^16, a table indexed by the
/// numbers side by side holds the groups in front of the hash, and grows with the parts. Past that,
/// the hash alone.
/// </para>
/// <para>
/// Two numbers of 32 bits make a word of 64, multiplied by an odd number drawn once a process: the
/// product spreads the tuples of small numbers over the whole word, and is undone by the number's
/// inverse. The groups are found by open addressing, a slot from the top bits of the word, in a
/// table of group numbers at most half full; the words are kept once, by group. A word of 128 bits
/// is mixed by the same number before its top bits are read. Drawn once a process, the number cannot
/// be aimed at: no data is built to crowd the slots.
/// </para>
/// </remarks>
/// <typeparam name="TKey">The word: <see cref="ulong"/> for two parts, <see cref="UInt128"/> for three or four.</typeparam>
internal sealed class PackedKeys<TKey> : GroupKeys
    where TKey : unmanaged, IEquatable<TKey>
{
    /// <summary>The bits the table of groups is indexed by at most: 2^16 slots, a quarter of a megabyte.</summary>
    private const int TableBits = 16;

    /// <summary>An odd multiplier drawn once a process: its product spreads the bits of a tuple of small numbers.</summary>
    private static readonly ulong Odd = ((ulong)Random.Shared.NextInt64() << 1) | 1;

    /// <summary>The inverse of <see cref="Odd"/> modulo 2^64, which takes a product back to the pair.</summary>
    private static readonly ulong Inverse = Invert(Odd);

    private readonly ColumnShape[] _shapes;
    private readonly KeyFacts? _facts;
    private readonly GroupKeys[] _parts;
    private readonly int[][] _ids;
    private readonly GroupRanges[] _partRanges;
    private readonly bool[] _ranged;
    private TKey[] _keys = new TKey[16];

    // The groups by open addressing: a group number a slot, -1 for none, the slots a power of two
    // at most half full.
    private int[] _hashed = NewSlots(32);

    // The table of groups, -1 for a tuple not met yet, null once the parts outgrow it: each part's
    // number in its own bits, from bit _shifts[p], _bits[p] of them.
    private int[]? _table;
    private readonly int[] _bits;
    private readonly int[] _shifts;
    private int[] _slots = [];

    // Scratch kept from one call to the next: a part's numbers for an append, and for a keep the
    // numbers the kept groups hold and their new ones.
    private int[] _partIds = [];
    private int[] _renumbered = [];
    private int[] _held = [];

    internal PackedKeys(ColumnShape[] shapes, KeyFacts? facts)
    {
        _shapes = shapes;
        _facts = facts;
        _parts = new GroupKeys[shapes.Length];
        _ids = new int[shapes.Length][];
        _partRanges = new GroupRanges[shapes.Length];
        _ranged = new bool[shapes.Length];
        _bits = new int[shapes.Length];
        _shifts = new int[shapes.Length];
        for (int p = 0; p < shapes.Length; p++)
        {
            _parts[p] = AggregationPlan.Single(shapes[p], facts?.Sorted[p] ?? false, facts?.Bounds[p]);
            _ids[p] = [];
            _partRanges[p] = new GroupRanges();
            _bits[p] = 2;
        }

        Rebuild();
    }

    internal override bool Assign(CanonicalArena arena, ReadOnlySpan<int> nodes, int rows, ReadOnlySpan<ulong> selection, int[] rowGroups, GroupRanges ranges)
    {
        bool ranged = true;
        for (int p = 0; p < _parts.Length; p++)
        {
            Scratch.Grow(ref _ids[p], rows);
            _partRanges[p].Clear();
            _ranged[p] = _parts[p].Assign(arena, nodes.Slice(p, 1), rows, selection, _ids[p], _partRanges[p]);
            ranged &= _ranged[p];
        }

        // The table covers every number the parts have handed out, before a slot is reckoned.
        Cover();
        if (ranged)
        {
            Cut(selection, ranges);
            return true;
        }

        for (int p = 0; p < _parts.Length; p++)
        {
            if (_ranged[p])
            {
                GroupRanges partRanges = _partRanges[p];
                Span<int> ids = _ids[p].AsSpan(0, rows);
                for (int r = 0; r < partRanges.Count; r++)
                {
                    ids[partRanges.StartAt(r)..partRanges.EndAt(r)].Fill(partRanges.GroupAt(r));
                }
            }
        }

        if (_table is not null)
        {
            TableRows(rows, selection, rowGroups);
        }
        else
        {
            HashedRows(rows, selection, rowGroups);
        }

        return false;
    }

    /// <summary>The parts' ranges cut at the union of their boundaries: a group a piece every part covers and the selection reaches.</summary>
    private void Cut(ReadOnlySpan<ulong> selection, GroupRanges ranges)
    {
        if (_parts.Length == 2)
        {
            CutPair(selection, ranges);
            return;
        }

        Span<int> at = stackalloc int[_parts.Length];
        Span<int> ids = stackalloc int[_parts.Length];
        at.Clear();
        int lastGroup = -1;
        TKey last = default;
        while (true)
        {
            int start = 0;
            int end = int.MaxValue;
            for (int p = 0; p < _parts.Length; p++)
            {
                GroupRanges partRanges = _partRanges[p];
                if (at[p] >= partRanges.Count)
                {
                    return;
                }

                start = Math.Max(start, partRanges.StartAt(at[p]));
                end = Math.Min(end, partRanges.EndAt(at[p]));
            }

            if (start < end && RowMasks.Count(selection, start, end) > 0)
            {
                for (int p = 0; p < _parts.Length; p++)
                {
                    ids[p] = _partRanges[p].GroupAt(at[p]);
                }

                TKey key = Pack(ids);
                if (lastGroup < 0 || !key.Equals(last))
                {
                    last = key;
                    lastGroup = GroupOf(ids, key);
                }

                ranges.Add(start, end, lastGroup);
            }

            // Past the pieces that end first: every part whose range ends there steps to its next.
            for (int p = 0; p < _parts.Length; p++)
            {
                if (_partRanges[p].EndAt(at[p]) <= end)
                {
                    at[p]++;
                }
            }
        }
    }

    /// <summary><see cref="Cut"/> for two parts, their ranges read as spans.</summary>
    private void CutPair(ReadOnlySpan<ulong> selection, GroupRanges ranges)
    {
        ReadOnlySpan<int> firstStarts = _partRanges[0].Starts;
        ReadOnlySpan<int> firstEnds = _partRanges[0].Ends;
        ReadOnlySpan<int> firstGroups = _partRanges[0].Groups;
        ReadOnlySpan<int> secondStarts = _partRanges[1].Starts;
        ReadOnlySpan<int> secondEnds = _partRanges[1].Ends;
        ReadOnlySpan<int> secondGroups = _partRanges[1].Groups;
        Span<int> ids = stackalloc int[2];
        int a = 0;
        int b = 0;
        int lastFirst = -1;
        int lastSecond = -1;
        int lastGroup = -1;
        while (a < firstStarts.Length && b < secondStarts.Length)
        {
            int start = Math.Max(firstStarts[a], secondStarts[b]);
            int end = Math.Min(firstEnds[a], secondEnds[b]);
            if (start < end && (selection.IsEmpty || RowMasks.Count(selection, start, end) > 0))
            {
                if (firstGroups[a] != lastFirst || secondGroups[b] != lastSecond)
                {
                    lastFirst = firstGroups[a];
                    lastSecond = secondGroups[b];
                    ids[0] = lastFirst;
                    ids[1] = lastSecond;
                    lastGroup = GroupOf(ids, Pack(ids));
                }

                ranges.Add(start, end, lastGroup);
            }

            int firstEnd = firstEnds[a];
            int secondEnd = secondEnds[b];
            a += firstEnd <= end ? 1 : 0;
            b += secondEnd <= end ? 1 : 0;
        }
    }

    /// <summary>Each selected row's group by its tuple's word, a row whose word is the row before's taking its group.</summary>
    private void HashedRows(int rows, ReadOnlySpan<ulong> selection, int[] rowGroups)
    {
        TKey last = default;
        int lastGroup = -1;
        RowCursor cursor = new RowCursor(selection, 0, rows);
        if (typeof(TKey) == typeof(ulong) && _parts.Length == 2)
        {
            int[] first = _ids[0];
            int[] second = _ids[1];
            while (cursor.Next(out int row))
            {
                TKey key = Unsafe.BitCast<ulong, TKey>(((uint)first[row] | ((ulong)(uint)second[row] << 32)) * Odd);
                if (lastGroup < 0 || !key.Equals(last))
                {
                    last = key;
                    lastGroup = Lookup(key);
                }

                rowGroups[row] = lastGroup;
            }

            return;
        }

        Span<int> ids = stackalloc int[_parts.Length];
        while (cursor.Next(out int row))
        {
            for (int p = 0; p < _parts.Length; p++)
            {
                ids[p] = _ids[p][row];
            }

            TKey key = Pack(ids);
            if (lastGroup < 0 || !key.Equals(last))
            {
                last = key;
                lastGroup = Lookup(key);
            }

            rowGroups[row] = lastGroup;
        }
    }

    /// <summary>Each selected row's group read from the table, a tuple not met yet looked up in the hash once.</summary>
    private void TableRows(int rows, ReadOnlySpan<ulong> selection, int[] rowGroups)
    {
        // The slots a part at a time; a row the selection leaves out holds a number of no
        // consequence, never read below.
        Scratch.Grow(ref _slots, rows);
        Span<int> slots = _slots.AsSpan(0, rows);
        _ids[0].AsSpan(0, rows).CopyTo(slots);
        int mask = (1 << TableBits) - 1;
        for (int p = 1; p < _parts.Length; p++)
        {
            ReadOnlySpan<int> ids = _ids[p].AsSpan(0, rows);
            int shift = _shifts[p];
            for (int row = 0; row < rows; row++)
            {
                slots[row] = (slots[row] | (ids[row] << shift)) & mask;
            }
        }

        int[] table = _table!;
        Span<int> tuple = stackalloc int[_parts.Length];
        RowCursor cursor = new RowCursor(selection, 0, rows);
        while (cursor.Next(out int row))
        {
            int slot = slots[row];
            int group = table[slot];
            if (group < 0)
            {
                for (int p = 0; p < _parts.Length; p++)
                {
                    tuple[p] = _ids[p][row];
                }

                group = table[slot] = Lookup(Pack(tuple));
            }

            rowGroups[row] = group;
        }
    }

    /// <summary>The group of a tuple, through the table when there is one.</summary>
    private int GroupOf(ReadOnlySpan<int> ids, TKey key)
    {
        if (_table is not int[] table)
        {
            return Lookup(key);
        }

        int slot = Slot(ids);
        int group = table[slot];
        if (group < 0)
        {
            group = table[slot] = Lookup(key);
        }

        return group;
    }

    /// <summary>The table's slot of a tuple whose numbers the table covers.</summary>
    private int Slot(ReadOnlySpan<int> ids)
    {
        int slot = 0;
        for (int p = 0; p < ids.Length; p++)
        {
            slot |= ids[p] << _shifts[p];
        }

        return slot;
    }

    /// <summary>
    /// Widens the table to every number the parts have handed out, each part's bits rounded up to
    /// a power of two, or drops it for good once they need more than <see cref="TableBits"/>.
    /// </summary>
    private void Cover()
    {
        if (_table is null)
        {
            return;
        }

        bool grown = false;
        int total = 0;
        for (int p = 0; p < _parts.Length; p++)
        {
            int count = _parts[p].Count;
            while (count > 1 << _bits[p])
            {
                _bits[p]++;
                grown = true;
            }

            total += _bits[p];
        }

        if (total > TableBits)
        {
            _table = null;
            return;
        }

        if (grown)
        {
            Rebuild();
        }
    }

    /// <summary>The table at the parts' bits, filled with every group's slot.</summary>
    private void Rebuild()
    {
        int shift = 0;
        for (int p = 0; p < _parts.Length; p++)
        {
            _shifts[p] = shift;
            shift += _bits[p];
        }

        if (shift > TableBits)
        {
            _table = null;
            return;
        }

        int[] table = _table is { } held && held.Length == 1 << shift ? held : new int[1 << shift];
        table.AsSpan().Fill(-1);
        Span<int> ids = stackalloc int[_parts.Length];
        for (int g = 0; g < Count; g++)
        {
            for (int p = 0; p < _parts.Length; p++)
            {
                ids[p] = Id(_keys[g], p);
            }

            table[SlotAt(ids, shift)] = g;
        }

        _table = table;
    }

    private int SlotAt(ReadOnlySpan<int> ids, int bits)
    {
        int slot = Slot(ids);
        return slot & ((1 << bits) - 1);
    }

    /// <summary>The word of a tuple of the parts' numbers.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static TKey Pack(ReadOnlySpan<int> ids)
    {
        ulong low = (uint)ids[0] | ((ulong)(uint)ids[1] << 32);
        if (typeof(TKey) == typeof(ulong))
        {
            return Unsafe.BitCast<ulong, TKey>(low * Odd);
        }

        ulong high = (uint)ids[2] | (ids.Length > 3 ? (ulong)(uint)ids[3] << 32 : 0);
        return Unsafe.BitCast<UInt128, TKey>(new UInt128(high, low));
    }

    /// <summary>The number part <paramref name="part"/> has in a word.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Id(TKey key, int part)
    {
        if (typeof(TKey) == typeof(ulong))
        {
            return (int)(uint)((Unsafe.BitCast<TKey, ulong>(key) * Inverse) >> (32 * part));
        }

        return (int)(uint)(Unsafe.BitCast<TKey, UInt128>(key) >> (32 * part));
    }

    internal override GroupKeys Fresh() => new PackedKeys<TKey>(_shapes, _facts);

    internal override void Keep(ReadOnlySpan<int> groups)
    {
        // Each part keeps the numbers the kept groups hold, numbered again in their order, so that
        // no part holds more values than the open groups do; the words are packed again from them.
        Span<int> ids = stackalloc int[_parts.Length];
        for (int p = 0; p < _parts.Length; p++)
        {
            int count = _parts[p].Count;
            Scratch.Grow(ref _renumbered, count);
            Span<int> renumbered = _renumbered.AsSpan(0, count);
            renumbered.Fill(-1);
            foreach (int group in groups)
            {
                renumbered[Id(_keys[group], p)] = 0;
            }

            Scratch.Grow(ref _held, count);
            int held = 0;
            for (int id = 0; id < count; id++)
            {
                if (renumbered[id] == 0)
                {
                    renumbered[id] = held;
                    _held[held++] = id;
                }
            }

            _parts[p].Keep(_held.AsSpan(0, held));

            // The part's new numbers go into the words now: the next part reads its own from them.
            for (int i = 0; i < groups.Length; i++)
            {
                TKey key = _keys[groups[i]];
                for (int q = 0; q < _parts.Length; q++)
                {
                    ids[q] = q == p ? renumbered[Id(key, q)] : Id(key, q);
                }

                _keys[groups[i]] = Pack(ids);
            }
        }

        for (int i = 0; i < groups.Length; i++)
        {
            _keys[i] = _keys[groups[i]];
        }

        Count = groups.Length;
        Rehash(_hashed.Length);
        if (_table is not null)
        {
            Rebuild();
        }
    }

    internal override void MergeInto(GroupKeys target, Span<int> map)
    {
        // The parts' numbers here become their numbers in the target's parts, then each tuple its
        // group there. The target's table learns them as its rows meet them: a tuple new to it had
        // no slot, and its next batch covers its parts' new numbers first.
        PackedKeys<TKey> into = (PackedKeys<TKey>)target;
        int[][] partMaps = new int[_parts.Length][];
        for (int p = 0; p < _parts.Length; p++)
        {
            partMaps[p] = new int[_parts[p].Count];
            _parts[p].MergeInto(into._parts[p], partMaps[p]);
        }

        Span<int> ids = stackalloc int[_parts.Length];
        for (int g = 0; g < Count; g++)
        {
            for (int p = 0; p < _parts.Length; p++)
            {
                ids[p] = partMaps[p][Id(_keys[g], p)];
            }

            map[g] = into.Lookup(Pack(ids));
        }

        MergeSeen(target);
    }

    internal override int[] Order(bool sorted) => Identity(Count);

    internal override Func<int, T> Reader<T>(int component)
    {
        Func<int, T> part = _parts[component].Reader<T>(0);
        return group => part(Id(_keys[group], component));
    }

    internal override void Append(int component, ColumnStore store, ReadOnlySpan<int> groups)
    {
        Scratch.Grow(ref _partIds, groups.Length);
        for (int i = 0; i < groups.Length; i++)
        {
            _partIds[i] = Id(_keys[groups[i]], component);
        }

        _parts[component].Append(0, store, _partIds.AsSpan(0, groups.Length));
    }

    internal override bool Orders(int component) => _parts[component].Orders(0);

    internal override int CompareKeys(int a, int b, int component) =>
        _parts[component].CompareKeys(Id(_keys[a], component), Id(_keys[b], component), 0);

    /// <summary>The group of a word, added when it is new.</summary>
    private int Lookup(TKey key)
    {
        int[] hashed = _hashed;
        int mask = hashed.Length - 1;
        int slot = Home(key, hashed.Length);
        while (true)
        {
            int group = hashed[slot];
            if (group < 0)
            {
                break;
            }

            if (_keys[group].Equals(key))
            {
                return group;
            }

            slot = (slot + 1) & mask;
        }

        if (Count == _keys.Length)
        {
            Array.Resize(ref _keys, Count * 2);
        }

        int added = Count++;
        _keys[added] = key;
        hashed[slot] = added;
        if (Count * 2 > hashed.Length)
        {
            Rehash(hashed.Length * 2);
        }

        return added;
    }

    /// <summary>The slots at <paramref name="length"/>, every group placed again from its word.</summary>
    private void Rehash(int length)
    {
        int[] hashed = _hashed.Length == length ? _hashed : NewSlots(length);
        if (ReferenceEquals(hashed, _hashed))
        {
            hashed.AsSpan().Fill(-1);
        }

        int mask = length - 1;
        for (int g = 0; g < Count; g++)
        {
            int slot = Home(_keys[g], length);
            while (hashed[slot] >= 0)
            {
                slot = (slot + 1) & mask;
            }

            hashed[slot] = g;
        }

        _hashed = hashed;
    }

    /// <summary>A word's first slot among <paramref name="length"/>, a power of two: the top bits of the word, mixed first when it is wide.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Home(TKey key, int length)
    {
        int shift = 64 - BitOperations.Log2((uint)length);
        if (typeof(TKey) == typeof(ulong))
        {
            return (int)(Unsafe.BitCast<TKey, ulong>(key) >> shift);
        }

        UInt128 wide = Unsafe.BitCast<TKey, UInt128>(key);
        return (int)(((((ulong)wide * Odd) ^ (ulong)(wide >> 64)) * Odd) >> shift);
    }

    private static int[] NewSlots(int length)
    {
        int[] slots = new int[length];
        slots.AsSpan().Fill(-1);
        return slots;
    }

    /// <summary>The inverse of an odd number modulo 2^64, by Newton's iteration: each step doubles the bits it holds.</summary>
    private static ulong Invert(ulong odd)
    {
        ulong inverse = odd;
        for (int step = 0; step < 6; step++)
        {
            inverse *= 2 - (odd * inverse);
        }

        return inverse;
    }
}
