using System;
using System.Buffers;
using System.Collections.Generic;
using System.Numerics;

namespace Vorticity.Aggregating;

/// <summary>
/// A mergeable sketch of a distribution with a relative error: counts per bucket of a log-linear grid,
/// whose buckets split each power of two into <c>2^k</c> equal parts. A value's bucket is the top bits
/// of its double, its exponent and the first <c>k</c> bits of its mantissa, so it is the same integer on
/// every machine and never asks for a logarithm. Every count is an integer and a merge adds them bucket
/// by bucket, so a sketch merged in any order holds the same counts.
/// </summary>
/// <remarks>
/// <para>
/// A bucket <c>[L, U)</c> has <c>U ≤ L (1 + 2^-k)</c>, and a quantile answers it with <c>2LU / (L + U)</c>,
/// within <c>2^-k / (2 + 2^-k)</c> of any value of it, relatively: 0.77 % at <c>k = 6</c>, the bits a
/// requested error of 1 % takes. That is about 213 buckets a decade, where DDSketch's logarithmic grid at
/// 1 % takes 115, the price of a bucket found by a shift.
/// </para>
/// <para>
/// Positive and negative values keep stores of their own, by magnitude, and zeros a count; a NaN is
/// skipped, an infinity falls in the bucket past the largest double. A store is sparse, sorted indices
/// and their counts, while it holds few buckets, then a dense window over the indices it spans, and past
/// <see cref="MaxWindow"/> of them a map of the buckets that hold values, so that its bytes follow the
/// values it holds and not the range they span.
/// </para>
/// </remarks>
internal sealed class QuantileSketch : IGroupSketch<QuantileSketch>
{
    /// <summary>The buckets a store keeps sorted before it becomes a dense window.</summary>
    internal const int SparseLimit = 32;

    /// <summary>The most mantissa bits a grid takes: a relative error near 5 × 10⁻⁷.</summary>
    internal const int MaxBits = 20;

    /// <summary>
    /// The most buckets a store's dense window spans: 512 KiB of counts. A grid of many bits over a wide range, which a
    /// relative error of 5 × 10⁻⁷ asks, spans a million buckets a power of two, 8 MiB of counts a binade; past this, a
    /// store keeps a map of the buckets that hold values instead, its bytes those values' and not their range's.
    /// </summary>
    internal const int MaxWindow = 1 << 16;

    /// <summary>The bytes a bucket of a store's map takes: its entry, and its share of the map's buckets.</summary>
    private const int WideEntryBytes = 32;

    private readonly int _shift;
    private Store _positive;
    private Store _negative;

    /// <summary>An empty sketch whose buckets split each power of two into <c>2^bits</c>.</summary>
    internal QuantileSketch(int bits)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(bits, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(bits, MaxBits);
        Bits = bits;
        _shift = 52 - bits;
    }

    /// <summary>The mantissa bits of the grid.</summary>
    internal int Bits { get; }

    /// <summary>The values counted: every one but the NaN.</summary>
    internal long Count { get; private set; }

    /// <summary>The zeros among them.</summary>
    internal long Zeros { get; private set; }

    /// <summary>The bytes the sketch holds in its arrays, which grow as it meets buckets.</summary>
    public long Bytes => _positive.Bytes + _negative.Bytes;

    /// <summary>The fewest mantissa bits whose buckets answer within <paramref name="relativeError"/>: <c>2^-k / (2 + 2^-k)</c> at most.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The error is not in (0, 1).</exception>
    internal static int BitsFor(double relativeError)
    {
        if (!(relativeError > 0 && relativeError < 1))
        {
            throw new ArgumentOutOfRangeException(nameof(relativeError), relativeError, "A relative error lies strictly between 0 and 1.");
        }

        // 2^-k / (2 + 2^-k) = 1 / (2^(k+1) + 1).
        int bits = 1;
        while (bits < MaxBits && 1.0 / ((1L << (bits + 1)) + 1) > relativeError)
        {
            bits++;
        }

        return bits;
    }

    /// <summary>The relative error a grid of <paramref name="bits"/> guarantees.</summary>
    internal static double ErrorOf(int bits) => 1.0 / ((1L << (bits + 1)) + 1);

