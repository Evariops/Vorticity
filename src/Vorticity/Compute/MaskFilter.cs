using System;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using Vorticity.Arrays.Decoders.Canonical;

namespace Vorticity.Compute;

/// <summary>
/// The rows a selection keeps, taken by its mask rather than by its indices: a bit a row, 64 rows a
/// word, the kept rows packed in order.
/// </summary>
/// <remarks>
/// <para>
/// A gather by indices reads a row per index, an index per row kept. Where most rows are kept a
/// mask does the same work a vector at a time: AVX-512's compress packs the lanes a word keeps
/// into the low lanes of a register, always register to register -- never the compressing store,
/// which Zen 4 runs at a fraction of the speed -- and a whole-register store follows, the next one
/// overwriting what it wrote past the kept lanes. Bits are taken by <c>pext</c>, 64 rows an
/// instruction.
/// </para>
/// <para>
/// Every kernel writes exactly the rows it keeps: the whole-register stores stop a register short
/// of the destination's end, and the rows past that point are kept a bit at a time.
/// </para>
/// </remarks>
internal static class MaskFilter
{
    /// <summary>Whether values are compressed a register at a time: 512-bit vectors with every lane width's compress.</summary>
    internal static bool IsAccelerated => WordBytes.IsAccelerated && Avx512Vbmi2.IsSupported;

    /// <summary>
    /// The rows of <paramref name="source"/> whose bits <paramref name="mask"/> sets into
    /// <paramref name="destination"/>, which holds exactly as many.
    /// </summary>
    /// <typeparam name="T">A value of 1, 2, 4 or 8 bytes.</typeparam>
    internal static void Compress<T>(ReadOnlySpan<T> source, ReadOnlySpan<ulong> mask, Span<T> destination)
        where T : unmanaged
    {
        Debug.Assert(mask.Length == (source.Length + 63) >> 6);
        ref T from = ref MemoryMarshal.GetReference(source);
        ref T to = ref MemoryMarshal.GetReference(destination);
        int lanes = Vector512<T>.Count;
        int written = 0;
        int w = 0;
        if (IsAccelerated)
        {
            WordBytes spread = WordBytes.Create();
            ulong groupBits = lanes == 64 ? ulong.MaxValue : (1UL << lanes) - 1;

            // A whole word of rows in the source, and a register of room past what the word keeps.
            for (; ((w + 1) << 6) <= source.Length; w++)
            {
                ulong word = mask[w];
                if (written + BitOperations.PopCount(word) + lanes > destination.Length)
                {
                    break;
                }

                for (int group = 0; group < 64 / lanes; group++)
                {
                    Vector512<T> row = Vector512.LoadUnsafe(ref from, (nuint)((w << 6) + (group * lanes)));
                    Vector512<T> kept = spread.Lanes<T>(word, group);
                    Vector512<T> packed = Unsafe.SizeOf<T>() switch
                    {
                        1 => Avx512Vbmi2.Compress(Vector512<byte>.Zero, kept.AsByte(), row.AsByte()).As<byte, T>(),
                        2 => Avx512Vbmi2.Compress(Vector512<ushort>.Zero, kept.AsUInt16(), row.AsUInt16()).As<ushort, T>(),
                        4 => Avx512F.Compress(Vector512<uint>.Zero, kept.AsUInt32(), row.AsUInt32()).As<uint, T>(),
                        _ => Avx512F.Compress(Vector512<ulong>.Zero, kept.AsUInt64(), row.AsUInt64()).As<ulong, T>(),
                    };
                    packed.StoreUnsafe(ref to, (nuint)written);
                    written += BitOperations.PopCount((word >> (group * lanes)) & groupBits);
                }
            }
        }

        for (; w < mask.Length; w++)
        {
            ulong word = mask[w];
            while (word != 0)
            {
                destination[written++] = source[(w << 6) + BitOperations.TrailingZeroCount(word)];
                word &= word - 1;
            }
        }

        Debug.Assert(written == destination.Length);
    }

