using System;
using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;

namespace Vorticity.Arrays.Decoders.Canonical;

/// <summary>
/// Whole-run bitmap writes, for the decoders that produce runs rather than bits. Bitmaps are
/// least-significant-bit first, as Arrow and <c>vortex.bool</c> both are: bit i lives in byte
/// <c>i &gt;&gt; 3</c> at position <c>i &amp; 7</c>.
/// </summary>
/// <remarks>
/// <para>
/// A loop that genuinely touches scattered bits belongs on <c>CanonicalSupport.BitAt</c> and
/// <c>CanonicalSupport.SetBit</c>, which are the single-bit spelling of the same layout; these
/// kernels are for whole runs, where a run reduces to two edge bytes and a fill between them.
/// </para>
/// <para>
/// The choice between the two vector widths is written here because this class holds one of each.
/// <see cref="Classify"/> folds bytes into an <c>or</c> and an <c>and</c> accumulator: every lane
/// does the same thing to its own element, and the two answers are the same whether the machine
/// gives sixteen lanes or sixty-four, so it takes <c>Vector&lt;T&gt;</c> and whatever width the
/// hardware has. <see cref="PackBytes"/> extracts sixteen sign bits into a <see cref="ushort"/>
/// and writes exactly two bytes per iteration: the lane count is part of the output's shape, so it
/// takes <c>Vector128</c> and is pinned to sixteen.
/// </para>
/// <para>
/// The rule that separates them: <c>Vector&lt;T&gt;</c> when the work is element-wise and its
/// result does not depend on how many lanes ran at once; a fixed width when the width reaches the
/// output — a lane shuffle, a mask extracted to an integer, a block of a size the format sets, or
/// a floating-point reduction, whose folding order the width decides and whose last bit therefore
/// moves with it.
/// </para>
/// <para>
/// Reading <c>Vector&lt;T&gt;.Count</c> is not itself a breach of the rule. A loop may take its
/// stride, its seed or its step from the width and still write the same bytes at every width —
/// what matters is the bytes, not whether the width was consulted to produce them.
/// </para>
/// </remarks>
internal static class BitmapKernels
{
    /// <summary>
    /// Reports whether a bit range holds any set bit and whether it holds any clear bit.
    /// </summary>
    /// <param name="bits">The bitmap; the caller has checked that the range fits.</param>
    /// <param name="bitOffset">First bit.</param>
    /// <param name="length">How many bits; must be positive.</param>
    /// <param name="anySet">Set when at least one bit in the range is 1.</param>
    /// <param name="anyClear">Set when at least one bit in the range is 0.</param>
    /// <remarks>
    /// <para>
    /// The two answers together say whether a validity bitmap is all-valid, all-invalid or mixed,
    /// which decides whether the bitmap is kept at all.
    /// </para>
    /// <para>
    /// The edges are masked once each and the interior is whole bytes, so the question reduces to
    /// "is this run all zero" and "is this run all ones": an or accumulator and an and accumulator
    /// over vectors. The early exit earns its place on a mixed bitmap, where the answer usually
    /// settles in the first vector.
    /// </para>
    /// </remarks>
    internal static void Classify(
        ReadOnlySpan<byte> bits, int bitOffset, int length, out bool anySet, out bool anyClear)
    {
        int firstByte = bitOffset >> 3;
        long endExclusive = (long)bitOffset + length;
        int lastByte = (int)((endExclusive - 1) >> 3);
        int lo = bitOffset & 7;
        int hi = (int)((endExclusive - 1) & 7) + 1;

        if (firstByte == lastByte)
        {
            byte only = (byte)(((1 << hi) - 1) & ~((1 << lo) - 1));
            byte masked = (byte)(bits[firstByte] & only);
            anySet = masked != 0;
            anyClear = masked != only;
            return;
        }

        byte headMask = (byte)(0xFF << lo);
        byte head = (byte)(bits[firstByte] & headMask);
        byte tailMask = (byte)((1 << hi) - 1);
        byte tail = (byte)(bits[lastByte] & tailMask);

        anySet = head != 0 || tail != 0;
        anyClear = head != headMask || tail != tailMask;
        if (anySet && anyClear)
        {
            return;
        }

        ReadOnlySpan<byte> middle = bits[(firstByte + 1)..lastByte];
        int count = middle.Length;
        ref byte source = ref MemoryMarshal.GetReference(middle);
        int i = 0;

        if (Vector.IsHardwareAccelerated && count >= Vector<byte>.Count)
        {
            int width = Vector<byte>.Count;
            Vector<byte> ors = Vector<byte>.Zero;
            Vector<byte> ands = Vector<byte>.AllBitsSet;

            // Four vectors per exit test. Accumulating is two instructions, while asking whether
            // the answer is settled costs two vector compares and a branch, and a uniform bitmap
            // -- the case that reaches this loop at all -- never settles, so a test per vector
            // would be spent to learn nothing. A mixed bitmap leaves up to three vectors later
            // than a per-vector test would let it, which costs it almost nothing: a mix the edges
            // can see never enters this loop, since the head and tail are compared above and the
            // loop is reached only when both ends agree.
            int block = width * 4;
            for (; i <= count - block; i += block)
            {
                ors |= Vector.LoadUnsafe(ref source, (nuint)i);
                ands &= Vector.LoadUnsafe(ref source, (nuint)i);
                ors |= Vector.LoadUnsafe(ref source, (nuint)(i + width));
                ands &= Vector.LoadUnsafe(ref source, (nuint)(i + width));
                ors |= Vector.LoadUnsafe(ref source, (nuint)(i + (width * 2)));
                ands &= Vector.LoadUnsafe(ref source, (nuint)(i + (width * 2)));
                ors |= Vector.LoadUnsafe(ref source, (nuint)(i + (width * 3)));
                ands &= Vector.LoadUnsafe(ref source, (nuint)(i + (width * 3)));
                if (ors != Vector<byte>.Zero && ands != Vector<byte>.AllBitsSet)
                {
                    anySet = true;
                    anyClear = true;
                    return;
                }
            }

            for (; i <= count - width; i += width)
            {
                Vector<byte> value = Vector.LoadUnsafe(ref source, (nuint)i);
                ors |= value;
                ands &= value;
            }

            anySet |= ors != Vector<byte>.Zero;
            anyClear |= ands != Vector<byte>.AllBitsSet;
        }

        for (; i < count; i++)
        {
            byte value = Unsafe.Add(ref source, i);
            anySet |= value != 0;
            anyClear |= value != 0xFF;
            if (anySet && anyClear)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Counts the set bits of <paramref name="count"/> bits from <paramref name="start"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Eight bytes at a time, with the two partial ends masked once before the loop rather than
    /// tested on every byte: both ends are known before the loop starts.
    /// </para>
    /// <para>
    /// The single-byte case is separate because a range inside one byte has both masks on the same
    /// byte, and the two-sided form would count it twice.
    /// </para>
    /// </remarks>
    /// <param name="bits">The bitmap; the caller has established it covers the range.</param>
    /// <param name="start">First bit.</param>
    /// <param name="count">How many bits; zero and negative count as none.</param>
    /// <returns>How many of those bits are set.</returns>
    internal static int CountSet(ReadOnlySpan<byte> bits, int start, int count)
    {
        if (count <= 0)
        {
            return 0;
        }

        int firstByte = start >> 3;
        long endExclusive = (long)start + count;
        int lastByte = (int)((endExclusive - 1) >> 3);
        int lo = start & 7;
        int hi = (int)((endExclusive - 1) & 7) + 1;

        if (firstByte == lastByte)
        {
            uint only = (uint)(bits[firstByte] & (0xFF << lo) & (0xFF >> (8 - hi)));
            return BitOperations.PopCount(only);
        }

        int total = BitOperations.PopCount((uint)(bits[firstByte] & (0xFF << lo)) & 0xFF)
            + BitOperations.PopCount((uint)(bits[lastByte] & (0xFF >> (8 - hi))));

        ReadOnlySpan<byte> middle = bits[(firstByte + 1)..lastByte];
        if (AdvSimd.Arm64.IsSupported && middle.Length >= CountBlock)
        {
            total += CountBlocks(middle, out int counted);
            middle = middle[counted..];
        }

        ReadOnlySpan<ulong> words = MemoryMarshal.Cast<byte, ulong>(middle);
        for (int i = 0; i < words.Length; i++)
        {
            total += BitOperations.PopCount(words[i]);
        }

        for (int i = words.Length * sizeof(ulong); i < middle.Length; i++)
        {
            total += BitOperations.PopCount((uint)middle[i]);
        }

        return total;
    }

    /// <summary>Bytes <see cref="CountBlocks"/> counts at a time: four vectors.</summary>
    private const int CountBlock = 4 * 16;

    /// <summary>
    /// The set bits of the whole <see cref="CountBlock"/>-byte blocks of <paramref name="bytes"/>,
    /// and how many bytes those blocks are.
    /// </summary>
    /// <remarks>
    /// On arm64 a scalar population count moves each word into a vector register and back; this
    /// counts sixteen bytes to an instruction and folds the counts into halfword lanes, in four
    /// accumulators so that no fold waits on the one before. A lane gains at most sixteen a block,
    /// so the lanes are summed every 4 096 blocks, before one could overflow.
    /// </remarks>
    private static int CountBlocks(ReadOnlySpan<byte> bytes, out int counted)
    {
        ref byte start = ref MemoryMarshal.GetReference(bytes);
        int blocks = bytes.Length / CountBlock;
        long total = 0;
        int block = 0;
        while (block < blocks)
        {
            int stop = Math.Min(blocks, block + 4_096);
            Vector128<ushort> a = Vector128<ushort>.Zero;
            Vector128<ushort> b = Vector128<ushort>.Zero;
            Vector128<ushort> c = Vector128<ushort>.Zero;
            Vector128<ushort> d = Vector128<ushort>.Zero;
            for (; block < stop; block++)
            {
                nuint at = (nuint)(block * CountBlock);
                a = AdvSimd.AddPairwiseWideningAndAdd(a, AdvSimd.PopCount(Vector128.LoadUnsafe(ref start, at)));
                b = AdvSimd.AddPairwiseWideningAndAdd(b, AdvSimd.PopCount(Vector128.LoadUnsafe(ref start, at + 16)));
                c = AdvSimd.AddPairwiseWideningAndAdd(c, AdvSimd.PopCount(Vector128.LoadUnsafe(ref start, at + 32)));
                d = AdvSimd.AddPairwiseWideningAndAdd(d, AdvSimd.PopCount(Vector128.LoadUnsafe(ref start, at + 48)));
            }

            total += AdvSimd.Arm64.AddAcrossWidening(a).ToScalar() + AdvSimd.Arm64.AddAcrossWidening(b).ToScalar()
                + AdvSimd.Arm64.AddAcrossWidening(c).ToScalar() + AdvSimd.Arm64.AddAcrossWidening(d).ToScalar();
        }

        counted = blocks * CountBlock;
        return (int)total;
    }

    /// <summary>
    /// Copies <paramref name="count"/> bits from <paramref name="source"/> at
    /// <paramref name="sourceStart"/> to <paramref name="destination"/> at
    /// <paramref name="destinationStart"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The destination is written a byte at a time, not a bit. Once the destination is byte
    /// aligned -- which costs at most seven bits of head -- each output byte is eight source bits
    /// read as one unaligned pair and shifted into place, whatever the source's own alignment.
    /// The destination byte is assigned rather than or-ed into, so the caller need not hand over a
    /// cleared buffer.
    /// </para>
    /// <para>
    /// The second source byte of a pair is read only when the shift needs it, and when it does,
    /// the caller's promise that <paramref name="count"/> bits are readable is exactly what puts
    /// it in range: the last whole output byte ends at source bit
    /// <c>sourceStart + 8 * wholeBytes - 1</c>, whose byte index is the one the pair's high half
    /// reads.
    /// </para>
    /// </remarks>
    /// <param name="source">The source bitmap.</param>
    /// <param name="sourceStart">First source bit.</param>
    /// <param name="destination">The destination bitmap.</param>
    /// <param name="destinationStart">First destination bit.</param>
    /// <param name="count">How many bits; zero and negative are no-ops.</param>
    internal static void CopyRange(
        ReadOnlySpan<byte> source, int sourceStart, Span<byte> destination, int destinationStart,
        int count)
    {
        if (count <= 0)
        {
            return;
        }

        // The head, bit by bit, until the destination lands on a byte boundary.
        int copied = 0;
        while (copied < count && ((destinationStart + copied) & 7) != 0)
        {
            CopyOne(source, sourceStart + copied, destination, destinationStart + copied);
            copied++;
        }

        int wholeBytes = (count - copied) >> 3;
        if (wholeBytes > 0)
        {
            int destinationByte = (destinationStart + copied) >> 3;
            int sourceBit = sourceStart + copied;
            int sourceByte = sourceBit >> 3;
            int shift = sourceBit & 7;

            if (shift == 0)
            {
                source.Slice(sourceByte, wholeBytes).CopyTo(destination.Slice(destinationByte, wholeBytes));
            }
            else
            {
                for (int b = 0; b < wholeBytes; b++)
                {
                    int pair = source[sourceByte + b] | (source[sourceByte + b + 1] << 8);
                    destination[destinationByte + b] = (byte)(pair >> shift);
                }
            }

            copied += wholeBytes << 3;
        }

        // The tail, bit by bit again: fewer than eight of them by construction.
        for (; copied < count; copied++)
        {
            CopyOne(source, sourceStart + copied, destination, destinationStart + copied);
        }
    }

    /// <summary>Copies one bit, setting or clearing the destination to match.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void CopyOne(
        ReadOnlySpan<byte> source, int sourceBit, Span<byte> destination, int destinationBit)
    {
        int bit = (source[sourceBit >> 3] >> (sourceBit & 7)) & 1;
        int at = destinationBit >> 3;
        int mask = 1 << (destinationBit & 7);
        destination[at] = (byte)((destination[at] & ~mask) | (bit == 0 ? 0 : mask));
    }

    /// <summary>Sets <paramref name="count"/> bits from <paramref name="start"/>.</summary>
    /// <param name="bits">The bitmap; the caller has sized it.</param>
    /// <param name="start">First bit.</param>
    /// <param name="count">How many bits; zero and negative are no-ops.</param>
    internal static void SetRange(Span<byte> bits, int start, int count)
    {
        if (count <= 0)
        {
            return;
        }

        int firstByte = start >> 3;
        int lastBit = start + count - 1;
        int lastByte = lastBit >> 3;
        byte firstMask = (byte)(0xFF << (start & 7));
        byte lastMask = (byte)(0xFF >> (7 - (lastBit & 7)));

        if (firstByte == lastByte)
        {
            bits[firstByte] |= (byte)(firstMask & lastMask);
            return;
        }

        bits[firstByte] |= firstMask;
        if (lastByte > firstByte + 1)
        {
            bits.Slice(firstByte + 1, lastByte - firstByte - 1).Fill(0xFF);
        }

        bits[lastByte] |= lastMask;
    }

    /// <summary>Clears <paramref name="count"/> bits from <paramref name="start"/>.</summary>
    /// <param name="bits">The bitmap; the caller has sized it.</param>
    /// <param name="start">First bit.</param>
    /// <param name="count">How many bits; zero and negative are no-ops.</param>
    internal static void ClearRange(Span<byte> bits, int start, int count)
    {
        if (count <= 0)
        {
            return;
        }

        int firstByte = start >> 3;
        int lastBit = start + count - 1;
        int lastByte = lastBit >> 3;
        byte firstMask = (byte)(0xFF << (start & 7));
        byte lastMask = (byte)(0xFF >> (7 - (lastBit & 7)));

        if (firstByte == lastByte)
        {
            bits[firstByte] &= (byte)~(firstMask & lastMask);
            return;
        }

        bits[firstByte] &= (byte)~firstMask;
        if (lastByte > firstByte + 1)
        {
            bits.Slice(firstByte + 1, lastByte - firstByte - 1).Clear();
        }

        bits[lastByte] &= (byte)~lastMask;
    }

    /// <summary>
    /// Packs one byte per boolean into one bit per boolean: bit i is set iff
    /// <c>source[i] != 0</c>.
    /// </summary>
    /// <param name="source">One byte per value; any non-zero byte counts as true.</param>
    /// <param name="destination">
    /// <c>(source.Length + 7) / 8</c> bytes; every one of them is written, including the partial
    /// last byte, whose bits past the value count are cleared.
    /// </param>
    /// <remarks>
    /// <c>vortex.bytebool</c> is this loop and nothing else, so it is worth the vector path:
    /// sixteen bytes compare to zero in one instruction and their sixteen sign bits extract to a
    /// <see cref="ushort"/> in one more, which writes two output bytes per iteration and touches
    /// each of them once instead of read-modify-writing a destination byte per value.
    /// </remarks>
    internal static void PackBytes(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        int length = source.Length;
        int i = 0;

        if (Vector128.IsHardwareAccelerated && length >= Vector128<byte>.Count)
        {
            ref byte input = ref MemoryMarshal.GetReference(source);
            for (; i <= length - Vector128<byte>.Count; i += Vector128<byte>.Count)
            {
                Vector128<byte> values = Vector128.LoadUnsafe(ref input, (uint)i);

                // Equals gives 0xFF where the byte is zero, so the mask of its sign bits is the
                // complement of what the bitmap wants.
                uint zeros = Vector128.Equals(values, Vector128<byte>.Zero)
                    .ExtractMostSignificantBits();
                BinaryPrimitives.WriteUInt16LittleEndian(
                    destination.Slice(i >> 3, 2), (ushort)~zeros);
            }
        }

        // Whole bytes of the tail, written once each rather than bit by bit.
        for (; i + 8 <= length; i += 8)
        {
            byte packed = 0;
            for (int k = 0; k < 8; k++)
            {
                packed |= (byte)((source[i + k] != 0 ? 1 : 0) << k);
            }

            destination[i >> 3] = packed;
        }

        if (i < length)
        {
            byte packed = 0;
            for (int k = 0; i + k < length; k++)
            {
                packed |= (byte)((source[i + k] != 0 ? 1 : 0) << k);
            }

            destination[i >> 3] = packed;
        }
    }

    /// <summary>Sets or clears <paramref name="count"/> bits from <paramref name="start"/>.</summary>
    /// <param name="bits">The bitmap.</param>
    /// <param name="start">First bit.</param>
    /// <param name="count">How many bits.</param>
    /// <param name="value">What to write.</param>
    internal static void FillRange(Span<byte> bits, int start, int count, bool value)
    {
        if (value)
        {
            SetRange(bits, start, count);
        }
        else
        {
            ClearRange(bits, start, count);
        }
    }
}
