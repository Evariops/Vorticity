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
        _ when name.StartsWith("parts:", StringComparison.Ordinal) => Parts(name),
        _ when name.StartsWith("window:", StringComparison.Ordinal) => Windows(name),
        _ => throw new ArgumentException($"No switch named '{name}': merge, parts:A:B, window:A:B."),
    };

    /// <summary><c>window:A:B</c>, the slots folding windows of A rows against B, 0 for the whole batch.</summary>
    private static Switch Windows(string name)
    {
        string[] rows = name.Split(':');
        return new Switch($"window {rows[1]}", Set(rows[1]), $"window {rows[2]}", Set(rows[2]));

        static Action<AggregationPlan> Set(string rows) =>
            plan => plan.FoldWindow = int.Parse(rows, System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary><c>parts:A:B</c>, a merge in A parts against one in B, either of them <c>auto</c> for the merge's own count.</summary>
    private static Switch Parts(string name)
    {
        string[] counts = name.Split(':');
        return new Switch($"{counts[1]} parts", Set(counts[1]), $"{counts[2]} parts", Set(counts[2]));

        static Action<AggregationPlan> Set(string count) =>
            count == "auto" ? static _ => { } : plan => plan.MergeParts = int.Parse(count, System.Globalization.CultureInfo.InvariantCulture);
    }
}
