// The S3 store's write path against the emulator: puts whole and in parts, their conditions and
// checksums, the outcome of a conditional put whose answer was lost, and deletes in batches.
using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.IO;
using Vorticity.ObjectStorage;
using Xunit;

namespace Vorticity.Tests.ObjectStorage;

public sealed class S3WriteTests
{
    private const string Bucket = "lake";

    /// <summary>A store whose parts are small, so that a test sends a multipart upload in a few hundred kilobytes.</summary>
    private static S3ObjectStore Small(S3Emulator emulator, S3Profile? profile = null, S3Checksum checksum = S3Checksum.Default, int attempts = 5) =>
        new S3ObjectStore(new S3StoreOptions
        {
            Bucket = Bucket,
            Region = "eu-west-3",
            Profile = profile,
            Endpoint = profile is null ? null : new Uri("https://storage.example.com"),
            Credentials = StoreCredentials.AccessKey(S3Emulator.AccessKey, S3Emulator.Secret),
            Handler = emulator,
            Checksum = checksum,
            PartThreshold = 256 << 10,
            PartSize = 64 << 10,
            MinimumPartSize = 64 << 10,
            Transport = new StoreTransportOptions { RetryBaseDelay = TimeSpan.Zero, MaxAttempts = attempts },
        });

    [Fact]
    public async Task APutUpToTheThresholdGoesWholeWithItsChecksumAndCondition()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using S3Emulator emulator = new S3Emulator();
        await using S3ObjectStore store = Small(emulator);
        byte[] first = Bytes(1_000, 1);

        Assert.Equal(PutOutcome.Created, await PutAsync(store, "commit/1", first, PutCondition.IfAbsent, ct));
        Assert.Equal(["PUT /commit/1"], emulator.Log);
        Assert.Equal(first, emulator.BytesOf(Bucket, "commit/1"));
        Assert.Matches("^[0-9a-f]{32}$", emulator.MetadataOf(Bucket, "commit/1")[S3ObjectStore.PutIdHeader]);

        Assert.Equal(PutOutcome.Exists, await PutAsync(store, "commit/1", Bytes(10, 2), PutCondition.IfAbsent, ct));
        Assert.Equal(first, emulator.BytesOf(Bucket, "commit/1"));

        string token = emulator.ETagOf(Bucket, "commit/1");
        Assert.Equal(PutOutcome.Created, await PutAsync(store, "commit/1", Bytes(20, 3), PutCondition.IfMatch(token), ct));
        Assert.Equal(PutOutcome.Changed, await PutAsync(store, "commit/1", Bytes(30, 4), PutCondition.IfMatch(token), ct));
        Assert.Equal(PutOutcome.Changed, await PutAsync(store, "commit/none", Bytes(30, 4), PutCondition.IfMatch(token), ct));
        Assert.Equal(PutOutcome.Created, await PutAsync(store, "commit/1", Bytes(40, 5), PutCondition.None, ct));
        Assert.Equal(Bytes(40, 5), emulator.BytesOf(Bucket, "commit/1"));

