using System;
using System.Runtime.InteropServices;

namespace Vorticity;

/// <summary>
/// A float as a key holds it: either zero as +0, and every NaN as the one NaN, so that the values an equality calls the
/// same, and every NaN, which groups as one, hash, sort and count alike, in a distinct count, a slice's codes, a synopsis
/// order or a group of sketches.
/// </summary>
internal static class FloatKey
{
    internal static double Normal(double value) => double.IsNaN(value) ? double.NaN : value == 0 ? 0 : value;

    internal static float Normal(float value) => float.IsNaN(value) ? float.NaN : value == 0 ? 0 : value;

    internal static Half Normal(Half value) => Half.IsNaN(value) ? Half.NaN : value == Half.Zero ? Half.Zero : value;

    /// <summary>
    /// The little-endian bytes of a float of 8, 4 or 2 bytes, <paramref name="value"/>, as a key holds them, written to
    /// <paramref name="scratch"/> (8 bytes at least).
    /// </summary>
    internal static ReadOnlySpan<byte> Normal(ReadOnlySpan<byte> value, Span<byte> scratch)
    {
        switch (value.Length)
        {
            case sizeof(double):
            {
                double d = Normal(MemoryMarshal.Read<double>(value));
                MemoryMarshal.Write(scratch, in d);
                return scratch[..sizeof(double)];
            }

            case sizeof(float):
            {
                float f = Normal(MemoryMarshal.Read<float>(value));
                MemoryMarshal.Write(scratch, in f);
                return scratch[..sizeof(float)];
            }

            default:
            {
                Half h = Normal(MemoryMarshal.Read<Half>(value));
                MemoryMarshal.Write(scratch, in h);
                return scratch[..2];
            }
        }
    }
}
