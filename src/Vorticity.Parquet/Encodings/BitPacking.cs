using System;
using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

namespace Vorticity.Parquet.Encodings;

/// <summary>
/// Parquet's bit packing: values of a fixed width laid end to end from the least significant bit of
/// each byte, as the RLE/bit-packing hybrid, the delta encodings' miniblocks and ALP store them.
/// </summary>
/// <remarks>
/// <para>
/// Eight values of width <c>w</c> take exactly <c>w</c> bytes, a group, which is the unit every
/// path walks. The scalar path reads a 64-bit word per value; for widths up to 8 a group is one
/// word, which <c>PDEP</c> spreads into eight bytes and <c>PEXT</c> gathers back. The vector paths
/// gather the bytes each value spans into its lane with a byte shuffle and shift each lane by its
/// own amount, which depends on the width alone and is laid out once per width.
/// </para>
/// <para>
/// No path reads past its source: the wide loads stop where fewer bytes remain than
/// they span, and the tail is read through a zero-padded copy.
/// </para>
/// </remarks>
internal static class BitPacking
{
    /// <summary>The bytes <paramref name="count"/> values of <paramref name="bitWidth"/> bits take.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static long PackedBytes(long count, int bitWidth) => (count * bitWidth + 7) >> 3;

    /// <summary>The widest a value of the vector paths may be: its bits, shifted by at most 7, stay within its 32-bit lane.</summary>
    private const int VectorWidth32 = 25;

    /// <summary>
    /// Unpacks <c>destination.Length</c> values of <paramref name="bitWidth"/> bits, 0 to 32, from
    /// <paramref name="source"/>, which holds at least <see cref="PackedBytes"/> of them.
    /// </summary>
    internal static void Unpack32(ReadOnlySpan<byte> source, int bitWidth, Span<uint> destination)
    {
        int count = destination.Length;
        if ((uint)bitWidth > 32)
        {
            ThrowWidth(bitWidth, 32);
        }

        if (source.Length < PackedBytes(count, bitWidth))
        {
            ParquetThrow.Truncated("bit-packed run");
        }

        if (bitWidth == 0)
        {
            destination.Clear();
            return;
        }

        if (bitWidth == 32)
        {
            MemoryMarshal.Cast<byte, uint>(source[..(count * 4)]).CopyTo(destination);
            if (!BitConverter.IsLittleEndian)
            {
                BinaryPrimitives.ReverseEndianness(destination, destination);
            }

            return;
        }

        int done = 0;
        if (bitWidth <= VectorWidth32)
        {
            if (Avx512Vbmi.IsSupported && Avx512F.IsSupported)
            {
                done = Unpack32Avx512(source, bitWidth, destination);
            }
            else if (Avx2.IsSupported)
            {
                done = Unpack32Avx2(source, bitWidth, destination);
            }
            else if (AdvSimd.Arm64.IsSupported)
            {
                done = Unpack32Neon(source, bitWidth, destination);
            }
        }

        Unpack32Scalar(source, bitWidth, destination, done);
    }

