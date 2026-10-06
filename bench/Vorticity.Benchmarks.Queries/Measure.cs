using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Vorticity.Aggregating;

namespace Vorticity.Benchmarks.Queries;

/// <summary>What one run of a query reports while it runs: its answers, the first one timed.</summary>
internal sealed class Run
{
    private readonly Stopwatch _clock;
    private readonly long _baseline;
    private readonly int _probeEvery;
    private readonly Action<AggregationPlan>? _configure;
    private long _answers;

    internal Run(Stopwatch clock, long baseline, int probeEvery, Action<AggregationPlan>? configure = null)
    {
        _clock = clock;
        _baseline = baseline;
        _probeEvery = probeEvery;
        _configure = configure;
    }

    /// <summary>The time from the start to the first answer.</summary>
    internal TimeSpan? First { get; private set; }

    /// <summary>The most live managed bytes over the baseline seen at an answer, in a memory pass.</summary>
    internal long Live { get; private set; }

    /// <summary>The plan of the aggregation the query runs, when it says which: its last run is the engine's counts.</summary>
    internal AggregationPlan? Plan { get; private set; }

    /// <summary>Names the aggregation whose engine counts the bench reports, its switches set as the run asks.</summary>
    internal Aggregation Track(Aggregation aggregation)
    {
        Plan = ((AggregationQuery)aggregation.Query).Plan;
        _configure?.Invoke(Plan);
        return aggregation;
    }

    /// <summary>Names the aggregation whose engine counts the bench reports, its switches set as the run asks.</summary>
    internal Aggregation<T> Track<T>(Aggregation<T> aggregation)
    {
        Plan = ((AggregationQuery)aggregation.Query).Plan;
        _configure?.Invoke(Plan);
        return aggregation;
    }

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
/// <param name="Engine">The engine's counts in the best round, lane by lane, when the query tracks its aggregation.</param>
/// <param name="Gen2">The collections of the second generation over the timed rounds, per round.</param>
/// <param name="Faults">The minor page faults of the best round, -1 where the system does not count them.</param>
/// <param name="LargeBytes">The bytes allocated in the large object heap by the memory pass.</param>
internal readonly record struct Measurement(
    double Millis, double FirstMillis, long Allocated, long Live, long Result, AggregationRun? Engine = null, double Gen2 = 0, long Faults = -1, long LargeBytes = 0);

internal static class Measure
{
    /// <summary>The listener of the large object heap's allocations, made once: a memory pass listens to it.</summary>
    private static readonly LargeAllocations Large = new LargeAllocations();

    /// <summary>Runs <paramref name="query"/> a warm-up and <paramref name="rounds"/> timed rounds, then a memory pass.</summary>
    internal static async Task<Measurement> RunAsync(Func<Run, Task<long>> query, int rounds, int probeEvery)
    {
        // Warm-up: the JIT, the session's caches and the file's mapping are the steady state, not
        // what a query costs.
        Side side = new Side(null);
        await query(new Run(Stopwatch.StartNew(), 0, 0)).ConfigureAwait(false);
        for (int i = 0; i < rounds; i++)
        {
            await side.RoundAsync(query).ConfigureAwait(false);
        }

        return await side.FinishAsync(query, probeEvery).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs <paramref name="query"/> under two settings of a switch in one process: a warm-up each,
    /// then <paramref name="rounds"/> rounds each, in pairs whose order alternates (A B, B A, …), and
    /// a memory pass each. Each side keeps its best round: on a loaded machine, a burst of load lands
    /// on a round, not on a side, and the two share every cache, mapping and page the process holds.
    /// </summary>
    internal static async Task<(Measurement A, Measurement B)> CompareAsync(
        Func<Run, Task<long>> query, int rounds, int probeEvery, Action<AggregationPlan> a, Action<AggregationPlan> b)
    {
        Side first = new Side(a);
        Side second = new Side(b);
        await query(new Run(Stopwatch.StartNew(), 0, 0, a)).ConfigureAwait(false);
        await query(new Run(Stopwatch.StartNew(), 0, 0, b)).ConfigureAwait(false);
        for (int i = 0; i < rounds; i++)
        {
            await ((i & 1) == 0 ? first : second).RoundAsync(query).ConfigureAwait(false);
            await ((i & 1) == 0 ? second : first).RoundAsync(query).ConfigureAwait(false);
        }

        return (await first.FinishAsync(query, probeEvery).ConfigureAwait(false), await second.FinishAsync(query, probeEvery).ConfigureAwait(false));
    }

    /// <summary>One setting's rounds: the best so far, and what that round allocated, answered first and counted.</summary>
    private sealed class Side(Action<AggregationPlan>? configure)
    {
        private double _best = double.MaxValue;
        private double _first;
        private long _allocated;
        private long _faults = -1;
        private int _gen2;
        private int _rounds;
        private long _result;
        private AggregationRun? _engine;

        internal async Task RoundAsync(Func<Run, Task<long>> query)
        {
            // A continuation runs on the stack of the operation that completed it: until the stack
            // unwinds, the last run's state machines, and every state they hold, are rooted there.
            await Task.Yield();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            long before = GC.GetTotalAllocatedBytes(precise: true);
            int collections = GC.CollectionCount(2);
            long faults = ProcessCounters.MinorFaults();
            Stopwatch clock = Stopwatch.StartNew();
            Run run = new Run(clock, 0, 0, configure);
            long result = await query(run).ConfigureAwait(false);
            double millis = clock.Elapsed.TotalMilliseconds;
            long bytes = GC.GetTotalAllocatedBytes(precise: true) - before;
            faults = faults < 0 ? -1 : ProcessCounters.MinorFaults() - faults;
            _gen2 += GC.CollectionCount(2) - collections;
            _rounds++;
            _result = result;
            if (millis < _best)
            {
                _best = millis;
                _first = run.First?.TotalMilliseconds ?? millis;
                _allocated = bytes;
                _faults = faults;
                _engine = run.Plan?.LastRun;
            }
        }

        /// <summary>The memory pass, then the side's measure.</summary>
        internal async Task<Measurement> FinishAsync(Func<Run, Task<long>> query, int probeEvery)
        {
            await Task.Yield();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            long baseline = GC.GetTotalMemory(forceFullCollection: true);
            Run memory = new Run(Stopwatch.StartNew(), baseline, probeEvery, configure);
            Large.Start();
            await query(memory).ConfigureAwait(false);
            long large = Large.Stop();
            return new Measurement(_best, _first, _allocated, memory.Live, _result, _engine, _rounds > 0 ? (double)_gen2 / _rounds : 0, _faults, large);
        }
    }
}
