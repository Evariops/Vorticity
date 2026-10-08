using System;
using System.Runtime.CompilerServices;

namespace Vorticity.Aggregating;

/// <summary>
/// The (group, value) pairs of a distinct count of fixed-width values, each once: the pairs in the order
/// they came, found by an open-addressing table of their numbers, each
/// chained to the pair its group met before. A group's pairs are read without reading another's: a
/// merge of some groups, a part of a parallel merge, reads theirs alone, where a set of pairs read
/// every pair of every group for each part; keeping a few groups, as a streaming group by does, reads
/// their pairs alone and holds no other, in arrays of the size the most pairs held at once took.
/// </summary>
/// <remarks>
/// A pair takes its value, its group and the number of its group's pair before it, and a slot of
/// four bytes at most half full: 24 bytes for a <see cref="long"/>, where a set's entry and bucket
/// took 28. The slots probe linearly, under <see cref="DistinctEntry{TValue}.Hash"/>, whose seeds no
/// column can aim at.
/// </remarks>
internal sealed class DistinctPairs<TValue>
    where TValue : unmanaged, IEquatable<TValue>
{
    private Pair[] _pairs = new Pair[16];

    // A pair's number plus one, read only where its slot's tag agrees: a power of two of them, at most half full.
    private int[] _slots = new int[32];

    // Each slot's tag, 0 for none: its top bit set over the top seven bits of the pair's hash, read
    // first, so that a new pair finds its free slot without reading the pairs its chain passes.
    private byte[] _tags = new byte[32];

    // Each group's last pair, its number plus one, 0 for a group with none.
    private int[] _first = [];

    private int _count;

    // The arrays the last Keep moved the pairs out of, which the next moves them into (Keep).
    private Pair[] _sparePairs = [];
    private int[] _spareSlots = [];
    private byte[] _spareTags = [];
    private int[] _spareFirst = [];

    // The shelf its arrays grow from, under the query's memory; null for pairs nothing counts.
    private ArrayShelf? _shelf;

    /// <summary>The shelf the pairs' arrays grow from from now on.</summary>
    internal void Govern(ArrayShelf? shelf) => _shelf = shelf;

    /// <summary>Gives the pairs' arrays back to their shelf: the pairs are let go, and read no more.</summary>
    internal void Release()
    {
        Give(_pairs, _slots, _tags, _first);
        Give(_sparePairs, _spareSlots, _spareTags, _spareFirst);
        _pairs = [];
        _slots = [];
        _tags = [];
        _first = [];
        _sparePairs = [];
        _spareSlots = [];
        _spareTags = [];
        _spareFirst = [];
        _count = 0;
    }

    /// <summary>The pairs held.</summary>
    internal int Count => _count;

    /// <summary>The bytes of the pairs, the slots and the groups' chains, at their capacity, and of the arrays kept for the next <see cref="Keep"/>.</summary>
    internal long Footprint =>
        ((long)(_pairs.Length + _sparePairs.Length) * Unsafe.SizeOf<Pair>())
        + ((long)(_slots.Length + _first.Length + _spareSlots.Length + _spareFirst.Length) * sizeof(int))
        + _tags.Length + _spareTags.Length;

    /// <summary>Makes room for the chains of groups up to <paramref name="groups"/>.</summary>
    internal void EnsureGroups(int groups)
    {
        if (groups > _first.Length)
        {
            ArrayShelf.Resize(_shelf, ref _first, Scratch.Capacity(groups, _first.Length));
        }
    }

    /// <summary>Adds the pair (<paramref name="group"/>, <paramref name="value"/>); whether it was new.</summary>
    internal bool Add(int group, TValue value)
    {
        // The tags first: a new pair, every row of a count of
        // distinct values that are distinct, reads no pair of its chain, each a miss to memory.
        byte[] tags = _tags;
        int mask = tags.Length - 1;
        int shift = Shift(tags.Length);
        ulong spread = Spread(group, value);
        int at = (int)(spread >> shift);
        byte tag = Tag(spread, shift);
        Pair[] pairs = _pairs;
        while (true)
        {
            byte seen = tags[at];
            if (seen == 0)
            {
                break;
            }

            if (seen == tag)
            {
                ref Pair pair = ref pairs[_slots[at] - 1];
                if (pair.Group == group && pair.Value.Equals(value))
                {
                    return false;
                }
            }

            at = (at + 1) & mask;
        }

        if (_count == pairs.Length)
        {
            ArrayShelf.Resize(_shelf, ref _pairs, GroupKeys.Doubled(_count));
            pairs = _pairs;
        }

        pairs[_count] = new Pair { Value = value, Group = group, Next = _first[group] };
        _first[group] = ++_count;
        tags[at] = tag;
        _slots[at] = _count;
        if (_count * 2 > tags.Length)
        {
            Rehash(GroupKeys.Doubled(tags.Length));
        }

        return true;
    }

    /// <summary>
    /// An odd number the pair's hash is multiplied by, its home and tag the product's top bits. The hash
    /// adds the group to the low bits of a value's: one value's pairs over a thousand groups, their low
    /// bits in a row, filled runs of slots that linear probing walked to their end, 34 s for a count of
    /// 8.6M distinct values by a thousand keys, where a million keys took 1.6. The product spreads them
    /// over the table.
    /// </summary>
    private const ulong Spreading = 0x9E37_79B9_7F4A_7C15UL;

    /// <summary>The shift that leaves a product's home among <paramref name="length"/> slots, a power of two.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Shift(int length) => 64 - System.Numerics.BitOperations.Log2((uint)length);

    /// <summary>The slot the pair (<paramref name="group"/>, <paramref name="value"/>) starts from among <paramref name="length"/>, a power of two.</summary>
    internal static int HomeOf(int group, TValue value, int length) => (int)(Spread(group, value) >> Shift(length));

    /// <summary>The pair's hash times <see cref="Spreading"/>: its home is the top bits, its tag the seven below.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong Spread(int group, TValue value) => DistinctEntry<TValue>.Hash(group, value) * Spreading;

    /// <summary>A slot's tag: its top bit, so that none is zero, over the seven bits of the product below the home's.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte Tag(ulong spread, int shift) => (byte)(0x80 | (spread >> (shift - 7)));

    /// <summary>Adds every pair of <paramref name="other"/>, its group <c>g</c> as <c>map[g]</c>, in the order they lie; each new one counted at its group.</summary>
    internal void MergeAll(DistinctPairs<TValue> other, ReadOnlySpan<int> map, Span<long> counts)
    {
        Pair[] pairs = other._pairs;
        for (int number = 0; number < other._count; number++)
        {
            int group = map[pairs[number].Group];
            if (Add(group, pairs[number].Value))
            {
                counts[group]++;
            }
        }
    }

    /// <summary>Adds the pairs of group <paramref name="from"/> of <paramref name="other"/> as group <paramref name="into"/>, its chain alone read.</summary>
    /// <returns>The pairs that were new.</returns>
    internal int MergeGroup(DistinctPairs<TValue> other, int from, int into)
    {
        if (from >= other._first.Length)
        {
            return 0;
        }

        int added = 0;
        Pair[] pairs = other._pairs;
        for (int number = other._first[from]; number != 0; number = pairs[number - 1].Next)
        {
            added += Add(into, pairs[number - 1].Value) ? 1 : 0;
        }

        return added;
    }

    /// <summary>Adds the values of group <paramref name="from"/> to <paramref name="set"/>, its chain alone read; the values that were new.</summary>
    internal int AddTo(DistinctValues<TValue> set, int from)
    {
        if (from >= _first.Length)
        {
            return 0;
        }

        int added = 0;
        Pair[] pairs = _pairs;
        for (int number = _first[from]; number != 0; number = pairs[number - 1].Next)
        {
            added += set.Add(pairs[number - 1].Value) ? 1 : 0;
        }

        return added;
    }

    /// <summary>The group of pair <paramref name="number"/>.</summary>
    internal int GroupAt(int number) => _pairs[number].Group;

    /// <summary>The value of pair <paramref name="number"/>.</summary>
    internal TValue ValueAt(int number) => _pairs[number].Value;

    /// <summary>
    /// The pairs placed by part, the part of a pair the top bits past <paramref name="shift"/> of its
    /// hash under its group's target <c>map[g]</c>: the same pair in every lane falls in one part.
    /// </summary>
    /// <returns>The pairs' numbers, part after part, and where each part's start, then the end.</returns>
    internal (int[] Placed, int[] Starts) Cut(ReadOnlySpan<int> map, int shift, int parts)
    {
        int[] partOf = GC.AllocateUninitializedArray<int>(_count);
        int[] starts = new int[parts + 1];
        Pair[] pairs = _pairs;
        for (int number = 0; number < _count; number++)
        {
            int part = (int)(DistinctEntry<TValue>.Hash(map[pairs[number].Group], pairs[number].Value) >> shift);
            partOf[number] = part;
            starts[part + 1]++;
        }

        for (int part = 0; part < parts; part++)
        {
            starts[part + 1] += starts[part];
        }

        int[] next = starts[..^1];
        int[] placed = GC.AllocateUninitializedArray<int>(_count);
        for (int number = 0; number < _count; number++)
        {
            placed[next[partOf[number]]++] = number;
        }

        return (placed, starts);
    }

    /// <summary>
    /// Keeps the pairs of <paramref name="groups"/> alone, group <c>groups[i]</c> becoming group
    /// <c>i</c>: their chains read, and nothing else, into arrays their size.
    /// </summary>
    internal void Keep(ReadOnlySpan<int> groups)
    {
        Pair[] pairs = _pairs;
        int[] first = _first;
        int kept = 0;
        foreach (int group in groups)
        {
            for (int number = first[group]; number != 0; number = pairs[number - 1].Next)
            {
                kept++;
            }
        }

        // The arrays keep the size they reached: a streaming group by keeps its open groups a batch at a
        // time, and fitted to them, the slots and the pairs grew back by doubling every batch, a third of
        // the cycles of the distinct users of each of 365 days. Their
        // bytes stay those of the most pairs held at once. The pairs move into the arrays the Keep before
        // moved them out of, and leave theirs to the next: arrays new at every batch cost those 365 days
        // 850 MiB of the large object heap and 77 full collections a query at one lane, and a fifth of
        // its time (232 ms against 185, measured on 2026-10-08).
        int[] slots = _slots;
        byte[] tags = _tags;
        int length = (int)Math.Max(Math.Max(32, System.Numerics.BitOperations.RoundUpToPowerOf2((uint)(2 * kept + 1))), slots.Length);
        _pairs = Spare(ref _sparePairs, Math.Max(16, Math.Max(kept, pairs.Length)), exact: false, zeroed: false);
        _slots = Spare(ref _spareSlots, length, exact: true, zeroed: false);
        _tags = Spare(ref _spareTags, length, exact: true, zeroed: true);
        _first = Spare(ref _spareFirst, Math.Max(16, groups.Length), exact: false, zeroed: true);
        _count = 0;
        for (int i = 0; i < groups.Length; i++)
        {
            for (int number = first[groups[i]]; number != 0; number = pairs[number - 1].Next)
            {
                Add(i, pairs[number - 1].Value);
            }
        }

        _sparePairs = pairs;
        _spareSlots = slots;
        _spareTags = tags;
        _spareFirst = first;
    }

    /// <summary>
    /// The array kept for <paramref name="length"/> elements, that length exactly when <paramref name="exact"/>
    /// or at least, zeroed when <paramref name="zeroed"/>; a new one, zeroed, when it does not fit, the kept
    /// one given back. A slot is read only where its tag is set: the slots are not zeroed.
    /// </summary>
    private T[] Spare<T>(ref T[] spare, int length, bool exact, bool zeroed)
    {
        T[] array = spare;
        spare = [];
        if (exact ? array.Length == length : array.Length >= length)
        {
            if (zeroed)
            {
                Array.Clear(array);
            }

            return array;
        }

        if (array.Length > 0)
        {
            _shelf?.Give(array);
        }

        return NewArray<T>(length);
    }

    /// <summary>Gives a set of the pairs' arrays back to their shelf.</summary>
    private void Give(Pair[] pairs, int[] slots, byte[] tags, int[] first)
    {
        _shelf?.Give(pairs);
        _shelf?.Give(slots);
        _shelf?.Give(tags);
        _shelf?.Give(first);
    }

    /// <summary>A zeroed array, from the shelf when the pairs have one.</summary>
    private T[] NewArray<T>(int length) => _shelf is null ? new T[length] : _shelf.Take<T>(length, zeroed: true);

    /// <summary>The slots at <paramref name="length"/>, a power of two, every pair placed again.</summary>
    private void Rehash(int length)
    {
        int[] old = _slots;
        byte[] oldTags = _tags;
        byte[] tags = NewArray<byte>(length);
        int[] slots = _shelf is null ? GC.AllocateUninitializedArray<int>(length) : _shelf.Take<int>(length, zeroed: false);
        int mask = length - 1;
        int shift = Shift(length);
        Pair[] pairs = _pairs;
        for (int number = 0; number < _count; number++)
        {
            ulong spread = Spread(pairs[number].Group, pairs[number].Value);
            int at = (int)(spread >> shift);
            while (tags[at] != 0)
            {
                at = (at + 1) & mask;
            }

            tags[at] = Tag(spread, shift);
            slots[at] = number + 1;
        }

        _slots = slots;
        _tags = tags;
        _shelf?.Give(old);
        _shelf?.Give(oldTags);
    }

    /// <summary>A value, the group that saw it, and the number plus one of that group's pair before it.</summary>
    private struct Pair
    {
        public TValue Value;
        public int Group;
        public int Next;
    }
}
