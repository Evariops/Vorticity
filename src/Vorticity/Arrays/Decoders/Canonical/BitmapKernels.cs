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
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace Vorticity.Arrays.Decoders.Canonical;

/// <summary>Whole-run bitmap writes, for the decoders that produce runs rather than bits.</summary>
internal static class BitmapKernels
{
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
