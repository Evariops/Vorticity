using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace Vorticity.Compute;

/// <summary>
/// The three states a predicate takes on one row, one byte per row rather than two bitmaps: a byte
/// spares every kernel its own bit addressing, which is the one thing this layer must not get
/// wrong, and the representation is internal so a denser one can replace it without a public
/// change. The values are chosen so that conjunction and disjunction are masks rather than
/// branches, and so that selecting a row is a comparison against <see cref="True"/>. Vectorizing
/// these kernels pays where vectorizing a comparison does not: they read bytes and write bytes, so
/// the lanes line up and nothing has to be packed on the way out.
/// </summary>
internal static class Trilean
{
    /// <summary>The predicate is false for this row.</summary>
    internal const byte False = 0;

    /// <summary>The predicate is true for this row; the only state that selects it.</summary>
    internal const byte True = 1;

    /// <summary>An operand was null, so the predicate has no truth value here.</summary>
    internal const byte Unknown = 2;

    /// <summary>
    /// <c>left AND right</c>, in place over <paramref name="left"/>.
    /// </summary>
    /// <remarks>
    /// <c>unknown AND false = false</c>: a null operand does not make a conjunction unknown when
    /// the other side already decided it. This is the rule that lets a filter of the shape
    /// <c>a = 1 AND b = 2</c> skip a row whose <c>a</c> is null without reading <c>b</c>'s
    /// nullness into the answer.
    /// </remarks>
    /// <param name="left">The accumulator, overwritten.</param>
    /// <param name="right">The other operand; same length.</param>
    internal static void And(Span<byte> left, ReadOnlySpan<byte> right)
    {
        int i = 0;
        if (Vector128.IsHardwareAccelerated && left.Length >= Vector128<byte>.Count)
        {
            ref byte a0 = ref MemoryMarshal.GetReference(left);
            ref byte b0 = ref MemoryMarshal.GetReference(right);
            Vector128<byte> ones = Vector128.Create(True);
            Vector128<byte> unknowns = Vector128.Create(Unknown);
            Vector128<byte> zeros = Vector128<byte>.Zero;
            int last = left.Length - Vector128<byte>.Count;
            for (; i <= last; i += Vector128<byte>.Count)
            {
                Vector128<byte> a = Vector128.LoadUnsafe(ref a0, (nuint)i);
                Vector128<byte> b = Vector128.LoadUnsafe(ref b0, (nuint)i);

                // The mirror of Or: False wins outright, and between the other two it is True only
                // when both sides are.
                Vector128<byte> anyFalse = Vector128.Equals(a, zeros) | Vector128.Equals(b, zeros);
                Vector128<byte> bothTrue = Vector128.Equals(a, ones) & Vector128.Equals(b, ones);
                Vector128<byte> decided =
                    (bothTrue & ones) | Vector128.AndNot(unknowns, bothTrue);
                Vector128.AndNot(decided, anyFalse).StoreUnsafe(ref a0, (nuint)i);
            }
        }

        for (; i < left.Length; i++)
        {
            byte a = left[i];
            byte b = right[i];
            left[i] = a == False || b == False
                ? False
                : a == Unknown || b == Unknown ? Unknown : True;
        }
    }

    /// <summary>
    /// <c>left OR right</c>, in place over <paramref name="left"/>.
    /// </summary>
    /// <remarks><c>unknown OR true = true</c>, the mirror of the AND rule.</remarks>
    /// <param name="left">The accumulator, overwritten.</param>
    /// <param name="right">The other operand; same length.</param>
    internal static void Or(Span<byte> left, ReadOnlySpan<byte> right)
    {
        int i = 0;
        if (Vector128.IsHardwareAccelerated && left.Length >= Vector128<byte>.Count)
        {
            ref byte a0 = ref MemoryMarshal.GetReference(left);
            ref byte b0 = ref MemoryMarshal.GetReference(right);
            Vector128<byte> ones = Vector128.Create(True);
            Vector128<byte> unknowns = Vector128.Create(Unknown);
            int last = left.Length - Vector128<byte>.Count;
            for (; i <= last; i += Vector128<byte>.Count)
            {
                Vector128<byte> a = Vector128.LoadUnsafe(ref a0, (nuint)i);
                Vector128<byte> b = Vector128.LoadUnsafe(ref b0, (nuint)i);

                // True wins outright; otherwise an Unknown on either side survives. Both tests are
                // masks, so the choice between them is an AndNot rather than a branch.
                Vector128<byte> anyTrue = Vector128.Equals(a, ones) | Vector128.Equals(b, ones);
                Vector128<byte> anyUnknown =
                    Vector128.Equals(a, unknowns) | Vector128.Equals(b, unknowns);
                Vector128<byte> result =
                    (anyTrue & ones) | Vector128.AndNot(anyUnknown & unknowns, anyTrue);
                result.StoreUnsafe(ref a0, (nuint)i);
            }
        }

        for (; i < left.Length; i++)
        {
            byte a = left[i];
            byte b = right[i];
            left[i] = a == True || b == True
                ? True
                : a == Unknown || b == Unknown ? Unknown : False;
        }
    }

