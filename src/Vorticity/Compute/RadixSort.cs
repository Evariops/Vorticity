using System;

namespace Vorticity.Compute;

/// <summary>
/// A stable sort of words by their low bits, a value moved alongside each: a least significant digit first radix sort of
/// eleven bits a pass, which skips a pass whose digit every word holds alike, as words of a narrow range or of a common
/// prefix do.
/// </summary>
/// <remarks>
/// Eleven bits keep a pass's counts, 8 KiB, in the first level of cache, and sort words of 33 bits in three passes: a
/// slice's packed codes, a minute of instants in microseconds, the bits of a hash a bucket is cut by.
/// </remarks>
internal static class RadixSort
{
    /// <summary>The bits a pass sorts by.</summary>
    internal const int DigitBits = 11;

    /// <summary>The counts a pass takes: one a digit, and one more.</summary>
    internal const int Counts = (1 << DigitBits) + 1;

    private const int Mask = (1 << DigitBits) - 1;

    /// <summary>
    /// Sorts <paramref name="keys"/> by their low <paramref name="bits"/> bits, stably, <paramref name="values"/> moved
    /// alongside, through <paramref name="spareKeys"/> and <paramref name="spareValues"/>, as long as they;
    /// <paramref name="counts"/> holds <see cref="Counts"/> of them. True when the sorted words and their values are in the
    /// spares, the others then left as their scratch.
    /// </summary>
    internal static bool Sort<TValue>(
        Span<ulong> keys, Span<TValue> values, Span<ulong> spareKeys, Span<TValue> spareValues, int bits, Span<int> counts)
        where TValue : unmanaged
    {
        bool spared = false;
        for (int shift = 0; shift < bits; shift += DigitBits)
        {
            spared ^= spared
                ? Pass(spareKeys, spareValues, keys, values, shift, counts)
                : Pass(keys, values, spareKeys, spareValues, shift, counts);
        }

        return spared;
    }

    /// <summary>
    /// Moves the words of <paramref name="from"/> and their values into <paramref name="to"/> in the order of their digit at
    /// <paramref name="shift"/>, stably; false, moving nothing, when every word holds the same digit.
    /// </summary>
    private static bool Pass<TValue>(
        ReadOnlySpan<ulong> from, ReadOnlySpan<TValue> fromValues, Span<ulong> to, Span<TValue> toValues, int shift, Span<int> counts)
        where TValue : unmanaged
    {
        counts.Clear();
        for (int i = 0; i < from.Length; i++)
        {
            counts[(int)((from[i] >> shift) & Mask) + 1]++;
        }

        // A digit every word holds alike moves nothing.
        if (from.Length <= 1 || counts[(int)((from[0] >> shift) & Mask) + 1] == from.Length)
        {
            return false;
        }

        for (int d = 0; d < Mask + 1; d++)
        {
            counts[d + 1] += counts[d];
        }

        for (int i = 0; i < from.Length; i++)
        {
            int at = counts[(int)((from[i] >> shift) & Mask)]++;
            to[at] = from[i];
            toValues[at] = fromValues[i];
        }

        return true;
    }
}
