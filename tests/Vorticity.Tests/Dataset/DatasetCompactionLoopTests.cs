// The background driver: a loop that plans, runs the job due for it, paces what it wrote, and
// sleeps when nothing is due, until it is cancelled.
//
// A LOOP IS ONLY EVER A CALLER OF CompactAsync'S JOB, so what these tests hold it to is what it adds:
// that it drains a dataset and then waits instead of spinning, that loops which know one another
// take distinct jobs from one plan, that loops which cannot count one another lease a job's levels,
// that the leases a store keeps are swept once they end, and that the pace is the rate it was given.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.Tests.Scan;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed class DatasetCompactionLoopTests
{
    private static readonly string[] Cities = ["Paris", "Lyon", "Nice", "Lille"];

    private static readonly CompactionOptions Compaction = new CompactionOptions { LevelZeroCeiling = 2, TargetBytesAtLevelOne = 1 << 20 };

    [Fact]
    public async Task ALoopDrainsWhatIsDueAndThenWaits()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using MemoryObjectStore inner = new MemoryObjectStore();
        await using CountingObjectStore store = new CountingObjectStore(inner);
        await using VortexDataset dataset = await CreateAsync(store, 6, ct);
        Assert.True((await dataset.PlanCompactionAsync(Compaction, ct)).HasWork);

        ConcurrentQueue<CompactionResult> ran = new ConcurrentQueue<CompactionResult>();
        using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task loop = dataset.RunCompactionAsync(
            new CompactionSchedule { Compaction = Compaction, Idle = TimeSpan.FromMilliseconds(40), Progress = new Recorder(ran) },
            stop.Token);
        await UntilDrainedAsync(dataset, ct);

        // Drained, it sleeps and asks again: a while later it has run nothing more, and asked the
        // store a head request or so a turn rather than spinning.
        int drained = ran.Count;
        store.Reset();
        await Task.Delay(200, ct);
        Assert.Equal(drained, ran.Count);
        Assert.InRange(store.Requests, 0, 12);
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => loop);

        Assert.NotEmpty(ran);
        Assert.All(ran, result => Assert.Equal(OperationOutcome.Applied, result.Outcome));
        Assert.Equal(0, dataset.Lag);
        Assert.Equal(6 * 50, await RowsAsync(dataset, ct));
        Assert.True((await dataset.VerifyAsync(cancellationToken: ct)).Holds);
    }

    [Fact]
    public async Task ALoopHandsItsTaskBackBeforeItRunsAJob()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await CreateAsync(store, 6, ct);
        ConcurrentQueue<CompactionResult> ran = new ConcurrentQueue<CompactionResult>();
        using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // A store that answers at once never suspends a job: what the caller gets back first is the task.
        using ManualResetEventSlim gate = new ManualResetEventSlim();
        Task loop = dataset.RunCompactionAsync(
            new CompactionSchedule { Compaction = Compaction, Idle = TimeSpan.FromMilliseconds(5), Progress = new Gated(ran, gate) },
            stop.Token);
        Assert.Empty(ran);
        gate.Set();
        await UntilDrainedAsync(dataset, ct);
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => loop);
    }

    [Fact]
    public async Task TwoLoopsThatKnowEachOtherDrainADatasetTogether()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset first = await CreateAsync(store, 9, ct);
        await using VortexDataset second = await VortexDataset.OpenAsync(store, Options(), ct);

        using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task[] loops =
        [
            first.RunCompactionAsync(new CompactionSchedule { Compaction = Compaction, Idle = TimeSpan.FromMilliseconds(5), Loops = 2, Loop = 0 }, stop.Token),
            second.RunCompactionAsync(new CompactionSchedule { Compaction = Compaction, Idle = TimeSpan.FromMilliseconds(5), Loops = 2, Loop = 1 }, stop.Token),
        ];
        await UntilDrainedAsync(first, ct);
        stop.Cancel();
        foreach (Task loop in loops)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => loop);
        }

        await first.RefreshAsync(ct);
        Assert.Equal(9 * 50, await RowsAsync(first, ct));
        Assert.True((await first.VerifyAsync(cancellationToken: ct)).Holds);
    }

    [Fact]
    public async Task EachLoopTakesTheJobRankedAtItsIndex()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using MemoryObjectStore store = new MemoryObjectStore();
        CompactionPlan plan = new CompactionPlan { Jobs = [Job(0, 1), Job(2, 3)] };

        CompactionJob? zero = await CompactionLoop.ChooseAsync(store, new CompactionSchedule { Loops = 2, Loop = 0 }, plan, new LeaseBook(), ct);
        CompactionJob? one = await CompactionLoop.ChooseAsync(store, new CompactionSchedule { Loops = 2, Loop = 1 }, plan, new LeaseBook(), ct);
        CompactionJob? two = await CompactionLoop.ChooseAsync(store, new CompactionSchedule { Loops = 3, Loop = 2 }, plan, new LeaseBook(), ct);
        Assert.Same(plan.Jobs[0], zero);
        Assert.Same(plan.Jobs[1], one);
        Assert.Null(two);
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public async Task ALoopThatLeasesTakesTheNextJobWhenAnotherHoldsALevel()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using MemoryObjectStore store = new MemoryObjectStore();
        ManualClock clock = new ManualClock(new DateTimeOffset(2026, 9, 18, 12, 0, 30, TimeSpan.Zero));
        CompactionSchedule leasing = new CompactionSchedule { UseLeases = true, TimeProvider = clock };
        CompactionPlan plan = new CompactionPlan { Jobs = [Job(0, 1), Job(1, 2), Job(3, 3)] };

        // The first loop leases the first job's levels; the second finds level 1 held, skips the job
        // that shares it, and takes the third; a third loop finds nothing it can lease this minute.
        LeaseBook first = new LeaseBook();
        Assert.Same(plan.Jobs[0], await CompactionLoop.ChooseAsync(store, leasing, plan, first, ct));
        Assert.Same(plan.Jobs[2], await CompactionLoop.ChooseAsync(store, leasing, plan, new LeaseBook(), ct));
        Assert.Null(await CompactionLoop.ChooseAsync(store, leasing, plan, new LeaseBook(), ct));
        long end = CompactionLoop.LeaseEnd(clock.Now, leasing.LeaseSpan);
        Assert.Equal(new DateTimeOffset(2026, 9, 18, 12, 1, 0, TimeSpan.Zero).ToUnixTimeSeconds(), end);
        Assert.Equal(
            [CompactionLoop.LeaseKey(0, end), CompactionLoop.LeaseKey(1, end), CompactionLoop.LeaseKey(3, end)],
            (await store.ListAllAsync(CompactionLoop.LeasePrefix, ct)).Order(StringComparer.Ordinal));

        // The first loop holds its levels for the rest of the minute, and runs their next job.
        Assert.Same(plan.Jobs[0], await CompactionLoop.ChooseAsync(store, leasing, plan, first, ct));
        Assert.Equal(3, (await store.ListAllAsync(CompactionLoop.LeasePrefix, ct)).Count);

        // The next minute, nothing is held.
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Same(plan.Jobs[0], await CompactionLoop.ChooseAsync(store, leasing, plan, new LeaseBook(), ct));
    }

    [Fact]
    public async Task VacuumDeletesTheLeasesThatEndedAWindowAgo()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        ManualClock clock = new ManualClock(new DateTimeOffset(2026, 9, 18, 12, 0, 0, TimeSpan.Zero));
        await using MemoryObjectStore store = new MemoryObjectStore { TimeProvider = clock };
        await using VortexDataset dataset = await CreateAsync(store, 1, ct);
        long old = CompactionLoop.LeaseEnd(clock.Now - TimeSpan.FromHours(3), TimeSpan.FromMinutes(1));
        long current = CompactionLoop.LeaseEnd(clock.Now, TimeSpan.FromMinutes(1));
        Assert.True(await CompactionLoop.TryLeaseAsync(store, Job(0, 1), old, dataset.Version, null, ct));
        Assert.True(await CompactionLoop.TryLeaseAsync(store, Job(0, 1), current, dataset.Version, null, ct));

        VacuumResult vacuumed = await dataset.VacuumAsync(new VacuumOptions { TimeProvider = clock }, ct);
        Assert.Equal([CompactionLoop.LeaseKey(0, old), CompactionLoop.LeaseKey(1, old)], vacuumed.Deleted.Where(key => key.StartsWith(CompactionLoop.LeasePrefix, StringComparison.Ordinal)));
        Assert.Equal(
            [CompactionLoop.LeaseKey(0, current), CompactionLoop.LeaseKey(1, current)],
            (await store.ListAllAsync(CompactionLoop.LeasePrefix, ct)).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task ALoopPacedBelowWhatAJobWroteWaitsBeforeItsNext()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await CreateAsync(store, 6, ct);
        ConcurrentQueue<CompactionResult> ran = new ConcurrentQueue<CompactionResult>();
        using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task loop = dataset.RunCompactionAsync(
            new CompactionSchedule { Compaction = Compaction, Idle = TimeSpan.FromMilliseconds(5), BytesPerSecond = 1, Progress = new Recorder(ran) },
            stop.Token);
        await UntilDrainedAsync(dataset, ct);

        // A byte a second, after a job that wrote kilobytes: level 0 fills again and waits.
        await CreateMoreAsync(dataset, 3, ct);
        await Task.Delay(200, ct);
        Assert.Single(ran);
        Assert.True((await dataset.PlanCompactionAsync(Compaction, ct)).HasWork);
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => loop);
    }

    [Fact]
    public void ALoopWaitsUntilWhatAJobWroteFitsItsRate()
    {
        CompactionSchedule paced = new CompactionSchedule { BytesPerSecond = 1 << 20 };
        Assert.Equal(TimeSpan.FromSeconds(6), paced.PauseAfter(10L << 20, TimeSpan.FromSeconds(4)));
        Assert.Equal(TimeSpan.Zero, paced.PauseAfter(10L << 20, TimeSpan.FromSeconds(12)));
        Assert.Equal(TimeSpan.Zero, new CompactionSchedule().PauseAfter(10L << 20, TimeSpan.Zero));
    }

    [Fact]
    public async Task AScheduleOutOfRangeIsRefusedBeforeAnythingRuns()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await CreateAsync(store, 1, ct);
        foreach (CompactionSchedule wrong in (CompactionSchedule[])
            [
                new CompactionSchedule { Loops = 2, Loop = 2 },
                new CompactionSchedule { Loop = -1 },
                new CompactionSchedule { Loops = 0 },
                new CompactionSchedule { Idle = TimeSpan.Zero },
                new CompactionSchedule { BytesPerSecond = -1 },
                new CompactionSchedule { UseLeases = true, LeaseSpan = TimeSpan.FromMilliseconds(10) },
            ])
        {
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => dataset.RunCompactionAsync(wrong, ct));
        }
    }

    /// <summary>A job reading one object at <paramref name="from"/> and writing <paramref name="to"/>, for a plan made by hand.</summary>
    private static CompactionJob Job(int from, int to) =>
        new CompactionJob(
            from,
            to,
            CompactionStyle.Leveled,
            CompactionTrigger.LevelSize,
            [new CompactionInput(from, new byte[] { (byte)from }, new ObjectEntry(CommitKey.ForData($"{from:x8}"), (UInt128)(from + 1), 10, 100, UInt128.Zero))],
            1 << 20,
            0);

    /// <summary>Waits until the handle's version has no compaction due.</summary>
    private static async Task UntilDrainedAsync(VortexDataset dataset, CancellationToken ct)
    {
        using CancellationTokenSource patience = CancellationTokenSource.CreateLinkedTokenSource(ct);
        patience.CancelAfter(TimeSpan.FromSeconds(60));
        while ((await dataset.PlanCompactionAsync(Compaction, patience.Token)).HasWork)
        {
            await Task.Delay(5, patience.Token);
            await dataset.RefreshAsync(patience.Token);
        }
    }

    private static async Task<long> RowsAsync(VortexDataset dataset, CancellationToken ct) =>
        (await dataset.Scan<ChangeRow>().ToRecordsAsync(ct).ToListAsync(ct)).Count;

    private static DatasetOptions Options() => new DatasetOptions
    {
        Seed = 0x100_9ED,
        ClusteringKey = ["Key"],
        RetentionWindow = TimeSpan.FromHours(1),
        Write = new VortexWriteOptions { RowBlockSize = 64, DataBlockTargetBytes = 2 << 10 },
    };

    /// <summary>A clustered dataset of <paramref name="appends"/> objects of fifty rows, each in level 0.</summary>
    private static async Task<VortexDataset> CreateAsync(IObjectStore store, int appends, CancellationToken ct)
    {
        Decoders.EnsureRegistered();
        VortexDataset dataset = await VortexDataset.CreateAsync(store, ChangeRow.Schema, Options(), ct);
        await CreateMoreAsync(dataset, appends, ct);
        return dataset;
    }

    /// <summary>Appends <paramref name="appends"/> objects of fifty rows after every key the dataset holds.</summary>
    private static async Task CreateMoreAsync(VortexDataset dataset, int appends, CancellationToken ct)
    {
        long from = dataset.RowCount;
        for (int append = 0; append < appends; append++)
        {
            ChangeRow[] rows = [.. Enumerable.Range(0, 50).Select(i => new ChangeRow(((from / 50) + append) * 1_000L + i, i * 0.5, Cities[i % Cities.Length]))];
            await using ObjectDraft draft = dataset.StartObject();
            await draft.Writer.WriteAsync<ChangeRow>(rows, ct);
            await dataset.AppendAsync(draft, ct);
        }
    }

    /// <summary>Keeps what a loop reports, once the test lets it.</summary>
    private sealed class Gated(ConcurrentQueue<CompactionResult> into, ManualResetEventSlim gate) : IProgress<CompactionResult>
    {
        public void Report(CompactionResult value)
        {
            gate.Wait(TimeSpan.FromSeconds(30));
            into.Enqueue(value);
        }
    }

    /// <summary>Keeps what a loop reports, on the thread that reports it.</summary>
    private sealed class Recorder(ConcurrentQueue<CompactionResult> into) : IProgress<CompactionResult>
    {
        public void Report(CompactionResult value) => into.Enqueue(value);
    }
}
