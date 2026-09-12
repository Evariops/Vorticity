// What zone-map pruning is worth as the filter gets less selective.
//
// docs/05-benchmarks.md §3 asks for filter pushdown at 1%, 10% and 50% selectivity and has never had
// it. `ScanBenchmarks` measures exactly one band - about 100 rows of 65 536, roughly 0.15% - with
// pruning on and off, and reports a single ratio. That number is the BEST CASE by construction: a
// band narrow enough to live in one or two zones is the case pruning exists for, and quoting its
// speedup as "what pruning is worth" says nothing about the predicate a user actually writes.
//
// The curve is the answer, because pruning has to stop paying somewhere. A zone map skips a zone
// only when the whole zone falls outside the band, so as the band widens the number of skippable
// zones falls to zero and the pruned path converges on the unpruned one plus the cost of having
// checked. Where that crossover sits is a property of the file's zone length and of nothing else,
// and until it is measured, "pruning is worth 7.4x" is a sentence about one predicate.
using System;
using System.Threading;
using System.Threading.Tasks;

using BenchmarkDotNet.Attributes;

using Vorticity.Columns;
using Vorticity.Expressions;
using Vorticity.File;
using Vorticity.Scan;

namespace Vorticity.Benchmarks;

/// <summary>A selective filter at four selectivities, with and without pruning.</summary>
[Config(typeof(BenchmarkConfig))]
public class FilterSelectivityBenchmarks
{
    /// <summary>`monotone` starts here and steps by 3, so a band of 3n rows is n wide in value.</summary>
    private const long Base = 1_000_000;

    /// <summary>The step between consecutive values of the `monotone` column.</summary>
    private const long Step = 3;

    /// <summary>Rows in the file, which turns a percentage into a band width.</summary>
    private const long Rows = 65_536;

    private string _path = string.Empty;

    /// <summary>Percent of rows the predicate matches.</summary>
    [Params(1, 10, 50, 90)]
    public int Percent { get; set; } = 1;

    /// <summary>Whether zone-map pruning is allowed.</summary>
    [Params(true, false)]
    public bool Prune { get; set; }

    [GlobalSetup]
    public void Setup() =>
        _path = Corpus.Dataset("VORTICITY_BENCH_DATA", "containers/zoned_many_zones_nulls");

    [Benchmark(Description = "selective filter")]
    public async Task<long> Filter()
    {
        // The band starts at the column's own base, so every selectivity measures a PREFIX of the
        // column. Starting it in the middle would change which zones are skippable as well as how
        // many, and two variables in one axis is not an axis.
        long width = Rows * Step * Percent / 100;
        VortexExpr filter = Expr.And(
            Expr.Ge(Expr.Field("monotone"), Expr.Literal(FilterLiteral.From(Base))),
            Expr.Lt(Expr.Field("monotone"), Expr.Literal(FilterLiteral.From(Base + width))));

        await using VortexFile file = await VortexFile.OpenAsync(_path, CancellationToken.None);
        long rows = 0;
        await foreach (RecordBatch batch in file.Scan()
            .Project("monotone")
            .Where(filter)
            .WithPruning(Prune)
            .ExecuteAsync()
            .WithCancellation(CancellationToken.None))
        {
            rows += batch.RowCount;
        }

        return rows;
    }
}
