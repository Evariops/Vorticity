using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

using Vorticity.Arrays.Decoders.Canonical;

namespace Vorticity.Compute;

/// <summary>
/// The rows a filter selected, as the ascending list of their indices: from a verdict per row, or
/// from a bitmap of them.
/// </summary>
/// <remarks>
/// <para>
/// A row at a time, each index is written at the next slot and kept or not by its verdict, with no
/// branch on the verdict, which follows the data. With AVX-512, sixteen rows at a time: their
/// verdicts are a mask, the mask compresses a vector of the sixteen indices to the kept ones, and
/// the vector is stored whole at the next slot, the lanes past the kept ones written over by the
/// next store. The compress is into a register, then an ordinary store: Zen 4 microcodes a compress
/// into memory.
/// </para>
/// <para>
/// A bitmap is read a word at a time, so a word of no selected row costs a test and a word of
/// every row sixty-four consecutive indices; only a mixed word is compressed, or where there is no
/// AVX-512 walked a set bit at a time.
/// </para>
/// </remarks>
internal static class RowIndices
{
    /// <summary>The rows whose state is <see cref="Trilean.True"/>.</summary>
    /// <param name="states">One state per row.</param>
    /// <param name="indices">Room for every row, selected or not: each is written before it is kept.</param>
    /// <returns>How many rows were selected; they are the first entries of <paramref name="indices"/>.</returns>
    internal static int FromStates(ReadOnlySpan<byte> states, Span<int> indices)
    {
        int rows = states.Length;
        if (indices.Length < rows)
        {
            throw new ArgumentException("The indices cannot hold a slot per row.", nameof(indices));
        }

        ref byte from = ref MemoryMarshal.GetReference(states);
        ref int into = ref MemoryMarshal.GetReference(indices);
        int count = 0;
        int row = 0;
        if (Vector512.IsHardwareAccelerated && Avx512F.IsSupported)
        {
            Vector512<int> selected = Vector512.Create((int)Trilean.True);
            Vector512<int> next = Vector512.CreateSequence(0, 1);
            Vector512<int> step = Vector512.Create(16);
            for (; row <= rows - 16; row += 16)
            {
                Vector512<int> keep = Vector512.Equals(
                    Avx512F.ConvertToVector512Int32(Vector128.LoadUnsafe(ref from, (nuint)row)), selected);
                Avx512F.Compress(Vector512<int>.Zero, keep, next).StoreUnsafe(ref into, (nuint)count);
                count += BitOperations.PopCount(keep.ExtractMostSignificantBits());
                next += step;
            }
        }

        for (; row < rows; row++)
        {
            Unsafe.Add(ref into, count) = row;
            count += Unsafe.Add(ref from, row) == Trilean.True ? 1 : 0;
        }

        return count;
    }

    /// <summary>
    /// The rows whose bit is set in <paramref name="bits"/> and, unless <paramref name="allValid"/>,
    /// in <paramref name="validity"/> too.
    /// </summary>
    /// <param name="bits">The rows' bits.</param>
    /// <param name="offset">The bit of row 0 in <paramref name="bits"/>.</param>
    /// <param name="validity">The rows' validity; ignored when <paramref name="allValid"/>.</param>
    /// <param name="validityOffset">The bit of row 0 in <paramref name="validity"/>.</param>
    /// <param name="allValid">Whether every row is valid.</param>
    /// <param name="rows">How many rows.</param>
    /// <param name="indices">Room for every row, as for <see cref="FromStates"/>.</param>
    /// <returns>How many rows were selected.</returns>
    internal static int FromBits(
        ReadOnlySpan<byte> bits, int offset, ReadOnlySpan<byte> validity, int validityOffset, bool allValid,
        int rows, Span<int> indices)
    {
        if (indices.Length < rows)
        {
            throw new ArgumentException("The indices cannot hold a slot per row.", nameof(indices));
        }

        ref int into = ref MemoryMarshal.GetReference(indices);
        bool wide = Vector512.IsHardwareAccelerated && Avx512F.IsSupported;
        int count = 0;
        for (int row = 0; row < rows; row += 64)
        {
            int take = Math.Min(64, rows - row);
            ulong word = BitWords.Load(bits, offset + row) & BitWords.Mask(take);
            if (!allValid)
            {
                word &= BitWords.Load(validity, validityOffset + row);
            }

            if (word == 0)
            {
                continue;
            }

            if (word == ulong.MaxValue)
            {
                Consecutive(ref Unsafe.Add(ref into, count), row);
                count += 64;
                continue;
            }

            // A compress stores sixteen slots whatever it keeps, which the last, partial word of the
            // rows may not have room for past its own rows: that one is walked.
            count = wide && take == 64
                ? Compressed(word, row, ref into, count)
                : Walked(word, row, ref into, count);
        }

        return count;
    }

    /// <summary>Sixty-four consecutive indices from <paramref name="first"/>.</summary>
    private static void Consecutive(ref int into, int first)
    {
        if (Vector512.IsHardwareAccelerated)
        {
            Vector512<int> next = Vector512.CreateSequence(first, 1);
            Vector512<int> step = Vector512.Create(16);
            for (int i = 0; i < 64; i += 16)
            {
                next.StoreUnsafe(ref into, (nuint)i);
                next += step;
            }

            return;
        }

        for (int i = 0; i < 64; i++)
        {
            Unsafe.Add(ref into, i) = first + i;
        }
    }

    /// <summary>A mixed word's rows, sixteen bits at a time through a compress.</summary>
    private static int Compressed(ulong word, int row, ref int into, int count)
    {
        Vector512<int> bit = Vector512.Create(1, 2, 4, 8, 16, 32, 64, 128, 256, 512, 1024, 2048, 4096, 8192, 16384, 32768);
        Vector512<int> next = Vector512.CreateSequence(row, 1);
        Vector512<int> step = Vector512.Create(16);
        for (int i = 0; i < 64; i += 16)
        {
            int part = (int)(word >> i) & 0xFFFF;
            if (part != 0)
            {
                Vector512<int> keep = Vector512.Equals(Vector512.Create(part) & bit, bit);
                Avx512F.Compress(Vector512<int>.Zero, keep, next).StoreUnsafe(ref into, (nuint)count);
                count += BitOperations.PopCount((uint)part);
            }

            next += step;
        }

        return count;
    }

    /// <summary>A mixed word's rows, a set bit at a time.</summary>
    private static int Walked(ulong word, int row, ref int into, int count)
    {
        while (word != 0)
        {
            Unsafe.Add(ref into, count++) = row + BitOperations.TrailingZeroCount(word);
            word &= word - 1;
        }

        return count;
    }
}
