using System;
using System.Linq;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Exporters;
using BenchmarkDotNet.Loggers;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;
using BenchmarkDotNet.Toolchains.InProcess.Emit;

namespace Vorticity.Zstd.Benchmarks;

/// <summary>
/// The default job out of process (BenchmarkDotNet 0.16 runs net11.0 that way), allocations measured,
/// and a speedup column. <c>--inprocess</c> on the command line keeps everything in this process.
/// </summary>
public sealed class BenchmarkConfig : ManualConfig
{
    internal static bool InProcess { get; set; }

    /// <summary>A <c>--job</c> on the command line replaces the default job instead of adding to it.</summary>
    internal static bool ExplicitJob { get; set; }

    public BenchmarkConfig()
    {
        Job job = Job.Default;
        if (InProcess)
        {
            job = job.WithToolchain(InProcessEmitToolchain.Default);
        }

        if (!ExplicitJob)
        {
            AddJob(job);
        }

        AddLogger(ConsoleLogger.Default);
        AddColumnProvider(DefaultColumnProviders.Instance);
        AddExporter(MarkdownExporter.GitHub);
        AddAnalyser(DefaultConfig.Instance.GetAnalysers().ToArray());
        AddValidator(DefaultConfig.Instance.GetValidators().ToArray());
        AddDiagnoser(MemoryDiagnoser.Default);
        AddColumn(new SpeedupColumn());
        HideColumns(Column.Error, Column.StdDev, Column.RatioSD);
    }
}

/// <summary>The baseline's mean time divided by this case's: above 1 is faster than the baseline.</summary>
public sealed class SpeedupColumn : IColumn
{
    public string Id => nameof(SpeedupColumn);
    public string ColumnName => "Speedup";
    public bool AlwaysShow => true;
    public ColumnCategory Category => ColumnCategory.Custom;
    public int PriorityInCategory => 0;
    public bool IsNumeric => true;
    public UnitType UnitType => UnitType.Dimensionless;
    public string Legend => "Baseline mean / this mean (higher is faster)";

    public string GetValue(Summary summary, BenchmarkCase benchmarkCase) => GetValue(summary, benchmarkCase, SummaryStyle.Default);

    public string GetValue(Summary summary, BenchmarkCase benchmarkCase, SummaryStyle style)
    {
        BenchmarkCase? baseline = summary.GetBaseline(summary.GetLogicalGroupKey(benchmarkCase));
        double? mine = summary[benchmarkCase]?.ResultStatistics?.Mean;
        double? theirs = baseline is null ? null : summary[baseline]?.ResultStatistics?.Mean;
        return mine is > 0 && theirs is > 0 ? (theirs.Value / mine.Value).ToString("0.00x", System.Globalization.CultureInfo.InvariantCulture) : "-";
    }

    public bool IsAvailable(Summary summary) => true;
    public bool IsDefault(Summary summary, BenchmarkCase benchmarkCase) => false;
}
