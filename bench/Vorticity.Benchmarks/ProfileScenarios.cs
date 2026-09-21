// The scenarios the profiling session runs, as a plain loop rather than a benchmark.
//
// Every benchmark table reports TOTALS. What none of them has is the distribution inside
// one: the optimizations in this repository were found by reading code and deducing, and twice out
// of three times the obvious target was the wrong one - vectorizing the bit-packing kernel was worth
// 3.6x on the kernel and 1.10x end to end, because that was not where the time was.
//
// WHY THIS IS NOT A BENCHMARK. BenchmarkDotNet's own harness, its warmup strategy and its
// per-iteration bookkeeping all appear in a sampled profile, and on a 1.3 ms scenario they are not
// negligible. A bare loop in a bare process gives `dotnet-trace` nothing to attribute but the
// library. The trade is that there is no statistical machinery here at all - which is correct,
// because a profile is not a measurement of speed and must never be quoted as one.
//
// `[EventPipeProfiler]` would be the in-harness equivalent; it is not used because it inherits
// the in-process toolchain's problem of profiling the harness along with
// the code. `dotnet-trace collect -- dotnet <this>.dll --profile <name>` has neither problem.
using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

namespace Vorticity.Benchmarks;

/// <summary>Runs one named scenario in a loop, for a profiler to sample.</summary>
internal static class ProfileScenarios
{
    /// <summary>Runs <paramref name="name"/> for <paramref name="seconds"/> and reports the rate.</summary>
    /// <param name="name">The scenario.</param>
    /// <param name="seconds">How long to loop.</param>
    /// <returns>0, or 2 when the scenario is unknown.</returns>
    internal static async Task<int> RunAsync(string name, double seconds)
    {
        string path = Corpus.Dataset("VORTICITY_BENCH_DATA", "containers/zoned_many_zones_nulls");
        // THE SCENARIOS ARE `Scenarios.All`, not copies of them. A profile is a
        // statement about a gate's time only if the two run the same code with the same arguments,
        // and two files that merely look alike do not guarantee that -- a correction applied to
        // one copy and not the other is what happens when they drift.
        Scenarios.Scenario? found = Scenarios.ByName(name);
        if (found is null)
        {
            Console.Error.WriteLine(
                $"Unknown scenario '{name}'. One of: " +
                string.Join(", ", Scenarios.All.Select(entry => entry.Name)) + ".");
            return 2;
        }

        Func<string, Task<long>> scenario = found.Ours;

        // Warm first, and OUTSIDE the profiled window, or the profile is a picture of the JIT. That
        // is not a hypothetical here: tiering has already distorted two measurements in this
        // repository, one of them a ceiling that went red on a cold first run.
        for (int i = 0; i < 20; i++)
        {
            await scenario(path).ConfigureAwait(false);
        }

        long deadline = Stopwatch.GetTimestamp() + (long)(seconds * Stopwatch.Frequency);
        long iterations = 0;
        long rows = 0;
        while (Stopwatch.GetTimestamp() < deadline)
        {
            rows += await scenario(path).ConfigureAwait(false);
            iterations++;
        }

        Console.Out.WriteLine(
            $"{name}: {iterations} iterations, {rows} rows, over {seconds:F1}s of sampling.");
        return 0;
    }
}
