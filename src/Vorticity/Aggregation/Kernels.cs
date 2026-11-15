using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using Vorticity.Buffers;

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
        if (Avx2.IsSupported)
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
        if (Avx2.IsSupported)
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
    /// The sum of whole 32-byte blocks of <paramref name="values"/>, each byte xor-ed with
    /// <paramref name="bias"/> first, and how many bytes that was; the caller has AVX2.
    /// </summary>
    /// <remarks>
    /// <c>vpsadbw</c> against zero sums a block's eight bytes of each 64-bit lane into it, 64 bytes
    /// an instruction with AVX-512 BW and 32 with AVX2, into lanes wide enough never to overflow;
    /// the ladder of widens it replaces took four and had to be cut into blocks before a narrow
    /// lane could.
    /// </remarks>
    private static int ByteSums(ReadOnlySpan<byte> values, byte bias, out ulong sum)
    {
        ref byte first = ref MemoryMarshal.GetReference(values);
        ulong total = 0;
        int i = 0;
        if (Vector512.IsHardwareAccelerated && Avx512BW.IsSupported)
        {
            Vector512<byte> flip = Vector512.Create(bias);
            Vector512<ulong> a = Vector512<ulong>.Zero;
            Vector512<ulong> b = Vector512<ulong>.Zero;
            for (; i <= values.Length - 128; i += 128)
            {
                a += Avx512BW.SumAbsoluteDifferences(Vector512.LoadUnsafe(ref first, (nuint)i) ^ flip, Vector512<byte>.Zero).AsUInt64();
                b += Avx512BW.SumAbsoluteDifferences(Vector512.LoadUnsafe(ref first, (nuint)(i + 64)) ^ flip, Vector512<byte>.Zero).AsUInt64();
            }

            for (; i <= values.Length - 64; i += 64)
            {
                a += Avx512BW.SumAbsoluteDifferences(Vector512.LoadUnsafe(ref first, (nuint)i) ^ flip, Vector512<byte>.Zero).AsUInt64();
            }

            total = Vector512.Sum(a + b);
        }

        // AVX2's blocks, or the one 32-byte block the wide ones left.
        Vector256<byte> flip256 = Vector256.Create(bias);
        Vector256<ulong> c = Vector256<ulong>.Zero;
        Vector256<ulong> d = Vector256<ulong>.Zero;
        for (; i <= values.Length - 64; i += 64)
        {
            c += Avx2.SumAbsoluteDifferences(Vector256.LoadUnsafe(ref first, (nuint)i) ^ flip256, Vector256<byte>.Zero).AsUInt64();
            d += Avx2.SumAbsoluteDifferences(Vector256.LoadUnsafe(ref first, (nuint)(i + 32)) ^ flip256, Vector256<byte>.Zero).AsUInt64();
        }

        for (; i <= values.Length - 32; i += 32)
        {
            c += Avx2.SumAbsoluteDifferences(Vector256.LoadUnsafe(ref first, (nuint)i) ^ flip256, Vector256<byte>.Zero).AsUInt64();
        }

        sum = total + Vector256.Sum(c + d);
        return i;
    }

    /// <summary>
    /// The sum of whole 16-value blocks of <paramref name="values"/>, each xor-ed with
    /// <paramref name="bias"/> first, and how many values that was; the caller has AVX2.
    /// </summary>
    /// <remarks>
    /// <c>vpmaddwd</c> against ones adds each pair of words into a doubleword, 32 values an
    /// instruction with AVX-512 BW and 16 with AVX2; a doubleword gains at most 65 536 a step, so
    /// the lanes are widened and summed every <see cref="NarrowBlock"/> steps, before one could
    /// overflow.
    /// </remarks>
    private static int PairSums(ReadOnlySpan<short> values, ushort bias, out long sum)
    {
        ref short first = ref MemoryMarshal.GetReference(values);
        long total = 0;
        int i = 0;
        if (Vector512.IsHardwareAccelerated && Avx512BW.IsSupported)
        {
            Vector512<short> flip = Vector512.Create((short)bias);
            Vector512<short> ones = Vector512.Create((short)1);
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
        }

        // AVX2's blocks, two a step, or the one 16-value block the wide ones left; each of the two
        // accumulators takes a block a step, so both stay within NarrowBlock steps between widens.
        Vector256<short> flip256 = Vector256.Create((short)bias);
        Vector256<short> ones256 = Vector256.Create((short)1);
        int pairs = values.Length & ~31;
        while (i < pairs)
        {
            int stop = Math.Min(pairs, i + (32 * NarrowBlock));
            Vector256<int> a = Vector256<int>.Zero;
            Vector256<int> b = Vector256<int>.Zero;
            for (; i < stop; i += 32)
            {
                a += Avx2.MultiplyAddAdjacent(Vector256.LoadUnsafe(ref first, (nuint)i) ^ flip256, ones256);
                b += Avx2.MultiplyAddAdjacent(Vector256.LoadUnsafe(ref first, (nuint)(i + 16)) ^ flip256, ones256);
            }

            (Vector256<long> al, Vector256<long> ah) = Vector256.Widen(a);
            (Vector256<long> bl, Vector256<long> bh) = Vector256.Widen(b);
            total += Vector256.Sum((al + ah) + (bl + bh));
        }

        if (i <= values.Length - 16)
        {
            (Vector256<long> low, Vector256<long> high) = Vector256.Widen(Avx2.MultiplyAddAdjacent(Vector256.LoadUnsafe(ref first, (nuint)i) ^ flip256, ones256));
            total += Vector256.Sum(low + high);
            i += 16;
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
        if (Avx2.IsSupported)
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
        if (Avx2.IsSupported)
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
                if (acc[lane] < best || ExtremeOps.ZeroBelow(acc[lane], best))
                {
                    best = acc[lane];
                }
            }
        }

        // A NaN compares false with everything, so the scalar tail skips it without a test. -0.0 is below +0.0, as
        // Vector.Min and IEEE 754's minimum order them, so the least of zeros does not depend on where they lie.
        for (; i < values.Length; i++)
        {
            T value = values[i];
            if (value < best || ExtremeOps.ZeroBelow(value, best))
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
                if (acc[lane] > best || ExtremeOps.ZeroAbove(acc[lane], best))
                {
                    best = acc[lane];
                }
            }
        }

        for (; i < values.Length; i++)
        {
            T value = values[i];
            if (value > best || ExtremeOps.ZeroAbove(value, best))
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
    internal static ReadOnlySpan<ulong> And(ReadOnlySpan<ulong> left, ReadOnlySpan<ulong> right, int rows, ref ScratchBlock<ulong> scratch)
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
    internal static Selection Window(ReadOnlySpan<ulong> mask, int rows, int start, int end, ref ScratchBlock<ulong> scratch)
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
    internal static void Unclip(ScratchBlock<ulong> scratch, int start, int end)
    {
        if (end > start)
        {
            int first = start >> 6;
            scratch.AsSpan(first, ((end - 1) >> 6) - first + 1).Clear();
        }
    }
}