        // Content that is not the length announced writes nothing.
        await Assert.ThrowsAsync<ObjectStoreException>(async () => await store.PutAsync("short", Reader(Bytes(10, 6)), 11, PutCondition.None, ct));
        await Assert.ThrowsAsync<ObjectStoreException>(async () => await store.PutAsync("long", Reader(Bytes(12, 6)), 11, PutCondition.None, ct));
        Assert.Null(emulator.BytesOf(Bucket, "short"));
        Assert.Null(emulator.BytesOf(Bucket, "long"));
    }

    [Theory]
    [InlineData(S3Checksum.Default)]
    [InlineData(S3Checksum.ContentMd5)]
    [InlineData(S3Checksum.None)]
    public async Task EachChecksumIsOneTheServiceVerifies(S3Checksum checksum)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using S3Emulator emulator = new S3Emulator { RequireChecksum = checksum != S3Checksum.None };
        await using S3ObjectStore store = Small(emulator, checksum: checksum);
        Assert.Equal(PutOutcome.Created, await PutAsync(store, "whole", Bytes(1_000, 7), PutCondition.None, ct));
        Assert.Equal(PutOutcome.Created, await PutAsync(store, "parts", Bytes(300_000, 8), PutCondition.None, ct));
        Assert.Equal(Bytes(300_000, 8), emulator.BytesOf(Bucket, "parts"));
    }

    [Fact]
    public async Task ALargerPutGoesUpInPartsAndAppearsWholeAtTheCompletion()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using S3Emulator emulator = new S3Emulator();
        await using S3ObjectStore store = Small(emulator);
        byte[] content = Bytes((1 << 20) + 123, 9);
        Assert.Equal(PutOutcome.Created, await PutAsync(store, "data/big", content, PutCondition.None, ct));
        Assert.Equal(content, emulator.BytesOf(Bucket, "data/big"));
        Assert.Equal("POST /data/big?uploads", emulator.Log.First());
        Assert.Equal(17, emulator.Log.Count(line => line.StartsWith("PUT /data/big?partNumber=", StringComparison.Ordinal)));
        Assert.StartsWith("POST /data/big?uploadId=", emulator.Log.Last(), StringComparison.Ordinal);
        Assert.Equal(0, emulator.OpenUploads);
    }

    [Theory]
    [InlineData(1_000, 1)]
    [InlineData(200_000, 1)]
    [InlineData(700_001, 13)]
    public async Task APutOfUnknownLengthGoesWholeUpToTheThresholdAndInPartsPast(int length, int requests)
    {
        // What a file written to a location sends: its length is known at its end only.
        CancellationToken ct = TestContext.Current.CancellationToken;
        using S3Emulator emulator = new S3Emulator();
        await using S3ObjectStore store = Small(emulator);
        byte[] content = Bytes(length, 10);
        Pipe pipe = new Pipe(new PipeOptions(pauseWriterThreshold: 32 << 10, resumeWriterThreshold: 16 << 10));
        Task<PutOutcome> put = store.PutAsync("files/out", pipe.Reader, -1, PutCondition.None, ct).AsTask();
        for (int at = 0; at < content.Length; at += 10_007)
        {
            await pipe.Writer.WriteAsync(content.AsMemory(at, Math.Min(10_007, content.Length - at)), ct);
        }

        await pipe.Writer.CompleteAsync();
        Assert.Equal(PutOutcome.Created, await put);
        Assert.Equal(content, emulator.BytesOf(Bucket, "files/out"));
        Assert.Equal(requests, emulator.Requests);
    }

    [Fact]
    public async Task AnUploadThatFailsOrIsGivenUpIsAborted()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using S3Emulator emulator = new S3Emulator();
        emulator.Intercept = (request, _) => request.RequestUri!.Query.Contains("partNumber=3", StringComparison.Ordinal)
            ? S3Emulator.Error(HttpStatusCode.Forbidden, "AccessDenied", "Access Denied")
            : null;
        await using S3ObjectStore store = Small(emulator);
        await Assert.ThrowsAsync<ObjectStoreException>(async () => await PutAsync(store, "data/big", Bytes(1 << 20, 11), PutCondition.None, ct));
        Assert.Null(emulator.BytesOf(Bucket, "data/big"));
        Assert.Equal(0, emulator.OpenUploads);
        Assert.Contains(emulator.Log, line => line.StartsWith("DELETE /data/big?uploadId=", StringComparison.Ordinal));

        // A writer that fails its pipe: the upload begun is aborted too.
        emulator.Intercept = null;
        Pipe pipe = new Pipe();
        Task<PutOutcome> put = store.PutAsync("files/abandoned", pipe.Reader, -1, PutCondition.None, ct).AsTask();
        await pipe.Writer.WriteAsync(Bytes(600_000, 12), ct);
        await pipe.Writer.CompleteAsync(new OperationCanceledException("abandoned"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await put);
        Assert.Null(emulator.BytesOf(Bucket, "files/abandoned"));
        Assert.Equal(0, emulator.OpenUploads);
    }

    [Fact]
    public async Task ACompletionRefusedOnItsConditionLeavesNoUploadBehind()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using S3Emulator emulator = new S3Emulator();
        emulator.Seed(Bucket, "commit/big", Bytes(10, 13));
        await using S3ObjectStore store = Small(emulator);
        Assert.Equal(PutOutcome.Exists, await PutAsync(store, "commit/big", Bytes(600_000, 14), PutCondition.IfAbsent, ct));
        Assert.Equal(Bytes(10, 13), emulator.BytesOf(Bucket, "commit/big"));
        Assert.Equal(0, emulator.OpenUploads);
    }

    [Fact]
    public async Task OnR2AConditionalPutGoesWholeWhateverItsSize()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using S3Emulator emulator = new S3Emulator();
        await using S3ObjectStore store = Small(emulator, S3Profile.R2);
        Assert.Equal(PutOutcome.Created, await PutAsync(store, "commit/big", Bytes(600_000, 15), PutCondition.IfAbsent, ct));
        Assert.Equal(["PUT /lake/commit/big"], emulator.Log);
        Assert.Equal(PutOutcome.Created, await PutAsync(store, "data/big", Bytes(600_000, 15), PutCondition.None, ct));
        Assert.Contains(emulator.Log, line => line.StartsWith("POST /lake/data/big?uploads", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AConditionalPutWhoseAnswerIsLostLearnsItsOutcomeFromTheHead(bool parts)
    {
        // The put lands and its answer never comes: the head finds this put's id, and the put is told
        // it created the object rather than that another writer did.
        CancellationToken ct = TestContext.Current.CancellationToken;
        using S3Emulator emulator = new S3Emulator();
        int lost = 0;
        emulator.LoseAnswer = request => request.Method != HttpMethod.Head
            && !request.RequestUri!.Query.Contains("partNumber", StringComparison.Ordinal)
            && !request.RequestUri.Query.Contains("uploads", StringComparison.Ordinal)
            && Interlocked.Increment(ref lost) == 1;
        await using S3ObjectStore store = Small(emulator);
        byte[] content = Bytes(parts ? 600_000 : 1_000, 16);
        Assert.Equal(PutOutcome.Created, await PutAsync(store, "commit/1", content, PutCondition.IfAbsent, ct));
        Assert.Equal(content, emulator.BytesOf(Bucket, "commit/1"));
        Assert.Equal("HEAD /commit/1", emulator.Log.Last());
    }

    [Fact]
    public async Task AConditionalPutThatNeverLandedIsSentAgainAndOneThatAnotherWonIsTakenFound()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using S3Emulator emulator = new S3Emulator();
        await using S3ObjectStore store = Small(emulator);

        // The connection fails before the service sees the put: the head finds nothing, the put goes again.
        emulator.Intercept = (request, index) => index == 0 ? throw new HttpRequestException("connection reset") : null;
        Assert.Equal(PutOutcome.Created, await PutAsync(store, "commit/1", Bytes(100, 17), PutCondition.IfAbsent, ct));
        Assert.Equal(["PUT /commit/1", "HEAD /commit/1", "PUT /commit/1"], emulator.Log);

        // The answer is lost, and the object the head finds is another writer's: the key was taken.
        emulator.Intercept = (request, index) =>
        {
            if (request.Method == HttpMethod.Put)
            {
                emulator.Seed(Bucket, "commit/2", Bytes(5, 18));
                throw new HttpRequestException("connection reset");
            }

            return null;
        };
        Assert.Equal(PutOutcome.Exists, await PutAsync(store, "commit/2", Bytes(100, 19), PutCondition.IfAbsent, ct));
        Assert.Equal(Bytes(5, 18), emulator.BytesOf(Bucket, "commit/2"));
    }

    [Fact]
    public async Task AConditionalPutWhoseHeadFailsTooSaysItsOutcomeIsUnknown()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using S3Emulator emulator = new S3Emulator();
        emulator.Intercept = (_, _) => throw new HttpRequestException("network down");
        await using S3ObjectStore store = Small(emulator, attempts: 2);
        ObjectStoreException unknown = await Assert.ThrowsAsync<ObjectStoreException>(
            async () => await PutAsync(store, "commit/1", Bytes(100, 20), PutCondition.IfAbsent, ct));
        Assert.True(unknown.OutcomeUnknown);
        Assert.Contains("outcome is unknown", unknown.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.Conflict, "ConditionalRequestConflict")]
    [InlineData(HttpStatusCode.BadRequest, "BadDigest")]
    public async Task APutTheServiceRefusedWithoutApplyingIsSentAgain(HttpStatusCode status, string code)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using S3Emulator emulator = new S3Emulator();
        emulator.Intercept = (_, index) => index == 0 ? S3Emulator.Error(status, code, "try again") : null;
        await using S3ObjectStore store = Small(emulator);
        Assert.Equal(PutOutcome.Created, await PutAsync(store, "commit/1", Bytes(100, 21), PutCondition.IfAbsent, ct));
        Assert.Equal(["PUT /commit/1", "PUT /commit/1"], emulator.Log);
    }

    [Fact]
    public async Task ADeleteGoesInBatchesOfAThousandKeysWithTheirDigest()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using S3Emulator emulator = new S3Emulator();
        List<string> keys = [.. Enumerable.Range(0, 2_500).Select(i => $"data/{i:D5} & <odd> \"key\"")];
        foreach (string key in keys)
        {
            emulator.Seed(Bucket, key, [1]);
        }

        await using S3ObjectStore store = Small(emulator);
        await store.DeleteAsync([.. keys, "data/absent"], ct);
        Assert.Equal(0, emulator.Count(Bucket));
        Assert.Equal(3, emulator.Log.Count(line => line == "POST /?delete"));

        // A key the lock keeps is reported, and the others go.
        emulator.Seed(Bucket, "kept", [1], retainUntil: DateTimeOffset.UtcNow.AddDays(1));
        emulator.Seed(Bucket, "free", [1]);
        ObjectStoreException refused = await Assert.ThrowsAsync<ObjectStoreException>(async () => await store.DeleteAsync(["kept", "free"], ct));
        Assert.Contains("'kept' (AccessDenied", refused.Message, StringComparison.Ordinal);
        Assert.Null(emulator.BytesOf(Bucket, "free"));
    }

    [Fact]
    public async Task AFileWrittenToALocationAppearsWholeWhenItsWriterCompletes()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using S3Emulator emulator = new S3Emulator();
        await using S3ObjectStore lake = Small(emulator);
        await using VortexSession session = VortexSession.Create(o => o.ObjectStores.Add("s3://lake", lake));
        LocationTests.Reading[] rows = [.. Enumerable.Range(0, 50_000).Select(i => new LocationTests.Reading(i / 100, i * 0.5))];
        await using (VortexFileWriter writer = session.CreateWriter<LocationTests.Reading>("s3://lake/out/readings.vortex"))
        {
            await writer.WriteAsync<LocationTests.Reading>(rows, ct);
            Assert.Null(emulator.BytesOf(Bucket, "out/readings.vortex"));
            await writer.CompleteAsync(ct);
            Assert.NotNull(emulator.BytesOf(Bucket, "out/readings.vortex"));
        }

        await using (VortexFile file = await session.OpenAsync("s3://lake/out/readings.vortex", cancellationToken: ct))
        {
            Assert.Equal(rows.Length, await file.Scan<LocationTests.Reading>().CountAsync(ct));
            Assert.Equal(rows.Sum(r => (long)r.Day), await file.Scan<LocationTests.Reading>().SumAsync(r => r.Day, ct));
        }

        // A writer given up leaves no object and no upload.
        await using (VortexFileWriter abandoned = session.CreateWriter<LocationTests.Reading>("s3://lake/out/abandoned.vortex"))
        {
            await abandoned.WriteAsync<LocationTests.Reading>(rows, ct);
        }

        Assert.Null(emulator.BytesOf(Bucket, "out/abandoned.vortex"));
        Assert.Equal(0, emulator.OpenUploads);
    }

    [Fact]
    public async Task ADatasetLivesInABucket()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using S3Emulator emulator = new S3Emulator();
        await using S3ObjectStore lake = Small(emulator);
        await using VortexSession session = VortexSession.Create(o => o.ObjectStores.Add("s3://lake", lake));
        Vorticity.Dataset.DatasetOptions options = new Vorticity.Dataset.DatasetOptions { Session = session, ClusteringKey = ["Day"] };
        await using (Vorticity.Dataset.VortexDataset dataset = await Vorticity.Dataset.VortexDataset.CreateAsync("s3://lake/tables/readings", LocationTests.Reading.Schema, options, ct))
        {
            for (int append = 0; append < 4; append++)
            {
                await using Vorticity.Dataset.ObjectDraft draft = dataset.StartObject();
                await draft.Writer.WriteAsync<LocationTests.Reading>([.. Enumerable.Range(append * 1_000, 1_000).Select(i => new LocationTests.Reading(i, i * 0.5))], ct);
                await dataset.AppendAsync(draft, ct);
            }

            Assert.NotNull(await dataset.CompactAsync(new Vorticity.Dataset.CompactionOptions { LevelZeroCeiling = 2, TargetBytesAtLevelOne = 1 << 20 }, ct));
        }

        await using (Vorticity.Dataset.VortexDataset reopened = await Vorticity.Dataset.VortexDataset.OpenAsync("s3://lake/tables/readings/", options, ct))
        {
            Assert.Equal(4_000, await reopened.Scan<LocationTests.Reading>().CountAsync(ct));
            Assert.Equal(1_000, await reopened.Scan<LocationTests.Reading>().Where(r => r.Day >= 3_000).CountAsync(ct));
            Vorticity.Dataset.VacuumResult swept = await reopened.VacuumAsync(
                new Vorticity.Dataset.VacuumOptions { TimeProvider = new Vorticity.Tests.Dataset.ManualClock(DateTimeOffset.UtcNow.AddDays(30)) }, ct);
            Assert.NotEmpty(swept.Deleted);
            Assert.Equal(4_000, await reopened.Scan<LocationTests.Reading>().CountAsync(ct));
        }

        // Every key the dataset touched lies under its location, in a path or a listing's prefix; a
        // batch delete's keys are in its body.
        Assert.All(emulator.Log.Where(line => line != "POST /?delete"), line => Assert.True(
            line.Contains("/tables/readings/", StringComparison.Ordinal) || line.Contains("prefix=tables%2Freadings%2F", StringComparison.Ordinal),
            line));
    }

    [Fact]
    public async Task AnEncryptedDatasetAndASealedFileLieSealedInABucket()
    {
        // Sealing happens above the store: the service is only ever sent sealed bytes.
        CancellationToken ct = TestContext.Current.CancellationToken;
        using S3Emulator emulator = new S3Emulator();
        using VortexKeyring keyring = Vorticity.Tests.Sealing.SealedObjects.Keyring();
        await using S3ObjectStore lake = Small(emulator);
        await using VortexSession session = VortexSession.Create(o =>
        {
            o.Keyring = keyring;
            o.EncryptFiles = true;
            o.ObjectStores.Add("s3://lake", lake);
        });
        Vorticity.Dataset.DatasetOptions options = new Vorticity.Dataset.DatasetOptions { Session = session, Encrypted = true };
        await using (Vorticity.Dataset.VortexDataset dataset = await Vorticity.Dataset.VortexDataset.CreateAsync("s3://lake/secret", LocationTests.Reading.Schema, options, ct))
        {
            await using Vorticity.Dataset.ObjectDraft draft = dataset.StartObject();
            await draft.Writer.WriteAsync<LocationTests.Reading>([.. Enumerable.Range(0, 2_000).Select(i => new LocationTests.Reading(i, i))], ct);
            await dataset.AppendAsync(draft, ct);
        }

        await using (VortexFileWriter writer = session.CreateWriter<LocationTests.Reading>("s3://lake/files/sealed.vortex"))
        {
            await writer.WriteAsync<LocationTests.Reading>([.. Enumerable.Range(0, 500).Select(i => new LocationTests.Reading(i, i))], ct);
            await writer.CompleteAsync(ct);
        }

        List<ObjectListing> stored;
        await using (S3ObjectStore raw = Small(emulator))
        {
            stored = await raw.ListAsync(string.Empty, null, ct).ToListAsync(ct);
        }

        Assert.NotEmpty(stored);
        Assert.All(stored, o => Assert.True(emulator.BytesOf(Bucket, o.Key).AsSpan().StartsWith("VXSEALED"u8), $"'{o.Key}' is not sealed"));
        await using (Vorticity.Dataset.VortexDataset reopened = await Vorticity.Dataset.VortexDataset.OpenAsync("s3://lake/secret", options, ct))
        {
            Assert.Equal(2_000, await reopened.Scan<LocationTests.Reading>().CountAsync(ct));
        }

        await using VortexFile file = await session.OpenAsync("s3://lake/files/sealed.vortex", cancellationToken: ct);
        Assert.Equal(500, await file.Scan<LocationTests.Reading>().CountAsync(ct));
    }

    private static ValueTask<PutOutcome> PutAsync(S3ObjectStore store, string key, byte[] content, PutCondition condition, CancellationToken ct) =>
        store.PutAsync(key, Reader(content), content.Length, condition, ct);

    private static PipeReader Reader(byte[] content) => PipeReader.Create(new ReadOnlySequence<byte>(content));

    private static byte[] Bytes(int length, int seed)
    {
        byte[] bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }
}
