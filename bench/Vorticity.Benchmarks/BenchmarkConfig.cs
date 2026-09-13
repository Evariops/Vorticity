// The two configuration decisions this project makes: the toolchain, and the profile.
//
// THE TOOLCHAIN IS IN-PROCESS. BenchmarkDotNet's default toolchain generates a project, builds it and
// runs it out of process, which is what makes its numbers trustworthy across runtimes. 0.15.x cannot
// do that here: it does not know the `net11.0` moniker, and its SDK validator throws
// `GetRuntimeVersion not implemented for NotRecognized` before a single benchmark runs (reproduced on
// 0.15.8; 0.16.0-preview.1 is the first version that runs net11.0 out of process -- see
// BENCH-AUDIT.md §6). The in-process emit toolchain runs the benchmarks in the host process instead.
// What that costs: no process isolation, no different runtime or GC mode per job, a shared JIT state.
// What it does not cost: the measurement itself -- same warmup strategy, same statistics, same
// MemoryDiagnoser.
//
// THE PROFILE IS FAST BY DEFAULT, AND `--full` IS THE EXCEPTION. A class under `Job.Default` spends its
// time in the job, not in the kernel: FsstKernelBenchmarks reads 102 s for four cases, of which the
// kernel is microseconds. Measured the same evening on the same three classes (BENCH-AUDIT.md §4.2):
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
// 5 % on a kernel, or any number that goes into bench/BASELINE.md, is confirmed with `--full` on the
// one class concerned.
using System;
using System.Linq;

using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Filters;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Toolchains.InProcess.Emit;

using Perfolizer.Horology;

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
    /// answered their question once and are not regression guards (BENCH-AUDIT.md §3.1). They stay
    /// runnable and stay out of the run with no argument.
    /// </summary>
    internal static bool Exploring { get; set; }

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
        Job job = Job.Default.WithToolchain(InProcessEmitToolchain.Instance);
        AddJob(Full
            ? job.WithId("full")
            : job.WithId("fast")
                .WithIterationTime(TimeInterval.FromMilliseconds(100))
                .WithMinIterationTime(TimeInterval.FromMilliseconds(50))
                .WithIterationCount(5)
                .WithMinWarmupCount(4)
                .WithMaxWarmupCount(30));
        AddExporter(BenchmarkDotNet.Exporters.MarkdownExporter.GitHub);
        AddLogger(BenchmarkDotNet.Loggers.ConsoleLogger.Default);
        AddColumnProvider(DefaultColumnProviders.Instance);
        AddDiagnoser(BenchmarkDotNet.Diagnosers.MemoryDiagnoser.Default);
        if (!Exploring)
        {
            AddFilter(new SimpleFilter(benchmark => !benchmark.Descriptor.Categories.Contains(
                Explore, StringComparer.OrdinalIgnoreCase)));
        }

        WithOptions(ConfigOptions.DisableOptimizationsValidator);
    }
}