/// <summary>
/// Buffers a slot keeps from one batch to the next, grown and never shrunk, so a batch allocates nothing
/// once warm: blocks of the pool, a power of two long, the one they replace given back to it, so that the
/// next query's grow into the bytes this one's left.
/// </summary>
internal static class Scratch
{
    /// <summary>Makes <paramref name="block"/> hold at least <paramref name="length"/> elements; a grown block starts zeroed, its old content is not copied.</summary>
    internal static void Grow<T>(ref ScratchBlock<T> block, int length)
        where T : unmanaged
    {
        if (block.Length < length)
        {
            Blocks.ReturnBytes(block.Bytes);
            block = ScratchBlock<T>.Rent(Capacity(length, block.Length));
        }
    }

    /// <summary>Gives <paramref name="block"/> back to the pool, its owner done with it, and leaves it empty.</summary>
    internal static void Return<T>(ref ScratchBlock<T> block)
        where T : unmanaged
    {
        Blocks.ReturnBytes(block.Bytes);
        block = default;
    }

    /// <summary>
    /// The length an array of <paramref name="current"/> elements grows to so that it holds
    /// <paramref name="needed"/>: twice its length at least, rounded up to a power of two. A group by
    /// that streams holds, at each batch, the open group of the last besides its own: sized exactly
    /// by the first batch, every array would grow again at the second.
    /// </summary>
    internal static int Capacity(int needed, int current)
    {
        long wanted = Math.Max(needed, 2L * current);
        return wanted > 1L << 30 ? Array.MaxLength : (int)BitOperations.RoundUpToPowerOf2((uint)wanted);
    }
}

