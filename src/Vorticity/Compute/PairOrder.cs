using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace Vorticity.Compute;

/// <summary>
/// Which way a column's adjacent values must go: up or down, ties allowed or not. Implemented by
/// structs of constants, so that a kernel instantiated on one is compiled for that way alone, the
/// others folded out.
/// </summary>
internal interface IPairOrder
{
    /// <summary>Whether each value must be at or below the one before it, rather than at or above.</summary>
    static abstract bool Descending { get; }

    /// <summary>Whether a value level with the one before it stops the lanes, as a fall does.</summary>
    static abstract bool Strict { get; }
}

/// <summary>The pairs of a column's adjacent values, a register at a time.</summary>
internal static class PairOrder
{
    /// <summary>Each value at or above the one before it: a sorted column's statistics.</summary>
    internal readonly struct Rising : IPairOrder
    {
        public static bool Descending => false;

        public static bool Strict => false;
    }

    /// <summary>Each value above the one before it: a tie left to the keys after.</summary>
    internal readonly struct StrictlyRising : IPairOrder
    {
        public static bool Descending => false;

        public static bool Strict => true;
    }

    /// <summary>Each value at or below the one before it.</summary>
    internal readonly struct Falling : IPairOrder
    {
        public static bool Descending => true;

        public static bool Strict => false;
    }

    /// <summary>Each value below the one before it.</summary>
    internal readonly struct StrictlyFalling : IPairOrder
    {
        public static bool Descending => true;

        public static bool Strict => true;
    }

    /// <summary>
    /// How far whole registers clear the pairs of <paramref name="values"/>, each element against the
    /// one before it by a second load one element behind, so that no lane crosses from one iteration
    /// to the next; the ties they pass set <paramref name="repeats"/>.
    /// </summary>
    /// <returns>
    /// Where a scalar walk of the pairs resumes: the end of the last whole window, or the start of
    /// the first window where a lane went the wrong way, was level under a strict order, or held a
    /// NaN; the walk meets it there in row order. 1 where no register serves.
    /// </returns>
    /// <remarks>
    /// Four lanes or more only. Two-lane registers, 64-bit values on 128 bits, are slower than a
    /// perfectly predicted scalar loop and are left to it: each iteration pays two loads and three
    /// compares to advance two elements, where the loop's every branch goes the same way until the
    /// one fall it leaves at.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int Cleared<T, TOrder>(ReadOnlySpan<T> values, ref bool repeats)
        where T : unmanaged, INumber<T>
        where TOrder : struct, IPairOrder
    {
        if (Vector512.IsHardwareAccelerated && Vector512<T>.IsSupported)
        {
            // Eight lanes or more at every width, 64-bit values included.
            return Lanes512<T, TOrder>(values, ref repeats);
        }

        if (Vector128.IsHardwareAccelerated && Vector128<T>.IsSupported && Vector128<T>.Count >= 4)
        {
            return Lanes128<T, TOrder>(values, ref repeats);
        }

        return 1;
    }

    private static int Lanes128<T, TOrder>(ReadOnlySpan<T> values, ref bool repeats)
        where T : unmanaged, INumber<T>
        where TOrder : struct, IPairOrder
    {
        int lanes = Vector128<T>.Count;
        ref T head = ref MemoryMarshal.GetReference(values);
        Vector128<T> equal = Vector128<T>.Zero;
        int i = 1;
        for (; i + lanes <= values.Length; i += lanes)
        {
            Vector128<T> current = Vector128.LoadUnsafe(ref head, (nuint)i);
            Vector128<T> previous = Vector128.LoadUnsafe(ref head, (nuint)(i - 1));

            // A NaN is unequal to itself; a lane the wrong way of its predecessor, or level with it
            // under a strict order, falls. Either sends the window back to the scalar walk.
            bool falls = TOrder.Descending
                ? TOrder.Strict ? Vector128.GreaterThanOrEqualAny(current, previous) : Vector128.GreaterThanAny(current, previous)
                : TOrder.Strict ? Vector128.LessThanOrEqualAny(current, previous) : Vector128.LessThanAny(current, previous);
            if (falls || !Vector128.EqualsAll(current, current))
            {
                break;
            }

            if (!TOrder.Strict)
            {
                equal |= Vector128.Equals(current, previous);
            }
        }

        repeats |= equal != Vector128<T>.Zero;
        return i;
    }

    /// <summary><see cref="Lanes128{T, TOrder}"/> on 512-bit vectors, which hold eight 64-bit lanes.</summary>
    private static int Lanes512<T, TOrder>(ReadOnlySpan<T> values, ref bool repeats)
        where T : unmanaged, INumber<T>
        where TOrder : struct, IPairOrder
    {
        int lanes = Vector512<T>.Count;
        ref T head = ref MemoryMarshal.GetReference(values);
        Vector512<T> equal = Vector512<T>.Zero;
        int i = 1;
        for (; i + lanes <= values.Length; i += lanes)
        {
            Vector512<T> current = Vector512.LoadUnsafe(ref head, (nuint)i);
            Vector512<T> previous = Vector512.LoadUnsafe(ref head, (nuint)(i - 1));
            bool falls = TOrder.Descending
                ? TOrder.Strict ? Vector512.GreaterThanOrEqualAny(current, previous) : Vector512.GreaterThanAny(current, previous)
                : TOrder.Strict ? Vector512.LessThanOrEqualAny(current, previous) : Vector512.LessThanAny(current, previous);
            if (falls || !Vector512.EqualsAll(current, current))
            {
                break;
            }

            if (!TOrder.Strict)
            {
                equal |= Vector512.Equals(current, previous);
            }
        }

        repeats |= equal != Vector512<T>.Zero;
        return i;
    }
}
