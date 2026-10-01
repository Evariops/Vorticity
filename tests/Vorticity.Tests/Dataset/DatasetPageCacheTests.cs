// The pages a handle keeps from one version it reads to the next, and the version its own commit
// created, which it reads as it wrote it.
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
    public void AVersionHeldAgainAfterItWasLetGoIsAmongTheNewest()
    {
        PageCache cache = new PageCache(0);
        for (ulong version = 1; version <= 5; version++)
        {
            cache.Hold(version, new byte[1]);
        }

        // Let go of as a replaced object is, then held again from a fresh open: the oldest held is
        // now 2, and it goes first.
        cache.Forget(1);
        cache.Hold(1, new byte[2]);
        for (ulong version = 6; version <= PageCache.HeldVersions + 1; version++)
        {
            cache.Hold(version, new byte[1]);
        }

        Assert.False(cache.TryGetHeld(2, out _));
        Assert.True(cache.TryGetHeld(1, out ReadOnlyMemory<byte> region));
        Assert.Equal(2, region.Length);
        Assert.True(cache.TryGetHeld(3, out _));
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
    public async Task AStartLearnedOfAnObjectSinceWrittenAgainIsReadAgainFromItsPreamble()
    {
        // A version's object removed as torn and written again may start its pages elsewhere: the
        // page read where the start learned before says does not hash, the preamble is read, and
        // the page is read where the object starts now, which the handle keeps from then on.
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using MemoryObjectStore inner = new MemoryObjectStore();
        await using CountingObjectStore store = new CountingObjectStore(inner);
        await DatasetCommitter.CommitAsync(store, [.. Enumerable.Range(0, Objects).Select(i => Add(2L * i))], new CommitOptions { Seed = Seed }, ct);
        (ulong version, CommitObject? commit) = await DatasetCommitter.LatestAsync(store, ct);
        CommitObject opened = Assert.IsType<CommitObject>(commit);
        PageCache cache = new PageCache(0);
        CommitPageSource first = new CommitPageSource(store, cache);
        first.Open(version, opened);
        Assert.True(first.TryGetKnown(DatasetLevels.Of(opened.Header)[1].Root, out ReadOnlyMemory<byte> top));
        PageReference last = TreePage.ReadInternal(top)[^1].Child;

        cache.AddStart(version, opened.HeaderEnd + 8);
        CommitPageSource later = new CommitPageSource(store, cache);
        store.Reset();
        ReadOnlyMemory<byte> page = await later.ReadPageAsync(last, ct);
        Assert.Equal(TreePageKind.Leaf, TreePage.KindOf(page.Span));
        Assert.Equal(3, store.Requests);
        Assert.True(cache.TryGetStart(version, out long start));
        Assert.Equal(opened.HeaderEnd, start);
    }

    [Fact]
    public async Task AHeaderSaysWhereThePagesItNamesPastItsRoomStart()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using MemoryObjectStore inner = new MemoryObjectStore();
        await using CountingObjectStore store = new CountingObjectStore(inner);

        // Small pages, so that the tree's top levels alone fill what a header carries; then one
        // object at the end, whose commit rewrites the right edge of every height.
        CommitOptions options = new CommitOptions { Seed = Seed, Rule = new ProllyBoundaryRule(Seed, minBytes: 256, targetBytes: 512, maxBytes: 1_024) };
        await DatasetCommitter.CommitAsync(store, [.. Enumerable.Range(0, Objects).Select(i => Add(2L * i))], options, ct);
        ulong edge = (await DatasetCommitter.CommitAsync(store, [Add(2L * Objects)], options, ct)).Version;

        // Level 0 spends a third of the room first, so level 1 stops at a page the load wrote, well
        // left of the edge: the edge's pages past the stop are named by the pages the header carries,
        // and by nothing else.
        CommitResult last = await DatasetCommitter.CommitAsync(
            store, [.. Enumerable.Range(0, 300).Select(i => (DatasetOperation)((DatasetOperation.AddObject)Add((2L * i) + 1) with { Level = 0 }))], options, ct);
        CommitObject opened = await CommitObject.OpenAsync(store, CommitKey.For(last.Version), ct);
        Assert.Contains(opened.Header.Starts, start => start.Version == edge);

        // Such a page reads in one request, where the edge's preamble would be a second.
        CommitLevel level = opened.Header.Levels.Single(recorded => recorded.Level == 1);
        HashSet<PageReference> carried = [.. level.Inlined.Select(page => page.Reference)];
        PageReference named = level.Inlined
            .Where(page => TreePage.KindOf(page.Bytes.Span) == TreePageKind.Internal)
            .SelectMany(page => TreePage.ReadInternal(page.Bytes))
            .Select(child => child.Child)
            .Last(child => child.Version == edge && !carried.Contains(child));
        CommitPageSource pages = new CommitPageSource(store);
        pages.Open(last.Version, opened);
        store.Reset();
        _ = await pages.ReadPageAsync(named, ct);
        Assert.Equal(1, store.Requests);
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

    [Fact]
    public async Task AHandleReadsTheVersionItCommittedWithoutAskingTheStoreForIt()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using MemoryObjectStore inner = new MemoryObjectStore();
        await using CountingObjectStore store = new CountingObjectStore(inner);
        await using VortexDataset dataset = await CreateAsync(store, new DatasetOptions(), 3, ct);

        store.Reset();
        CommitResult result = await dataset.CommitAsync([Add(1)], ct);

        // The creation and nothing else: the handle knew its version to be the latest a moment ago,
        // so it builds on it as it holds it, and reads what it wrote as it wrote it.
        Assert.Equal(result.Version, dataset.Version);
        Assert.Equal((1, 1), (store.Requests, store.CountOf(ObjectOperation.PutIfAbsent)));
        store.Reset();
        Assert.Equal(4, await CountAsync(dataset, ct));
        Assert.Equal(0, store.Requests);
    }

    [Fact]
    public async Task AHandleReadsThePagesItsCommitWrotePastItsOpenReadWithoutAskingTheStore()
    {
        // A commit of many objects writes more pages than the read opening its object brings back:
        // the writer held them all, and its handle keeps those the read leaves out.
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using MemoryObjectStore inner = new MemoryObjectStore();
        await using CountingObjectStore store = new CountingObjectStore(inner);
        await using VortexDataset dataset = await CreateAsync(store, new DatasetOptions(), 3, ct);
        CommitResult result = await dataset.CommitAsync([.. Enumerable.Range(0, Objects).Select(i => Add((2L * i) + 1))], ct);
        Assert.True(result.Commit.Length > CommitFormat.OpenBytes, $"the commit object is {result.Commit.Length} bytes");

        store.Reset();
        Assert.Equal(Objects + 3, await CountAsync(dataset, ct));
        Assert.Equal(0, store.Requests);
    }

    [Fact]
    public async Task ABatchThatFindsNothingToDoAsksOnlyWhetherItsVersionIsStillTheLatest()
    {
        // Built on the version held, the batch finds nothing to do and asks whether a later one
        // exists; none does, and the commit object it holds is the latest's, which it reads again
        // for nothing.
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using MemoryObjectStore inner = new MemoryObjectStore();
        await using CountingObjectStore store = new CountingObjectStore(inner);
        await using VortexDataset dataset = await CreateAsync(store, new DatasetOptions(), 3, ct);
        ulong held = dataset.Version;

        store.Reset();
        CommitResult result = await dataset.CommitAsync([Add(2)], ct);
        Assert.Equal((OperationOutcome.AlreadyThere, held), (result.Outcomes[0], result.Version));
        Assert.Equal((1, 1), (store.Requests, store.CountOf(ObjectOperation.Head)));
    }

    [Fact]
    public async Task AHandleThatLearnedItsVersionLongAgoListsBeforeItBuildsOnIt()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        ManualClock clock = new ManualClock(new DateTimeOffset(2026, 9, 18, 12, 0, 0, TimeSpan.Zero));
        await using MemoryObjectStore inner = new MemoryObjectStore();
        await using CountingObjectStore store = new CountingObjectStore(inner);
        await using VortexDataset dataset = await CreateAsync(store, new DatasetOptions { TimeProvider = clock }, 3, ct);

        // Within half the window a refresh with nothing new is one head; past it, one listing, and
        // neither reads the version again.
        store.Reset();
        Assert.Equal(dataset.Version, await dataset.RefreshAsync(ct));
        Assert.Equal((1, 1), (store.Requests, store.CountOf(ObjectOperation.Head)));
        clock.Advance(TimeSpan.FromDays(4));
        store.Reset();
        Assert.Equal(dataset.Version, await dataset.RefreshAsync(ct));
        Assert.Equal((1, 1), (store.Requests, store.CountOf(ObjectOperation.List)));

        // The listing dated the version again: the commit builds on it outright. Past the half
        // window once more, it lists first, and builds on the version it holds.
        store.Reset();
        await dataset.CommitAsync([Add(1)], ct);
        Assert.Equal((1, 1), (store.Requests, store.CountOf(ObjectOperation.PutIfAbsent)));
        clock.Advance(TimeSpan.FromDays(4));
        store.Reset();
        await dataset.CommitAsync([Add(3)], ct);
        Assert.Equal((2, 1, 1), (store.Requests, store.CountOf(ObjectOperation.List), store.CountOf(ObjectOperation.PutIfAbsent)));
        Assert.Equal(5, await CountAsync(dataset, ct));
    }

    [Fact]
    public async Task AHandleThatLosesTheRaceAsksForTheVersionsAfterTheOneItBuiltOn()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        ManualClock clock = new ManualClock(new DateTimeOffset(2026, 9, 18, 12, 0, 0, TimeSpan.Zero));
        await using MemoryObjectStore inner = new MemoryObjectStore();
        await using RacedStore raced = new RacedStore(inner);
        await using CountingObjectStore store = new CountingObjectStore(raced);
        await using VortexDataset dataset = await CreateAsync(store, new DatasetOptions { TimeProvider = clock }, 3, ct);
        ulong held = dataset.Version;

        // Past the half window, so the commit lists; another writer creates the next version just
        // before it: the second attempt trusts the listing it just made, and asks for what follows.
        clock.Advance(TimeSpan.FromDays(4));
        raced.Racing = () => AddAsync(inner, 2, ct);
        store.Reset();
        CommitResult result = await dataset.CommitAsync([Add(7)], ct);
        Assert.Equal((2, held + 2), (result.Attempts, result.Version));
        Assert.Equal(
            (1, 2, 1, 1),
            (store.CountOf(ObjectOperation.List), store.CountOf(ObjectOperation.PutIfAbsent), store.CountOf(ObjectOperation.Head), store.CountOf(ObjectOperation.GetRange)));
        Assert.Equal(5, await CountAsync(dataset, ct));
    }

    [Fact]
    public async Task ABatchThatFindsNothingToDoOnTheVersionHeldIsDecidedAgainOnTheLatest()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using MemoryObjectStore inner = new MemoryObjectStore();
        await using CountingObjectStore store = new CountingObjectStore(inner);
        await using VortexDataset dataset = await CreateAsync(store, new DatasetOptions(), 3, ct);

        // Another writer removes an object the handle still holds, which the handle then adds: on the
        // version it holds the object is there, on the latest it is not, and the latest decides.
        DatasetOperation.AddObject added = (DatasetOperation.AddObject)Add(2);
        CommitResult removed = await DatasetCommitter.CommitAsync(
            inner, [new DatasetOperation.ReplaceObjects([(1, added.Key)], [])], new CommitOptions { Seed = Seed }, ct);
        Assert.Equal(OperationOutcome.Applied, removed.Outcomes[0]);
        CommitResult result = await dataset.CommitAsync([added], ct);
        Assert.Equal((OperationOutcome.Applied, removed.Version + 1), (result.Outcomes[0], result.Version));
        Assert.Equal(3, await CountAsync(dataset, ct));
    }

    [Fact]
    public async Task AHandleWhoseCommitFindsNothingToDoReadsTheVersionItFound()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using MemoryObjectStore inner = new MemoryObjectStore();
        await using CountingObjectStore store = new CountingObjectStore(inner);
        await using VortexDataset dataset = await CreateAsync(store, new DatasetOptions(), 3, ct);
        ulong held = dataset.Version;

        // Another writer adds the very object this handle is about to add.
        await AddAsync(store, 0, ct);
        store.Reset();
        CommitResult result = await dataset.CommitAsync([Add(1)], ct);

        // The creation that finds the number taken, the head that finds nothing after it, the read
        // of the other writer's version, and nothing after: the handle reads the version its commit
        // was decided on.
        Assert.Equal(OperationOutcome.AlreadyThere, result.Outcomes[0]);
        Assert.Equal((held + 1, held + 1), (result.Version, dataset.Version));
        Assert.Equal((1, 1, 1), (store.CountOf(ObjectOperation.Head), store.CountOf(ObjectOperation.GetRange), store.CountOf(ObjectOperation.PutIfAbsent)));
        Assert.Equal(4, await CountAsync(dataset, ct));
    }

    [Fact]
    public async Task AHandleNeverGoesBackToAVersionOlderThanTheOneItReads()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using MemoryObjectStore inner = new MemoryObjectStore();
        await using HeldStore store = new HeldStore(inner);
        await using VortexDataset dataset = await CreateAsync(store, new DatasetOptions(), 3, ct);
        ulong held = dataset.Version;

        // The first commit creates the next version and is not told yet; a second one on the same
        // handle finds that number taken, rebases onto it, creates the one after, and moves the
        // handle there.
        store.Holding = CommitKey.For(held + 1);
        Task<CommitResult> first = dataset.CommitAsync([Add(1)], ct).AsTask();
        await store.Reached.Task.WaitAsync(ct);
        CommitResult second = await dataset.CommitAsync([Add(3)], ct);
        Assert.Equal((held + 2, held + 2), (second.Version, dataset.Version));

        // Told at last, the first moves nothing: the handle already reads past the version it made.
        store.Release.SetResult();
        Assert.Equal(held + 1, (await first).Version);
        Assert.Equal(held + 2, dataset.Version);
        Assert.Equal(5, await CountAsync(dataset, ct));
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
    private static Task<VortexDataset> CreateAsync(IObjectStore store, DatasetOptions options, CancellationToken ct) =>
        CreateAsync(store, options, Objects, ct);

    /// <summary>A clustered dataset whose level 1 holds <paramref name="objects"/> entries, at even keys, and a handle on it.</summary>
    private static async Task<VortexDataset> CreateAsync(IObjectStore store, DatasetOptions options, int objects, CancellationToken ct)
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

        List<DatasetOperation> operations = [.. Enumerable.Range(0, objects).Select(i => Add(2L * i))];
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

    /// <summary>A store that holds back the answer to one put, once the object is in: a writer not told yet.</summary>
    private sealed class HeldStore(IObjectStore inner) : IObjectStore
    {
        /// <summary>The key whose next put is held, once; null holds none.</summary>
        internal string? Holding { get; set; }

        /// <summary>Set once the held put has created its object.</summary>
        internal TaskCompletionSource Reached { get; } = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Set by the test to answer the held put.</summary>
        internal TaskCompletionSource Release { get; } = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<ObjectRange> GetRangeAsync(string key, long offset, int length, CancellationToken cancellationToken) =>
            inner.GetRangeAsync(key, offset, length, cancellationToken);

        public ValueTask<ObjectHead?> HeadAsync(string key, CancellationToken cancellationToken) =>
            inner.HeadAsync(key, cancellationToken);

        public async ValueTask<PutOutcome> PutIfAbsentAsync(string key, System.IO.Pipelines.PipeReader content, long length, CancellationToken cancellationToken)
        {
            PutOutcome outcome = await inner.PutIfAbsentAsync(key, content, length, cancellationToken);
            if (key == Holding)
            {
                Holding = null;
                Reached.SetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }

            return outcome;
        }

        public ValueTask DeleteAsync(IReadOnlyList<string> keys, CancellationToken cancellationToken) =>
            inner.DeleteAsync(keys, cancellationToken);

        public IAsyncEnumerable<string> ListAsync(string prefix, string? startAfter, CancellationToken cancellationToken) =>
            inner.ListAsync(prefix, startAfter, cancellationToken);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>A store in which, once, another writer commits just before this one's creation reaches it.</summary>
    private sealed class RacedStore(IObjectStore inner) : IObjectStore
    {
        /// <summary>The other writer's commit, run before the next creation of a commit object; null runs none.</summary>
        internal Func<Task>? Racing { get; set; }

        public ValueTask<ObjectRange> GetRangeAsync(string key, long offset, int length, CancellationToken cancellationToken) =>
            inner.GetRangeAsync(key, offset, length, cancellationToken);

        public ValueTask<ObjectHead?> HeadAsync(string key, CancellationToken cancellationToken) =>
            inner.HeadAsync(key, cancellationToken);

        public async ValueTask<PutOutcome> PutIfAbsentAsync(string key, System.IO.Pipelines.PipeReader content, long length, CancellationToken cancellationToken)
        {
            if (key.StartsWith(CommitKey.Prefix, StringComparison.Ordinal) && Racing is { } race)
            {
                Racing = null;
                await race();
            }

            return await inner.PutIfAbsentAsync(key, content, length, cancellationToken);
        }

        public ValueTask DeleteAsync(IReadOnlyList<string> keys, CancellationToken cancellationToken) =>
            inner.DeleteAsync(keys, cancellationToken);

        public IAsyncEnumerable<string> ListAsync(string prefix, string? startAfter, CancellationToken cancellationToken) =>
            inner.ListAsync(prefix, startAfter, cancellationToken);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
