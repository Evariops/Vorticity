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
/// <remarks>
/// Every hash goes through seeds drawn once a process, so that which values share a bucket cannot be
/// worked out ahead: values built to collide under fixed constants would make every table of a
/// chunk quadratic in its distinct values. No hash is written, so the file does not change.
/// </remarks>
internal static class KeyHash
{
    private const ulong Golden = 0x9E3779B97F4A7C15UL;

    /// <summary>What <see cref="Mix"/> adds to its first product, and XxHash3's seed.</summary>
    private static readonly ulong Seed = DrawSeed();

    /// <summary>The seed of <see cref="Fold"/>'s first operand.</summary>
    private static readonly ulong Left = DrawSeed();

    /// <summary>The seed of <see cref="Fold"/>'s second operand, and odd, the multiplier after it.</summary>
    private static readonly ulong Right = DrawSeed() | 1;

    /// <summary>
    /// Mixes a whole fixed-width value, zero-extended. The final fold is what makes the low bits
    /// good, and a power-of-two mask reads exactly those.
    /// </summary>
    /// <remarks>
    /// The seed is added to the first product, which a multiply-add does in the one instruction
    /// the product takes, and both multiplies are by one constant, so that a call builds only two:
    /// the seed and the multiplier. A multiplier drawn at random would cost nothing either, but
    /// some draws leave values that differ only in their high bits in runs of hundreds of slots.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ulong Mix(ulong value) => Finish((value * Golden) + Seed);

    /// <summary>
    /// A value of up to sixteen bytes given as its first and last eight, which overlap below
    /// sixteen, or as a view's two words, with its length.
    /// </summary>
    /// <remarks>
    /// The fold has already spread both words over the whole product under two seeds, so a
    /// multiply-add by one of them and a shift finish it, where a single word takes all of
    /// <see cref="Mix"/>, and a call builds no constant but the seeds. The length is the add,
    /// beside the fold rather than after it, so it costs the hash no step; it is what tells
    /// "abcdefgh" from itself twice, whose words are the same.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ulong Pair(ulong low, ulong high, int length)
    {
        ulong hash = (Fold(low, high) * Right) + (ulong)length;
        return hash ^ (hash >> 32);
    }

    /// <summary>
    /// A byte string's hash: one word mixed below eight bytes, words folded then mixed up to 64,
    /// XxHash3-64 beyond. The branch is on the length alone, so two equal values always take the
    /// same arm.
    /// </summary>
    internal static ulong Bytes(ReadOnlySpan<byte> bytes)
    {
        ref byte first = ref MemoryMarshal.GetReference(bytes);
        if (bytes.Length > 16)
        {
            return bytes.Length <= 64 ? Words(ref first, bytes.Length) : XxHash3.HashToUInt64(bytes, (long)Seed);
        }

        if (bytes.Length >= 8)
        {
            ulong low = Unsafe.ReadUnaligned<ulong>(ref first);
            ulong high = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref first, bytes.Length - 8));
            return Pair(low, high, bytes.Length);
        }

        ulong word;
        if (bytes.Length >= 4)
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

        // The length goes in because "a" and "a\0" make the same word.
        return Finish((word * Golden) + Seed + (ulong)bytes.Length);
    }

    /// <summary>
    /// Two words made one by the full product of each XORed with its own seed, its halves folded:
    /// the high half's low bits hang on every bit of both.
    /// </summary>
    /// <remarks>
    /// A sum or a product of the bare words would let pairs be written that fold to the same word
    /// whatever seed a mix applied after: sixteen bytes chosen from the other eight. Through seeded
    /// operands, which pairs collide depends on seeds no one outside the process knows.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ulong Fold(ulong low, ulong high)
    {
        ulong upper = Math.BigMul(low ^ Left, high ^ Right, out ulong lower);
        return upper ^ lower;
    }

    /// <summary>
    /// A string of 17 to 64 bytes: its words read from both ends, overlapping for the lengths
    /// between, folded pair by pair into two lanes and mixed once, with no call.
    /// </summary>
    /// <remarks>
    /// Two equal strings read the same words, which is all a bucket asks; a string's every byte
    /// lies in some word read, so strings that differ anywhere fold differently but by accident.
    /// The second lane is turned half a word before the two are joined, so that a string whose
    /// halves repeat does not cancel itself out.
    /// </remarks>
    private static ulong Words(ref byte first, int length)
    {
        ulong a = Unsafe.ReadUnaligned<ulong>(ref first);
        ulong b = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref first, 8));
        ulong c = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref first, length - 16));
        ulong d = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref first, length - 8));
        ulong left = Fold(a, c);
        ulong right = Fold(b, d);
        if (length > 32)
        {
            ulong e = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref first, 16));
            ulong f = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref first, 24));
            ulong g = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref first, length - 32));
            ulong h = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref first, length - 24));
            left = Fold(e ^ left, g);
            right = Fold(f ^ right, h);
        }

        ulong hash = ((left ^ BitOperations.RotateLeft(right, 32)) * Right) + (ulong)length;
        return hash ^ (hash >> 32);
    }

    /// <summary>The rest of a mix, once its first product has taken the seed.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong Finish(ulong hash)
    {
        hash ^= hash >> 29;
        hash *= Golden;
        hash ^= hash >> 32;
        return hash;
    }

    private static ulong DrawSeed()
    {
        Span<byte> bytes = stackalloc byte[16];
        Guid.NewGuid().TryWriteBytes(bytes);
        return BitConverter.ToUInt64(bytes);
    }
}