    /// <summary>Counts <paramref name="value"/> once, and returns the bytes the sketch grew by: zero but when an array grew.</summary>
    internal long Add(double value) => Add(value, 1);

    /// <summary>Counts <paramref name="value"/> <paramref name="count"/> times, and returns the bytes the sketch grew by.</summary>
    internal long Add(double value, long count)
    {
        if (double.IsNaN(value) || count == 0)
        {
            return 0;
        }

        Count += count;
        if (value > 0)
        {
            return _positive.Add(Index(value), count);
        }

        if (value < 0)
        {
            return _negative.Add(Index(-value), count);
        }

        Zeros += count;
        return 0;
    }

    /// <summary>
    /// Counts each of <paramref name="values"/> once, as <see cref="Add(double)"/> one at a time would, and returns the
    /// bytes the sketch grew by.
    /// </summary>
    /// <remarks>
    /// A positive value whose bucket the positive store's dense window holds, the run of a distribution's values once its
    /// buckets are known, is counted in a loop of its own, the window, its first index and the count held in locals:
    /// a value at a time, <see cref="Add(double)"/> wrote the sketch's count and read the store's fields back for every
    /// one, about eight cycles a value and half of a lane's over a percentile of 540 million durations. Any other value,
    /// a zero, a negative, a NaN, a bucket past the window or a store still sparse, goes through <see cref="Add(double)"/>,
    /// and the loop goes on with the window that left.
    /// </remarks>
    internal long AddAll<TValue>(ReadOnlySpan<TValue> values)
        where TValue : unmanaged, INumberBase<TValue>
    {
        long grown = 0;
        int i = 0;
        while (i < values.Length)
        {
            if (_positive.Dense(out long[] window, out int low))
            {
                int start = i;
                int shift = _shift;
                for (; i < values.Length; i++)
                {
                    double value = double.CreateTruncating(values[i]);
                    int at = (int)(BitConverter.DoubleToInt64Bits(value) >> shift) - low;
                    if (!(value > 0) || (uint)at >= (uint)window.Length)
                    {
                        break;
                    }

                    window[at]++;
                }

                Count += i - start;
                if (i == values.Length)
                {
                    break;
                }
            }

            grown += Add(double.CreateTruncating(values[i]));
            i++;
        }

        return grown;
    }

    /// <summary>Forgets every value, its arrays kept for the next ones: a sketch that counts an hour after another.</summary>
    internal void Clear()
    {
        Count = 0;
        Zeros = 0;
        _positive.Clear();
        _negative.Clear();
    }

    /// <summary>Adds <paramref name="other"/>'s counts, a sketch of the same grid.</summary>
    /// <exception cref="ArgumentException">The grids differ.</exception>
    public void Merge(QuantileSketch other)
    {
        if (other.Bits != Bits)
        {
            throw new ArgumentException($"A sketch of {other.Bits} mantissa bits merges into one of {Bits}: a grid merges with itself.", nameof(other));
        }

        Count += other.Count;
        Zeros += other.Zeros;
        other._positive.AddTo(ref _positive);
        other._negative.AddTo(ref _negative);
    }

    /// <summary>The buckets that hold values, in ascending order of value, and the zeros among them.</summary>
    internal IEnumerable<(double Lower, double Upper, long Count)> Buckets()
    {
        List<(int Index, long Count)> negative = _negative.Entries();
        for (int i = negative.Count - 1; i >= 0; i--)
        {
            (double lower, double upper) = BoundsOf(negative[i].Index);
            yield return (-upper, -lower, negative[i].Count);
        }

        if (Zeros > 0)
        {
            yield return (0, 0, Zeros);
        }

        foreach ((int index, long count) in _positive.Entries())
        {
            (double lower, double upper) = BoundsOf(index);
            yield return (lower, upper, count);
        }
    }