/// <summary>
/// A block of the pool a holder keeps as scratch from one batch to the next, held as its bytes alone: a field of 8 bytes
/// where a <see cref="Block{T}"/> takes 16, on the slots, keys and masks a query makes for every lane and every sub-table
/// of its core, by the thousand. Its length is every element its bytes hold, as a scratch's is its capacity.
/// </summary>
/// <typeparam name="T">The element type.</typeparam>
[DebuggerDisplay("ScratchBlock<{typeof(T).Name,nq}>[{Length}]")]
internal readonly struct ScratchBlock<T>
    where T : unmanaged
{
    private readonly byte[]? _bytes;

    private ScratchBlock(byte[]? bytes) => _bytes = bytes;

    /// <summary>A block of <paramref name="length"/> elements at least, from the pool, every byte zeroed.</summary>
    internal static ScratchBlock<T> Rent(int length)
    {
        byte[]? bytes = Blocks.Rent<byte>(checked(length * Unsafe.SizeOf<T>())).Bytes;
        bytes?.AsSpan().Clear();
        return new ScratchBlock<T>(bytes);
    }

    /// <summary>Every element the bytes hold.</summary>
    public int Length => _bytes is null ? 0 : _bytes.Length / Unsafe.SizeOf<T>();

    /// <summary>Whether the scratch holds no bytes: never grown, or given back.</summary>
    internal bool IsDefault => _bytes is null;

    /// <summary>The bytes, which the pool takes back.</summary>
    internal byte[]? Bytes => _bytes;

    /// <summary>The scratch as a block of <see cref="Length"/> elements.</summary>
    internal Block<T> Block => Block<T>.Over(_bytes, Length);

    /// <summary>Element <paramref name="index"/>.</summary>
    /// <exception cref="IndexOutOfRangeException"><paramref name="index"/> lies past <see cref="Length"/>.</exception>
    public ref T this[int index]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            byte[]? bytes = _bytes;
            if (bytes is null || ((ulong)(uint)index + 1) * (ulong)Unsafe.SizeOf<T>() > (ulong)bytes.Length)
            {
                ThrowIndex();
            }

            return ref Unsafe.Add(ref Unsafe.As<byte, T>(ref MemoryMarshal.GetArrayDataReference(bytes!)), (nint)(uint)index);
        }
    }

    /// <summary>Every element, as a span.</summary>
    public Span<T> AsSpan() => _bytes is null ? default : MemoryMarshal.Cast<byte, T>(_bytes.AsSpan(0, Length * Unsafe.SizeOf<T>()));

    /// <summary>The elements from <paramref name="start"/> on, as a span.</summary>
    public Span<T> AsSpan(int start) => AsSpan()[start..];

    /// <summary><paramref name="length"/> elements from <paramref name="start"/>, as a span.</summary>
    public Span<T> AsSpan(int start, int length) => AsSpan().Slice(start, length);

    /// <summary>A reference to element 0; for a vector load from a computed offset.</summary>
    /// <exception cref="NullReferenceException">The scratch holds no bytes.</exception>
    public ref T GetReference() => ref Unsafe.As<byte, T>(ref MemoryMarshal.GetArrayDataReference(_bytes!));

    /// <summary>Zeroes every element.</summary>
    public void Clear() => _bytes?.AsSpan().Clear();

    public static implicit operator Span<T>(ScratchBlock<T> block) => block.AsSpan();

    public static implicit operator ReadOnlySpan<T>(ScratchBlock<T> block) => block.AsSpan();

    public static implicit operator Block<T>(ScratchBlock<T> block) => block.Block;

    [DoesNotReturn]
    private static void ThrowIndex() => throw new IndexOutOfRangeException();
}

