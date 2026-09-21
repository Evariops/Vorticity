using System;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Vorticity.Aggregating;

/// <summary>Sums of dense spans, widened the way the file statistics widen them, with <see cref="Vector{T}"/>.</summary>
internal static class SumKernels
{
    // Lanes of 32 bits absorb this many vectors of 8- or 16-bit values without overflowing.
    private const int NarrowBlock = 16_384;

    /// <summary>The sum of signed integers, exact in 128 bits whatever their order.</summary>
    internal static Int128 Signed<T>(ReadOnlySpan<T> values)
        where T : unmanaged, IBinaryInteger<T>
    {
        if (typeof(T) == typeof(int))
        {
            return Int32(MemoryMarshal.Cast<T, int>(values));
        }

        if (typeof(T) == typeof(long))
        {
            return Int64(MemoryMarshal.Cast<T, long>(values));
        }

        if (typeof(T) == typeof(short))
        {
            return Int16(MemoryMarshal.Cast<T, short>(values));
        }

        if (typeof(T) == typeof(sbyte))
        {
            return Int8(MemoryMarshal.Cast<T, sbyte>(values));
        }

        Int128 total = 0;
        foreach (T value in values)
        {
            total += Int128.CreateTruncating(value);
        }

        return total;
    }

    /// <summary>The sum of unsigned integers, exact in 128 bits whatever their order.</summary>
    internal static UInt128 Unsigned<T>(ReadOnlySpan<T> values)
        where T : unmanaged, IBinaryInteger<T>
    {
        if (typeof(T) == typeof(uint))
        {
            return UInt32(MemoryMarshal.Cast<T, uint>(values));
        }

        if (typeof(T) == typeof(ulong))
        {
            return UInt64(MemoryMarshal.Cast<T, ulong>(values));
        }

        if (typeof(T) == typeof(ushort))
        {
            return UInt16(MemoryMarshal.Cast<T, ushort>(values));
        }

        if (typeof(T) == typeof(byte))
        {
            return UInt8(MemoryMarshal.Cast<T, byte>(values));
        }

        UInt128 total = 0;
        foreach (T value in values)
        {
            total += UInt128.CreateTruncating(value);
        }

        return total;
    }

    /// <summary>The sum of floating-point values as a double, NaN skipped as the statistics skip it.</summary>
    /// <param name="values">The values.</param>
    /// <param name="counted">How many values were not NaN.</param>
    internal static double Float<T>(ReadOnlySpan<T> values, out long counted)
        where T : unmanaged, INumberBase<T>
    {
        if (typeof(T) == typeof(double))
        {
            return Double(MemoryMarshal.Cast<T, double>(values), out counted);
        }

        if (typeof(T) == typeof(float))
        {
            return Single(MemoryMarshal.Cast<T, float>(values), out counted);
        }

        double total = 0;
        long seen = 0;
        foreach (T value in values)
        {
            double widened = double.CreateTruncating(value);
            if (!double.IsNaN(widened))
            {
                total += widened;
                seen++;
            }
        }

        counted = seen;
        return total;
    }

    private static long Int32(ReadOnlySpan<int> values)
    {
        long total = 0;
        int i = 0;
        if (Vector.IsHardwareAccelerated && values.Length >= Vector<int>.Count)
        {
            // A lane of 64 bits takes 2^31 values of 32 bits before it can overflow: no span has that many.
            Vector<long> low = Vector<long>.Zero;
            Vector<long> high = Vector<long>.Zero;
            for (; i <= values.Length - Vector<int>.Count; i += Vector<int>.Count)
            {
                Vector.Widen(new Vector<int>(values.Slice(i)), out Vector<long> l, out Vector<long> h);
                low += l;
                high += h;
            }

            total = Vector.Sum(low + high);
        }

        for (; i < values.Length; i++)
        {
            total += values[i];
        }

        return total;
    }

