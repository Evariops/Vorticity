using System;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Vorticity.Compute;

/// <summary>Arithmetic progressions, written a vector at a time.</summary>
internal static class Progression
{
    /// <summary>
    /// Writes <paramref name="start"/> plus i <paramref name="step"/>s into slot i of
    /// <paramref name="values"/>, wrapping as two's-complement addition does.
    /// </summary>
    /// <remarks>
    /// A running sum is a loop-carried dependency one add deep, so a serial loop runs at the latency
    /// of an add per value however wide the machine is. <c>start + i * step</c> is the same sequence
    /// with no dependency at all: a vector seeded with the first values and advanced by a vector's
    /// worth of steps wraps exactly as the running sum does, the addition being associative. Four
    /// accumulators rather than one, so that four stores retire in the time one add takes and the
    /// store units are the only bound.
    /// </remarks>
    internal static void Fill<T>(Span<T> values, T start, T step)
        where T : unmanaged, IBinaryInteger<T>
    {
        int index = 0;
        T accumulator = start;
        if (Vector.IsHardwareAccelerated && Vector<T>.IsSupported && values.Length >= Vector<T>.Count)
        {
            int lanes = Vector<T>.Count;
            Vector<T> bump = new Vector<T>(unchecked(T.CreateTruncating(lanes) * step));
            ref T destination = ref MemoryMarshal.GetReference(values);
            Vector<T> v0 = unchecked((Vector<T>.Indices * step) + new Vector<T>(start));
            Vector<T> v1 = unchecked(v0 + bump);
            Vector<T> v2 = unchecked(v1 + bump);
            Vector<T> v3 = unchecked(v2 + bump);
            Vector<T> quadBump = unchecked(bump + bump + bump + bump);
            int quad = lanes * 4;
            for (; index <= values.Length - quad; index += quad)
            {
                v0.StoreUnsafe(ref destination, (nuint)index);
                v1.StoreUnsafe(ref destination, (nuint)(index + lanes));
                v2.StoreUnsafe(ref destination, (nuint)(index + (2 * lanes)));
                v3.StoreUnsafe(ref destination, (nuint)(index + (3 * lanes)));
                v0 = unchecked(v0 + quadBump);
                v1 = unchecked(v1 + quadBump);
                v2 = unchecked(v2 + quadBump);
                v3 = unchecked(v3 + quadBump);
            }

            // v0 is still the vector for `index`, all four having advanced together.
            for (; index <= values.Length - lanes; index += lanes)
            {
                v0.StoreUnsafe(ref destination, (nuint)index);
                v0 = unchecked(v0 + bump);
            }

            accumulator = v0[0];
        }

        for (; index < values.Length; index++)
        {
            values[index] = accumulator;
            accumulator = unchecked(accumulator + step);
        }
    }
}
