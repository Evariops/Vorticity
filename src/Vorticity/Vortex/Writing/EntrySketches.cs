using System;
using System.Numerics;
using Vorticity.Aggregating;
using Vorticity.Buffers;

namespace Vorticity.Writing;

/// <summary>
/// The quantile sketches of one measure of a column's counts, one an entry, on the grid of
/// <see cref="ColumnCountsBuilder.SketchBits"/>, held together: every entry's buckets in one table, a key an entry, a sign
/// and an index, and its count, and each entry's zeros. An entry's sketch encodes to the bytes
/// <see cref="QuantileSketch.Encode"/> writes of a sketch of the same values, the sketches' encoding being their buckets in
/// order whatever form a sketch keeps them in.
/// </summary>
/// <remarks>
/// A sketch an entry, an object of its own with its stores' arrays, came to 2.3 bytes a row of a consolidation of S1's
/// logs: hundreds of thousands of them an object. The table's blocks come from the pool at lengths of powers of two, which
/// the next object's counts take again, and go back when the counts are done.
/// </remarks>
internal sealed class EntrySketches(int bits) : IDisposable
{
    // A slot no key holds; and the bit of a key whose bucket is a positive value's, which sorts a sketch's negative store
    // before its positive one, each by its indices.
    private const long Free = -1;
    private const long Positive = 1L << 31;
    private const int FirstSlots = 1_024;

    private readonly int _shift = QuantileSketch.ShiftOf(bits);

    // The buckets, a key a slot or Free, and their counts, half the slots full at most; the bits of a slot's hash.
    private Block<long> _keys;
    private Block<long> _counts;
    private int _held;
    private int _hashBits;

    // Each entry's zeros, plus one for an entry a value reached, NaN or not, which has a sketch; 0 for one none reached.
    private Block<long> _zeros;

    // Once sealed, the buckets in order at the front of the blocks, and where each entry's start; the bytes of one encoded.
    private Block<int> _starts;
    private Block<byte> _encoded;

    /// <summary>Adds <paramref name="value"/> to entry <paramref name="entry"/>'s sketch, made at its first value: a NaN is counted in none of its buckets.</summary>
    internal void Add(int entry, double value)
    {
        if (entry >= _zeros.Length)
        {
            Blocks.Resize(ref _zeros, Blocks.Grown(entry + 1, _zeros.Length));
        }

        ref long zeros = ref _zeros[entry];
        zeros += zeros == 0 ? 1 : 0;
        if (double.IsNaN(value))
        {
            return;
        }

        if (value == 0)
        {
            zeros++;
            return;
        }

        long bucket = value > 0 ? Positive | (uint)QuantileSketch.Index(value, _shift) : (uint)QuantileSketch.Index(-value, _shift);
        Count(((long)entry << 32) | bucket, 1);
    }

    /// <summary>
    /// Moves every entry's sketch onto entry <paramref name="to"/>[its entry], merging the sketches of the entries that move
    /// onto the same one as <see cref="QuantileSketch.Merge"/> does: the entries renumbered, those of the values left out
    /// one. An entry moves onto one no later than itself.
    /// </summary>
    internal void Remap(ReadOnlySpan<int> to)
    {
        Block<long> keys = _keys;
        Block<long> counts = _counts;
        _keys = Blocks.Rent<long>(keys.Length);
        _keys.Fill(Free);
        _counts = Blocks.Rent<long>(counts.Length);
        _held = 0;
        for (int slot = 0; slot < keys.Length; slot++)
        {
            if (keys[slot] != Free)
            {
                Count(((long)to[(int)(keys[slot] >> 32)] << 32) | (keys[slot] & uint.MaxValue), counts[slot]);
            }
        }

        Blocks.Return(keys);
        Blocks.Return(counts);

        Block<long> zeros = _zeros;
        _zeros = Blocks.RentCleared<long>(zeros.Length);
        for (int entry = 0; entry < zeros.Length && entry < to.Length; entry++)
        {
            if (zeros[entry] > 0)
            {
                ref long into = ref _zeros[to[entry]];
                into = into == 0 ? zeros[entry] : into + zeros[entry] - 1;
            }
        }

        Blocks.Return(zeros);
    }

