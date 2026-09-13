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