    /// <summary>
    /// The value at <paramref name="q"/> of the values counted, as their distribution answers it
    /// (<see cref="Distribution.Quantile"/>): the estimate of the first bucket, in ascending order of value, whose values
    /// reach past the rank <c>q (n - 1)</c>; walked in place, with no histogram made, null for no value.
    /// </summary>
    internal double? Quantile(double q)
    {
        if (Count == 0)
        {
            return null;
        }

        double rank = q * (Count - 1);
        long below = 0;
        if (_negative.Find(descending: true, rank, ref below) is int negative)
        {
            (double lower, double upper) = BoundsOf(negative);
            return Estimate(-upper, -lower);
        }

        below += Zeros;
        if (Zeros > 0 && below > rank)
        {
            return 0;
        }

        if (_positive.Find(descending: false, rank, ref below) is int positive)
        {
            (double low, double high) = BoundsOf(positive);
            return Estimate(low, high);
        }

        // The rank lies below the count, so a bucket reaches past it; should rounding say otherwise, the distribution's
        // own search answers.
        return new Distribution(this).Quantile(q);
    }

    /// <summary>The value a bucket answers with: <c>2LU / (L + U)</c>, as close relatively to either end; 0 for the zeros.</summary>
    internal static double Estimate(double lower, double upper)
    {
        if (lower == 0 && upper == 0)
        {
            return 0;
        }

        if (double.IsInfinity(lower) || double.IsInfinity(upper))
        {
            return double.IsInfinity(lower) ? lower : upper;
        }

        return 2 * lower * (upper / (lower + upper));
    }

    /// <summary>The buckets of each store, written for a spill or a state: zeros and count, then each store's entries.</summary>
    public void Write(Action<long> word)
    {
        word(Count);
        word(Zeros);
        foreach (Store store in (Store[])[_negative, _positive])
        {
            List<(int Index, long Count)> entries = store.Entries();
            word(entries.Count);
            foreach ((int index, long count) in entries)
            {
                word(index);
                word(count);
            }
        }
    }

    /// <summary>
    /// The sketch as a table of sketches holds it, in varints: its grid's bits, its zeros, then each store's buckets, the
    /// negative's then the positive's, as their count and each one's index, the gap from the one before, and its count.
    /// A bucket's gap takes a byte where its neighbours hold values, and a count of fewer than 128 another.
    /// </summary>
    public void Encode(IBufferWriter<byte> into)
    {
        Span<byte> head = into.GetSpan(3 * MaxVarint);
        int at = Varint(head, (ulong)Bits);
        at += Varint(head[at..], (ulong)Zeros);
        into.Advance(at);
        _negative.Encode(into);
        _positive.Encode(into);
    }

    /// <summary>
    /// Adds the counts of a sketch <see cref="Encode"/> wrote to this one's: its buckets mapped onto this grid, which is
    /// as fine or coarser, a bucket of <c>k</c> bits the bucket of <c>k - s</c> bits its index shifted by <c>s</c> names,
    /// so that the counts are those this grid gives the same values.
    /// </summary>
    /// <exception cref="VortexFormatException">The bytes do not hold a sketch, or one of a coarser grid.</exception>
    public void MergeEncoded(ReadOnlySpan<byte> bytes)
    {
        int at = 0;
        int bits = checked((int)ReadVarint(bytes, ref at));
        if (bits < Bits || bits > MaxBits)
        {
            throw new VortexFormatException($"A sketch of {bits} mantissa bits merges into one of {Bits}: a grid merges into itself or a coarser one.");
        }

        int shift = bits - Bits;
        long zeros = checked((long)ReadVarint(bytes, ref at));
        long count = zeros;
        for (int side = 0; side < 2; side++)
        {
            ulong entries = ReadVarint(bytes, ref at);
            if (entries > (ulong)bytes.Length)
            {
                throw new VortexFormatException($"A sketch's store says {entries} buckets in {bytes.Length} bytes.");
            }

            long index = 0;
            for (ulong e = 0; e < entries; e++)
            {
                index = checked(index + (long)ReadVarint(bytes, ref at));
                long held = checked((long)ReadVarint(bytes, ref at));
                count = checked(count + held);
                if (side == 0)
                {
                    _negative.Add(checked((int)(index >> shift)), held);
                }
                else
                {
                    _positive.Add(checked((int)(index >> shift)), held);
                }
            }
        }

        if (at != bytes.Length)
        {
            throw new VortexFormatException($"A sketch holds {bytes.Length - at} bytes past its buckets.");
        }

        Count += count;
        Zeros += zeros;
    }