    /// <summary>
    /// <see cref="Compress{T}"/> for rows of sixteen bytes, a view or a 128-bit decimal, as two
    /// 64-bit lanes each: <paramref name="source"/> and <paramref name="destination"/> hold two
    /// words a row.
    /// </summary>
    /// <remarks>A row's bit is deposited into both of its lanes' bits, four rows a register.</remarks>
    internal static void CompressPairs(ReadOnlySpan<ulong> source, ReadOnlySpan<ulong> mask, Span<ulong> destination)
    {
        int rows = source.Length / 2;
        Debug.Assert(mask.Length == (rows + 63) >> 6);
        ref ulong from = ref MemoryMarshal.GetReference(source);
        ref ulong to = ref MemoryMarshal.GetReference(destination);
        int written = 0;
        int w = 0;
        if (IsAccelerated && Bmi2.X64.IsSupported)
        {
            WordBytes spread = WordBytes.Create();
            for (; ((w + 1) << 6) <= rows; w++)
            {
                ulong word = mask[w];
                if (written + (2 * BitOperations.PopCount(word)) + 8 > destination.Length)
                {
                    break;
                }

                for (int half = 0; half < 2; half++)
                {
                    // Thirty-two rows' bits, each twice: the lanes of 32 rows, eight groups of eight.
                    ulong doubled = Bmi2.X64.ParallelBitDeposit(word >> (32 * half), 0x5555_5555_5555_5555UL) * 3;
                    for (int group = 0; group < 8; group++)
                    {
                        nuint at = (nuint)((w << 7) + (half << 6) + (group << 3));
                        Vector512<ulong> kept = spread.Lanes<ulong>(doubled, group);
                        Avx512F.Compress(Vector512<ulong>.Zero, kept, Vector512.LoadUnsafe(ref from, at)).StoreUnsafe(ref to, (nuint)written);
                        written += BitOperations.PopCount((doubled >> (group << 3)) & 0xFF);
                    }
                }
            }
        }

        for (; w < mask.Length; w++)
        {
            ulong word = mask[w];
            while (word != 0)
            {
                int row = (w << 6) + BitOperations.TrailingZeroCount(word);
                destination[written++] = source[2 * row];
                destination[written++] = source[(2 * row) + 1];
                word &= word - 1;
            }
        }

        Debug.Assert(written == destination.Length);
    }

    /// <summary>
    /// The bits of <paramref name="rows"/> rows of <paramref name="bits"/> from bit
    /// <paramref name="offset"/> that <paramref name="mask"/> keeps, packed into
    /// <paramref name="destination"/> from its bit 0; returns how many of them are set.
    /// </summary>
    /// <param name="bits">The bitmap.</param>
    /// <param name="offset">The bit of row 0.</param>
    /// <param name="rows">The rows the bitmap holds.</param>
    /// <param name="mask">A bit a row, set for the rows kept.</param>
    /// <param name="destination">Cleared, and room for every kept bit.</param>
    internal static int Bits(ReadOnlySpan<byte> bits, int offset, int rows, ReadOnlySpan<ulong> mask, Span<byte> destination)
    {
        Debug.Assert(mask.Length == (rows + 63) >> 6);
        Span<ulong> words = MemoryMarshal.Cast<byte, ulong>(destination);
        int written = 0;
        int set = 0;
        ulong pending = 0;
        for (int w = 0; w < mask.Length; w++)
        {
            ulong keep = mask[w];
            if (keep == 0)
            {
                continue;
            }

            ulong source = BitWords.Load(bits, offset + (w << 6));
            ulong packed = Bmi2.X64.IsSupported ? Bmi2.X64.ParallelBitExtract(source, keep) : Extract(source, keep);
            int count = BitOperations.PopCount(keep);
            set += BitOperations.PopCount(packed);

            // Appended to the word being filled, and the bits past it to the next one.
            int used = written & 63;
            pending |= packed << used;
            if (used + count >= 64)
            {
                Store(destination, words, written >> 6, pending);
                pending = used == 0 ? 0 : packed >> (64 - used);
            }

            written += count;
        }

        if ((written & 63) != 0)
        {
            Store(destination, words, written >> 6, pending);
        }

        return set;
    }

    /// <summary>A word of packed bits into its place, or its bytes where the destination ends inside it.</summary>
    private static void Store(Span<byte> destination, Span<ulong> words, int index, ulong value)
    {
        if (index < words.Length)
        {
            words[index] = value;
            return;
        }

        for (int b = 0; (index << 3) + b < destination.Length; b++)
        {
            destination[(index << 3) + b] = (byte)(value >> (8 * b));
        }
    }

    /// <summary><c>pext</c> a bit at a time, where there is no BMI2.</summary>
    private static ulong Extract(ulong source, ulong keep)
    {
        ulong packed = 0;
        int at = 0;
        while (keep != 0)
        {
            packed |= ((source >> BitOperations.TrailingZeroCount(keep)) & 1) << at++;
            keep &= keep - 1;
        }

        return packed;
    }
}
