// Three-valued logic, one byte per row - docs/08-semantics.md §3.
//
// A byte rather than two bitmaps, deliberately. The bitmap form is denser and is what a vectorized
// evaluator will want, but it makes every kernel do its own bit addressing, and the one thing this
// layer must not get wrong is which of {true, false, unknown} a row is in. One byte per row costs
// 8 KiB for a default batch, is rented rather than allocated, and keeps every kernel a flat loop
// over an index. The representation is internal precisely so it can become bitmaps later without a
// public change.
//
// The values are chosen so that AND and OR are table lookups rather than branches, and so that the
// final selection -- "a row is returned only when the filter evaluates to true" -- is `== True`.
using System;

namespace Vorticity.Compute;

/// <summary>The three states a predicate takes on one row.</summary>
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
    /// the other side already decided it. This is the rule that makes
    /// <c>WHERE a = 1 AND b = 2</c> skip a row whose <c>a</c> is null without reading <c>b</c>'s
    /// nullness into the answer.
    /// </remarks>
    /// <param name="left">The accumulator, overwritten.</param>
    /// <param name="right">The other operand; same length.</param>
    internal static void And(Span<byte> left, ReadOnlySpan<byte> right)
    {
        for (int i = 0; i < left.Length; i++)
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
        for (int i = 0; i < left.Length; i++)
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
        for (int i = 0; i < values.Length; i++)
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
    internal static int CountTrue(ReadOnlySpan<byte> values)
    {
        int count = 0;
        for (int i = 0; i < values.Length; i++)
        {
            if (values[i] == True)
            {
                count++;
            }
        }

        return count;
    }
}
