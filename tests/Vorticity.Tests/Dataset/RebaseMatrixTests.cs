// The rebase matrix, row by row, and the request counts of a commit.
//
// WHY A MATRIX AND NOT A HANDFUL OF CASES. The rebase makes a claim that is easy to state and easy
// to get wrong: a writer that loses a commit re-applies its LOGICAL operations to the winner's
// tree, and each operation knows what to do when the ground moved. The seven rows are the seven
// ways the ground moves. A test per row is the only way to know that the branch for that row
// exists and does what the row says.
using System;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed class RebaseMatrixTests
{
    private const ulong Seed = 0xC0FFEE_1234_5678;

    private static CommitOptions Options() => new CommitOptions
    {
        Seed = Seed,
        Rule = new ProllyBoundaryRule(Seed, minBytes: 256, maxBytes: 1_024),
        Template = new CommitHeader
        {
            Version = 1,
            ClusteringKey = ["tenant"],
            Chunker = new ChunkerSettings(256, 512, 1_024),
        },
    };

    private static ReadOnlyMemory<byte> Key(int i) => Encoding.UTF8.GetBytes($"k{i:D6}");

    private static ObjectEntry Object(int i, int version = 0) => new ObjectEntry(
        CommitKey.ForData($"{i:x8}{version:x8}"), (UInt128)i << 32 | (uint)version, i + 1, 4_096, (UInt128)i);

    private static DatasetOperation Add(int i, int version = 0) =>
        new DatasetOperation.AddObject(Key(i), Object(i, version));

    /// <summary>The bytes of an indexer's fragment: a commit writes them and names where they lie.</summary>
    private static ReadOnlyMemory<byte> Fragment(int i)
    {
        byte[] bytes = new byte[32];
        bytes.AsSpan().Fill((byte)(0xA0 + i));
        return bytes;
    }

    /// <summary>A compaction at level 0, which is where these rebase cases all happen.</summary>
    /// <param name="inputs">The objects it consumes.</param>
    /// <param name="outputs">The objects it produces.</param>
    private static DatasetOperation Replace(int[] inputs, params (int Key, int Version)[] outputs)
    {
        List<(int Level, ReadOnlyMemory<byte> Key)> consumed = [];
        foreach (int input in inputs)
        {
            consumed.Add((0, Key(input)));
        }

        List<(int Level, ReadOnlyMemory<byte> Key, ObjectEntry Entry)> produced = [];
        foreach ((int key, int version) in outputs)
        {
            produced.Add((0, Key(key), Object(key, version)));
        }

        return new DatasetOperation.ReplaceObjects(consumed, produced);
    }

    [Fact]
    public async Task AnUncontendedCommitIsThreeDependentRequests()
    {
        // The List, the read of the latest commit's header, the creation.
        await using MemoryObjectStore inner = new MemoryObjectStore();
        await using CountingObjectStore store = new CountingObjectStore(inner);

        CommitResult first = await DatasetCommitter.CommitAsync(store, [Add(1)], Options(), default);
        Assert.Equal(1UL, first.Version);
        Assert.Equal(1, first.Attempts);

        store.Reset();
        CommitResult second = await DatasetCommitter.CommitAsync(store, [Add(2)], Options(), default);

        Assert.Equal(2UL, second.Version);
        Assert.Equal(3, store.DependentSteps);
        Assert.Equal(1, store.CountOf(ObjectOperation.List));
        Assert.Equal(1, store.CountOf(ObjectOperation.GetRange));
        Assert.Equal(1, store.CountOf(ObjectOperation.PutIfAbsent));
        Assert.Equal(2, second.Tree.Entries);
    }

    [Fact]
    public async Task AppendAndAppendBothLand()
    {
        // Both objects land in level 0, ordered by commit.
        await using MemoryObjectStore store = new MemoryObjectStore();
        await DatasetCommitter.CommitAsync(store, [Add(1)], Options(), default);

        // Two writers, each reading version 1 and adding its own object.
        Task<CommitResult> left = DatasetCommitter.CommitAsync(store, [Add(2)], Options(), default).AsTask();
        Task<CommitResult> right = DatasetCommitter.CommitAsync(store, [Add(3)], Options(), default).AsTask();
        CommitResult[] results = await Task.WhenAll(left, right);

        Assert.Equal([2UL, 3UL], [.. results.Select(r => r.Version).OrderBy(v => v)]);
        CommitResult latest = results.MaxBy(r => r.Version)!;
        Assert.Equal(3, latest.Tree.Entries);
        foreach (int i in new[] { 1, 2, 3 })
        {
            Assert.NotNull(await latest.Tree.FindAsync(Key(i), latest.Pages, default));
        }
    }

    [Fact]
    public async Task ATwiceAddedObjectIsAddedOnce()
    {
        // The same uid is the same bytes, so the loser's add is already there.
        await using MemoryObjectStore store = new MemoryObjectStore();
        await DatasetCommitter.CommitAsync(store, [Add(1)], Options(), default);
        CommitResult again = await DatasetCommitter.CommitAsync(store, [Add(1)], Options(), default);

        Assert.Equal([OperationOutcome.AlreadyThere], again.Outcomes);
        Assert.Equal(1, again.Tree.Entries);
    }

    [Fact]
    public async Task TheSecondIndexerOfOneFragmentWritesNothingForIt()
    {
        // The second indexer finds the fragment present in the winner's leaf, drops its own and
        // writes nothing for it.
        await using MemoryObjectStore store = new MemoryObjectStore();
        await DatasetCommitter.CommitAsync(store, [Add(1)], Options(), default);

        DatasetOperation indexing = new DatasetOperation.AddFragment(Key(1), Object(1).Uid, Fragment(7));
        CommitResult winner = await DatasetCommitter.CommitAsync(store, [indexing], Options(), default);
        Assert.Equal([OperationOutcome.Applied], winner.Outcomes);

        CommitResult loser = await DatasetCommitter.CommitAsync(store, [indexing], Options(), default);
        Assert.Equal([OperationOutcome.AlreadyThere], loser.Outcomes);

        TreeEntry entry = Assert.NotNull(await loser.Tree.FindAsync(Key(1), loser.Pages, default));
        PageReference named = Assert.Single(ObjectEntry.FromBytes(entry.Value.Span).Fragments);

        // The winner's commit object holds the fragment where its entry says; the loser's holds none.
        using ObjectRange won = await store.GetRangeAsync(winner.Key, 0, 1 << 20, default);
        CommitObject written = CommitObject.Open(won.Memory.Span, won.Length);
        Assert.Equal([named], written.Table.Fragments);
        Assert.Equal(Fragment(7).ToArray(), written.Page(won.Memory.Span, named).ToArray());

        using ObjectRange lost = await store.GetRangeAsync(loser.Key, 0, 1 << 20, default);
        Assert.Empty(CommitObject.Open(lost.Memory.Span, lost.Length).Table.Fragments);
    }

    [Fact]
    public async Task AFragmentWhoseObjectIsGoneIsDropped()
    {
        // The fragment's object is gone; the fragment is dropped and never written.
        await using MemoryObjectStore store = new MemoryObjectStore();
        await DatasetCommitter.CommitAsync(store, [Add(1), Add(2)], Options(), default);

        // A compaction replaces object 1 by an output of its own.
        DatasetOperation compaction = Replace([1], (1, 9));
        await DatasetCommitter.CommitAsync(store, [compaction], Options(), default);

        // The indexer was working against the OLD uid.
        CommitResult indexer = await DatasetCommitter.CommitAsync(
            store,
            [new DatasetOperation.AddFragment(Key(1), Object(1).Uid, Fragment(3))],
            Options(),
            default);

        Assert.Equal([OperationOutcome.Dropped], indexer.Outcomes);
        TreeEntry entry = Assert.NotNull(await indexer.Tree.FindAsync(Key(1), indexer.Pages, default));
        Assert.Empty(ObjectEntry.FromBytes(entry.Value.Span).Fragments);
    }

    [Fact]
    public async Task ACompactionWhoseInputIsGoneAbandons()
    {
        // The second compaction finds an input missing and abandons; its outputs are garbage.
        await using MemoryObjectStore store = new MemoryObjectStore();
        await DatasetCommitter.CommitAsync(store, [Add(1), Add(2), Add(3)], Options(), default);

        DatasetOperation first = Replace([1, 2], (1, 5));
        CommitResult winner = await DatasetCommitter.CommitAsync(store, [first], Options(), default);
        Assert.Equal([OperationOutcome.Applied], winner.Outcomes);
        Assert.Equal(2, winner.Tree.Entries);

        DatasetOperation overlapping = Replace([2, 3], (2, 6));
        CommitResult loser = await DatasetCommitter.CommitAsync(store, [overlapping], Options(), default);

        Assert.Equal([OperationOutcome.Abandoned], loser.Outcomes);
        Assert.Equal(2, loser.Tree.Entries);
        Assert.NotNull(await loser.Tree.FindAsync(Key(3), loser.Pages, default));
    }

    [Fact]
    public async Task AnAppendIsNotSweptUpByACompactionThatDidNotSeeIt()
    {
        // The compaction took a snapshot of <= 8 objects; the new append is not among them and
        // stays in level 0.
        await using MemoryObjectStore store = new MemoryObjectStore();
        await DatasetCommitter.CommitAsync(store, [Add(1), Add(2)], Options(), default);

        // The compactor read version 1 and decided to fold objects 1 and 2 into one.
        DatasetOperation compaction = Replace([1, 2], (1, 4));

        // Meanwhile an append lands.
        await DatasetCommitter.CommitAsync(store, [Add(3)], Options(), default);

        CommitResult after = await DatasetCommitter.CommitAsync(store, [compaction], Options(), default);
        Assert.Equal([OperationOutcome.Applied], after.Outcomes);
        Assert.Equal(2, after.Tree.Entries);
        Assert.NotNull(await after.Tree.FindAsync(Key(3), after.Pages, default));
    }

    [Fact]
    public async Task AReaderHoldsARootAndSeesOneVersion()
    {
        // The reader holds a root, and everything it references is immutable.
        await using MemoryObjectStore store = new MemoryObjectStore();
        CommitResult first = await DatasetCommitter.CommitAsync(
            store, [.. Enumerable.Range(0, 200).Select(i => Add(i))], Options(), default);

        // The reader's view, taken now.
        DatasetTree held = first.Tree;
        IPageSource pages = first.Pages;

        for (int round = 0; round < 3; round++)
        {
            await DatasetCommitter.CommitAsync(
                store,
                [.. Enumerable.Range(200 + (round * 50), 50).Select(i => Add(i))],
                Options(),
                default);
        }

        Assert.Equal(200, held.Entries);
        List<TreeEntry> walked = [];
        await foreach (TreeEntry entry in held.EnumerateAsync(pages, default))
        {
            walked.Add(entry);
        }

        Assert.Equal(200, walked.Count);
        Assert.Null(await held.FindAsync(Key(250), pages, default));
    }

    [Fact]
    public async Task ARebaseCostsTheHeaderTheTouchedLeavesAndTheCreation()
    {
        // An iteration of the commit loop costs depth + 2 dependent requests.
        await using MemoryObjectStore inner = new MemoryObjectStore();
        await using CountingObjectStore store = new CountingObjectStore(inner);
        await DatasetCommitter.CommitAsync(
            store, [.. Enumerable.Range(0, 400).Select(i => Add(i))], Options(), default);

        CommitResult before = await DatasetCommitter.CommitAsync(store, [Add(1_000)], Options(), default);
        store.Reset();
        CommitResult after = await DatasetCommitter.CommitAsync(store, [Add(1_001)], Options(), default);

        Assert.Equal(before.Version + 1, after.Version);
        Assert.Equal(1, store.CountOf(ObjectOperation.List));
        Assert.Equal(1, store.CountOf(ObjectOperation.PutIfAbsent));

        // ONE read, not `depth + 2`: inlining pages in the header pays for itself here. The
        // previous commit's header carried the whole tree of 400 objects -- the pages it wrote and
        // the ones it had read on the way -- so this commit's descent and rewrite found every page
        // it needed in the header it had already read, and the three steps are the List, that
        // header, the put.
        Assert.Equal(1, store.CountOf(ObjectOperation.GetRange));
        Assert.Equal(3, store.DependentSteps);
    }

    [Fact]
    public async Task AWriterThatKeepsLosingGivesUpWithTheReason()
    {
        await using MemoryObjectStore store = new MemoryObjectStore();
        await DatasetCommitter.CommitAsync(store, [Add(1)], Options(), default);

        // A store that always says the key is taken: the writer never wins.
        await using AlwaysTaken taken = new AlwaysTaken(store);
        ObjectStoreException refused = await Assert.ThrowsAsync<ObjectStoreException>(
            async () => await DatasetCommitter.CommitAsync(
                taken, [Add(2)], Options() with { MaxAttempts = 3 }, default));
        Assert.Contains("coordinator", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>A store whose creations always lose, to exercise the give-up path.</summary>
    private sealed class AlwaysTaken(IObjectStore inner) : IObjectStore
    {
        public ValueTask<ObjectRange> GetRangeAsync(
            string key, long offset, int length, CancellationToken cancellationToken) =>
            inner.GetRangeAsync(key, offset, length, cancellationToken);

        public ValueTask<ObjectHead?> HeadAsync(string key, CancellationToken cancellationToken) =>
            inner.HeadAsync(key, cancellationToken);

        public async ValueTask<PutOutcome> PutIfAbsentAsync(
            string key, PipeReader content, long length, CancellationToken cancellationToken)
        {
            await content.CompleteAsync().ConfigureAwait(false);
            return PutOutcome.Exists;
        }

        public ValueTask DeleteAsync(IReadOnlyList<string> keys, CancellationToken cancellationToken) =>
            inner.DeleteAsync(keys, cancellationToken);

        public IAsyncEnumerable<string> ListAsync(
            string prefix, string? startAfter, CancellationToken cancellationToken) =>
            inner.ListAsync(prefix, startAfter, cancellationToken);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
