using System;
using System.Collections.Generic;

namespace Vorticity;

/// <summary>How an aggregation's estimates are drawn (<see cref="Aggregation.EstimateAsync"/>).</summary>
public sealed record EstimateOptions
{
    /// <summary>
    /// The shares of the question's ranges each tier reads, ascending, each tier reading those of the tier before and more:
    /// 0.1 %, 1 % and 10 % by default.
    /// </summary>
    public IReadOnlyList<double> Shares { get; init; } = [0.001, 0.01, 0.1];

    /// <summary>Whether the last tier is the exact answer, every range read: true by default.</summary>
    public bool Exact { get; init; } = true;

    /// <summary>
    /// The bytes of the narrowest column it reads a range holds at least: 1 MiB by default, about what a request to an object
    /// store carries before its bytes cost more than its round trip. A range holds whole chunks of every column.
    /// </summary>
    public long RangeBytes { get; init; } = 1L << 20;

    /// <summary>The seed of the draw, for estimates drawn the same way twice; null for one the process chooses.</summary>
    public int? Seed { get; init; }

    /// <summary>
    /// The cost of the exact answer under which it comes at once, with no estimate before it: a second by default, read cold
    /// at the cost model's link, 1.25 GB a second, each request worth its round trip (<see cref="AggregationEstimate.ExactCost"/>).
    /// <see cref="TimeSpan.Zero"/> draws the estimates whatever the cost; a source that cannot say a cost draws them too.
    /// </summary>
    public TimeSpan ExactUnder { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Throws unless the shares are ascending, each above 0 and at most 1, and the range holds bytes.</summary>
    internal void Validate()
    {
        ArgumentNullException.ThrowIfNull(Shares);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(RangeBytes);
        ArgumentOutOfRangeException.ThrowIfLessThan(ExactUnder, TimeSpan.Zero);
        double last = 0;
        foreach (double share in Shares)
        {
            if (!(share > last) || share > 1)
            {
                throw new ArgumentOutOfRangeException(nameof(Shares), share, "The shares are ascending, each above 0 and at most 1.");
            }

            last = share;
        }
    }
}

/// <summary>
/// A tier of an aggregation's estimates: its answers over the ranges read so far, scaled to every row, and a margin for
/// each count of rows, sum and mean, which the next tier narrows, until the exact answer, whose margins are 0.
/// </summary>
/// <remarks>
/// <para>
/// A count of rows and a sum are estimated as a stratified cluster sample's totals: the ranges of each stratum, a run of
/// objects, drawn at random, two at least, their totals scaled by the stratum's ranges over those drawn. A mean is the ratio
/// of the sum's estimate over the count of values'. A margin is the half-width of a 95 % interval: Student's quantile times
/// the estimate's standard error, from the spread of the totals among the ranges each stratum drew.
/// </para>
/// <para>
/// A least, a greatest, a distribution, a quantile and an approximate distinct count are the sample's, with no margin: a
/// least of the sample is above the least of the rows or at it. A group the sample does not meet is not in the estimate.
/// </para>
/// </remarks>
public sealed class AggregationEstimate
{
    private readonly ResultQuery _result;

    internal AggregationEstimate(ResultQuery result, double share, int ranges, long groups, bool exact, TimeSpan? exactCost)
    {
        _result = result;
        Share = share;
        Ranges = ranges;
        Groups = groups;
        IsExact = exact;
        ExactCost = exactCost;
    }

    /// <summary>
    /// What the exact answer costs, announced before it is read: its requests and bytes as the source's planner prices them,
    /// read cold at the cost model's link, 1.25 GB a second, each request worth its round trip; null when the source cannot
    /// say. The same for every tier.
    /// </summary>
    public TimeSpan? ExactCost { get; }

    /// <summary>The share of the question's rows read so far.</summary>
    public double Share { get; }

    /// <summary>The ranges read so far.</summary>
    public int Ranges { get; }

    /// <summary>The groups met so far, which the estimate answers.</summary>
    public long Groups { get; }

    /// <summary>Whether every range is read: the answers are the question's, every margin 0.</summary>
    public bool IsExact { get; }

    /// <summary>What the reads of the estimate did so far, every tier's.</summary>
    public ScanMetrics Metrics => _result.Metrics();

    /// <summary>
    /// The estimate as a scan of <typeparamref name="TRecord"/>, whose members take the elements of the selection in order,
    /// then the margin of each count of rows, sum and mean among them, in their order, as a <see cref="double"/>.
    /// </summary>
    /// <typeparam name="TRecord">The record: the selection's members, then a member for each margin.</typeparam>
    /// <returns>A scan of the estimate's groups. Read it before the next tier is asked for.</returns>
    /// <exception cref="VortexSchemaException">The record has another number of members, or a member does not take its element or its margin.</exception>
    public Scan<TRecord> As<TRecord>()
        where TRecord : IVortexRecord<TRecord> =>
        Aggregation.Scan<TRecord>(_result);
}