    /// <summary>Puts the buckets in order, entry by entry, for <see cref="Encoded"/>: the sketches take no value after.</summary>
    internal void Seal(int entries)
    {
        Span<long> keys = _keys;
        Span<long> counts = _counts;
        int held = 0;
        for (int slot = 0; slot < keys.Length; slot++)
        {
            if (keys[slot] != Free)
            {
                keys[held] = keys[slot];
                counts[held] = counts[slot];
                held++;
            }
        }

        keys[..held].Sort(counts[..held]);
        Blocks.Ensure(ref _starts, Blocks.Grown(entries + 1, 0));
        Span<int> starts = _starts;
        for (int entry = 0, at = 0; entry <= entries; entry++)
        {
            while (at < held && (int)(keys[at] >> 32) < entry)
            {
                at++;
            }

            starts[entry] = at;
        }

        _held = held;
    }

    /// <summary>The bytes of entry <paramref name="entry"/>'s sketch, once sealed, as <see cref="QuantileSketch.Encode"/> writes them; null for an entry no value reached.</summary>
    internal byte[]? Encoded(int entry)
    {
        if (entry >= _zeros.Length || _zeros[entry] == 0)
        {
            return null;
        }

        int from = _starts[entry];
        int to = _starts[entry + 1];
        int positive = from;
        while (positive < to && (_keys[positive] & Positive) == 0)
        {
            positive++;
        }

        int most = (4 + (2 * (to - from))) * QuantileSketch.MaxVarint;
        if (_encoded.Length < most)
        {
            Blocks.Ensure(ref _encoded, Blocks.Grown(most, _encoded.Length));
        }

        Span<byte> into = _encoded;
        int at = QuantileSketch.Varint(into, (ulong)bits);
        at += QuantileSketch.Varint(into[at..], (ulong)(_zeros[entry] - 1));
        at += Store(into[at..], from, positive);
        at += Store(into[at..], positive, to);
        return into[..at].ToArray();
    }

    /// <summary>Writes the buckets <c>[from, to)</c> as a store of the sketch: their count, then each one's index, the gap from the one before, and its count.</summary>
    private int Store(Span<byte> into, int from, int to)
    {
        int at = QuantileSketch.Varint(into, (ulong)(to - from));
        int previous = 0;
        for (int i = from; i < to; i++)
        {
            int index = (int)(_keys[i] & int.MaxValue);
            at += QuantileSketch.Varint(into[at..], (ulong)(index - previous));
            at += QuantileSketch.Varint(into[at..], (ulong)_counts[i]);
            previous = index;
        }

        return at;
    }

    /// <summary>Adds <paramref name="count"/> to the bucket of <paramref name="key"/>, a new one at 0.</summary>
    private void Count(long key, long count)
    {
        if (2 * (_held + 1) > _keys.Length)
        {
            Rehash(Math.Max(FirstSlots, 2 * _keys.Length));
        }

        Span<long> keys = _keys;
        int mask = keys.Length - 1;
        int slot = (int)(((ulong)key * 0x9E3779B97F4A7C15UL) >> (64 - _hashBits));
        while (true)
        {
            long held = keys[slot];
            if (held == key)
            {
                _counts[slot] += count;
                return;
            }

            if (held == Free)
            {
                keys[slot] = key;
                _counts[slot] = count;
                _held++;
                return;
            }

            slot = (slot + 1) & mask;
        }
    }

    /// <summary>The table grown to <paramref name="slots"/> slots, a power of two, its buckets placed again.</summary>
    private void Rehash(int slots)
    {
        Block<long> keys = _keys;
        Block<long> counts = _counts;
        _keys = Blocks.Rent<long>(slots);
        _keys.Fill(Free);
        _counts = Blocks.Rent<long>(slots);
        _hashBits = BitOperations.Log2((uint)slots);
        _held = 0;
        for (int slot = 0; slot < keys.Length; slot++)
        {
            if (keys[slot] != Free)
            {
                Count(keys[slot], counts[slot]);
            }
        }

        Blocks.Return(keys);
        Blocks.Return(counts);
    }

    /// <summary>Every block back to the pool, once: the sketches are not used again.</summary>
    public void Dispose()
    {
        Blocks.Return(_keys);
        Blocks.Return(_counts);
        Blocks.Return(_zeros);
        Blocks.Return(_starts);
        Blocks.Return(_encoded);
        _keys = default;
        _counts = default;
        _zeros = default;
        _starts = default;
        _encoded = default;
        _held = 0;
    }
}
