// Bitmap runs, by the word instead of by the bit.
//
// Every encoding that expands rows writes validity in RUNS: `vortex.runend` marks a whole run
// valid, `vortex.sparse` fills then patches, `vortex.constant` sets every bit or none. The
// bit-at-a-time loops those started as are O(bits) with a load, an or and a store each; a run is
// two edge bytes and a `Fill` in between, which is the memset intrinsic. On a million rows that is
// the difference between a million read-modify-writes and 125 KB of vector stores.
//
// LSB-first throughout, which is what Arrow and `vortex.bool` both use: bit i of a bitmap lives in
// byte i >> 3 at position i & 7. CanonicalSupport.BitAt/SetBit are the single-bit spelling of the
// same layout and stay where they are - a loop that genuinely touches scattered bits should keep
// using them.
using System;
using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace Vorticity.Arrays.Decoders.Canonical;

/// <summary>Whole-run bitmap writes, for the decoders that produce runs rather than bits.</summary>
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
    /// which decides whether the bitmap is kept at all. It was a byte-at-a-time loop that
    /// recomputed an edge mask for EVERY byte -- two comparisons against a mask that is 0xFF for
    /// all but the first and last -- and on an all-invalid million-row column it was 36% of the
    /// scan.
    /// </para>
    /// <para>
    /// The edges are masked once each and the interior is whole bytes, so it reduces to "is this
    /// run all zero" and "is this run all ones": an OR accumulator and an AND accumulator over
    /// vectors. THE EARLY EXIT IS KEPT, because it is what made the old loop tolerable on a MIXED
    /// bitmap - the answer is usually settled in the first byte or two - and it now settles in the
    /// first vector instead.
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

            // FOUR VECTORS PER EXIT TEST. Accumulating is two instructions; asking whether the
            // answer is settled is two vector compares and a branch, and on a uniform bitmap -- the
            // case that reaches this loop at all -- it is never settled, so every one of those was
            // spent to learn nothing. Removing the test outright reads 4,126 us against 3,737 on a
            // million-bit classify, so it was nine per cent of the kernel.
            //
            // A mixed bitmap now leaves up to three vectors later than it did. It costs that
            // bitmap almost nothing, because a mix the edges can see never enters this loop: the
            // head and tail are compared above, and the loop is reached only when both ends agree.
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
    /// Counts the SET bits of <paramref name="count"/> bits from <paramref name="start"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Eight bytes at a time, with the two partial ends masked ONCE instead of tested per byte.
    /// The loops this replaces did one of two things per bit or per byte: a
    /// <c>mask.IsValid(row)</c> call over every row of the array, or a byte-wise popcount carrying
    /// an <c>i == firstByte</c> and an <c>i == lastByte</c> compare into all 125 000 iterations of
    /// a million-row bitmap. Both ends are known before the loop starts.
    /// </para>
    /// <para>
    /// The single-byte case is separate because a range inside one byte has BOTH masks on the same
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

    /// <summary>
    /// Copies <paramref name="count"/> bits from <paramref name="source"/> at
    /// <paramref name="sourceStart"/> to <paramref name="destination"/> at
    /// <paramref name="destinationStart"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// THE DESTINATION IS WRITTEN A BYTE AT A TIME, not a bit. Once the destination is byte
    /// aligned -- which costs at most seven bits of head -- each output byte is eight source bits
    /// read as one unaligned pair and shifted into place, whatever the source's own alignment.
    /// Eight times fewer read-modify-writes, and the destination byte is written rather than
    /// OR-ed, so the caller no longer has to hand over a cleared buffer.
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
    /// <param name="source">One byte per value; ANY non-zero byte is true, as upstream has it.</param>
    /// <param name="destination">
    /// <c>(source.Length + 7) / 8</c> bytes; every one of them is written, including the partial
    /// last byte, whose bits past the value count are cleared.
    /// </param>
    /// <remarks>
    /// <c>vortex.bytebool</c> is this loop and nothing else, and it was a read-modify-write of the
    /// destination byte per VALUE. Sixteen bytes compare to zero in one instruction and their
    /// sixteen sign bits extract to a <see cref="ushort"/> in one more, so the vector path writes
    /// two output bytes per iteration and touches each of them once.
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

                // Equals gives 0xFF where the byte IS zero, so the mask of its sign bits is the
                // complement of what the bitmap wants.
                uint zeros = Vector128.Equals(values, Vector128<byte>.Zero)
                    .ExtractMostSignificantBits();
                BinaryPrimitives.WriteUInt16LittleEndian(
                    destination.Slice(i >> 3, 2), (ushort)~zeros);
            }
        }

        // Whole bytes of the tail, still written once each rather than bit by bit.
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
