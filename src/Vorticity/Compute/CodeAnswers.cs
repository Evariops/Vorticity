using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Types;

namespace Vorticity.Compute;

/// <summary>
/// A predicate answered once per distinct value of a dictionary and spread over its rows by their
/// codes: each row's verdict is the one its code names, and a null row's is unknown.
/// </summary>
/// <remarks>
/// <para>
/// The work per row is one indirection through a table of a byte per value, which the scalar loop
/// pays with a load, a bounds check and a store. With AVX-512 VBMI a dictionary of up to 128
/// values is its table in one or two registers, and a byte permute looks up 64 rows at once
/// (`vpermb`, or `vpermi2b` for the second 64): the codes are narrowed to bytes, their largest is
/// held against the dictionary's length, and the nulls take unknown from their validity word.
/// </para>
/// <para>
/// The codes are read unsigned at their own width. A negative signed code reads as one at or above
/// half the width's range, so the limit a signed code is held to is the smaller of the dictionary's
/// length and that half: a negative code is outside the dictionary as it is when read signed.
/// </para>
/// </remarks>
internal static class CodeAnswers
{
    /// <summary>Spreads <paramref name="answers"/> over the rows by their codes.</summary>
    /// <param name="answers">One verdict per value of the dictionary.</param>
    /// <param name="codes">One code per row, at <paramref name="codesPType"/>'s width.</param>
    /// <param name="codesPType">The codes' physical type, any integer.</param>
    /// <param name="validity">The rows' validity bitmap; ignored when <paramref name="allValid"/>.</param>
    /// <param name="validityOffset">The bit of row 0 in <paramref name="validity"/>.</param>
    /// <param name="allValid">Whether no row is null.</param>
    /// <param name="destination">One verdict per row.</param>
    /// <returns>
    /// -1, or the first valid row whose code names no value, before which every row is answered:
    /// the caller reports it. A null row's code is never held to the dictionary.
    /// </returns>
    internal static int Expand(
        ReadOnlySpan<byte> answers, ReadOnlySpan<byte> codes, PType codesPType,
        ReadOnlySpan<byte> validity, int validityOffset, bool allValid, Span<byte> destination)
    {
        bool signed = codesPType.IsSignedInteger();
        return codesPType.ByteWidth() switch
        {
            1 => Expand<byte>(answers, codes, signed, validity, validityOffset, allValid, destination),
            2 => Expand<ushort>(answers, codes, signed, validity, validityOffset, allValid, destination),
            4 => Expand<uint>(answers, codes, signed, validity, validityOffset, allValid, destination),
            _ => Expand<ulong>(answers, codes, signed, validity, validityOffset, allValid, destination),
        };
    }

    private static int Expand<T>(
        ReadOnlySpan<byte> answers, ReadOnlySpan<byte> codeBytes, bool signed,
        ReadOnlySpan<byte> validity, int validityOffset, bool allValid, Span<byte> destination)
        where T : unmanaged, IBinaryInteger<T>, IUnsignedNumber<T>
    {
        int rows = destination.Length;
        ReadOnlySpan<T> codes = MemoryMarshal.Cast<byte, T>(codeBytes)[..rows];

        // Past this, a code names no value: the dictionary's end, or for a signed code the first
        // one that reads negative.
        ulong limit = (ulong)answers.Length;
        if (signed)
        {
            limit = Math.Min(limit, 1UL << ((Unsafe.SizeOf<T>() * 8) - 1));
        }

        int row = 0;
        if (answers.Length <= 128 && WordBytes.IsAccelerated && Avx512Vbmi.IsSupported)
        {
            row = Permuted(answers, codes, limit, validity, validityOffset, allValid, destination, out int bad);
            if (bad >= 0)
            {
                return bad;
            }
        }

        if (limit == 0)
        {
            return Rows(answers, codes, limit, validity, validityOffset, allValid, destination, row, rows);
        }

        return allValid
            ? Dense(answers, codes, limit, destination, row, rows)
            : Clamped(answers, codes, limit, validity, validityOffset, destination, row, rows);
    }

