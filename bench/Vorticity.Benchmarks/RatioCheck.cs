// The ratio gate: our time over Rust's, held to a ceiling, as a command rather than a benchmark.
//
// docs/05-benchmarks.md separates the quantities that can be locked in CI from the ones that cannot,
// and puts the ratio against Rust in a category of its own. Absolute microseconds depend on the
// machine, the thermal state and the runner, so gating on them produces a test that fails for
// reasons nobody caused. The RATIO does not: both implementations run in the same process on the
// same bytes with the same clock, so a slower machine slows both sides and the quotient survives.
// It is the only speed measurement in this repository that means the same thing on two machines.
//
// WHY NOT A UNIT TEST, WHEN THE ALLOCATION CEILINGS ARE ONE. Allocations are exactly reproducible
// and cost nothing to measure, so they belong in the suite. This needs a 36 MB cdylib that has to be
// built by hand, and it needs seconds of wall clock to say anything. Putting either in `dotnet test`
// would make the suite slower and conditionally red, which is how a suite stops being run.
//
// WHY NOT A BENCHMARKDOTNET ASSERTION. BenchmarkDotNet runs one benchmark to completion and then
// the next, which is precisely the shape docs/05 §5 warns about: "thermal drift over a long run
// systematically favors whoever goes first." For a ratio that bias is not noise, it is a systematic
// error in the quantity being gated. So the two sides are INTERLEAVED here - one iteration of ours,
// one of theirs, alternating which goes first - and drift becomes common-mode instead of a result.
// The median of each side is taken rather than the mean, so one GC or one scheduler preemption
// moves nothing.
//
// The ceilings are ratchets like every other number in this repository: set just above what was
// measured, lowered by hand when an improvement lands, red on a regression.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

using Vorticity.Columns;
using Vorticity.File;
using Vorticity.Scan;

namespace Vorticity.Benchmarks;

/// <summary>Checks each comparison axis against its ratio ceiling.</summary>
internal static class RatioCheck
{
    /// <summary>
    /// How long each axis is exercised, per side, before a single round is measured.
    /// </summary>
    /// <remarks>
    /// A BUDGET RATHER THAN A COUNT, and the first version of this file got it wrong in a way worth
    /// keeping written down. Five warm-up iterations made every managed axis read 2-5x slower than
    /// BenchmarkDotNet reports for identical work - a full scan at 5 260 us against 1 294 - while
    /// the Rust side came back within 2% of its published figure on all four axes. One side wrong
    /// and the other right is not thermal drift and not a regression: Rust is native from its first
    /// call, and our side was still running tier-0 code. The harness was measuring the JIT's
    /// tiering state.
    ///
    /// BenchmarkDotNet avoids this by warming until its measurements stabilize, which for a
    /// hundred-microsecond method is thousands of invocations, not five. A wall-clock budget buys
    /// the same thing without a convergence loop: whatever the axis costs, it is called enough times
    /// to be promoted and to have its loops replaced on-stack.
    /// </remarks>
    private static readonly TimeSpan WarmupBudget = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Interleaved rounds measured, per axis.
    /// </summary>
    /// <remarks>
    /// Raised from 21 because the short axes needed it. Interleaving buys immunity to thermal drift
    /// and charges for it in cache locality: `open to first batch` is ~100 us of managed work
    /// measured immediately after a 1.2 ms native call that has just walked over the caches, where
    /// BenchmarkDotNet runs it in a tight loop of nothing but itself. The effect is real and one-
    /// sided, and it showed up as a median moving +-9% run to run on that axis against +-1% on the
    /// full scan. More rounds is the cheap fix: every round of that axis is dominated by Rust's side
    /// anyway, so 51 of them still costs under a tenth of a second.
    /// </remarks>
    private const int Rounds = 51;

    /// <summary>
    /// The margin over the reference ratio, per docs/05's "failure beyond +15% of the reference".
    /// </summary>
    /// <remarks>
    /// The ratio is LARGELY deterministic, not entirely: the two sides do not share a page-cache
    /// state or a allocator, and 15% is what the observed round-to-round spread leaves comfortable.
    /// A tighter margin would turn this into a flake generator, which gates a change nobody made.
    /// </remarks>
    private const double Margin = 1.15;

