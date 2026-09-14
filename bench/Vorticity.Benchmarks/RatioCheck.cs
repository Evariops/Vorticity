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
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Vorticity.Columns;
using Vorticity.Expressions;
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
    /// Rounds measured before the interval is consulted.
    /// </summary>
    /// <remarks>
    /// Interleaving buys immunity to thermal drift and charges for it in cache locality: `open to
    /// first batch` is ~100 us of managed work measured immediately after a 1.2 ms native call that
    /// has just walked over the caches, where BenchmarkDotNet runs it in a tight loop of nothing but
    /// itself. The effect is real and one-sided. Rounds are the cheap fix, and how many are needed
    /// is not the same for every axis -- so this is a FLOOR, and <see cref="MaxRounds"/> with
    /// <see cref="Precision"/> decide the rest.
    /// </remarks>
    private const int MinRounds = 21;

    /// <summary>The ceiling on rounds, when the interval never gets tight enough.</summary>
    private const int MaxRounds = 501;

    /// <summary>
    /// Rounds stop when half the interval width is within this fraction of the median.
    /// </summary>
    /// <remarks>
    /// 5% against a 15% margin: the gate can then see a regression a third the size of what it is
    /// allowed to pass, rather than the +12 to +22% run-to-run spread that used to sit on top of it.
    /// An axis that cannot get there inside <see cref="Budget"/> says so in its own column instead
    /// of pretending.
    /// </remarks>
    private const double Precision = 0.05;

    /// <summary>Wall clock an axis may spend on rounds before it stops asking for more.</summary>
    private const double Budget = 10.0;

    /// <summary>
    /// A round times at least this long, by repeating the work when one call is shorter.
    /// </summary>
    /// <remarks>
    /// Under ~200 us a single `Stopwatch`-timed call is dominated by timer, cache and scheduler
    /// jitter, and a median over rounds does not recover what no round resolved (BENCH-AUDIT.md
    /// §4.4). Repeating k times inside one timed round is the same total work with the jitter
    /// divided by k. The estimator changes with it -- k calls in a row measure a WARM path, where a
    /// single call measured a cache-cold one -- which is the path a reader that opens ten files in
    /// a row actually exercises.
    /// </remarks>
    private const double MinRoundMicroseconds = 1_000;

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
    /// <param name="Name">The axis, for the report and its key in <see cref="References"/>.</param>
    /// <param name="Ours">Our side.</param>
    /// <param name="Theirs">Rust's side.</param>
    /// <param name="Path">
    /// The file this axis runs on, when it is not the dataset. Only the <c>rewritten</c> group sets
    /// it: every other axis is a different QUESTION about one file, and that group is the same
    /// question about two files.
    /// </param>
    private sealed record Axis(
        string Name,
        Func<string, Task<long>> Ours,
        Func<string, long> Theirs,
        string? Path = null);

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
    /// (The full-scan row is from before the axis was made like-for-like; it now reads 0.536,
    /// because the reference side canonicalizes and the reader side got faster.)
    ///
    /// The two that agree are the long axis and the trivial one; the two that do not are short
    /// managed paths measured beside a native call an order of magnitude longer. Both estimators
    /// are honest about different things. BenchmarkDotNet reports the best case, a tight loop of
    /// one path with the caches to itself; this reports a path sharing a process with other work,
    /// which is the case a gate should defend. Neither figure should be quoted as the other.
    ///
    /// THERE ARE TWO FULL-SCAN AXES, AND THEY MEASURE DIFFERENT QUESTIONS. Upstream's scan hands
    /// back arrays in the file's own encodings and `len()` answers from their metadata, so nothing
    /// is decompressed; the .NET reader has no lazy state, because `CanonicalArena` is the only
    /// representation it has. "full scan" therefore drives the Rust side through
    /// `execute::&lt;Canonical&gt;`, which is the comparison a decoder ratio has to be built on.
    /// "full scan, upstream lazy" keeps the old call beside it, because "how long to get a stream
    /// of arrays you may never fully read" is a real question about a real API -- it just is not
    /// the same question, and quoting one as the other is what this pair exists to prevent.
    /// </remarks>
    private static readonly Axis[] Axes =
    [
        // THE FIRST FOUR COME FROM `Scenarios`, which is what makes `--profile fullscan` a
        // statement about THIS axis and not about a scan that resembles it (A2).
        FromScenario("fullscan"),
        new Axis(
            "full scan, upstream lazy",
            ScanAll,
            p => RustReader.Require(RustReader.ScanAll(p), "scan")),
        FromScenario("projected"),
        new Axis(
            "open to first batch",
            FirstBatch,
            p => RustReader.Require(RustReader.OpenFirstBatch(p), "first batch")),
        new Axis(
            "open, footer only",
            FooterOnly,
            p => RustReader.Require(RustReader.OpenOnly(p), "open")),
        FromScenario("write"),
        new Axis(
            "filtered scan, 1% band",
            p => FilteredScan(p, BandLow, NarrowBand),
            p => RustReader.Require(
                RustReader.ScanFiltered(p, Field, BandLow, NarrowBand), "filtered scan")),
        new Axis(
            "filtered scan, half the rows",
            p => FilteredScan(p, BandLow, WideBand),
            p => RustReader.Require(
                RustReader.ScanFiltered(p, Field, BandLow, WideBand), "filtered scan")),
        FromScenario("take"),
    ];

    /// <summary>Lanes asked for by `--lanes N`, or 0 for the lane-free axes only.</summary>
    /// <remarks>
    /// A LANE AXIS IS ADDED, NOT SUBSTITUTED. The thirteen axes are ratchets measured at one lane;
    /// swapping one for a lane version would compare a number against a reference that does not
    /// describe it. This appends `full scan, N lanes` instead, with the thread count PINNED on both
    /// sides (docs/05 §5) -- and it arrives without a reference, so the gate prints the line to
    /// paste once the machine has been quiet enough to trust it.
    /// </remarks>
    internal static int Lanes { get; set; }

    /// <summary>The lane axis, when `--lanes N` asked for one.</summary>
    private static Axis LaneAxis(int lanes) => new Axis(
        string.Create(CultureInfo.InvariantCulture, $"full scan, {lanes} lanes"),
        p => Vorticity.Bench.Scenarios.ScenarioSet.ScanAllLanes(p, lanes),
        p => RustReader.Require(
            RustReader.ScanCanonicalThreads(p, lanes), "threaded scan"));

    /// <summary>The axis a shared scenario defines.</summary>
    /// <param name="name">Its `--profile` name.</param>
    private static Axis FromScenario(string name)
    {
        Scenarios.Scenario scenario = Scenarios.ByName(name)
            ?? throw new InvalidOperationException($"No scenario named '{name}'.");
        return new Axis(scenario.Axis, scenario.Ours, scenario.Theirs);
    }

    /// <summary>
    /// The corpus entries whose rewrite is worth watching, and the group that reads them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// EVERY OTHER AXIS READS BYTES THE REFERENCE WROTE, because the conformance corpus is the
    /// reference's output. So the cost of OUR OWN encoding choices appeared in no number at all, and
    /// that gap hid a real one: with zstd disabled, `high_cardinality_i64_r8193` scanned 1.27x
    /// slower from our writer's output than from the reference's -- invisible to every benchmark and
    /// to the written-size ratchet alike.
    /// </para>
    /// <para>
    /// FOUR AXES, BECAUSE TWO RATIOS SEPARATE THE QUESTION. "Our file is slower" can mean the
    /// encoding is genuinely more expensive to decode, or that OUR DECODER is weak on a scheme our
    /// writer happens to like. Read the report as a 2x2: the two `ours` columns are the two readers'
    /// cost on our bytes and on the reference's, the two `rust` columns the same for the reference
    /// implementation. If Rust reads our file as fast as the reference's, the encoding is fine and
    /// the decoder is ours to fix; if Rust slows down too, the writer is choosing a scheme that
    /// costs everyone.
    /// </para>
    /// <para>
    /// This group replaces the `RewrittenComparison` BenchmarkDotNet class, which asked the same
    /// unique question with a worse estimator -- sequential arms cannot share a drift the way these
    /// interleaved ones do -- and asked it against the lazy `ScanAll`, which is A1: a Rust side that
    /// does not decompress is not the counterpart of a .NET scan that has no choice but to.
    /// `ScanCanonical` is what the group is born on.
    /// </para>
    /// <para>
    /// The five-column file because every other axis uses it; the high-cardinality integer file
    /// because it is where the gap was first seen. They are NOT redirected by
    /// `VORTICITY_BENCH_DATA`: the variable points the other axes at a bigger dataset, and this
    /// group is about two specific files whose rewrite has a known history.
    /// </para>
    /// </remarks>
    private static readonly (string Entry, string Label)[] Rewritten =
    [
        ("containers/zoned_many_zones_nulls", "zoned"),
        ("distributions/high_cardinality_i64_r8193", "high card"),
    ];

    /// <summary>
    /// The ratios the rewritten group was measured at, keyed by axis name, in the same order as
    /// <see cref="Rewritten"/>. Kept beside <see cref="Axes"/>'s references and lowered the same way.
    /// </summary>
    /// <summary>
    /// The ratio each axis was measured at when its ceiling was last set. ONE TABLE, for every axis,
    /// because `--recalibrate` prints a replacement for it and a reference split across two places
    /// is a reference that gets updated in one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// LOWER A REFERENCE BY HAND when an improvement lands, in the same commit, and never raise one
    /// without saying in the commit message what got slower and why that is acceptable.
    /// `--recalibrate N` does the measuring: N passes, the max per axis, printed ready to paste.
    /// </para>
    /// <para>
    /// `rewritten zoned, ours` is a ceiling of FIVE, and it is not a typo. The pair says what it was
    /// built to say: our reader takes 990 us on bytes our writer produced against 610 us on the
    /// reference's, while the Rust reader goes the other way, 1 315 us down to 195 us. The encoding
    /// is not the problem -- the reference implementation reads our file nearly seven times faster
    /// than it reads its own -- so this is our decoder on a scheme our writer likes. Written up as a
    /// finding in BENCH-AUDIT.md §5.A.
    /// </para>
    /// </remarks>
    private static readonly Dictionary<string, double> References = new()
    {
        ["full scan"] = 0.462,   // 5 processes, spread 0.453-0.463; held
        ["full scan, upstream lazy"] = 0.463,   // 5 processes, spread 0.456-0.467; held
        ["projected scan, 1 of 5 columns"] = 0.467,   // 5 processes, spread 0.433-0.467; was 0.477
        ["open to first batch"] = 0.086,   // 5 processes, spread 0.083-0.086; held
        ["open, footer only"] = 0.774,   // 5 processes, spread 0.759-0.774; k>1
        ["read and write back"] = 1.027,   // 5 processes, spread 1.003-1.032; held
        ["filtered scan, 1% band"] = 0.238,   // 5 processes, spread 0.206-0.238; k>1
        ["filtered scan, half the rows"] = 0.327,   // 5 processes, spread 0.320-0.327; k>1
        ["scattered take, 64 of 64 splits"] = 0.245,   // 5 processes, spread 0.240-0.247; held
        ["rewritten zoned, reference's"] = 0.457,   // 5 processes, spread 0.454-0.462; held
        ["rewritten zoned, ours"] = 5.189,   // 5 processes, spread 4.789-5.189; k>1
        ["rewritten high card, reference's"] = 0.868,   // 5 processes, spread 0.849-0.868; k>1
        ["rewritten high card, ours"] = 0.927,   // 5 processes, spread 0.893-0.927; held
    };

    /// <summary>
    /// How far under its reference a ratio may sit before it is called stale.
    /// </summary>
    /// <remarks>
    /// TIGHTER THAN `ThroughputCheck`'s 0.70, and the difference is measured rather than chosen.
    /// That gate's references are a max over three runs of encodings whose own run-to-run spread is
    /// 20-25%; these axes are longer and mostly steadier -- +-2% on `full scan` and `scattered take`
    /// against +12 to +22% on the four short ones (BENCH-AUDIT.md annexe A.1). 0.85 is what the
    /// steady axes leave comfortable; the short ones are why it is not tighter still, and why B2 --
    /// deciding on an interval rather than a point -- is what actually fixes this gate's resolution.
    ///
    /// A ratio far BELOW its reference is reported rather than silently accepted: a ratchet that is
    /// never lowered stops being a ratchet. The state this was written to end had five axes of nine
    /// stale at once, `read and write back` by 41%, and nothing said so -- that axis could have
    /// regressed by 75% and still passed.
    /// </remarks>
    private const double StaleBelow = 0.85;

    /// <summary>The field the projection, filter and band axes read; it exists in the dataset.</summary>
    private const string Field = "monotone";

    /// <summary>
    /// The filter band's lower bound, and two widths.
    /// </summary>
    /// <remarks>
    /// The dataset's `monotone` column runs from 1 000 000 upward over 65 536 rows, which is what
    /// `ZoneMapWritingTests` and `FilterSelectivityBenchmarks` both already use. Two widths because
    /// the two ends of the selectivity range answer different questions: a narrow band is about
    /// PRUNING, a wide one is about the comparison kernel, and the plan asked for a ratio on both.
    /// </remarks>
    private const long BandLow = 1_000_000;

    /// <summary>About 1% of the rows: the pruning end.</summary>
    private const long NarrowBand = 656;

    /// <summary>About half the rows: the kernel end.</summary>
    private const long WideBand = 32_768;

    /// <summary>Rows a scattered take asks for, and the gap between them.</summary>
    /// <remarks>
    /// 64 rows one per 1024 is the shape `PathAllocationTests` uses and the shape docs/05 quotes its
    /// "0.32x of a full scan" from: one row from each of the dataset's 64 splits, which is the
    /// worst case for a reader that fetches by split.
    /// </remarks>
    private const long TakeCount = 64;

    /// <summary>The gap between taken rows.</summary>
    private const long TakeStride = 1024;

    /// <summary>How many warm-up iterations each axis got, for the report.</summary>
    private static readonly Dictionary<string, int> Warmed = [];

    /// <summary>Runs the selected axes and reports.</summary>
    /// <param name="only">
    /// Substrings selecting axes by name; empty runs all of them. A kernel change touches one axis
    /// and waiting 29 s for thirteen is the difference between checking it and not bothering --
    /// the same reason `--throughput` takes a family.
    /// </param>
    /// <param name="recalibrate">
    /// When positive, measure that many passes and print a replacement <see cref="References"/>
    /// table instead of gating. The max over the passes, matching `ThroughputCheck`'s convention: a
    /// ratchet set from a mean would go red on half the runs that produced it.
    /// </param>
    /// <returns>0 when every axis is inside its ceiling, 1 when one is not, 2 with no harness.</returns>
    /// <param name="rebase">
    /// Let <c>--recalibrate</c> raise a reference. THE ONE LEGITIMATE USE is an estimator change --
    /// when the harness stops measuring what the old numbers describe, holding them is not a ratchet,
    /// it is a comparison between two different measurements. It is a flag rather than a default so
    /// that a raise is always a deliberate act with a commit message behind it.
    /// </param>
    /// <param name="onePass">
    /// Set by <see cref="PassFlag"/>: measure once and print machine-readable lines for the parent
    /// process that spawned this one. Not a user-facing mode.
    /// </param>
    internal static async Task<int> RunAsync(string[] only, int recalibrate, bool rebase, bool onePass)
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

        List<string> temporary = [];
        try
        {
            bool Selected(string name) => only.Length == 0 ||
                only.Any(o => name.Contains(o, StringComparison.OrdinalIgnoreCase));

            // The rewritten group is BUILT only when it is selected: constructing it writes two
            // files with our own writer, which is a second or two that a `--ratio-check "full scan"`
            // should not pay to then throw away.
            Axis[] axes =
            [
                .. Axes.Where(a => Selected(a.Name)),
                .. RewrittenNames().Any(Selected)
                    ? await RewrittenAxesAsync(temporary, Selected).ConfigureAwait(false)
                    : [],
                .. Lanes > 1 ? new[] { LaneAxis(Lanes) } : [],
            ];
            if (axes.Length == 0)
            {
                Console.Error.WriteLine(
                    $"No axis matches {string.Join(", ", only)}. The axes are:\n  " +
                    string.Join("\n  ", Axes.Select(a => a.Name).Concat(RewrittenNames())));
                return 2;
            }

            return onePass
                ? await PassOnceAsync(path, axes).ConfigureAwait(false)
                : recalibrate > 0
                    ? await RecalibrateAsync(path, axes, recalibrate, rebase).ConfigureAwait(false)
                    : await CheckAsync(path, axes).ConfigureAwait(false);
        }
        finally
        {
            foreach (string file in temporary)
            {
                System.IO.File.Delete(file);
            }
        }
    }

    /// <summary>Measures each axis once and holds it to its ceiling.</summary>
    private static async Task<int> CheckAsync(string path, Axis[] axes)
    {
        Console.Out.WriteLine(
            $"RATIO CHECK: median per-round ratio with a 95% bootstrap interval, {MinRounds}+ " +
            $"interleaved rounds after a " +
            $"{WarmupBudget.TotalSeconds.ToString("F0", CultureInfo.InvariantCulture)}s warm-up, " +
            $"ceiling = reference x {Margin.ToString("F2", CultureInfo.InvariantCulture)}");
        Console.Out.WriteLine(
            "  OVER needs the whole interval above the ceiling; STALE needs it all under " +
            $"{StaleBelow.ToString("F2", CultureInfo.InvariantCulture)} x reference. " +
            "mde = smallest change this axis can currently see.");
        Console.Out.WriteLine(
            "  axis                             ours      rust     ratio  [   95% interval]  reference  ceiling   n  k     mde");

        int over = 0;
        List<string> stale = [];
        List<string> unreferenced = [];
        List<string> blunt = [];
        foreach (Axis axis in axes)
        {
            (Measurement m, int rows) = await MeasureOnceAsync(path, axis).ConfigureAwait(false);
            if (rows != 0)
            {
                return rows;
            }

            Interval ratio = m.Ratio;
            string columns;
            string verdict = string.Empty;
            if (!References.TryGetValue(axis.Name, out double reference))
            {
                unreferenced.Add(string.Create(
                    CultureInfo.InvariantCulture, $"        [\"{axis.Name}\"] = {ratio.Median:F3},"));
                columns = "         --       --";
                verdict = "   NO REF";
            }
            else
            {
                double ceiling = reference * Margin;
                columns = string.Create(
                    CultureInfo.InvariantCulture, $" {reference,10:F3} {ceiling,8:F3}");

                // THE LOWER BOUND DECIDES. A median over the ceiling with an interval straddling it
                // is a coin toss, and failing on it is how this gate produced two false reds in four
                // invocations. Failing only when the whole interval is over means a red is a claim
                // the numbers support.
                if (ratio.Low > ceiling)
                {
                    verdict = "   OVER";
                    over++;
                }
                else if (ratio.High < reference * StaleBelow)
                {
                    verdict = "   STALE";
                    stale.Add(string.Create(
                        CultureInfo.InvariantCulture,
                        $"  {axis.Name}: {ratio.Median:F3} {ratio} against a {reference:F3} reference " +
                        $"({(1 - (ratio.Median / reference)) * 100:F0}% under) -- lower it."));
                }
                else if (ratio.Median > ceiling)
                {
                    // Over on the point estimate, not on the interval: reported so that a developer
                    // who is looking at a real regression is not told everything is fine.
                    verdict = "   noisy";
                    string over_ = string.Create(
                        CultureInfo.InvariantCulture,
                        $"{ratio.Median:F3} is over the {ceiling:F3} ceiling but {ratio} straddles it");
                    blunt.Add(
                        $"  {axis.Name}: {over_} -- indistinguishable from noise at " +
                        $"{ratio.Samples} rounds. Re-run; if it stays over, it is real.");
                }
            }

            // n, k and mde are reported, not asserted. They are what explains an implausible ratio,
            // and a plausible one is not evidence that any of them was good enough.
            Console.Out.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"  {axis.Name,-30} {m.Ours,8:F1}us {m.Theirs,8:F1}us {ratio.Median,8:F3}  {ratio}{columns} " +
                $"{ratio.Samples,3} {m.Repeats,2} {ratio.MinimumDetectableEffect,6:P1}{verdict}"));
        }

        if (axes.Any(a => a.Path is not null))
        {
            Console.Out.WriteLine(
                "  The `rewritten` pairs read as a 2x2 per file: compare the two RATIOS for our " +
                "decoder, the two `rust` columns for the writer's encoding choice.");
        }

        if (unreferenced.Count > 0)
        {
            Console.Out.WriteLine(
                $"\n{unreferenced.Count} axis/axes have no reference. Add these to " +
                "RatioCheck.References, having checked the machine is quiet:");
            unreferenced.ForEach(Console.Out.WriteLine);
        }

        if (blunt.Count > 0)
        {
            Console.Out.WriteLine(
                $"\n{blunt.Count} axis/axes are over the ceiling on the median but not on the " +
                "interval:");
            blunt.ForEach(Console.Out.WriteLine);
        }

        if (stale.Count > 0)
        {
            Console.Out.WriteLine(
                $"\n{stale.Count} axis/axes are STALE -- the code got faster and the ratchet did " +
                "not follow, so they defend nothing:");
            stale.ForEach(Console.Out.WriteLine);
            Console.Out.WriteLine("  Recalibrate with: --ratio-check --recalibrate 3");
        }

        Console.Out.WriteLine(
            over == 0
                ? "Every axis is inside its ceiling."
                : $"{over} axis/axes above ceiling. A ratio only moves when the code moves: " +
                  "find the change, do not raise the ceiling.");
        return over == 0 ? 0 : 1;
    }

    /// <summary>
    /// Measures each axis over several passes and prints a <see cref="References"/> table to paste.
    /// </summary>
    /// <remarks>
    /// STALE says a reference is wrong; this is what makes it right, so that lowering a ratchet is a
    /// command rather than an afternoon. It prints and gates nothing: a recalibration that could
    /// also pass its own gate would be a ratchet setting itself.
    /// </remarks>
    private static async Task<int> RecalibrateAsync(string path, Axis[] axes, int passes, bool rebase)
    {
        Console.Out.WriteLine(
            $"RECALIBRATE: {passes} PROCESSES over {axes.Length} axis/axes. " +
            "Nothing is gated; paste the table below into RatioCheck.References.");
        if (rebase)
        {
            Console.Out.WriteLine(
                "  --rebase: references may RISE. Only for an estimator change, and the commit " +
                "message has to say which one.");
        }

        Dictionary<string, List<double>> ratios = [];
        Dictionary<string, bool> grouped = [];
        for (int pass = 1; pass <= passes; pass++)
        {
            // A PASS IS A PROCESS, and B2.5 is why. Measured over twenty runs of one axis each:
            // between-run variance is two to three times the within-run variance, and a 95%
            // within-run interval contains the grand median 11 or 12 times out of 20 instead of 19.
            // Passes inside one process sample the wrong distribution -- they share a JIT, a heap, a
            // page cache and a thermal state -- so a reference built from them is narrower than the
            // thing it has to survive. Re-running ourselves costs a second of startup per pass and
            // buys the only distribution the gate actually meets.
            if (await PassAsync(axes, pass, passes, ratios, grouped).ConfigureAwait(false) is int bad)
            {
                return bad;
            }
        }

        int held = 0;
        foreach (Axis axis in axes)
        {
            List<double> seen = ratios[axis.Name];
            double max = seen.Max();
            double min = seen.Min();
            bool known = References.TryGetValue(axis.Name, out double current);

            // A RATCHET ONLY EVER COMES DOWN. When the passes peak above the reference the old value
            // is printed back, not the new one: this command exists to lower ceilings that the code
            // outran, and a recalibration that also raised them would launder run-to-run noise into
            // a looser gate -- the one thing BENCH-AUDIT.md §8 forbids outright. If a measurement
            // above the reference is REAL, it is a regression and belongs in the OVER column, not
            // here.
            // --rebase raises ONLY an axis that now groups calls into a round, because k > 1 IS
            // the estimator change: k calls in a row measure a warm path where a single timed call
            // measured a cache-cold one. An axis still timed one call at a time is measuring what it
            // always did, so a higher number there is noise or a regression -- neither of which a
            // rebase may absorb.
            bool changedEstimator = rebase && grouped.GetValueOrDefault(axis.Name);
            bool loosens = known && max >= current && !changedEstimator;
            held += loosens ? 1 : 0;
            double value = loosens ? current : max;
            string movement = !known
                ? "new"
                : changedEstimator && known && max >= current
                    ? string.Create(
                        CultureInfo.InvariantCulture,
                        $"REBASED UP from {current:F3} (k>1): {(max / current) - 1:+0.0%}")
                : loosens
                    ? string.Create(
                        CultureInfo.InvariantCulture,
                        $"HELD at {current:F3}: {passes} passes peaked at {max:F3}, no loosening")
                    : string.Create(
                        CultureInfo.InvariantCulture, $"was {current:F3}, {(max / current) - 1:+0.0%;-0.0%;0.0%}");
            Console.Out.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"        [\"{axis.Name}\"] = {value:F3},   // {passes} passes, spread " +
                $"{min:F3}-{max:F3}; {movement}"));
        }

        if (held > 0)
        {
            Console.Out.WriteLine(
                $"\n{held} axis/axes measured ABOVE their reference and were printed back " +
                "unchanged. Paste the table as it stands: if one of those is a real regression, " +
                "`--ratio-check` says OVER and raising the reference is not the answer.");
        }

        return 0;
    }

    /// <summary>
    /// Asserts the two readers agree on the axis, then times both sides interleaved.
    /// </summary>
    /// <returns>
    /// The two medians in microseconds, and an exit code that is 0 unless the readers disagreed.
    /// </returns>
    private static async Task<(Measurement Result, int Exit)> MeasureOnceAsync(
        string path, Axis axis)
    {
        string file = axis.Path ?? path;

        // PER AXIS, not once for the file. The precondition in RunAsync covers the full scan; the
        // filter and take axes select rows, and two readers that disagree about WHICH rows survive
        // would produce a ratio between two different amounts of work. That is the failure
        // `--ffi-check` was written for, and it only ever checked the full scan.
        long mineRows = await axis.Ours(file).ConfigureAwait(false);
        long rustRows = axis.Theirs(file);
        if (mineRows != rustRows)
        {
            Console.Error.WriteLine(
                $"The two readers disagree on the `{axis.Name}` axis: {mineRows} rows " +
                $"versus {rustRows}. No ratio over it means anything.");
            return (default, 2);
        }

        return (await MeasureAsync(file, axis).ConfigureAwait(false), 0);
    }

    /// <summary>Runs one recalibration pass in a CHILD PROCESS and folds its result in.</summary>
    /// <returns>Null on success, or an exit code.</returns>
    private static async Task<int?> PassAsync(
        Axis[] axes,
        int pass,
        int passes,
        Dictionary<string, List<double>> ratios,
        Dictionary<string, bool> grouped)
    {
        string self = Environment.ProcessPath
            ?? throw new InvalidOperationException("No process path; cannot re-run for a pass.");
        ProcessStartInfo start = new ProcessStartInfo(self)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add("--ratio-check");
        foreach (Axis axis in axes)
        {
            start.ArgumentList.Add(axis.Name);
        }

        start.ArgumentList.Add(PassFlag);

        using Process child = Process.Start(start)
            ?? throw new InvalidOperationException("Could not start a pass.");
        string output = await child.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
        string errors = await child.StandardError.ReadToEndAsync().ConfigureAwait(false);
        await child.WaitForExitAsync().ConfigureAwait(false);
        if (child.ExitCode != 0)
        {
            Console.Error.WriteLine($"Pass {pass} failed ({child.ExitCode}):\n{errors}");
            return child.ExitCode;
        }

        int seen = 0;
        foreach (string line in output.Split('\n'))
        {
            if (!line.StartsWith(PassPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            string[] parts = line[PassPrefix.Length..].Split('\t');
            if (parts.Length != 3 ||
                !double.TryParse(parts[1], CultureInfo.InvariantCulture, out double median) ||
                !int.TryParse(parts[2], CultureInfo.InvariantCulture, out int repeats))
            {
                continue;
            }

            if (!ratios.TryGetValue(parts[0], out List<double>? values))
            {
                values = [];
                ratios[parts[0]] = values;
            }

            // THE MEDIAN, and the interval deliberately NOT folded in. Taking each pass's upper
            // bound instead was tried and rejected: it stacked the within-pass uncertainty on the
            // between-pass spread and then on the x1.15 margin, and twelve of thirteen references
            // rose by 2 to 20 %. Uncertainty is applied ONCE, at the decision.
            values.Add(median);
            grouped[parts[0]] = repeats > 1;
            seen++;
        }

        if (seen == 0)
        {
            Console.Error.WriteLine($"Pass {pass} measured nothing.\n{errors}");
            return 2;
        }

        Console.Out.WriteLine($"  pass {pass} of {passes}: {seen} axis/axes.");
        return null;
    }

    /// <summary>Measures each axis once and prints the machine-readable lines a pass collects.</summary>
    private static async Task<int> PassOnceAsync(string path, Axis[] axes)
    {
        foreach (Axis axis in axes)
        {
            (Measurement m, int rows) = await MeasureOnceAsync(path, axis).ConfigureAwait(false);
            if (rows != 0)
            {
                return rows;
            }

            Console.Out.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"{PassPrefix}{axis.Name}\t{m.Ratio.Median:R}\t{m.Repeats}"));
        }

        return 0;
    }

    /// <summary>The flag that makes a run one recalibration pass. Internal, not documented for users.</summary>
    internal const string PassFlag = "--recalibrate-pass";

    /// <summary>The prefix a pass prints its measurements under.</summary>
    private const string PassPrefix = "PASS\t";

    /// <summary>
    /// Rewrites each entry of <see cref="Rewritten"/> with OUR writer, once, and returns the two
    /// axes that read the pair.
    /// </summary>
    /// <param name="temporary">Collects the files written, for the caller to delete.</param>
    /// <remarks>
    /// The rewrite happens here rather than per round because writing is not what this measures.
    /// Both files are asserted to hold the same rows before either is timed: a rewrite that dropped
    /// a row would turn the pair of ratios into a comparison of two different computations, and it
    /// is the one failure this group can produce that the per-axis row check would not catch --
    /// that check compares the two READERS on one file, not the two FILES.
    /// </remarks>
    private static async Task<List<Axis>> RewrittenAxesAsync(
        List<string> temporary, Func<string, bool> selected)
    {
        List<Axis> axes = [];
        foreach ((string entry, string label) in Rewritten)
        {
            if (!Names(label).Any(selected))
            {
                continue;
            }

            string reference = Corpus.Path(entry);
            string ours = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), $"vorticity-rewritten-{Guid.NewGuid():N}.vortex");
            temporary.Add(ours);
            await Rewrite(reference, ours).ConfigureAwait(false);

            long referenceRows = RustReader.Require(RustReader.ScanCanonical(reference), "reference scan");
            long ourRows = RustReader.Require(RustReader.ScanCanonical(ours), "rewritten scan");
            if (referenceRows != ourRows)
            {
                throw new InvalidOperationException(
                    $"The rewrite of {entry} holds {ourRows} rows against the original's {referenceRows}.");
            }

            foreach ((string suffix, string file) in
                new[] { ("reference's", reference), ("ours", ours) })
            {
                string name = $"rewritten {label}, {suffix}";
                if (!selected(name))
                {
                    continue;
                }

                axes.Add(new Axis(
                    name,
                    ScanAll,
                    p => RustReader.Require(RustReader.ScanCanonical(p), "scan"),
                    file));
            }
        }

        return axes;
    }

    /// <summary>The two axis names of one rewritten entry.</summary>
    private static IEnumerable<string> Names(string label) =>
        [$"rewritten {label}, reference's", $"rewritten {label}, ours"];

    /// <summary>Every axis name the rewritten group would produce.</summary>
    private static IEnumerable<string> RewrittenNames() =>
        Rewritten.SelectMany(r => Names(r.Label));

    /// <summary>Reads a file with our reader and writes it back with our writer.</summary>
    private static async Task Rewrite(string source, string destination)
    {
        await using VortexFile file = await VortexFile.OpenAsync(source, CancellationToken.None);
        await using Vorticity.Writing.VortexFileWriter writer =
            Vorticity.Writing.VortexFileWriter.Create(destination, file.Schema);
        await foreach (RecordBatch batch in file.Scan().ExecuteAsync()
            .WithCancellation(CancellationToken.None))
        {
            await writer.WriteAsync(batch, CancellationToken.None);
        }

        await writer.CompleteAsync(CancellationToken.None);
    }

    /// <summary>
    /// Times both sides of one axis, interleaved, until the ratio's interval is tight enough.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Which side goes first alternates by round. Without that, whichever runs first in every round
    /// pays the cache-cold cost every time and the other never does - a bias that would be stable
    /// across runs, which is worse than noise because it looks like a result.
    /// </para>
    /// <para>
    /// THE RATIO IS PER ROUND, not median over median. The two sides of a round are measured
    /// microseconds apart, so whatever slowed one slowed the other: the pairing is real, and it is
    /// what makes a per-round ratio a sample rather than an arithmetic accident. The median of those
    /// ratios is what the gate holds, and bootstrapping them is what gives it an interval.
    /// </para>
    /// <para>
    /// ROUNDS ARE ADAPTIVE. A long axis reaches 5% half-width in the 21 minimum rounds and stops; a
    /// short one keeps going until it gets there or spends its budget. The old fixed 51 gave the
    /// long axes more than they needed and the short ones less.
    /// </para>
    /// </remarks>
    private static async Task<Measurement> MeasureAsync(string path, Axis axis)
    {
        long deadline = Stopwatch.GetTimestamp() +
            (long)(WarmupBudget.TotalSeconds * Stopwatch.Frequency);
        int warmed = 0;
        double lastOurs = 0;
        double lastTheirs = 0;
        do
        {
            lastOurs = await TimeAsync(axis.Ours, path, 1).ConfigureAwait(false);
            lastTheirs = Time(axis.Theirs, path, 1);
            warmed++;
        }
        while (Stopwatch.GetTimestamp() < deadline);

        Warmed[axis.Name] = warmed;

        // k from the warm-up's own timing, on the slower side: a round must clear the timer's noise
        // floor, and it is the round that is timed, not either call.
        int repeats = Repeats(Math.Max(lastOurs, lastTheirs));

        List<double> ratios = [];
        List<double> mine = [];
        List<double> rust = [];
        long stop = Stopwatch.GetTimestamp() + (long)(Budget * Stopwatch.Frequency);
        Interval interval = default;
        for (int round = 0; round < MaxRounds; round++)
        {
            double ours;
            double theirs;
            if (round % 2 == 0)
            {
                ours = await TimeAsync(axis.Ours, path, repeats).ConfigureAwait(false);
                theirs = Time(axis.Theirs, path, repeats);
            }
            else
            {
                theirs = Time(axis.Theirs, path, repeats);
                ours = await TimeAsync(axis.Ours, path, repeats).ConfigureAwait(false);
            }

            mine.Add(ours);
            rust.Add(theirs);
            ratios.Add(theirs == 0 ? 0 : ours / theirs);

            if (round + 1 >= MinRounds)
            {
                interval = Statistics.Bootstrap(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(ratios));
                if (interval.MinimumDetectableEffect <= Precision ||
                    Stopwatch.GetTimestamp() >= stop)
                {
                    break;
                }
            }
        }

        return new Measurement(Median([.. mine]), Median([.. rust]), interval, repeats);
    }

    /// <summary>One axis measured: both sides, the ratio's interval, and how it was timed.</summary>
    /// <param name="Ours">Median microseconds per round on our side.</param>
    /// <param name="Theirs">Median microseconds per round on Rust's.</param>
    /// <param name="Ratio">The median per-round ratio and its 95% interval.</param>
    /// <param name="Repeats">Calls per timed round.</param>
    private readonly record struct Measurement(
        double Ours, double Theirs, Interval Ratio, int Repeats);

    /// <summary>Calls per timed round, so that one round clears the timer's noise floor.</summary>
    private static int Repeats(double microseconds) =>
        microseconds <= 0 ? 1 : Math.Max(1, (int)Math.Ceiling(MinRoundMicroseconds / microseconds));

    /// <summary>Microseconds per call, timing <paramref name="repeats"/> of them as one round.</summary>
    private static async Task<double> TimeAsync(
        Func<string, Task<long>> work, string path, int repeats)
    {
        long start = Stopwatch.GetTimestamp();
        for (int i = 0; i < repeats; i++)
        {
            await work(path).ConfigureAwait(false);
        }

        return Stopwatch.GetElapsedTime(start).TotalMicroseconds / repeats;
    }

    /// <summary>Microseconds per call, timing <paramref name="repeats"/> of them as one round.</summary>
    private static double Time(Func<string, long> work, string path, int repeats)
    {
        long start = Stopwatch.GetTimestamp();
        for (int i = 0; i < repeats; i++)
        {
            work(path);
        }

        return Stopwatch.GetElapsedTime(start).TotalMicroseconds / repeats;
    }

    private static double Median(double[] samples)
    {
        double[] sorted = (double[])samples.Clone();
        Array.Sort(sorted);
        return sorted[sorted.Length / 2];
    }

    private static Task<long> ScanAll(string path) => Scenarios.ScanAll(path);

    /// <summary>Scans under the same half-open band the Rust side is given.</summary>
    /// <param name="path">The file.</param>
    /// <param name="low">The band's inclusive lower bound.</param>
    /// <param name="width">The band's width.</param>
    /// <remarks>
    /// PRUNING ON, because that is the path a caller gets by default and the one the zone map
    /// exists for. The reference prunes too, from the same zone map, so the two sides are answering
    /// the same question.
    /// </remarks>
    private static Task<long> FilteredScan(string path, long low, long width) =>
        Scenarios.FilteredScan(path, low, width);

    /// <summary>Takes the same strided rows the Rust side is given.</summary>
    /// <param name="path">The file.</param>
    /// <param name="count">How many rows.</param>
    /// <param name="stride">The gap between them.</param>
    private static Task<long> ScatteredTake(string path, long count, long stride) =>
        Scenarios.ScatteredTake(path, count, stride);

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

    /// <summary>
    /// Reads a file and writes it back out, which is the write axis -- the one docs/05 never had a
    /// reference for.
    /// </summary>
    /// <remarks>
    /// The read is on both sides of the ratio and is therefore common-mode, but it is not small:
    /// subtract the `full scan` axis from both before reading the quotient as a statement about
    /// writers. The sink discards, on both sides, because a write benchmark that measures the
    /// filesystem measures the filesystem.
    /// </remarks>
    private static Task<long> ReadAndWrite(string path) => Scenarios.ReadAndWrite(path);

    private static async Task<long> FooterOnly(string path)
    {
        await using VortexFile file = await VortexFile.OpenAsync(path, CancellationToken.None);
        return file.RowCount;
    }
}
