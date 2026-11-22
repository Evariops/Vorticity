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
    /// How many loops share the dataset, each told its <see cref="Loop"/>; 1 by default. Under tiers,
    /// each partition is dealt to one loop by the hash of its value, and a loop takes the jobs of its
    /// own partitions alone: no two loops ever run one job. Under levels, every loop ranks the same due
    /// jobs, no two of which read or write one level, and takes the one whose rank is its index.
    /// </summary>
    public int Loops { get; init; } = 1;

    /// <summary>This loop's index among <see cref="Loops"/>, from 0.</summary>
    public int Loop { get; init; }

    /// <summary>Told of every compaction the loop ran, abandoned ones included.</summary>
    public IProgress<CompactionResult>? Progress { get; init; }

    /// <summary>
    /// How often the loop runs a vacuum (<see cref="VortexDataset.VacuumAsync"/>) when nothing is due: what no version inside
    /// the retention window reads goes then, the commit objects of the versions past it among them, which only a vacuum's walk
    /// of the retained versions tells apart. Null, the default, runs one within the loop's first four idle turns, drawn at
    /// random so that loops started together spread, then one every half of the dataset's retention window, a quarter either
    /// way. <see cref="Timeout.InfiniteTimeSpan"/> runs none: the application runs its own. Only the first loop of a dataset
    /// runs them (<see cref="Loop"/> 0), and none runs on a store under retention locks, where a vacuum asks for a head per
    /// object to read its lock.
    /// </summary>
    public TimeSpan? VacuumEvery { get; init; }

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
        if (VacuumEvery is { } every && every != Timeout.InfiniteTimeSpan)
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(every, TimeSpan.Zero, nameof(VacuumEvery));
        }
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
        JobStoppedException? stopped = null;

        // The first vacuum within the first four idle turns, at random, so that loops started together spread: a period of
        // half the default window, 3.5 days, would never come for a maintainer restarted every day.
        DateTimeOffset vacuumAt = clock.GetUtcNow() + (schedule.Idle * (4 * Random.Shared.NextDouble()));
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await dataset.RefreshAsync(cancellationToken).ConfigureAwait(false);
            CompactionPlan plan = await CompactionPolicy.PlanAsync(
                    dataset, schedule.Compaction, schedule.Loop + 1, cancellationToken, loops: schedule.Loops, loop: schedule.Loop)
                .ConfigureAwait(false);
            if (Choose(schedule, plan) is not { } job)
            {
                // Nothing to compact: the first loop sums what the levels settled into rollups, then sleeps.
                if (schedule.Loop == 0 && await DatasetRollups.RollUpAsync(dataset, cancellationToken, refreshed: true).ConfigureAwait(false) > 0)
                {
                    continue;
                }

                // What the loop's jobs replaced, once no retained version reads it; and, when due, what no retained version
                // reads at all, commit objects included.
                await dataset.DeleteRetiredAsync(cancellationToken).ConfigureAwait(false);
                vacuumAt = await VacuumIfDueAsync(dataset, schedule, vacuumAt, cancellationToken).ConfigureAwait(false);
                await Task.Delay(schedule.Idle, clock, cancellationToken).ConfigureAwait(false);
                continue;
            }

            long started = clock.GetTimestamp();
            CompactionResult result;
            try
            {
                result = await DatasetCompactor.RunAsync(dataset, job, cancellationToken).ConfigureAwait(false);
            }
            catch (JobStoppedException now)
            {
                // Ended short of room with nothing it may commit: the next plan counts its inputs at what their files said,
                // and a job that ends so again with nothing learned fails the loop.
                dataset.StoppedShort(stopped, now);
                stopped = now;
                continue;
            }

            stopped = null;
            schedule.Progress?.Report(result);
            TimeSpan pause = schedule.PauseAfter(result.BytesOut, clock.GetElapsedTime(started));
            if (pause > TimeSpan.Zero)
            {
                await Task.Delay(pause, clock, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Runs a vacuum once <paramref name="at"/> has come, for the first loop of a dataset on a store under no retention lock
    /// (<see cref="CompactionSchedule.VacuumEvery"/>): when the next one is due. One that fails ends its activity as an error,
    /// and the loop goes on, the next a period away.
    /// </summary>
    private static async ValueTask<DateTimeOffset> VacuumIfDueAsync(VortexDataset dataset, CompactionSchedule schedule, DateTimeOffset at, CancellationToken cancellationToken)
    {
        DateTimeOffset now = schedule.TimeProvider.GetUtcNow();
        if (now < at || schedule.Loop != 0 || schedule.VacuumEvery == Timeout.InfiniteTimeSpan || dataset.LockedStore)
        {
            return at;
        }

        try
        {
            await dataset.VacuumAsync(new VacuumOptions { TimeProvider = dataset.Options.TimeProvider }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception failed) when (!(failed is OperationCanceledException && cancellationToken.IsCancellationRequested))
        {
            // Told by the vacuum's activity: a store that fails now may answer at the next.
        }

        // A quarter of the period either way: loops that vacuumed together drift apart.
        TimeSpan every = schedule.VacuumEvery ?? (DatasetVacuum.WindowOf(dataset.Snapshot.Header.Retention) / 2);
        return now + (every * (0.75 + (0.5 * Random.Shared.NextDouble())));
    }

    /// <summary>
    /// The job this loop runs from the plan: the first of a plan of its own partitions, the one ranked at its index of one
    /// that ranks every loop's, or null when none is.
    /// </summary>
    internal static CompactionJob? Choose(CompactionSchedule schedule, CompactionPlan plan) =>
        plan.Sharded ? (plan.Jobs.Length > 0 ? plan.Jobs[0] : null)
        : schedule.Loop < plan.Jobs.Length ? plan.Jobs[schedule.Loop] : null;
}
