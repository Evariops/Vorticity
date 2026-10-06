using System;
using System.Globalization;
using System.Reflection;
using Vorticity.Aggregating;

namespace Vorticity.Benchmarks.Queries;

/// <summary>
/// The engine's internal switches the bench compares two settings of in one process
/// (<c>--switch name</c>): each query runs under both in turns, and each setting keeps its best
/// round. A query that does not track its aggregation runs the same under both.
/// </summary>
/// <remarks>
/// A switch is a property of the plan, set by its name: bench/queries-ab.sh builds today's bench
/// against older commits of the library, which the bench must compile against whatever switches
/// they have. A switch a commit lacks fails when it is asked for, not when the bench builds.
/// </remarks>
internal static class Switches
{
    /// <summary>A switch: its two settings, each named and set on a plan.</summary>
    internal sealed record Switch(string A, Action<AggregationPlan> SetA, string B, Action<AggregationPlan> SetB);

    internal static Switch? Find(string? name) => name switch
    {
        null => null,
        "merge" => new Switch("in series", Set("MergeInParts", false), "in parts", Set("MergeInParts", true)),
        _ when name.StartsWith("parts:", StringComparison.Ordinal) => Pair(name, "MergeParts", "parts"),
        _ when name.StartsWith("window:", StringComparison.Ordinal) => Pair(name, "FoldWindow", "window"),
        _ when name.StartsWith("probe:", StringComparison.Ordinal) => Pair(name, "ProbeAhead", "probe"),
        _ => throw new ArgumentException($"No switch named '{name}': merge, parts:A:B, window:A:B, probe:A:B."),
    };

    /// <summary><c>name:A:B</c>, the integer switch <paramref name="property"/> at A against B, either of them <c>auto</c> for the engine's own.</summary>
    private static Switch Pair(string name, string property, string label)
    {
        string[] values = name.Split(':');
        return new Switch($"{label} {values[1]}", Of(values[1]), $"{label} {values[2]}", Of(values[2]));

        Action<AggregationPlan> Of(string value) =>
            value == "auto" ? static _ => { } : Set(property, int.Parse(value, CultureInfo.InvariantCulture));
    }

    /// <summary>Sets the plan's switch <paramref name="property"/> to <paramref name="value"/>.</summary>
    private static Action<AggregationPlan> Set(string property, object value)
    {
        PropertyInfo setting = typeof(AggregationPlan).GetProperty(property, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            ?? throw new ArgumentException($"This build of the library has no switch {property}.");
        return plan => setting.SetValue(plan, value);
    }
}