    /// <summary>Rows <c>[from, to)</c> of a column without nulls, a load and a store each.</summary>
    /// <returns>-1, or the first row whose code is past the limit.</returns>
    private static int Dense<T>(
        ReadOnlySpan<byte> answers, ReadOnlySpan<T> codes, ulong limit, Span<byte> destination, int from, int to)
        where T : unmanaged, IBinaryInteger<T>, IUnsignedNumber<T>
    {
        ref T code0 = ref MemoryMarshal.GetReference(codes);
        ref byte answer0 = ref MemoryMarshal.GetReference(answers);
        ref byte into = ref MemoryMarshal.GetReference(destination);
        for (int row = from; row < to; row++)
        {
            ulong code = ulong.CreateTruncating(Unsafe.Add(ref code0, row));
            if (code >= limit)
            {
                return row;
            }

            Unsafe.Add(ref into, row) = Unsafe.Add(ref answer0, (nint)code);
        }

        return -1;
    }

    /// <summary>
    /// Rows <c>[from, to)</c> of a column with nulls, without a branch on the validity: every row
    /// looks its code up, a code past the limit clamped to the first value, and the nulls are then
    /// made unknown from their words. A clamped code is looked for again only when there was one,
    /// and reported only when a valid row holds it.
    /// </summary>
    /// <returns>-1, or the first valid row whose code is past the limit.</returns>
    private static int Clamped<T>(
        ReadOnlySpan<byte> answers, ReadOnlySpan<T> codes, ulong limit,
        ReadOnlySpan<byte> validity, int validityOffset, Span<byte> destination, int from, int to)
        where T : unmanaged, IBinaryInteger<T>, IUnsignedNumber<T>
    {
        ref T code0 = ref MemoryMarshal.GetReference(codes);
        ref byte answer0 = ref MemoryMarshal.GetReference(answers);
        ref byte into = ref MemoryMarshal.GetReference(destination);
        bool outside = false;
        for (int row = from; row < to; row++)
        {
            ulong code = ulong.CreateTruncating(Unsafe.Add(ref code0, row));
            bool past = code >= limit;
            outside |= past;
            Unsafe.Add(ref into, row) = Unsafe.Add(ref answer0, past ? 0 : (nint)code);
        }

        if (outside)
        {
            int bad = Rows(answers, codes, limit, validity, validityOffset, allValid: false, destination, from, to);
            if (bad >= 0)
            {
                return bad;
            }
        }

        ComparisonKernels.MarkUnknown(validity, validityOffset + from, destination[from..to]);
        return -1;
    }

    /// <summary>
    /// Whole blocks of 64 rows by byte permutes of the answers, until a block holds a code past the
    /// limit: that block is answered row by row, which finds the row, or passes it when every
    /// such code is a null row's.
    /// </summary>
    /// <returns>The rows answered, a multiple of 64.</returns>
    private static int Permuted<T>(
        ReadOnlySpan<byte> answers, ReadOnlySpan<T> codes, ulong limit,
        ReadOnlySpan<byte> validity, int validityOffset, bool allValid, Span<byte> destination, out int bad)
        where T : unmanaged, IBinaryInteger<T>, IUnsignedNumber<T>
    {
        // The table, the answers padded to two registers; a padding entry is never selected.
        Span<byte> table = stackalloc byte[128];
        table.Clear();
        answers.CopyTo(table);
        Vector512<byte> low = Vector512.Create<byte>(table[..64]);
        Vector512<byte> high = Vector512.Create<byte>(table[64..]);
        bool two = answers.Length > 64;

        // A block's codes pass when none exceeds the last code in range; a limit of 0, an empty
        // dictionary, passes none.
        Vector512<T> last = Vector512.Create(T.CreateTruncating(limit == 0 ? 0 : limit - 1));
        bool none = limit == 0;
        WordBytes spread = WordBytes.Create();
        Vector512<byte> unknown = Vector512.Create(Trilean.Unknown);
        ref T from = ref MemoryMarshal.GetReference(codes);
        ref byte into = ref MemoryMarshal.GetReference(destination);
        int rows = destination.Length;
        int row = 0;
        bad = -1;
        for (; row <= rows - 64; row += 64)
        {
            Vector512<byte> index = Narrowed(ref Unsafe.Add(ref from, row), out Vector512<T> greatest);
            if (none || Vector512.GreaterThanAny(greatest, last))
            {
                bad = Rows(answers, codes, limit, validity, validityOffset, allValid, destination, row, row + 64);
                if (bad >= 0)
                {
                    return row;
                }

                continue;
            }

            Vector512<byte> verdicts = two
                ? Avx512Vbmi.PermuteVar64x8x2(low, index, high)
                : Avx512Vbmi.PermuteVar64x8(low, index);
            if (!allValid)
            {
                verdicts = Vector512.ConditionalSelect(
                    spread.Clear(BitWords.Load(validity, validityOffset + row)), unknown, verdicts);
            }

            verdicts.StoreUnsafe(ref into, (nuint)row);
        }

        return row;
    }

