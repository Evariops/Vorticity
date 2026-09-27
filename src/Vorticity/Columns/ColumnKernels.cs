using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using Vorticity.Compute;

namespace Vorticity;

/// <summary>
/// The copies out of a column into CLR values that a row at a time would pay for with a test and a
/// branch a row: bits into <see cref="bool"/>, and values with a validity into nullables.
/// </summary>
/// <remarks>
/// A nullable is written whole, its flag and its value side by side as one wider integer: the
/// value widened and moved up past the flag, the flag set, and the pair cleared under the lanes a
/// validity word leaves out, which is how the runtime writes <c>null</c>.
/// </remarks>
internal static class ColumnKernels
{
    /// <summary>Row <c>i</c> of <paramref name="into"/> is bit <c>i % 64</c> of word <c>i / 64</c> of <paramref name="bits"/>.</summary>
    internal static void Bools(ReadOnlySpan<ulong> bits, Span<bool> into)
    {
        int i = 0;
        ref byte to = ref Unsafe.As<bool, byte>(ref MemoryMarshal.GetReference(into));
        if (WordBytes.IsAccelerated)
        {
            // A byte per bit: 64 rows a store.
            WordBytes spread = WordBytes.Create();
            for (; i <= into.Length - 64; i += 64)
            {
                spread.Ones(bits[i >> 6]).StoreUnsafe(ref to, (nuint)i);
            }
        }

        if (BitConverter.IsLittleEndian)
        {
            for (; i <= into.Length - 8; i += 8)
            {
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref to, i), Spread((byte)(bits[i >> 6] >> (i & 63))));
            }
        }

        for (; i < into.Length; i++)
        {
            into[i] = ((bits[i >> 6] >> (i & 63)) & 1UL) != 0;
        }
    }

    /// <summary>
    /// Row <c>i</c> of <paramref name="into"/> is bit <c>i</c> of <paramref name="bits"/>, or null
    /// where <paramref name="valid"/>, empty when every row is, clears it.
    /// </summary>
    internal static void NullableBools(ReadOnlySpan<ulong> valid, ReadOnlySpan<ulong> bits, Span<bool?> into)
    {
        int i = 0;
        if (WordBytes.IsAccelerated && Pairs<bool>.Laid)
        {
            // A pair of bytes a row as a 16-bit lane: the flag low, the value high.
            WordBytes spread = WordBytes.Create();
            Vector512<ushort> flag = Vector512.Create((ushort)0x0001);
            Vector512<ushort> value = Vector512.Create((ushort)0x0100);
            ref byte to = ref Unsafe.As<bool?, byte>(ref MemoryMarshal.GetReference(into));
            for (; i <= into.Length - 64; i += 64)
            {
                ulong present = valid.IsEmpty ? ulong.MaxValue : valid[i >> 6];
                ulong set = bits[i >> 6] & present;
                for (int half = 0; half < 2; half++)
                {
                    ((spread.Lanes<ushort>(present, half) & flag) | (spread.Lanes<ushort>(set, half) & value))
                        .AsByte().StoreUnsafe(ref to, (nuint)((2 * i) + (64 * half)));
                }
            }
        }

        for (; i < into.Length; i++)
        {
            into[i] = ColumnData.IsValid(valid, i) ? ((bits[i >> 6] >> (i & 63)) & 1UL) != 0 : null;
        }
    }

    /// <summary>
    /// Row <c>i</c> of <paramref name="into"/> is row <c>i</c> of <paramref name="values"/>, or null
    /// where <paramref name="valid"/>, empty when every row is, clears it.
    /// </summary>
    internal static void Nullables<T>(ReadOnlySpan<T> values, ReadOnlySpan<ulong> valid, Span<T?> into)
        where T : unmanaged
    {
        int i = 0;
        if (WordBytes.IsAccelerated && Pairs<T>.Laid)
        {
            WordBytes spread = WordBytes.Create();
            ref byte from = ref Unsafe.As<T, byte>(ref MemoryMarshal.GetReference(values));
            ref byte to = ref Unsafe.As<T?, byte>(ref MemoryMarshal.GetReference(into));
            int size = Unsafe.SizeOf<T>();
            for (; i <= into.Length - 64; i += 64)
            {
                ulong word = valid.IsEmpty ? ulong.MaxValue : valid[i >> 6];
                ref byte source = ref Unsafe.Add(ref from, i * size);
                ref byte target = ref Unsafe.Add(ref to, i * 2 * size);
                if (size == 1)
                {
                    for (int group = 0; group < 2; group++)
                    {
                        Vector512<ushort> v = Avx512BW.ConvertToVector512UInt16(Vector256.LoadUnsafe(ref source, (nuint)(32 * group)));
                        (((v << 8) | Vector512<ushort>.One) & spread.Lanes<ushort>(word, group)).AsByte().StoreUnsafe(ref target, (nuint)(64 * group));
                    }
                }
                else if (size == 2)
                {
                    ref ushort shorts = ref Unsafe.As<byte, ushort>(ref source);
                    for (int group = 0; group < 4; group++)
                    {
                        Vector512<uint> v = Avx512F.ConvertToVector512UInt32(Vector256.LoadUnsafe(ref shorts, (nuint)(16 * group)));
                        (((v << 16) | Vector512<uint>.One) & spread.Lanes<uint>(word, group)).AsByte().StoreUnsafe(ref target, (nuint)(64 * group));
                    }
                }
                else if (size == 4)
                {
                    ref uint ints = ref Unsafe.As<byte, uint>(ref source);
                    for (int group = 0; group < 8; group++)
                    {
                        Vector512<ulong> v = Avx512F.ConvertToVector512UInt64(Vector256.LoadUnsafe(ref ints, (nuint)(8 * group)));
                        (((v << 32) | Vector512<ulong>.One) & spread.Lanes<ulong>(word, group)).AsByte().StoreUnsafe(ref target, (nuint)(64 * group));
                    }
                }
                else
                {
                    // A pair is two 64-bit lanes: eight flags and eight values interleaved into
                    // two vectors by one two-table permute each.
                    ref ulong longs = ref Unsafe.As<byte, ulong>(ref source);
                    Vector512<ulong> low = Vector512.Create(0UL, 8, 1, 9, 2, 10, 3, 11);
                    Vector512<ulong> high = Vector512.Create(4UL, 12, 5, 13, 6, 14, 7, 15);
                    for (int group = 0; group < 8; group++)
                    {
                        Vector512<ulong> lanes = spread.Lanes<ulong>(word, group);
                        Vector512<ulong> flags = lanes & Vector512<ulong>.One;
                        Vector512<ulong> kept = Vector512.LoadUnsafe(ref longs, (nuint)(8 * group)) & lanes;
                        Avx512F.PermuteVar8x64x2(flags, low, kept).AsByte().StoreUnsafe(ref target, (nuint)(128 * group));
                        Avx512F.PermuteVar8x64x2(flags, high, kept).AsByte().StoreUnsafe(ref target, (nuint)((128 * group) + 64));
                    }
                }
            }
        }

        for (; i < into.Length; i++)
        {
            into[i] = ColumnData.IsValid(valid, i) ? values[i] : null;
        }
    }

    /// <summary>The eight bits of <paramref name="bits"/> as eight bytes, 0 or 1, the lowest bit first in memory.</summary>
    /// <remarks>
    /// The byte copied into each byte of a word, each byte kept to its own bit, and 127 added so that
    /// a set bit carries into the byte's top bit and no further.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong Spread(byte bits) =>
        ((((bits * 0x0101010101010101UL) & 0x8040201008040201UL) + 0x7F7F7F7F7F7F7F7FUL) >> 7) & 0x0101010101010101UL;

    /// <summary>Whether a <typeparamref name="T"/>? is laid out as the vector paths write it.</summary>
    private static class Pairs<T>
        where T : unmanaged
    {
        /// <summary>
        /// A value of 1, 2, 4 or 8 bytes, its nullable twice that size, with the flag as the first
        /// byte and the value at the offset of its own size -- what the runtime does, checked once
        /// rather than assumed.
        /// </summary>
        internal static readonly bool Laid = Probe();

        private static bool Probe()
        {
            int size = Unsafe.SizeOf<T>();
            if (size is not (1 or 2 or 4 or 8) || Unsafe.SizeOf<T?>() != 2 * size)
            {
                return false;
            }

            T value = default;
            MemoryMarshal.AsBytes(new Span<T>(ref value)).Fill(0xA5);
            T? probe = value;
            ReadOnlySpan<byte> bytes = MemoryMarshal.CreateReadOnlySpan(ref Unsafe.As<T?, byte>(ref probe), 2 * size);
            return bytes[0] == 1 && !bytes[size..].ContainsAnyExcept((byte)0xA5);
        }
    }
}
