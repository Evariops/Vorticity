// The Azure store against the emulator: Shared Key held to the reference's example, every operation and
// what it makes of an answer, the structured body, batch deletes, and the three ways to authorize.
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
using Vorticity.Dataset;
using Vorticity.IO;
using Vorticity.ObjectStorage;
using Vorticity.ObjectStorage.Azure;
using Xunit;

namespace Vorticity.Tests.ObjectStorage;

public sealed class AzureTests
{
    private const string Lake = "lake";

    internal static AzureBlobObjectStore Store(
        AzureEmulator emulator, AzureCredentials? credentials = null, bool verifyReads = false, long threshold = 32L << 20, int block = 16 << 20, int attempts = 5) =>
        new AzureBlobObjectStore(new AzureBlobStoreOptions
        {
            Account = AzureEmulator.Account,
            Container = Lake,
            Credentials = credentials ?? AzureCredentials.SharedKey(AzureEmulator.Account, AzureEmulator.Key),
            Endpoint = emulator.BasePath.Length > 0 ? new Uri("http://127.0.0.1:8080" + emulator.BasePath) : null,
            Handler = emulator,
            VerifyReads = verifyReads,
            PartThreshold = threshold,
            BlockSize = block,
            Transport = new StoreTransportOptions { RetryBaseDelay = TimeSpan.Zero, MaxAttempts = attempts },
        });

