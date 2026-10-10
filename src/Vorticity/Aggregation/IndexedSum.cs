using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Vorticity.Aggregating;

/// <summary>
/// A sum of doubles that is the same bits whatever the order of its values and the cut of its rows:
/// an indexed sum (Demmel and Nguyen; Ahrens, Demmel and Nguyen's ReproBLAS). Bins sit on one grid of
/// exponents for every sum, bin <c>j</c>'s unit <c>2^(27j − 1074)</c>, and three are kept: the bin
/// the largest value calls for and the two below, at least 53 bits under that value. A value is
/// split from the top bin down by rounding at each unit, the pre-rounding of an extractor
/// <c>1.5 · 2^52</c> units wide, each part an integer of its bin's unit read off the rounded bits.
/// A bin above a value takes nothing of it, so a value's parts do not depend on when the top moved,
/// and a bin is the exact sum of its parts, in a long. The answer is the bins' exact total, rounded
/// once.
/// </summary>
/// <remarks>
/// <para>
/// A top that moves up drops the bins it leaves below the three, which a final top that high would
/// not keep either; nothing ever carries from one bin into another, which a drop after it would
/// make depend on the order. The top is each sum's own, from its own values: a group of small
/// values keeps its precision beside a group of large ones.
/// </para>
/// <para>
/// A part is at most 2^26 units, so a sum takes 2^36 values, some 69 billion, in one group or one
/// scan, and throws <see cref="OverflowException"/> past them rather than lose its exactness.
/// Thirty-two bytes a group: three bins and a word for the count, the top and the infinities met.
/// </para>
/// <para>
/// A NaN is skipped, as the statistics skip it; an infinity is counted and marks the sum, which is
/// that infinity, or NaN when both are met, as IEEE 754 gives.
/// </para>
/// </remarks>
internal struct IndexedSum
{
    /// <summary>The values a chunk of a dense span is checked and split by at once.</summary>
    internal const int Chunk = 2048;

    private const int Width = 27;
    private const int Lowest = -1074;
    private const int MinTop = 2;
    private const int MaxTop = 77;

    // From this top up an extractor passes the doubles: the values are split scaled down by
    // 2^(-27 · 4), four bins lower, where the parts are the same integers.
    private const int HugeTop = 76;
    private const int HugeBins = 4;

    private const int TopShift = 48;
    private const long CountMask = (1L << TopShift) - 1;
    private const long TopMask = 0x7FL << TopShift;
    private const long PositiveInfinity = 1L << 56;
    private const long NegativeInfinity = 1L << 57;

    /// <summary>The values a sum takes: each adds a part of at most 2^26 to a bin, which stays a long, times a weight.</summary>
    private const long Endurance = 1L << 36;

    private static readonly double HugeScale = Math.ScaleB(1.0, -Width * HugeBins);

    // By bin: the extractor 1.5 · 2^52 units wide, its bits; zero past the doubles.
    private static readonly double[] Sigma = Extractors();
    private static readonly long[] SigmaBits = Array.ConvertAll(Sigma, BitConverter.DoubleToInt64Bits);

    // By top: the magnitude a value must stay below, half the unit of the bin above; zero for no
    // top yet and for the tops split scaled, which every value then reaches through the slow path.
    private static readonly double[] Limit = Limits();

    internal long M0;
    internal long M1;
    internal long M2;

    // The values counted, the top bin above bit 48, the infinities met above bit 56.
    internal long Meta;

    /// <summary>The values summed: every one but the NaN.</summary>
    internal readonly long Count => Meta & CountMask;

    private readonly int Top => (int)((Meta & TopMask) >>> TopShift);

    /// <summary>The sum, rounded once from the bins' exact total.</summary>
    internal readonly double Value
    {
        get
        {
            long infinities = Meta & (PositiveInfinity | NegativeInfinity);
            if (infinities != 0)
            {
                return infinities == PositiveInfinity ? double.PositiveInfinity : infinities == NegativeInfinity ? double.NegativeInfinity : double.NaN;
            }

            int top = Top;
            if (top == 0)
            {
                return 0;
            }

            // The bins' total in two words, each bin sign-extended and shifted into place, added with
            // their carries: the shifts of an Int128 were calls to its operators.
            ulong low = (ulong)M0 << (2 * Width);
            ulong high = (ulong)(M0 >> (64 - (2 * Width)));
            Add(ref high, ref low, (ulong)(M1 >> (64 - Width)), (ulong)M1 << Width);
            Add(ref high, ref low, (ulong)(M2 >> 63), (ulong)M2);
            return Round(high, low, (Width * (top - 2)) + Lowest);
        }
    }

