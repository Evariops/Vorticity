// The Cloud Storage store against the emulator: generations as tokens and preconditions, the XML API's
// data and uploads in parts, the JSON API's metadata, listings and batches, and the tokens.
using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.IO;
using Vorticity.ObjectStorage;
using Xunit;

namespace Vorticity.Tests.ObjectStorage;

public sealed class GoogleTests
{
    private const string Bucket = "warehouse";

    internal static GoogleCloudStorageObjectStore Store(
        GoogleEmulator emulator, GoogleCredentials? credentials = null, bool readsSuffix = false, long threshold = 32L << 20, int part = 16 << 20) =>
        new GoogleCloudStorageObjectStore(new GoogleCloudStorageOptions
        {
            Bucket = Bucket,
            Credentials = credentials ?? GoogleCredentials.Anonymous(),
            Handler = emulator,
            ReadsSuffix = readsSuffix,
            PartThreshold = threshold,
            PartSize = part,
            Transport = new StoreTransportOptions { RetryBaseDelay = TimeSpan.Zero },
        });

    [Fact]
    public async Task AReadIsBoundToTheGenerationAndATailTakesTheMetadataFirst()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using GoogleEmulator emulator = new GoogleEmulator();
        emulator.Seed(Bucket, "data/one.vortex", Encoding.ASCII.GetBytes("0123456789"));
        emulator.Seed(Bucket, "empty", []);
        await using GoogleCloudStorageObjectStore store = Store(emulator);
        string generation = emulator.GenerationOf(Bucket, "data/one.vortex");

        byte[] four = new byte[4];
        Assert.Equal(new ObjectRead(4, 10, generation), await store.ReadAsync("data/one.vortex", 3, four, null, ct));
        Assert.Equal("3456", Encoding.ASCII.GetString(four));
        Assert.Equal("GET /warehouse/data/one.vortex bytes=3-6", emulator.Log.Single());

        Assert.Equal(new ObjectRead(4, 10, generation), await store.ReadTailAsync("data/one.vortex", four, ct));
        Assert.Equal("6789", Encoding.ASCII.GetString(four));
        Assert.Equal("GET /storage/v1/b/warehouse/o/data%2Fone.vortex", emulator.Log.ElementAt(1));
        Assert.Equal(3, emulator.Requests);

