// A speedup in the kernel tables, instead of BenchmarkDotNet's ratio.
//
// BenchmarkDotNet's `Ratio` is a row's time over its baseline's: under 1.00, the row took less. Every
// other table the bench publishes prints a speedup, the other side's time over the measured side's,
// a figure that grows with the answer a reader is after; a kernel table reading the other way on the
// same page would be read wrong. So `BenchmarkConfig` hides the ratio and its deviation, and this
// prints the baseline's mean over the row's.
//
// A RATIO OF MEANS, which anyone can check against the Mean column. BenchmarkDotNet's ratio is the
// mean of every pairwise ratio of the two rows' measurements instead; the two differ in the third
// figure only where a row's own spread is wide, and `MannWhitney(5%)` beside it is what says whether
// a difference is one.
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;

namespace Vorticity.Benchmarks;

/// <summary>A row's speedup over its baseline: the baseline's mean over the row's.</summary>
internal sealed class SpeedupColumn : IColumn
{
    /// <inheritdoc/>
    public string Id => nameof(SpeedupColumn);

    /// <inheritdoc/>
    public string ColumnName => "Speedup";

    /// <inheritdoc/>
    public bool AlwaysShow => true;

    /// <inheritdoc/>
    public ColumnCategory Category => ColumnCategory.Baseline;

    /// <inheritdoc/>
    public int PriorityInCategory => 0;

    /// <inheritdoc/>
    public bool IsNumeric => true;

    /// <inheritdoc/>
    public UnitType UnitType => UnitType.Dimensionless;

    /// <inheritdoc/>
    public string Legend => "The baseline's mean over this row's: above 1.00x, this row took less";

    /// <inheritdoc/>
    public bool IsDefault(Summary summary, BenchmarkCase benchmarkCase) => false;

    /// <inheritdoc/>
    public bool IsAvailable(Summary summary) => summary.HasBaselines();

    /// <inheritdoc/>
    public string GetValue(Summary summary, BenchmarkCase benchmarkCase)
    {
        BenchmarkCase? baseline = summary.GetBaseline(summary.GetLogicalGroupKey(benchmarkCase));
        double? mean = summary[benchmarkCase]?.ResultStatistics?.Mean;
        double? baselineMean = baseline is null ? null : summary[baseline]?.ResultStatistics?.Mean;
        return mean is > 0 && baselineMean is > 0 ? ResultsPage.Speedup(baselineMean.Value / mean.Value) : "-";
    }

    /// <inheritdoc/>
    public string GetValue(Summary summary, BenchmarkCase benchmarkCase, SummaryStyle style) =>
        GetValue(summary, benchmarkCase);
}
