using System;
using System.Buffers;
using System.Numerics;

namespace Vorticity.Writing;

/// <summary>
/// pco's test for integers that are multiples of a common divisor, <c>int_mult::choose_base</c>: a
/// random sample of the latents, the greatest common divisor of each triple's differences, and the
/// divisor that turns up more often than chance would have it -- when what it saves a value is
/// worth the second latent it costs.
/// </summary>
/// <remarks>
/// The sample is upstream's to the row: the same generator, Xoroshiro128++ seeded through
/// SplitMix64 from zero, drawing the same rows, so a column's base is the one pco would find.
/// </remarks>
internal static class PcoIntMult
{
    /// <summary>The fewest latents a sample is drawn from, and the fewest it may hold: <c>MIN_SAMPLE</c>.</summary>
    internal const int MinSample = 10;

    /// <summary>One latent sampled per this many past the minimum: <c>SAMPLE_RATIO</c>.</summary>
    internal const int SampleRatio = 40;

    private const int SamplingPersistence = 4;
    private const double ZetaOf2 = Math.PI * Math.PI / 6.0;
    private const double LowerBoundRatio = 1.0;
    private const double RequiredBitsSavedPerNum = 0.5;
    private const double MemorizableBins = 256;

    /// <summary>Rust's <c>f64::EPSILON</c>, the gap above one; not <see cref="double.Epsilon"/>, the least subnormal.</summary>
    private const double MachineEpsilon = 2.220446049250313e-16;

    /// <summary>Draws the sample, <c>choose_sample</c>: rows at random, each taken once, until the sample is full or the draws run out.</summary>
    /// <param name="latents">The chunk's latents.</param>
    /// <param name="into">The sample, as long as the target size.</param>
    /// <param name="visited">A bit per latent, cleared here.</param>
    /// <returns>How many were drawn.</returns>
    internal static int Sample(ReadOnlySpan<ulong> latents, Span<ulong> into, Span<byte> visited)
    {
        visited.Clear();
        Xoroshiro128PlusPlus random = Xoroshiro128PlusPlus.FromSplitMix64(0);
        ulong n = (ulong)latents.Length;
        int count = 0;
        for (int draws = 0; count < into.Length && draws < SamplingPersistence * into.Length; draws++)
        {
            int index = (int)(random.Next() % n);
            int bit = 1 << (index & 7);
            if ((visited[index >> 3] & bit) == 0)
            {
                into[count++] = latents[index];
                visited[index >> 3] |= (byte)bit;
            }
        }

        return count;
    }

    /// <summary>The sample's common divisor, when it is worth a second latent; zero otherwise.</summary>
    internal static ulong ChooseBase(ReadOnlySpan<ulong> sample)
    {
        int triples = sample.Length / 3;
        ulong[] gcds = ArrayPool<ulong>.Shared.Rent(Math.Max(triples, 1));
        ulong[] primaries = ArrayPool<ulong>.Shared.Rent(Math.Max(sample.Length, 1));
        try
        {
            int found = 0;
            for (int t = 0; t < triples; t++)
            {
                ulong gcd = TripleGcd(sample[3 * t], sample[(3 * t) + 1], sample[(3 * t) + 2]);
                if (gcd > 1)
                {
                    gcds[found++] = gcd;
                }
            }

            // The most prominent divisor: counted by sorting rather than hashing, the best score
            // kept, a tie to the larger divisor.
            Span<ulong> sorted = gcds.AsSpan(0, found);
            sorted.Sort();
            ulong candidate = 0;
            double best = double.NegativeInfinity;
            for (int i = 0; i < sorted.Length;)
            {
                int end = i + 1;
                while (end < sorted.Length && sorted[end] == sorted[i])
                {
                    end++;
                }

                if (Score(sorted[i], end - i, triples) is { } score && score >= best)
                {
                    best = score;
                    candidate = sorted[i];
                }

                i = end;
            }

            if (candidate == 0)
            {
                return 0;
            }

            Span<ulong> quotients = primaries.AsSpan(0, sample.Length);
            for (int i = 0; i < sample.Length; i++)
            {
                quotients[i] = sample[i] / candidate;
            }

            return BitsSavedPerNum(quotients, best) > RequiredBitsSavedPerNum ? candidate : 0;
        }
        finally
        {
            ArrayPool<ulong>.Shared.Return(primaries);
            ArrayPool<ulong>.Shared.Return(gcds);
        }
    }

    /// <summary>
    /// What the divisor saves a value, <c>est_bits_saved_per_num</c>: its bits for each value whose
    /// quotient is rare -- a frequent one the bins would memorize anyway.
    /// </summary>
    private static double BitsSavedPerNum(Span<ulong> quotients, double bitsSaved)
    {
        quotients.Sort();
        int cutoff = Math.Max(1, (int)(quotients.Length / MemorizableBins));
        double saved = 0;
        for (int i = 0; i < quotients.Length;)
        {
            int end = i + 1;
            while (end < quotients.Length && quotients[end] == quotients[i])
            {
                end++;
            }

            if (end - i <= cutoff)
            {
                saved += (end - i) * bitsSaved;
            }

            i = end;
        }

        return saved / quotients.Length;
    }