    private static long Int16(ReadOnlySpan<short> values)
    {
        long total = 0;
        int i = 0;
        if (Vector.IsHardwareAccelerated)
        {
            int step = Vector<short>.Count;
            int vectorEnd = values.Length - (values.Length % step);
            while (i < vectorEnd)
            {
                int blockEnd = vectorEnd - i > step * NarrowBlock ? i + (step * NarrowBlock) : vectorEnd;
                Vector<int> acc = Vector<int>.Zero;
                for (; i < blockEnd; i += step)
                {
                    Vector.Widen(new Vector<short>(values.Slice(i)), out Vector<int> l, out Vector<int> h);
                    acc += l + h;
                }

                Vector.Widen(acc, out Vector<long> wl, out Vector<long> wh);
                total += Vector.Sum(wl + wh);
            }
        }

        for (; i < values.Length; i++)
        {
            total += values[i];
        }

        return total;
    }

    private static long Int8(ReadOnlySpan<sbyte> values)
    {
        long total = 0;
        int i = 0;
        if (Vector.IsHardwareAccelerated)
        {
            int step = Vector<sbyte>.Count;
            int vectorEnd = values.Length - (values.Length % step);
            while (i < vectorEnd)
            {
                int blockEnd = vectorEnd - i > step * NarrowBlock ? i + (step * NarrowBlock) : vectorEnd;
                Vector<int> acc = Vector<int>.Zero;
                for (; i < blockEnd; i += step)
                {
                    Vector.Widen(new Vector<sbyte>(values.Slice(i)), out Vector<short> l, out Vector<short> h);
                    Vector.Widen(l + h, out Vector<int> wl, out Vector<int> wh);
                    acc += wl + wh;
                }

                Vector.Widen(acc, out Vector<long> ll, out Vector<long> lh);
                total += Vector.Sum(ll + lh);
            }
        }

        for (; i < values.Length; i++)
        {
            total += values[i];
        }

        return total;
    }

    private static Int128 Int64(ReadOnlySpan<long> values)
    {
        Int128 total = 0;
        int i = 0;
        if (Vector.IsHardwareAccelerated && values.Length >= Vector<long>.Count)
        {
            Vector<long> acc = Vector<long>.Zero;
            Vector<long> overflow = Vector<long>.Zero;
            for (; i <= values.Length - Vector<long>.Count; i += Vector<long>.Count)
            {
                Vector<long> v = new Vector<long>(values.Slice(i));
                Vector<long> sum = acc + v;

                // Two operands of one sign whose sum has the other: the sign bit of this is set.
                overflow |= (acc ^ sum) & (v ^ sum);
                acc = sum;
            }

            if (Vector.LessThanAny(overflow, Vector<long>.Zero))
            {
                // A lane left 64 bits, which values near the ends of the range do: the span is
                // summed again one value at a time, in 128 bits.
                total = 0;
                foreach (long value in values)
                {
                    total += value;
                }

                return total;
            }

            for (int lane = 0; lane < Vector<long>.Count; lane++)
            {
                total += acc[lane];
            }
        }

        for (; i < values.Length; i++)
        {
            total += values[i];
        }

        return total;
    }

    private static ulong UInt32(ReadOnlySpan<uint> values)
    {
        ulong total = 0;
        int i = 0;
        if (Vector.IsHardwareAccelerated && values.Length >= Vector<uint>.Count)
        {
            Vector<ulong> low = Vector<ulong>.Zero;
            Vector<ulong> high = Vector<ulong>.Zero;
            for (; i <= values.Length - Vector<uint>.Count; i += Vector<uint>.Count)
            {
                Vector.Widen(new Vector<uint>(values.Slice(i)), out Vector<ulong> l, out Vector<ulong> h);
                low += l;
                high += h;
            }

            total = Vector.Sum(low + high);
        }

        for (; i < values.Length; i++)
        {
            total += values[i];
        }

        return total;
    }

