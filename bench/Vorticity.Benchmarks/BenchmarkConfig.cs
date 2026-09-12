// The one configuration decision this project has to make, and why it is not the usual one.
//
// BenchmarkDotNet's default toolchain generates a project, builds it and runs it out of process,
// which is what makes its numbers trustworthy across runtimes. It cannot do that here: 0.15.4 does
// not know the `net11.0` moniker, and its SDK validator throws
// `GetRuntimeVersion not implemented for NotRecognized` before a single benchmark runs. The library
// targets net11.0 only, deliberately (docs/03-architecture.md §1), so there is no other leg to move
// to.
//
// The in-process emit toolchain runs the benchmarks in the host process instead. What that costs is
// real and worth stating rather than hiding: no process isolation, so a benchmark cannot be
// measured against a different runtime or a different GC mode, and the host's own JIT state is
// shared. What it does not cost is the measurement itself -- the same warmup, the same iteration
// strategy, the same statistics, the same MemoryDiagnoser.
//
// Revisit when BenchmarkDotNet ships net11.0 support: deleting this file and the [Config] attributes
// is the whole change.
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Toolchains.InProcess.Emit;

namespace Vorticity.Benchmarks;

/// <summary>Runs in process, because BenchmarkDotNet 0.15.4 does not recognize net11.0.</summary>
public sealed class BenchmarkConfig : ManualConfig
{
    public BenchmarkConfig()
    {
        AddJob(Job.Default.WithToolchain(InProcessEmitToolchain.Instance));
        AddExporter(BenchmarkDotNet.Exporters.MarkdownExporter.GitHub);
        AddLogger(BenchmarkDotNet.Loggers.ConsoleLogger.Default);
        AddColumnProvider(DefaultColumnProviders.Instance);
        AddDiagnoser(BenchmarkDotNet.Diagnosers.MemoryDiagnoser.Default);
        WithOptions(ConfigOptions.DisableOptimizationsValidator);
    }
}
