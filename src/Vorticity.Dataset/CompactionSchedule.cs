using System;
using System.Threading;
using System.Threading.Tasks;

namespace Vorticity.Dataset;

/// <summary>
/// How a compaction loop runs (<see cref="VortexDataset.RunCompactionAsync"/>): what it plans against,
/// how fast it may write, and how it shares a dataset with other loops.
/// </summary>
public sealed record CompactionSchedule
{
    /// <summary>The sizes and bounds the loop plans against; null for the defaults.</summary>
    public CompactionOptions? Compaction { get; init; }

    /// <summary>
    /// The rate the loop's compactions may write at, in bytes per second; 0, the default, paces
    /// nothing. After each job the loop waits until what the job wrote, over the time it took and the
    /// wait, fits the rate, so that compaction leaves the store's bandwidth to the writers.
    /// </summary>
    public long BytesPerSecond { get; init; }

    /// <summary>
    /// How long the loop sleeps when nothing is due for it before it asks again; a minute by default.
    /// Each wake is a refresh, one round trip, and a plan, which reads the pages the handle does not
    /// hold already.
    /// </summary>
    public TimeSpan Idle { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// How many loops share the dataset, each told its <see cref="Loop"/>; 1 by default. Every loop
    /// ranks the same due jobs, no two of which read or write one level, and takes the one whose rank
    /// is its index: loops spread over jobs rather than all racing for the first.
    /// </summary>
    public int Loops { get; init; } = 1;

    /// <summary>This loop's index among <see cref="Loops"/>, from 0.</summary>
    public int Loop { get; init; }

    /// <summary>Told of every compaction the loop ran, abandoned ones included.</summary>
    public IProgress<CompactionResult>? Progress { get; init; }

    /// <summary>The clock the loop sleeps and paces by; the system's by default.</summary>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    /// <summary>How long the loop waits once it has written <paramref name="written"/> bytes over <paramref name="elapsed"/>, to keep to the rate.</summary>
    internal TimeSpan PauseAfter(long written, TimeSpan elapsed)
    {
        if (BytesPerSecond <= 0 || written <= 0)
        {
            return TimeSpan.Zero;
        }

        TimeSpan owed = TimeSpan.FromSeconds((double)written / BytesPerSecond) - elapsed;
        return owed > TimeSpan.Zero ? owed : TimeSpan.Zero;
    }

    /// <summary>Throws unless the loop can run under this schedule.</summary>
    internal void Check()
    {
        ArgumentOutOfRangeException.ThrowIfNegative(BytesPerSecond, nameof(BytesPerSecond));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(Idle, TimeSpan.Zero, nameof(Idle));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(Loops, nameof(Loops));
        ArgumentOutOfRangeException.ThrowIfNegative(Loop, nameof(Loop));
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(Loop, Loops, nameof(Loop));
        ArgumentNullException.ThrowIfNull(TimeProvider, nameof(TimeProvider));
    }
}

/// <summary>
/// The background driver: plans, runs the job due for this loop, paces what it wrote, and sleeps
/// when nothing is due, until it is cancelled. A job is the one <see cref="VortexDataset.CompactAsync"/>
/// runs, so the loop is safe against every writer and every other loop: what it saves is the waste
/// of two loops running one job, one of which a commit abandons.
/// </summary>
internal static class CompactionLoop
{
    /// <summary>Runs until <paramref name="cancellationToken"/> is cancelled, or a job fails.</summary>
    public static async Task RunAsync(VortexDataset dataset, CompactionSchedule schedule, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        ArgumentNullException.ThrowIfNull(schedule);
        schedule.Check();
        TimeProvider clock = schedule.TimeProvider;

        // Off the caller's thread from the first turn: against a store that answers at once, the jobs
        // due would otherwise all run before the caller got its task back.
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await dataset.RefreshAsync(cancellationToken).ConfigureAwait(false);
            CompactionPlan plan = await CompactionPolicy.PlanAsync(dataset, schedule.Compaction, schedule.Loop + 1, cancellationToken)
                .ConfigureAwait(false);
            if (Choose(schedule, plan) is not { } job)
            {
                // Nothing to compact: the first loop sums what the levels settled into rollups, then sleeps.
                if (schedule.Loop == 0 && await DatasetRollups.RollUpAsync(dataset, cancellationToken, refreshed: true).ConfigureAwait(false) > 0)
                {
                    continue;
                }

                await Task.Delay(schedule.Idle, clock, cancellationToken).ConfigureAwait(false);
                continue;
            }

            long started = clock.GetTimestamp();
            CompactionResult result = await DatasetCompactor.RunAsync(dataset, job, cancellationToken).ConfigureAwait(false);
            schedule.Progress?.Report(result);
            TimeSpan pause = schedule.PauseAfter(result.BytesOut, clock.GetElapsedTime(started));
            if (pause > TimeSpan.Zero)
            {
                await Task.Delay(pause, clock, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>The job this loop runs from the plan: the one ranked at its index, or null when none is.</summary>
    internal static CompactionJob? Choose(CompactionSchedule schedule, CompactionPlan plan) =>
        schedule.Loop < plan.Jobs.Length ? plan.Jobs[schedule.Loop] : null;
}