    [Fact]
    public void TheSharedKeyStringIsTheOneTheReferenceShows()
    {
        // The reference's example for the emulator's addressing, whose path names the account again.
        using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, "http://127.0.0.1:8080/myaccount/mycontainer?restype=container&comp=metadata&timeout=20");
        request.Headers.TryAddWithoutValidation("x-ms-date", "Sun, 11 Oct 2009 21:49:13 GMT");
        request.Headers.TryAddWithoutValidation("x-ms-version", "2009-09-19");
        Assert.Equal(
            "GET\n\n\n\n\n\n\n\n\n\n\n\nx-ms-date:Sun, 11 Oct 2009 21:49:13 GMT\nx-ms-version:2009-09-19\n/myaccount/myaccount/mycontainer\ncomp:metadata\nrestype:container\ntimeout:20",
            SharedKeySigner.StringToSign(request, "myaccount", 0));
    }

    [Theory]
    [InlineData("")]
    [InlineData("/vxaccount")]
    public async Task AReadIsOneRangeBoundToTheETagAndATailTakesThePropertiesFirst(string basePath)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using AzureEmulator emulator = new AzureEmulator { BasePath = basePath };
        emulator.Seed(Lake, "data/one.vortex", Encoding.ASCII.GetBytes("0123456789"));
        emulator.Seed(Lake, "empty", []);
        await using AzureBlobObjectStore store = Store(emulator);
        string etag = emulator.ETagOf(Lake, "data/one.vortex");

        byte[] four = new byte[4];
        Assert.Equal(new ObjectRead(4, 10, etag), await store.ReadAsync("data/one.vortex", 3, four, null, ct));
        Assert.Equal("3456", Encoding.ASCII.GetString(four));
        Assert.Equal($"GET {basePath}/lake/data/one.vortex bytes=3-6", emulator.Log.Single());

        Assert.Equal(new ObjectRead(4, 10, etag), await store.ReadTailAsync("data/one.vortex", four, ct));
        Assert.Equal("6789", Encoding.ASCII.GetString(four));
        Assert.Equal(3, emulator.Requests);

        Assert.Equal((0, 0L), (await store.ReadAsync("empty", 0, new byte[8], null, ct)) is var e ? (e.BytesRead, e.ObjectLength) : default);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await store.ReadAsync("data/one.vortex", 10, four, null, ct));
        await Assert.ThrowsAsync<ObjectNotFoundException>(async () => await store.ReadAsync("none", 0, four, null, ct));

        emulator.Seed(Lake, "data/one.vortex", new byte[10]);
        await Assert.ThrowsAsync<VortexFormatException>(async () => await store.ReadAsync("data/one.vortex", 0, four, etag, ct));
    }

    [Fact]
    public async Task AStructuredBodyIsCheckedAsItArrivesAndADamagedOneReadAgain()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using AzureEmulator emulator = new AzureEmulator();
        byte[] content = new byte[10_000];
        new Random(5).NextBytes(content);
        emulator.Seed(Lake, "f", content);
        await using AzureBlobObjectStore store = Store(emulator, verifyReads: true);

        byte[] destination = new byte[10_000];
        Assert.Equal(10_000, (await store.ReadAsync("f", 0, destination, null, ct)).BytesRead);
        Assert.Equal(content, destination);

        emulator.DamageNextStructuredBody = true;
        Array.Clear(destination);
        Assert.Equal(10_000, (await store.ReadAsync("f", 0, destination, null, ct)).BytesRead);
        Assert.Equal(content, destination);
        Assert.Equal(3, emulator.Requests);
    }

    [Fact]
    public async Task AHeadAndAListingSayTheLocks()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using AzureEmulator emulator = new AzureEmulator { PageSize = 2 };
        DateTimeOffset until = new DateTimeOffset(2027, 1, 2, 3, 4, 5, TimeSpan.Zero);
        emulator.Seed(Lake, "kept", [1], until, legalHold: true);
        emulator.Seed(Lake, "free", [1, 2]);
        emulator.Seed(Lake, "odd\u0001name", [1, 2, 3]);
        emulator.Seed(Lake, "z", [1]);
        await using AzureBlobObjectStore store = Store(emulator);

        ObjectHead kept = Assert.NotNull(await store.HeadAsync("kept", ct));
        Assert.Equal((until, true), (kept.RetainUntil, kept.LegalHold));
        Assert.Null(await store.HeadAsync("none", ct));

        List<ObjectListing> all = await store.ListAsync(string.Empty, null, ct).ToListAsync(ct);
        Assert.Equal(["free", "kept", "odd\u0001name", "z"], all.Select(o => o.Key));
        Assert.All(all, o => Assert.True(o.LockReported));
        Assert.Equal(kept, all[1].Head);
        Assert.Equal(await store.HeadAsync("free", ct), all[0].Head);

        // startFrom includes its key: a listing after a key leaves it out.
        Assert.Equal(["odd\u0001name", "z"], await store.ListAsync(string.Empty, "kept", ct).Select(o => o.Key).ToListAsync(ct));
    }

    [Fact]
    public async Task APutIsOneBlobOrBlocksAndAListCommittedOnItsCondition()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using AzureEmulator emulator = new AzureEmulator();
        await using AzureBlobObjectStore store = Store(emulator, threshold: 256 << 10, block: 64 << 10);

        Assert.Equal(PutOutcome.Created, await PutAsync(store, "commit/1", Bytes(1_000, 1), PutCondition.IfAbsent, ct));
        Assert.Equal(PutOutcome.Exists, await PutAsync(store, "commit/1", Bytes(10, 2), PutCondition.IfAbsent, ct));
        string token = emulator.ETagOf(Lake, "commit/1");
        Assert.Equal(PutOutcome.Created, await PutAsync(store, "commit/1", Bytes(20, 3), PutCondition.IfMatch(token), ct));
        Assert.Equal(PutOutcome.Changed, await PutAsync(store, "commit/1", Bytes(30, 4), PutCondition.IfMatch(token), ct));
        Assert.Equal(PutOutcome.Changed, await PutAsync(store, "commit/none", Bytes(30, 4), PutCondition.IfMatch(token), ct));
        Assert.Equal(Bytes(20, 3), emulator.BytesOf(Lake, "commit/1"));

        byte[] large = Bytes(700_001, 5);
        int before = emulator.Requests;
        Assert.Equal(PutOutcome.Created, await PutAsync(store, "data/big", large, PutCondition.None, ct));
        Assert.Equal(large, emulator.BytesOf(Lake, "data/big"));
        Assert.Equal(11 + 1, emulator.Requests - before);
        Assert.Equal(PutOutcome.Exists, await PutAsync(store, "data/big", large, PutCondition.IfAbsent, ct));

        // Unknown length, as a file written to a location sends it.
        Pipe pipe = new Pipe();
        Task<PutOutcome> put = store.PutAsync("files/out", pipe.Reader, -1, PutCondition.None, ct).AsTask();
        await pipe.Writer.WriteAsync(large, ct);
        await pipe.Writer.CompleteAsync();
        Assert.Equal(PutOutcome.Created, await put);
        Assert.Equal(large, emulator.BytesOf(Lake, "files/out"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AConditionalPutWhoseAnswerIsLostLearnsItsOutcomeFromTheProperties(bool blocks)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using AzureEmulator emulator = new AzureEmulator();
        int lost = 0;
        emulator.LoseAnswer = request => !request.RequestUri!.Query.Contains("comp=block&", StringComparison.Ordinal) && Interlocked.Increment(ref lost) == 1;
        await using AzureBlobObjectStore store = Store(emulator, threshold: 256 << 10, block: 64 << 10);
        byte[] content = Bytes(blocks ? 600_000 : 1_000, 6);
        Assert.Equal(PutOutcome.Created, await PutAsync(store, "commit/1", content, PutCondition.IfAbsent, ct));
        Assert.Equal(content, emulator.BytesOf(Lake, "commit/1"));
        Assert.Equal("HEAD /lake/commit/1", emulator.Log.Last());
    }

    [Fact]
    public async Task ADeleteGoesInBatchesOf256EachSubRequestAuthorized()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using AzureEmulator emulator = new AzureEmulator();
        List<string> keys = [.. Enumerable.Range(0, 600).Select(i => $"data/{i:D4} & key")];
        foreach (string key in keys)
        {
            emulator.Seed(Lake, key, [1]);
        }

        await using AzureBlobObjectStore store = Store(emulator);
        await store.DeleteAsync([.. keys, "data/absent"], ct);
        Assert.Equal(0, emulator.Count(Lake));
        Assert.Equal(3, emulator.Log.Count(line => line == "POST /lake?restype=container&comp=batch"));

        emulator.Seed(Lake, "kept", [1], DateTimeOffset.UtcNow.AddDays(1));
        emulator.Seed(Lake, "free", [1]);
        ObjectStoreException refused = await Assert.ThrowsAsync<ObjectStoreException>(async () => await store.DeleteAsync(["kept", "free"], ct));
        Assert.Contains("'kept' (HTTP 409 BlobImmutableDueToPolicy)", refused.Message, StringComparison.Ordinal);
        Assert.Null(emulator.BytesOf(Lake, "free"));
    }

    [Fact]
    public async Task ASharedAccessSignatureRidesEveryRequestABatchsSubRequestsIncluded()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using AzureEmulator emulator = new AzureEmulator { Sas = "sv=2025-01-05&sr=c&sig=emulator%2Bsig" };
        await using AzureBlobObjectStore store = Store(emulator, AzureCredentials.SharedAccessSignature("?sv=2025-01-05&sr=c&sig=emulator%2Bsig"));
        Assert.Equal(PutOutcome.Created, await PutAsync(store, "a", [1, 2], PutCondition.IfAbsent, ct));
        Assert.Equal(2, (await store.ReadAsync("a", 0, new byte[2], null, ct)).BytesRead);
        Assert.Single(await store.ListAsync(string.Empty, null, ct).ToListAsync(ct));
        await store.DeleteAsync(["a"], ct);
        Assert.Equal(0, emulator.Count(Lake));
    }

    [Fact]
    public async Task ATokenTheServiceRefusesIsRenewedOnceAndTheRequestSentAgain()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using AzureEmulator emulator = new AzureEmulator { Token = "second" };
        emulator.Seed(Lake, "a", [1]);
        int asked = 0;
        AzureCredentials tokens = AzureCredentials.Token(_ =>
            new ValueTask<AccessToken>(new AccessToken(Interlocked.Increment(ref asked) == 1 ? "first" : "second", DateTimeOffset.UtcNow.AddHours(1))));
        await using AzureBlobObjectStore store = Store(emulator, tokens);
        Assert.NotNull(await store.HeadAsync("a", ct));
        Assert.NotNull(await store.HeadAsync("a", ct));
        Assert.Equal((2, 3), (asked, emulator.Requests));
        Assert.DoesNotContain("second", tokens.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AWrongKeyIsRefusedAndTheRefusalNamesTheAccountNeverTheKey()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using AzureEmulator emulator = new AzureEmulator();
        emulator.Seed(Lake, "a", [1]);
        string wrong = Convert.ToBase64String(Encoding.ASCII.GetBytes("not the account's key at all"));
        await using AzureBlobObjectStore store = Store(emulator, AzureCredentials.SharedKey(AzureEmulator.Account, wrong));
        ObjectStoreException refused = await Assert.ThrowsAsync<ObjectStoreException>(async () => await store.ReadAsync("a", 0, new byte[1], null, ct));
        Assert.Contains("AuthenticationFailed", refused.Message, StringComparison.Ordinal);
        Assert.Contains(AzureEmulator.Account, refused.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(wrong, refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AConnectionStringNamesTheAccountItsEndpointAndItsCredentials()
    {
        AzureConnection cloud = AzureConnection.Parse($"DefaultEndpointsProtocol=https;AccountName=contoso;AccountKey={AzureEmulator.Key};EndpointSuffix=core.windows.net");
        Assert.Equal(("contoso", "https://contoso.blob.core.windows.net/"), (cloud.Account, cloud.BlobEndpoint!.ToString()));
        Assert.Equal(AzureEmulator.Key, cloud.AccountKey);

        AzureConnection signed = AzureConnection.Parse("BlobEndpoint=https://contoso.blob.core.windows.net/;SharedAccessSignature=sv=2025&sig=x");
        Assert.Equal(("contoso", "sv=2025&sig=x"), (signed.Account, signed.Sas));
        using AzureCredentials credentials = AzureCredentials.FromConnectionString("BlobEndpoint=https://contoso.blob.core.windows.net/;SharedAccessSignature=sv=2025&sig=x");
        Assert.Throws<ArgumentException>(() => AzureCredentials.FromConnectionString("AccountName=contoso"));
    }

    [Fact]
    public async Task ADatasetAndAFileLiveInAContainer()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using AzureEmulator emulator = new AzureEmulator();
        await using VortexSession session = VortexSession.Create(o => o.ObjectStores.Add("az://lake", Store(emulator, threshold: 256 << 10, block: 64 << 10)));
        DatasetOptions options = new DatasetOptions { Session = session, ClusteringKey = ["Day"] };
        await using (VortexDataset dataset = await VortexDataset.CreateAsync("az://lake/tables/readings", LocationTests.Reading.Schema, options, ct))
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

        await using (VortexFileWriter writer = session.CreateWriter<LocationTests.Reading>("az://lake/files/readings.vortex"))
        {
            await writer.WriteAsync<LocationTests.Reading>([.. Enumerable.Range(0, 40_000).Select(i => new LocationTests.Reading(i, i))], ct);
            await writer.CompleteAsync(ct);
        }

        await using VortexFile file = await session.OpenAsync("az://lake/files/readings.vortex", cancellationToken: ct);
        Assert.Equal(40_000, await file.Scan<LocationTests.Reading>().CountAsync(ct));
    }

    private static ValueTask<PutOutcome> PutAsync(AzureBlobObjectStore store, string key, byte[] content, PutCondition condition, CancellationToken ct) =>
        store.PutAsync(key, PipeReader.Create(new ReadOnlySequence<byte>(content)), content.Length, condition, ct);

    private static byte[] Bytes(int length, int seed)
    {
        byte[] bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }
}