    /// <summary>Adds the word pair (<paramref name="addHigh"/>, <paramref name="addLow"/>) to (<paramref name="high"/>, <paramref name="low"/>), the low word's carry into the high.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Add(ref ulong high, ref ulong low, ulong addHigh, ulong addLow)
    {
        ulong sum = low + addLow;
        high += addHigh + (sum < low ? 1UL : 0UL);
        low = sum;
    }

    /// <summary>
    /// The value of the two's complement total (<paramref name="high"/>, <paramref name="low"/>) units of
    /// <c>2^exponent</c>, rounded to the nearest double, ties to even, as <see cref="Round(Int128, int)"/>:
    /// in words while fewer than 64 bits are dropped, the round bit and the bits below it read off the
    /// low word; through the 128-bit rounding past them.
    /// </summary>
    internal static double Round(ulong high, ulong low, int exponent)
    {
        if ((high | low) == 0)
        {
            return 0;
        }

        bool negative = (long)high < 0;
        if (negative)
        {
            low = ~low + 1;
            high = ~high + (low == 0 ? 1UL : 0UL);
        }

        int length = high != 0 ? 128 - BitOperations.LeadingZeroCount(high) : 64 - BitOperations.LeadingZeroCount(low);
        int leading = length - 1 + exponent;
        int drop = Math.Max(leading - 52, Lowest) - exponent;
        ulong kept;
        if (drop <= 0)
        {
            // At most 53 bits: the low word holds them all, exact as a double.
            kept = low;
        }
        else if (drop < 64)
        {
            kept = (low >> drop) | (high << (64 - drop));
            ulong round = (low >> (drop - 1)) & 1;
            ulong below = low & ((1UL << (drop - 1)) - 1);
            if (round != 0 && (below != 0 || (kept & 1) != 0))
            {
                kept++;
            }

            exponent += drop;
        }
        else
        {
            double wide = Round(new Int128(high, low), exponent);
            return negative ? -wide : wide;
        }

        double result = Math.ScaleB((double)kept, exponent);
        return negative ? -result : result;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Add(double value)
    {
        int top = Top;
        if (!(Math.Abs(value) < Limit[top]))
        {
            AddSlow(value);
            return;
        }

        if ((++Meta & CountMask) > Endurance)
        {
            throw Outgrown();
        }

        Deposit(value, top);
    }

    /// <summary>Folds <paramref name="count"/> values equal to <paramref name="value"/>: a run, a constant, a dictionary entry.</summary>
    internal void AddWeighted(double value, long count)
    {
        if (double.IsNaN(value))
        {
            return;
        }

        Counted(count);
        if (double.IsInfinity(value))
        {
            Meta |= value > 0 ? PositiveInfinity : NegativeInfinity;
            return;
        }

        int top = Raise(Math.Abs(value));
        if (top >= HugeTop)
        {
            value *= HugeScale;
            top -= HugeBins;
        }

        // Each part times the count: at most 2^26 times what the endurance leaves, exact in a long.
        ref double sigma = ref MemoryMarshal.GetArrayDataReference(Sigma);
        ref long bits = ref MemoryMarshal.GetArrayDataReference(SigmaBits);
        double s = Unsafe.Add(ref sigma, top);
        double y = s + value;
        M0 += (BitConverter.DoubleToInt64Bits(y) - Unsafe.Add(ref bits, top)) * count;
        double r = value - (y - s);
        s = Unsafe.Add(ref sigma, top - 1);
        y = s + r;
        M1 += (BitConverter.DoubleToInt64Bits(y) - Unsafe.Add(ref bits, top - 1)) * count;
        r -= y - s;
        M2 += (BitConverter.DoubleToInt64Bits(Unsafe.Add(ref sigma, top - 2) + r) - Unsafe.Add(ref bits, top - 2)) * count;
    }

    /// <summary>
    /// Folds a dense span, a chunk at a time: its largest magnitude moves the top once, then its
    /// values are split in vector lanes with the top's extractors; a chunk holding a NaN or an
    /// infinity is folded a value at a time. The parts are the ones a value at a time gives.
    /// </summary>
    internal void AddSpan(ReadOnlySpan<double> values)
    {
        for (int start = 0; start < values.Length; start += Chunk)
        {
            ReadOnlySpan<double> chunk = values.Slice(start, Math.Min(Chunk, values.Length - start));
            if (!TryMagnitude(chunk, out double largest))
            {
                foreach (double value in chunk)
                {
                    Add(value);
                }

                continue;
            }

            int top = Raise(largest);
            Counted(chunk.Length);
            if (top >= HugeTop)
            {
                foreach (double value in chunk)
                {
                    Deposit(value * HugeScale, top - HugeBins);
                }

                continue;
            }

            DepositSpan(chunk, top);
        }
    }

    /// <summary>Takes back <paramref name="count"/> values counted that were not values: the zeros standing for rows left out.</summary>
    internal void Uncount(long count) => Meta -= count;

    /// <summary>Folds another sum: the higher top for both, the bins added.</summary>
    internal void Merge(in IndexedSum other)
    {
        // An empty sum takes the other whole: its count, its top, its infinities and its bins are what the
        // fold would give, with no shift. Nearly every group of db-benchmark's q10 merges into an empty
        // one, 9 % of its cycles at fourteen lanes.
        if (Meta == 0)
        {
            this = other;
            return;
        }

        IndexedSum theirs = other;
        Counted(theirs.Count);
        int mine = Top;
        int top = theirs.Top;
        if (top > mine)
        {
            Shift(top);
        }
        else if (top != 0 && mine > top)
        {
            theirs.Shift(mine);
        }

        if (top != 0)
        {
            M0 += theirs.M0;
            M1 += theirs.M1;
            M2 += theirs.M2;
        }

        Meta |= theirs.Meta & (PositiveInfinity | NegativeInfinity);
    }

    /// <summary>The value of <paramref name="total"/> units of <c>2^exponent</c>, rounded to the nearest double, ties to even.</summary>
    internal static double Round(Int128 total, int exponent)
    {
        if (total == Int128.Zero)
        {
            return 0;
        }

        bool negative = total < Int128.Zero;
        UInt128 magnitude = negative ? (UInt128)(-total) : (UInt128)total;
        int length = 128 - (int)UInt128.LeadingZeroCount(magnitude);

        // The lowest bit a double keeps there: 52 below the leading one, never below 2^-1074.
        int leading = length - 1 + exponent;
        int drop = Math.Max(leading - 52, Lowest) - exponent;
        UInt128 kept = magnitude;
        if (drop > 0)
        {
            kept = drop >= 128 ? UInt128.Zero : magnitude >> drop;
            UInt128 rest = drop >= 128 ? magnitude : magnitude & ((UInt128.One << drop) - UInt128.One);
            UInt128 half = drop > 128 ? UInt128.MaxValue : UInt128.One << (drop - 1);
            if (rest > half || (rest == half && (kept & UInt128.One) == UInt128.One))
            {
                kept++;
            }

            exponent += drop;
        }

        // At most 2^53 now, exact as a double; the scale is exact unless it passes the largest double.
        double result = Math.ScaleB((double)(ulong)kept, exponent);
        return negative ? -result : result;
    }

    private static OverflowException Outgrown() =>
        new OverflowException($"A float sum takes {Endurance} values at most, in one group or one scan: past them its bins would lose their exactness.");

    private static double[] Extractors()
    {
        double[] sigma = new double[MaxTop + 1];
        for (int bin = 0; bin < sigma.Length; bin++)
        {
            int exponent = 52 + (Width * bin) + Lowest;
            sigma[bin] = exponent <= 1023 ? 1.5 * Math.ScaleB(1.0, exponent) : 0;
        }

        return sigma;
    }

    private static double[] Limits()
    {
        double[] limit = new double[MaxTop + 1];
        for (int top = MinTop; top < HugeTop; top++)
        {
            limit[top] = Math.ScaleB(1.0, (Width * top) + Lowest + Width - 1);
        }

        return limit;
    }

    /// <summary>
    /// The top a magnitude calls for: the lowest whose limit, <c>2^(27 · top − 1048)</c>, the whole
    /// binade of the magnitude stays below. The binade read off the exponent's bits, or for a subnormal
    /// off its highest bit, as <see cref="Math.ILogB"/> gives it, which is a call.
    /// </summary>
    internal static int Needed(double magnitude)
    {
        if (magnitude == 0)
        {
            return MinTop;
        }

        ulong bits = (ulong)BitConverter.DoubleToInt64Bits(magnitude);
        int biased = (int)(bits >> 52);
        int binade = biased != 0 ? biased - 1023 : 63 - BitOperations.LeadingZeroCount(bits) + Lowest;
        int bound = binade + 1 - (Lowest + Width - 1);
        return Math.Max(MinTop, (bound + Width - 1) / Width);
    }

    /// <summary>
    /// The largest magnitude of <paramref name="values"/>, false when one of them is a NaN or an
    /// infinity.
    /// </summary>
    private static bool TryMagnitude(ReadOnlySpan<double> values, out double largest)
    {
        int i = 0;
        double best = 0;
        if (Vector.IsHardwareAccelerated && values.Length >= Vector<double>.Count)
        {
            Vector<double> most = Vector<double>.Zero;
            Vector<long> finite = Vector<long>.AllBitsSet;
            Vector<double> infinity = new Vector<double>(double.PositiveInfinity);
            ref double first = ref MemoryMarshal.GetReference(values);
            for (; i <= values.Length - Vector<double>.Count; i += Vector<double>.Count)
            {
                Vector<double> magnitude = Vector.Abs(Vector.LoadUnsafe(ref first, (nuint)i));
                finite &= Vector.LessThan(magnitude, infinity);
                most = Vector.Max(most, magnitude);
            }

            if (!Vector.EqualsAll(finite, Vector<long>.AllBitsSet))
            {
                largest = 0;
                return false;
            }

            for (int lane = 0; lane < Vector<double>.Count; lane++)
            {
                best = Math.Max(best, most[lane]);
            }
        }

        for (; i < values.Length; i++)
        {
            double magnitude = Math.Abs(values[i]);
            if (!(magnitude < double.PositiveInfinity))
            {
                largest = 0;
                return false;
            }

            best = Math.Max(best, magnitude);
        }

        largest = best;
        return true;
    }

    private void AddSlow(double value)
    {
        if (double.IsNaN(value))
        {
            return;
        }

        Counted(1);
        if (double.IsInfinity(value))
        {
            Meta |= value > 0 ? PositiveInfinity : NegativeInfinity;
            return;
        }

        int top = Raise(Math.Abs(value));
        if (top >= HugeTop)
        {
            Deposit(value * HugeScale, top - HugeBins);
        }
        else
        {
            Deposit(value, top);
        }
    }

    private void Counted(long count)
    {
        if (Count + count > Endurance)
        {
            throw Outgrown();
        }

        Meta += count;
    }

    /// <summary>The top once <paramref name="magnitude"/> is in: this one, or the one it calls for, the bins moved up to it.</summary>
    private int Raise(double magnitude)
    {
        int top = Top;
        int needed = Needed(magnitude);
        if (needed > top)
        {
            Shift(needed);
            return needed;
        }

        return top;
    }

    /// <summary>Moves the bins up to <paramref name="top"/>; those left below the three fall out.</summary>
    private void Shift(int top)
    {
        int now = Top;
        if (now != 0)
        {
            int steps = Math.Min(top - now, 3);
            for (int step = 0; step < steps; step++)
            {
                M2 = M1;
                M1 = M0;
                M0 = 0;
            }
        }

        Meta = (Meta & ~TopMask) | ((long)top << TopShift);
    }

    /// <summary>Splits one value into the three bins from <paramref name="top"/> down.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Deposit(double value, int top)
    {
        ref double sigma = ref MemoryMarshal.GetArrayDataReference(Sigma);
        ref long bits = ref MemoryMarshal.GetArrayDataReference(SigmaBits);
        double s = Unsafe.Add(ref sigma, top);
        double y = s + value;
        M0 += BitConverter.DoubleToInt64Bits(y) - Unsafe.Add(ref bits, top);
        double r = value - (y - s);
        s = Unsafe.Add(ref sigma, top - 1);
        y = s + r;
        M1 += BitConverter.DoubleToInt64Bits(y) - Unsafe.Add(ref bits, top - 1);
        r -= y - s;
        M2 += BitConverter.DoubleToInt64Bits(Unsafe.Add(ref sigma, top - 2) + r) - Unsafe.Add(ref bits, top - 2);
    }

    /// <summary>
    /// Splits a chunk in vector lanes. A lane sums the rounded bits themselves, wrapping, and the
    /// extractors' bits come off once a chunk: exact modulo 2^64, where each bin's true sum lies.
    /// </summary>
    private void DepositSpan(ReadOnlySpan<double> values, int top)
    {
        int i = 0;
        if (Vector.IsHardwareAccelerated && values.Length >= Vector<double>.Count)
        {
            Vector<double> s0 = new Vector<double>(Sigma[top]);
            Vector<double> s1 = new Vector<double>(Sigma[top - 1]);
            Vector<double> s2 = new Vector<double>(Sigma[top - 2]);
            Vector<long> a0 = Vector<long>.Zero;
            Vector<long> a1 = Vector<long>.Zero;
            Vector<long> a2 = Vector<long>.Zero;
            ref double first = ref MemoryMarshal.GetReference(values);
            int whole = values.Length - (values.Length % Vector<double>.Count);
            for (; i < whole; i += Vector<double>.Count)
            {
                Vector<double> x = Vector.LoadUnsafe(ref first, (nuint)i);
                Vector<double> y = s0 + x;
                a0 += Vector.AsVectorInt64(y);
                Vector<double> r = x - (y - s0);
                y = s1 + r;
                a1 += Vector.AsVectorInt64(y);
                r -= y - s1;
                a2 += Vector.AsVectorInt64(s2 + r);
            }

            long n = whole;
            unchecked
            {
                M0 += Vector.Sum(a0) - (n * SigmaBits[top]);
                M1 += Vector.Sum(a1) - (n * SigmaBits[top - 1]);
                M2 += Vector.Sum(a2) - (n * SigmaBits[top - 2]);
            }
        }

        for (; i < values.Length; i++)
        {
            Deposit(values[i], top);
        }
    }

    /// <summary>
    /// Folds the rows [<paramref name="start"/>, <paramref name="end"/>) of <paramref name="values"/> into the
    /// sums of their groups, <paramref name="states"/>: the window's largest magnitude calls for a top, whose
    /// extractors stay in registers. A row whose group sits at that top is split with them, its group read
    /// for its bins alone; a group that holds no value yet takes its first at the top the value calls for;
    /// any other row is folded as <see cref="Add(double)"/> folds it. A window with a NaN or an infinity, or that
    /// calls for a top split scaled, is folded a row at a time. The parts are those Add gives.
    /// </summary>
    /// <remarks>
    /// Add reads the group's top, then its extractors by it, then splits the value: a chain from the group's
    /// state through seven additions to its bins, which a row whose group a row still in flight holds
    /// waits for whole. A hundred groups took ≈ 12 cycles a value (vortex-queries --micro floatsum). Here
    /// the split does not wait on the group: only the three bins and the count do.
    /// </remarks>
    /// <returns>The rows folded as Add folds them: groups at another top than the window's.</returns>
    internal static int Fold(StateView<IndexedSum> states, ReadOnlySpan<int> groups, ReadOnlySpan<double> values, int start, int end)
    {
        int top = TopOf(states, groups, values, start, end);
        if (top is < MinTop or >= HugeTop)
        {
            for (int row = start; row < end; row++)
            {
                states[groups[row]].Add(values[row]);
            }

            return end - start;
        }

        // The group's top and count read at once: their difference from the top's, unsigned, is the count
        // when the group sits at the top, past the endurance otherwise. A value at or past the top's limit,
        // a NaN or an infinity among them, is folded as Add folds it.
        long topBits = (long)top << TopShift;
        double limit = Limit[top];
        double s0 = Sigma[top];
        double s1 = Sigma[top - 1];
        double s2 = Sigma[top - 2];
        long b0 = SigmaBits[top];
        long b1 = SigmaBits[top - 1];
        long b2 = SigmaBits[top - 2];
        nint offset = states.Offset;
        ref int groupOf = ref MemoryMarshal.GetReference(groups);
        ref double valueOf = ref MemoryMarshal.GetReference(values);
        int added = 0;
        for (int row = start; row < end; row++)
        {
            double value = Unsafe.Add(ref valueOf, row);
            double magnitude = Math.Abs(value);
            ref IndexedSum sum = ref Unsafe.As<byte, IndexedSum>(ref Unsafe.AddByteOffset(ref states.Record(Unsafe.Add(ref groupOf, row)), offset));
            long meta = sum.Meta;
            if (((ulong)((meta & (TopMask | CountMask)) - topBits) < Endurance) & (magnitude < limit))
            {
                sum.Meta = meta + 1;
                double y = s0 + value;
                sum.M0 += BitConverter.DoubleToInt64Bits(y) - b0;
                double r = value - (y - s0);
                y = s1 + r;
                sum.M1 += BitConverter.DoubleToInt64Bits(y) - b1;
                r -= y - s1;
                sum.M2 += BitConverter.DoubleToInt64Bits(s2 + r) - b2;
            }
            else if (meta == 0 && magnitude < double.PositiveInfinity && Needed(magnitude) is int own && own < HugeTop)
            {
                // A group's first value, which no NaN came before: counted, its top the value's, as AddSlow
                // does with no bin to move.
                sum.Meta = ((long)own << TopShift) + 1;
                sum.Deposit(value, own);
            }
            else
            {
                sum.Add(value);
                added++;
            }
        }

        return added;
    }

    /// <summary>
    /// The top a window is split at: that of its first row's group, or of the first row's value when its
    /// group holds none yet; 0 when that value is a NaN or an infinity.
    /// </summary>
    private static int TopOf(StateView<IndexedSum> states, ReadOnlySpan<int> groups, ReadOnlySpan<double> values, int start, int end)
    {
        if (start >= end)
        {
            return 0;
        }

        int top = states[groups[start]].Top;
        double magnitude = Math.Abs(values[start]);
        return top != 0 ? top : magnitude < double.PositiveInfinity ? Needed(magnitude) : 0;
    }
}

/// <summary>A sum of floating-point values in an <see cref="IndexedSum"/>: the same bits in any order and under any cut.</summary>
internal readonly struct IndexedFloatSum<TValue> : IValueOp<TValue, IndexedSum>
    where TValue : unmanaged, INumberBase<TValue>
{
    public IndexedSum Seed() => default;

    public void Add(ref IndexedSum state, TValue value) => state.Add(double.CreateTruncating(value));

    public void AddSelected(ref IndexedSum state, TValue value) => Add(ref state, value);

    public void AddWeighted(ref IndexedSum state, TValue value, long count) => state.AddWeighted(double.CreateTruncating(value), count);

    [SkipLocalsInit]
    public void AddSpan(ref IndexedSum state, ReadOnlySpan<TValue> values)
    {
        if (typeof(TValue) == typeof(double))
        {
            state.AddSpan(MemoryMarshal.Cast<TValue, double>(values));
            return;
        }

        // Widened a chunk at a time, exactly: a float or a half is a double.
        Span<double> widened = stackalloc double[IndexedSum.Chunk];
        for (int start = 0; start < values.Length; start += IndexedSum.Chunk)
        {
            ReadOnlySpan<TValue> chunk = values.Slice(start, Math.Min(IndexedSum.Chunk, values.Length - start));
            for (int i = 0; i < chunk.Length; i++)
            {
                widened[i] = double.CreateTruncating(chunk[i]);
            }

            state.AddSpan(widened[..chunk.Length]);
        }
    }

    public void Merge(ref IndexedSum into, in IndexedSum other) => into.Merge(in other);

    public double? Mean(in IndexedSum state) => state.Count == 0 ? null : state.Value / state.Count;

    /// <remarks>The rows left out read as zero, which no bin takes anything of, and come off the count.</remarks>
    [SkipLocalsInit]
    public void AddWords(ref IndexedSum state, ReadOnlySpan<TValue> block, ReadOnlySpan<ulong> words)
    {
        Span<TValue> selected = stackalloc TValue[WordFold.Run * 64];
        selected = selected[..block.Length];
        WordFold.Select(block, words, TValue.Zero, selected);
        AddSpan(ref state, selected);
        state.Uncount(selected.Length - WordFold.Count(words));
    }
}