    /// <summary>One axis: what both sides do, and what the quotient is allowed to be.</summary>
    /// <param name="Name">The axis, for the report.</param>
    /// <param name="Reference">The ratio measured by this harness when the ceiling was last set.</param>
    /// <param name="Ours">Our side.</param>
    /// <param name="Theirs">Rust's side.</param>
    private sealed record Axis(
        string Name, double Reference, Func<string, Task<long>> Ours, Func<string, long> Theirs);

    /// <summary>
    /// The axes, with the ratio each was measured at. LOWER A REFERENCE BY HAND when an
    /// improvement lands, in the same commit, and never raise one without saying in the commit
    /// message what got slower and why that is acceptable.
    /// </summary>
    /// <remarks>
    /// These are this harness's own numbers, not bench/BASELINE.md's, and on two of the four axes
    /// they are not close to them. Setting a ceiling here from a figure measured there would be
    /// gating one estimator with another's answer, which is how the first version of this file came
    /// out red on every axis.
    ///
    ///     axis                    here    BenchmarkDotNet
    ///     full scan               0.953   0.966
    ///     open, footer only       0.732   0.752
    ///     projected scan          0.514   0.392
    ///     open to first batch     0.093   0.060
    ///
    /// The two that agree are the long axis and the trivial one; the two that do not are short
    /// managed paths measured beside a native call an order of magnitude longer. Both estimators
    /// are honest about different things. BenchmarkDotNet reports the best case, a tight loop of
    /// one path with the caches to itself; this reports a path sharing a process with other work,
    /// which is the case a gate should defend. Neither figure should be quoted as the other.
    /// </remarks>
    private static readonly Axis[] Axes =
    [
        new Axis("full scan", 0.953, ScanAll, p => RustReader.Require(RustReader.ScanAll(p), "scan")),
        new Axis(
            "projected scan, 1 of 5 columns",
            0.514,
            ScanProjected,
            p => RustReader.Require(RustReader.ScanProjected(p, Field), "projected scan")),
        new Axis(
            "open to first batch",
            0.093,
            FirstBatch,
            p => RustReader.Require(RustReader.OpenFirstBatch(p), "first batch")),
        new Axis(
            "open, footer only",
            0.732,
            FooterOnly,
            p => RustReader.Require(RustReader.OpenOnly(p), "open")),
    ];

    /// <summary>The field the projection axis reads; it exists in the default dataset.</summary>
    private const string Field = "monotone";

    /// <summary>How many warm-up iterations each axis got, for the report.</summary>
    private static readonly Dictionary<string, int> Warmed = [];

