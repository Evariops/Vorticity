namespace Vorticity.Types.Numerics;

/// <summary>Whether an unscaled decimal holds within a number of digits, against a table of the powers of ten in 256 bits.</summary>
internal static class DecimalDigits
{
    /// <summary>10^0 to 10^76 as four little-endian limbs each: 10^76 is the first power a 256-bit decimal cannot reach.</summary>
    private static readonly ulong[] Powers = Build();

    /// <summary>Whether |<paramref name="value"/>| is below 10^<paramref name="digits"/>, <paramref name="digits"/> from 0 to 76.</summary>
    internal static bool Fits(Int256 value, int digits)
    {
        value.GetMagnitude(out ulong m0, out ulong m1, out ulong m2, out ulong m3);
        int at = digits * 4;
        return Int256.CompareMagnitudes(m0, m1, m2, m3, Powers[at], Powers[at + 1], Powers[at + 2], Powers[at + 3]) < 0;
    }

    /// <summary>10^<paramref name="exponent"/>'s limbs, least significant first.</summary>
    internal static void Power(int exponent, out ulong l0, out ulong l1, out ulong l2, out ulong l3)
    {
        int at = exponent * 4;
        l0 = Powers[at];
        l1 = Powers[at + 1];
        l2 = Powers[at + 2];
        l3 = Powers[at + 3];
    }

    private static ulong[] Build()
    {
        ulong[] powers = new ulong[77 * 4];
        ulong m0 = 1, m1 = 0, m2 = 0, m3 = 0;
        for (int i = 0; i < 77; i++)
        {
            powers[i * 4] = m0;
            powers[(i * 4) + 1] = m1;
            powers[(i * 4) + 2] = m2;
            powers[(i * 4) + 3] = m3;
            Int256.TryMultiplyMagnitudeByTen(ref m0, ref m1, ref m2, ref m3);
        }

        return powers;
    }
}
