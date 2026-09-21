// What zone-map pruning is worth as the filter gets less selective.
//
// Filter pushdown at 1%, 10% and 50% selectivity is a benchmark axis that nothing measured.
// `PathAllocationTests` exercises exactly one band - about 100 rows of 65 536, roughly 0.15% -
// and that band is the BEST CASE by construction: a
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
using Vorticity.Scanning;

namespace Vorticity.Benchmarks;

/// <summary>A selective filter at two selectivities, pruned and not. A curve: <c>--explore</c>.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public class FilterSelectivityBenchmarks
{
    /// <summary>The column the band is taken on.</summary>
    private const string Field = "monotone";

    /// <summary>
    /// The file's own shape: rows, the column's first value, and its step.
    /// </summary>
    /// <remarks>
    /// READ FROM THE FILE, NOT WRITTEN DOWN. These were three constants --
    /// 65 536 rows, a base of 1 000 000, a step of 3 -- all properties of ONE corpus entry, in a
    /// class that honours `VORTICITY_BENCH_DATA`. Pointing that variable at another file kept the
    /// constants and measured a band with nothing in it, silently: a percentage of a row count that
    /// was not the row count, starting at a value the column never takes.
    /// </remarks>
    private Corpus.ColumnShape _shape;

    private string _path = string.Empty;

    /// <summary>
    /// Percent of rows the predicate matches. The two ends rather than four points: `--ratio-check`
    /// holds 1 % and 50 % against Rust, and the arm without pruning is flat by construction, so the
    /// intermediate points are shape rather than guard.
    /// </summary>
    [Params(1, 50)]
    public int Percent { get; set; } = 1;

    /// <summary>Whether zone-map pruning is allowed.</summary>
    [Params(true, false)]
    public bool Prune { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _path = Corpus.Dataset("VORTICITY_BENCH_DATA", "containers/zoned_many_zones_nulls");
        _shape = Corpus.RequireIntegerColumn(_path, Field, "VORTICITY_BENCH_DATA");
    }

    [Benchmark(Description = "selective filter")]
    public async Task<long> Filter()
    {
        // The band starts at the column's own base, so every selectivity measures a PREFIX of the
        // column. Starting it in the middle would change which zones are skippable as well as how
        // many, and two variables in one axis is not an axis.
        long width = _shape.Rows * _shape.Step * Percent / 100;
        VortexExpr filter = Expr.And(
            Expr.Ge(Expr.Field(Field), Expr.Literal(FilterLiteral.From(_shape.First))),
            Expr.Lt(Expr.Field(Field), Expr.Literal(FilterLiteral.From(_shape.First + width))));

        await using VortexFile file = await VortexFile.OpenAsync(_path, CancellationToken.None);
        long rows = 0;
        await foreach (RecordBatch batch in file.Scan()
            .Project(Field)
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
