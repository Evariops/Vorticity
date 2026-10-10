using System;
using System.Collections.Generic;
using System.Globalization;
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
    /// Each wake is a refresh, one head request, and a plan, which reads the pages the handle does
    /// not hold already.
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

    /// <summary>
    /// Whether the loop leases a job's levels before it runs the job, for deployments that run loops
    /// they cannot count. A lease is an object created by put-if-absent under a level and the end of
    /// the current <see cref="LeaseSpan"/>: the loop that creates it holds the level until then, and
    /// runs every job of its levels it chooses meanwhile; the others take the next job. A loop that
    /// leased some of a job's levels and found another holding the rest holds those it leased all the
    /// same, until the span ends. Nothing releases a lease, so that a store that refuses deletes still
    /// works. Vacuum deletes the leases that ended before its window.
    /// </summary>
    public bool UseLeases { get; init; }

    /// <summary>
    /// How long a lease holds its levels, the same for every loop of a dataset; a minute by default.
    /// A job still running when its lease ends may meet another loop's job on its levels, and one of
    /// the two is then abandoned, so the span outlasts most jobs.
    /// </summary>
    public TimeSpan LeaseSpan { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>Told of every compaction the loop ran, abandoned ones included.</summary>
    public IProgress<CompactionResult>? Progress { get; init; }

    /// <summary>The clock the loop sleeps, paces and dates its leases by; the system's by default.</summary>
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
        ArgumentOutOfRangeException.ThrowIfLessThan(LeaseSpan, TimeSpan.FromSeconds(1), nameof(LeaseSpan));
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
    /// <summary>Where leases live, under the dataset's prefix.</summary>
    internal const string LeasePrefix = "leases/";

    /// <summary>How many due jobs a leasing loop ranks, and tries in turn.</summary>
    private const int LeasedJobs = 4;

    /// <summary>Runs until <paramref name="cancellationToken"/> is cancelled, or a job fails.</summary>
    public static async Task RunAsync(VortexDataset dataset, CompactionSchedule schedule, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        ArgumentNullException.ThrowIfNull(schedule);
        schedule.Check();
        TimeProvider clock = schedule.TimeProvider;
        int ranked = schedule.UseLeases ? Math.Max(LeasedJobs, schedule.Loops) : schedule.Loop + 1;
        LeaseBook leases = new LeaseBook();

        // Off the caller's thread from the first turn: against a store that answers at once, the jobs
        // due would otherwise all run before the caller got its task back.
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await dataset.RefreshAsync(cancellationToken).ConfigureAwait(false);
            CompactionPlan plan = await CompactionPolicy.PlanAsync(dataset, schedule.Compaction, ranked, cancellationToken)
                .ConfigureAwait(false);
            CompactionJob? job = await ChooseAsync(dataset.Store, schedule, plan, leases, cancellationToken).ConfigureAwait(false);
            if (job is null)
            {
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

    /// <summary>
    /// The job this loop runs from the plan: the one ranked at its index, or, leasing, the first
    /// whose levels it holds or could lease; null when none is. <paramref name="leases"/> are the
    /// leases this loop created, which it holds without asking the store again.
    /// </summary>
    internal static async ValueTask<CompactionJob?> ChooseAsync(
        IObjectStore store, CompactionSchedule schedule, CompactionPlan plan, LeaseBook leases, CancellationToken cancellationToken)
    {
        if (!schedule.UseLeases)
        {
            return schedule.Loop < plan.Jobs.Length ? plan.Jobs[schedule.Loop] : null;
        }

        long end = LeaseEnd(schedule.TimeProvider.GetUtcNow(), schedule.LeaseSpan);
        leases.EndedBefore(end);
        for (int rank = 0; rank < plan.Jobs.Length; rank++)
        {
            CompactionJob job = plan.Jobs[(schedule.Loop + rank) % plan.Jobs.Length];
            if (await TryLeaseAsync(store, job, end, plan.Version, leases, cancellationToken).ConfigureAwait(false))
            {
                return job;
            }
        }

        return null;
    }

    /// <summary>
    /// Leases every level <paramref name="job"/> reads or writes until <paramref name="end"/>, lowest
    /// first, and says whether it holds them all: a level the loop leased already in this span is
    /// held, a level another loop holds stops it there, and the levels it leased before are held to
    /// the end of the span all the same. <paramref name="leases"/>, the loop's own, takes those it
    /// creates; null for a caller that keeps none.
    /// </summary>
    internal static async ValueTask<bool> TryLeaseAsync(
        IObjectStore store, CompactionJob job, long end, ulong version, LeaseBook? leases, CancellationToken cancellationToken)
    {
        SortedSet<int> levels = [job.FromLevel, job.ToLevel];
        foreach (CompactionInput input in job.Inputs)
        {
            levels.Add(input.Level);
        }

        byte[]? holder = null;
        foreach (int level in levels)
        {
            if (leases is not null && leases.Holds(level, end))
            {
                continue;
            }

            holder ??= System.Text.Encoding.ASCII.GetBytes(string.Create(CultureInfo.InvariantCulture, $"version {version}"));
            if (await store.PutIfAbsentAsync(LeaseKey(level, end), holder, cancellationToken).ConfigureAwait(false) != PutOutcome.Created)
            {
                return false;
            }

            leases?.Took(level, end);
        }

        return true;
    }

    /// <summary>The key of the lease on <paramref name="level"/> that ends at <paramref name="end"/>, in Unix seconds.</summary>
    internal static string LeaseKey(int level, long end) =>
        string.Create(CultureInfo.InvariantCulture, $"{LeasePrefix}{level:D3}/{end:D12}");

    /// <summary>When the span holding <paramref name="now"/> ends, in Unix seconds: every loop of one span shares it.</summary>
    internal static long LeaseEnd(DateTimeOffset now, TimeSpan span)
    {
        long seconds = Math.Max((long)span.TotalSeconds, 1);
        return ((now.ToUnixTimeSeconds() / seconds) + 1) * seconds;
    }

    /// <summary>When the lease under <paramref name="key"/> ends, in Unix seconds; false for a key that is no lease's.</summary>
    internal static bool TryParseEnd(string key, out long end)
    {
        end = 0;
        int slash = key.LastIndexOf('/');
        return key.StartsWith(LeasePrefix, StringComparison.Ordinal)
            && slash > LeasePrefix.Length
            && long.TryParse(key.AsSpan(slash + 1), NumberStyles.None, CultureInfo.InvariantCulture, out end);
    }
}

/// <summary>
/// The leases one compaction loop created, by level and the end of their span: the loop holds those
/// levels until then, and a job on them needs no lease of it again. Not shared between loops.
/// </summary>
internal sealed class LeaseBook
{
    private readonly HashSet<(int Level, long End)> _held = [];

    /// <summary>Whether the loop leased <paramref name="level"/> for the span ending at <paramref name="end"/>.</summary>
    internal bool Holds(int level, long end) => _held.Contains((level, end));

    /// <summary>Records a lease the loop created.</summary>
    internal void Took(int level, long end) => _held.Add((level, end));

    /// <summary>Forgets the leases whose span ended before the one ending at <paramref name="end"/>.</summary>
    internal void EndedBefore(long end) => _held.RemoveWhere(lease => lease.End < end);
}
