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
    /// <remarks>
    /// Calibrated on 2026-10-04 at the end of the engine stage's bench (6j), on an M-series machine
    /// of 14 cores under a load of 4 to 6: each reference is the higher ratio of two runs of 7 and 9
    /// rounds at degrees 1 and 14, so the first ones are loose and come down as the stage's lines
    /// earn it, or at a quiet calibration. "degree N" is one lane per processor, 14 there.
    /// </remarks>
    internal static readonly IReadOnlyList<(string Axis, string Measured, string Floor, double? Reference)> Axes =
    [
        ("run-end key against the hand loop", "group by city (run-end), count avg", "hand loop city (run-end), count sum", 0.884),
        ("dictionary key against the hand loop", "group by endpoint (dictionary), count avg", "hand loop endpoint (dictionary), count sum", 2.117),
        ("custom aggregator against built-ins, run-end key", "group by city (run-end), welford", "group by city (run-end), count avg", 1.136),
        ("filtered group against the filter before the group by", "group by endpoint (dictionary), errors in the group", "group by endpoint (dictionary), errors filtered before", 0.826),
        ("first batch of a full scan, 16M against 1M", "first batch, full scan, 16M", "first batch, full scan", 1.081),
        ("first batch of a filtered scan, 16M against 1M", "first batch, scan filtered everywhere, 16M", "first batch, scan filtered everywhere", 5.176),
        ("first group of a sorted key, 16M against 1M", "first group, group by day (sorted), 16M", "first group, group by day (sorted)", 1.254),

        // At equal work: a count and an integer sum on both sides, where the axes above also pay
        // for the reproducible float sums of the operator.
        ("run-end key, an int sum against the hand loop", "group by city (run-end), count sum of an int", "hand loop city (run-end), count sum of an int", 0.510),
        ("dictionary key, an int sum against the hand loop", "group by endpoint (dictionary), count sum of an int", "hand loop endpoint (dictionary), count sum of an int", 2.180),

        // A key that streams against the same query forced to block, on every lane: what streaming
        // costs in throughput where the blocking pass runs its ranges side by side. Brought down
        // from 5.761 and 7.465 by the ranges that stream (6k), measured at 0.85 to 0.89: 1.0 with
        // the margin is the line's own gate, 15 % at most.
        ("streaming against blocking, (City, Day), degree N", "group by city day (composite), count avg, degree N", "group by city day (composite), count avg, blocking, degree N", 1.000),
        ("streaming against blocking, Welford, degree N", "group by day (sorted), welford, degree N", "group by day (sorted), welford, blocking, degree N", 1.000),

        // A dataset against the file of its rows (6e): a group by on sixteen objects within 15 % of
        // the file's on every lane, where an object is a range of the queue, and on one lane, where
        // each object opens ahead of the one read; the first batch flat from one object to sixteen.
        // Measured at 0.81 to 0.85, 1.06 to 1.09 and 0.72 to 0.75: 1.0 is the lines' own gates.
        ("dataset of 16 objects against its file, degree N", "dataset group by city, count avg, 16 objects, degree N", "group by city (run-end), count avg, degree N", 1.000),
        ("dataset of 16 objects against its file", "dataset group by city, count avg, 16 objects", "group by city (run-end), count avg", 1.093),
        ("first batch of a dataset, 16 objects against 1", "dataset first batch, full scan, 16 objects", "dataset first batch, full scan, 1 object", 1.000),
    ];
}
