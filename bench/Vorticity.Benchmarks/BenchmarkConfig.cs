// The two configuration decisions this project makes: the toolchain, and the profile.
//
// THE TOOLCHAIN DEPENDS ON THE PROFILE, and that is new. BenchmarkDotNet's default toolchain
// generates a project, builds it and runs it out of process, which is what makes its numbers
// trustworthy across runtimes. 0.15.x could not do that here -- it does not know the `net11.0`
// moniker and throws `GetRuntimeVersion not implemented for NotRecognized` before a single benchmark
// runs (reproduced on 0.15.8) -- so everything was in-process. **0.16.0-preview.1 runs net11.0 out of
// process**, so:
//
//   fast (the default)   in-process     three seconds of measurement does not pay ten of building a host
//   --full               out of process process isolation per case, GC and runtime jobs, and `--disasm`
//   --full --inprocess   in-process     the escape hatch, when the generated host is the problem
//
// What in-process costs: no process isolation, no different runtime or GC mode per job, a shared JIT
// state, and no disassembly at all. What it does not cost: the measurement itself -- same warmup
// strategy, same statistics, same MemoryDiagnoser.
//
// THE PROFILE IS FAST BY DEFAULT, AND `--full` IS THE EXCEPTION. A class under `Job.Default` spends its
// time in the job, not in the kernel: an FSST kernel class read 102 s for four cases, of which the
// kernel is microseconds. Measured the same evening on the same three classes:
//
//     profile                                   Fsst (4)   Take (8)   FastLanes (4)   fidelity vs full
//     full      Job.Default                       102 s      150 s        82 s          --
//     fast      auto warm-up, 5 x 50-100 ms         7 s       16 s         3 s          within +-4 %
//     3 warm-ups, 5 x 100 ms                        6 s       11 s         2 s          a 630 us path read 2 265 us: under-warmed
//     2 warm-ups, 3 x 50 ms                         2 s        4 s         1 s          measures the JIT's tiering, not the code
//
// The last two rows are why the warm-up is left to BenchmarkDotNet: it warms until the measurements
// stabilize, which is what a 600 us async path needs and what a fixed count does not give it. Only the
// iteration time and count are shortened. A fast-profile figure is a DIRECTION; anything under about
// 5 % on a kernel, or any number recorded as a baseline, is confirmed with `--full` on the
// one class concerned.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Filters;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Toolchains.InProcess.Emit;

using Perfolizer.Horology;
using Perfolizer.Metrology;

namespace Vorticity.Benchmarks;

/// <summary>In-process, fast by default; <c>--full</c> selects the reference profile.</summary>
public sealed class BenchmarkConfig : ManualConfig
{
    /// <summary>
    /// Set by <see cref="Program"/> before any benchmark class is instantiated: <c>--full</c> on the
    /// command line. A static rather than an argument because BenchmarkDotNet instantiates this
    /// config from the <c>[Config]</c> attribute, with no way to pass it anything.
    /// </summary>
    internal static bool Full { get; set; }

    /// <summary>
    /// Set the same way by <see cref="Program"/>: <c>--explore</c> on the command line. The
    /// <see cref="Explore"/> category holds the CURVES - a selectivity sweep, a take sweep - which
    /// answered their question once and are not regression guards. They stay
    /// runnable and stay out of the run with no argument.
    /// </summary>
    internal static bool Exploring { get; set; }

    /// <summary>
    /// Set the same way: <c>--inprocess</c>, which keeps <c>--full</c> in this process.
    /// </summary>
    /// <remarks>
    /// The fast profile is in-process unconditionally -- three seconds of measurement does not pay
    /// ten seconds of generating and building a host. <c>--full</c> is the opposite trade and now
    /// runs OUT of process by default: process isolation per case, no JIT or
    /// `ArrayPool.Shared` state carried from the previous class, GC and runtime jobs side by side,
    /// and `--disasm`, which the in-process toolchain cannot do at all. This flag is the escape
    /// hatch for the case where the generated host is the problem rather than the answer.
    /// </remarks>
    internal static bool InProcess { get; set; }

    /// <summary>
    /// Set the same way: each <c>--case Name=Value[,Name=Value]</c> on the command line, the cases
    /// of a class to run, a case running when every parameter it names has that value. None means
    /// every case.
    /// </summary>
    /// <remarks>
    /// A curve of a dozen cases answers one question at the end; the iterations before it ask about
    /// one or two of them, and running the others is waiting for nothing.
    /// </remarks>
    internal static List<Dictionary<string, string>> Cases { get; } = [];