    /// <summary>The greatest common divisor of a triple's differences from its least.</summary>
    private static ulong TripleGcd(ulong a, ulong b, ulong c)
    {
        if (a > b)
        {
            (a, b) = (b, a);
        }

        if (b > c)
        {
            (b, c) = (c, b);
        }

        if (a > b)
        {
            (a, b) = (b, a);
        }

        return Gcd(b - a, c - a);
    }

    private static ulong Gcd(ulong x, ulong y)
    {
        if (x == 0)
        {
            return y;
        }

        while (y != 0)
        {
            x %= y;
            (x, y) = (y, x);
        }

        return x;
    }

    /// <summary>
    /// A divisor's worth, <c>filter_score_triple_gcd</c>: null unless triples share it three standard
    /// deviations more often than random integers would, and otherwise the bits a value it saves
    /// at worst, the remainders spread as unevenly as that frequency allows.
    /// </summary>
    private static double? Score(ulong divisor, int withDivisor, int totalTriples)
    {
        double gcd = divisor;
        double count = withDivisor;
        double total = totalTriples;
        double probability = count / total;
        double natural = 1.0 / (ZetaOf2 * gcd * gcd);
        double deviation = Math.Sqrt(natural * (1.0 - natural) / total);
        if ((probability - natural) / deviation < 3.0)
        {
            return null;
        }

        double lowerBound = count - (LowerBoundRatio * Math.Sqrt(count));
        if (lowerBound <= 0)
        {
            return null;
        }

        double congruence = Math.Min(ZetaOf2 * lowerBound / total, 1.0);
        double gcdMinus1 = gcd - 1.0;
        double inverseSquare = 1.0 / (gcdMinus1 * gcdMinus1);
        if (SolveByFalsePosition(inverseSquare, congruence, 1.0 / gcd, Math.Cbrt(congruence) + MachineEpsilon) is not { } concentrated)
        {
            return null;
        }

        double entropy = Entropy(concentrated) + (gcdMinus1 * Entropy((1.0 - concentrated) / gcdMinus1));
        double saved = Math.Log2(gcd) - entropy;
        return saved < RequiredBitsSavedPerNum ? null : saved;
    }

    private static double Entropy(double p) => p is 0.0 or 1.0 ? 0.0 : -p * Math.Log2(p);

    /// <summary>How far a triple's chance of congruence, one remainder at <paramref name="p"/> and the rest even, falls short of what was seen.</summary>
    private static double Congruence(double p, double inverseSquare, double congruence) =>
        (p * p * p) + ((1.0 - p) * (1.0 - p) * (1.0 - p) * inverseSquare) - congruence;

    /// <summary>
    /// The probability of the commonest remainder that makes the congruences as frequent as seen:
    /// the root of <see cref="Congruence"/> between its bounds, by false position,
    /// <c>solve_root_by_false_position</c>.
    /// </summary>
    private static double? SolveByFalsePosition(double inverseSquare, double congruence, double lower, double upper)
    {
        const double Tolerance = 1E-4;
        double atLower = Congruence(lower, inverseSquare, congruence);
        double atUpper = Congruence(upper, inverseSquare, congruence);
        if (atLower > 0.0 || atUpper < 0.0)
        {
            return null;
        }

        while (upper - lower > Tolerance && atUpper - atLower > 0.0)
        {
            double share = 0.001 + (0.998 * atUpper / (atUpper - atLower));
            double middle = (share * lower) + ((1.0 - share) * upper);
            double atMiddle = Congruence(middle, inverseSquare, congruence);
            if (atMiddle < 0.0)
            {
                lower = middle;
                atLower = atMiddle;
            }
            else
            {
                upper = middle;
                atUpper = atMiddle;
            }
        }

        return (lower + upper) / 2.0;
    }

    /// <summary>rand_xoshiro's Xoroshiro128++, seeded as its <c>seed_from_u64</c> seeds it.</summary>
    private struct Xoroshiro128PlusPlus
    {
        private ulong _s0;
        private ulong _s1;

        internal static Xoroshiro128PlusPlus FromSplitMix64(ulong seed)
        {
            ulong state = seed;
            return new Xoroshiro128PlusPlus { _s0 = SplitMix(ref state), _s1 = SplitMix(ref state) };
        }

        internal ulong Next()
        {
            ulong s0 = _s0;
            ulong s1 = _s1;
            ulong result = unchecked(BitOperations.RotateLeft(s0 + s1, 17) + s0);
            s1 ^= s0;
            _s0 = BitOperations.RotateLeft(s0, 49) ^ s1 ^ (s1 << 21);
            _s1 = BitOperations.RotateLeft(s1, 28);
            return result;
        }

        private static ulong SplitMix(ref ulong state)
        {
            unchecked
            {
                state += 0x9e3779b97f4a7c15;
                ulong z = state;
                z = (z ^ (z >> 30)) * 0xbf58476d1ce4e5b9;
                z = (z ^ (z >> 27)) * 0x94d049bb133111eb;
                return z ^ (z >> 31);
            }
        }
    }
}
