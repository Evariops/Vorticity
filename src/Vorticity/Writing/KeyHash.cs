using System;
using System.IO.Hashing;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Vorticity.Writing;

/// <summary>
/// Bucket hashes for the write path's tables. Not checksums: collisions are compared away, so which
/// hash is used picks a bucket and cannot change a byte of the written file.
/// </summary>
internal static class KeyHash
{
    private const ulong Golden = 0x9E3779B97F4A7C15UL;
    private const ulong Spread = 0xBF58476D1CE4E5B9UL;

    /// <summary>
    /// Mixes a whole fixed-width value, zero-extended. The final fold is what makes the low bits
    /// good, and a power-of-two mask reads exactly those.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ulong Mix(ulong value)
    {
        ulong hash = value * Golden;
        hash ^= hash >> 29;
        hash *= Spread;
        hash ^= hash >> 32;
        return hash;
    }

    /// <summary>
    /// A byte string's hash: folded when it fits a view, XxHash3-64 beyond. The branch is on the
    /// length alone, so two equal values always take the same arm.
    /// </summary>
    internal static ulong Bytes(ReadOnlySpan<byte> bytes)
    {
        ref byte first = ref MemoryMarshal.GetReference(bytes);
        if (bytes.Length > 16)
        {
            return bytes.Length <= 64 ? Words(ref first, bytes.Length) : XxHash3.HashToUInt64(bytes);
        }
        ulong word;
        if (bytes.Length >= 8)
        {
            ulong low = Unsafe.ReadUnaligned<ulong>(ref first);
            ulong high = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref first, bytes.Length - 8));
            word = low ^ (high * Spread);
        }
        else if (bytes.Length >= 4)
        {
            // The two reads overlap for the lengths between the powers, which keeps the arm branchless.
            ulong low = Unsafe.ReadUnaligned<uint>(ref first);
            ulong high = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref first, bytes.Length - 4));
            word = (high << 32) | low;
        }
        else
        {
            word = 0;
            for (int i = 0; i < bytes.Length; i++)
            {
                word |= (ulong)Unsafe.Add(ref first, i) << (i * 8);
            }
        }

        // The length is mixed in because "a" and "a\0" fold to the same word.
        return Mix(word ^ ((ulong)bytes.Length * Golden));
    }

    /// <summary>
    /// A string of 17 to 64 bytes: its words read from both ends, overlapping for the lengths
    /// between, folded into two sums by multiplies and mixed once, with no call.
    /// </summary>
    /// <remarks>
    /// Two equal strings read the same words, which is all a bucket asks; a string's every byte
    /// lies in some word read, so strings that differ anywhere fold differently but by accident.
    /// </remarks>
    private static ulong Words(ref byte first, int length)
    {
        ulong a = Unsafe.ReadUnaligned<ulong>(ref first);
        ulong b = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref first, 8));
        ulong c = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref first, length - 16));
        ulong d = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref first, length - 8));
        ulong left = (a * Golden) ^ BitOperations.RotateLeft(c * Spread, 31);
        ulong right = (b * Spread) ^ BitOperations.RotateLeft(d * Golden, 27);
        if (length > 32)
        {
            ulong e = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref first, 16));
            ulong f = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref first, 24));
            ulong g = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref first, length - 32));
            ulong h = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref first, length - 24));
            left ^= BitOperations.RotateLeft(e * Spread, 17) ^ (g * Golden);
            right ^= BitOperations.RotateLeft(f * Golden, 43) ^ (h * Spread);
        }

        return Mix(left ^ BitOperations.RotateLeft(right, 32) ^ ((ulong)length * Golden));
    }
}
