// Deciding a gate on an interval instead of a point.
//
// THE PROBLEM THIS EXISTS FOR, in the numbers that produced it (BENCH-AUDIT.md B2). `--ratio-check`'s
// short axes moved +12 to +22 % run to run with no byte changed; `--throughput --check` failed two
// invocations out of four, both falsely. The margin is +15 % everywhere. When the noise and the
// margin are the same size a gate does two wrong things at once: it fails on changes nobody made,
// and it passes a real 15 % regression on a short axis.
//
// Widening the margin is the wrong fix and the audit says so: it trades one blindness for another.
// The right one is to stop pretending a single number was measured. Each round times BOTH sides, so
// the rounds are PAIRED and a per-round ratio is a legitimate sample: whatever slowed our side in
// round 7 slowed theirs too. A sample has a distribution, and a distribution has an interval.
//
// WHY THE MEDIAN AND WHY BOOTSTRAP. The mean of a ratio is not robust -- one GC pause in one round
// moves it and it never comes back. The median does not have a closed-form confidence interval for
// an arbitrary distribution, and the ratio of two timing distributions is not normal at these
// lengths. Bootstrapping asks the sample itself: resample it with replacement 2 000 times, take the
// median of each resample, and read the 2.5th and 97.5th percentiles of those medians. It costs
// under a millisecond on 51 rounds and assumes nothing about the shape.
//
// WHAT THE INTERVAL IS FOR, once it exists. Three things the point estimate could not do:
//   * RED ONLY ON THE LOWER BOUND. "1.16" against a 1.08 ceiling is a coin toss; "1.16 [1.12; 1.20]"
//     is a regression and "1.16 [1.02; 1.31]" is noise. Gating on the lower bound fails only when
//     the whole interval is over.
//   * STALE ON THE UPPER BOUND, for the same reason in the other direction.
//   * A MINIMUM DETECTABLE EFFECT. Half the interval width, as a percentage, is the smallest
//     regression that axis can see today. Printing it turns "the gate is noisy" into a number a
//     developer can act on -- and says which axes deserve more rounds.
using System;

namespace Vorticity.Benchmarks;

/// <summary>A median and the interval it was measured with.</summary>
/// <param name="Median">The median of the sample.</param>
/// <param name="Low">The 2.5th percentile of the bootstrapped medians.</param>
/// <param name="High">The 97.5th percentile.</param>
/// <param name="Samples">How many observations went in.</param>
internal readonly record struct Interval(double Median, double Low, double High, int Samples)
{
    /// <summary>
    /// Half the interval width over the median: the smallest relative change this many rounds of
    /// this axis can distinguish from noise.
    /// </summary>
    public double MinimumDetectableEffect =>
        Median == 0 ? double.PositiveInfinity : (High - Low) / 2 / Median;

    /// <summary>The interval, for a report column.</summary>
    public override string ToString() =>
        string.Create(
            System.Globalization.CultureInfo.InvariantCulture, $"[{Low,6:F3};{High,6:F3}]");
}

/// <summary>The bootstrap, and nothing else.</summary>
internal static class Statistics
{
    /// <summary>
    /// Resamples per interval.
    /// </summary>
    /// <remarks>
    /// 2 000 is the usual floor for a percentile interval and it is free here: 2 000 x 51 draws is
    /// a hundred thousand operations against axes that take seconds.
    /// </remarks>
    private const int Resamples = 2_000;

    /// <summary>
    /// A FIXED SEED, so the same sample gives the same interval.
    /// </summary>
    /// <remarks>
    /// The bootstrap is a numerical method, not a measurement. Letting it wander would add a second
    /// source of run-to-run difference to a gate whose whole problem is run-to-run difference, and
    /// it would mean a red could not be reproduced from the same numbers.
    /// </remarks>
    private const int Seed = 20260913;

    /// <summary>The median of <paramref name="samples"/> and its 95% confidence interval.</summary>
    /// <param name="samples">The observations. Not modified.</param>
    internal static Interval Bootstrap(ReadOnlySpan<double> samples)
    {
        if (samples.Length == 0)
        {
            return new Interval(0, 0, 0, 0);
        }

        double[] sorted = samples.ToArray();
        Array.Sort(sorted);
        double median = Median(sorted);
        if (samples.Length < 3)
        {
            // An interval from one or two observations would be a fiction with a number attached.
            return new Interval(median, sorted[0], sorted[^1], samples.Length);
        }

        Random random = new Random(Seed);
        double[] medians = new double[Resamples];
        double[] draw = new double[samples.Length];
        for (int i = 0; i < Resamples; i++)
        {
            for (int j = 0; j < draw.Length; j++)
            {
                draw[j] = samples[random.Next(samples.Length)];
            }

            Array.Sort(draw);
            medians[i] = Median(draw);
        }

        Array.Sort(medians);
        return new Interval(median, Percentile(medians, 0.025), Percentile(medians, 0.975), samples.Length);
    }

    /// <summary>The median of an already sorted array.</summary>
    private static double Median(double[] sorted) =>
        sorted.Length % 2 == 1
            ? sorted[sorted.Length / 2]
            : (sorted[(sorted.Length / 2) - 1] + sorted[sorted.Length / 2]) / 2;

    /// <summary>The <paramref name="fraction"/> percentile of an already sorted array.</summary>
    private static double Percentile(double[] sorted, double fraction)
    {
        double position = fraction * (sorted.Length - 1);
        int low = (int)Math.Floor(position);
        int high = (int)Math.Ceiling(position);
        return low == high ? sorted[low] : sorted[low] + ((sorted[high] - sorted[low]) * (position - low));
    }
}
