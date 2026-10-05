using System;
using Vorticity.Aggregating;

namespace Vorticity.Benchmarks.Queries;

/// <summary>
/// The engine's internal switches the bench compares two settings of in one process
/// (<c>--switch name</c>): each query runs under both in turns, and each setting keeps its best
/// round. A query that does not track its aggregation runs the same under both.
/// </summary>
internal static class Switches
{
    /// <summary>A switch: its two settings, each named and set on a plan.</summary>
    internal sealed record Switch(string A, Action<AggregationPlan> SetA, string B, Action<AggregationPlan> SetB);

    internal static Switch? Find(string? name) => name switch
    {
        null => null,
        "merge" => new Switch("in series", plan => plan.MergeInParts = false, "in parts", plan => plan.MergeInParts = true),
        _ => throw new ArgumentException($"No switch named '{name}': merge."),
    };
}