    /// <summary>The most bytes a varint of 64 bits takes.</summary>
    private const int MaxVarint = 10;

    private static int Varint(Span<byte> into, ulong value)
    {
        int at = 0;
        while (value >= 0x80)
        {
            into[at++] = (byte)(value | 0x80);
            value >>= 7;
        }

        into[at++] = (byte)value;
        return at;
    }

    private static ulong ReadVarint(ReadOnlySpan<byte> bytes, ref int at)
    {
        ulong value = 0;
        for (int shift = 0; shift < 64; shift += 7)
        {
            if (at >= bytes.Length)
            {
                throw new VortexFormatException("A sketch ends inside a varint.");
            }

            byte b = bytes[at++];
            value |= (ulong)(b & 0x7F) << shift;
            if (b < 0x80)
            {
                return value;
            }
        }

        throw new VortexFormatException("A sketch holds a varint of more than 64 bits.");
    }

    /// <summary>A sketch read back from the words <see cref="Write"/> wrote, a spill's or a test's.</summary>
    internal static QuantileSketch Read<TWords>(int bits, ref TWords words)
        where TWords : IWordSource, allows ref struct
    {
        QuantileSketch sketch = new QuantileSketch(bits);
        long count = words.NextWord();
        long zeros = words.NextWord();
        for (int side = 0; side < 2; side++)
        {
            long entries = words.NextWord();
            for (long e = 0; e < entries; e++)
            {
                int index = checked((int)words.NextWord());
                sketch.AddBucket(negative: side == 0, index, words.NextWord());
            }
        }

        sketch.SetTotals(count, zeros);
        return sketch;
    }

    /// <summary>A sketch read back from the words <paramref name="word"/> hands out one at a time.</summary>
    internal static QuantileSketch Read(int bits, Func<long> word)
    {
        WordsOf words = new WordsOf(word);
        return Read(bits, ref words);
    }

    /// <summary>Adds a bucket's count as read back, its values already counted by <see cref="SetTotals"/>.</summary>
    internal void AddBucket(bool negative, int index, long count)
    {
        if (negative)
        {
            _negative.Add(index, count);
        }
        else
        {
            _positive.Add(index, count);
        }
    }

    /// <summary>Sets the values counted and the zeros among them, as read back with the buckets.</summary>
    internal void SetTotals(long count, long zeros)
    {
        Count = count;
        Zeros = zeros;
    }

    private int Index(double magnitude) => (int)(BitConverter.DoubleToInt64Bits(magnitude) >> _shift);

    /// <summary>A bucket's bounds; the infinity's bucket is the infinity alone, past which the bits name NaNs.</summary>
    private (double Lower, double Upper) BoundsOf(int index)
    {
        double lower = BitConverter.Int64BitsToDouble((long)index << _shift);
        return double.IsInfinity(lower) ? (lower, lower) : (lower, BitConverter.Int64BitsToDouble((long)(index + 1) << _shift));
    }

    /// <summary>
    /// The counts of one sign's buckets: sorted indices while few, a dense window past them, and past a window of
    /// <see cref="MaxWindow"/> buckets a map of the buckets that hold values.
    /// </summary>
    private struct Store
    {
        private int[] _keys;
        private long[] _counts;
        private int _sparse;

        // The dense window, null while sparse: counts of indices from _low on.
        private long[]? _window;
        private int _low;

        // The buckets of values too far apart for a window, by index: what a grid of many bits over a wide range holds.
        private Dictionary<int, long>? _wide;

        internal readonly long Bytes =>
            _wide is { } wide ? (long)wide.Count * WideEntryBytes
            : _window is { } window ? (long)window.Length * sizeof(long)
            : ((long)(_keys?.Length ?? 0) * sizeof(int)) + ((long)(_counts?.Length ?? 0) * sizeof(long));

