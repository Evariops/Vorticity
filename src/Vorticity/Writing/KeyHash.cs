// The one bucket hash of the write path, shared by the chooser's row comparer and the ingest-time
// distinct table (docs/11-write-strategy.md §3.2.1), so that "the same value hashes the same" is a
// property of one function and not an agreement between two.
//
// IT CANNOT MOVE A BYTE OF THE FILE, and every consumer relies on that: a hash picks a bucket, an
// exact comparison settles every collision, and a dictionary code is handed out by order of first
// appearance, which is row order. Changing this function changes which chain a value lands in and
// nothing else -- since the values in one chain are distinct, at most one can compare equal
// whatever the order. `WrittenSizeTests` is the proof and it is byte-exact.
//
// TWO ARMS FOR STRINGS, AND THE BRANCH IS ON THE LENGTH so that two equal values always take the
// same one. The corpus calls this with 3,9 bytes on average (464 MB over 120 M calls), and a value
// that fits a view fits sixteen: up to sixteen bytes fold into one word with two overlapping loads
// -- the trick xxhash itself uses for its short inputs -- and take the single multiply-xorshift a
// fixed-width value takes. Above that, XxHash3-64: dedicated paths under 128 and 240 bytes, a
// vectorised stripe loop beyond, the hash upstream's Bloom filter uses, from the one first-party
// package docs/03-architecture.md §1 admits. A Bloom filter needs XxHash3 on EVERY value, short
// ones included, so when the block hash buffer of §3.2.1 arrives the short arm will be conditioned
// on whether a filter is live rather than on the length alone -- a reason to keep both arms here.
//
// WHAT THE SHORT ARM DOES NOT FIX, recorded because it was guessed wrong twice before it was
// isolated: `struct` and `varbin` are 3 % to 6 % slower under XxHash3 than under the FNV-1a this
// replaced (0,47 -> 0,50 and 0,35 -> 0,36 on a restricted axis), and widening the arm from eight
// bytes to sixteen moved neither -- their values are longer than a view. The same isolation puts
// `zstd` at 1,25 under FNV-1a and 1,05 under XxHash3. Taken with eyes open.
using System;
using System.IO.Hashing;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Vorticity.Writing;

/// <summary>Bucket hashes for the write path's tables. Not checksums: collisions are compared away.</summary>
internal static class KeyHash
{
    private const ulong Golden = 0x9E3779B97F4A7C15UL;
    private const ulong Spread = 0xBF58476D1CE4E5B9UL;

    /// <summary>A whole fixed-width value mixed in one step.</summary>
    /// <remarks>
    /// Multiply-xorshift: the multiply spreads the low bits upward and the shifts fold them back
    /// down, so consecutive integers -- the common shape of an id column -- land far apart. The
    /// final fold is what makes the LOW bits good, which is what a power-of-two mask reads.
    /// </remarks>
    /// <param name="value">The value's bits, zero-extended.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ulong Mix(ulong value)
    {
        ulong hash = value * Golden;
        hash ^= hash >> 29;
        hash *= Spread;
        hash ^= hash >> 32;
        return hash;
    }

    /// <summary>A byte string's hash: folded when it fits a view, XxHash3-64 beyond.</summary>
    /// <param name="bytes">The value's bytes.</param>
    internal static ulong Bytes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > 16)
        {
            return XxHash3.HashToUInt64(bytes);
        }

        ref byte first = ref MemoryMarshal.GetReference(bytes);
        ulong word;
        if (bytes.Length >= 8)
        {
            ulong low = Unsafe.ReadUnaligned<ulong>(ref first);
            ulong high = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref first, bytes.Length - 8));
            word = low ^ (high * Spread);
        }
        else if (bytes.Length >= 4)
        {
            // The two reads OVERLAP for the lengths between the powers, which is what keeps both
            // arms branchless -- the same trick xxhash itself uses for its short inputs.
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

        // The length is mixed in rather than concatenated: "a" and "a\0" hold the same word and
        // would otherwise share a bucket for no reason.
        return Mix(word ^ ((ulong)bytes.Length * Golden));
    }
}
