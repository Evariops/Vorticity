using System;
using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using Vorticity.Arrays.Decoders.Canonical;

namespace Vorticity.Parquet.Reading;

/// <summary>The kernels that turn a batch's levels, a byte per entry, into the bits, offsets and sizes of its nodes.</summary>
/// <remarks>
/// A kernel compares 64 levels at a time against a level, a mask of 64 bits out of one or a few
/// vector comparisons, and works on the masks: a field's slots are where two masks meet, its
/// validity the bits of a third its slots select, and a list's elements a population count between
/// the slots. A branch is taken per slot at most, never per entry.
/// </remarks>
internal static class LevelKernels
{
    /// <summary>
    /// A field's slots among the levels: the entries whose repetition level is at most
    /// <paramref name="repetition"/> and whose definition level reaches <paramref name="definition"/>.
    /// Each one's bit of <paramref name="present"/> is set where its definition level reaches
    /// <paramref name="definedAt"/>, every byte of the bitmap written.
    /// </summary>
    /// <returns>The slots, which past <paramref name="length"/> are not marked.</returns>
    internal static int Slots(
        ReadOnlySpan<byte> rep, ReadOnlySpan<byte> def, int repetition, int definition, int definedAt, Span<byte> present, int length, out int valid)
    {
        int entries = def.Length;
        ref byte r = ref MemoryMarshal.GetReference(rep);
        ref byte d = ref MemoryMarshal.GetReference(def);
        int slots = 0;
        valid = 0;
        Appender bits = new(present);
        for (int i = 0; i < entries; i += 64)
        {
            int width = Math.Min(64, entries - i);
            ulong live = width == 64 ? ulong.MaxValue : (1UL << width) - 1;
            ulong slotBits = AtMost(ref r, i, width, repetition) & AtLeast(ref d, i, width, definition) & live;
            int count = BitOperations.PopCount(slotBits);
            if (slots + count > length)
            {
                return slots + count;
            }

            ulong packed = Extract(AtLeast(ref d, i, width, definedAt), slotBits);
            valid += BitOperations.PopCount(packed);
            slots += count;
            bits.Append(packed, count);
        }

        bits.Flush();
        return slots;
    }

