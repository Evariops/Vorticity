// The upload every store makes the same way, against the S3 emulator: bytes in memory sent where they
// lie, whole or as slices; a put bound to go whole, bounded in memory and spooled on disk on demand;
// parts that grow with an unknown length; a write that lands after the head that found nothing, known
// for this put's own; and an upload the service asks to make again, made again from bytes it can send
// again.
using System;
using System.Buffers;
using System.IO;
using System.IO.Pipelines;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.IO;
using Vorticity.ObjectStorage;
using Xunit;

namespace Vorticity.Tests.ObjectStorage;

public sealed class UploadTests : IDisposable
{
    private const string Bucket = "lake";

    private readonly string _spool = Directory.CreateTempSubdirectory("vorticity-spool-").FullName;

    public void Dispose() => Directory.Delete(_spool, recursive: true);

    [Fact]
    public async Task BytesInMemoryGoWholeOrAsSlicesOfThemselves()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using S3Emulator emulator = new S3Emulator { MinimumPartSize = 64 << 10 };
        await using S3ObjectStore store = Store(emulator);

        byte[] small = Bytes(1_000, 1);
        Assert.Equal(PutOutcome.Created, await store.PutAsync("commit/1", new ReadOnlySequence<byte>(small), PutCondition.IfAbsent, ct));
        Assert.Equal(["PUT /commit/1"], emulator.Log);
        Assert.Equal(small, emulator.BytesOf(Bucket, "commit/1"));