        /// <summary>Whether the store is a dense window, and its counts and the index of its first, which a run of adds reads in place.</summary>
        internal readonly bool Dense(out long[] window, out int low)
        {
            window = _window!;
            low = _low;
            return window is not null;
        }

        /// <summary>Adds a bucket's count, and returns the bytes the store grew by: zero unless an array grew.</summary>
        internal long Add(int index, long count)
        {
            if (_wide is { } wide)
            {
                ref long held = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(wide, index, out bool existed);
                held += count;
                return existed ? 0 : WideEntryBytes;
            }

            if (_window is { } window)
            {
                int at = index - _low;
                if ((uint)at < (uint)window.Length)
                {
                    window[at] += count;
                    return 0;
                }

                long before = Bytes;
                Widen(index);
                Add(index, count);
                return Bytes - before;
            }

            long grown = 0;
            if (_keys is null)
            {
                _keys = new int[4];
                _counts = new long[4];
                grown = Bytes;
            }

            int found = Array.BinarySearch(_keys, 0, _sparse, index);
            if (found >= 0)
            {
                _counts[found] += count;
                return grown;
            }

            int insert = ~found;
            if (_sparse == _keys.Length)
            {
                long before = Bytes;
                if (_sparse >= SparseLimit)
                {
                    Densify();
                    Add(index, count);
                    return grown + Bytes - before;
                }

                Array.Resize(ref _keys, _keys.Length * 2);
                Array.Resize(ref _counts, _counts.Length * 2);
                grown += Bytes - before;
            }

            Array.Copy(_keys, insert, _keys, insert + 1, _sparse - insert);
            Array.Copy(_counts, insert, _counts, insert + 1, _sparse - insert);
            _keys[insert] = index;
            _counts[insert] = count;
            _sparse++;
            return grown;
        }

        /// <summary>Forgets every count, a dense window or a map kept as it is, emptied.</summary>
        internal void Clear()
        {
            if (_window is { } window)
            {
                Array.Clear(window);
            }

            _wide?.Clear();
            _sparse = 0;
        }

        /// <summary>Adds this store's counts into <paramref name="into"/>.</summary>
        internal readonly void AddTo(ref Store into)
        {
            if (_wide is { } wide)
            {
                foreach ((int index, long count) in wide)
                {
                    into.Add(index, count);
                }

                return;
            }

            if (_window is { } window)
            {
                for (int i = 0; i < window.Length; i++)
                {
                    if (window[i] != 0)
                    {
                        into.Add(_low + i, window[i]);
                    }
                }

                return;
            }

            for (int i = 0; i < _sparse; i++)
            {
                into.Add(_keys[i], _counts[i]);
            }
        }

        /// <summary>The buckets that hold values, as <see cref="QuantileSketch.Encode"/> writes them: their count, then each one's gap and count.</summary>
        internal readonly void Encode(IBufferWriter<byte> into)
        {
            // A map's buckets, put in order first.
            if (_wide is not null)
            {
                List<(int Index, long Count)> sorted = Entries();
                Span<byte> count = into.GetSpan(MaxVarint);
                into.Advance(Varint(count, (ulong)sorted.Count));
                int last = 0;
                foreach ((int index, long held) in sorted)
                {
                    Span<byte> entry = into.GetSpan(2 * MaxVarint);
                    int at = Varint(entry, (ulong)(index - last));
                    at += Varint(entry[at..], (ulong)held);
                    into.Advance(at);
                    last = index;
                }

                return;
            }

            int entries = 0;
            if (_window is { } window)
            {
                foreach (long count in window)
                {
                    entries += count != 0 ? 1 : 0;
                }
            }
            else
            {
                entries = _sparse;
            }

            Span<byte> head = into.GetSpan(MaxVarint);
            into.Advance(Varint(head, (ulong)entries));
            int previous = 0;
            for (int i = 0; i < (_window?.Length ?? _sparse); i++)
            {
                (int index, long count) = _window is { } dense ? (_low + i, dense[i]) : (_keys[i], _counts[i]);
                if (count == 0)
                {
                    continue;
                }

                Span<byte> entry = into.GetSpan(2 * MaxVarint);
                int at = Varint(entry, (ulong)(index - previous));
                at += Varint(entry[at..], (ulong)count);
                into.Advance(at);
                previous = index;
            }
        }