    private static ulong UInt16(ReadOnlySpan<ushort> values)
    {
        ulong total = 0;
        int i = 0;
        if (Vector.IsHardwareAccelerated)
        {
            int step = Vector<ushort>.Count;
            int vectorEnd = values.Length - (values.Length % step);
            while (i < vectorEnd)
            {
                int blockEnd = vectorEnd - i > step * NarrowBlock ? i + (step * NarrowBlock) : vectorEnd;
                Vector<uint> acc = Vector<uint>.Zero;
                for (; i < blockEnd; i += step)
                {
                    Vector.Widen(new Vector<ushort>(values.Slice(i)), out Vector<uint> l, out Vector<uint> h);
                    acc += l + h;
                }

                Vector.Widen(acc, out Vector<ulong> wl, out Vector<ulong> wh);
                total += Vector.Sum(wl + wh);
            }
        }

        for (; i < values.Length; i++)
        {
            total += values[i];
        }

        return total;
    }

    private static ulong UInt8(ReadOnlySpan<byte> values)
    {
        ulong total = 0;
        int i = 0;
        if (Vector.IsHardwareAccelerated)
        {
            int step = Vector<byte>.Count;
            int vectorEnd = values.Length - (values.Length % step);
            while (i < vectorEnd)
            {
                int blockEnd = vectorEnd - i > step * NarrowBlock ? i + (step * NarrowBlock) : vectorEnd;
                Vector<uint> acc = Vector<uint>.Zero;
                for (; i < blockEnd; i += step)
                {
                    Vector.Widen(new Vector<byte>(values.Slice(i)), out Vector<ushort> l, out Vector<ushort> h);
                    Vector.Widen(l + h, out Vector<uint> wl, out Vector<uint> wh);
                    acc += wl + wh;
                }

                Vector.Widen(acc, out Vector<ulong> ll, out Vector<ulong> lh);
                total += Vector.Sum(ll + lh);
            }
        }

        for (; i < values.Length; i++)
        {
            total += values[i];
        }

        return total;
    }

    private static UInt128 UInt64(ReadOnlySpan<ulong> values)
    {
        UInt128 total = 0;
        int i = 0;
        if (Vector.IsHardwareAccelerated && values.Length >= Vector<ulong>.Count)
        {
            Vector<ulong> acc = Vector<ulong>.Zero;
            Vector<ulong> overflow = Vector<ulong>.Zero;
            for (; i <= values.Length - Vector<ulong>.Count; i += Vector<ulong>.Count)
            {
                Vector<ulong> v = new Vector<ulong>(values.Slice(i));
                Vector<ulong> sum = acc + v;
                overflow |= Vector.LessThan(sum, v);
                acc = sum;
            }

            if (overflow != Vector<ulong>.Zero)
            {
                // A lane left 64 bits: the span is summed again one value at a time, in 128 bits.
                total = 0;
                foreach (ulong value in values)
                {
                    total += value;
                }

                return total;
            }

            for (int lane = 0; lane < Vector<ulong>.Count; lane++)
            {
                total += acc[lane];
            }
        }

        for (; i < values.Length; i++)
        {
            total += values[i];
        }

        return total;
    }

    private static double Double(ReadOnlySpan<double> values, out long counted)
    {
        double total = 0;
        long skipped = 0;
        int i = 0;
        if (Vector.IsHardwareAccelerated && values.Length >= Vector<double>.Count)
        {
            Vector<double> acc = Vector<double>.Zero;
            Vector<long> nan = Vector<long>.Zero;
            for (; i <= values.Length - Vector<double>.Count; i += Vector<double>.Count)
            {
                Vector<double> v = new Vector<double>(values.Slice(i));
                Vector<long> number = Vector.Equals(v, v);
                acc += Vector.ConditionalSelect(number, v, Vector<double>.Zero);
                nan += Vector<long>.One + number;
            }

            total = Vector.Sum(acc);
            skipped = Vector.Sum(nan);
        }

        for (; i < values.Length; i++)
        {
            double value = values[i];
            if (double.IsNaN(value))
            {
                skipped++;
            }
            else
            {
                total += value;
            }
        }

        counted = values.Length - skipped;
        return total;
    }