        // Past the threshold, in parts that are slices of the caller's sequence, across its segments.
        byte[] large = Bytes(600_000, 2);
        Assert.Equal(PutOutcome.Created, await store.PutAsync("data/1", Segmented(large, 100_000), PutCondition.None, ct));
        Assert.Contains(emulator.Log, line => line.StartsWith("POST /data/1?uploads", StringComparison.Ordinal));
        Assert.Equal(large, emulator.BytesOf(Bucket, "data/1"));
        Assert.Equal(0, emulator.OpenUploads);
    }

    [Fact]
    public async Task AConditionalWriteThatLandsAfterTheHeadIsKnownForThisPutsOwn()
    {
        // The first attempt's answer is lost and the write lands only after the head that found nothing:
        // the second attempt is refused on its condition, by this put's own write, which its id proves.
        CancellationToken ct = TestContext.Current.CancellationToken;
        using S3Emulator emulator = new S3Emulator();
        int late = 0;
        emulator.LandsLate = request => request.Method == HttpMethod.Put && Interlocked.Increment(ref late) == 1;
        await using S3ObjectStore store = Store(emulator);

        byte[] content = Bytes(100, 3);
        Assert.Equal(PutOutcome.Created, await store.PutIfAbsentAsync("commit/1", content, ct));
        Assert.Equal(["PUT /commit/1", "HEAD /commit/1", "PUT /commit/1", "HEAD /commit/1"], emulator.Log);
        Assert.Equal(content, emulator.BytesOf(Bucket, "commit/1"));
    }

    [Fact]
    public async Task APutBoundToGoWholeIsBoundedInMemoryAndSpooledOnDiskWhenAsked()
    {
        // R2 takes a condition only on a whole put: past the ceiling a put would hold in memory, it is
        // refused before a byte goes, whatever its length says, unless a spool directory holds it.
        CancellationToken ct = TestContext.Current.CancellationToken;
        using S3Emulator emulator = new S3Emulator { Region = "auto" };
        byte[] content = Bytes(600_000, 6);
        await using (S3ObjectStore bounded = Store(emulator, S3Profile.R2, new StoreTransportOptions { RetryBaseDelay = TimeSpan.Zero, MaxHeldBytes = 256 << 10 }))
        {
            foreach (long length in new long[] { content.Length, -1 })
            {
                ObjectStoreException refused = await Assert.ThrowsAsync<ObjectStoreException>(
                    async () => await bounded.PutAsync("commit/1", PipeReader.Create(new ReadOnlySequence<byte>(content)), length, PutCondition.IfAbsent, ct));
                Assert.Contains("SpoolDirectory", refused.Message, StringComparison.Ordinal);
            }

            Assert.Empty(emulator.Log);
        }

        await using S3ObjectStore spooled = Store(emulator, S3Profile.R2, new StoreTransportOptions
        {
            RetryBaseDelay = TimeSpan.Zero,
            MaxHeldBytes = 256 << 10,
            SpoolDirectory = _spool,
        });
        Assert.Equal(PutOutcome.Created, await spooled.PutAsync("commit/1", PipeReader.Create(new ReadOnlySequence<byte>(content)), -1, PutCondition.IfAbsent, ct));
        Assert.Equal(["PUT /lake/commit/1"], emulator.Log);
        Assert.Equal(content, emulator.BytesOf(Bucket, "commit/1"));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_spool));
    }

    [Fact]
    public async Task AStreamSpooledOnDiskIsSentAgainFromThereAndItsFileGoes()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using S3Emulator emulator = new S3Emulator { MinimumPartSize = 64 << 10 };
        int parts = 0;
        emulator.Intercept = (request, _) => request.RequestUri!.Query.Contains("partNumber", StringComparison.Ordinal) && Interlocked.Increment(ref parts) == 2
            ? S3Emulator.Error(HttpStatusCode.InternalServerError, "InternalError", "try again")
            : null;
        await using S3ObjectStore store = Store(emulator, transport: new StoreTransportOptions { RetryBaseDelay = TimeSpan.Zero, SpoolDirectory = _spool });
        byte[] content = Bytes(600_000, 7);
        Assert.Equal(PutOutcome.Created, await store.PutAsync("data/1", PipeReader.Create(new ReadOnlySequence<byte>(content)), -1, PutCondition.None, ct));
        Assert.Equal(content, emulator.BytesOf(Bucket, "data/1"));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_spool));
    }

    [Fact]
    public async Task AnUploadTheServiceAsksToMakeAgainIsMadeAgainFromBytesItCanSendAgain()
    {
        // The first completion meets a concurrent operation: from bytes in memory, the upload is made
        // again; from a stream read as it went, the put says how to let the store make it again.
        CancellationToken ct = TestContext.Current.CancellationToken;
        using S3Emulator emulator = new S3Emulator { MinimumPartSize = 64 << 10 };
        int completions = 0;
        emulator.Intercept = (request, _) => request.Method == HttpMethod.Post && request.RequestUri!.Query.Contains("uploadId", StringComparison.Ordinal)
            && Interlocked.Increment(ref completions) == 1
                ? S3Emulator.Error(HttpStatusCode.Conflict, "ConditionalRequestConflict", "A conflicting conditional operation is in progress.")
                : null;
        await using S3ObjectStore store = Store(emulator);
        byte[] content = Bytes(600_000, 8);

        Assert.Equal(PutOutcome.Created, await store.PutAsync("data/1", new ReadOnlySequence<byte>(content), PutCondition.IfAbsent, ct));
        Assert.Equal(2, emulator.Log.Count(line => line.EndsWith("?uploads", StringComparison.Ordinal)));
        Assert.Equal(content, emulator.BytesOf(Bucket, "data/1"));
        Assert.Equal(0, emulator.OpenUploads);

        completions = 0;
        ObjectStoreException refused = await Assert.ThrowsAsync<ObjectStoreException>(
            async () => await store.PutAsync("data/2", PipeReader.Create(new ReadOnlySequence<byte>(content)), -1, PutCondition.IfAbsent, ct));
        Assert.Contains("SpoolDirectory", refused.Message, StringComparison.Ordinal);
        Assert.Equal(0, emulator.OpenUploads);
    }

    [Fact]
    public async Task ThePartsOfAnObjectOfUnknownLengthGrowWithTheirNumber()
    {
        StoreCredentials keys = StoreCredentials.AccessKey("id", "secret");
        await using S3ObjectStore amazon = new S3ObjectStore(new S3StoreOptions { Bucket = "b", Region = "eu-west-3", Credentials = keys });
        await using S3ObjectStore scaleway = S3ObjectStore.ForScaleway("fr-par", "b", keys);
        await using S3ObjectStore r2 = S3ObjectStore.ForR2("account", "b", keys);

        // Doubled every tenth of the parts the service takes, up to eight times: about 1 TiB on S3.
        Assert.Equal(
            [16 << 20, 16 << 20, 32 << 20, 64 << 20, 128 << 20, 128 << 20],
            new[] { 1, 1_000, 1_001, 2_001, 3_001, 10_000 }.Select(number => amazon.PartSizeFor(-1, number)));
        Assert.Equal(32 << 20, scaleway.PartSizeFor(-1, 101));

        // R2 wants every part but the last of one size.
        Assert.Equal(16 << 20, r2.PartSizeFor(-1, 5_000));
    }

    [Fact]
    public async Task APutBoundedInLengthStreamsWhereAnUnknownOneIsSpooled()
    {
        // A spool directory nothing can be written into: what goes through it fails, what streams does not.
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("A directory is made read-only by its Unix mode.");
        }
        else
        {
            await BoundedUnderALockedSpoolAsync(TestContext.Current.CancellationToken);
        }
    }

    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    private async Task BoundedUnderALockedSpoolAsync(CancellationToken ct)
    {
        string locked = Directory.CreateDirectory(Path.Combine(_spool, "locked")).FullName;
        StoreTransportOptions unusable = new StoreTransportOptions { RetryBaseDelay = TimeSpan.Zero, SpoolDirectory = locked };
        using S3Emulator emulator = new S3Emulator { MinimumPartSize = 64 << 10 };
        await using S3ObjectStore store = Store(emulator, transport: unusable);
        System.IO.File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            await BoundedAsync(emulator, store, ct);
        }
        finally
        {
            System.IO.File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private static async Task BoundedAsync(S3Emulator emulator, S3ObjectStore store, CancellationToken ct)
    {
        byte[] content = Bytes(600_000, 9);

        // Past the part threshold, in parts sized by the bound; under it, whole.
        Assert.Equal(PutOutcome.Created, await store.PutAsync("data/1", PipeReader.Create(Segmented(content, 7_000)), -1, 700_000, PutCondition.None, ct));
        Assert.Equal(content, emulator.BytesOf(Bucket, "data/1"));
        Assert.Equal(10, emulator.Log.Count(line => line.Contains("partNumber", StringComparison.Ordinal)));
        Assert.Equal(PutOutcome.Created, await store.PutAsync("data/2", PipeReader.Create(new ReadOnlySequence<byte>(content.AsMemory(0, 100_000))), -1, 200_000, PutCondition.None, ct));
        Assert.Equal(content.AsSpan(0, 100_000).ToArray(), emulator.BytesOf(Bucket, "data/2"));
        Assert.Equal("PUT /data/2", emulator.Log.Last());
        await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(
            async () => await store.PutAsync("data/3", PipeReader.Create(new ReadOnlySequence<byte>(content)), -1, PutCondition.None, ct));

        // Content past its bound is refused, and nothing is left behind, in parts or whole.
        foreach (long bound in new long[] { 500_000, 200_000 })
        {
            ObjectStoreException past = await Assert.ThrowsAsync<ObjectStoreException>(
                async () => await store.PutAsync("data/4", PipeReader.Create(new ReadOnlySequence<byte>(content)), -1, bound, PutCondition.None, ct));
            Assert.Contains("at most", past.Message, StringComparison.Ordinal);
        }

        Assert.Equal(0, emulator.OpenUploads);
        Assert.Null(emulator.BytesOf(Bucket, "data/4"));

        // A bound past what the service's parts reach goes to the spool, and is refused before a byte
        // goes without a spool.
        await using S3ObjectStore plain = Store(emulator);
        int before = emulator.Log.Count;
        ObjectStoreException far = await Assert.ThrowsAsync<ObjectStoreException>(
            async () => await plain.PutAsync("data/5", PipeReader.Create(new ReadOnlySequence<byte>(content)), -1, 1L << 50, PutCondition.None, ct));
        Assert.Contains("SpoolDirectory", far.Message, StringComparison.Ordinal);
        Assert.Equal(before, emulator.Log.Count);

        // On R2, the parts of a bounded put are of one size.
        await using S3ObjectStore r2 = Store(emulator, S3Profile.R2);
        Assert.Equal(r2.PartSizeFor(700_000, 1), r2.PartSizeFor(700_000, 9));
    }

    private static S3ObjectStore Store(S3Emulator emulator, S3Profile? profile = null, StoreTransportOptions? transport = null) =>
        new S3ObjectStore(new S3StoreOptions
        {
            Bucket = Bucket,
            Region = profile == S3Profile.R2 ? null : "eu-west-3",
            Profile = profile,
            Endpoint = profile is null ? null : new Uri("https://storage.example.com"),
            Credentials = StoreCredentials.AccessKey(S3Emulator.AccessKey, S3Emulator.Secret),
            Handler = emulator,
            PartThreshold = 256 << 10,
            PartSize = 64 << 10,
            MinimumPartSize = 64 << 10,
            Transport = transport ?? new StoreTransportOptions { RetryBaseDelay = TimeSpan.Zero },
        });

    private static byte[] Bytes(int length, int seed)
    {
        byte[] bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    /// <summary>The bytes as a sequence of segments of <paramref name="segment"/> bytes, as a caller's chunks are.</summary>
    private static ReadOnlySequence<byte> Segmented(byte[] bytes, int segment)
    {
        Chunk first = new Chunk(bytes.AsMemory(0, Math.Min(segment, bytes.Length)), 0);
        Chunk last = first;
        for (int at = segment; at < bytes.Length; at += segment)
        {
            last = last.Then(bytes.AsMemory(at, Math.Min(segment, bytes.Length - at)));
        }

        return new ReadOnlySequence<byte>(first, 0, last, last.Memory.Length);
    }

    private sealed class Chunk : ReadOnlySequenceSegment<byte>
    {
        internal Chunk(ReadOnlyMemory<byte> memory, long runningIndex)
        {
            Memory = memory;
            RunningIndex = runningIndex;
        }

        internal Chunk Then(ReadOnlyMemory<byte> memory)
        {
            Chunk next = new Chunk(memory, RunningIndex + Memory.Length);
            Next = next;
            return next;
        }
    }
}
