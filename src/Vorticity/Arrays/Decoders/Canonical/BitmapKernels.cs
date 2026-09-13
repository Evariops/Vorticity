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

        if (Vector<byte>.IsSupported && count >= Vector<byte>.Count)
        {
            int width = Vector<byte>.Count;
            Vector<byte> ors = Vector<byte>.Zero;
            Vector<byte> ands = Vector<byte>.AllBitsSet;
            for (; i <= count - width; i += width)
            {
                Vector<byte> value = Vector.LoadUnsafe(ref source, (nuint)i);
                ors |= value;
                ands &= value;
                if (ors != Vector<byte>.Zero && ands != Vector<byte>.AllBitsSet)
                {
                    anySet = true;
                    anyClear = true;
                    return;
                }
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
