// The level-0 compaction a commit runs before it returns, when the dataset asks for one.
//
// WHAT INLINE MODE PROMISES is narrow: a commit that takes level 0 past its ceiling merges it into the
// level above when the job reads no more than the budget, and leaves it for another driver
// otherwise; and the commit stands whatever befalls that merge. The tests hold it to both halves,
// the second with a store that refuses to read a data object once the commit is in.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.Tests.Scan;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed class DatasetInlineCompactionTests
{
    private static readonly string[] Cities = ["Paris", "Lyon", "Nice", "Lille"];

    [Fact]
    public async Task AnAppendThatTakesLevelZeroPastItsCeilingCompactsItBeforeItReturns()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await CreateAsync(store, Options() with { InlineCompactionBytes = 1 << 20 }, ct);
        for (int append = 0; append < DatasetLevels.DefaultLevelZeroCeiling; append++)
        {
            await AppendAsync(dataset, append, ct);
            Assert.Equal(append + 1, dataset.Levels[0].Entries);
        }

        // The ninth takes level 0 past its ceiling: its commit, then the compaction's.
        ulong appended = await AppendAsync(dataset, DatasetLevels.DefaultLevelZeroCeiling, ct);
        Assert.Equal(appended + 1, dataset.Version);
        Assert.Equal((0L, 0L), (dataset.Lag, dataset.Levels[0].Entries));
        Assert.Equal((DatasetLevels.DefaultLevelZeroCeiling + 1) * 50, await RowsAsync(dataset, ct));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1_000)]
    public async Task AJobPastTheBudgetIsLeftToAnotherDriver(long budget)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await CreateAsync(store, Options() with { InlineCompactionBytes = budget }, ct);
        for (int append = 0; append <= DatasetLevels.DefaultLevelZeroCeiling; append++)
        {
            await AppendAsync(dataset, append, ct);
        }

        Assert.Equal(1, dataset.Lag);
        Assert.NotNull(await dataset.CompactAsync(null, ct));
        Assert.Equal(0, dataset.Lag);
    }

    [Fact]
    public async Task AnUpdateThatTakesLevelZeroPastItsCeilingCompactsItToo()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await CreateAsync(store, Options() with { InlineCompactionBytes = 1 << 20 }, ct);
        for (int append = 0; append < DatasetLevels.DefaultLevelZeroCeiling; append++)
        {
            await AppendAsync(dataset, append, ct);
        }

        // An update writes the rows it changed as a new object of level 0.
        RowChangeResult changed = await dataset.UpdateAsync<ChangeRow>(r => r.Key < 10, row => row with { Measure = -1.0 }, ct);
        Assert.Equal(OperationOutcome.Applied, changed.Outcome);
        Assert.Equal(changed.Version + 1, dataset.Version);
        Assert.Equal(0, dataset.Lag);
        Assert.Equal(10, (await dataset.Scan<ChangeRow>().Where(r => r.Measure == -1.0).ToRecordsAsync(ct).ToListAsync(ct)).Count);
    }

    [Fact]
    public async Task AReplacementThatTakesLevelZeroPastItsCeilingCompactsItToo()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await CreateAsync(store, Options() with { InlineCompactionBytes = 1 << 20 }, ct);
        for (int append = 0; append < DatasetLevels.DefaultLevelZeroCeiling; append++)
        {
            await AppendAsync(dataset, append, ct);
        }

        ChangeRow[] rows = [.. Enumerable.Range(0, 50).Select(i => new ChangeRow(90_000L + i, 1.0, Cities[i % Cities.Length]))];
        ObjectDraft draft = dataset.StartObject();
        await draft.Writer.WriteAsync<ChangeRow>(rows, ct);
        ReplaceResult replaced = await dataset.ReplaceObjectsAsync([], [draft], ct);
        Assert.Equal((OperationOutcome.Applied, replaced.Version + 1), (replaced.Outcome, dataset.Version));
        Assert.Equal(0, dataset.Lag);
    }

    [Fact]
    public async Task ACommitStandsWhenTheCompactionAfterItFails()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await CreateAsync(store, Options() with { InlineCompactionBytes = 1 << 20 }, ct);
        for (int append = 0; append < DatasetLevels.DefaultLevelZeroCeiling; append++)
        {
            await AppendAsync(dataset, append, ct);
        }

        // Writing and committing read no data object; the merge after the commit reads them all.
        Assert.Null(dataset.LastInlineCompactionFailure);
        store.Fails = (operation, key) => operation == ObjectOperation.GetRange && key.StartsWith(CommitKey.DataPrefix, StringComparison.Ordinal);
        ulong appended = await AppendAsync(dataset, DatasetLevels.DefaultLevelZeroCeiling, ct);
        Assert.Equal((appended, 1L), (dataset.Version, dataset.Lag));

        // The caller is told the commit's version, and the handle what the compaction raised.
        Assert.NotNull(dataset.LastInlineCompactionFailure);

        // The next commit, or another driver, takes the job up again.
        store.Fails = null;
        Assert.NotNull(await dataset.CompactAsync(null, ct));
        Assert.Equal((DatasetLevels.DefaultLevelZeroCeiling + 1) * 50, await RowsAsync(dataset, ct));
    }

    [Fact]
    public async Task ACommitWhileTheHandleCompactsAfterAnotherLeavesTheJobToIt()
    {
        // The first commit's compaction is held reading its inputs; a second commit takes level 0
        // past its ceiling too, and leaves the job to the compaction under way rather than writing
        // the same merge, of which one would then be abandoned.
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using MemoryObjectStore inner = new MemoryObjectStore();
        await using HeldReads store = new HeldReads(inner);
        await using VortexDataset dataset = await CreateAsync(store, Options() with { InlineCompactionBytes = 1 << 20 }, ct);
        for (int append = 0; append < DatasetLevels.DefaultLevelZeroCeiling; append++)
        {
            await AppendAsync(dataset, append, ct);
        }

        int before = (await inner.ListAllAsync(CommitKey.DataPrefix, ct)).Count;
        store.Holding = true;
        Task<ulong> first = AppendAsync(dataset, DatasetLevels.DefaultLevelZeroCeiling, ct);
        await store.Reached.Task.WaitAsync(ct);
        await AppendAsync(dataset, DatasetLevels.DefaultLevelZeroCeiling + 1, ct).WaitAsync(TimeSpan.FromSeconds(30), ct);
        store.Release.SetResult();
        await first;

        // Two appended objects and one merge's output.
        Assert.Equal(before + 3, (await inner.ListAllAsync(CommitKey.DataPrefix, ct)).Count);
        Assert.Null(dataset.LastInlineCompactionFailure);
        Assert.Equal((DatasetLevels.DefaultLevelZeroCeiling + 2) * 50, await RowsAsync(dataset, ct));
    }

    /// <summary>A store whose first read of a data object, once asked to, waits until the test releases it.</summary>
    private sealed class HeldReads(IObjectStore inner) : IObjectStore
    {
        internal bool Holding { get; set; }

        internal TaskCompletionSource Reached { get; } = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource Release { get; } = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<ObjectRange> GetRangeAsync(string key, long offset, int length, CancellationToken cancellationToken)
        {
            if (Holding && key.StartsWith(CommitKey.DataPrefix, StringComparison.Ordinal))
            {
                Holding = false;
                Reached.SetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }

            return await inner.GetRangeAsync(key, offset, length, cancellationToken);
        }

        public ValueTask<ObjectHead?> HeadAsync(string key, CancellationToken cancellationToken) =>
            inner.HeadAsync(key, cancellationToken);

        public ValueTask<PutOutcome> PutIfAbsentAsync(string key, System.IO.Pipelines.PipeReader content, long length, CancellationToken cancellationToken) =>
            inner.PutIfAbsentAsync(key, content, length, cancellationToken);

        public ValueTask DeleteAsync(IReadOnlyList<string> keys, CancellationToken cancellationToken) =>
            inner.DeleteAsync(keys, cancellationToken);

        public IAsyncEnumerable<string> ListAsync(string prefix, string? startAfter, CancellationToken cancellationToken) =>
            inner.ListAsync(prefix, startAfter, cancellationToken);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static DatasetOptions Options() => new DatasetOptions
    {
        Seed = 0x1_9_11E,
        ClusteringKey = ["Key"],
        Write = new VortexWriteOptions { RowBlockSize = 64, DataBlockTargetBytes = 2 << 10 },
    };

    private static async Task<VortexDataset> CreateAsync(IObjectStore store, DatasetOptions options, CancellationToken ct)
    {
        Decoders.EnsureRegistered();
        return await VortexDataset.CreateAsync(store, ChangeRow.Schema, options, ct);
    }

    /// <summary>Appends fifty rows, the <paramref name="append"/>-th thousand of keys, and returns the version its commit created.</summary>
    private static async Task<ulong> AppendAsync(VortexDataset dataset, int append, CancellationToken ct)
    {
        ChangeRow[] rows = [.. Enumerable.Range(0, 50).Select(i => new ChangeRow((append * 1_000L) + i, i * 0.5, Cities[i % Cities.Length]))];
        await using ObjectDraft draft = dataset.StartObject();
        await draft.Writer.WriteAsync<ChangeRow>(rows, ct);
        return await dataset.AppendAsync(draft, ct);
    }

    private static async Task<long> RowsAsync(VortexDataset dataset, CancellationToken ct) =>
        (await dataset.Scan<ChangeRow>().ToRecordsAsync(ct).ToListAsync(ct)).Count;
}
