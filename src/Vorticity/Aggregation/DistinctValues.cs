using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using Vorticity.Writing;

namespace Vorticity.Aggregating;

/// <summary>
/// The distinct values of a distinct count of one group -- a count over the whole scan, which has no
/// key -- each once, in the slots of an open-addressing table (PLAN-HIGH-CARDINALITY, profiling). A
/// value lies in its slot: one seen before is found at the first read, where a pair of
/// <see cref="DistinctPairs{TValue}"/> was found behind a tag and a slot's number, three reads, each a
/// miss to memory once the table outgrew the caches; and a value takes its slot alone, where a pair
/// took its group and its chain too: 10^7 values of 32 bits take 134 MB, where their pairs took 369.
/// </summary>
/// <remarks>
/// A slot whose bits are all zero is free, so the value of those bits is held apart, by a flag. A
/// float lies as one pattern of bits per value, every NaN and both zeros made one, so that values a
/// count takes as equal are equal bit for bit. The slots probe linearly from a home among the top bits
/// of the value's hash, under seeds no column can aim at, at most half full: a run of homes is a run of
/// slots, which a doubling copies in order and a part of a parallel merge walks in every lane's table.
/// </remarks>
internal sealed class DistinctValues<TValue>
    where TValue : unmanaged, IEquatable<TValue>
{
    // The values, a power of two of slots at most half full.
    private TValue[] _slots;

    // The values in the slots.
    private int _count;

    // Whether the value whose bits are all zero was seen, which takes no slot.
    private bool _zero;

    // The top bits of the hash every value shares, a part's of a parallel merge, which a home skips.
    private readonly int _skip;

    // The shelf the slots grow from, under the query's memory; null for values nothing counts.
    private ArrayShelf? _shelf;

    /// <summary>
    /// Values whose slots take <paramref name="capacity"/> before they double, the top
    /// <paramref name="skip"/> bits of every hash the same: a part's (<see cref="AddPart"/>), whose homes,
    /// from the bits below, spread over the slots, where the top bits would have put them all in one
    /// run, a part's share of the slots, and probing would have walked it to its end.
    /// </summary>
    internal DistinctValues(int capacity = 0, int skip = 0)
    {
        _slots = new TValue[Math.Max(32, (int)BitOperations.RoundUpToPowerOf2((uint)(2 * capacity)))];
        _skip = skip;
    }

    /// <summary>The shelf the slots grow from from now on (PLAN-HIGH-CARDINALITY, H2, decision 13).</summary>
    internal void Govern(ArrayShelf? shelf) => _shelf = shelf;

    /// <summary>The values held.</summary>
    internal long Count => _count + (_zero ? 1 : 0);

    /// <summary>Whether the value whose bits are all zero is held, which no slot holds.</summary>
    internal bool HoldsZero => _zero;

    /// <summary>The bytes of the slots, at their capacity.</summary>
    internal long Footprint => (long)_slots.Length * Unsafe.SizeOf<TValue>();

    /// <summary>Gives the slots back to their shelf: the values are let go, and read no more.</summary>
    internal void Release()
    {
        _shelf?.Give(_slots);
        _slots = [];
        _count = 0;
        _zero = false;
    }

    /// <summary>Adds <paramref name="value"/>; whether it was new.</summary>
    internal bool Add(TValue value)
    {
        value = Canonical(value);
        if (IsFree(value))
        {
            bool added = !_zero;
            _zero = true;
            return added;
        }

        return Insert(value, Hash(value));
    }

    /// <summary>Adds every value of <paramref name="other"/>, in the order its slots hold them.</summary>
    /// <returns>The values that were new.</returns>
    internal long MergeAll(DistinctValues<TValue> other)
    {
        long added = other._zero && !_zero ? 1 : 0;
        _zero |= other._zero;
        foreach (TValue value in other._slots)
        {
            if (!IsFree(value) && Insert(value, Hash(value)))
            {
                added++;
            }
        }

        return added;
    }

    /// <summary>
    /// Adds the values of <paramref name="lane"/> of part <paramref name="part"/> of
    /// <c>2^<paramref name="bits"/></c>, the top bits of their hash, <paramref name="bits"/> at least one:
    /// those of a run of homes, which lie from its first slot to the first free slot past its last. The
    /// lane's homes are the top bits (no <c>skip</c>); these values', the bits below
    /// <paramref name="bits"/> (a <c>skip</c> of <paramref name="bits"/>).
    /// </summary>
    internal void AddPart(DistinctValues<TValue> lane, int part, int bits)
    {
        TValue[] slots = lane._slots;
        int mask = slots.Length - 1;
        int laneBits = BitOperations.Log2((uint)slots.Length);
        int from = laneBits >= bits ? part << (laneBits - bits) : part >> (bits - laneBits);
        int to = laneBits >= bits ? (part + 1) << (laneBits - bits) : from + 1;
        int shift = 64 - bits;
        for (int at = from; at < to; at++)
        {
            if (!IsFree(slots[at]))
            {
                AddOfPart(slots[at], part, shift);
            }
        }

        // Past the run, the values of its homes that probing took further, the run of the last part
        // going on from the first slot: a table at most half full leaves a free slot before the run's.
        for (int at = to & mask; !IsFree(slots[at]); at = (at + 1) & mask)
        {
            AddOfPart(slots[at], part, shift);
        }
    }

    /// <summary>Adds <paramref name="value"/>, not the free one, when the top bits past <paramref name="shift"/> of its hash are <paramref name="part"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void AddOfPart(TValue value, int part, int shift)
    {
        ulong hash = Hash(value);
        if ((int)(hash >> shift) == part)
        {
            Insert(value, hash);
        }
    }

    /// <summary>Adds <paramref name="value"/>, not the free one, of hash <paramref name="hash"/>; whether it was new.</summary>
    private bool Insert(TValue value, ulong hash)
    {
        TValue[] slots = _slots;
        int mask = slots.Length - 1;
        int at = Home(hash, Shift(slots.Length));
        while (true)
        {
            TValue seen = slots[at];
            if (IsFree(seen))
            {
                break;
            }

            if (Same(seen, value))
            {
                return false;
            }

            at = (at + 1) & mask;
        }

        slots[at] = value;
        if (++_count * 2 > slots.Length)
        {
            Rehash(GroupKeys.Doubled(slots.Length));
        }

        return true;
    }

    /// <summary>
    /// The slots at <paramref name="length"/>, a power of two, every value placed again in the order the
    /// old slots hold them: by home but for the values of the last homes that probing took to the first
    /// slots, so that the new slots fill from their first to their last, as the old ones are read.
    /// </summary>
    private void Rehash(int length)
    {
        TValue[] old = _slots;
        TValue[] slots = _shelf is null ? new TValue[length] : _shelf.Take<TValue>(length, zeroed: true);
        int mask = length - 1;
        int shift = Shift(length);
        foreach (TValue value in old)
        {
            if (!IsFree(value))
            {
                int at = Home(Hash(value), shift);
                while (!IsFree(slots[at]))
                {
                    at = (at + 1) & mask;
                }

                slots[at] = value;
            }
        }

        _slots = slots;
        _shelf?.Give(old);
    }

    /// <summary>The part of <c>2^<paramref name="bits"/></c> that <paramref name="value"/>, not the free one, falls in.</summary>
    internal static int PartOf(TValue value, int bits) => (int)(Hash(Canonical(value)) >> (64 - bits));

    /// <summary>The most slots a value lies past its home: the longest walk of a probe.</summary>
    internal int Farthest()
    {
        int farthest = 0;
        int mask = _slots.Length - 1;
        int shift = Shift(_slots.Length);
        for (int at = 0; at < _slots.Length; at++)
        {
            if (!IsFree(_slots[at]))
            {
                farthest = Math.Max(farthest, (at - Home(Hash(_slots[at]), shift)) & mask);
            }
        }

        return farthest;
    }

    /// <summary>The home of a value of hash <paramref name="hash"/> under <paramref name="shift"/>: the top bits past those it shares with every value.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int Home(ulong hash, int shift) => (int)((hash << _skip) >> shift);

    /// <summary>The shift that leaves a hash's home among <paramref name="length"/> slots, a power of two: its top bits.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Shift(int length) => 64 - BitOperations.Log2((uint)length);

    /// <summary>
    /// A value's hash, under seeds drawn once a process: the top bits of a product the fold of its words
    /// went into, which hang on every bit of the value.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong Hash(TValue value)
    {
        (ulong low, ulong high) = KeyWords.Of(value);
        return KeyHash.Pair(low, high, 0);
    }

    /// <summary>A value as the bits it lies in: a float's one pattern a value, every NaN and both zeros made one.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static TValue Canonical(TValue value)
    {
        if (typeof(TValue) == typeof(double))
        {
            return Unsafe.BitCast<ulong, TValue>(KeyWords.Of(value).Low);
        }

        if (typeof(TValue) == typeof(float))
        {
            return Unsafe.BitCast<uint, TValue>((uint)KeyWords.Of(value).Low);
        }

        if (typeof(TValue) == typeof(Half))
        {
            return Unsafe.BitCast<ushort, TValue>((ushort)KeyWords.Of(value).Low);
        }

        return value;
    }

    /// <summary>Whether a slot holding <paramref name="value"/> is free: its bits all zero.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsFree(TValue value) => Same(value, default);

    /// <summary>Whether two values, as they lie, are equal bit for bit; a value of more than eight bytes is an integer, its equality its bits'.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool Same(TValue left, TValue right) => Unsafe.SizeOf<TValue>() switch
    {
        1 => Unsafe.BitCast<TValue, byte>(left) == Unsafe.BitCast<TValue, byte>(right),
        2 => Unsafe.BitCast<TValue, ushort>(left) == Unsafe.BitCast<TValue, ushort>(right),
        4 => Unsafe.BitCast<TValue, uint>(left) == Unsafe.BitCast<TValue, uint>(right),
        8 => Unsafe.BitCast<TValue, ulong>(left) == Unsafe.BitCast<TValue, ulong>(right),
        _ => left.Equals(right),
    };
}