    /// <summary><c>NOT values</c>, in place. <c>NOT unknown = unknown</c>.</summary>
    /// <param name="values">The operand, overwritten.</param>
    internal static void Not(Span<byte> values)
    {
        int i = 0;
        if (Vector128.IsHardwareAccelerated && values.Length >= Vector128<byte>.Count)
        {
            ref byte a0 = ref MemoryMarshal.GetReference(values);
            Vector128<byte> ones = Vector128.Create(True);
            Vector128<byte> unknowns = Vector128.Create(Unknown);
            int last = values.Length - Vector128<byte>.Count;
            for (; i <= last; i += Vector128<byte>.Count)
            {
                Vector128<byte> a = Vector128.LoadUnsafe(ref a0, (nuint)i);

                // False and True are 0 and 1, so flipping them is one XOR; Unknown keeps itself.
                Vector128<byte> isUnknown = Vector128.Equals(a, unknowns);
                Vector128<byte> result =
                    (isUnknown & unknowns) | Vector128.AndNot(a ^ ones, isUnknown);
                result.StoreUnsafe(ref a0, (nuint)i);
            }
        }

        for (; i < values.Length; i++)
        {
            byte value = values[i];
            values[i] = value == Unknown ? Unknown : value == True ? False : True;
        }
    }

    /// <summary>Fills <paramref name="values"/> with one state.</summary>
    /// <param name="values">The span to fill.</param>
    /// <param name="state">The state.</param>
    internal static void Fill(Span<byte> values, byte state) => values.Fill(state);

    /// <summary>
    /// Turns a comparison result into a state, given whether the row's value was null.
    /// </summary>
    /// <param name="isValid">Whether the row held a value.</param>
    /// <param name="result">The comparison's result, when it held one.</param>
    internal static byte From(bool isValid, bool result) =>
        !isValid ? Unknown : result ? True : False;

    /// <summary>How many rows the predicate selected.</summary>
    /// <param name="values">The evaluated predicate.</param>
    /// <remarks>The library's count, which compares a vector of states at a time.</remarks>
    internal static int CountTrue(ReadOnlySpan<byte> values) => values.Count(True);

    /// <summary>
    /// The rows whose state is <paramref name="state"/>, or is not when <paramref name="equal"/>
    /// is false, as a bitmap of 64-bit words: bit <c>i % 64</c> of word <c>i / 64</c> for row
    /// <c>i</c>.
    /// </summary>
    /// <param name="values">One state per row.</param>
    /// <param name="state">The state asked about.</param>
    /// <param name="equal">Whether a row's bit says it holds the state, or that it does not.</param>
    /// <param name="words">
    /// At least <c>ceil(rows / 64)</c> words, of which exactly those are written: the last one's bits
    /// past the rows are clear whichever way the question is asked.
    /// </param>
    /// <returns>How many bits were set.</returns>
    /// <remarks>
    /// A compare of 64 states is one mask of 64 bits where there are 512-bit vectors (`vpcmpeqb`
    /// into a mask register, one `kmovq` out) and four 16-bit ones where there are 128-bit vectors,
    /// against a test and a branch per row. The count is the words' popcount, which the caller
    /// would otherwise take with a second pass over the states.
    /// </remarks>
    internal static int ToWords(ReadOnlySpan<byte> values, byte state, bool equal, Span<ulong> words)
    {
        int rows = values.Length;
        int full = rows >> 6;
        if (words.Length < (rows + 63) >> 6)
        {
            throw new ArgumentException("The words cannot hold a bit per row.", nameof(words));
        }

        ref byte from = ref MemoryMarshal.GetReference(values);
        ref ulong into = ref MemoryMarshal.GetReference(words);
        ulong flip = equal ? 0 : ulong.MaxValue;
        int count = 0;
        int w = 0;
        if (Vector512.IsHardwareAccelerated)
        {
            Vector512<byte> wanted = Vector512.Create(state);
            for (; w < full; w++)
            {
                ulong word = Vector512.Equals(Vector512.LoadUnsafe(ref from, (nuint)w << 6), wanted)
                    .ExtractMostSignificantBits() ^ flip;
                Unsafe.Add(ref into, w) = word;
                count += BitOperations.PopCount(word);
            }
        }
        else if (Vector128.IsHardwareAccelerated)
        {
            Vector128<byte> wanted = Vector128.Create(state);
            for (; w < full; w++)
            {
                ref byte at = ref Unsafe.Add(ref from, (nint)w << 6);
                ulong word = Vector128.Equals(Vector128.LoadUnsafe(ref at), wanted).ExtractMostSignificantBits()
                    | ((ulong)Vector128.Equals(Vector128.LoadUnsafe(ref at, 16), wanted).ExtractMostSignificantBits() << 16)
                    | ((ulong)Vector128.Equals(Vector128.LoadUnsafe(ref at, 32), wanted).ExtractMostSignificantBits() << 32)
                    | ((ulong)Vector128.Equals(Vector128.LoadUnsafe(ref at, 48), wanted).ExtractMostSignificantBits() << 48);
                word ^= flip;
                Unsafe.Add(ref into, w) = word;
                count += BitOperations.PopCount(word);
            }
        }

        // What the vectors left, a word at a time: every word where there are none, and the last,
        // partial one, whose bits past the rows are never set.
        for (int row = w << 6; row < rows; row += 64)
        {
            int take = Math.Min(64, rows - row);
            ulong word = 0;
            for (int k = 0; k < take; k++)
            {
                word |= (ulong)((Unsafe.Add(ref from, row + k) == state) == equal ? 1 : 0) << k;
            }

            Unsafe.Add(ref into, row >> 6) = word;
            count += BitOperations.PopCount(word);
        }

        return count;
    }
}
