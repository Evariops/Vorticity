// The scenarios the profiling session runs, as a plain loop rather than a benchmark.
//
// docs/05 and every table in bench/ report TOTALS. What none of them has is the distribution inside
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
// `[EventPipeProfiler]` would be the in-harness equivalent and is what the plan names; it is not
// used because it inherits the in-process toolchain's problem of profiling the harness along with
// the code. `dotnet-trace collect -- dotnet <this>.dll --profile <name>` has neither problem.
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

using Vorticity.Columns;
using Vorticity.File;
using Vorticity.Scan;
using Vorticity.Writing;

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
        Func<string, Task<long>> scenario = name switch
        {
            "fullscan" => FullScan,
            "projected" => Projected,
            "take" => ScatteredTake,
            "write" => Write,
            _ => null!,
        };

        if (scenario is null)
        {
            Console.Error.WriteLine(
                $"Unknown scenario '{name}'. One of: fullscan, projected, take, write.");
            return 2;
        }

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

    private static async Task<long> FullScan(string path)
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

    private static async Task<long> Projected(string path)
    {
        await using VortexFile file = await VortexFile.OpenAsync(path, CancellationToken.None);
        long rows = 0;
        await foreach (RecordBatch batch in file.Scan().Project("monotone").ExecuteAsync()
            .WithCancellation(CancellationToken.None))
        {
            rows += batch.RowCount;
        }

        return rows;
    }

    private static async Task<long> ScatteredTake(string path)
    {
        long[] indices = new long[64];
        for (int i = 0; i < indices.Length; i++)
        {
            indices[i] = (i * 1024L) + 511;
        }

        await using VortexFile file = await VortexFile.OpenAsync(path, CancellationToken.None);
        long rows = 0;
        await foreach (RecordBatch batch in file.Scan().Take(indices).ExecuteAsync()
            .WithCancellation(CancellationToken.None))
        {
            rows += batch.RowCount;
        }

        return rows;
    }

    /// <summary>Read every batch and write it back through a discarding sink.</summary>
    /// <remarks>
    /// The read half is inside the loop and inside the profile, which is unavoidable without a
    /// second harness and is not a problem here: a profile attributes by FRAME, so the reader's
    /// frames and the writer's separate themselves in the output. The totals in
    /// bench/ALLOCATIONS.md say the write half dominates by 667x on allocation; this says what it
    /// dominates by on time.
    /// </remarks>
    private static async Task<long> Write(string path)
    {
        long rows = 0;
        await using VortexFile source = await VortexFile.OpenAsync(path, CancellationToken.None);
        await using VortexFileWriter writer = VortexFileWriter.Create(new DiscardSink(), source.Schema);

        await foreach (RecordBatch batch in source.Scan().ExecuteAsync()
            .WithCancellation(CancellationToken.None))
        {
            rows += batch.RowCount;
            await writer.WriteAsync(batch, CancellationToken.None);
        }

        await writer.CompleteAsync(CancellationToken.None);
        return rows;
    }

    private sealed class DiscardSink : ISegmentSink
    {
        public long Position { get; private set; }

        public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
        {
            Position += data.Length;
            return ValueTask.CompletedTask;
        }

        public ValueTask FlushAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }
}
