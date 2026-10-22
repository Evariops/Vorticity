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
}