        /// <summary>The indices that hold values and their counts, ascending.</summary>
        /// <summary>
        /// The index of the first bucket, in ascending order of index or descending, at which the counts added to
        /// <paramref name="below"/> pass <paramref name="rank"/>; null when none does, every count added.
        /// </summary>
        internal readonly int? Find(bool descending, double rank, ref long below)
        {
            if (_wide is not null)
            {
                List<(int Index, long Count)> sorted = Entries();
                for (int i = 0; i < sorted.Count; i++)
                {
                    (int index, long count) = sorted[descending ? sorted.Count - 1 - i : i];
                    below += count;
                    if (below > rank)
                    {
                        return index;
                    }
                }

                return null;
            }

            if (_window is { } window)
            {
                for (int i = 0; i < window.Length; i++)
                {
                    int at = descending ? window.Length - 1 - i : i;
                    below += window[at];
                    if (window[at] != 0 && below > rank)
                    {
                        return _low + at;
                    }
                }

                return null;
            }

            for (int i = 0; i < _sparse; i++)
            {
                int at = descending ? _sparse - 1 - i : i;
                below += _counts[at];
                if (below > rank)
                {
                    return _keys[at];
                }
            }

            return null;
        }

        internal readonly List<(int Index, long Count)> Entries()
        {
            List<(int Index, long Count)> entries = [];
            if (_wide is { } wide)
            {
                foreach ((int index, long count) in wide)
                {
                    if (count != 0)
                    {
                        entries.Add((index, count));
                    }
                }

                entries.Sort(static (left, right) => left.Index.CompareTo(right.Index));
                return entries;
            }

            if (_window is { } window)
            {
                for (int i = 0; i < window.Length; i++)
                {
                    if (window[i] != 0)
                    {
                        entries.Add((_low + i, window[i]));
                    }
                }

                return entries;
            }

            for (int i = 0; i < _sparse; i++)
            {
                entries.Add((_keys[i], _counts[i]));
            }

            return entries;
        }

        /// <summary>
        /// Leaves the sorted indices for a dense window over the ones they span, or for a map of them when that window
        /// would pass <see cref="MaxWindow"/> buckets.
        /// </summary>
        private void Densify()
        {
            int low = _keys[0];
            long span = (long)_keys[_sparse - 1] - low + 1;
            if (span > MaxWindow)
            {
                Dictionary<int, long> wide = new Dictionary<int, long>(2 * _sparse);
                for (int i = 0; i < _sparse; i++)
                {
                    wide[_keys[i]] = _counts[i];
                }

                _wide = wide;
            }
            else
            {
                long[] window = new long[Math.Max((int)span, SparseLimit * 2)];
                for (int i = 0; i < _sparse; i++)
                {
                    window[_keys[i] - low] = _counts[i];
                }

                _window = window;
                _low = low;
            }

            _keys = null!;
            _counts = null!;
            _sparse = 0;
        }

        /// <summary>
        /// Grows the window to hold <paramref name="index"/>, by half its length again at least each way it grows; or,
        /// when it would pass <see cref="MaxWindow"/> buckets, leaves it for a map of the buckets it holds.
        /// </summary>
        private void Widen(int index)
        {
            long[] window = _window!;
            int low = Math.Min(_low, index);
            int high = Math.Max(_low + window.Length - 1, index);
            int slack = window.Length / 2;
            int newLow = index < _low ? Math.Max(0, low - slack) : _low;
            long length = (long)high - newLow + 1 + (index > _low ? slack : 0);
            if (length > MaxWindow)
            {
                Dictionary<int, long> wide = new Dictionary<int, long>();
                for (int i = 0; i < window.Length; i++)
                {
                    if (window[i] != 0)
                    {
                        wide[_low + i] = window[i];
                    }
                }

                _wide = wide;
                _window = null;
                return;
            }

            long[] grown = new long[length];
            Array.Copy(window, 0, grown, _low - newLow, window.Length);
            _window = grown;
            _low = newLow;
        }
    }
}