    /// <summary>
    /// Unpacks <c>destination.Length</c> values of <paramref name="bitWidth"/> bits, 0 to 8, one byte
    /// each: levels, and a dictionary's codes when it has at most 256 entries.
    /// </summary>
    internal static void Unpack8(ReadOnlySpan<byte> source, int bitWidth, Span<byte> destination)
    {
        int count = destination.Length;
        if ((uint)bitWidth > 8)
        {
            ThrowWidth(bitWidth, 8);
        }

        if (source.Length < PackedBytes(count, bitWidth))
        {
            ParquetThrow.Truncated("bit-packed run");
        }

        if (bitWidth == 0)
        {
            destination.Clear();
            return;
        }

        if (bitWidth == 8)
        {
            source[..count].CopyTo(destination);
            return;
        }

        // A group of eight values is bitWidth bytes, at most seven: one word holds it, and PDEP lays
        // each value in a byte of its own.
        int groups = count >> 3;
        int group = 0;
        ref byte input = ref MemoryMarshal.GetReference(source);
        ref byte output = ref MemoryMarshal.GetReference(destination);
        if (Bmi2.X64.IsSupported && BitConverter.IsLittleEndian)
        {
            ulong lanes = 0x0101010101010101UL * ((1UL << bitWidth) - 1);

            // Group g reads the 8 bytes from g × width, so it fits while g × width + 8 holds in the source.
            int safe = source.Length < 8 ? 0 : Math.Min(groups, (source.Length - 8) / bitWidth + 1);
            for (; group < safe; group++)
            {
                ulong word = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref input, group * bitWidth));
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref output, group * 8), Bmi2.X64.ParallelBitDeposit(word, lanes));
            }
        }

        ulong mask = (1UL << bitWidth) - 1;
        Span<byte> padded = stackalloc byte[8];
        for (; group < groups; group++)
        {
            ulong word = Word(source, group * bitWidth, padded);
            for (int j = 0; j < 8; j++)
            {
                Unsafe.Add(ref output, group * 8 + j) = (byte)((word >> (j * bitWidth)) & mask);
            }
        }

        int rest = count - groups * 8;
        if (rest > 0)
        {
            ulong word = Word(source, groups * bitWidth, padded);
            for (int j = 0; j < rest; j++)
            {
                Unsafe.Add(ref output, groups * 8 + j) = (byte)((word >> (j * bitWidth)) & mask);
            }
        }
    }

    /// <summary>
    /// Unpacks <c>destination.Length</c> values of <paramref name="bitWidth"/> bits, 0 to 64: the
    /// delta encodings' 64-bit miniblocks and ALP's doubles.
    /// </summary>
    internal static void Unpack64(ReadOnlySpan<byte> source, int bitWidth, Span<ulong> destination)
    {
        int count = destination.Length;
        if ((uint)bitWidth > 64)
        {
            ThrowWidth(bitWidth, 64);
        }

        if (source.Length < PackedBytes(count, bitWidth))
        {
            ParquetThrow.Truncated("bit-packed run");
        }

        if (bitWidth == 0)
        {
            destination.Clear();
            return;
        }

        if (bitWidth == 64)
        {
            MemoryMarshal.Cast<byte, ulong>(source[..(count * 8)]).CopyTo(destination);
            if (!BitConverter.IsLittleEndian)
            {
                BinaryPrimitives.ReverseEndianness(destination, destination);
            }

            return;
        }

        ulong mask = (1UL << bitWidth) - 1;
        ref byte input = ref MemoryMarshal.GetReference(source);
        int i = 0;

        // A value starts at most 7 bits into its first byte, so a width up to 57 fits one 64-bit
        // load; a wider one takes a second.
        long lastSafeByte = source.Length - 9;
        if (bitWidth <= 57)
        {
            for (; i < count; i++)
            {
                long bit = (long)i * bitWidth;
                long at = bit >> 3;
                if (at > lastSafeByte + 1)
                {
                    break;
                }

                ulong word = BinaryPrimitives.ReadUInt64LittleEndian(MemoryMarshal.CreateReadOnlySpan(ref Unsafe.Add(ref input, (nint)at), 8));
                destination[i] = (word >> (int)(bit & 7)) & mask;
            }
        }

        Span<byte> padded = stackalloc byte[16];
        for (; i < count; i++)
        {
            long bit = (long)i * bitWidth;
            int at = (int)(bit >> 3);
            int shift = (int)(bit & 7);
            int available = Math.Min(16, source.Length - at);
            padded.Clear();
            source.Slice(at, available).CopyTo(padded);
            ulong low = BinaryPrimitives.ReadUInt64LittleEndian(padded);
            ulong high = BinaryPrimitives.ReadUInt64LittleEndian(padded[8..]);
            ulong value = shift == 0 ? low : (low >> shift) | (high << (64 - shift));
            destination[i] = value & mask;
        }
    }

    /// <summary>
    /// Unpacks <c>destination.Length</c> deltas of <paramref name="bitWidth"/> bits, 1 to 57, and
    /// writes their running sum from <paramref name="last"/>, each delta raised by
    /// <paramref name="minimum"/>, wrapping: DELTA_BINARY_PACKED's INT64 miniblock in one pass, no
    /// delta stored between the unpack and the sum. Returns the last value written.
    /// </summary>
    /// <remarks>
    /// A value starts at most 7 bits into its first byte, so one 64-bit load holds it wherever eight
    /// bytes remain; a caller that passes the rest of its page keeps every value but the page's last
    /// few on that load.
    /// </remarks>
    internal static ulong UnpackSum64(ReadOnlySpan<byte> source, int bitWidth, ulong minimum, ulong last, Span<long> destination)
    {
        int count = destination.Length;
        if ((uint)(bitWidth - 1) > 56)
        {
            ThrowWidth(bitWidth, 57);
        }

        if (source.Length < PackedBytes(count, bitWidth))
        {
            ParquetThrow.Truncated("bit-packed run");
        }

        ulong mask = (1UL << bitWidth) - 1;
        ref byte input = ref MemoryMarshal.GetReference(source);
        ref long output = ref MemoryMarshal.GetReference(destination);
        int i = 0;
        if (BitConverter.IsLittleEndian)
        {
            long limit = source.Length - 8;
            for (; i < count; i++)
            {
                long bit = (long)i * bitWidth;
                long at = bit >> 3;
                if (at > limit)
                {
                    break;
                }

                ulong word = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref input, (nint)at));
                last += minimum + ((word >> (int)(bit & 7)) & mask);
                Unsafe.Add(ref output, i) = (long)last;
            }
        }

        Span<byte> padded = stackalloc byte[8];
        for (; i < count; i++)
        {
            long bit = (long)i * bitWidth;
            ulong word = Word(source, (int)(bit >> 3), padded);
            last += minimum + ((word >> (int)(bit & 7)) & mask);
            Unsafe.Add(ref output, i) = (long)last;
        }

        return last;
    }

    /// <summary>
    /// Packs <c>values</c>, each below 2^<paramref name="bitWidth"/>, into
    /// <see cref="PackedBytes"/> bytes of <paramref name="destination"/>; the last byte's unused
    /// bits are zero.
    /// </summary>
    internal static void Pack32(ReadOnlySpan<uint> values, int bitWidth, Span<byte> destination)
    {
        if ((uint)bitWidth > 32)
        {
            ThrowWidth(bitWidth, 32);
        }

        int bytes = (int)PackedBytes(values.Length, bitWidth);
        if (destination.Length < bytes)
        {
            throw new ArgumentException("The destination is shorter than the packed values.", nameof(destination));
        }

        if (bitWidth == 0)
        {
            return;
        }

        ulong buffer = 0;
        int filled = 0;
        int written = 0;
        foreach (uint value in values)
        {
            buffer |= (ulong)value << filled;
            filled += bitWidth;
            while (filled >= 8)
            {
                destination[written++] = (byte)buffer;
                buffer >>= 8;
                filled -= 8;
            }
        }

        if (filled > 0)
        {
            destination[written] = (byte)buffer;
        }
    }

    /// <summary>Packs byte-wide values of <paramref name="bitWidth"/> bits, 0 to 8: levels and small codes.</summary>
    internal static void Pack8(ReadOnlySpan<byte> values, int bitWidth, Span<byte> destination)
    {
        if ((uint)bitWidth > 8)
        {
            ThrowWidth(bitWidth, 8);
        }

        int count = values.Length;
        int bytes = (int)PackedBytes(count, bitWidth);
        if (destination.Length < bytes)
        {
            throw new ArgumentException("The destination is shorter than the packed values.", nameof(destination));
        }

        if (bitWidth == 0)
        {
            return;
        }

        int groups = count >> 3;
        int group = 0;
        if (Bmi2.X64.IsSupported && BitConverter.IsLittleEndian)
        {
            ulong lanes = 0x0101010101010101UL * ((1UL << bitWidth) - 1);
            ref byte input = ref MemoryMarshal.GetReference(values);
            Span<byte> word = stackalloc byte[8];
            for (; group < groups; group++)
            {
                ulong packed = Bmi2.X64.ParallelBitExtract(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref input, group * 8)), lanes);
                BinaryPrimitives.WriteUInt64LittleEndian(word, packed);
                word[..bitWidth].CopyTo(destination[(group * bitWidth)..]);
            }
        }

        ulong buffer = 0;
        int filled = 0;
        int written = group * bitWidth;
        for (int i = group * 8; i < count; i++)
        {
            buffer |= (ulong)values[i] << filled;
            filled += bitWidth;
            while (filled >= 8)
            {
                destination[written++] = (byte)buffer;
                buffer >>= 8;
                filled -= 8;
            }
        }

        if (filled > 0)
        {
            destination[written] = (byte)buffer;
        }
    }

    /// <summary>Packs values of <paramref name="bitWidth"/> bits, 0 to 64.</summary>
    internal static void Pack64(ReadOnlySpan<ulong> values, int bitWidth, Span<byte> destination)
    {
        if ((uint)bitWidth > 64)
        {
            ThrowWidth(bitWidth, 64);
        }

        int bytes = (int)PackedBytes(values.Length, bitWidth);
        if (destination.Length < bytes)
        {
            throw new ArgumentException("The destination is shorter than the packed values.", nameof(destination));
        }

        if (bitWidth == 0)
        {
            return;
        }

        UInt128 buffer = 0;
        int filled = 0;
        int written = 0;
        UInt128 mask = bitWidth == 64 ? ulong.MaxValue : (1UL << bitWidth) - 1;
        foreach (ulong value in values)
        {
            buffer |= ((UInt128)value & mask) << filled;
            filled += bitWidth;
            while (filled >= 8)
            {
                destination[written++] = (byte)buffer;
                buffer >>= 8;
                filled -= 8;
            }
        }

        if (filled > 0)
        {
            destination[written] = (byte)buffer;
        }
    }

    /// <summary>The 64 bits at <paramref name="at"/>, read through a zero-padded copy where fewer remain.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong Word(ReadOnlySpan<byte> source, int at, Span<byte> padded)
    {
        if (source.Length - at >= 8)
        {
            return BinaryPrimitives.ReadUInt64LittleEndian(source[at..]);
        }

        padded.Clear();
        source[at..].CopyTo(padded);
        return BinaryPrimitives.ReadUInt64LittleEndian(padded);
    }

    /// <summary>The scalar path from value <paramref name="start"/>: a 64-bit word per value, the tail through a padded copy.</summary>
    private static void Unpack32Scalar(ReadOnlySpan<byte> source, int bitWidth, Span<uint> destination, int start)
    {
        int count = destination.Length;
        ulong mask = (1UL << bitWidth) - 1;
        ref byte input = ref MemoryMarshal.GetReference(source);
        ref uint output = ref MemoryMarshal.GetReference(destination);
        int i = start;
        if (BitConverter.IsLittleEndian)
        {
            // A value of at most 32 bits starting at most 7 bits into a byte lies in the 8 bytes
            // from that byte.
            long limit = source.Length - 8;
            for (; i < count; i++)
            {
                long bit = (long)i * bitWidth;
                long at = bit >> 3;
                if (at > limit)
                {
                    break;
                }

                ulong word = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref input, (nint)at));
                Unsafe.Add(ref output, i) = (uint)((word >> (int)(bit & 7)) & mask);
            }
        }

        Span<byte> padded = stackalloc byte[8];
        for (; i < count; i++)
        {
            long bit = (long)i * bitWidth;
            int at = (int)(bit >> 3);
            ulong word = Word(source, at, padded);
            Unsafe.Add(ref output, i) = (uint)((word >> (int)(bit & 7)) & mask);
        }
    }

    /// <summary>
    /// Sixteen values a step: a 64-byte load spans two groups, a byte permute moves each value's four
    /// bytes into its lane, and a variable shift and a mask finish it. Returns the values done.
    /// </summary>
    private static unsafe int Unpack32Avx512(ReadOnlySpan<byte> source, int bitWidth, Span<uint> destination)
    {
        int steps = destination.Length >> 4;
        if (steps == 0)
        {
            return 0;
        }

        Vector512<byte> permute = Vector512.Create(Tables512.Permute[bitWidth]);
        Vector512<uint> shifts = Vector512.Create(Tables512.Shifts[bitWidth]);
        Vector512<uint> mask = Vector512.Create((1u << bitWidth) - 1);
        int stride = 2 * bitWidth;
        int last = source.Length - 64;
        int step = 0;
        fixed (byte* input = source)
        fixed (uint* output = destination)
        {
            for (; step < steps && step * stride <= last; step++)
            {
                Vector512<byte> bytes = Vector512.Load(input + step * stride);
                Vector512<uint> lanes = Avx512Vbmi.PermuteVar64x8(bytes, permute).AsUInt32();
                Vector512<uint> values = Avx512F.ShiftRightLogicalVariable(lanes, shifts) & mask;
                values.Store(output + (step << 4));
            }
        }

        return step << 4;
    }

    /// <summary>
    /// Eight values a step, one group: each 128-bit half loads the bytes of four values, a shuffle
    /// moves each value's four bytes into its lane, and a variable shift and a mask finish it.
    /// </summary>
    private static unsafe int Unpack32Avx2(ReadOnlySpan<byte> source, int bitWidth, Span<uint> destination)
    {
        int steps = destination.Length >> 3;
        if (steps == 0)
        {
            return 0;
        }

        Vector256<byte> shuffle = Vector256.Create(Tables256.Shuffle[bitWidth]);
        Vector256<uint> shifts = Vector256.Create(Tables256.Shifts[bitWidth]);
        Vector256<uint> mask = Vector256.Create((1u << bitWidth) - 1);
        int upper = (4 * bitWidth) >> 3;
        int last = source.Length - (upper + 16);
        int step = 0;
        fixed (byte* input = source)
        fixed (uint* output = destination)
        {
            for (; step < steps && step * bitWidth <= last; step++)
            {
                byte* group = input + step * bitWidth;
                Vector256<byte> bytes = Vector256.Create(Vector128.Load(group), Vector128.Load(group + upper));
                Vector256<uint> lanes = Avx2.Shuffle(bytes, shuffle).AsUInt32();
                Vector256<uint> values = Avx2.ShiftRightLogicalVariable(lanes, shifts) & mask;
                values.Store(output + (step << 3));
            }
        }

        return step << 3;
    }

    /// <summary>The same as the AVX2 path, a 128-bit table lookup and a negative shift per half group.</summary>
    private static unsafe int Unpack32Neon(ReadOnlySpan<byte> source, int bitWidth, Span<uint> destination)
    {
        int steps = destination.Length >> 3;
        if (steps == 0)
        {
            return 0;
        }

        Vector128<byte> lowLookup = Vector128.Create(Tables256.Shuffle[bitWidth], 0);
        Vector128<byte> highLookup = Vector128.Create(Tables256.Shuffle[bitWidth], 16);
        Vector128<int> lowShifts = -Vector128.Create(Tables256.Shifts[bitWidth], 0).AsInt32();
        Vector128<int> highShifts = -Vector128.Create(Tables256.Shifts[bitWidth], 4).AsInt32();
        Vector128<uint> mask = Vector128.Create((1u << bitWidth) - 1);
        int upper = (4 * bitWidth) >> 3;
        int last = source.Length - (upper + 16);
        int step = 0;
        fixed (byte* input = source)
        fixed (uint* output = destination)
        {
            for (; step < steps && step * bitWidth <= last; step++)
            {
                byte* group = input + step * bitWidth;
                Vector128<uint> low = AdvSimd.Arm64.VectorTableLookup(Vector128.Load(group), lowLookup).AsUInt32();
                Vector128<uint> high = AdvSimd.Arm64.VectorTableLookup(Vector128.Load(group + upper), highLookup).AsUInt32();
                (AdvSimd.ShiftLogical(low, lowShifts) & mask).Store(output + (step << 3));
                (AdvSimd.ShiftLogical(high, highShifts) & mask).Store(output + (step << 3) + 4);
            }
        }

        return step << 3;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowWidth(int bitWidth, int most) =>
        throw new ParquetFormatException($"A bit width of {bitWidth} passes the {most} bits the values hold.");

    /// <summary>
    /// The AVX2 and NEON tables, per width up to <see cref="VectorWidth32"/>: for each of a group's
    /// eight values, the four bytes its bits lie in, counted from the start of its half (values 0
    /// to 3 from the group's first byte, 4 to 7 from byte <c>(4 × width) / 8</c>), and the shift
    /// that brings its first bit down to bit 0.
    /// </summary>
    private static class Tables256
    {
        internal static readonly byte[][] Shuffle = new byte[VectorWidth32 + 1][];
        internal static readonly uint[][] Shifts = new uint[VectorWidth32 + 1][];

        static Tables256()
        {
            for (int width = 0; width <= VectorWidth32; width++)
            {
                byte[] shuffle = new byte[32];
                uint[] shifts = new uint[8];
                int upper = (4 * width) >> 3;
                for (int j = 0; j < 8; j++)
                {
                    int bit = j * width;
                    int start = (bit >> 3) - (j < 4 ? 0 : upper);
                    for (int b = 0; b < 4; b++)
                    {
                        shuffle[j * 4 + b] = (byte)(start + b);
                    }

                    shifts[j] = (uint)(bit & 7);
                }

                Shuffle[width] = shuffle;
                Shifts[width] = shifts;
            }
        }
    }

    /// <summary>The AVX-512 tables: sixteen values, two groups, each value's four bytes counted from the first group's start.</summary>
    private static class Tables512
    {
        internal static readonly byte[][] Permute = new byte[VectorWidth32 + 1][];
        internal static readonly uint[][] Shifts = new uint[VectorWidth32 + 1][];

        static Tables512()
        {
            for (int width = 0; width <= VectorWidth32; width++)
            {
                byte[] permute = new byte[64];
                uint[] shifts = new uint[16];
                for (int j = 0; j < 16; j++)
                {
                    int bit = j * width;
                    for (int b = 0; b < 4; b++)
                    {
                        permute[j * 4 + b] = (byte)((bit >> 3) + b);
                    }

                    shifts[j] = (uint)(bit & 7);
                }

                Permute[width] = permute;
                Shifts[width] = shifts;
            }
        }
    }
}