    /// <summary>The category name for a curve: excluded unless <c>--explore</c> asks for it.</summary>
    public const string Explore = "explore";

    /// <summary>
    /// A micro-benchmark of one kernel, measured against a ported arm in the same process: the
    /// classes a kernel change runs before it commits.
    /// </summary>
    public const string Kernel = "kernel";

    /// <summary>
    /// A library path measured through its public API rather than at a kernel. Slower per case than
    /// <see cref="Kernel"/>, and in the run with no argument for the same reason: it guards a number
    /// nothing else guards.
    /// </summary>
    public const string Path = "path";

    /// <summary>
    /// Every class carries exactly one of these. The run with no argument is <see cref="Kernel"/>
    /// plus <see cref="Path"/>; <c>--explore</c> adds the third. <see cref="Program"/> asserts the
    /// labelling at startup, so a class that arrives without one is a build-time-loud mistake rather
    /// than a case that silently stops running.
    /// </summary>
    public static readonly string[] Categories = [Kernel, Path, Explore];

    public BenchmarkConfig()
    {
        Job inProcess = Job.Default.WithToolchain(InProcessEmitToolchain.Default);
        AddJob(Full
            ? (InProcess ? inProcess.WithId("full, in process") : Job.Default.WithId("full"))
            : inProcess.WithId("fast")
                .WithIterationTime(TimeInterval.FromMilliseconds(100))
                .WithMinIterationTime(TimeInterval.FromMilliseconds(50))
                .WithIterationCount(5)
                .WithMinWarmupCount(4)
                .WithMaxWarmupCount(30));
        AddExporter(BenchmarkDotNet.Exporters.MarkdownExporter.GitHub);

        // THE FULL JSON IS WHAT MAKES TWO RUNS COMPARABLE. The markdown table is for reading; it
        // carries a mean and a standard deviation, which is not enough to test whether two runs
        // differ. `JsonExporter.Full` writes every measurement, and `--compare` reads two of those
        // files and prints Faster / Same / Slower per case.
        AddExporter(BenchmarkDotNet.Exporters.Json.JsonExporter.Full);
        AddLogger(BenchmarkDotNet.Loggers.ConsoleLogger.Default);
        AddColumnProvider(DefaultColumnProviders.Instance);
        AddDiagnoser(BenchmarkDotNet.Diagnosers.MemoryDiagnoser.Default);

        // A SPEEDUP, NOT A RATIO: the direction of every other table the bench publishes (see
        // SpeedupColumn). The allocation ratio stays; it is bytes, not time.
        HideColumns(Column.Ratio, Column.RatioSD);
        AddColumn(new SpeedupColumn());

        // THE DECISION RULE, AS A COLUMN. The project's rule is that a gain counts when a row's
        // time over its baseline's leaves [0.95; 1.05] and two of that ratio's standard deviations
        // around it exclude 1 -- arithmetic done by hand on every table, and therefore done wrong
        // or not at all. This is the same question asked by
        // the library: a TOST against the baseline arm at the same threshold, printed as
        // `Faster` / `Same` / `Slower`. A 3% threshold was proposed; the rule it automates says
        // 5%, so 5% is what the column carries -- a threshold that disagrees with the rule it
        // stands for would be worse than no column.
        // ns/row and GB/s for the classes that declare what one invocation moves.
        AddColumn(new PerRowColumn(), new ThroughputColumn());
        AddColumn(new StatisticalTestColumn(
            new PercentValue(5).ToThreshold()));

        // THE OPTIMIZATIONS VALIDATOR IS ON. `ConfigOptions.DisableOptimizationsValidator` used to
        // sit at the end of this constructor, which turned off the one check that refuses to
        // measure a Debug assembly. Nothing needed it: with it removed, a Release run is unaffected
        // and a Debug run refuses, which is the whole point. It is not sufficient on its own --
        // BenchmarkDotNet prints its complaint and still exits 0, and it never sees `--ratio-check`
        // or `--throughput` -- so `Program.Main` carries the guard that covers every mode.
        if (!Exploring)
        {
            AddFilter(new SimpleFilter(benchmark => !benchmark.Descriptor.Categories.Contains(
                Explore, StringComparer.OrdinalIgnoreCase)));
        }

        if (Cases.Count > 0)
        {
            AddFilter(new SimpleFilter(benchmark => Cases.Any(wanted => wanted.All(pair =>
                benchmark.Parameters.Items.Any(parameter =>
                    string.Equals(parameter.Name, pair.Key, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(
                        Convert.ToString(parameter.Value, CultureInfo.InvariantCulture), pair.Value,
                        StringComparison.OrdinalIgnoreCase))))));
        }

    }
}
