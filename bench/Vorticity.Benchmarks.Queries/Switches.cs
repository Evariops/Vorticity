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
        "bymerge" => new Switch("hashed parts", Set("MergeByValue", false), "parts by value", Set("MergeByValue", true)),
        _ when name.StartsWith("parts:", StringComparison.Ordinal) => Pair(name, "MergeParts", "parts"),
        _ when name.StartsWith("window:", StringComparison.Ordinal) => Pair(name, "FoldWindow", "window"),
        _ when name.StartsWith("probe:", StringComparison.Ordinal) => Pair(name, "ProbeAhead", "probe"),
        "core" => new Switch("reference", Set("Core", false), "core", Core()),
        "parted" => new Switch("whole", Under(Core(), Set("CoreParted", false)), "parted", Under(Core(), Set("CoreParted", true))),
        "topchunks" => new Switch("one ranking", Set("TopInChunks", false), "in chunks", Set("TopInChunks", true)),
        "extremes" => new Switch("every group", Set("TopOnExtremes", false), "top alone", Set("TopOnExtremes", true)),
        "coredistinct" => new Switch("reader's thread", Set("CoreDistinct", false), "core", Set("CoreDistinct", true)),
        _ when name.StartsWith("capacity:", StringComparison.Ordinal) => CorePair(name, "CoreCapacity", "capacity"),
        _ when name.StartsWith("alpha:", StringComparison.Ordinal) => CorePair(name, "CoreAlpha", "alpha"),
        _ when name.StartsWith("floor:", StringComparison.Ordinal) => CorePair(name, "CoreFloor", "floor"),
        _ when name.StartsWith("table:", StringComparison.Ordinal) => CorePair(name, "CoreTableGroups", "table"),
        _ when name.StartsWith("batch:", StringComparison.Ordinal) => CorePair(name, "CoreBatchEntries", "batch"),
        _ when name.StartsWith("bypass:", StringComparison.Ordinal) => CorePair(name, "CoreBypass", "bypass", percent: true),
        _ when name.StartsWith("period:", StringComparison.Ordinal) => CorePair(name, "CoreBypassPeriod", "period"),
        _ => throw new ArgumentException($"No switch named '{name}': merge, bymerge, parts:A:B, window:A:B, probe:A:B, core, parted, topchunks, extremes, coredistinct, capacity:A:B, alpha:A:B, floor:A:B, table:A:B, batch:A:B, bypass:A:B (percent), period:A:B."),
    };

    /// <summary>Both settings, <paramref name="first"/> then <paramref name="then"/>: the core on, and one of its own switches.</summary>
    private static Action<AggregationPlan> Under(Action<AggregationPlan> first, Action<AggregationPlan> then) => plan =>
    {
        first(plan);
        then(plan);
    };

    /// <summary>As <see cref="Pair"/>, under the core (PLAN-HIGH-CARDINALITY, H4): its cache's capacity or its α at A against B.</summary>
    private static Switch CorePair(string name, string property, string label, bool percent = false)
    {
        Switch pair = Pair(name, property, label, percent);
        Action<AggregationPlan> core = Core();
        return pair with { SetA = plan => { core(plan); pair.SetA(plan); }, SetB = plan => { core(plan); pair.SetB(plan); } };
    }

    /// <summary>The core on, at every degree: the bench measures it where it would not hold the groups by default too.</summary>
    private static Action<AggregationPlan> Core()
    {
        Action<AggregationPlan> on = Set("Core", true);
        Action<AggregationPlan> lanes = Set("CoreLanes", 1);
        return plan =>
        {
            on(plan);
            lanes(plan);
        };
    }

    /// <summary>
    /// <c>name:A:B</c>, the integer switch <paramref name="property"/> at A against B, either of them
    /// <c>auto</c> for the engine's own; a share in percent when <paramref name="percent"/>, set as a fraction.
    /// </summary>
    private static Switch Pair(string name, string property, string label, bool percent = false)
    {
        string[] values = name.Split(':');
        return new Switch($"{label} {values[1]}", Of(values[1]), $"{label} {values[2]}", Of(values[2]));

        Action<AggregationPlan> Of(string value) =>
            value == "auto" ? static _ => { }
            : percent ? Set(property, int.Parse(value, CultureInfo.InvariantCulture) / 100.0)
            : Set(property, int.Parse(value, CultureInfo.InvariantCulture));
    }

    /// <summary>Sets the plan's switch <paramref name="property"/> to <paramref name="value"/>.</summary>
    private static Action<AggregationPlan> Set(string property, object value)
    {
        PropertyInfo setting = typeof(AggregationPlan).GetProperty(property, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            ?? throw new ArgumentException($"This build of the library has no switch {property}.");
        return plan => setting.SetValue(plan, value);
    }
}