        Assert.Equal((0, 0L), (await store.ReadAsync("empty", 0, new byte[8], null, ct)) is var e ? (e.BytesRead, e.ObjectLength) : default);
        await Assert.ThrowsAsync<ObjectNotFoundException>(async () => await store.ReadAsync("none", 0, four, null, ct));
        emulator.Seed(Bucket, "data/one.vortex", new byte[10]);
        await Assert.ThrowsAsync<VortexFormatException>(async () => await store.ReadAsync("data/one.vortex", 0, four, generation, ct));
    }

    [Fact]
    public async Task ASuffixReadOnceProvenOpensInOneRequest()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using GoogleEmulator emulator = new GoogleEmulator();
        emulator.Seed(Bucket, "f", Encoding.ASCII.GetBytes("0123456789"));
        await using GoogleCloudStorageObjectStore store = Store(emulator, readsSuffix: true);
        byte[] four = new byte[4];
        Assert.Equal(4, (await store.ReadTailAsync("f", four, ct)).BytesRead);
        Assert.Equal(["GET /warehouse/f bytes=-4"], emulator.Log);
    }

    [Fact]
    public async Task TheMetadataAndTheListingSayRetentionAndHolds()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using GoogleEmulator emulator = new GoogleEmulator { PageSize = 2 };
        DateTimeOffset until = new DateTimeOffset(2027, 1, 2, 3, 4, 5, TimeSpan.Zero);
        emulator.Seed(Bucket, "a", [1], until, held: true);
        emulator.Seed(Bucket, "b c", [1, 2]);
        emulator.Seed(Bucket, "d/é", [1, 2, 3]);
        await using GoogleCloudStorageObjectStore store = Store(emulator);

        ObjectHead held = Assert.NotNull(await store.HeadAsync("a", ct));
        Assert.Equal((until, true, emulator.GenerationOf(Bucket, "a")), (held.RetainUntil, held.LegalHold, held.Token));
        Assert.Null(await store.HeadAsync("none", ct));

        List<ObjectListing> all = await store.ListAsync(string.Empty, null, ct).ToListAsync(ct);
        Assert.Equal(["a", "b c", "d/é"], all.Select(o => o.Key));
        Assert.All(all, o => Assert.True(o.LockReported));
        Assert.Equal(held, all[0].Head);
        Assert.Equal(["b c", "d/é"], await store.ListAsync(string.Empty, "a", ct).Select(o => o.Key).ToListAsync(ct));
    }

    [Fact]
    public async Task APutIsConditionedOnTheGenerationAndALargeOneWithoutAConditionGoesInParts()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using GoogleEmulator emulator = new GoogleEmulator();
        await using GoogleCloudStorageObjectStore store = Store(emulator, threshold: 256 << 10, part: 64 << 10);

        Assert.Equal(PutOutcome.Created, await PutAsync(store, "commit/1", Bytes(1_000, 1), PutCondition.IfAbsent, ct));
        Assert.Equal(PutOutcome.Exists, await PutAsync(store, "commit/1", Bytes(10, 2), PutCondition.IfAbsent, ct));
        string generation = emulator.GenerationOf(Bucket, "commit/1");
        Assert.Equal(PutOutcome.Created, await PutAsync(store, "commit/1", Bytes(20, 3), PutCondition.IfMatch(generation), ct));
        Assert.Equal(PutOutcome.Changed, await PutAsync(store, "commit/1", Bytes(30, 4), PutCondition.IfMatch(generation), ct));
        Assert.Equal(PutOutcome.Changed, await PutAsync(store, "commit/none", Bytes(30, 4), PutCondition.IfMatch(generation), ct));
        Assert.Equal(Bytes(20, 3), emulator.BytesOf(Bucket, "commit/1"));

        // Without a condition, in parts; with one, whole, since the service takes none on parts.
        byte[] large = Bytes(600_000, 5);
        int before = emulator.Requests;
        Assert.Equal(PutOutcome.Created, await PutAsync(store, "data/big", large, PutCondition.None, ct));
        Assert.Equal(1 + 10 + 1, emulator.Requests - before);
        Assert.Equal(large, emulator.BytesOf(Bucket, "data/big"));
        before = emulator.Requests;
        Assert.Equal(PutOutcome.Created, await PutAsync(store, "commit/big", large, PutCondition.IfAbsent, ct));
        Assert.Equal(1, emulator.Requests - before);
        Assert.Equal(0, emulator.OpenUploads);

        Pipe pipe = new Pipe();
        Task<PutOutcome> put = store.PutAsync("files/out", pipe.Reader, -1, PutCondition.None, ct).AsTask();
        await pipe.Writer.WriteAsync(large, ct);
        await pipe.Writer.CompleteAsync();
        Assert.Equal(PutOutcome.Created, await put);
        Assert.Equal(large, emulator.BytesOf(Bucket, "files/out"));
    }

    [Fact]
    public async Task AConditionalPutWhoseAnswerIsLostLearnsItsOutcomeFromTheMetadata()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using GoogleEmulator emulator = new GoogleEmulator();
        int lost = 0;
        emulator.LoseAnswer = request => request.Method == HttpMethod.Put && Interlocked.Increment(ref lost) == 1;
        await using GoogleCloudStorageObjectStore store = Store(emulator);
        Assert.Equal(PutOutcome.Created, await PutAsync(store, "commit/1", Bytes(100, 6), PutCondition.IfAbsent, ct));
        Assert.Equal(["PUT /warehouse/commit/1", "GET /storage/v1/b/warehouse/o/commit%2F1"], emulator.Log);
    }

    [Fact]
    public async Task ADeleteGoesInBatchesOfAHundredAndAHeldObjectIsReported()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using GoogleEmulator emulator = new GoogleEmulator();
        List<string> keys = [.. Enumerable.Range(0, 250).Select(i => $"data/{i:D3}")];
        foreach (string key in keys)
        {
            emulator.Seed(Bucket, key, [1]);
        }

        await using GoogleCloudStorageObjectStore store = Store(emulator);
        await store.DeleteAsync([.. keys, "data/absent"], ct);
        Assert.Equal(0, emulator.Count(Bucket));
        Assert.Equal(3, emulator.Log.Count(line => line == "POST /batch/storage/v1"));

        emulator.Seed(Bucket, "held", [1], held: true);
        emulator.Seed(Bucket, "free", [1]);
        ObjectStoreException refused = await Assert.ThrowsAsync<ObjectStoreException>(async () => await store.DeleteAsync(["held", "free"], ct));
        Assert.Contains("'held' (HTTP 403)", refused.Message, StringComparison.Ordinal);
        Assert.Null(emulator.BytesOf(Bucket, "free"));
    }

    [Fact]
    public async Task ATokenTheServiceRefusesIsRenewedAndEveryRequestCarriesOne()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using GoogleEmulator emulator = new GoogleEmulator { Token = "second" };
        emulator.Seed(Bucket, "a", [1]);
        int asked = 0;
        await using GoogleCloudStorageObjectStore store = Store(emulator, GoogleCredentials.Token(_ =>
            new ValueTask<AccessToken>(new AccessToken(Interlocked.Increment(ref asked) == 1 ? "first" : "second", DateTimeOffset.UtcNow.AddHours(1)))));
        Assert.NotNull(await store.HeadAsync("a", ct));
        Assert.Equal(2, asked);
        await store.DeleteAsync(["a"], ct);
        Assert.Equal(0, emulator.Count(Bucket));
    }

    [Fact]
    public async Task ADatasetAndAFileLiveInABucket()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using GoogleEmulator emulator = new GoogleEmulator();
        await using VortexSession session = VortexSession.Create(o => o.ObjectStores.Add("gs://warehouse", Store(emulator, threshold: 256 << 10, part: 64 << 10)));
        DatasetOptions options = new DatasetOptions { Session = session, ClusteringKey = ["Day"] };
        await using (VortexDataset dataset = await VortexDataset.CreateAsync("gs://warehouse/tables/readings", LocationTests.Reading.Schema, options, ct))
        {
            for (int append = 0; append < 3; append++)
            {
                await using ObjectDraft draft = dataset.StartObject();
                await draft.Writer.WriteAsync<LocationTests.Reading>([.. Enumerable.Range(append * 1_000, 1_000).Select(i => new LocationTests.Reading(i, i))], ct);
                await dataset.AppendAsync(draft, ct);
            }

            Assert.NotNull(await dataset.CompactAsync(new CompactionOptions { LevelZeroCeiling = 2, TargetBytesAtLevelOne = 1 << 20 }, ct));
            VacuumResult swept = await dataset.VacuumAsync(new VacuumOptions { TimeProvider = new Vorticity.Tests.Dataset.ManualClock(DateTimeOffset.UtcNow.AddDays(30)) }, ct);
            Assert.NotEmpty(swept.Deleted);
            Assert.Equal(3_000, await dataset.Scan<LocationTests.Reading>().CountAsync(ct));
        }

        await using (VortexFileWriter writer = session.CreateWriter<LocationTests.Reading>("gs://warehouse/files/readings.vortex"))
        {
            await writer.WriteAsync<LocationTests.Reading>([.. Enumerable.Range(0, 40_000).Select(i => new LocationTests.Reading(i, i))], ct);
            await writer.CompleteAsync(ct);
        }

        await using VortexFile file = await session.OpenAsync("gs://warehouse/files/readings.vortex", cancellationToken: ct);
        Assert.Equal(40_000, await file.Scan<LocationTests.Reading>().CountAsync(ct));
    }

    private static ValueTask<PutOutcome> PutAsync(GoogleCloudStorageObjectStore store, string key, byte[] content, PutCondition condition, CancellationToken ct) =>
        store.PutAsync(key, PipeReader.Create(new ReadOnlySequence<byte>(content)), content.Length, condition, ct);

    private static byte[] Bytes(int length, int seed)
    {
        byte[] bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }
}
