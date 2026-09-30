// The pages a handle keeps from one version it reads to the next.
//
// A PAGE IS IMMUTABLE AND OUTLIVES ITS VERSION: every version names the pages its commit did not
// rewrite by the references an earlier version gave them, so what a handle read at one version it
// has for the next. The handle tests hold a refresh to that -- a walk of a tree too large for its
// header, done again after another writer changed one object, asks the store for what changed and
// nothing else -- and hold the cache to being what saves the reads, since a handle given none
// reads the tree again. The objects are entries only: a walk reads the tree, never an object.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.Expressions;
using Vorticity.Scanning;
using Vorticity.Tests.Scan;
using Vorticity.Types;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed class DatasetPageCacheTests
{
    private const ulong Seed = 0xCA_C4E5_EED;

    /// <summary>Enough objects of about 200 bytes that the tree's leaves pass what a header carries.</summary>
    private const int Objects = 20_000;

    [Fact]
    public void ACacheKeepsItsBudgetAndLetsTheLeastRecentlyUsedPageGoFirst()
    {
        PageCache cache = new PageCache(100);
        (PageReference first, byte[] one) = Page(1, 40);
        (PageReference second, byte[] two) = Page(2, 40);
        (PageReference third, byte[] three) = Page(3, 40);
        cache.Add(first, one);
        cache.Add(second, two);
        Assert.True(cache.TryGet(first, out _));

        // The second is now the least recently used, and the third takes its room.
        cache.Add(third, three);
        Assert.False(cache.TryGet(second, out _));
        Assert.True(cache.TryGet(first, out ReadOnlyMemory<byte> kept));
        Assert.True(kept.Span.SequenceEqual(one));
        Assert.True(cache.TryGet(third, out _));
        Assert.Equal((80, 2), (cache.Size, cache.Count));

        // A page past the whole budget is never kept, and takes nothing out.
        (PageReference huge, byte[] big) = Page(4, 101);
        cache.Add(huge, big);
        Assert.False(cache.TryGet(huge, out _));
        Assert.Equal(2, cache.Count);
    }

    [Fact]
    public void ACacheHoldsTheRegionsOfTheLastVersionsItWasGiven()
    {
        PageCache cache = new PageCache(0);
        for (ulong version = 1; version <= PageCache.HeldVersions; version++)
        {
            cache.Hold(version, new byte[version]);
        }

        Assert.True(cache.TryGetHeld(1, out ReadOnlyMemory<byte> region));
        Assert.Equal(1, region.Length);
        cache.Hold(PageCache.HeldVersions + 1, new byte[3]);
        Assert.False(cache.TryGetHeld(1, out _));
        Assert.True(cache.TryGetHeld(2, out region));
        Assert.Equal(2, region.Length);
        Assert.True(cache.TryGetHeld(PageCache.HeldVersions + 1, out region));
        Assert.Equal(3, region.Length);

        // A version opened again is held as the latest read found it, and keeps its place.
        cache.Hold(2, new byte[5]);
        Assert.True(cache.TryGetHeld(2, out region));
        Assert.Equal(5, region.Length);
        cache.Hold(PageCache.HeldVersions + 2, new byte[1]);
        Assert.False(cache.TryGetHeld(2, out _));
    }

    [Fact]
    public void ACacheRemembersAVersionsStartUntilItHasLearnedAsManyNewerOnes()
    {
        PageCache cache = new PageCache(0);
        for (ulong version = 1; version <= PageCache.MaxStarts; version++)
        {
            cache.AddStart(version, (long)version * 10);
        }

        Assert.True(cache.TryGetStart(1, out long start));
        Assert.Equal(10, start);
        cache.AddStart(PageCache.MaxStarts + 1, 7);
        Assert.False(cache.TryGetStart(1, out _));
        Assert.True(cache.TryGetStart(2, out start));
        Assert.Equal(20, start);
        Assert.True(cache.TryGetStart(PageCache.MaxStarts + 1, out start));
        Assert.Equal(7, start);

        // The latest learned wins: a version's object read again is the one there now.
        cache.AddStart(2, 21);
        Assert.True(cache.TryGetStart(2, out start));
        Assert.Equal(21, start);
    }

    [Fact]
    public async Task APageAVersionWroteInsideItsOpenReadServesALaterSourceWithoutARequest()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using MemoryObjectStore inner = new MemoryObjectStore();
        await using CountingObjectStore store = new CountingObjectStore(inner);
        await DatasetCommitter.CommitAsync(store, [Add(0), Add(2), Add(4)], new CommitOptions { Seed = Seed }, ct);
        (ulong version, CommitObject? commit) = await DatasetCommitter.LatestAsync(store, ct);
        CommitObject opened = Assert.IsType<CommitObject>(commit);
        DatasetTree tree = DatasetLevels.Of(opened.Header)[1];
        Assert.Equal(1, tree.Depth);

        // The version's one leaf lies in the region its open read holds, not in its header.
        Assert.DoesNotContain(opened.Header.Levels, level => level.Inlined.Any(page => page.Reference == tree.Root));

        // No budget for pages read from the store: only the region the first source held can answer.
        PageCache cache = new PageCache(0);
        new CommitPageSource(store, cache).Open(version, opened);
        CommitPageSource later = new CommitPageSource(store, cache);
        Assert.False(later.TryGetKnown(tree.Root, out _));
        store.Reset();
        ReadOnlyMemory<byte> leaf = await later.ReadPageAsync(tree.Root, ct);
        Assert.Equal(3, TreePage.ReadLeaf(leaf).Count);
        Assert.Equal(0, store.Requests);
    }

    [Fact]
    public async Task ARegionThatNoLongerHoldsAPageIsLetGoAndThePageReadFromTheStore()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using MemoryObjectStore inner = new MemoryObjectStore();
        await using CountingObjectStore store = new CountingObjectStore(inner);
        await DatasetCommitter.CommitAsync(store, [Add(0), Add(2), Add(4)], new CommitOptions { Seed = Seed }, ct);
        (ulong version, CommitObject? commit) = await DatasetCommitter.LatestAsync(store, ct);
        CommitObject opened = Assert.IsType<CommitObject>(commit);
        DatasetTree tree = DatasetLevels.Of(opened.Header)[1];

        // Held from an object that is not the one there now: no page of it hashes to its reference.
        PageCache cache = new PageCache(0);
        cache.Hold(version, new byte[opened.Held.Length]);
        cache.AddStart(version, opened.HeaderEnd);
        CommitPageSource later = new CommitPageSource(store, cache);
        store.Reset();
        ReadOnlyMemory<byte> leaf = await later.ReadPageAsync(tree.Root, ct);
        Assert.Equal(3, TreePage.ReadLeaf(leaf).Count);
        Assert.Equal(1, store.Requests);
        Assert.False(cache.TryGetHeld(version, out _));
    }

    [Fact]
    public async Task APageOfAnOlderVersionCostsOneRequestOnceTheHandleKnowsWhereItsPagesStart()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using MemoryObjectStore inner = new MemoryObjectStore();
        await using CountingObjectStore store = new CountingObjectStore(inner);
        await DatasetCommitter.CommitAsync(store, [.. Enumerable.Range(0, Objects).Select(i => Add(2L * i))], new CommitOptions { Seed = Seed }, ct);
        (ulong version, CommitObject? commit) = await DatasetCommitter.LatestAsync(store, ct);
        CommitObject opened = Assert.IsType<CommitObject>(commit);
        DatasetTree tree = DatasetLevels.Of(opened.Header)[1];
        PageCache cache = new PageCache(0);
        CommitPageSource first = new CommitPageSource(store, cache);
        first.Open(version, opened);
        Assert.True(first.TryGetKnown(tree.Root, out ReadOnlyMemory<byte> top));
        PageReference last = TreePage.ReadInternal(top)[^1].Child;

        // A source that never opened this version, as one for a later version is: on its own it
        // reads where the version's pages start, then the page. Through the cache the first
        // source filled, the page is the one request.
        foreach ((PageCache? through, long requests) in new (PageCache?, long)[] { (null, 2), (cache, 1) })
        {
            CommitPageSource later = new CommitPageSource(store, through);
            store.Reset();
            _ = await later.ReadPageAsync(last, ct);
            Assert.Equal(requests, store.Requests);
        }
    }

    [Fact]
    public async Task AHandleAsksAgainOnlyForThePagesAnotherWritersCommitChanged()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using MemoryObjectStore inner = new MemoryObjectStore();
        await using CountingObjectStore store = new CountingObjectStore(inner);
        await using VortexDataset dataset = await CreateAsync(store, new DatasetOptions(), ct);
        Assert.Equal(2, dataset.Levels[1].Depth);

        store.Reset();
        Assert.Equal(Objects, await CountAsync(dataset, ct));
        long first = store.Requests;
        Assert.True(first > 10, $"the first walk asked for {first} pages: the tree should outgrow its header");

        // Another writer adds one object in the middle of the level: one leaf and the top change.
        await AddAsync(store, Objects / 2, ct);
        await dataset.RefreshAsync(ct);
        store.Reset();
        Assert.Equal(Objects + 1, await CountAsync(dataset, ct));
        long second = store.Requests;

        Console.Out.Write(FormattableString.Invariant(
            $"PAGE CACHE: a walk over {Objects} objects asked for {first} pages, and after another writer's commit for {second}.\n"));

        // The leaf that changed, when the read that opened its version did not hold it.
        Assert.InRange(second, 0, 1);
    }

    [Fact]
    public async Task AHandleThatKeepsNoPageReadsTheTreeAgainAfterACommit()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using MemoryObjectStore inner = new MemoryObjectStore();
        await using CountingObjectStore store = new CountingObjectStore(inner);
        await using VortexDataset dataset = await CreateAsync(store, new DatasetOptions { PageCacheBytes = 0 }, ct);

        store.Reset();
        Assert.Equal(Objects, await CountAsync(dataset, ct));
        long first = store.Requests;
        await AddAsync(store, Objects / 2, ct);
        await dataset.RefreshAsync(ct);
        store.Reset();
        Assert.Equal(Objects + 1, await CountAsync(dataset, ct));

        // Every leaf again, the one that changed included.
        Assert.InRange(store.Requests, first - 1, first + 1);
    }

    /// <summary>A page of <paramref name="length"/> bytes and the reference that names it.</summary>
    private static (PageReference Reference, byte[] Bytes) Page(int seed, int length)
    {
        byte[] bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return (new PageReference((ulong)seed, 0, length, (UInt128)(uint)seed), bytes);
    }

    /// <summary>The objects a walk of the handle's version finds, which reads every page of its trees.</summary>
    private static async Task<int> CountAsync(VortexDataset dataset, CancellationToken ct)
    {
        int count = 0;
        await foreach (DataObject _ in dataset.ObjectsAsync(ct))
        {
            count++;
        }

        return count;
    }

    /// <summary>A clustered dataset whose level 1 holds <see cref="Objects"/> entries, and a handle on it.</summary>
    private static async Task<VortexDataset> CreateAsync(IObjectStore store, DatasetOptions options, CancellationToken ct)
    {
        Decoders.EnsureRegistered();
        DTypeArena types = new DTypeArena();
        DType schema = types.Struct(
            ["key", "measure"],
            [types.Primitive(PType.I64, Nullability.NonNullable), types.Primitive(PType.F64, Nullability.NonNullable)],
            Nullability.NonNullable);
        options = options with { Seed = Seed, ClusteringKey = ["key"] };
        await using (VortexDataset created = await VortexDataset.CreateAsync(store, schema, options, ct))
        {
        }

        List<DatasetOperation> operations = [.. Enumerable.Range(0, Objects).Select(i => Add(2L * i))];
        await DatasetCommitter.CommitAsync(store, operations, new CommitOptions { Seed = Seed }, ct);
        return await VortexDataset.OpenAsync(store, options, ct);
    }

    /// <summary>Another writer's commit of one object whose key falls between those of <paramref name="at"/> and the next.</summary>
    private static async Task AddAsync(IObjectStore store, int at, CancellationToken ct)
    {
        CommitResult added = await DatasetCommitter.CommitAsync(store, [Add((2L * at) + 1)], new CommitOptions { Seed = Seed }, ct);
        Assert.Equal(OperationOutcome.Applied, added.Outcomes[0]);
    }

    /// <summary>An object at level 1 whose smallest key is <paramref name="low"/>, with bounds enough to make its entry about 200 bytes.</summary>
    private static DatasetOperation Add(long low)
    {
        UInt128 uid = (UInt128)(ulong)low + 1;
        ObjectSummaries summaries = ObjectSummaries.From(
        [
            new ColumnSummary("key", FilterLiteral.From(low), true, FilterLiteral.From(low), true, IsExact: true, 0, true),
            new ColumnSummary("measure", FilterLiteral.From(low * 1.5), true, FilterLiteral.From(low * 2.5), true, true, 0, true),
        ]);
        ObjectEntry entry = new ObjectEntry(CommitKey.ForData($"{low:x16}"), uid, 1_000, 1 << 20, uid, summaries);

        // The key a clustered dataset gives an object: its smallest key, in an order bytes keep, then its uid.
        byte[] key = new byte[24];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(key, (ulong)low ^ (1UL << 63));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(key.AsSpan(8), (ulong)(uid >> 64));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(key.AsSpan(16), (ulong)uid);
        return new DatasetOperation.AddObject(key, entry) { Level = 1 };
    }
}
