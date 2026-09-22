// The contract every IObjectStore owes its callers.
//
// ONE SUITE, EVERY STORE, and that is the point rather than a convenience. The contract is four
// requirements a store must document: an atomic PutIfAbsent, a strongly consistent List, a token
// that changes whenever the bytes under a key change, and GetRange on objects of any size. A
// requirement stated in prose is a requirement each implementer reads differently; a requirement
// with a test is one an S3 library can RUN against its own store before shipping. So these tests
// take a factory and know nothing else about the store under them, and the two that ship here --
// memory and file system -- are simply the first two callers.
using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed class ObjectStoreContractTests : IDisposable
{
    private readonly List<string> _directories = [];

    /// <summary>The stores under test, each with the name its failures are reported under.</summary>
    public static TheoryData<string> Stores() => ["memory", "file"];

    private IObjectStore Open(string kind)
    {
        if (kind == "memory")
        {
            return new MemoryObjectStore();
        }

        string root = Path.Combine(Path.GetTempPath(), $"vorticity-store-{Guid.NewGuid():N}");
        _directories.Add(root);
        return new FileObjectStore(root);
    }

    public void Dispose()
    {
        foreach (string directory in _directories)
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task AnObjectIsCreatedOnceAndReadBackWhole(string kind)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using IObjectStore store = Open(kind);
        byte[] content = Bytes("the bytes of a small object");

        Assert.Equal(PutOutcome.Created, await store.PutIfAbsentAsync("data/one.vortex", content, ct));
        Assert.Equal(PutOutcome.Exists, await store.PutIfAbsentAsync("data/one.vortex", Bytes("other"), ct));

        ObjectHead head = Assert.NotNull(await store.HeadAsync("data/one.vortex", ct));
        Assert.Equal(content.Length, head.Length);

        using ObjectRange range = await store.GetRangeAsync("data/one.vortex", 0, content.Length, ct);
        Assert.Equal(content, range.Bytes.ToArray());

        // The second put changed nothing: the object is still the first writer's.
        Assert.Equal(content.Length, (await store.HeadAsync("data/one.vortex", ct))!.Value.Length);
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task AnAbsentKeyHeadsNullAndReadsAsNotFound(string kind)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using IObjectStore store = Open(kind);
        Assert.Null(await store.HeadAsync("data/none.vortex", ct));
        await Assert.ThrowsAsync<ObjectNotFoundException>(
            async () => await store.GetRangeAsync("data/none.vortex", 0, 16, ct));

        // Deleting an absent key is not an error, and creates nothing.
        await store.DeleteAsync(["data/none.vortex"], ct);
        Assert.Null(await store.HeadAsync("data/none.vortex", ct));
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task ARangeIsClampedToTheObjectAndRefusedPastItsEnd(string kind)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using IObjectStore store = Open(kind);
        byte[] content = Bytes("0123456789");
        await store.PutIfAbsentAsync("data/range", content, ct);

        using (ObjectRange middle = await store.GetRangeAsync("data/range", 3, 4, ct))
        {
            Assert.Equal(Bytes("3456"), middle.Bytes.ToArray());
        }

        // A tail read asks for more than the object holds by design, so a short answer is
        // the normal case and not an error.
        using (ObjectRange tail = await store.GetRangeAsync("data/range", 6, 64 << 10, ct))
        {
            Assert.Equal(Bytes("6789"), tail.Bytes.ToArray());
        }

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            async () => await store.GetRangeAsync("data/range", 10, 1, ct));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            async () => await store.GetRangeAsync("data/range", -1, 1, ct));
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task AnEmptyObjectIsAnObject(string kind)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using IObjectStore store = Open(kind);
        Assert.Equal(PutOutcome.Created, await store.PutIfAbsentAsync("data/empty", default, ct));
        Assert.Equal(0, (await store.HeadAsync("data/empty", ct))!.Value.Length);
        using ObjectRange range = await store.GetRangeAsync("data/empty", 0, 16, ct);
        Assert.Equal(0, range.Length);
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task TheTokenChangesWhenTheBytesUnderAKeyDo(string kind)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        // The third requirement, and the one binding a reader to one version of an object rests
        // on: an object is immutable, so the only way its bytes change is a delete and a new
        // object under the same key.
        await using IObjectStore store = Open(kind);
        await store.PutIfAbsentAsync("data/token", Bytes("first"), ct);
        string first = (await store.HeadAsync("data/token", ct))!.Value.Token;

        await store.DeleteAsync(["data/token"], ct);
        Assert.Null(await store.HeadAsync("data/token", ct));

        // A file store's token carries the last-write time, whose resolution is the file system's;
        // a different length is what makes this test independent of that resolution.
        await store.PutIfAbsentAsync("data/token", Bytes("second, longer"), ct);
        string second = (await store.HeadAsync("data/token", ct))!.Value.Token;
        Assert.NotEqual(first, second);

        using ObjectRange range = await store.GetRangeAsync("data/token", 0, 4, ct);
        Assert.Equal(second, range.Token);
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task AHeadSaysWhenTheStoreCreatedTheObject(string kind)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        // The vacuum ages an unreferenced object by the store's timestamp: a store that could not
        // say when it created an object would have every orphan look like a writer in flight.
        await using IObjectStore store = Open(kind);
        DateTimeOffset before = DateTimeOffset.UtcNow.AddSeconds(-5);
        await store.PutIfAbsentAsync("data/dated", Bytes("dated"), ct);
        DateTimeOffset created = (await store.HeadAsync("data/dated", ct))!.Value.LastModified;
        Assert.InRange(created, before, DateTimeOffset.UtcNow.AddSeconds(5));
    }

    [Fact]
    public async Task TheMemoryStoreStampsObjectsWithItsOwnClock()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        ManualClock clock = new ManualClock(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero));
        await using MemoryObjectStore store = new MemoryObjectStore { TimeProvider = clock };
        await store.PutIfAbsentAsync("data/first", Bytes("first"), ct);
        clock.Advance(TimeSpan.FromHours(1));
        await store.PutIfAbsentAsync("data/second", Bytes("second"), ct);

        Assert.Equal(clock.Now - TimeSpan.FromHours(1), (await store.HeadAsync("data/first", ct))!.Value.LastModified);
        Assert.Equal(clock.Now, (await store.HeadAsync("data/second", ct))!.Value.LastModified);
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task ListingIsOrdinalAndPagesByItsLastKey(string kind)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using IObjectStore store = Open(kind);
        string[] keys =
        [
            "commit/99999999999999999997.vxc",
            "commit/99999999999999999998.vxc",
            "commit/99999999999999999999.vxc",
            "data/a.vortex",
            "data/b.vortex",
        ];

        foreach (string key in keys.Reverse())
        {
            await store.PutIfAbsentAsync(key, Bytes(key), ct);
        }

        Assert.Equal(keys, await store.ListAsync(string.Empty, null, 100, ct));

        // One List of the commit prefix returns the newest commit, because the key inverts
        // the version. Nothing here knows that rule -- it is ordinal order doing the work.
        IReadOnlyList<string> newest = await store.ListAsync("commit/", null, 1, ct);
        Assert.Equal(["commit/99999999999999999997.vxc"], newest);

        IReadOnlyList<string> page = await store.ListAsync("data/", null, 1, ct);
        Assert.Equal(["data/a.vortex"], page);
        Assert.Equal(["data/b.vortex"], await store.ListAsync("data/", page[^1], 1, ct));
        Assert.Empty(await store.ListAsync("data/", "data/b.vortex", 1, ct));
        Assert.Empty(await store.ListAsync("nothing/", null, 10, ct));
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task ADeletedKeyCanBeCreatedAgain(string kind)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using IObjectStore store = Open(kind);
        await store.PutIfAbsentAsync("data/gone", Bytes("one"), ct);
        await store.DeleteAsync(["data/gone"], ct);
        Assert.Null(await store.HeadAsync("data/gone", ct));
        Assert.Equal(PutOutcome.Created, await store.PutIfAbsentAsync("data/gone", Bytes("two"), ct));
        using ObjectRange range = await store.GetRangeAsync("data/gone", 0, 3, ct);
        Assert.Equal(Bytes("two"), range.Bytes.ToArray());
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task OnlyOneOfManyConcurrentPutsCreatesTheKey(string kind)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        // The first requirement, and the one the whole commit protocol rests on: a commit is a
        // conditional creation, so a store whose PutIfAbsent is a get-then-put loses commits.
        await using IObjectStore store = Open(kind);
        const int writers = 16;
        using Barrier barrier = new Barrier(writers);
        Task<PutOutcome>[] puts = new Task<PutOutcome>[writers];
        for (int writer = 0; writer < writers; writer++)
        {
            byte[] content = Bytes($"writer {writer}");
            puts[writer] = Task.Run(async () =>
            {
                barrier.SignalAndWait();
                return await store.PutIfAbsentAsync("commit/00000000000000000001.vxc", content, default);
            });
        }

        PutOutcome[] outcomes = await Task.WhenAll(puts);
        Assert.Equal(1, outcomes.Count(outcome => outcome == PutOutcome.Created));
        Assert.Equal(writers - 1, outcomes.Count(outcome => outcome == PutOutcome.Exists));

        // And the object that is there is one writer's bytes, whole.
        using ObjectRange range = await store.GetRangeAsync("commit/00000000000000000001.vxc", 0, 64, ct);
        Assert.StartsWith("writer ", Encoding.UTF8.GetString(range.Bytes), StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task AKeyThatCouldEscapeTheStoreIsRefused(string kind)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using IObjectStore store = Open(kind);
        foreach (string key in new[] { "", "/data/a", "data/a/", "../secret", "data/../../secret", "a//b", "data\\a" })
        {
            await Assert.ThrowsAsync<ArgumentException>(
                async () => await store.PutIfAbsentAsync(key, Bytes("x"), ct));
        }

        await Assert.ThrowsAnyAsync<ArgumentException>(
            async () => await store.PutIfAbsentAsync(null!, Bytes("x"), ct));
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task AnObjectOfManyMegabytesIsReadInPieces(string kind)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        // The fourth requirement: a ranged GetRange on objects of any size.
        await using IObjectStore store = Open(kind);
        byte[] content = new byte[4 << 20];
        for (int i = 0; i < content.Length; i++)
        {
            content[i] = (byte)(i * 31);
        }

        await store.PutIfAbsentAsync("data/big.vortex", content, ct);
        foreach (long offset in new long[] { 0, 1, 1 << 20, content.Length - 7 })
        {
            int length = (int)Math.Min(64 << 10, content.Length - offset);
            using ObjectRange range = await store.GetRangeAsync("data/big.vortex", offset, length, ct);
            Assert.Equal(content.AsSpan((int)offset, length).ToArray(), range.Bytes.ToArray());
        }
    }
}
