// What a lane buys, on both sides, at the same thread count.
//
// BENCH-AUDIT.md D2: `ScanBuilder.WithDegreeOfParallelism` existed and nothing measured it, and
// `vxbench` was single-threaded by construction -- so the contention family of PERF-AUDIT-v2.md §7
// had no number at all, and v2 says, rightly, to optimize nothing there before the bench exists.
// This is the bench.
//
// THE THREAD COUNT IS PINNED ON BOTH SIDES, which docs/05 §5 requires and which is the only way the
// pair means anything: a ratio between an n-lane reader and a reference free to use every core
// measures a threading model, not a decoder. `vxbench_scan_canonical_threads` drives upstream's
// worker pool with exactly n workers for the same reason we ask for exactly n lanes.
//
// ONE LANE IS NOT THE SINGLE-THREADED PATH on either side, and the class says so by measuring it
// anyway: ours at degree 1 takes the non-pipelined branch, theirs at 1 worker still pays the pool
// hand-off that the single-thread runtime does not. The interesting number is not the speed-up
// alone -- it is where the speed-up stops, and whether the two stop in the same place.
//
// `[ThreadingDiagnoser]` because the question behind the family is contention: lock contention and
// completed work items are what tell a lane that did work from a lane that waited.
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Diagnosers;

using Set = Vorticity.Bench.Scenarios.ScenarioSet;

namespace Vorticity.Benchmarks;

/// <summary>Our scan at n lanes against the reference's pool at n workers.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Path)]
[ThreadingDiagnoser]
public class LanesBenchmarks
{
    /// <summary>Lanes in flight, and worker threads on the reference's side.</summary>
    [ParamsSource(nameof(Degrees))]
    public int Lanes { get; set; } = 4;

    /// <summary>The lane counts the current profile measures.</summary>
    /// <remarks>
    /// Four by default -- one point is not a curve, but four arms of a curve in the run with no
    /// argument is a curve nobody asked for. `--full` walks 1, 2, 4, 8: one, because it is the
    /// baseline everything else is read against; eight, because this machine has more cores than
    /// that and the answer must be allowed to stop improving before it runs out of hardware.
    /// </remarks>
    public static IEnumerable<int> Degrees => BenchmarkConfig.Full ? [1, 2, 4, 8] : [4];

    private string _path = string.Empty;

    [GlobalSetup]
    public void Setup() => _path = Corpus.Dataset(
        "VORTICITY_BENCH_DATA", "containers/zoned_many_zones_nulls");

    [Benchmark(Baseline = true, Description = "ours, n lanes")]
    public long Ours() => Set.ScanAllLanes(_path, Lanes).GetAwaiter().GetResult();

    [Benchmark(Description = "reference, n workers")]
    public long Reference() =>
        RustReader.Require(RustReader.ScanCanonicalThreads(_path, Lanes), "threaded scan");
}