    /// <summary>Sixty-four codes as bytes, their low bits, and the largest of them at their own width.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector512<byte> Narrowed<T>(ref T at, out Vector512<T> greatest)
        where T : unmanaged, IBinaryInteger<T>, IUnsignedNumber<T>
    {
        if (typeof(T) == typeof(byte))
        {
            Vector512<byte> bytes = Vector512.LoadUnsafe(ref Unsafe.As<T, byte>(ref at));
            greatest = bytes.As<byte, T>();
            return bytes;
        }

        if (typeof(T) == typeof(ushort))
        {
            ref ushort first = ref Unsafe.As<T, ushort>(ref at);
            Vector512<ushort> a = Vector512.LoadUnsafe(ref first);
            Vector512<ushort> b = Vector512.LoadUnsafe(ref first, 32);
            greatest = Vector512.Max(a, b).As<ushort, T>();
            return Vector512.Narrow(a, b);
        }

        if (typeof(T) == typeof(uint))
        {
            ref uint first = ref Unsafe.As<T, uint>(ref at);
            Vector512<uint> a = Vector512.LoadUnsafe(ref first);
            Vector512<uint> b = Vector512.LoadUnsafe(ref first, 16);
            Vector512<uint> c = Vector512.LoadUnsafe(ref first, 32);
            Vector512<uint> d = Vector512.LoadUnsafe(ref first, 48);
            greatest = Vector512.Max(Vector512.Max(a, b), Vector512.Max(c, d)).As<uint, T>();
            return Vector512.Narrow(Vector512.Narrow(a, b), Vector512.Narrow(c, d));
        }

        ref ulong word = ref Unsafe.As<T, ulong>(ref at);
        Vector512<ulong> w0 = Vector512.LoadUnsafe(ref word);
        Vector512<ulong> w1 = Vector512.LoadUnsafe(ref word, 8);
        Vector512<ulong> w2 = Vector512.LoadUnsafe(ref word, 16);
        Vector512<ulong> w3 = Vector512.LoadUnsafe(ref word, 24);
        Vector512<ulong> w4 = Vector512.LoadUnsafe(ref word, 32);
        Vector512<ulong> w5 = Vector512.LoadUnsafe(ref word, 40);
        Vector512<ulong> w6 = Vector512.LoadUnsafe(ref word, 48);
        Vector512<ulong> w7 = Vector512.LoadUnsafe(ref word, 56);
        greatest = Vector512.Max(
            Vector512.Max(Vector512.Max(w0, w1), Vector512.Max(w2, w3)),
            Vector512.Max(Vector512.Max(w4, w5), Vector512.Max(w6, w7))).As<ulong, T>();
        return Vector512.Narrow(
            Vector512.Narrow(Vector512.Narrow(w0, w1), Vector512.Narrow(w2, w3)),
            Vector512.Narrow(Vector512.Narrow(w4, w5), Vector512.Narrow(w6, w7)));
    }

    /// <summary>
    /// Rows <c>[from, to)</c> one at a time, the codes' width resolved and the validity read as
    /// bits rather than through a switch per row.
    /// </summary>
    /// <returns>-1, or the first valid row whose code is past the limit.</returns>
    private static int Rows<T>(
        ReadOnlySpan<byte> answers, ReadOnlySpan<T> codes, ulong limit,
        ReadOnlySpan<byte> validity, int validityOffset, bool allValid, Span<byte> destination, int from, int to)
        where T : unmanaged, IBinaryInteger<T>, IUnsignedNumber<T>
    {
        for (int row = from; row < to; row++)
        {
            if (!allValid)
            {
                int at = validityOffset + row;
                if (((validity[at >> 3] >> (at & 7)) & 1) == 0)
                {
                    destination[row] = Trilean.Unknown;
                    continue;
                }
            }

            ulong code = ulong.CreateTruncating(codes[row]);
            if (code >= limit)
            {
                return row;
            }

            destination[row] = answers[(int)code];
        }

        return -1;
    }
}
