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
using System.IO.Pipelines;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.IO;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed class ObjectStoreContractTests : IDisposable
{
    private readonly List<string> _directories = [];
    private readonly List<IDisposable> _emulators = [];

    /// <summary>The stores under test, each with the name its failures are reported under.</summary>
    public static TheoryData<string> Stores() => ["memory", "file", "s3"];

    /// <summary>The stores whose keys are paths, which refuse a key that could leave their root.</summary>
    public static TheoryData<string> PathStores() => ["memory", "file"];

    private IObjectStore Open(string kind)
    {
        if (kind == "memory")
        {
            return new MemoryObjectStore();
        }

        if (kind == "s3")
        {
            Vorticity.Tests.ObjectStorage.S3Emulator emulator = new Vorticity.Tests.ObjectStorage.S3Emulator();
            _emulators.Add(emulator);
            return Vorticity.Tests.ObjectStorage.S3ReadTests.Store(emulator);
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

        foreach (IDisposable emulator in _emulators)
        {
            emulator.Dispose();
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
    public async Task AStoreThatLocksNothingSaysSo(string kind)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using IObjectStore store = Open(kind);
        await store.PutIfAbsentAsync("data/free", Bytes("free"), ct);
        ObjectHead head = (await store.HeadAsync("data/free", ct))!.Value;
        Assert.Equal((null, false), (head.RetainUntil, head.LegalHold));
        Assert.False(head.IsLockedAt(DateTimeOffset.MaxValue));
        await store.DeleteAsync(["data/free"], ct);
        Assert.Null(await store.HeadAsync("data/free", ct));
    }

    [Fact]
    public async Task AStoreUnderALockRefusesToDeleteBeforeTheDateAndAHeadSaysWhen()
    {
        // A bucket under S3 Object Lock in compliance mode, as the memory store plays it: every
        // object it creates is kept for the term, and a batch holding one is refused whole.
        CancellationToken ct = TestContext.Current.CancellationToken;
        ManualClock clock = new ManualClock(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero));
        await using MemoryObjectStore store = new MemoryObjectStore { TimeProvider = clock, RetainFor = TimeSpan.FromDays(30) };
        await store.PutIfAbsentAsync("data/kept", Bytes("kept"), ct);
        store.RetainFor = null;
        await store.PutIfAbsentAsync("data/free", Bytes("free"), ct);

        ObjectHead kept = (await store.HeadAsync("data/kept", ct))!.Value;
        Assert.Equal(clock.Now + TimeSpan.FromDays(30), kept.RetainUntil);
        Assert.True(kept.IsLockedAt(clock.Now));
        await Assert.ThrowsAsync<ObjectStoreException>(async () => await store.DeleteAsync(["data/free", "data/kept"], ct));
        Assert.NotNull(await store.HeadAsync("data/free", ct));

        clock.Advance(TimeSpan.FromDays(31));
        Assert.False((await store.HeadAsync("data/kept", ct))!.Value.IsLockedAt(clock.Now));
        await store.DeleteAsync(["data/free", "data/kept"], ct);
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public async Task AnObjectUnderALegalHoldStaysUntilTheHoldIsLifted()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using MemoryObjectStore store = new MemoryObjectStore();
        await store.PutIfAbsentAsync("data/held", Bytes("held"), ct);
        store.Hold("data/held", held: true);
        ObjectHead head = (await store.HeadAsync("data/held", ct))!.Value;
        Assert.Equal((null, true), (head.RetainUntil, head.LegalHold));
        Assert.True(head.IsLockedAt(DateTimeOffset.MaxValue));
        await Assert.ThrowsAsync<ObjectStoreException>(async () => await store.DeleteAsync(["data/held"], ct));

        store.Hold("data/held", held: false);
        await store.DeleteAsync(["data/held"], ct);
        Assert.Null(await store.HeadAsync("data/held", ct));
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

        // A prefix is a string, not a directory: it may end inside a name, and names no parent.
        Assert.Equal(["data/a.vortex"], await store.ListAsync("data/a", null, 10, ct));
        Assert.Equal(keys[..3], await store.ListAsync("comm", null, 10, ct));
        Assert.Empty(await store.ListAsync("../", null, 10, ct));
    }

    [Fact]
    public async Task AFileStoreListsAPrefixFromItsDirectoryAlone()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "a directory is made unreadable by its Unix mode");
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using IObjectStore store = Open("file");
        await store.PutIfAbsentAsync("commit/99999999999999999999.vxc", Bytes("a commit"), ct);
        await store.PutIfAbsentAsync("data/a.vortex", Bytes("an object"), ct);

        // The commits are what an open lists, every time: the data objects, however many, are not
        // walked for them. A data directory no one may read shows it.
        string data = Path.Combine(_directories[^1], "data");
        if (!OperatingSystem.IsWindows())
        {
            UnixFileMode mode = global::System.IO.File.GetUnixFileMode(data);
            global::System.IO.File.SetUnixFileMode(data, UnixFileMode.None);
            try
            {
                Assert.Equal(["commit/99999999999999999999.vxc"], await store.ListAsync("commit/", null, 10, ct));
            }
            finally
            {
                global::System.IO.File.SetUnixFileMode(data, mode);
            }
        }
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
    public async Task AReadSeesAnObjectWholeOrNotAtAll(string kind)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        // A reader may meet a key a put has not finished, since the commit protocol reads the newest
        // key it lists: it finds the object absent, or waits for its last byte, and never reads a part.
        // The writer waits until the store has taken what it gave, so the read starts on an object
        // whose first half is stored and whose second is not.
        await using IObjectStore store = Open(kind);
        const string key = "commit/00000000000000000001.vxc";
        Pipe pipe = new Pipe(new PipeOptions(pauseWriterThreshold: 1, resumeWriterThreshold: 1));
        ValueTask<PutOutcome> put = store.PutIfAbsentAsync(key, pipe.Reader, 8, ct);
        await pipe.Writer.WriteAsync(Bytes("half"), ct);

        Task<byte[]?> read = ReadWholeOrAbsentAsync(store, key, ct);
        await pipe.Writer.WriteAsync(Bytes("done"), ct);
        await pipe.Writer.CompleteAsync();
        Assert.Equal(PutOutcome.Created, await put);

        byte[]? seen = await read;
        Assert.True(seen is null || seen.AsSpan().SequenceEqual(Bytes("halfdone")), seen is null ? null : Encoding.UTF8.GetString(seen));
    }

    private static async Task<byte[]?> ReadWholeOrAbsentAsync(IObjectStore store, string key, CancellationToken ct)
    {
        try
        {
            using ObjectRange range = await store.GetRangeAsync(key, 0, 64, ct);
            return BuffersExtensions.ToArray(range.Bytes);
        }
        catch (ObjectNotFoundException)
        {
            return null;
        }
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
            // A thread of its own each, since the barrier blocks it: sixteen pool threads blocked at once
            // would wait for the pool to grow.
            byte[] content = Bytes($"writer {writer}");
            puts[writer] = Task.Factory.StartNew(
                async () =>
                {
                    barrier.SignalAndWait();
                    return await store.PutIfAbsentAsync("commit/00000000000000000001.vxc", content, default);
                },
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default).Unwrap();
        }

        PutOutcome[] outcomes = await Task.WhenAll(puts);
        Assert.Equal(1, outcomes.Count(outcome => outcome == PutOutcome.Created));
        Assert.Equal(writers - 1, outcomes.Count(outcome => outcome == PutOutcome.Exists));

        // And the object that is there is one writer's bytes, whole.
        using ObjectRange range = await store.GetRangeAsync("commit/00000000000000000001.vxc", 0, 64, ct);
        Assert.StartsWith("writer ", Encoding.UTF8.GetString(range.Bytes), StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(PathStores))]
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

    [Fact]
    public async Task EachStoreDeclaresWhatItDoesInOneRequest()
    {
        ObjectStoreCapabilities all = ObjectStoreCapabilities.ReadsSuffix | ObjectStoreCapabilities.ListsInOrder
            | ObjectStoreCapabilities.ReportsLocksInListing | ObjectStoreCapabilities.ReplacesOnTokenMatch
            | ObjectStoreCapabilities.KeepsLocks;
        await using IObjectStore memory = Open("memory");
        await using IObjectStore file = Open("file");
        Assert.Equal(all, memory.Capabilities);

        // No file system compares and renames in one step across processes, and a directory keeps no lock.
        Assert.Equal(all & ~ObjectStoreCapabilities.ReplacesOnTokenMatch & ~ObjectStoreCapabilities.KeepsLocks, file.Capabilities);
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task AReadFillsTheCallersMemoryAndSaysTheLengthAndTheToken(string kind)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using IObjectStore store = Open(kind);
        byte[] content = Bytes("0123456789");
        await store.PutIfAbsentAsync("data/read", content, ct);
        string token = (await store.HeadAsync("data/read", ct))!.Value.Token;

        // The bytes land from the destination's start, as many as the object has past the offset.
        byte[] destination = new byte[16];
        Assert.Equal(new ObjectRead(4, 10, token), await store.ReadAsync("data/read", 6, destination, null, ct));
        Assert.Equal(Bytes("6789"), destination[..4]);
        Assert.Equal(new ObjectRead(3, 10, token), await store.ReadAsync("data/read", 2, destination.AsMemory(0, 3), token, ct));
        Assert.Equal(Bytes("234"), destination[..3]);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            async () => await store.ReadAsync("data/read", 10, destination, null, ct));
        await Assert.ThrowsAsync<ObjectNotFoundException>(
            async () => await store.ReadAsync("data/none", 0, destination, null, ct));

        // An empty object reads at offset 0, the one offset it has.
        await store.PutIfAbsentAsync("data/empty", default, ct);
        Assert.Equal((0, 0L), Pick(await store.ReadAsync("data/empty", 0, destination, null, ct)));
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task ATailReadSaysTheLengthWithTheLastBytes(string kind)
    {
        // What opens a file of unknown length in one request on a store that reads suffixes.
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using IObjectStore store = Open(kind);
        await store.PutIfAbsentAsync("data/tail", Bytes("0123456789"), ct);
        string token = (await store.HeadAsync("data/tail", ct))!.Value.Token;

        byte[] destination = new byte[4];
        Assert.Equal(new ObjectRead(4, 10, token), await store.ReadTailAsync("data/tail", destination, ct));
        Assert.Equal(Bytes("6789"), destination);

        byte[] larger = new byte[64];
        Assert.Equal(new ObjectRead(10, 10, token), await store.ReadTailAsync("data/tail", larger, ct));
        Assert.Equal(Bytes("0123456789"), larger[..10]);

        await store.PutIfAbsentAsync("data/empty", default, ct);
        Assert.Equal((0, 0L), Pick(await store.ReadTailAsync("data/empty", larger, ct)));
        await Assert.ThrowsAsync<ObjectNotFoundException>(
            async () => await store.ReadTailAsync("data/none", larger, ct));
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task AReadBoundToATokenRefusesAnotherObject(string kind)
    {
        // The object a reader opened, deleted and created again under its key: the read asks for the
        // token it knows, and the store refuses to send the new object's bytes.
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using IObjectStore store = Open(kind);
        await store.PutIfAbsentAsync("data/bound", Bytes("first"), ct);
        string first = (await store.HeadAsync("data/bound", ct))!.Value.Token;
        await store.DeleteAsync(["data/bound"], ct);
        await store.PutIfAbsentAsync("data/bound", Bytes("second, longer"), ct);

        byte[] destination = new byte[32];
        await Assert.ThrowsAsync<VortexFormatException>(
            async () => await store.ReadAsync("data/bound", 0, destination, first, ct));
        string second = (await store.HeadAsync("data/bound", ct))!.Value.Token;
        Assert.Equal(14, (await store.ReadAsync("data/bound", 0, destination, second, ct)).BytesRead);
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task APutWithoutAConditionReplacesTheObjectWhole(string kind)
    {
        // A file written to a location replaces the object as a rename replaces a path's file.
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using IObjectStore store = Open(kind);
        await store.PutIfAbsentAsync("files/replaced.vortex", Bytes("first"), ct);
        string first = (await store.HeadAsync("files/replaced.vortex", ct))!.Value.Token;

        Assert.Equal(PutOutcome.Created, await PutAsync(store, "files/replaced.vortex", Bytes("second, longer"), PutCondition.None, ct));
        ObjectHead head = (await store.HeadAsync("files/replaced.vortex", ct))!.Value;
        Assert.Equal(14, head.Length);
        Assert.NotEqual(first, head.Token);
        Assert.Equal(PutOutcome.Created, await PutAsync(store, "files/fresh.vortex", Bytes("new"), PutCondition.None, ct));

        // Nothing else is left under the prefix, a temporary file of the file store included.
        Assert.Equal(["files/fresh.vortex", "files/replaced.vortex"], await store.ListAsync("files/", null, 10, ct));
    }

    [Fact]
    public async Task APutOnATokenReplacesOnlyTheObjectThatStillHasIt()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using MemoryObjectStore store = new MemoryObjectStore();
        await store.PutIfAbsentAsync("files/appended.vortex", Bytes("first"), ct);
        string first = (await store.HeadAsync("files/appended.vortex", ct))!.Value.Token;

        Assert.Equal(PutOutcome.Created, await PutAsync(store, "files/appended.vortex", Bytes("second"), PutCondition.IfMatch(first), ct));

        // Another writer got there first: its token is not the one this writer read.
        Assert.Equal(PutOutcome.Changed, await PutAsync(store, "files/appended.vortex", Bytes("third"), PutCondition.IfMatch(first), ct));
        Assert.Equal(PutOutcome.Changed, await PutAsync(store, "files/none.vortex", Bytes("third"), PutCondition.IfMatch(first), ct));
        using ObjectRange range = await store.GetRangeAsync("files/appended.vortex", 0, 64, ct);
        Assert.Equal(Bytes("second"), range.Bytes.ToArray());
        Assert.Null(await store.HeadAsync("files/none.vortex", ct));
    }

    [Fact]
    public async Task AFileStoreRefusesAPutOnATokenItCannotHold()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using IObjectStore store = Open("file");
        await store.PutIfAbsentAsync("files/one.vortex", Bytes("first"), ct);
        string token = (await store.HeadAsync("files/one.vortex", ct))!.Value.Token;
        await Assert.ThrowsAsync<NotSupportedException>(
            async () => await PutAsync(store, "files/one.vortex", Bytes("second"), PutCondition.IfMatch(token), ct));
        Assert.Equal(5, (await store.HeadAsync("files/one.vortex", ct))!.Value.Length);
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task AListingCarriesEachObjectsHead(string kind)
    {
        // What spares vacuum a head per object: the listing says length, token and date.
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using IObjectStore store = Open(kind);
        await store.PutIfAbsentAsync("data/a.vortex", Bytes("a"), ct);
        await store.PutIfAbsentAsync("data/b.vortex", Bytes("bb"), ct);

        List<ObjectListing> listed = await store.ListAsync("data/", null, ct).ToListAsync(ct);
        Assert.Equal(["data/a.vortex", "data/b.vortex"], listed.Select(o => o.Key));
        foreach (ObjectListing entry in listed)
        {
            Assert.Equal(await store.HeadAsync(entry.Key, ct), entry.Head);
            Assert.Equal((store.Capabilities & ObjectStoreCapabilities.ReportsLocksInListing) != 0, entry.LockReported);
        }
    }

    [Fact]
    public async Task AMemoryStoreListsTheLocksItKeeps()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        ManualClock clock = new ManualClock(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero));
        await using MemoryObjectStore store = new MemoryObjectStore { TimeProvider = clock, RetainFor = TimeSpan.FromDays(30) };
        await store.PutIfAbsentAsync("data/kept", Bytes("kept"), ct);
        store.Hold("data/kept", held: true);

        ObjectListing listed = Assert.Single(await store.ListAsync("data/", null, ct).ToListAsync(ct));
        Assert.Equal((clock.Now + TimeSpan.FromDays(30), true, true), (listed.Head.RetainUntil, listed.Head.LegalHold, listed.LockReported));
    }

    private static (int BytesRead, long ObjectLength) Pick(ObjectRead read) => (read.BytesRead, read.ObjectLength);

    private static ValueTask<PutOutcome> PutAsync(IObjectStore store, string key, byte[] content, PutCondition condition, CancellationToken ct) =>
        store.PutAsync(key, PipeReader.Create(new ReadOnlySequence<byte>(content)), content.Length, condition, ct);

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