    /// <summary>
    /// A list's or a map's slots among the levels, as <see cref="Slots"/> finds them: each one's
    /// offset and count of elements, and its bit of <paramref name="present"/>, when it is given, set
    /// where the slot is not null. An element is an entry whose repetition level is at most
    /// <paramref name="repeatedAt"/> and whose definition level reaches <paramref name="elementsAt"/>;
    /// a slot's are those from its entry to the next entry that starts a value of the holder.
    /// </summary>
    /// <remarks>
    /// One pass records, for every entry that starts a value of the holder, the elements before it:
    /// a trailing-zero count and a population count, no branch. The slots' offsets are those counts,
    /// and their sizes the differences between neighbours, the holder's null or empty values, which
    /// are no slots, stepped over where there are any.
    /// </remarks>
    /// <param name="rep">The entries' repetition levels.</param>
    /// <param name="def">The entries' definition levels.</param>
    /// <param name="repetition">The holder's repetition level: a value of it starts at an entry at most it.</param>
    /// <param name="definition">The holder's definition level: it holds a value at an entry that reaches it.</param>
    /// <param name="definedAt">The list's own definition level: it is not null at an entry that reaches it.</param>
    /// <param name="repeatedAt">The list's repetition level: a new element starts at an entry at most it.</param>
    /// <param name="elementsAt">The definition level from which the list holds an element.</param>
    /// <param name="present">A bit per slot, set where the list is not null; empty when not wanted.</param>
    /// <param name="offsets">Each slot's first element.</param>
    /// <param name="sizes">Each slot's elements.</param>
    /// <param name="scratch">Two ints per entry, which the kernel writes over.</param>
    /// <param name="valid">The slots not null.</param>
    /// <param name="total">The elements, which the list's child holds.</param>
    /// <param name="stray">
    /// Whether an entry repeats a list without being an element of it, or an element lies where no
    /// slot is open: what a well-formed file never holds.
    /// </param>
    /// <returns>The slots, other than <c>offsets.Length</c> when the levels do not hold that many.</returns>
    internal static int Lists(
        ReadOnlySpan<byte> rep, ReadOnlySpan<byte> def, int repetition, int definition, int definedAt, int repeatedAt, int elementsAt,
        Span<byte> present, Span<int> offsets, Span<int> sizes, Span<int> scratch, out int valid, out int total, out bool stray)
    {
        int entries = def.Length;
        ref byte r = ref MemoryMarshal.GetReference(rep);
        ref byte d = ref MemoryMarshal.GetReference(def);
        Span<int> marks = scratch[..entries];
        Span<int> empty = scratch.Slice(entries, entries);
        ref int mark = ref MemoryMarshal.GetReference(marks);
        bool marking = !present.IsEmpty;
        Appender bits = new(present);
        int starts = 0;
        int holes = 0;
        int count = 0;
        valid = 0;
        stray = false;
        for (int i = 0; i < entries; i += 64)
        {
            int width = Math.Min(64, entries - i);
            ulong live = width == 64 ? ulong.MaxValue : (1UL << width) - 1;
            ulong holder = AtMost(ref r, i, width, repetition) & live;
            ulong repeated = AtMost(ref r, i, width, repeatedAt) & live;
            ulong elements = repeated & AtLeast(ref d, i, width, elementsAt);

            // An entry that repeats at the list's level and is not one of its elements.
            stray |= (repeated & ~holder & ~elements) != 0;
            ulong defined = AtLeast(ref d, i, width, definition);
            ulong slots = holder & defined;
            ulong packed = Extract(AtLeast(ref d, i, width, definedAt), slots);
            valid += BitOperations.PopCount(packed);
            if (marking)
            {
                bits.Append(packed, BitOperations.PopCount(slots));
            }

            // The holder's null or empty values, which start no slot: rare, and noted by their place.
            for (ulong none = holder & ~defined; none != 0; none &= none - 1)
            {
                empty[holes++] = starts + BitOperations.PopCount(holder & ((1UL << BitOperations.TrailingZeroCount(none)) - 1));
            }

            for (ulong at = holder; at != 0; at &= at - 1)
            {
                Unsafe.Add(ref mark, starts++) = count + BitOperations.PopCount(elements & ((1UL << BitOperations.TrailingZeroCount(at)) - 1));
            }

            count += BitOperations.PopCount(elements);
        }

        total = count;
        int found = starts - holes;
        if (found != offsets.Length)
        {
            return found;
        }

        if (marking)
        {
            bits.Flush();
        }

        // Each slot's elements run to the next value of the holder, or to the last element.
        int assigned = 0;
        if (holes == 0)
        {
            marks[..found].CopyTo(offsets);
            for (int k = 0; k < found - 1; k++)
            {
                sizes[k] = marks[k + 1] - marks[k];
            }

            if (found > 0)
            {
                sizes[found - 1] = count - marks[found - 1];
                assigned = count - marks[0];
            }
        }
        else
        {
            int slot = 0;
            int hole = 0;
            for (int j = 0; j < starts; j++)
            {
                int end = j + 1 < starts ? marks[j + 1] : count;
                if (hole < holes && empty[hole] == j)
                {
                    hole++;
                    continue;
                }

                offsets[slot] = marks[j];
                sizes[slot] = end - marks[j];
                assigned += sizes[slot++];
            }
        }

        stray |= assigned != count;
        return found;
    }

    /// <summary>
    /// The position of the zero <paramref name="n"/> zeros precede, or -1 when there are not that
    /// many: where a row starts, in repetition levels. Thirty-two levels are counted at a time, and
    /// the zero sought is found inside the vector that holds it.
    /// </summary>
    internal static int NthZero(ReadOnlySpan<byte> levels, int n)
    {
        int length = levels.Length;
        int i = 0;
        ref byte input = ref MemoryMarshal.GetReference(levels);
        if (Vector256.IsHardwareAccelerated)
        {
            for (; i <= length - Vector256<byte>.Count; i += Vector256<byte>.Count)
            {
                uint zeros = Vector256.Equals(Vector256.LoadUnsafe(ref input, (nuint)i), Vector256<byte>.Zero).ExtractMostSignificantBits();
                int count = BitOperations.PopCount(zeros);
                if (n < count)
                {
                    return i + Select(zeros, n);
                }

                n -= count;
            }
        }

        if (Vector128.IsHardwareAccelerated)
        {
            for (; i <= length - Vector128<byte>.Count; i += Vector128<byte>.Count)
            {
                uint zeros = Vector128.Equals(Vector128.LoadUnsafe(ref input, (nuint)i), Vector128<byte>.Zero).ExtractMostSignificantBits();
                int count = BitOperations.PopCount(zeros);
                if (n < count)
                {
                    return i + Select(zeros, n);
                }

                n -= count;
            }
        }

        for (; i < length; i++)
        {
            if (levels[i] == 0)
            {
                if (n == 0)
                {
                    return i;
                }

                n--;
            }
        }

        return -1;
    }

