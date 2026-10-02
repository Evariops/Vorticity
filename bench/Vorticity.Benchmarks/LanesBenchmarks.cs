using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Diagnosers;

using Set = Vorticity.Bench.Scenarios.ScenarioSet;

namespace Vorticity.Benchmarks;

/// <summary>Our scan at n lanes against the reference on a Tokio runtime of n workers.</summary>
/// <remarks>
/// <para>
/// The thread count is pinned on both sides: a ratio between an n-lane reader and a reference free
/// to use every core measures a threading model, not a decoder.
/// </para>
/// <para>
/// One lane is not the single-threaded path on either side, and the class measures it anyway: ours
/// at degree 1 takes the non-pipelined branch, theirs at one worker still hands the work to that
/// worker. The number to read is where the speed-up stops, and whether the two stop in the same
/// place.
/// </para>
/// <para>
/// <c>[ThreadingDiagnoser]</c> because the question behind the class is contention: lock contention
/// and completed work items tell a lane that did work from a lane that waited.
/// </para>
/// </remarks>
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
    public void Setup()
    {
        // Here and not only in Program.Main: under `--full` the class runs in a process of
        // BenchmarkDotNet's, which never enters ours.
        Scenarios.OpenFilesCold();
        _path = Corpus.Dataset("VORTICITY_BENCH_DATA", "containers/zoned_many_zones_nulls");
    }

    [Benchmark(Baseline = true, Description = "ours, n lanes")]
    public long Ours() => Set.ScanAllLanes(_path, Lanes).GetAwaiter().GetResult();

    [Benchmark(Description = "reference, n workers")]
    public long Reference() =>
        RustReader.Require(RustReader.ScanCanonicalThreads(_path, Lanes), "threaded scan");
}