    /// <summary>Runs every axis and reports.</summary>
    /// <returns>0 when every axis is inside its ceiling, 1 when one is not, 2 with no harness.</returns>
    internal static async Task<int> RunAsync()
    {
        if (!RustReader.Available)
        {
            Console.Error.WriteLine(
                $"vxbench not found (looked for {RustReader.ExpectedPath}).\n" +
                "Build it with: cd tools/vxbench-rs && cargo build --release");
            return 2;
        }

        string path = Corpus.Dataset("VORTICITY_BENCH_DATA", "containers/zoned_many_zones_nulls");

        // The same precondition --ffi-check exists for, asserted here rather than assumed: a ratio
        // between two readers that return different row counts is not a ratio, and this would
        // report one just as confidently.
        long ours = await ScanAll(path).ConfigureAwait(false);
        long theirs = RustReader.Require(RustReader.ScanAll(path), "scan");
        if (ours != theirs)
        {
            Console.Error.WriteLine(
                $"The two readers disagree on {path}: {ours} rows versus {theirs}. " +
                "No ratio over this file means anything.");
            return 2;
        }

        Console.Out.WriteLine(
            $"RATIO CHECK: median of {Rounds} interleaved rounds after a " +
            $"{WarmupBudget.TotalSeconds.ToString("F0", CultureInfo.InvariantCulture)}s warm-up per axis, " +
            $"ceiling = reference x {Margin.ToString("F2", CultureInfo.InvariantCulture)}");
        Console.Out.WriteLine(
            "  axis                             ours      rust     ratio  reference  ceiling   warmed");

        int over = 0;
        foreach (Axis axis in Axes)
        {
            (double mine, double rust) = await MeasureAsync(path, axis).ConfigureAwait(false);
            double ratio = mine / rust;
            double ceiling = axis.Reference * Margin;
            bool bad = ratio > ceiling;
            over += bad ? 1 : 0;

            // The warm-up count is reported, not asserted: it is the number that explains an
            // implausible ratio, and a plausible one is not evidence that it was high enough.
            string verdict = bad ? "   OVER" : string.Empty;
            int warmed = Warmed[axis.Name];
            Console.Out.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"  {axis.Name,-30} {mine,8:F1}us {rust,8:F1}us {ratio,8:F3} {axis.Reference,10:F3} {ceiling,8:F3} {warmed,8}{verdict}"));
        }

        Console.Out.WriteLine(
            over == 0
                ? "Every axis is inside its ceiling."
                : $"{over} axis/axes above ceiling. A ratio only moves when the code moves: " +
                  "find the change, do not raise the ceiling.");
        return over == 0 ? 0 : 1;
    }

    /// <summary>
    /// Times both sides of one axis, interleaved, and returns the median microseconds of each.
    /// </summary>
    /// <remarks>
    /// Which side goes first alternates by round. Without that, whichever runs first in every round
    /// pays the cache-cold cost every time and the other never does - a bias that would be stable
    /// across runs, which is worse than noise because it looks like a result.
    /// </remarks>
    private static async Task<(double Ours, double Theirs)> MeasureAsync(string path, Axis axis)
    {
        long deadline = Stopwatch.GetTimestamp() +
            (long)(WarmupBudget.TotalSeconds * Stopwatch.Frequency);
        int warmed = 0;
        do
        {
            await axis.Ours(path).ConfigureAwait(false);
            axis.Theirs(path);
            warmed++;
        }
        while (Stopwatch.GetTimestamp() < deadline);

        Warmed[axis.Name] = warmed;

        double[] mine = new double[Rounds];
        double[] rust = new double[Rounds];
        for (int round = 0; round < Rounds; round++)
        {
            if (round % 2 == 0)
            {
                mine[round] = await TimeAsync(axis.Ours, path).ConfigureAwait(false);
                rust[round] = Time(axis.Theirs, path);
            }
            else
            {
                rust[round] = Time(axis.Theirs, path);
                mine[round] = await TimeAsync(axis.Ours, path).ConfigureAwait(false);
            }
        }

        return (Median(mine), Median(rust));
    }

    private static async Task<double> TimeAsync(Func<string, Task<long>> work, string path)
    {
        long start = Stopwatch.GetTimestamp();
        await work(path).ConfigureAwait(false);
        return Stopwatch.GetElapsedTime(start).TotalMicroseconds;
    }

    private static double Time(Func<string, long> work, string path)
    {
        long start = Stopwatch.GetTimestamp();
        work(path);
        return Stopwatch.GetElapsedTime(start).TotalMicroseconds;
    }

    private static double Median(double[] samples)
    {
        double[] sorted = (double[])samples.Clone();
        Array.Sort(sorted);
        return sorted[sorted.Length / 2];
    }

    private static async Task<long> ScanAll(string path)
    {
        await using VortexFile file = await VortexFile.OpenAsync(path, CancellationToken.None);
        long rows = 0;
        await foreach (RecordBatch batch in file.Scan().ExecuteAsync()
            .WithCancellation(CancellationToken.None))
        {
            rows += batch.RowCount;
        }

        return rows;
    }

    private static async Task<long> ScanProjected(string path)
    {
        await using VortexFile file = await VortexFile.OpenAsync(path, CancellationToken.None);
        long rows = 0;
        await foreach (RecordBatch batch in file.Scan().Project(Field).ExecuteAsync()
            .WithCancellation(CancellationToken.None))
        {
            rows += batch.RowCount;
        }

        return rows;
    }

    private static async Task<long> FirstBatch(string path)
    {
        await using VortexFile file = await VortexFile.OpenAsync(path, CancellationToken.None);
        await foreach (RecordBatch batch in file.Scan().ExecuteAsync()
            .WithCancellation(CancellationToken.None))
        {
            return batch.RowCount;
        }

        return 0;
    }

    private static async Task<long> FooterOnly(string path)
    {
        await using VortexFile file = await VortexFile.OpenAsync(path, CancellationToken.None);
        return file.RowCount;
    }
}
