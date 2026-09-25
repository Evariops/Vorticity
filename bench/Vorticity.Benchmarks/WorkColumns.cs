// ns/row and GB/s in the table, instead of a division left to the reader.
//
// `RowEncodingBenchmarks` printed `rows=… keyBytes=…` to stderr in its
// `GlobalSetup` and left the arithmetic to whoever read the table; every kernel class reported
// microseconds for a quantity of work only its source says. A mean of 19.6 µs means nothing until
// you know it moved 64 blocks of 1024 values, and nobody divides in their head twice a day.
//
// THE CONTRACT IS STATIC, ON PURPOSE. A benchmark class opts in by declaring
//
//     public static (long Rows, long Bytes) BenchmarkWork(string method, IReadOnlyDictionary<…> p)
//
// which takes the ARM's name and the case's parameters, and returns the work one invocation does.
// The arm's name is not optional: a view kernel class declared one byte count for the class and its
// `require ascending` came out at 292 GB/s, which this machine cannot do -- that arm reads the
// offsets and nothing else, where `build views` moves the heap and the views. A wrong GB/s is worse
// than no GB/s, because it looks like a measurement. Static and
// parameter-only, because the columns are computed when the SUMMARY is built: with `--full` that
// happens in the host process while the benchmark ran in another, so anything stashed in an
// instance field during `GlobalSetup` would simply not be there. A class that declares nothing
// shows `-`, which is the honest answer for a benchmark whose work has no natural unit.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;

using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;

namespace Vorticity.Benchmarks;

/// <summary>What one invocation of a benchmark moves, as the class itself declares it.</summary>
internal static class Work
{
    /// <summary>The method a class declares to opt in.</summary>
    private const string Method = "BenchmarkWork";

    /// <summary>
    /// One answer per case, because the columns ask several times each.
    /// </summary>
    /// <remarks>
    /// `IsAvailable` asks about every case, then each of the two columns asks again per row. A
    /// class whose work is a multiplication would not care; `RowEncodingBenchmarks` opens a file
    /// and encodes a batch to answer, and four of those per case is three too many.
    /// </remarks>
    private static readonly Dictionary<string, (long Rows, long Bytes)?> Cache = [];

    /// <summary>The rows and bytes one invocation of <paramref name="benchmarkCase"/> moves.</summary>
    /// <param name="benchmarkCase">The case.</param>
    /// <returns>The work, or null when the class declares none.</returns>
    internal static (long Rows, long Bytes)? Of(BenchmarkCase benchmarkCase)
    {
        string key = benchmarkCase.Descriptor.Type.FullName + "|" +
            benchmarkCase.Descriptor.WorkloadMethod.Name + "|" +
            benchmarkCase.Parameters.DisplayInfo;
        lock (Cache)
        {
            if (Cache.TryGetValue(key, out (long Rows, long Bytes)? cached))
            {
                return cached;
            }
        }

        (long Rows, long Bytes)? work = Measure(benchmarkCase);
        lock (Cache)
        {
            Cache[key] = work;
        }

        return work;
    }

    private static (long Rows, long Bytes)? Measure(BenchmarkCase benchmarkCase)
    {
        Type type = benchmarkCase.Descriptor.Type;
        MethodInfo? method = type.GetMethod(
            Method, BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
        if (method is null)
        {
            return null;
        }

        Dictionary<string, object?> parameters = benchmarkCase.Parameters.Items
            .ToDictionary(item => item.Name, item => item.Value, StringComparer.Ordinal);
        string arm = benchmarkCase.Descriptor.WorkloadMethod.Name;
        try
        {
            return method.Invoke(null, [arm, parameters]) as (long Rows, long Bytes)?;
        }
        catch (Exception e) when (e is TargetInvocationException or ArgumentException)
        {
            // A class that declares the method and then throws is telling us its work is not
            // computable here; that is a dash, not a crashed report.
            return null;
        }
    }
}

/// <summary>Nanoseconds per row, for the classes that say how many rows they move.</summary>
internal sealed class PerRowColumn : IColumn
{
    /// <inheritdoc/>
    public string Id => nameof(PerRowColumn);

    /// <inheritdoc/>
    public string ColumnName => "ns/row";

    /// <inheritdoc/>
    public bool AlwaysShow => false;

    /// <inheritdoc/>
    public ColumnCategory Category => ColumnCategory.Custom;

    /// <inheritdoc/>
    public int PriorityInCategory => 1;

    /// <inheritdoc/>
    public bool IsNumeric => true;

    /// <inheritdoc/>
    public UnitType UnitType => UnitType.Dimensionless;

    /// <inheritdoc/>
    public string Legend => "Mean divided by the rows one invocation moves, as the class declares them";

    /// <inheritdoc/>
    public bool IsDefault(Summary summary, BenchmarkCase benchmarkCase) => false;

    /// <inheritdoc/>
    public bool IsAvailable(Summary summary) =>
        summary.BenchmarksCases.Any(c => Work.Of(c) is not null);

    /// <inheritdoc/>
    public string GetValue(Summary summary, BenchmarkCase benchmarkCase)
    {
        double? mean = summary[benchmarkCase]?.ResultStatistics?.Mean;
        if (mean is null || Work.Of(benchmarkCase) is not { Rows: > 0 } work)
        {
            return "-";
        }

        return (mean.Value / work.Rows).ToString("F2", CultureInfo.InvariantCulture);
    }

    /// <inheritdoc/>
    public string GetValue(Summary summary, BenchmarkCase benchmarkCase, SummaryStyle style) =>
        GetValue(summary, benchmarkCase);
}

/// <summary>Gigabytes per second, for the classes that say how many bytes they move.</summary>
internal sealed class ThroughputColumn : IColumn
{
    /// <inheritdoc/>
    public string Id => nameof(ThroughputColumn);

    /// <inheritdoc/>
    public string ColumnName => "GB/s";

    /// <inheritdoc/>
    public bool AlwaysShow => false;

    /// <inheritdoc/>
    public ColumnCategory Category => ColumnCategory.Custom;

    /// <inheritdoc/>
    public int PriorityInCategory => 2;

    /// <inheritdoc/>
    public bool IsNumeric => true;

    /// <inheritdoc/>
    public UnitType UnitType => UnitType.Dimensionless;

    /// <inheritdoc/>
    public string Legend =>
        "Bytes one invocation moves, over its mean. A kernel that is memory-bound says so here";

    /// <inheritdoc/>
    public bool IsDefault(Summary summary, BenchmarkCase benchmarkCase) => false;

    /// <inheritdoc/>
    public bool IsAvailable(Summary summary) =>
        summary.BenchmarksCases.Any(c => Work.Of(c) is { Bytes: > 0 });

    /// <inheritdoc/>
    public string GetValue(Summary summary, BenchmarkCase benchmarkCase)
    {
        double? mean = summary[benchmarkCase]?.ResultStatistics?.Mean;
        if (mean is null || mean.Value <= 0 || Work.Of(benchmarkCase) is not { Bytes: > 0 } work)
        {
            return "-";
        }

        // Bytes per nanosecond IS gigabytes per second, which is the one unit conversion in this
        // file and the reason it needs no constant.
        return (work.Bytes / mean.Value).ToString("F2", CultureInfo.InvariantCulture);
    }

    /// <inheritdoc/>
    public string GetValue(Summary summary, BenchmarkCase benchmarkCase, SummaryStyle style) =>
        GetValue(summary, benchmarkCase);
}
