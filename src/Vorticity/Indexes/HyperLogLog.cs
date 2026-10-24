using System;

namespace Vorticity.Indexes;

/// <summary>
/// The count a HyperLogLog's registers estimate, by Ertl's improved estimator, which needs no table of bias corrections
/// and is the same bits on every machine: the engine's sketch of distinct values and a column sheet's share it.
/// </summary>
/// <remarks>
/// O. Ertl, <i>New cardinality estimation algorithms for HyperLogLog sketches</i> (2017), algorithm 6.
/// </remarks>
internal static class HyperLogLog
{
    /// <summary>1 / (2 ln 2), written out: a platform's logarithm need not round alike, a literal does.</summary>
    private const double AlphaInfinity = 0.7213475204444817;

    /// <summary>The rank of a 64-bit hash under <paramref name="precision"/> bits of register index: its leading zeros past them, plus one.</summary>
    internal static byte Rank(ulong hash, int precision) =>
        (byte)(System.Numerics.BitOperations.LeadingZeroCount((hash << precision) | (1UL << (precision - 1))) + 1);

    /// <summary>The register a 64-bit hash raises under <paramref name="precision"/> bits of index: its top bits.</summary>
    internal static int Register(ulong hash, int precision) => (int)(hash >> (64 - precision));

    /// <summary>
    /// The estimate of the registers of <paramref name="precision"/> bits of index, counted by rank:
    /// <paramref name="counts"/>[r] the registers of rank r, [0] the empty ones, up to rank 65 − precision.
    /// </summary>
    internal static double Estimate(ReadOnlySpan<int> counts, int precision)
    {
        double m = 1 << precision;
        int q = 64 - precision;
        double z = m * Tau(1 - (counts[q + 1] / m));
        for (int k = q; k >= 1; k--)
        {
            z = 0.5 * (z + counts[k]);
        }

        z += m * Sigma(counts[0] / m);
        return AlphaInfinity * m * m / z;
    }

    private static double Sigma(double x)
    {
        if (x == 1)
        {
            return double.PositiveInfinity;
        }

        double y = 1;
        double z = x;
        while (true)
        {
            x *= x;
            double previous = z;
            z += x * y;
            y += y;
            if (z == previous)
            {
                return z;
            }
        }
    }

    private static double Tau(double x)
    {
        if (x == 0 || x == 1)
        {
            return 0;
        }

        double y = 1;
        double z = 1 - x;
        while (true)
        {
            x = Math.Sqrt(x);
            double previous = z;
            y *= 0.5;
            z -= (1 - x) * (1 - x) * y;
            if (z == previous)
            {
                return z / 3;
            }
        }
    }
}
