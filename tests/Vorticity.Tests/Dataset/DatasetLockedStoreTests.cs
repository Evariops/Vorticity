// A dataset on a store that keeps every object under a retention lock, as S3 Object Lock, Azure
// immutable blob storage and a GCS bucket lock do.
//
// THE STORE KEEPS EVERY BYTE WRITTEN FOR THE LOCK'S TERM, so the profile is about writing fewer: a
// delete marks rather than rewrites, compaction holds the read bounds and purges nothing, and a
// vacuum deletes nothing the lock still keeps. The memory store plays the lock here: a head reports
// the date, and a delete before it is refused, which is what a vacuum that ignored the date would meet.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.Tests.Scan;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed class DatasetLockedStoreTests
{
    private static readonly string[] Cities = ["Paris", "Lyon", "Nice", "Lille"];

    [Fact]
    public async Task ADatasetOnALockedStoreSaysSoInEveryHeaderAndMergesAtAFanoutOfAHundred()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using (VortexDataset created = await CreateAsync(store, Options() with { LockedStore = true }, 1, ct))
        {
            await AppendAsync(created, 1, ct);
        }

        // Every handle takes the profile from the dataset, whatever its own options say.
        await using VortexDataset opened = await VortexDataset.OpenAsync(store, Options(), ct);
        Assert.True(opened.LockedStore);
        Assert.True(opened.Snapshot.Header.LockedStore);
        Assert.Equal(DatasetOptions.LockedFanout, opened.Compaction.Fanout);
        Assert.Equal((0L, 1), (opened.Options.MarkedObjectBytes, opened.Options.MarkedShare));

        await using MemoryObjectStore plain = new MemoryObjectStore();
        await using (VortexDataset created = await CreateAsync(plain, Options(), 1, ct))
        {
        }

        await using VortexDataset unlocked = await VortexDataset.OpenAsync(plain, Options() with { LockedStore = true }, ct);
        Assert.False(unlocked.LockedStore);
        Assert.False(unlocked.Options.LockedStore);
        Assert.Equal(0, unlocked.Compaction.Fanout);
    }

    [Fact]
    public async Task ADeleteOnALockedStoreMarksWhateverTheObjectsSizeOrTheShareItTakes()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await CreateAsync(store, Options() with { LockedStore = true }, 2, ct);

        // Objects of a few kilobytes, which a delete elsewhere rewrites, and most of one's rows.
        RowChangeResult most = await dataset.DeleteAsync<ChangeRow>(r => r.Key < 40, ct);
        Assert.Equal((40L, 1L, 0L), (most.Rows, most.ObjectsMarked, most.ObjectsOut));

        // Every row of an object: marked whole, it leaves the tree, and its bytes stay in the store.
        RowChangeResult all = await dataset.DeleteAsync<ChangeRow>(r => r.Key >= 1_000, ct);
        Assert.Equal((50L, 0L), (all.Rows, all.ObjectsOut));
        Assert.Equal(10, await RowsAsync(dataset, ct));
    }

    [Fact]
    public async Task CompactionOnALockedStorePurgesNoMarks()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        CompactionOptions compaction = new CompactionOptions { LevelZeroCeiling = 8 };
        foreach (bool locked in (bool[])[false, true])
        {
            await using MemoryObjectStore store = new MemoryObjectStore();
            await using VortexDataset dataset = await CreateAsync(
                store, Options() with { LockedStore = locked, MarkedObjectBytes = 0, MarkedShare = 1 }, 1, ct);
            Assert.Equal(1, (await dataset.DeleteAsync<ChangeRow>(r => r.Key < 30, ct)).ObjectsMarked);

            // Marks on most of the object's rows: due for a purge, which only the lock forgoes.
            CompactionPlan plan = await dataset.PlanCompactionAsync(compaction, ct);
            Assert.Equal(locked ? CompactionTrigger.None : CompactionTrigger.Marks, plan.Job?.Trigger ?? CompactionTrigger.None);
        }
    }

    [Fact]
    public async Task VacuumLeavesWhatTheLockKeepsAndSaysWhenItMayGo()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        ManualClock clock = new ManualClock(new DateTimeOffset(2026, 9, 18, 12, 0, 0, TimeSpan.Zero));
        await using MemoryObjectStore store = new MemoryObjectStore { TimeProvider = clock, RetainFor = TimeSpan.FromDays(30) };
        DatasetOptions options = Options() with { LockedStore = true, RetentionWindow = TimeSpan.FromHours(1), TimeProvider = clock };
        await using VortexDataset dataset = await CreateAsync(store, options, 0, ct);
        DateTimeOffset first = clock.Now;
        for (int append = 0; append < 3; append++)
        {
            // A minute apart, so that the locks end a minute apart too.
            clock.Advance(TimeSpan.FromMinutes(1));
            await AppendAsync(dataset, append, ct);
        }

        Assert.NotNull(await dataset.CompactAsync(new CompactionOptions { LevelZeroCeiling = 2, TargetBytesAtLevelOne = 1 << 20 }, ct));
        long lease = CompactionLoop.LeaseEnd(clock.Now, TimeSpan.FromMinutes(1));
        Assert.True(await CompactionLoop.TryLeaseAsync(store, new CompactionJob(0, 1, CompactionStyle.Leveled, CompactionTrigger.LevelSize, [], 0, 0), lease, dataset.Version, null, ct));

        // Past the window, every superseded object and the lease are garbage, and every one is locked.
        clock.Advance(TimeSpan.FromHours(2));
        VacuumResult early = await dataset.VacuumAsync(new VacuumOptions { TimeProvider = clock }, ct);
        Assert.Empty(early.Deleted);
        Assert.Contains(CompactionLoop.LeaseKey(0, lease), early.Locked);
        Assert.Contains(early.Locked, key => key.StartsWith(CommitKey.DataPrefix, StringComparison.Ordinal));
        Assert.Contains(early.Locked, key => key.StartsWith(CommitKey.Prefix, StringComparison.Ordinal));
        Assert.Equal(first + TimeSpan.FromDays(30), early.NextUnlock);

        // Once the lock passes, the same objects go, save one a legal hold keeps, which has no date.
        string held = early.Locked.First(key => key.StartsWith(CommitKey.DataPrefix, StringComparison.Ordinal));
        store.Hold(held, held: true);
        clock.Advance(TimeSpan.FromDays(30));
        VacuumResult late = await dataset.VacuumAsync(new VacuumOptions { TimeProvider = clock }, ct);
        Assert.Equal([held], late.Locked);
        Assert.Null(late.NextUnlock);
        Assert.Equal(early.Locked.Length - 1, late.Deleted.Length);
        Assert.Equal(150, await RowsAsync(dataset, ct));
    }

    private static DatasetOptions Options() => new DatasetOptions
    {
        Seed = 0x10_C4ED,
        ClusteringKey = ["Key"],
        Write = new VortexWriteOptions { RowBlockSize = 64, DataBlockTargetBytes = 2 << 10 },
    };

    /// <summary>A clustered dataset of <paramref name="appends"/> objects of fifty rows.</summary>
    private static async Task<VortexDataset> CreateAsync(IObjectStore store, DatasetOptions options, int appends, CancellationToken ct)
    {
        Decoders.EnsureRegistered();
        VortexDataset dataset = await VortexDataset.CreateAsync(store, ChangeRow.Schema, options, ct);
        for (int append = 0; append < appends; append++)
        {
            await AppendAsync(dataset, append, ct);
        }

        return dataset;
    }

    private static async Task AppendAsync(VortexDataset dataset, int append, CancellationToken ct)
    {
        ChangeRow[] rows = [.. Enumerable.Range(0, 50).Select(i => new ChangeRow((append * 1_000L) + i, i * 0.5, Cities[i % Cities.Length]))];
        await using ObjectDraft draft = dataset.StartObject();
        await draft.Writer.WriteAsync<ChangeRow>(rows, ct);
        await dataset.AppendAsync(draft, ct);
    }

    private static async Task<long> RowsAsync(VortexDataset dataset, CancellationToken ct) =>
        (await dataset.Scan<ChangeRow>().ToRecordsAsync(ct).ToListAsync(ct)).Count;
}
