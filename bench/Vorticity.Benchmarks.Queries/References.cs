using System.Collections.Generic;

namespace Vorticity.Benchmarks.Queries;

/// <summary>
/// The axes the gate holds: ratios, so that they mean the same on any machine. Each is one query's
/// time over another's, its floor, measured in the same process: an operator against the loop a
/// caller would write by hand, a first batch on a file sixteen times larger against the small one.
/// </summary>
/// <remarks>
/// A reference comes down when the code earns it and never goes up to let a change pass
/// (CONTRIBUTING.md, "The ratchet rule"). <c>--check</c> allows the 15 % the other gates allow for
/// the noise between processes; a red axis is replayed before it is believed.
/// </remarks>
internal static class References
{
    /// <summary>The margin a measured ratio may exceed its reference by.</summary>
    internal const double Margin = 1.15;

    /// <summary>The axis, its numerator and denominator scenarios, and the reference ratio, or null before calibration.</summary>
    internal static readonly IReadOnlyList<(string Axis, string Measured, string Floor, double? Reference)> Axes =
    [
        ("run-end key against the hand loop", "group by city (run-end), count avg", "hand loop city (run-end), count sum", null),
        ("dictionary key against the hand loop", "group by endpoint (dictionary), count avg", "hand loop endpoint (dictionary), count sum", null),
        ("custom aggregator against built-ins, run-end key", "group by city (run-end), welford", "group by city (run-end), count avg", null),
        ("filtered group against the filter before the group by", "group by endpoint (dictionary), errors in the group", "group by endpoint (dictionary), errors filtered before", null),
        ("first batch of a full scan, 16M against 1M", "first batch, full scan, 16M", "first batch, full scan", null),
        ("first batch of a filtered scan, 16M against 1M", "first batch, scan filtered everywhere, 16M", "first batch, scan filtered everywhere", null),
        ("first group of a sorted key, 16M against 1M", "first group, group by day (sorted), 16M", "first group, group by day (sorted)", null),

        // At equal work: a count and an integer sum on both sides, where the axes above also pay
        // for the reproducible float sums of the operator.
        ("run-end key, an int sum against the hand loop", "group by city (run-end), count sum of an int", "hand loop city (run-end), count sum of an int", null),
        ("dictionary key, an int sum against the hand loop", "group by endpoint (dictionary), count sum of an int", "hand loop endpoint (dictionary), count sum of an int", null),

        // A key that streams against the same query forced to block, on every lane: what streaming
        // costs in throughput where the blocking pass runs its ranges side by side.
        ("streaming against blocking, (City, Day), degree N", "group by city day (composite), count avg, degree N", "group by city day (composite), count avg, blocking, degree N", null),
        ("streaming against blocking, Welford, degree N", "group by day (sorted), welford, degree N", "group by day (sorted), welford, blocking, degree N", null),
    ];
}
