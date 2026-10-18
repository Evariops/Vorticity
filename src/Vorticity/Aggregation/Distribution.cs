using System;
using System.Collections.Generic;
using Vorticity.Aggregating;

namespace Vorticity;

/// <summary>One bucket of a <see cref="Distribution"/>: the values in <c>[Lower, Upper)</c>, and how many there were.</summary>
/// <param name="Lower">The bucket's lower bound; for the zeros, 0.</param>
/// <param name="Upper">The bucket's upper bound, excluded; for the zeros, 0.</param>
/// <param name="Count">The values that fell in it.</param>
public readonly record struct DistributionBucket(double Lower, double Upper, long Count);

/// <summary>
/// The distribution of a column's values, within a relative error: quantiles and a histogram, from a
/// sketch that merges exactly, so the same rows give the same answer whatever the cut of a scan, its
/// degree of parallelism or the order its parts merged in.
/// </summary>
/// <remarks>
/// A quantile is a value within <see cref="RelativeError"/> of a value of the column at that rank, relatively:
/// 1 % by default, which the grid holds at 0.77 %. The buckets split each power of two into equal parts,
/// so their bounds are powers of two and fractions of them, not round numbers: a threshold such as 250 ms
/// falls inside a bucket, and a count of the values below it is the histogram's up to that bucket, within
/// the bucket's values. A NaN is skipped, as every aggregate skips it.
/// </remarks>
public sealed class Distribution
{
    private readonly DistributionBucket[] _buckets;
    private readonly long[] _below;

    internal Distribution(QuantileSketch sketch)
    {
        RelativeError = QuantileSketch.ErrorOf(sketch.Bits);
        Count = sketch.Count;
        List<DistributionBucket> buckets = [];
        foreach ((double lower, double upper, long count) in sketch.Buckets())
        {
            buckets.Add(new DistributionBucket(lower, upper, count));
        }

        _buckets = [.. buckets];
        _below = new long[_buckets.Length + 1];
        for (int i = 0; i < _buckets.Length; i++)
        {
            _below[i + 1] = _below[i] + _buckets[i].Count;
        }
    }

    /// <summary>The values the distribution counts.</summary>
    public long Count { get; }

    /// <summary>The largest error of a quantile, relatively to a value of the column at its rank.</summary>
    public double RelativeError { get; }

    /// <summary>The buckets that hold values, in ascending order of value.</summary>
    public IReadOnlyList<DistributionBucket> Histogram => _buckets;

    /// <summary>The median: <see cref="Quantile(double)"/> at 0.5.</summary>
    public double? Median => Quantile(0.5);

    /// <summary>
    /// The value at rank <c>q · (Count − 1)</c> of the values in ascending order, within
    /// <see cref="RelativeError"/>; null when the distribution counts no value.
    /// </summary>
    /// <param name="q">The rank's share, from 0, the smallest value, to 1, the largest.</param>
    /// <returns>The quantile, or null for no value.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="q"/> is not within [0, 1].</exception>
    public double? Quantile(double q)
    {
        if (!(q >= 0 && q <= 1))
        {
            throw new ArgumentOutOfRangeException(nameof(q), q, "A quantile's share lies within [0, 1].");
        }

        if (Count == 0)
        {
            return null;
        }

        // The first bucket whose values reach past the rank.
        double rank = q * (Count - 1);
        int low = 0;
        int high = _buckets.Length - 1;
        while (low < high)
        {
            int middle = (int)((uint)(low + high) >> 1);
            if (_below[middle + 1] > rank)
            {
                high = middle;
            }
            else
            {
                low = middle + 1;
            }
        }

        DistributionBucket bucket = _buckets[low];
        return QuantileSketch.Estimate(bucket.Lower, bucket.Upper);
    }

    /// <inheritdoc/>
    public override string ToString() =>
        Count == 0 ? "no value" : FormattableString.Invariant($"{Count} values, median {Median}, within {RelativeError:P2}");
}