/// <summary>
/// Values of <typeparamref name="T"/> a holder keeps from one call to the next, grown and never shrunk, for a type that
/// may hold references: the bytes of a block of the pool when it holds none, an array of the holder's own when it does,
/// or when a record reads the values as an array, which no block can be. One field, the bytes or the array.
/// </summary>
/// <typeparam name="T">The values' type.</typeparam>
internal struct ValueScratch<T>
{
    private Array? _held;

    /// <summary>Every value the scratch has room for, as many as it was grown to at least.</summary>
    internal readonly Span<T> Values
    {
        get
        {
            Array? held = _held;
            if (held is null)
            {
                return default;
            }

            if (Owns(held))
            {
                return Unsafe.As<T[]>(held);
            }

            byte[] bytes = Unsafe.As<byte[]>(held);
            return MemoryMarshal.CreateSpan(ref Unsafe.As<byte, T>(ref MemoryMarshal.GetArrayDataReference(bytes)), bytes.Length / Unsafe.SizeOf<T>());
        }
    }

    /// <summary>The values as the array of the holder's own they lie in, for a record that reads them so: the scratch grown <c>owned</c>.</summary>
    internal readonly T[] Owned => (T[])_held!;

    /// <summary>
    /// Room for <paramref name="length"/> values at least, reserved under <paramref name="memory"/> for
    /// <paramref name="what"/> when given; what the scratch held let go first, the new values zeroed. An array of the
    /// holder's own when <paramref name="owned"/>, for a record that reads the values as one.
    /// </summary>
    /// <exception cref="VortexMemoryException">The query's budget does not grant the room.</exception>
    internal void Grow(int length, bool owned = false, QueryMemory? memory = null, string what = "")
    {
        int held = Values.Length;
        if (length <= held)
        {
            return;
        }

        Return(memory);
        int capacity = Scratch.Capacity(length, held);
        long bytes = (long)capacity * Unsafe.SizeOf<T>();
        if (owned || RuntimeHelpers.IsReferenceOrContainsReferences<T>())
        {
            if (memory is not null && bytes >= Blocks.LeastCounted)
            {
                if (!memory.TryGrow(bytes))
                {
                    throw memory.Exceeded(what, -1, bytes);
                }

                memory.Measure(bytes);
            }

            _held = new T[capacity];
            return;
        }

        byte[] rented = Blocks.Rent<byte>(memory, checked((int)bytes), what).Bytes!;
        rented.AsSpan().Clear();
        _held = rented;
    }

    /// <summary>Lets the values go, the pool's bytes given back, and what was reserved under <paramref name="memory"/> with them.</summary>
    internal void Return(QueryMemory? memory = null)
    {
        Array? held = _held;
        _held = null;
        if (held is null)
        {
            return;
        }

        if (Owns(held) && typeof(T) != typeof(byte))
        {
            long bytes = (long)held.Length * Unsafe.SizeOf<T>();
            if (memory is not null && bytes >= Blocks.LeastCounted)
            {
                memory.Shrink(bytes);
                memory.Measure(-bytes);
            }

            return;
        }

        Blocks.Return(memory, Block<byte>.Over(Unsafe.As<byte[]>(held), held.Length));
    }

    /// <summary>Whether <paramref name="held"/> is an array of the values' own type: always for a type that holds references; for one that holds none, an array grown owned.</summary>
    private static bool Owns(Array held) => RuntimeHelpers.IsReferenceOrContainsReferences<T>() || held.GetType() == typeof(T[]);
}
