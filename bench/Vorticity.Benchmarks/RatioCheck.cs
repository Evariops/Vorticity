// The ratio gate: our time over Rust's, held to a ceiling, as a command rather than a benchmark.
//
// Some quantities can be locked in CI and some cannot, and the ratio against Rust sits in a
// category of its own. Absolute microseconds depend on the machine, the thermal state and the
// runner, so gating on them produces a test that fails for reasons nobody caused. The RATIO
// does not: both implementations run in the same process on the same bytes with the same clock,
// so a slower machine slows both sides and the quotient survives. It is the only speed
// measurement in this repository that means the same thing on two machines.
//
// WHY NOT A UNIT TEST, WHEN THE ALLOCATION CEILINGS ARE ONE. Allocations are exactly reproducible
// and cost nothing to measure, so they belong in the suite. This needs a 36 MB cdylib that has to be
// built by hand, and it needs seconds of wall clock to say anything. Putting either in `dotnet test`
// would make the suite slower and conditionally red, which is how a suite stops being run.
//
// WHY NOT A BENCHMARKDOTNET ASSERTION. BenchmarkDotNet runs one benchmark to completion and then
// the next, which is precisely the wrong shape, because thermal drift over a long run
// systematically favors whoever goes first. For a ratio that bias is not noise, it is a systematic
// error in the quantity being gated. So the two sides are INTERLEAVED here - one iteration of ours,
// one of theirs, alternating which goes first - and drift becomes common-mode instead of a result.
// The median of each side is taken rather than the mean, so one GC or one scheduler preemption
// moves nothing.
//
// The ceilings are ratchets like every other number in this repository: set just above what was
// measured, lowered by hand when an improvement lands, red on a regression.
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Vorticity.Columns;
using Vorticity.Editions;
using Vorticity.Expressions;
using Vorticity.File;
using Vorticity.Scanning;

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

    /// <summary>Calls per split that decide which of the reference's splits an axis is timed under.</summary>
    private const int SplitRounds = 5;

    /// <summary>Wall clock an axis may spend on rounds before it stops asking for more.</summary>
    private const double Budget = 10.0;

    /// <summary>
    /// A round times at least this long, by repeating the work when one call is shorter.
    /// </summary>
    /// <remarks>
    /// Under ~200 us a single `Stopwatch`-timed call is dominated by timer, cache and scheduler
    /// jitter, and a median over rounds does not recover what no round resolved. Repeating k
    /// times inside one timed round is the same total work with the jitter divided by k. The
    /// estimator changes with it -- k calls in a row measure a WARM path, where a single call
    /// measured a cache-cold one -- which is the path a reader that opens ten files in a row
    /// actually exercises.
    /// </remarks>
    private const double MinRoundMicroseconds = 1_000;

    /// <summary>
    /// The margin over the reference ratio: a ratio more than 15% above its reference fails.
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
    /// These are this harness's own numbers, not BenchmarkDotNet's, and on two of the four axes
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
    /// (Both the full-scan and the projected row are from before those axes were made
    /// like-for-like: the reference side now canonicalizes on each, and the reader side got
    /// faster.)
    ///
    /// The two that agree are the long axis and the trivial one; the two that do not are short
    /// managed paths measured beside a native call an order of magnitude longer. Both estimators
    /// are honest about different things. BenchmarkDotNet reports the best case, a tight loop of
    /// one path with the caches to itself; this reports a path sharing a process with other work,
    /// which is the case a gate should defend. Neither figure should be quoted as the other.
    ///
    /// THE SCAN AXES COME IN PAIRS, AND EACH PAIR MEASURES TWO DIFFERENT QUESTIONS. Upstream's scan
    /// hands back arrays in the file's own encodings and `len()` answers from their metadata, so
    /// nothing is decompressed; the scan this bench runs on our side decodes every column it
    /// delivers but a constant one, which stays one value and a row count. "full scan" and
    /// "projected scan, 1 of 5 columns" therefore drive the Rust side to the same form (`plain` in
    /// `tools/vxbench-rs/src/lib.rs`: canonical, a constant kept), which is the comparison a decoder
    /// ratio has to be built on. "full scan, upstream lazy" and "projected scan, upstream lazy" keep
    /// the counting calls beside them, because "how long to get a stream of arrays you may never
    /// fully read" is a real question about a real API -- it just is not the same question, and
    /// quoting one as the other is what these pairs exist to prevent.
    /// </remarks>
    private static readonly Axis[] Axes =
    [
        // THE FIRST FOUR COME FROM `Scenarios`, which is what makes `--profile fullscan` a
        // statement about THIS axis and not about a scan that resembles it.
        FromScenario("fullscan"),
        new Axis(
            "full scan, upstream lazy",
            ScanAll,
            p => RustReader.Require(RustReader.ScanAll(p), "scan")),
        FromScenario("projected"),
        new Axis(
            "projected scan, upstream lazy",
            Scenarios.ScanProjected,
            p => RustReader.Require(RustReader.ScanProjected(p, Field), "projected scan")),
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
    /// sides -- and it arrives without a reference, so the gate prints the line to paste once the
    /// machine has been quiet enough to trust it.
    /// </remarks>
    internal static int Lanes { get; set; }

    /// <summary>
    /// The benchmark page to write the axes to, as its <c>in-process</c> section, or null; only a
    /// run of every axis writes it, since the section is the whole set.
    /// </summary>
    internal static string? Page { get; set; }

    /// <summary>
    /// The axes the page leaves out: Rust's scan without its decode, and a key-ordered read held to
    /// a take of rows whose positions the reference is given, which ask another question than ours;
    /// and the rewritten pairs, which judge a writer's choice rather than a reader.
    /// </summary>
    private static bool OnThePage(string axis) =>
        !axis.Contains("upstream lazy", StringComparison.Ordinal) &&
        !axis.Contains("against a take", StringComparison.Ordinal) &&
        !axis.StartsWith("rewritten", StringComparison.Ordinal);

    /// <summary>Where the generated tables live, beside the throughput corpus.</summary>
    /// <remarks>
    /// NOT IN THE REPOSITORY, for `ThroughputCheck`'s reason: 106 MB of generated bytes do not
    /// belong in a git history. `bench/gen-throughput.sh` writes them; when they are absent these
    /// axes are SKIPPED with a line saying so, because a gate that cannot run without a cache
    /// directory is a gate that stops being run.
    /// </remarks>
    private static string TableRoot =>
        Environment.GetEnvironmentVariable("VORTICITY_THROUGHPUT_CORPUS") is { Length: > 0 } set
            ? set
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".cache", "vorticity", "throughput-1M");

    /// <summary>The column `projected scan, 1 of 50 columns` keeps.</summary>
    private const string WideField = "c07";

    /// <summary>
    /// The axes that read the generated tables, or nothing when they have not been generated.
    /// </summary>
    /// <remarks>
    /// Every other axis reads ONE file -- 65 536 rows, five columns -- so the projection measured
    /// "1 column of 5" where the benchmark design calls for "1 of 50", and no axis had a string
    /// column at all. `table_mixed` is what a reader actually meets (a key, a measure, a
    /// price, sixteen labels, a million distinct names, a timestamp) and `table_wide` is the fifty
    /// columns the projection sentence was written about.
    /// </remarks>
    private static Axis[] TableAxes()
    {
        string mixed = Path.Combine(TableRoot, "table_mixed.vortex");
        string wide = Path.Combine(TableRoot, "table_wide.vortex");
        if (!System.IO.File.Exists(mixed) || !System.IO.File.Exists(wide))
        {
            return [];
        }

        return
        [
            new Axis(
                "full scan, 1M table",
                Scenarios.ScanAll,
                p => RustReader.Require(RustReader.ScanCanonical(p), "scan"),
                mixed),
            new Axis(
                "projected scan, 1 of 50 columns",
                p => Vorticity.Bench.Scenarios.ScenarioSet.ScanProjectedField(p, WideField),
                p => RustReader.Require(
                    RustReader.ScanProjectedCanonical(p, WideField), "projected scan"),
                wide),
        ];
    }

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
    /// interleaved ones do -- and asked it against the lazy `ScanAll`: a Rust side that does not
    /// decompress is not the counterpart of a .NET scan that has no choice but to.
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
    /// <summary>A ratchet: the ratio an axis was measured at, and the k it was measured with.</summary>
    /// <param name="Ratio">The ratio the ceiling is built on.</param>
    /// <param name="Repeats">
    /// Calls per timed round at the time. IT IS PART OF THE REFERENCE, not a note about it: k
    /// decides whether the number describes a cache-cold call or a warm one, and two ratios measured
    /// at different k are not the same quantity. Recording it is what lets `--rebase` tell an
    /// estimator change from a regression -- see `RecalibrateAsync`.
    /// </param>
    /// <param name="Spread">
    /// How far apart the calibration passes fell, as a fraction of <paramref name="Ratio"/>, or zero
    /// when the entry predates the field.
    /// </param>
    /// <remarks>
    /// IT IS WHAT THE CEILING IS BUILT FROM, not a note about it. A flat margin over every axis says
    /// the long ones and the short ones resolve the same change, and they do not: the steady axes
    /// sit inside a couple of per cent between runs while the short ones wander by twenty. A ceiling
    /// tighter than an axis can resolve does not catch a regression, it reports one at random, and
    /// then the gate stops being believed -- which is worse than a wide ceiling, because a wide
    /// ceiling at least says honestly how much this axis can see.
    ///
    /// Between passes rather than within one: a pass is a process, and the within-run interval is
    /// two to three times narrower than the distribution the gate actually meets.
    /// </remarks>
    private readonly record struct Reference(double Ratio, int Repeats, double Spread = 0);

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
    /// The reference side decodes everything it is asked for into the form our reader delivers: its
    /// entry points that read values (scan, threaded scan, first batch, take, filtered scans) take
    /// every split to `plain`, `execute::&lt;RecursiveCanonical&gt;` with a constant column kept as
    /// one value, because `execute::&lt;Canonical&gt;` stops as soon as the root array is one of
    /// twelve canonical kinds -- `Struct`, `Map`, `ListView` and `Variant` among them -- and on a
    /// tabular file that is before a single column has been decoded. Both sides read a mapped file
    /// where it lies, mapped anew at every call. `tools/vxbench-rs/src/lib.rs` carries the argument.
    /// </para>
    /// <para>
    /// EVERY LINE WAS REBASED ON 2026-10-02, and the code had not moved: the harness had. Until then
    /// the reference read its file with `open_path`, copying every segment it needed, while our
    /// side read a mapping a previous round had left it; the reference expanded every constant
    /// column, which our side keeps as one value; its writer wrote into a `Vec&lt;u8&gt;` while ours
    /// counted bytes; the count axis decoded every column of every row kept, and the uncorrelated
    /// key-order axis was given the positions of its rows; and every axis held it to its default
    /// split, which on the million-row table decoded the one chunk ten times. Each was a cost on
    /// Rust's side alone. The table was set with `--recalibrate 5 --rebase` under the corrected
    /// harness, and each note says what the line was on the old one: `projected scan, 1 of 50
    /// columns` rose from 0.071 to 0.757 and `full scan, 1M table` from 0.037 to 0.331, which is
    /// what the old harness was worth on them, the second through the split the reference was held
    /// to (`RustReader.FasterSplit`). Five passes, not three: two axes read in two modes, and three
    /// passes had seen only the lower one.
    /// </para>
    /// <para>
    /// A line is replaced only when its ceiling does not rise. `--recalibrate` never raises a
    /// reference, but it reprints the dispersion it measured, and on a held line a wider dispersion
    /// is a wider ceiling: that line keeps the calibration it had.
    /// </para>
    /// <para>
    /// `rewritten zoned, ours` WAS a ceiling of FIVE, and that entry is the clearest casualty. Its
    /// note used to read: "our reader takes 990 us on bytes our writer produced against 610 us on
    /// the reference's, while the Rust reader goes the other way, 1 315 us down to 195 us -- the
    /// reference implementation reads our file nearly seven times faster than it reads its own".
    /// It did not: those 195 us were an open and a split of three chunks, against 64 in the
    /// reference's own file, which is why the number tracked the SPLIT COUNT and not the encoding.
    /// Against a Rust that decodes, the axis reads 1.08, so a conclusion drawn from the old number
    /// has to be checked again.
    /// </para>
    /// </remarks>
    private static readonly Dictionary<string, Reference> References = new()
    {
        ["full scan"] = new(0.318, 2, 0.047),   // 5 passes, spread 0.304-0.319; was 0.312 on the old harness, +1.9%; held at a three-pass calibration of this binary, the five peaking at 0.319
        ["full scan, upstream lazy"] = new(0.477, 3, 0.017),   // 5 passes, spread 0.469-0.477; was 0.438 on the old harness, +8.9%
        ["projected scan, 1 of 5 columns"] = new(0.509, 11, 0.127),   // 5 passes, spread 0.444-0.509; was 0.409 on the old harness, +24.4%
        ["projected scan, upstream lazy"] = new(0.542, 12, 0.022),   // 5 passes, spread 0.531-0.542; was 0.457 on the old harness, +18.6%
        ["open to first batch"] = new(0.071, 10, 0.052),   // 5 passes, spread 0.068-0.072; was 0.084 on the old harness, -15.5%; held at a three-pass calibration of this binary, the five peaking at 0.072
        ["open, footer only"] = new(0.632, 33, 0.029),   // 5 passes, spread 0.614-0.632; was 0.622 on the old harness, +1.6%
        ["read and write back"] = new(0.341, 1, 0.046),   // 5 passes, spread 0.326-0.341; was 0.321 on the old harness, +6.2%
        ["filtered scan, 1% band"] = new(0.309, 11, 0.196),   // 5 passes, spread 0.248-0.309; was 0.211 on the old harness, +46.4%
        ["filtered scan, half the rows"] = new(0.300, 7, 0.060),   // 5 passes, spread 0.282-0.300; was 0.259 on the old harness, +15.8%
        ["scattered take, 64 of 64 splits"] = new(0.227, 4, 0.020),   // 5 passes, spread 0.223-0.227; was 0.205 on the old harness, +10.7%
        ["rewritten zoned, reference's"] = new(0.319, 2, 0.016),   // 5 passes, spread 0.316-0.321; was 0.313 on the old harness, +1.9%; held at a three-pass calibration of this binary, the five peaking at 0.321
        ["rewritten zoned, ours"] = new(0.924, 2, 0.031),   // 5 passes, spread 0.910-0.939; was 0.914 on the old harness, +1.1%; held at a three-pass calibration of this binary, the five peaking at 0.939
        ["rewritten high card, reference's"] = new(0.959, 27, 0.025),   // 5 passes, spread 0.936-0.959; was 0.762 on the old harness, +25.9%
        ["rewritten high card, ours"] = new(0.994, 23, 0.027),   // 5 passes, spread 0.968-0.994; was 0.842 on the old harness, +18.1%
        ["key order, sorted column, 1% band"] = new(0.795, 12, 0.025),   // 5 passes, spread 0.775-0.795; was 0.718 on the old harness, +10.7%
        ["key order, uncorrelated, 64 rows"] = new(0.880, 6, 0.072),   // 5 passes, spread 0.850-0.914; was 1.125 on the old harness, -21.8%; held at a three-pass calibration of this binary, the five peaking at 0.914
        ["count, exact cover, 1% band"] = new(0.965, 9, 0.141),   // 5 passes, spread 0.828-0.965; was 0.627 on the old harness, +53.9%
        ["key order, uncorrelated, against a take"] = new(1.381, 9, 0.133),   // 5 passes, spread 1.197-1.381; new
        ["filtered scan, string equality, fsst"] = new(0.745, 4, 0.187),   // 5 passes, spread 0.605-0.745; was 0.852 on the old harness, -12.6%
        ["filtered scan, string prefix, fsst"] = new(1.078, 2, 0.015),   // 5 passes, spread 1.138-1.153; was 1.237 on the old harness, -12.9%; held at a three-pass calibration of this binary, the five peaking at 1.153
        ["filtered scan, string equality, dict"] = new(0.943, 11, 0.059),   // 5 passes, spread 0.888-0.943; was 1.908 on the old harness, -50.6%
        ["filtered scan, string prefix, dict"] = new(0.943, 9, 0.030),   // 5 passes, spread 0.915-0.943; was 1.365 on the old harness, -30.9%
        ["filtered scan, band, runend"] = new(1.017, 14, 0.021),   // 5 passes, spread 0.996-1.017; was 0.968 on the old harness, +5.1%
        ["filtered scan, band, bitpacked"] = new(1.074, 13, 0.079),   // 5 passes, spread 0.989-1.074; was 0.924 on the old harness, +16.2%
        ["full scan, 1M table"] = new(0.331, 1, 0.081),   // 5 passes, spread 0.304-0.331; was 0.037 on the old harness, +794.6%
        ["projected scan, 1 of 50 columns"] = new(0.757, 20, 0.061),   // 5 passes, spread 0.710-0.757; was 0.071 on the old harness, +966.2%
    };

    /// <summary>
    /// The reference binary <see cref="References"/> was calibrated against, or null when none was
    /// recorded. <see cref="RustReader.Fingerprint"/> names the one a run loaded.
    /// </summary>
    /// <remarks>
    /// A ratio measured through another binary is a number about the rebuild, so the check refuses
    /// to gate on one; and a recalibration under another binary may raise a reference with
    /// <c>--rebase</c>, because the denominator changed, as a new k changes it.
    /// </remarks>
    private static readonly string? CalibratedShim = "49dacfd9920c";

    /// <summary>
    /// How far under its reference a ratio may sit before it is called stale.
    /// </summary>
    /// <remarks>
    /// TIGHTER THAN `ThroughputCheck`'s 0.70, and the difference is measured rather than chosen.
    /// That gate's references are a max over three runs of encodings whose own run-to-run spread is
    /// 20-25%; these axes are longer and mostly steadier -- +-2% on `full scan` and `scattered take`
    /// against +12 to +22% on the four short ones. 0.85 is what the steady axes leave comfortable;
    /// the short ones are why it is not tighter still, and why deciding on an interval rather than
    /// a point is what actually fixes this gate's resolution.
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
    /// 64 rows one per 1024 is the shape `PathAllocationTests` uses and the shape the "0.32x of a
    /// full scan" figure for a take was measured on: one row from each of the dataset's 64 splits,
    /// which is the worst case for a reader that fetches by split.
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
    /// Let <c>--recalibrate</c> raise a reference. The one legitimate use is an estimator change, a
    /// new k or a new reference binary: when the harness stops measuring what the old numbers
    /// describe, holding them is not a ratchet, it is a comparison between two different
    /// measurements. It is a flag rather than a default so that a raise is always a deliberate act
    /// with a commit message behind it.
    /// </param>
    /// <param name="onePass">
    /// Set by <see cref="PassFlag"/>: measure once and print machine-readable lines for the parent
    /// process that spawned this one. Not a user-facing mode.
    /// </param>
    /// <param name="rebaseFrom">
    /// With <paramref name="recalibrate"/>, the reference binary the table was set under: every pass
    /// runs under it and under the running one, and each reference moves by what the binary moved
    /// its ratio, our code being the same in both. A regression of ours that the table holds stays
    /// in it, which a rebase to the running measurement would absorb.
    /// </param>
    internal static async Task<int> RunAsync(
        string[] only, int recalibrate, bool rebase, bool abSame, bool onePass, string? rebaseFrom = null)
    {
        if (!RustReader.Available)
        {
            Console.Error.WriteLine(
                $"vxbench not found (looked for {RustReader.ExpectedPath}).\n" +
                "Build it with: cd tools/vxbench-rs && cargo build --release");
            return 2;
        }

        string path = Corpus.Dataset("VORTICITY_BENCH_DATA", "containers/zoned_many_zones_nulls");

        // THE GATE REFUSES A FILE OF ANOTHER SHAPE rather than deriving one.
        // The classes that only report can adapt to whatever file they are given; these thirteen
        // references cannot -- they are ratchets measured against this file's columns, its zones
        // and its splits, so the same numbers over another file would be a comparison with nothing.
        // The band's own constants are checked by using them: `monotone` has to exist and be an
        // integer, and the axes read exactly the values this file has. A gate says why and exits
        // rather than throwing a stack trace at a script.
        try
        {
            Corpus.RequireIntegerColumn(path, Scenarios.Field, "VORTICITY_BENCH_DATA");
        }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException
            or VortexFormatException or VortexUnsupportedException)
        {
            Console.Error.WriteLine(
                $"{e.Message}\nThe references are ratchets against this file's columns, zones and " +
                "splits; the same numbers over another file would be a comparison with nothing.");
            return 2;
        }

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

        if (Page is not null && (only.Length > 0 || recalibrate > 0 || onePass))
        {
            Console.Error.WriteLine("The page takes a check of every axis; it is not written by this run.");
            Page = null;
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
                .. KeyOrderNames.Any(Selected)
                    ? await KeyOrderAxesAsync(temporary, Selected).ConfigureAwait(false)
                    : [],
                .. StringPredicateNames.Any(Selected)
                    ? await StringPredicateAxesAsync(temporary, Selected).ConfigureAwait(false)
                    : [],
                .. RunEndPredicateNames.Any(Selected)
                    ? await RunEndPredicateAxesAsync(temporary, Selected).ConfigureAwait(false)
                    : [],
                .. Lanes > 1 ? new[] { LaneAxis(Lanes) } : [],
                .. TableAxes().Where(a => Selected(a.Name)),
            ];
            if (axes.Length == 0)
            {
                Console.Error.WriteLine(
                    $"No axis matches {string.Join(", ", only)}. The axes are:\n  " +
                    string.Join("\n  ", Axes.Select(a => a.Name).Concat(RewrittenNames())
                        .Concat(KeyOrderNames).Concat(StringPredicateNames)
                        .Concat(RunEndPredicateNames)));
                return 2;
            }

            return onePass
                ? await PassOnceAsync(path, axes).ConfigureAwait(false)
                : recalibrate > 0 && rebaseFrom is not null
                    ? await CarryAsync(axes, recalibrate, rebaseFrom).ConfigureAwait(false)
                : recalibrate > 0
                    ? await RecalibrateAsync(path, axes, recalibrate, rebase, abSame).ConfigureAwait(false)
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
        // Every ratio below has this binary as its denominator, and a rebuild can move it: the crate
        // is one codegen unit, so a function no axis calls can still change the inlining of one
        // they do.
        string running = RustReader.Fingerprint ?? "unknown";
        Console.Out.WriteLine(CalibratedShim is null
            ? $"  reference binary: {running}; the references record none"
            : string.Equals(running, CalibratedShim, StringComparison.Ordinal)
                ? $"  reference binary: {running}, the one the references were calibrated against"
                : $"  reference binary: {running}, but the references were calibrated against {CalibratedShim}");
        if (CalibratedShim is not null && !string.Equals(running, CalibratedShim, StringComparison.Ordinal))
        {
            Console.Error.WriteLine(
                $"REFUSED: the references were calibrated against reference binary {CalibratedShim} and " +
                $"this process loaded {running}. Recalibrate under the one in the tree " +
                "(`--ratio-check --recalibrate 3 --rebase`) and record its fingerprint, or restore the " +
                "binary the references name.");
            return 2;
        }

        Console.Out.WriteLine(
            "  axis                             ours      rust     ratio  [   95% interval]  reference  ceiling   n  k     mde");

        int over = 0;
        List<string> stale = [];
        List<string> unreferenced = [];
        List<string> blunt = [];
        List<(string Axis, Measurement Measured)> measured = [];
        foreach (Axis axis in axes)
        {
            (Measurement m, int rows) = await MeasureOnceAsync(path, axis).ConfigureAwait(false);
            if (rows != 0)
            {
                return rows;
            }

            measured.Add((axis.Name, m));
            Interval ratio = m.Ratio;
            string columns;
            string verdict = string.Empty;
            if (!References.TryGetValue(axis.Name, out Reference entry))
            {
                unreferenced.Add(string.Create(
                    CultureInfo.InvariantCulture,
                    $"        [\"{axis.Name}\"] = new({ratio.Median:F3}, {m.Repeats}),"));
                columns = "         --       --";
                verdict = "   NO REF";
            }
            else
            {
                double reference = entry.Ratio;
                double ceiling = reference * (1 + Math.Max(Margin - 1, entry.Spread));
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
        if (Page is not null)
        {
            await ResultsPage.WriteAsync(Page, [("in-process", Section(measured, running))]).ConfigureAwait(false);
        }

        return over == 0 ? 0 : 1;
    }

    /// <summary>The axes that compare one reader with the other, for the page's <c>in-process</c> section.</summary>
    /// <param name="measured">Every axis of the run, in its order.</param>
    /// <param name="binary">The reference binary's fingerprint.</param>
    private static string Section(List<(string Axis, Measurement Measured)> measured, string binary)
    {
        List<(string Axis, Measurement Measured)> shown = [.. measured.Where(m => OnThePage(m.Axis))];
        StringBuilder text = new StringBuilder();
        text.AppendLine("## In one process, after warm-up");
        text.AppendLine();
        text.AppendLine("Both readers called in turn in one process, Rust's through a C ABI, against one clock: at least");
        text.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"{MinRounds} rounds after a {WarmupBudget.TotalSeconds:F0}-second warm-up, until the 95 % interval of the per-round ratios is within"));
        text.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"{Precision:P0} of their median. Our side runs on the JIT, warmed. Each call opens its file and maps it anew, on"));
        text.AppendLine("both sides, and asks both the same question. Each axis is one the `--ratio-check` gate holds to a");
        text.AppendLine("ceiling; what each one reads and asks is in [05-benchmarks.md](../design/05-benchmarks.md) §3.");
        text.AppendLine();
        text.AppendLine("| axis | Vorticity, µs | Vortex Rust, µs | ratio | 95 % interval |");
        text.AppendLine("|---|---:|---:|---:|---|");
        foreach ((string axis, Measurement m) in shown)
        {
            text.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"| {axis} | {m.Ours:N1} | {m.Theirs:N1} | {m.Ratio.Median:F2}x | {m.Ratio.Low:F3} to {m.Ratio.High:F3} |"));
        }

        text.AppendLine();
        List<(string Axis, Measurement Measured)> behind = [.. shown.Where(m => m.Measured.Ratio.Median >= 1).OrderByDescending(m => m.Measured.Ratio.Median)];
        text.Append(string.Create(CultureInfo.InvariantCulture,
            $"Vorticity took less time on {shown.Count - behind.Count} of {shown.Count} axes."));
        text.AppendLine(behind.Count == 0
            ? string.Empty
            : string.Create(CultureInfo.InvariantCulture,
                $" At 1.00x or above: {string.Join(", ", behind.Select(b => $"{b.Axis} ({b.Measured.Ratio.Median:F2}x)"))}."));
        List<string> perChunk = [.. shown.Where(m => m.Measured.Split == RustReader.SplitPerChunk).Select(m => m.Axis)];
        List<string> byDefault = [.. shown.Where(m => m.Measured.Split != RustReader.SplitPerChunk).Select(m => m.Axis)];
        text.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"Rust is timed under the faster of its two splits, axis by axis: one split per chunk on {perChunk.Count} of {shown.Count}, its default on {Report.Fewer(perChunk, byDefault, "; ")}."));
        text.AppendLine();
        text.AppendLine(ResultsPage.Provenance($"Vortex 0.86.1 through the C ABI of `tools/vxbench-rs`, binary {binary}"));
        return text.ToString();
    }

    /// <summary>
    /// Measures each axis over several passes and prints a <see cref="References"/> table to paste.
    /// </summary>
    /// <remarks>
    /// STALE says a reference is wrong; this is what makes it right, so that lowering a ratchet is a
    /// command rather than an afternoon. It prints and gates nothing: a recalibration that could
    /// also pass its own gate would be a ratchet setting itself.
    /// </remarks>
    private static async Task<int> RecalibrateAsync(
        string path, Axis[] axes, int passes, bool rebase, bool abSame)
    {
        Console.Out.WriteLine(
            $"RECALIBRATE: {passes} PROCESSES over {axes.Length} axis/axes. " +
            "Nothing is gated; paste the table below into RatioCheck.References.");
        if (rebase)
        {
            Console.Out.WriteLine(
                "  --rebase: references may RISE. Only for an estimator change, a new k or a new " +
                "reference binary, or with --ab-same for a measurement that moved under code that did not.");
        }

        if (abSame)
        {
            Console.Out.WriteLine(
                "  --ab-same: asserting bench/ab.sh reports `same` for these axes between the " +
                "commit that set the reference and this one. The commit message has to quote it.");
        }

        Dictionary<string, List<double>> ratios = [];
        Dictionary<string, int> grouped = [];
        for (int pass = 1; pass <= passes; pass++)
        {
            // A PASS IS A PROCESS. Measured over twenty runs of one axis each:
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

        // A reference binary other than the one the table names is a new denominator under every
        // axis, which a rebase may follow as it follows a new k.
        bool newBinary = !string.Equals(RustReader.Fingerprint, CalibratedShim, StringComparison.Ordinal);
        int held = 0;
        foreach (Axis axis in axes)
        {
            List<double> seen = ratios[axis.Name];
            double max = seen.Max();
            double min = seen.Min();
            bool known = References.TryGetValue(axis.Name, out Reference entry);
            double current = entry.Ratio;
            int repeats = grouped.GetValueOrDefault(axis.Name, 1);

            // A RATCHET ONLY EVER COMES DOWN. When the passes peak above the reference the old value
            // is printed back, not the new one: this command exists to lower ceilings that the code
            // outran, and a recalibration that also raised them would launder run-to-run noise into
            // a looser gate -- the one thing a ratchet must never do. If a measurement above the
            // reference is REAL, it is a regression and belongs in the OVER column, not here.
            // --rebase raises ONLY an axis whose k HAS MOVED SINCE ITS REFERENCE WAS SET, because
            // that move IS the estimator change: k calls in a row measure a warm path where a
            // single timed call measured a cache-cold one, and the two are not the same quantity.
            // An axis measured at the same k as its reference is measuring exactly what it always
            // did, so a higher number there is noise or a regression -- neither of which a rebase
            // may absorb. The test was `k > 1` until k came from the faster side, which made k > 1
            // the ordinary case on nearly every axis, at which point it stopped discriminating and
            // started rubber-stamping.
            bool changedEstimator = rebase && known && (repeats != entry.Repeats || newBinary);

            // THE SECOND WAY UP, and the only objective one. A measurement can move while the code
            // stands still -- a heavier neighbour in the same process, a machine that is not the one
            // the reference was set on -- and a rule that never lets a reference rise turns that into
            // a gate that is red forever and therefore believed by nobody. `ab.sh` is what tells the
            // two apart: it runs the commit that set the reference and this one against one clock,
            // so `same` there means the code did not move and the measurement did. Asserting it is
            // the caller's act, and the commit message carries the figures.
            bool attested = rebase && abSame && known && max >= current;
            bool loosens = known && max >= current && !changedEstimator && !attested;
            held += loosens ? 1 : 0;
            double value = loosens ? current : max;
            string movement = !known
                ? "new"
                : changedEstimator && known && max >= current
                    ? string.Create(
                        CultureInfo.InvariantCulture,
                        $"REBASED UP from {current:F3} ({(repeats != entry.Repeats ? $"k {entry.Repeats}->{repeats}" : "a new reference binary")}): " +
                        $"{(max / current) - 1:+0.0%}")
                : attested
                    ? string.Create(
                        CultureInfo.InvariantCulture,
                        $"RAISED from {current:F3} on an A/B that reports no change in our own " +
                        $"time: {(max / current) - 1:+0.0%}")
                : loosens
                    ? string.Create(
                        CultureInfo.InvariantCulture,
                        $"HELD at {current:F3}: {passes} passes peaked at {max:F3}, no loosening")
                    : string.Create(
                        CultureInfo.InvariantCulture, $"was {current:F3}, {(max / current) - 1:+0.0%;-0.0%;0.0%}");
            // The spread the passes actually showed, as a fraction of the value the ceiling is
            // built on. It is emitted rather than described, because a number in a comment cannot
            // widen a ceiling and this axis's own dispersion is the only honest thing to widen it by.
            double spread = value > 0 ? (max - min) / value : 0;
            Console.Out.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"        [\"{axis.Name}\"] = new({value:F3}, {repeats}, {spread:F3}),   " +
                $"// {passes} passes, spread {min:F3}-{max:F3}; {movement}"));
        }

        // The table and the binary it was measured through belong to the same paste.
        Console.Out.WriteLine(
            $"\nPaste alongside it, into RatioCheck.CalibratedShim:\n" +
            $"    private static readonly string? CalibratedShim = \"{RustReader.Fingerprint ?? "unknown"}\";");
        if (held > 0)
        {
            Console.Out.WriteLine(
                $"\n{held} axis/axes measured ABOVE their reference and were printed back " +
                "unchanged. Paste the table as it stands: if one of those is a real regression, " +
                "`--ratio-check` says OVER and raising the reference is not the answer.");
            Console.Out.WriteLine(
                "  If it is not a regression, prove it rather than assert it: run\n" +
                "    bash bench/ab.sh <the commit that set the reference> --after HEAD <file> " +
                "<scenario>\n" +
                "  and, only if it reports `same`, recalibrate again with --rebase --ab-same.");
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

    /// <summary>
    /// Carries every reference over from the binary at <paramref name="from"/> to the running one,
    /// and prints the table to paste.
    /// </summary>
    /// <remarks>
    /// Each pass is a pair of processes, one under each binary, the first of them alternating, and
    /// each reference is multiplied by the running binary's peak ratio over the old one's. Both run
    /// the same build of ours, so the factor is the binary's alone, and whatever the table held
    /// against our code, an axis over its ceiling or one stale under it, it still holds.
    /// </remarks>
    private static async Task<int> CarryAsync(Axis[] axes, int passes, string from)
    {
        if (RustReader.FingerprintOf(from) is not { } old)
        {
            Console.Error.WriteLine($"--rebase-from: no reference binary at {from}.");
            return 2;
        }

        Console.Out.WriteLine(
            $"CARRY: {passes} pairs of processes over {axes.Length} axis/axes, under {old} and under the " +
            $"running {RustReader.Fingerprint ?? "unknown"}. Nothing is gated; paste the table below " +
            "into RatioCheck.References.");
        Dictionary<string, List<double>> before = [];
        Dictionary<string, List<double>> after = [];
        Dictionary<string, int> groupedBefore = [];
        Dictionary<string, int> grouped = [];
        for (int pass = 1; pass <= passes; pass++)
        {
            bool oldFirst = pass % 2 == 1;
            foreach (bool underOld in (bool[])[oldFirst, !oldFirst])
            {
                if (await PassAsync(
                        axes, pass, passes, underOld ? before : after, underOld ? groupedBefore : grouped,
                        underOld ? from : null).ConfigureAwait(false) is int bad)
                {
                    return bad;
                }
            }
        }

        foreach (Axis axis in axes)
        {
            List<double> seen = after[axis.Name];
            double max = seen.Max();
            double min = seen.Min();
            double was = before[axis.Name].Max();
            int repeats = grouped.GetValueOrDefault(axis.Name, 1);
            bool known = References.TryGetValue(axis.Name, out Reference entry);
            double factor = was > 0 ? max / was : 1;
            double value = known ? entry.Ratio * factor : max;
            double spread = max > 0 ? (max - min) / max : 0;
            string movement = known
                ? string.Create(
                    CultureInfo.InvariantCulture,
                    $"carried from {entry.Ratio:F3}, {factor - 1:+0.0%;-0.0%;0.0%} under the new binary " +
                    $"({was:F3} -> {max:F3}, k {groupedBefore.GetValueOrDefault(axis.Name, 1)} -> {repeats})")
                : "new";
            Console.Out.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"        [\"{axis.Name}\"] = new({value:F3}, {repeats}, {spread:F3}),   " +
                $"// {passes} passes, spread {min:F3}-{max:F3}; {movement}"));
        }

        Console.Out.WriteLine(
            $"\nPaste alongside it, into RatioCheck.CalibratedShim:\n" +
            $"    private static readonly string? CalibratedShim = \"{RustReader.Fingerprint ?? "unknown"}\";");
        return 0;
    }

    /// <summary>Runs one recalibration pass in a CHILD PROCESS and folds its result in.</summary>
    /// <param name="binary">The reference binary the pass loads, or null for the running one.</param>
    /// <returns>Null on success, or an exit code.</returns>
    private static async Task<int?> PassAsync(
        Axis[] axes,
        int pass,
        int passes,
        Dictionary<string, List<double>> ratios,
        Dictionary<string, int> grouped,
        string? binary = null)
    {
        string self = Environment.ProcessPath
            ?? throw new InvalidOperationException("No process path; cannot re-run for a pass.");
        ProcessStartInfo start = new ProcessStartInfo(self)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        if (binary is not null)
        {
            start.Environment["VORTICITY_VXBENCH"] = binary;
        }

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

            // The LAST pass's k, and the passes agree: k comes from a two-second warm-up of the
            // same work on the same machine, so a pass that disagreed would be reporting a machine
            // that changed under it, which the spread would show first.
            grouped[parts[0]] = repeats;
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

    /// <summary>The key-order group's axes, in the order they are reported.</summary>
    /// <remarks>
    /// <para>
    /// The index reads: `InKeyOrder` over a sorted column and over an uncorrelated one, and a count
    /// answered by the exact cover. The Rust side has no key order and no index, so each of the
    /// first three axes is held against the reference's answer to the same QUESTION of the file --
    /// the rows of the band, or their count -- which is the cost a caller would otherwise pay. The
    /// reference returns the band's rows in file order; on the sorted column that is key order, and
    /// on the uncorrelated one sorting its 64 rows would add a microsecond.
    /// </para>
    /// <para>
    /// THE FIRST VERSION ASKED THE REFERENCE EASIER OR HARDER QUESTIONS, and the page published
    /// them as like for like. The uncorrelated axis gave it the positions of 64 rows to take, one
    /// per split, which a key-ordered read has to find first; the count gave it a filtered scan
    /// that decoded every column of every row kept, which a count does not ask for. The take is
    /// kept, as the fourth axis, for what it is: the floor a key-ordered walk over scattered rows
    /// cannot go under, a gate on our walk and not a comparison, so the page leaves it out.
    /// </para>
    /// <para>
    /// `full scan` stays the guard that the window driver slows nothing else.
    /// </para>
    /// </remarks>
    private static readonly string[] KeyOrderNames =
    [
        "key order, sorted column, 1% band",
        "key order, uncorrelated, 64 rows",
        "count, exact cover, 1% band",
        "key order, uncorrelated, against a take",
    ];

    /// <summary>Rows of the key-order file: 64 splits of 1 024, the scattered take's shape.</summary>
    private const int KeyOrderRows = 65_536;

    /// <summary>The key-order file's sorted column, its uncorrelated one, and a payload.</summary>
    private const string SortedField = "sorted";

    private const string ShuffledField = "shuffled";

    /// <summary>
    /// An odd multiplier, so `row * it mod 65 536` is a permutation: a band of 64 keys lands on 64
    /// rows scattered over the file.
    /// </summary>
    private const long Shuffle = 40_503;

    /// <summary>Writes the key-order file once, with our writer, and returns the group's axes.</summary>
    /// <param name="temporary">Collects the file written, for the caller to delete.</param>
    /// <param name="selected">Which axes the caller asked for.</param>
    private static async Task<List<Axis>> KeyOrderAxesAsync(List<string> temporary, Func<string, bool> selected)
    {
        string path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"vorticity-keyorder-{Guid.NewGuid():N}.vortex");
        temporary.Add(path);
        await WriteKeyOrderFileAsync(path).ConfigureAwait(false);

        const long low = 20_000;
        List<Axis> axes =
        [
            new Axis(
                KeyOrderNames[0],
                p => KeyOrderedBand(p, SortedField, low, NarrowBand),
                p => RustReader.Require(RustReader.ScanFiltered(p, SortedField, low, NarrowBand), "filtered scan"),
                path),
            new Axis(
                KeyOrderNames[1],
                p => KeyOrderedBand(p, ShuffledField, low, TakeCount),
                p => RustReader.Require(RustReader.ScanFiltered(p, ShuffledField, low, TakeCount), "filtered scan"),
                path),
            new Axis(
                KeyOrderNames[2],
                p => CoveredCount(p, ShuffledField, low, NarrowBand),
                p => RustReader.Require(RustReader.CountFiltered(p, ShuffledField, low, NarrowBand), "count"),
                path),
            new Axis(
                KeyOrderNames[3],
                p => KeyOrderedBand(p, ShuffledField, low, TakeCount),
                p => RustReader.Require(RustReader.Take(p, TakeCount, TakeStride), "take"),
                path),
        ];
        return axes.FindAll(a => selected(a.Name));
    }

    private static async Task WriteKeyOrderFileAsync(string path)
    {
        Vorticity.Types.DTypeArena types = new Vorticity.Types.DTypeArena();
        Vorticity.Types.DType i64 = types.Primitive(Vorticity.Types.PType.I64, Vorticity.Types.Nullability.NonNullable);
        Vorticity.Types.DType schema = types.Struct(
            [SortedField, ShuffledField, "payload"], [i64, i64, i64], Vorticity.Types.Nullability.NonNullable);
        Vorticity.VortexWriteOptions options = new Vorticity.VortexWriteOptions
        {
            RowBlockSize = (int)TakeStride,
            DataBlockTargetBytes = null,

            // Runs of a permutation are as large as the columns they index, which the default
            // budget refuses; the axis is about reading the index, not about whether it pays.
            IndexBudgetPerMille = 1_000_000,
            WritePolicy = Vorticity.Indexes.WritePolicy.None.For(ShuffledField, Vorticity.Indexes.IndexSpec.SortedRuns),
        };

        await using Vorticity.VortexFileWriter writer =
            Vorticity.VortexFileWriter.Create(path, schema, options);
        const int batch = 8_192;
        for (int start = 0; start < KeyOrderRows; start += batch)
        {
            Vorticity.Arrays.CanonicalArena arena = new Vorticity.Arrays.CanonicalArena();
            int[] columns =
            [
                Longs(arena, i64, start, batch, row => row),
                Longs(arena, i64, start, batch, row => row * Shuffle % KeyOrderRows),
                Longs(arena, i64, start, batch, row => row * 7),
            ];
            int root = arena.AddStruct(schema, batch, Vorticity.Arrays.Validity.NonNullable, columns);
            using RecordBatch record = new RecordBatch(arena, root, start);
            await writer.WriteAsync(record, CancellationToken.None).ConfigureAwait(false);
        }

        await writer.CompleteAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private static int Longs(
        Vorticity.Arrays.CanonicalArena arena, Vorticity.Types.DType dtype, int start, int count, Func<long, long> value)
    {
        Vorticity.Buffers.VortexBuffer buffer = arena.Allocate(count * sizeof(long), sizeof(long), out Span<byte> bytes);
        Span<long> values = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, long>(bytes);
        for (int i = 0; i < count; i++)
        {
            values[i] = value(start + i);
        }

        return arena.AddPrimitive(dtype, count, Vorticity.Arrays.Validity.NonNullable, Vorticity.Types.PType.I64, buffer);
    }

    private static VortexExpr Band(string field, long low, long width) => Expr.And(
        Expr.Ge(Expr.Field(field), Expr.Literal(FilterLiteral.From(low))),
        Expr.Lt(Expr.Field(field), Expr.Literal(FilterLiteral.From(low + width))));

    /// <summary>A band delivered in the key order of its own column.</summary>
    private static async Task<long> KeyOrderedBand(string path, string field, long low, long width)
    {
        await using VortexFile file = await Vorticity.Bench.Scenarios.ScenarioSet.OpenAsync(path);
        long rows = 0;
        await foreach (RecordBatch batch in file.ScanBuilder().InKeyOrder(field).Where(Band(field, low, width)).ExecuteAsync()
            .WithCancellation(CancellationToken.None))
        {
            rows += batch.RowCount;
        }

        return rows;
    }

    /// <summary>A band counted: the runs answer it, and no data segment is read.</summary>
    private static async Task<long> CoveredCount(string path, string field, long low, long width)
    {
        await using VortexFile file = await Vorticity.Bench.Scenarios.ScenarioSet.OpenAsync(path);
        return await file.ScanBuilder().Where(Band(field, low, width)).CountAsync().ConfigureAwait(false);
    }

    /// <summary>The string-predicate group's axes, in the order they are reported.</summary>
    /// <remarks>
    /// The gate measured predicates on one i64 column and nothing else, so the place the remaining
    /// read time actually sits -- a predicate on text -- had no reference at all. Two predicates
    /// over two encodings: equality, which a compressed text encoding can answer over its k values
    /// or by compressing the needle with the column's own table, and a prefix, which it cannot.
    ///
    /// TWO ENCODINGS AND NOT THREE. `onpair` belongs in this group and is absent: our writer
    /// deliberately does not produce it (`src/Vorticity/Writing/ColumnCompressor.cs`), and the
    /// generated 1M file that does carry it has a bare array at its root, which a field path cannot
    /// address. Neither is a gap this group can close on its own.
    ///
    /// THE FILES ARE WRITTEN HERE rather than taken from the generated corpus, for the same reason
    /// the key-order group writes its own: the corpus's single-encoding files are bare arrays with
    /// no column to name, and the shared five-column file has no text column at all. An encoding
    /// hint pins what the chooser would otherwise price.
    /// </remarks>
    private static readonly string[] StringPredicateNames =
    [
        "filtered scan, string equality, fsst",
        "filtered scan, string prefix, fsst",
        "filtered scan, string equality, dict",
        "filtered scan, string prefix, dict",
    ];

    /// <summary>The text column both string-predicate files carry.</summary>
    private const string StringField = "strs";

    /// <summary>Rows of each string-predicate file: the shape every other axis reads.</summary>
    private const int StringRows = 65_536;

    /// <summary>The needle that matches one row of the fsst column.</summary>
    private const string FsstNeedle = "https://example.invalid/vortex/conformance/000040000";

    /// <summary>A prefix of the fsst column: the 10 000 rows whose number starts with five zeros.</summary>
    private const string FsstPrefix = "https://example.invalid/vortex/conformance/00000";

    /// <summary>The needle that matches one label of sixteen.</summary>
    private const string DictNeedle = "label-07";

    /// <summary>A prefix of the dict column: the seven labels from 10 to 15, plus 01.</summary>
    private const string DictPrefix = "label-1";

    /// <summary>Writes the two string-predicate files and returns the group's axes.</summary>
    /// <param name="temporary">Collects the files written, for the caller to delete.</param>
    /// <param name="selected">Which axes the caller asked for.</param>
    private static async Task<List<Axis>> StringPredicateAxesAsync(
        List<string> temporary, Func<string, bool> selected)
    {
        string fsst = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"vorticity-strings-fsst-{Guid.NewGuid():N}.vortex");
        string dict = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"vorticity-strings-dict-{Guid.NewGuid():N}.vortex");
        temporary.Add(fsst);
        temporary.Add(dict);
        await WriteStringFileAsync(
            fsst,
            Vorticity.EncodingHint.Fsst,
            // SCRAMBLED, not ascending, and the axis's name depends on it. A column whose values ascend
            // is a sorted column, and a scan answers an equality over one by seeking it: the rows
            // are proved before anything is read and there is no predicate left to evaluate. This
            // axis is named for what the encoding costs a filter, so its column must be one a
            // filter actually has to read. Multiplying by a prime coprime with the row count
            // permutes the rows and keeps every value distinct.
            row => string.Create(
                CultureInfo.InvariantCulture,
                $"https://example.invalid/vortex/conformance/{(row * 7_919) % StringRows:D9}"))
            .ConfigureAwait(false);
        await WriteStringFileAsync(
            dict,
            Vorticity.EncodingHint.Dictionary,
            row => string.Create(CultureInfo.InvariantCulture, $"label-{row % 16:D2}"))
            .ConfigureAwait(false);

        List<Axis> axes =
        [
            new Axis(
                StringPredicateNames[0],
                p => StringEquality(p, FsstNeedle),
                p => RustReader.Require(
                    RustReader.ScanFilteredEqUtf8(p, StringField, FsstNeedle), "string equality"),
                fsst),
            new Axis(
                StringPredicateNames[1],
                p => StringPrefix(p, FsstPrefix),
                p => RustReader.Require(
                    RustReader.ScanFilteredPrefixUtf8(p, StringField, FsstPrefix), "string prefix"),
                fsst),
            new Axis(
                StringPredicateNames[2],
                p => StringEquality(p, DictNeedle),
                p => RustReader.Require(
                    RustReader.ScanFilteredEqUtf8(p, StringField, DictNeedle), "string equality"),
                dict),
            new Axis(
                StringPredicateNames[3],
                p => StringPrefix(p, DictPrefix),
                p => RustReader.Require(
                    RustReader.ScanFilteredPrefixUtf8(p, StringField, DictPrefix), "string prefix"),
                dict),
        ];
        return axes.FindAll(a => selected(a.Name));
    }

    /// <summary>Writes one text column of <see cref="StringRows"/> rows under an encoding hint.</summary>
    /// <param name="path">Where to write.</param>
    /// <param name="hint">The encoding the column is pinned to.</param>
    /// <param name="value">The value of a row, by row number.</param>
    private static async Task WriteStringFileAsync(
        string path, Vorticity.EncodingHint hint, Func<int, string> value)
    {
        Vorticity.Types.DTypeArena types = new Vorticity.Types.DTypeArena();
        Vorticity.Types.DType utf8 = types.Utf8(Vorticity.Types.Nullability.NonNullable);
        Vorticity.Types.DType schema = types.Struct(
            [StringField], [utf8], Vorticity.Types.Nullability.NonNullable);
        Vorticity.VortexWriteOptions options = new Vorticity.VortexWriteOptions
        {
            EncodingHints = new Dictionary<string, Vorticity.EncodingHint>
            {
                [StringField] = hint,
            },
        };

        await using Vorticity.VortexFileWriter writer =
            Vorticity.VortexFileWriter.Create(path, schema, options);
        const int batch = 8_192;
        for (int start = 0; start < StringRows; start += batch)
        {
            Vorticity.Arrays.CanonicalArena arena = new Vorticity.Arrays.CanonicalArena();
            int column = Strings(arena, utf8, start, batch, value);
            int root = arena.AddStruct(schema, batch, Vorticity.Arrays.Validity.NonNullable, [column]);
            using RecordBatch record = new RecordBatch(arena, root, start);
            await writer.WriteAsync(record, CancellationToken.None).ConfigureAwait(false);
        }

        await writer.CompleteAsync(CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>Builds a varbin-view column from a run of rows.</summary>
    private static int Strings(
        Vorticity.Arrays.CanonicalArena arena,
        Vorticity.Types.DType dtype,
        int start,
        int count,
        Func<int, string> value)
    {
        const int ViewSize = 16;
        const int MaxInline = 12;

        byte[][] encoded = new byte[count][];
        int heapBytes = 0;
        for (int i = 0; i < count; i++)
        {
            encoded[i] = Encoding.UTF8.GetBytes(value(start + i));
            if (encoded[i].Length > MaxInline)
            {
                heapBytes += encoded[i].Length;
            }
        }

        Vorticity.Buffers.VortexBuffer views =
            arena.Allocate(count * ViewSize, ViewSize, out Span<byte> viewBytes);
        viewBytes.Clear();
        Vorticity.Buffers.VortexBuffer heap = Vorticity.Buffers.VortexBuffer.Empty;
        Span<byte> heapBuffer = default;
        if (heapBytes > 0)
        {
            heap = arena.Allocate(heapBytes, 1, out heapBuffer);
        }

        int offset = 0;
        for (int i = 0; i < count; i++)
        {
            byte[] bytes = encoded[i];
            Span<byte> view = viewBytes.Slice(i * ViewSize, ViewSize);
            BinaryPrimitives.WriteUInt32LittleEndian(view, (uint)bytes.Length);
            if (bytes.Length <= MaxInline)
            {
                bytes.CopyTo(view[4..]);
                continue;
            }

            bytes.AsSpan(0, 4).CopyTo(view[4..8]);
            BinaryPrimitives.WriteUInt32LittleEndian(view[8..12], 0);
            BinaryPrimitives.WriteUInt32LittleEndian(view[12..16], (uint)offset);
            bytes.CopyTo(heapBuffer[offset..]);
            offset += bytes.Length;
        }

        Span<Vorticity.Buffers.VortexBuffer> buffers = heapBytes == 0
            ? [Vorticity.Buffers.VortexBuffer.Empty]
            : [heap];
        return arena.AddVarBinView(dtype, count, Vorticity.Arrays.Validity.NonNullable, views, buffers);
    }

    /// <summary>Scans keeping the rows whose text column equals a needle.</summary>
    private static async Task<long> StringEquality(string path, string needle)
    {
        await using VortexFile file = await Vorticity.Bench.Scenarios.ScenarioSet.OpenAsync(path);
        long rows = 0;
        VortexExpr predicate = Expr.Eq(
            Expr.Field(StringField), Expr.Literal(FilterLiteral.From(needle)));
        await foreach (RecordBatch batch in file.ScanBuilder().Where(predicate).ExecuteAsync()
            .WithCancellation(CancellationToken.None))
        {
            rows += batch.RowCount;
        }

        return rows;
    }

    /// <summary>Scans keeping the rows whose text column starts with a prefix.</summary>
    private static async Task<long> StringPrefix(string path, string prefix)
    {
        await using VortexFile file = await Vorticity.Bench.Scenarios.ScenarioSet.OpenAsync(path);
        long rows = 0;
        VortexExpr predicate = Expr.StartsWith(
            Expr.Field(StringField), FilterLiteral.From(prefix));
        await foreach (RecordBatch batch in file.ScanBuilder().Where(predicate).ExecuteAsync()
            .WithCancellation(CancellationToken.None))
        {
            rows += batch.RowCount;
        }

        return rows;
    }

    /// <summary>
    /// The band-over-runs axis, which the generated corpus cannot carry.
    /// </summary>
    /// <remarks>
    /// The corpus's `runend` file is a bare array with no column to name, and a predicate names a
    /// column -- the same reason the string-predicate group writes its own. A band is the predicate
    /// a run-end column is for: it is answered by comparing each run's value once, where a scan of
    /// the rows compares it as many times as the run is long.
    ///
    /// WHAT IT ANSWERED THE DAY IT WAS BUILT, so that nobody pays the experiment twice. Letting the
    /// encoding answer the band from its runs -- one comparison a run, then a fill -- is worth
    /// nothing here: four runs alternating the push on and off read 101,0 and 103,4 us with it
    /// against 101,7 and 105,5 without, a difference of one and a half per cent under a noise floor
    /// of two to four. The saving a dictionary gets does not transfer, and the reason is in the
    /// shape: expanding a run-end column is a fill, not a decode, and the answer is a fill too, so
    /// both paths write one byte a row and that write is the axis. An encoding whose rows are cheap
    /// to produce has nothing to win by not producing them.
    ///
    /// AND THE BIT-PACKED HALF SAYS THE SAME, more bluntly. Comparing in the packed domain, which
    /// the reference does, could at most save what the unpack costs -- and DOUBLING the unpack on
    /// this axis reads 97,1 and 99,3 us against a baseline of 97,8, which is no change at all. The
    /// decode such a push would avoid costs nothing measurable here, so the fourteen per cent that
    /// separates us from the reference on this axis is somewhere else.
    /// </remarks>
    private static readonly string[] RunEndPredicateNames =
        ["filtered scan, band, runend", "filtered scan, band, bitpacked"];

    /// <summary>The integer column the run-end file carries.</summary>
    private const string RunEndField = "runs";

    /// <summary>Rows of the run-end file, and the length of each of its runs.</summary>
    private const int RunEndRows = 65_536;

    /// <summary>How many rows share a value, which fixes the run count at a thousand and change.</summary>
    private const int RunLength = 64;

    /// <summary>The band's first value and its width, in values and therefore in runs.</summary>
    private const long RunEndBandLow = 100;

    /// <summary>Sixteen runs, a thousand rows, one and a half per cent of the file.</summary>
    private const long RunEndBandWidth = 16;

    /// <summary>Writes the run-end file and returns the group's axis.</summary>
    /// <param name="temporary">Collects the file written, for the caller to delete.</param>
    /// <param name="selected">Which axes the caller asked for.</param>
    private static async Task<List<Axis>> RunEndPredicateAxesAsync(
        List<string> temporary, Func<string, bool> selected)
    {
        string runs = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"vorticity-runend-{Guid.NewGuid():N}.vortex");
        string packed = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"vorticity-bitpacked-{Guid.NewGuid():N}.vortex");
        temporary.Add(runs);
        temporary.Add(packed);

        // THE SAME COLUMN UNDER TWO ENCODINGS, which is what makes the pair readable: a thousand
        // values in runs of sixty-four bit-pack to ten bits just as well as they run-end, so the
        // two axes differ in how the column is stored and in nothing else.
        await WriteRunEndFileAsync(runs, Vorticity.EncodingHint.RunEnd)
            .ConfigureAwait(false);
        await WriteRunEndFileAsync(packed, Vorticity.EncodingHint.BitPacked)
            .ConfigureAwait(false);

        List<Axis> axes =
        [
            new Axis(
                RunEndPredicateNames[0],
                RunEndBand,
                p => RustReader.Require(
                    RustReader.ScanFiltered(p, RunEndField, RunEndBandLow, RunEndBandWidth),
                    "filtered scan"),
                runs),
            new Axis(
                RunEndPredicateNames[1],
                RunEndBand,
                p => RustReader.Require(
                    RustReader.ScanFiltered(p, RunEndField, RunEndBandLow, RunEndBandWidth),
                    "filtered scan"),
                packed),
        ];
        return axes.FindAll(a => selected(a.Name));
    }

    /// <summary>Writes one integer column of runs of <see cref="RunLength"/> equal values.</summary>
    /// <param name="path">Where to write.</param>
    /// <param name="hint">The encoding the column is pinned to.</param>
    private static async Task WriteRunEndFileAsync(
        string path, Vorticity.EncodingHint hint)
    {
        Vorticity.Types.DTypeArena types = new Vorticity.Types.DTypeArena();
        // i64 and not i32: the reference's filtered scan takes its bounds as i64 and refuses a
        // column of another width, so a narrower column would measure nothing at all.
        Vorticity.Types.DType i64 = types.Primitive(
            Vorticity.Types.PType.I64, Vorticity.Types.Nullability.NonNullable);
        Vorticity.Types.DType schema = types.Struct(
            [RunEndField], [i64], Vorticity.Types.Nullability.NonNullable);
        Vorticity.VortexWriteOptions options = new Vorticity.VortexWriteOptions
        {
            EncodingHints = new Dictionary<string, Vorticity.EncodingHint>
            {
                [RunEndField] = hint,
            },
        };

        await using Vorticity.VortexFileWriter writer =
            Vorticity.VortexFileWriter.Create(path, schema, options);
        const int batch = 8_192;
        for (int start = 0; start < RunEndRows; start += batch)
        {
            Vorticity.Arrays.CanonicalArena arena = new Vorticity.Arrays.CanonicalArena();
            Vorticity.Buffers.VortexBuffer values =
                arena.Allocate(batch * sizeof(long), sizeof(long), out Span<byte> bytes);
            Span<long> typed = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, long>(bytes);
            for (int i = 0; i < batch; i++)
            {
                typed[i] = (start + i) / RunLength;
            }

            int column = arena.AddPrimitive(
                i64, batch, Vorticity.Arrays.Validity.NonNullable,
                Vorticity.Types.PType.I64, values);
            int root = arena.AddStruct(
                schema, batch, Vorticity.Arrays.Validity.NonNullable, [column]);
            using RecordBatch record = new RecordBatch(arena, root, start);
            await writer.WriteAsync(record, CancellationToken.None).ConfigureAwait(false);
        }

        await writer.CompleteAsync(CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>Scans keeping the rows whose integer column falls in the band.</summary>
    private static async Task<long> RunEndBand(string path)
    {
        VortexExpr band = Expr.And(
            Expr.Ge(Expr.Field(RunEndField), Expr.Literal(FilterLiteral.From(RunEndBandLow))),
            Expr.Lt(
                Expr.Field(RunEndField),
                Expr.Literal(FilterLiteral.From(RunEndBandLow + RunEndBandWidth))));

        await using VortexFile file = await Vorticity.Bench.Scenarios.ScenarioSet.OpenAsync(path);
        long rows = 0;
        await foreach (RecordBatch batch in file.ScanBuilder().Where(band).ExecuteAsync()
            .WithCancellation(CancellationToken.None))
        {
            rows += batch.RowCount;
        }

        return rows;
    }

    /// <summary>The two axis names of one rewritten entry.</summary>
    private static IEnumerable<string> Names(string label) =>
        [$"rewritten {label}, reference's", $"rewritten {label}, ours"];

    /// <summary>Every axis name the rewritten group would produce.</summary>
    private static IEnumerable<string> RewrittenNames() =>
        Rewritten.SelectMany(r => Names(r.Label));

    /// <summary>Reads a file with our reader and writes it back with our writer, to a path.</summary>
    /// <param name="source">The file to read.</param>
    /// <param name="destination">Where to write it.</param>
    /// <remarks>
    /// EXPOSED FOR INSPECTION. The `rewritten` axes write this file into a temporary directory and
    /// delete it, which is right for a gate and useless for asking WHICH encoding our writer puts
    /// where the reference puts another. `--rewrite <in> <out>` keeps the bytes so
    /// `vxdump --encodings` and `--throughput` can be pointed at them.
    /// </remarks>
    /// <param name="edition">
    /// The edition the writer targets. Older editions exclude later encodings, which is how
    /// `--rewrite <in> <out> <edition>` isolates one of them: `Core20250500` has no
    /// `vortex.zstd`, so the difference between the two rewrites IS zstd.
    /// </param>
    /// <param name="rowBlock">
    /// `VortexWriteOptions.RowBlockSize`, or null for its default of 8192.
    /// </param>
    /// <param name="dataBlockBytes">
    /// `VortexWriteOptions.DataBlockTargetBytes`: a byte count, or null to leave the default of
    /// 1 MiB, or `Off` to disable the coalescing entirely.
    /// </param>
    /// <remarks>
    /// THE TWO BLOCK KNOBS ARE HERE BECAUSE CHUNKING COULD NOT BE PRICED WITHOUT THEM.
    /// They decide the file's chunking, a zone is a chunk, and a filter that keeps 219 rows out of
    /// a 24 576-row chunk decodes all 24 576 -- so they decide what a selective scan costs, and
    /// nothing in this bench could vary them. `Off` is spelled out rather than `0` because zero is
    /// a legal target that means "no minimum", and the difference between that and "no coalescing
    /// at all" is exactly what is being measured.
    /// </remarks>
    internal static async Task RewriteAsync(
        string source,
        string destination,
        VortexEdition? edition,
        int? rowBlock = null,
        long? dataBlockBytes = null)
    {
        if (edition is null && rowBlock is null && dataBlockBytes is null)
        {
            await Rewrite(source, destination).ConfigureAwait(false);
            return;
        }

        Vorticity.VortexWriteOptions options = new Vorticity.VortexWriteOptions
        {
            TargetEdition = edition ?? Vorticity.VortexWriteOptions.Default.TargetEdition,
            RowBlockSize = rowBlock ?? Vorticity.VortexWriteOptions.Default.RowBlockSize,
            DataBlockTargetBytes = dataBlockBytes switch
            {
                null => Vorticity.VortexWriteOptions.Default.DataBlockTargetBytes,
                Off => null,
                long bytes => bytes,
            },
        };

        await using VortexFile file = await VortexFile.OpenAsync(source, CancellationToken.None);
        await using Vorticity.VortexFileWriter writer =
            Vorticity.VortexFileWriter.Create(destination, file.DType, options);
        await foreach (RecordBatch batch in file.ScanBuilder().ExecuteAsync()
            .WithCancellation(CancellationToken.None))
        {
            await writer.WriteAsync(batch, CancellationToken.None);
        }

        await writer.CompleteAsync(CancellationToken.None);
    }

    /// <summary>The <c>dataBlockBytes</c> value that means "no byte coalescing at all".</summary>
    internal const long Off = -1;

    /// <summary>Reads a file with our reader and writes it back with our writer.</summary>
    private static async Task Rewrite(string source, string destination)
    {
        await using VortexFile file = await VortexFile.OpenAsync(source, CancellationToken.None);
        await using Vorticity.VortexFileWriter writer =
            Vorticity.VortexFileWriter.Create(destination, file.DType);
        await foreach (RecordBatch batch in file.ScanBuilder().ExecuteAsync()
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
        List<double> warmOurs = [];
        List<double> warmTheirs = [];
        do
        {
            warmOurs.Add(await TimeAsync(axis.Ours, path, 1).ConfigureAwait(false));
            warmTheirs.Add(Time(axis.Theirs, path, 1));
        }
        while (Stopwatch.GetTimestamp() < deadline);

        Warmed[axis.Name] = warmOurs.Count;

        // The reference's faster split on this axis's file, once both are warm: a figure one of its
        // own settings beats is not its figure (RustReader.FasterSplit).
        long split = RustReader.FasterSplit(() => axis.Theirs(path), SplitRounds);

        // k FROM THE WARM-UP'S TAIL, not from its last call. k is data now that a reference records
        // it (see `Reference`), and a k read off one timing jittered by a call or two on the short
        // axes -- 18 one run, 19 the next -- which would have let `--rebase` mistake noise for an
        // estimator change. The second half of the warm-up is the part that is actually warm; the
        // warm-up timed the reference's default split, so a file it reads faster otherwise is timed
        // again under the split it keeps.
        double hotOurs = Median([.. warmOurs.Skip(warmOurs.Count / 2)]);
        double hotTheirs = split == RustReader.SplitDefault
            ? Median([.. warmTheirs.Skip(warmTheirs.Count / 2)])
            : Median([.. Enumerable.Range(0, SplitRounds).Select(_ => Time(axis.Theirs, path, 1))]);

        // On the FASTER side, because it is the faster side that needs the grouping: see `Repeats`.
        int repeats = Repeats(Math.Min(hotOurs, hotTheirs), hotOurs + hotTheirs);

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

        RustReader.Require(RustReader.SetSplit(RustReader.SplitDefault), "split");
        return new Measurement(Median([.. mine]), Median([.. rust]), interval, repeats, split);
    }

    /// <summary>One axis measured: both sides, the ratio's interval, and how it was timed.</summary>
    /// <param name="Ours">Median microseconds per round on our side.</param>
    /// <param name="Theirs">Median microseconds per round on Rust's.</param>
    /// <param name="Ratio">The median per-round ratio and its 95% interval.</param>
    /// <param name="Repeats">Calls per timed round.</param>
    /// <param name="Split">The reference's split the axis was timed under, the faster of its two.</param>
    private readonly record struct Measurement(
        double Ours, double Theirs, Interval Ratio, int Repeats, long Split);

    /// <summary>Calls per timed round, so that one round clears the timer's noise floor.</summary>
    /// <param name="microseconds">One call on the FASTER of the two sides.</param>
    /// <param name="roundMicroseconds">One call on each side, for the budget cap.</param>
    /// <returns>Calls per timed round, at least one.</returns>
    /// <remarks>
    /// <para>
    /// THE FASTER SIDE SETS k, and taking the slower one was a mistake: a round whose two halves
    /// are 93 us and 1 223 us clears a millisecond on the strength of the slow half alone, so
    /// `open to first batch` grouped k = 1 and OUR term stayed timed on a single 93 us call -- the
    /// exact case grouping exists for, and the worst mde of the axes. The ratio is a quotient of
    /// two timings and it is no better resolved than its worse-resolved term, so the floor has to
    /// be met by the term that is furthest from it.
    /// </para>
    /// <para>
    /// AND CAPPED BY THE BUDGET, because k multiplies the round and <see cref="MinRounds"/> of them
    /// are not optional: an axis whose fast side is a microsecond would ask for a thousand calls and
    /// spend its whole <see cref="Budget"/> before the interval was ever consulted. The cap is the
    /// arithmetic that keeps the floor of rounds affordable; when it binds, the round is shorter
    /// than the noise floor and the mde column is where that shows.
    /// </para>
    /// </remarks>
    private static int Repeats(double microseconds, double roundMicroseconds)
    {
        if (microseconds <= 0)
        {
            return 1;
        }

        int wanted = Math.Max(1, (int)Math.Ceiling(MinRoundMicroseconds / microseconds));
        if (roundMicroseconds <= 0)
        {
            return wanted;
        }

        int affordable = Math.Max(
            1, (int)(Budget * 1_000_000 / (MinRounds * roundMicroseconds)));
        return Math.Min(wanted, affordable);
    }

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
        await using VortexFile file = await Vorticity.Bench.Scenarios.ScenarioSet.OpenAsync(path);
        long rows = 0;
        await foreach (RecordBatch batch in file.ScanBuilder().Project(Field).ExecuteAsync()
            .WithCancellation(CancellationToken.None))
        {
            rows += batch.RowCount;
        }

        return rows;
    }

    private static async Task<long> FirstBatch(string path)
    {
        await using VortexFile file = await Vorticity.Bench.Scenarios.ScenarioSet.OpenAsync(path);
        await foreach (RecordBatch batch in file.ScanBuilder().ExecuteAsync()
            .WithCancellation(CancellationToken.None))
        {
            return batch.RowCount;
        }

        return 0;
    }

    /// <summary>
    /// Reads a file and writes it back out, which is the write axis -- the one the benchmark design
    /// never gave a timing reference.
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
        await using VortexFile file = await Vorticity.Bench.Scenarios.ScenarioSet.OpenAsync(path);
        return file.RowCount;
    }
}
