using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

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
        if (Vector512.IsHardwareAccelerated && Avx512BW.IsSupported)
        {
            i = PairSums(values, 0, out total);
        }
        else if (Vector.IsHardwareAccelerated)
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
        if (Vector512.IsHardwareAccelerated && Avx512BW.IsSupported)
        {
            // Each byte biased by 128 is the unsigned byte `vpsadbw` sums; the bias comes off at
            // the end, 128 a value.
            i = ByteSums(MemoryMarshal.AsBytes(values), 0x80, out ulong biased);
            total = (long)biased - (128L * i);
        }
        else if (Vector.IsHardwareAccelerated)
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

    /// <summary>
    /// The sum of whole 64-byte blocks of <paramref name="values"/>, each byte xor-ed with
    /// <paramref name="bias"/> first, and how many bytes that was.
    /// </summary>
    /// <remarks>
    /// <c>vpsadbw</c> against zero sums a block's eight bytes of each 64-bit lane into it, 64 bytes
    /// an instruction, into lanes wide enough never to overflow; the ladder of widens it replaces
    /// took four and had to be cut into blocks before a narrow lane could.
    /// </remarks>
    private static int ByteSums(ReadOnlySpan<byte> values, byte bias, out ulong sum)
    {
        ref byte first = ref MemoryMarshal.GetReference(values);
        Vector512<byte> flip = Vector512.Create(bias);
        Vector512<ulong> a = Vector512<ulong>.Zero;
        Vector512<ulong> b = Vector512<ulong>.Zero;
        int i = 0;
        for (; i <= values.Length - 128; i += 128)
        {
            a += Avx512BW.SumAbsoluteDifferences(Vector512.LoadUnsafe(ref first, (nuint)i) ^ flip, Vector512<byte>.Zero).AsUInt64();
            b += Avx512BW.SumAbsoluteDifferences(Vector512.LoadUnsafe(ref first, (nuint)(i + 64)) ^ flip, Vector512<byte>.Zero).AsUInt64();
        }

        for (; i <= values.Length - 64; i += 64)
        {
            a += Avx512BW.SumAbsoluteDifferences(Vector512.LoadUnsafe(ref first, (nuint)i) ^ flip, Vector512<byte>.Zero).AsUInt64();
        }

        sum = Vector512.Sum(a + b);
        return i;
    }

    /// <summary>
    /// The sum of whole 32-value blocks of <paramref name="values"/>, each xor-ed with
    /// <paramref name="bias"/> first, and how many values that was.
    /// </summary>
    /// <remarks>
    /// <c>vpmaddwd</c> against ones adds each pair of words into a doubleword, 32 values an
    /// instruction; a doubleword gains at most 65 536 a step, so the lanes are widened and summed
    /// every <see cref="NarrowBlock"/> steps, before one could overflow.
    /// </remarks>
    private static int PairSums(ReadOnlySpan<short> values, ushort bias, out long sum)
    {
        ref short first = ref MemoryMarshal.GetReference(values);
        Vector512<short> flip = Vector512.Create((short)bias);
        Vector512<short> ones = Vector512.Create((short)1);
        long total = 0;
        int i = 0;
        int whole = values.Length & ~31;
        while (i < whole)
        {
            int stop = Math.Min(whole, i + (32 * NarrowBlock));
            Vector512<int> acc = Vector512<int>.Zero;
            for (; i < stop; i += 32)
            {
                acc += Avx512BW.MultiplyAddAdjacent(Vector512.LoadUnsafe(ref first, (nuint)i) ^ flip, ones);
            }

            (Vector512<long> low, Vector512<long> high) = Vector512.Widen(acc);
            total += Vector512.Sum(low + high);
        }

        sum = total;
        return i;
    }

    private static Int128 Int64(ReadOnlySpan<long> values)
    {
        Int128 total = 0;
        int i = 0;
        if (Vector512.IsHardwareAccelerated && values.Length >= 16)
        {
            // Each value as its high half, signed, and its low half, unsigned: a lane of 64 bits
            // takes 2^31 halves before it can overflow, which no span holds, so the sum is exact in
            // one pass whatever the values, where a sum of whole values had to be taken again in
            // 128 bits, a value at a time, once a lane left 64 bits.
            ref long first = ref MemoryMarshal.GetReference(values);
            Vector512<long> mask = Vector512.Create(0xFFFF_FFFFL);
            Vector512<long> high = Vector512<long>.Zero;
            Vector512<long> low = Vector512<long>.Zero;
            for (; i <= values.Length - 16; i += 16)
            {
                Vector512<long> a = Vector512.LoadUnsafe(ref first, (nuint)i);
                Vector512<long> b = Vector512.LoadUnsafe(ref first, (nuint)(i + 8));
                high += Vector512.ShiftRightArithmetic(a, 32) + Vector512.ShiftRightArithmetic(b, 32);
                low += (a & mask) + (b & mask);
            }

            // Summed across, the high halves stay within 2^62 either way and the low ones under 2^63.
            total = ((Int128)Vector512.Sum(high) << 32) + (ulong)Vector512.Sum(low);
        }
        else if (Vector.IsHardwareAccelerated && values.Length >= Vector<long>.Count)
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
        if (Vector512.IsHardwareAccelerated && Avx512BW.IsSupported)
        {
            // Each value less 32 768 is the signed word `vpmaddwd` adds in pairs; the bias comes
            // back at the end, 32 768 a value.
            i = PairSums(MemoryMarshal.Cast<ushort, short>(values), 0x8000, out long biased);
            total = (ulong)(biased + (32768L * i));
        }
        else if (Vector.IsHardwareAccelerated)
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
        if (Vector512.IsHardwareAccelerated && Avx512BW.IsSupported)
        {
            i = ByteSums(values, 0, out total);
        }
        else if (Vector.IsHardwareAccelerated)
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
        if (Vector512.IsHardwareAccelerated && values.Length >= 16)
        {
            // Each value as its two halves, exact in one pass whatever the values, as the signed sum.
            ref ulong first = ref MemoryMarshal.GetReference(values);
            Vector512<ulong> mask = Vector512.Create(0xFFFF_FFFFUL);
            Vector512<ulong> high = Vector512<ulong>.Zero;
            Vector512<ulong> low = Vector512<ulong>.Zero;
            for (; i <= values.Length - 16; i += 16)
            {
                Vector512<ulong> a = Vector512.LoadUnsafe(ref first, (nuint)i);
                Vector512<ulong> b = Vector512.LoadUnsafe(ref first, (nuint)(i + 8));
                high += Vector512.ShiftRightLogical(a, 32) + Vector512.ShiftRightLogical(b, 32);
                low += (a & mask) + (b & mask);
            }

            total = ((UInt128)Vector512.Sum(high) << 32) + Vector512.Sum(low);
        }
        else if (Vector.IsHardwareAccelerated && values.Length >= Vector<ulong>.Count)
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

    /// <summary>A vector's numbers, a NaN lane zero and counted in <paramref name="nan"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector512<double> Numbers(Vector512<double> v, ref Vector512<long> nan)
    {
        Vector512<double> number = Vector512.Equals(v, v);
        nan += Vector512<long>.One + number.AsInt64();
        return v & number;
    }

    private static double Double(ReadOnlySpan<double> values, out long counted)
    {
        double total = 0;
        long skipped = 0;
        int i = 0;
        if (Vector512.IsHardwareAccelerated && values.Length >= 32)
        {
            // Four sums, so that no addition waits on the one before: one sum is held to the
            // latency of a floating add a vector. The order of the additions is this loop's, as it
            // was the narrower vectors' before, and a total's last bits follow it.
            //
            // A NaN among the values makes the sum NaN, so the sum is taken first as if there were
            // none, and again with each NaN lane zeroed and counted only when it came out NaN --
            // where an infinity met its opposite as well, whose second sum is NaN again. Zeroed
            // lanes add nothing, so the two sums agree to the bit where both are taken.
            ref double first = ref MemoryMarshal.GetReference(values);
            int whole = values.Length & ~31;
            Vector512<double> a = Vector512<double>.Zero;
            Vector512<double> b = Vector512<double>.Zero;
            Vector512<double> c = Vector512<double>.Zero;
            Vector512<double> d = Vector512<double>.Zero;
            for (; i < whole; i += 32)
            {
                a += Vector512.LoadUnsafe(ref first, (nuint)i);
                b += Vector512.LoadUnsafe(ref first, (nuint)(i + 8));
                c += Vector512.LoadUnsafe(ref first, (nuint)(i + 16));
                d += Vector512.LoadUnsafe(ref first, (nuint)(i + 24));
            }

            total = Vector512.Sum((a + b) + (c + d));
            if (double.IsNaN(total))
            {
                a = b = c = d = Vector512<double>.Zero;
                Vector512<long> nan = Vector512<long>.Zero;
                for (i = 0; i < whole; i += 32)
                {
                    a += Numbers(Vector512.LoadUnsafe(ref first, (nuint)i), ref nan);
                    b += Numbers(Vector512.LoadUnsafe(ref first, (nuint)(i + 8)), ref nan);
                    c += Numbers(Vector512.LoadUnsafe(ref first, (nuint)(i + 16)), ref nan);
                    d += Numbers(Vector512.LoadUnsafe(ref first, (nuint)(i + 24)), ref nan);
                }

                total = Vector512.Sum((a + b) + (c + d));
                skipped = Vector512.Sum(nan);
            }
        }
        else if (Vector.IsHardwareAccelerated && values.Length >= Vector<double>.Count)
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
        if (Vector512.IsHardwareAccelerated && Avx512F.IsSupported && values.Length >= 32)
        {
            // Four loads of eight singles a step, each converted to eight doubles in one
            // instruction, into four sums, NaN or not first, as the doubles' loop: widening a
            // 512-bit load would take its upper half out first.
            ref float first = ref MemoryMarshal.GetReference(values);
            int whole = values.Length & ~31;
            Vector512<double> a = Vector512<double>.Zero;
            Vector512<double> b = Vector512<double>.Zero;
            Vector512<double> c = Vector512<double>.Zero;
            Vector512<double> d = Vector512<double>.Zero;
            for (; i < whole; i += 32)
            {
                a += Avx512F.ConvertToVector512Double(Vector256.LoadUnsafe(ref first, (nuint)i));
                b += Avx512F.ConvertToVector512Double(Vector256.LoadUnsafe(ref first, (nuint)(i + 8)));
                c += Avx512F.ConvertToVector512Double(Vector256.LoadUnsafe(ref first, (nuint)(i + 16)));
                d += Avx512F.ConvertToVector512Double(Vector256.LoadUnsafe(ref first, (nuint)(i + 24)));
            }

            total = Vector512.Sum((a + b) + (c + d));
            if (double.IsNaN(total))
            {
                a = b = c = d = Vector512<double>.Zero;
                Vector512<long> nan = Vector512<long>.Zero;
                for (i = 0; i < whole; i += 32)
                {
                    a += Numbers(Avx512F.ConvertToVector512Double(Vector256.LoadUnsafe(ref first, (nuint)i)), ref nan);
                    b += Numbers(Avx512F.ConvertToVector512Double(Vector256.LoadUnsafe(ref first, (nuint)(i + 8))), ref nan);
                    c += Numbers(Avx512F.ConvertToVector512Double(Vector256.LoadUnsafe(ref first, (nuint)(i + 16))), ref nan);
                    d += Numbers(Avx512F.ConvertToVector512Double(Vector256.LoadUnsafe(ref first, (nuint)(i + 24))), ref nan);
                }

                total = Vector512.Sum((a + b) + (c + d));
                skipped = Vector512.Sum(nan);
            }
        }
        else if (Vector.IsHardwareAccelerated && values.Length >= Vector<float>.Count)
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

    /// <summary>
    /// The rows of <paramref name="mask"/> inside [<paramref name="start"/>, <paramref name="end"/>), as a
    /// selection over <paramref name="scratch"/>: only the range's words are written, which every
    /// other word of the scratch leaves zero, and <see cref="Unclip"/> clears them again once the
    /// selection is read. A range costs its own words, not its block's.
    /// </summary>
    internal static Selection Window(ReadOnlySpan<ulong> mask, int rows, int start, int end, ref ulong[] scratch)
    {
        int words = (rows + 63) >> 6;
        Scratch.Grow(ref scratch, words);
        Span<ulong> into = scratch.AsSpan(0, words);
        if (end <= start)
        {
            return new Selection(into, rows, 0, 0, 0);
        }

        int first = start >> 6;
        int last = (end - 1) >> 6;
        int count = 0;
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

        return new Selection(into, rows, count, first, last + 1);
    }

    /// <summary>Gives back the words <see cref="Window"/> wrote for [<paramref name="start"/>, <paramref name="end"/>), cleared, as it found them.</summary>
    internal static void Unclip(ulong[] scratch, int start, int end)
    {
        if (end > start)
        {
            int first = start >> 6;
            scratch.AsSpan(first, ((end - 1) >> 6) - first + 1).Clear();
        }
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
