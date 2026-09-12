// The axes of docs/05-benchmarks.md §3 that can be measured without the FFI harness.
//
// WHAT THIS IS AND IS NOT. §2 specifies the real comparison as single-process, loading `vortex-ffi`
// through DllImport so both implementations are measured on the same data with the same clock and
// the same page-cache state. That harness does not exist yet, so nothing here reports a RATIO
// against Rust, and no number below should be quoted as one. What it does give is the other half of
// the protocol -- absolute figures per axis, with allocations, on a fixed dataset -- which is what
// makes a regression visible and what a SIMD kernel has to beat.
//
// MemoryDiagnoser is on for every benchmark because invariant 1 is a benchmark result, not a test
// result: "zero managed allocation per batch in steady state, excluding output buffers". The scan
// benchmarks are where that is observable at scale.
using System;
using System.Threading;
using System.Threading.Tasks;

using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;

using Vorticity.Columns;
using Vorticity.Expressions;
using Vorticity.File;
using Vorticity.Scan;

namespace Vorticity.Benchmarks;

/// <summary>Whole-file and projected scans, the headline axes.</summary>
[Config(typeof(BenchmarkConfig))]
public class ScanBenchmarks
{
    private string _path = string.Empty;
    private string _wide = string.Empty;

    [GlobalSetup]
    public void Setup()
    {
        // 65536 rows over five columns of mixed types, with nulls and NaNs: the widest realistic
        // shape the corpus carries. VORTICITY_BENCH_DATA points at a real dataset when one is
        // available, without changing a line here.
        _path = Corpus.Dataset("VORTICITY_BENCH_DATA", "containers/zoned_many_zones_nulls");
        _wide = Corpus.Dataset("VORTICITY_BENCH_WIDE", "containers/zoned_many_zones_nulls");
    }

    /// <summary>Full scan: rows/s and bytes/s, every column decoded.</summary>
    [Benchmark(Baseline = true)]
    public async Task<long> FullScan()
    {
        await using VortexFile file = await VortexFile.OpenAsync(_path, CancellationToken.None);
        long rows = 0;
        await foreach (RecordBatch batch in file.Scan().ExecuteAsync()
            .WithCancellation(CancellationToken.None))
        {
            rows += batch.RowCount;
        }

        return rows;
    }

    /// <summary>
    /// One column out of five. Tests I/O pruning rather than decode: the case Vortex is built for.
    /// </summary>
    [Benchmark]
    public async Task<long> ProjectedScan()
    {
        await using VortexFile file = await VortexFile.OpenAsync(_wide, CancellationToken.None);
        long rows = 0;
        await foreach (RecordBatch batch in file.Scan().Project("monotone").ExecuteAsync()
            .WithCancellation(CancellationToken.None))
        {
            rows += batch.RowCount;
        }

        return rows;
    }

    /// <summary>Time to the FIRST batch: the 1-2 round trip promise, on a local file.</summary>
    /// <remarks>
    /// docs/05 §3 notes this is the object-storage metric and that it is nearly invisible locally,
    /// where round trips are free. It is here so the local floor is known before the HTTP source
    /// with injected latency is wired up: a regression that turns one read into three shows up as a
    /// change in this number even locally.
    /// </remarks>
    [Benchmark]
    public async Task<long> OpenAndFirstBatch()
    {
        await using VortexFile file = await VortexFile.OpenAsync(_path, CancellationToken.None);
        await foreach (RecordBatch batch in file.Scan().ExecuteAsync()
            .WithCancellation(CancellationToken.None))
        {
            return batch.RowCount;
        }

        return 0;
    }

    /// <summary>A selective filter, with and without pruning, so the pruning is measurable.</summary>
    [Benchmark]
    [Arguments(true)]
    [Arguments(false)]
    public async Task<long> FilteredScan(bool prune)
    {
        // A narrow band of a sorted column: ~100 rows of 65536, which is the case pruning exists
        // for. Running it both ways in one benchmark is what makes the saving a number rather than
        // a claim.
        VortexExpr filter = Expr.And(
            Expr.Ge(Expr.Field("monotone"), Expr.Literal(FilterLiteral.From(1_003_000L))),
            Expr.Lt(Expr.Field("monotone"), Expr.Literal(FilterLiteral.From(1_003_300L))));

        await using VortexFile file = await VortexFile.OpenAsync(_path, CancellationToken.None);
        long rows = 0;
        await foreach (RecordBatch batch in file.Scan()
            .Project("monotone")
            .Where(filter)
            .WithPruning(prune)
            .ExecuteAsync()
            .WithCancellation(CancellationToken.None))
        {
            rows += batch.RowCount;
        }

        return rows;
    }

    /// <summary>Scattered random access: the claim over Parquet.</summary>
    [Benchmark]
    public async Task<long> TakeScatteredRows()
    {
        long[] rows = new long[1000];
        for (int i = 0; i < rows.Length; i++)
        {
            // Spread across the file so most splits hold exactly one wanted row.
            rows[i] = (long)i * 61;
        }

        await using VortexFile file = await VortexFile.OpenAsync(_path, CancellationToken.None);
        long taken = 0;
        await foreach (RecordBatch batch in file.Scan()
            .Project("monotone")
            .Take(rows)
            .ExecuteAsync()
            .WithCancellation(CancellationToken.None))
        {
            taken += batch.RowCount;
        }

        return taken;
    }
}
