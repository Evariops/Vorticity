// The ALP scaling tables - alp-0.0.4/src/alp/mod.rs, `impl ALPFloat for f32` / `for f64`.
//
// decode_single is `from_int(encoded) * F10[f] * IF10[e]`, and the ONLY way to reproduce it bit for
// bit is to reproduce it literally: the same two table lookups, the same order, the same precision.
//
// The inverse table is not 1/F10[e] computed at runtime. 0.1 is not representable in binary
// floating point, so `x * IF10[1]` and `x / F10[1]` round differently for some inputs, and a
// decoder that "simplifies" one into the other disagrees with the reference on real data rather
// than on adversarial data. Likewise the f32 tables are f32 literals, not narrowed f64 ones:
// 10^10 rounds to a different f32 depending on which way it is reached.
//
// The tables run past MAX_EXPONENT upstream (24 entries for f64, whose MAX_EXPONENT is 18), and
// that is deliberate there - the encoder searches only up to MAX_EXPONENT, but the decoder indexes
// whatever the file declares. So the bound a READER must enforce is the table length: it is what
// keeps the lookup in memory.
using System;
using System.Numerics;

namespace Vorticity.Arrays.Decoders.Compressed;

/// <summary>Powers of ten, and their reciprocals, in each float width ALP supports.</summary>
internal static class AlpTables
{
    /// <summary>10^k as <see cref="float"/>, for k in [0, 10].</summary>
    internal static ReadOnlySpan<float> F10Single =>
    [
        1.0f, 10.0f, 100.0f, 1000.0f, 10000.0f, 100000.0f, 1000000.0f, 10000000.0f,
        100000000.0f, 1000000000.0f, 10000000000.0f,
    ];

    /// <summary>10^-k as <see cref="float"/>, for k in [0, 10].</summary>
    internal static ReadOnlySpan<float> If10Single =>
    [
        1.0f, 0.1f, 0.01f, 0.001f, 0.0001f, 0.00001f, 0.000001f, 0.0000001f,
        0.00000001f, 0.000000001f, 0.0000000001f,
    ];

    /// <summary>10^k as <see cref="double"/>, for k in [0, 23].</summary>
    internal static ReadOnlySpan<double> F10Double =>
    [
        1.0, 10.0, 100.0, 1000.0, 10000.0, 100000.0, 1000000.0, 10000000.0,
        100000000.0, 1000000000.0, 10000000000.0, 100000000000.0,
        1000000000000.0, 10000000000000.0, 100000000000000.0, 1000000000000000.0,
        10000000000000000.0, 100000000000000000.0, 1000000000000000000.0,
        10000000000000000000.0, 100000000000000000000.0, 1000000000000000000000.0,
        10000000000000000000000.0, 100000000000000000000000.0,
    ];

    /// <summary>10^-k as <see cref="double"/>, for k in [0, 23].</summary>
    internal static ReadOnlySpan<double> If10Double =>
    [
        1.0, 0.1, 0.01, 0.001, 0.0001, 0.00001, 0.000001, 0.0000001,
        0.00000001, 0.000000001, 0.0000000001, 0.00000000001,
        0.000000000001, 0.0000000000001, 0.00000000000001, 0.000000000000001,
        0.0000000000000001, 0.00000000000000001, 0.000000000000000001,
        0.0000000000000000001, 0.00000000000000000001, 0.000000000000000000001,
        0.0000000000000000000001, 0.00000000000000000000001,
    ];

    /// <summary><c>decoded[i] = (float)encoded[i] * F10[f] * IF10[e]</c>.</summary>
    /// <param name="encoded">The i32 values, little-endian, at least <c>destination.Length</c>.</param>
    /// <param name="destination">The f32 output.</param>
    /// <param name="exponentE">Index into <see cref="If10Single"/>, already bounds-checked.</param>
    /// <param name="exponentF">Index into <see cref="F10Single"/>, already bounds-checked.</param>
    internal static void DecodeSingle(
        ReadOnlySpan<int> encoded, Span<float> destination, int exponentE, int exponentF)
    {
        float scale = F10Single[exponentF];
        float inverse = If10Single[exponentE];

        int i = 0;
        if (Vector<int>.IsSupported && destination.Length >= Vector<int>.Count)
        {
            Vector<float> scaleVector = new Vector<float>(scale);
            Vector<float> inverseVector = new Vector<float>(inverse);
            int lanes = Vector<int>.Count;
            for (; i <= destination.Length - lanes; i += lanes)
            {
                // Same order as the scalar line -- convert, multiply by scale, multiply by inverse
                // -- because float multiplication is not associative and the two orders differ in
                // the last bit on values this encoding produces by design.
                Vector<float> values = Vector.ConvertToSingle(Vector.LoadUnsafe(in encoded[i]));
                (values * scaleVector * inverseVector).StoreUnsafe(ref destination[i]);
            }
        }

        for (; i < destination.Length; i++)
        {
            destination[i] = encoded[i] * scale * inverse;
        }
    }

    /// <summary><c>decoded[i] = (double)encoded[i] * F10[f] * IF10[e]</c>.</summary>
    /// <param name="encoded">The i64 values, little-endian, at least <c>destination.Length</c>.</param>
    /// <param name="destination">The f64 output.</param>
    /// <param name="exponentE">Index into <see cref="If10Double"/>, already bounds-checked.</param>
    /// <param name="exponentF">Index into <see cref="F10Double"/>, already bounds-checked.</param>
    internal static void DecodeDouble(
        ReadOnlySpan<long> encoded, Span<double> destination, int exponentE, int exponentF)
    {
        double scale = F10Double[exponentF];
        double inverse = If10Double[exponentE];

        int i = 0;
        if (Vector<long>.IsSupported && destination.Length >= Vector<long>.Count)
        {
            Vector<double> scaleVector = new Vector<double>(scale);
            Vector<double> inverseVector = new Vector<double>(inverse);
            int lanes = Vector<long>.Count;
            for (; i <= destination.Length - lanes; i += lanes)
            {
                // NEON has the i64 -> f64 conversion natively, which AVX2 does not; writing this on
                // `Vector<T>` rather than a fixed width is what lets the same source use it here and
                // fall back to whatever the JIT emits elsewhere.
                Vector<double> values = Vector.ConvertToDouble(Vector.LoadUnsafe(in encoded[i]));
                (values * scaleVector * inverseVector).StoreUnsafe(ref destination[i]);
            }
        }

        for (; i < destination.Length; i++)
        {
            destination[i] = encoded[i] * scale * inverse;
        }
    }
}
