using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace Vorticity.Benchmarks.Queries;

/// <summary>What one run of a query reports while it runs: its answers, the first one timed.</summary>
internal sealed class Run
{
    private readonly Stopwatch _clock;
    private readonly long _baseline;
    private readonly int _probeEvery;
    private long _answers;

    internal Run(Stopwatch clock, long baseline, int probeEvery)
    {
        _clock = clock;
        _baseline = baseline;
        _probeEvery = probeEvery;
    }

    /// <summary>The time from the start to the first answer.</summary>
    internal TimeSpan? First { get; private set; }

    /// <summary>The most live managed bytes over the baseline seen at an answer, in a memory pass.</summary>
    internal long Live { get; private set; }

    /// <summary>Called for every answer the query hands out: a batch, a group, a row.</summary>
    internal void Answer()
    {
        if (First is null)
        {
            First = _clock.Elapsed;
        }

        if (_probeEvery > 0 && (_answers % _probeEvery) == 0)
        {
            // Not timed: a memory pass forces a collection at the answer to see what the query holds.
            Live = Math.Max(Live, GC.GetTotalMemory(forceFullCollection: true) - _baseline);
        }

        _answers++;
    }
}

/// <summary>A query measured: its best time over the rounds, its first answer, what it allocated and held.</summary>
/// <param name="Millis">The best time of the rounds.</param>
/// <param name="FirstMillis">The time to the first answer, in the best round.</param>
/// <param name="Allocated">Managed bytes allocated by every thread in the best round.</param>
/// <param name="Live">The most live managed bytes held at an answer, from the memory pass.</param>
/// <param name="Result">What the query returned, so that the work cannot be skipped and two sides can be compared.</param>
internal readonly record struct Measurement(double Millis, double FirstMillis, long Allocated, long Live, long Result);

internal static class Measure
{
    /// <summary>Runs <paramref name="query"/> a warm-up and <paramref name="rounds"/> timed rounds, then a memory pass.</summary>
    internal static async Task<Measurement> RunAsync(Func<Run, Task<long>> query, int rounds, int probeEvery)
    {
        // Warm-up: the JIT, the session's caches and the file's mapping are the steady state, not
        // what a query costs.
        await query(new Run(Stopwatch.StartNew(), 0, 0)).ConfigureAwait(false);


        double best = double.MaxValue;
        double first = 0;
        long allocated = 0;
        long result = 0;
        for (int i = 0; i < rounds; i++)
        {
            // A continuation runs on the stack of the operation that completed it: until the stack
            // unwinds, the last run's state machines, and every state they hold, are rooted there.
            await Task.Yield();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            long before = GC.GetTotalAllocatedBytes(precise: true);
            Stopwatch clock = Stopwatch.StartNew();
            Run run = new Run(clock, 0, 0);
            result = await query(run).ConfigureAwait(false);
            double millis = clock.Elapsed.TotalMilliseconds;
            long bytes = GC.GetTotalAllocatedBytes(precise: true) - before;
            if (millis < best)
            {
                best = millis;
                first = run.First?.TotalMilliseconds ?? millis;
                allocated = bytes;
            }
        }

        await Task.Yield();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        long baseline = GC.GetTotalMemory(forceFullCollection: true);
        Run memory = new Run(Stopwatch.StartNew(), baseline, probeEvery);
        await query(memory).ConfigureAwait(false);
        return new Measurement(best, first, allocated, memory.Live, result);
    }
}