    private static double Single(ReadOnlySpan<float> values, out long counted)
    {
        double total = 0;
        long skipped = 0;
        int i = 0;
        if (Vector.IsHardwareAccelerated && values.Length >= Vector<float>.Count)
        {
            Vector<double> acc = Vector<double>.Zero;
            Vector<long> nan = Vector<long>.Zero;
            for (; i <= values.Length - Vector<float>.Count; i += Vector<float>.Count)
            {
                Vector.Widen(new Vector<float>(values.Slice(i)), out Vector<double> low, out Vector<double> high);
                Vector<long> lowNumber = Vector.Equals(low, low);
                Vector<long> highNumber = Vector.Equals(high, high);
                acc += Vector.ConditionalSelect(lowNumber, low, Vector<double>.Zero) + Vector.ConditionalSelect(highNumber, high, Vector<double>.Zero);
                nan += (Vector<long>.One + lowNumber) + (Vector<long>.One + highNumber);
            }

            total = Vector.Sum(acc);
            skipped = Vector.Sum(nan);
        }

        for (; i < values.Length; i++)
        {
            float value = values[i];
            if (float.IsNaN(value))
            {
                skipped++;
            }
            else
            {
                total += value;
            }
        }

        counted = values.Length - skipped;
        return total;
    }
}

/// <summary>The smallest and largest value of dense spans, NaN skipped, with <see cref="Vector{T}"/> where the type has lanes.</summary>
internal static class ExtremeKernels
{
    internal static bool TryMin<T>(ReadOnlySpan<T> values, out T result)
        where T : unmanaged, INumber<T>
    {
        int i = FirstNumber(values);
        if (i == values.Length)
        {
            result = default;
            return false;
        }

        T best = values[i++];
        if (Vector.IsHardwareAccelerated && Vector<T>.IsSupported && values.Length - i >= Vector<T>.Count)
        {
            Vector<T> acc = new Vector<T>(best);
            for (; i <= values.Length - Vector<T>.Count; i += Vector<T>.Count)
            {
                Vector<T> v = new Vector<T>(values.Slice(i));
                if (typeof(T) == typeof(double) || typeof(T) == typeof(float))
                {
                    v = Vector.ConditionalSelect(Vector.Equals(v, v), v, acc);
                }

                acc = Vector.Min(acc, v);
            }

            for (int lane = 0; lane < Vector<T>.Count; lane++)
            {
                if (acc[lane] < best)
                {
                    best = acc[lane];
                }
            }
        }

        // A NaN compares false with everything, so the scalar tail skips it without a test.
        for (; i < values.Length; i++)
        {
            T value = values[i];
            if (value < best)
            {
                best = value;
            }
        }

        result = best;
        return true;
    }

    internal static bool TryMax<T>(ReadOnlySpan<T> values, out T result)
        where T : unmanaged, INumber<T>
    {
        int i = FirstNumber(values);
        if (i == values.Length)
        {
            result = default;
            return false;
        }

        T best = values[i++];
        if (Vector.IsHardwareAccelerated && Vector<T>.IsSupported && values.Length - i >= Vector<T>.Count)
        {
            Vector<T> acc = new Vector<T>(best);
            for (; i <= values.Length - Vector<T>.Count; i += Vector<T>.Count)
            {
                Vector<T> v = new Vector<T>(values.Slice(i));
                if (typeof(T) == typeof(double) || typeof(T) == typeof(float))
                {
                    v = Vector.ConditionalSelect(Vector.Equals(v, v), v, acc);
                }

                acc = Vector.Max(acc, v);
            }

            for (int lane = 0; lane < Vector<T>.Count; lane++)
            {
                if (acc[lane] > best)
                {
                    best = acc[lane];
                }
            }
        }

        for (; i < values.Length; i++)
        {
            T value = values[i];
            if (value > best)
            {
                best = value;
            }
        }

        result = best;
        return true;
    }