    /// <summary>Bit k set where the level at <paramref name="i"/> + k is at most <paramref name="max"/>, for <paramref name="width"/> levels, at most 64.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong AtMost(ref byte levels, int i, int width, int max)
    {
        if (max >= byte.MaxValue)
        {
            return ulong.MaxValue;
        }

        if (max < 0)
        {
            return 0;
        }

        byte limit = (byte)max;
        if (width == 64)
        {
            if (Vector512.IsHardwareAccelerated)
            {
                return Vector512.LessThanOrEqual(Vector512.LoadUnsafe(ref levels, (nuint)i), Vector512.Create(limit)).ExtractMostSignificantBits();
            }

            if (Vector256.IsHardwareAccelerated)
            {
                Vector256<byte> bound = Vector256.Create(limit);
                ulong low = Vector256.LessThanOrEqual(Vector256.LoadUnsafe(ref levels, (nuint)i), bound).ExtractMostSignificantBits();
                ulong high = Vector256.LessThanOrEqual(Vector256.LoadUnsafe(ref levels, (nuint)(i + 32)), bound).ExtractMostSignificantBits();
                return low | (high << 32);
            }

            if (Vector128.IsHardwareAccelerated)
            {
                Vector128<byte> bound = Vector128.Create(limit);
                ulong mask = 0;
                for (int part = 0; part < 4; part++)
                {
                    mask |= (ulong)Vector128.LessThanOrEqual(Vector128.LoadUnsafe(ref levels, (nuint)(i + (16 * part))), bound).ExtractMostSignificantBits() << (16 * part);
                }

                return mask;
            }
        }

        ulong bits = 0;
        for (int k = 0; k < width; k++)
        {
            if (Unsafe.Add(ref levels, i + k) <= limit)
            {
                bits |= 1UL << k;
            }
        }

        return bits;
    }

    /// <summary>Bit k set where the level at <paramref name="i"/> + k reaches <paramref name="min"/>; the bits past <paramref name="width"/> are any.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong AtLeast(ref byte levels, int i, int width, int min) =>
        min <= 0 ? ulong.MaxValue : ~AtMost(ref levels, i, width, min - 1);

    /// <summary>The bits of <paramref name="value"/> that <paramref name="mask"/> selects, packed from the bottom.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong Extract(ulong value, ulong mask)
    {
        if (mask == ulong.MaxValue)
        {
            return value;
        }

        if (Bmi2.X64.IsSupported)
        {
            return Bmi2.X64.ParallelBitExtract(value, mask);
        }

        ulong packed = 0;
        int k = 0;
        for (; mask != 0; mask &= mask - 1)
        {
            packed |= ((value >> BitOperations.TrailingZeroCount(mask)) & 1) << k++;
        }

        return packed;
    }

    /// <summary>Bits appended to a bitmap a word at a time, every byte of it written by the flush.</summary>
    private ref struct Appender(Span<byte> bits)
    {
        private readonly Span<byte> _bits = bits;
        private ulong _pending;
        private int _fill;
        private int _word;
        private int _count;

        /// <summary>Appends the low <paramref name="count"/> bits of <paramref name="packed"/>, the rest of which are clear.</summary>
        internal void Append(ulong packed, int count)
        {
            _count += count;
            _pending |= packed << _fill;
            if (_fill + count >= 64)
            {
                BinaryPrimitives.WriteUInt64LittleEndian(_bits.Slice(_word * sizeof(ulong), sizeof(ulong)), _pending);
                _word++;
                _pending = _fill == 0 ? 0 : packed >> (64 - _fill);
                _fill += count - 64;
            }
            else
            {
                _fill += count;
            }
        }

        /// <summary>Writes the bitmap's last bytes.</summary>
        internal readonly void Flush()
        {
            int bytes = CanonicalSupport.BitmapByteCount(_count) - (_word * sizeof(ulong));
            for (int b = 0; b < bytes; b++)
            {
                _bits[(_word * sizeof(ulong)) + b] = (byte)(_pending >> (8 * b));
            }
        }
    }

    /// <summary>The position of the set bit of <paramref name="mask"/> that <paramref name="n"/> set bits precede.</summary>
    private static int Select(uint mask, int n)
    {
        if (Bmi2.IsSupported)
        {
            return BitOperations.TrailingZeroCount(Bmi2.ParallelBitDeposit(1u << n, mask));
        }

        for (; n > 0; n--)
        {
            mask &= mask - 1;
        }

        return BitOperations.TrailingZeroCount(mask);
    }
}
