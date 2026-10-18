using System;
using System.Collections.Generic;

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
/// and their counts, while it holds few buckets, then a dense window over the indices it spans.
/// </para>
/// </remarks>
internal sealed class QuantileSketch
{
    /// <summary>The buckets a store keeps sorted before it becomes a dense window.</summary>
    internal const int SparseLimit = 32;

    /// <summary>The most mantissa bits a grid takes: a relative error near 5 × 10⁻⁷.</summary>
    internal const int MaxBits = 20;

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
    internal long Bytes => _positive.Bytes + _negative.Bytes;

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

    /// <summary>Counts <paramref name="value"/> once.</summary>
    internal void Add(double value) => Add(value, 1);

    /// <summary>Counts <paramref name="value"/> <paramref name="count"/> times.</summary>
    internal void Add(double value, long count)
    {
        if (double.IsNaN(value) || count == 0)
        {
            return;
        }

        Count += count;
        if (value > 0)
        {
            _positive.Add(Index(value), count);
        }
        else if (value < 0)
        {
            _negative.Add(Index(-value), count);
        }
        else
        {
            Zeros += count;
        }
    }

    /// <summary>Adds <paramref name="other"/>'s counts, a sketch of the same grid.</summary>
    /// <exception cref="ArgumentException">The grids differ.</exception>
    internal void Merge(QuantileSketch other)
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
    internal void Write(Action<long> word)
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

    /// <summary>A sketch read back from the words <see cref="Write"/> wrote.</summary>
    internal static QuantileSketch Read(int bits, Func<long> word)
    {
        QuantileSketch sketch = new QuantileSketch(bits);
        long count = word();
        long zeros = word();
        for (int side = 0; side < 2; side++)
        {
            long entries = word();
            for (long e = 0; e < entries; e++)
            {
                int index = checked((int)word());
                sketch.AddBucket(negative: side == 0, index, word());
            }
        }

        sketch.SetTotals(count, zeros);
        return sketch;
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

    /// <summary>The counts of one sign's buckets: sorted indices while few, a dense window past them.</summary>
    private struct Store
    {
        private int[] _keys;
        private long[] _counts;
        private int _sparse;

        // The dense window, null while sparse: counts of indices from _low on.
        private long[]? _window;
        private int _low;

        internal readonly long Bytes =>
            _window is { } window ? (long)window.Length * sizeof(long) : ((long)(_keys?.Length ?? 0) * sizeof(int)) + ((long)(_counts?.Length ?? 0) * sizeof(long));

        internal void Add(int index, long count)
        {
            if (_window is { } window)
            {
                int at = index - _low;
                if ((uint)at < (uint)window.Length)
                {
                    window[at] += count;
                    return;
                }

                Widen(index);
                _window![index - _low] += count;
                return;
            }

            _keys ??= new int[4];
            _counts ??= new long[4];
            int found = Array.BinarySearch(_keys, 0, _sparse, index);
            if (found >= 0)
            {
                _counts[found] += count;
                return;
            }

            int insert = ~found;
            if (_sparse == _keys.Length)
            {
                if (_sparse >= SparseLimit)
                {
                    Densify();
                    Add(index, count);
                    return;
                }

                Array.Resize(ref _keys, _keys.Length * 2);
                Array.Resize(ref _counts, _counts.Length * 2);
            }

            Array.Copy(_keys, insert, _keys, insert + 1, _sparse - insert);
            Array.Copy(_counts, insert, _counts, insert + 1, _sparse - insert);
            _keys[insert] = index;
            _counts[insert] = count;
            _sparse++;
        }

        /// <summary>Adds this store's counts into <paramref name="into"/>.</summary>
        internal readonly void AddTo(ref Store into)
        {
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

        /// <summary>The indices that hold values and their counts, ascending.</summary>
        internal readonly List<(int Index, long Count)> Entries()
        {
            List<(int, long)> entries = [];
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

        private void Densify()
        {
            int low = _keys[0];
            long[] window = new long[Math.Max(_keys[_sparse - 1] - low + 1, SparseLimit * 2)];
            for (int i = 0; i < _sparse; i++)
            {
                window[_keys[i] - low] = _counts[i];
            }

            _window = window;
            _low = low;
            _keys = null!;
            _counts = null!;
            _sparse = 0;
        }

        /// <summary>Grows the window to hold <paramref name="index"/>, by half its length again at least each way it grows.</summary>
        private void Widen(int index)
        {
            long[] window = _window!;
            int low = Math.Min(_low, index);
            int high = Math.Max(_low + window.Length - 1, index);
            int slack = window.Length / 2;
            int newLow = index < _low ? Math.Max(0, low - slack) : _low;
            int length = checked(high - newLow + 1 + (index > _low ? slack : 0));
            long[] grown = new long[length];
            Array.Copy(window, 0, grown, _low - newLow, window.Length);
            _window = grown;
            _low = newLow;
        }
    }
}