    private static int FirstNumber<T>(ReadOnlySpan<T> values)
        where T : unmanaged, INumber<T>
    {
        int i = 0;
        while (i < values.Length && T.IsNaN(values[i]))
        {
            i++;
        }

        return i;
    }
}

/// <summary>Row masks as 64-bit words, bit <c>i % 64</c> of word <c>i / 64</c> for row <c>i</c>; empty means every row.</summary>
internal static class RowMasks
{
    /// <summary>How many rows of [<paramref name="start"/>, <paramref name="end"/>) the mask holds.</summary>
    internal static int Count(ReadOnlySpan<ulong> mask, int start, int end)
    {
        if (end <= start)
        {
            return 0;
        }

        if (mask.IsEmpty)
        {
            return end - start;
        }

        int first = start >> 6;
        int last = (end - 1) >> 6;
        int count = 0;
        for (int w = first; w <= last && w < mask.Length; w++)
        {
            ulong word = mask[w];
            if (w == first)
            {
                word &= ulong.MaxValue << (start & 63);
            }

            if (w == last)
            {
                int tail = end - (w << 6);
                if (tail < 64)
                {
                    word &= (1UL << tail) - 1;
                }
            }

            count += BitOperations.PopCount(word);
        }

        return count;
    }

    /// <summary>The rows both masks hold: one of them when the other is every row, their conjunction in <paramref name="scratch"/> otherwise.</summary>
    internal static ReadOnlySpan<ulong> And(ReadOnlySpan<ulong> left, ReadOnlySpan<ulong> right, int rows, ref ulong[] scratch)
    {
        if (left.IsEmpty)
        {
            return right;
        }

        if (right.IsEmpty)
        {
            return left;
        }

        int words = (rows + 63) >> 6;
        Scratch.Grow(ref scratch, words);
        Span<ulong> into = scratch.AsSpan(0, words);
        for (int w = 0; w < words; w++)
        {
            ulong l = w < left.Length ? left[w] : 0;
            ulong r = w < right.Length ? right[w] : 0;
            into[w] = l & r;
        }

        return into;
    }

    /// <summary>The rows of <paramref name="mask"/> inside [<paramref name="start"/>, <paramref name="end"/>), in <paramref name="scratch"/>.</summary>
    internal static ReadOnlySpan<ulong> Clip(ReadOnlySpan<ulong> mask, int rows, int start, int end, ref ulong[] scratch, out int count)
    {
        int words = (rows + 63) >> 6;
        Scratch.Grow(ref scratch, words);
        Span<ulong> into = scratch.AsSpan(0, words);
        into.Clear();
        count = 0;
        if (end <= start)
        {
            return into;
        }

        int first = start >> 6;
        int last = (end - 1) >> 6;
        for (int w = first; w <= last; w++)
        {
            ulong word = mask.IsEmpty ? ulong.MaxValue : (w < mask.Length ? mask[w] : 0);
            if (w == first)
            {
                word &= ulong.MaxValue << (start & 63);
            }

            if (w == last)
            {
                int tail = end - (w << 6);
                if (tail < 64)
                {
                    word &= (1UL << tail) - 1;
                }
            }

            into[w] = word;
            count += BitOperations.PopCount(word);
        }

        return into;
    }
}

/// <summary>Buffers a slot keeps from one batch to the next, grown and never shrunk, so a batch allocates nothing once warm.</summary>
internal static class Scratch
{
    /// <summary>Makes <paramref name="array"/> hold at least <paramref name="length"/> elements; a grown array starts empty, its old content is not copied.</summary>
    internal static void Grow<T>(ref T[] array, int length)
    {
        if (array.Length < length)
        {
            array = new T[Math.Max(length, array.Length * 2)];
        }
    }
}
