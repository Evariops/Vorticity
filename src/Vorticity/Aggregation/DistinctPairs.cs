using System;
using System.Runtime.CompilerServices;

namespace Vorticity.Aggregating;

/// <summary>
/// The (group, value) pairs of a distinct count of fixed-width values, each once (PLAN-HIGH-CARDINALITY,
/// H9): the pairs in the order they came, found by an open-addressing table of their numbers, each
/// chained to the pair its group met before. A group's pairs are read without reading another's: a
/// merge of some groups, a part of a parallel merge, reads theirs alone, where a set of pairs read
/// every pair of every group for each part; keeping a few groups, as a streaming group by does, reads
/// their pairs alone and holds no more than them.
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

    // A pair's number plus one, 0 for none: a power of two of them, at most half full.
    private int[] _slots = new int[32];

    // Each group's last pair, its number plus one, 0 for a group with none.
    private int[] _first = [];

    private int _count;

    // The shelf its arrays grow from, under the query's memory; null for pairs nothing counts.
    private ArrayShelf? _shelf;

    /// <summary>The shelf the pairs' arrays grow from from now on (PLAN-HIGH-CARDINALITY, H2, decision 13).</summary>
    internal void Govern(ArrayShelf? shelf) => _shelf = shelf;

    /// <summary>Gives the pairs' arrays back to their shelf: the pairs are let go, and read no more.</summary>
    internal void Release()
    {
        _shelf?.Give(_pairs);
        _shelf?.Give(_slots);
        _shelf?.Give(_first);
        _pairs = [];
        _slots = [];
        _first = [];
        _count = 0;
    }

    /// <summary>The pairs held.</summary>
    internal int Count => _count;

    /// <summary>The bytes of the pairs, the slots and the groups' chains, at their capacity.</summary>
    internal long Footprint => ((long)_pairs.Length * Unsafe.SizeOf<Pair>()) + ((long)(_slots.Length + _first.Length) * sizeof(int));

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
        int[] slots = _slots;
        int mask = slots.Length - 1;
        int at = (int)DistinctEntry<TValue>.Hash(group, value) & mask;
        Pair[] pairs = _pairs;
        while (true)
        {
            int number = slots[at];
            if (number == 0)
            {
                break;
            }

            ref Pair pair = ref pairs[number - 1];
            if (pair.Group == group && pair.Value.Equals(value))
            {
                return false;
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
        slots[at] = _count;
        if (_count * 2 > slots.Length)
        {
            Rehash(GroupKeys.Doubled(slots.Length));
        }

        return true;
    }

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

        int[] slots = _slots;
        _pairs = NewArray<Pair>(Math.Max(16, kept));
        _slots = NewArray<int>((int)Math.Max(32, System.Numerics.BitOperations.RoundUpToPowerOf2((uint)(2 * kept + 1))));
        _first = NewArray<int>(Math.Max(16, groups.Length));
        _count = 0;
        for (int i = 0; i < groups.Length; i++)
        {
            for (int number = first[groups[i]]; number != 0; number = pairs[number - 1].Next)
            {
                Add(i, pairs[number - 1].Value);
            }
        }

        _shelf?.Give(pairs);
        _shelf?.Give(slots);
        _shelf?.Give(first);
    }

    /// <summary>A zeroed array, from the shelf when the pairs have one.</summary>
    private T[] NewArray<T>(int length) => _shelf is null ? new T[length] : _shelf.Take<T>(length, zeroed: true);

    /// <summary>The slots at <paramref name="length"/>, a power of two, every pair placed again.</summary>
    private void Rehash(int length)
    {
        int[] old = _slots;
        int[] slots = NewArray<int>(length);
        int mask = length - 1;
        Pair[] pairs = _pairs;
        for (int number = 0; number < _count; number++)
        {
            int at = (int)DistinctEntry<TValue>.Hash(pairs[number].Group, pairs[number].Value) & mask;
            while (slots[at] != 0)
            {
                at = (at + 1) & mask;
            }

            slots[at] = number + 1;
        }

        _slots = slots;
        _shelf?.Give(old);
    }

    /// <summary>A value, the group that saw it, and the number plus one of that group's pair before it.</summary>
    private struct Pair
    {
        public TValue Value;
        public int Group;
        public int Next;
    }
}
