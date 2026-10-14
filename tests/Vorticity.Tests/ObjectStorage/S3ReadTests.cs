// The S3 store's read path against the emulator: what each read, head and listing asks and what it
// makes of the answer, every refusal mapped as the interface says, and the retries the transport makes
// when the service or the connection fails.
using System;
using System.Collections.Generic;
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

public sealed class S3ReadTests
{
    private const string Bucket = "lake";

    internal static S3ObjectStore Store(S3Emulator emulator, string? region = "eu-west-3", Uri? endpoint = null, StoreTransportOptions? transport = null, bool? readsSuffix = null) =>
        new S3ObjectStore(new S3StoreOptions
        {
            Bucket = Bucket,
            Region = region,
            Endpoint = endpoint,
            Credentials = StoreCredentials.AccessKey(S3Emulator.AccessKey, S3Emulator.Secret),
            Handler = emulator,
            ReadsSuffix = readsSuffix,
            Transport = transport ?? new StoreTransportOptions { RetryBaseDelay = TimeSpan.Zero },
        });

    [Fact]
    public async Task AReadAsksForOneRangeAndLandsInTheCallersMemory()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using S3Emulator emulator = new S3Emulator();
        emulator.Seed(Bucket, "data/one.vortex", Encoding.ASCII.GetBytes("0123456789"));
        await using S3ObjectStore store = Store(emulator);
        string etag = emulator.ETagOf(Bucket, "data/one.vortex");

        byte[] destination = new byte[4];
        Assert.Equal(new ObjectRead(4, 10, etag), await store.ReadAsync("data/one.vortex", 3, destination, null, ct));
        Assert.Equal("3456", Encoding.ASCII.GetString(destination));
        Assert.Equal("GET /data/one.vortex bytes=3-6", emulator.Log.Last());

        // Past the object's end the answer is shorter; at its end the read is refused.
        byte[] larger = new byte[16];
        Assert.Equal(4, (await store.ReadAsync("data/one.vortex", 6, larger, etag, ct)).BytesRead);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await store.ReadAsync("data/one.vortex", 10, larger, null, ct));
        await Assert.ThrowsAsync<ObjectNotFoundException>(async () => await store.ReadAsync("data/none", 0, larger, null, ct));
    }

    [Fact]
    public async Task AReadBoundToATokenIsRefusedByTheServiceOnceTheObjectIsReplaced()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using S3Emulator emulator = new S3Emulator();
        emulator.Seed(Bucket, "data/one.vortex", new byte[64]);
        await using S3ObjectStore store = Store(emulator);
        string first = emulator.ETagOf(Bucket, "data/one.vortex");
        emulator.Seed(Bucket, "data/one.vortex", new byte[64]);

        VortexFormatException changed = await Assert.ThrowsAsync<VortexFormatException>(
            async () => await store.ReadAsync("data/one.vortex", 0, new byte[8], first, ct));
        Assert.Contains("changed under this reader", changed.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ATailReadSaysTheLengthInOneRequest()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using S3Emulator emulator = new S3Emulator();
        emulator.Seed(Bucket, "f", Encoding.ASCII.GetBytes("0123456789"));
        emulator.Seed(Bucket, "empty", []);
        await using S3ObjectStore store = Store(emulator);
        string etag = emulator.ETagOf(Bucket, "f");

        byte[] four = new byte[4];
        Assert.Equal(new ObjectRead(4, 10, etag), await store.ReadTailAsync("f", four, ct));
        Assert.Equal("6789", Encoding.ASCII.GetString(four));
        Assert.Equal(1, emulator.Requests);
        Assert.EndsWith("bytes=-4", emulator.Log.Last(), StringComparison.Ordinal);

        // A suffix longer than the object gives the object; an empty one takes a head for its token.
        byte[] larger = new byte[64];
        Assert.Equal(new ObjectRead(10, 10, etag), await store.ReadTailAsync("f", larger, ct));
        Assert.Equal((0, 0L), Pick(await store.ReadTailAsync("empty", larger, ct)));
        Assert.Equal((0, 0L), Pick(await store.ReadAsync("empty", 0, larger, null, ct)));
        await Assert.ThrowsAsync<ObjectNotFoundException>(async () => await store.ReadTailAsync("none", larger, ct));
    }

    [Fact]
    public async Task AServiceWhoseSuffixReadsAreNotProvenAsksAHeadThenARange()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using S3Emulator emulator = new S3Emulator();
        emulator.Seed(Bucket, "f", Encoding.ASCII.GetBytes("0123456789"));
        await using S3ObjectStore store = Store(emulator, readsSuffix: false);
        Assert.Equal(0, (int)(store.Capabilities & ObjectStoreCapabilities.ReadsSuffix));

        byte[] four = new byte[4];
        Assert.Equal(4, (await store.ReadTailAsync("f", four, ct)).BytesRead);
        Assert.Equal("6789", Encoding.ASCII.GetString(four));
        Assert.Equal(["HEAD", "GET"], emulator.Log.Select(line => line.Split(' ')[0]));
        Assert.EndsWith("bytes=6-9", emulator.Log.Last(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AHeadSaysTheLengthTokenDateAndLock()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using S3Emulator emulator = new S3Emulator();
        DateTimeOffset until = new DateTimeOffset(2027, 1, 2, 3, 4, 5, TimeSpan.Zero);
        emulator.Seed(Bucket, "kept", new byte[5], retainUntil: until, legalHold: true);
        emulator.Seed(Bucket, "free", new byte[3]);
        await using S3ObjectStore store = Store(emulator);

        ObjectHead kept = Assert.NotNull(await store.HeadAsync("kept", ct));
        Assert.Equal((5L, emulator.ETagOf(Bucket, "kept"), until, true), (kept.Length, kept.Token, kept.RetainUntil, kept.LegalHold));
        ObjectHead free = Assert.NotNull(await store.HeadAsync("free", ct));
        Assert.Equal((3L, (DateTimeOffset?)null, false), (free.Length, free.RetainUntil, free.LegalHold));
        Assert.Null(await store.HeadAsync("none", ct));
    }

    [Fact]
    public async Task AListingPagesInOrderAfterAKeyAndDecodesEveryKey()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using S3Emulator emulator = new S3Emulator { PageSize = 2 };
        string[] keys = ["commit/00000000000000000097.vxc", "commit/00000000000000000098.vxc", "data/a b.vortex", "data/a+b.vortex", "data/été", "data/x%y", "imports/z"];
        foreach (string key in keys)
        {
            emulator.Seed(Bucket, key, Encoding.UTF8.GetBytes(key));
        }

        await using S3ObjectStore store = Store(emulator);
        List<ObjectListing> all = await store.ListAsync(string.Empty, null, ct).ToListAsync(ct);
        Assert.Equal(keys.Order(StringComparer.Ordinal), all.Select(o => o.Key));
        Assert.All(all, o => Assert.Equal(Encoding.UTF8.GetByteCount(o.Key), o.Head.Length));
        Assert.All(all, o => Assert.Equal(emulator.ETagOf(Bucket, o.Key), o.Head.Token));
        Assert.Equal(4, emulator.Requests);

        Assert.Equal(["data/a+b.vortex", "data/x%y", "data/été"], await store.ListAsync("data/", "data/a b.vortex", ct).Select(o => o.Key).ToListAsync(ct));
        Assert.Empty(await store.ListAsync("none/", null, ct).ToListAsync(ct));

        // A caller that stops at the first key pays for one page.
        int before = emulator.Requests;
        await foreach (ObjectListing _ in store.ListAsync(string.Empty, null, ct))
        {
            break;
        }

        Assert.Equal(before + 1, emulator.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task AServiceFailureIsRetriedAndTheReadLands(HttpStatusCode status)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using S3Emulator emulator = new S3Emulator();
        emulator.Seed(Bucket, "f", Encoding.ASCII.GetBytes("0123456789"));
        emulator.Intercept = (_, index) => index < 2 ? S3Emulator.Error(status, status == HttpStatusCode.ServiceUnavailable ? "SlowDown" : "InternalError", "try again") : null;
        await using S3ObjectStore store = Store(emulator);
        int budget = store.Transport.BudgetLeft;

        Assert.Equal(10, (await store.ReadAsync("f", 0, new byte[10], null, ct)).BytesRead);
        Assert.Equal(3, emulator.Requests);
        Assert.Equal(budget - 10 + 1, store.Transport.BudgetLeft);
    }

    [Fact]
    public async Task AConnectionLostOrABodyCutShortIsRetried()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using S3Emulator emulator = new S3Emulator();
        byte[] content = Encoding.ASCII.GetBytes("0123456789");
        emulator.Seed(Bucket, "f", content);
        emulator.Intercept = (request, index) => index switch
        {
            0 => throw new HttpRequestException("connection reset"),
            1 => Short(content, 4),
            _ => null,
        };
        await using S3ObjectStore store = Store(emulator);
        byte[] destination = new byte[10];
        Assert.Equal(10, (await store.ReadAsync("f", 0, destination, null, ct)).BytesRead);
        Assert.Equal(content, destination);
        Assert.Equal(3, emulator.Requests);
    }

    [Fact]
    public async Task AStalledBodyIsAbandonedAndRetried()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using S3Emulator emulator = new S3Emulator();
        byte[] content = Encoding.ASCII.GetBytes("0123456789");
        emulator.Seed(Bucket, "f", content);
        emulator.Intercept = (request, index) => index == 0 ? Short(content, 3, stall: true) : null;
        await using S3ObjectStore store = Store(emulator, transport: new StoreTransportOptions { RetryBaseDelay = TimeSpan.Zero, StallTimeout = TimeSpan.FromMilliseconds(100) });
        Assert.Equal(10, (await store.ReadAsync("f", 0, new byte[10], null, ct)).BytesRead);
        Assert.Equal(2, emulator.Requests);
    }

    [Fact]
    public async Task AFailureThatOutlastsTheAttemptsNamesTheServicesAnswer()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using S3Emulator emulator = new S3Emulator();
        emulator.Seed(Bucket, "f", new byte[10]);
        emulator.Intercept = (_, _) => S3Emulator.Error(HttpStatusCode.ServiceUnavailable, "SlowDown", "Please reduce your request rate.");
        await using S3ObjectStore store = Store(emulator, transport: new StoreTransportOptions { RetryBaseDelay = TimeSpan.Zero, MaxAttempts = 3 });
        ObjectStoreException failed = await Assert.ThrowsAsync<ObjectStoreException>(async () => await store.ReadAsync("f", 0, new byte[10], null, ct));
        Assert.Equal(3, emulator.Requests);
        Assert.Contains("after 3 attempts", failed.Message, StringComparison.Ordinal);
        Assert.Contains("SlowDown", failed.Message, StringComparison.Ordinal);

        // A caller tests the service's answer without reading the message.
        Assert.Equal((503, "SlowDown"), (failed.StatusCode, failed.ErrorCode));
    }

    [Fact]
    public async Task AnEmptyBudgetFailsARequestAtItsFirstFailure()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using S3Emulator emulator = new S3Emulator();
        emulator.Seed(Bucket, "f", new byte[10]);
        emulator.Intercept = (_, _) => S3Emulator.Error(HttpStatusCode.InternalServerError, "InternalError", "down");
        await using S3ObjectStore store = Store(emulator, transport: new StoreTransportOptions { RetryBaseDelay = TimeSpan.Zero, MaxAttempts = 1_000 });

        // A service that stays down drains the budget, then costs each caller one attempt.
        await Assert.ThrowsAsync<ObjectStoreException>(async () => await store.ReadAsync("f", 0, new byte[10], null, ct));
        int drained = emulator.Requests;
        Assert.InRange(drained, 2, 1 + (500 / 5));
        ObjectStoreException refused = await Assert.ThrowsAsync<ObjectStoreException>(async () => await store.ReadAsync("f", 0, new byte[10], null, ct));
        Assert.Contains("budget", refused.Message, StringComparison.Ordinal);
        Assert.Equal(drained + 1, emulator.Requests);
    }

    [Fact]
    public async Task ARefusalNamesTheIdentityAndTheRequestIdButNeverTheSecret()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using S3Emulator emulator = new S3Emulator();
        emulator.Seed(Bucket, "f", new byte[10]);
        await using S3ObjectStore store = new S3ObjectStore(new S3StoreOptions
        {
            Bucket = Bucket,
            Region = "eu-west-3",
            Credentials = StoreCredentials.AccessKey(S3Emulator.AccessKey, "a wrong secret"),
            Handler = emulator,
        });
        ObjectStoreException refused = await Assert.ThrowsAsync<ObjectStoreException>(async () => await store.ReadAsync("f", 0, new byte[10], null, ct));
        Assert.Contains("SignatureDoesNotMatch", refused.Message, StringComparison.Ordinal);
        Assert.Contains(S3Emulator.AccessKey, refused.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("wrong secret", refused.Message, StringComparison.Ordinal);
        Assert.Equal(1, emulator.Requests);
        Assert.Equal((403, "SignatureDoesNotMatch", "EMU"), (refused.StatusCode, refused.ErrorCode, refused.RequestId));
    }

    [Fact]
    public async Task AClockTheServiceFindsSkewedIsCorrectedFromItsDate()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using S3Emulator emulator = new S3Emulator { Now = () => DateTimeOffset.UtcNow.AddMinutes(42) };
        emulator.Seed(Bucket, "f", new byte[10]);
        await using S3ObjectStore store = Store(emulator);
        Assert.Equal(10, (await store.ReadAsync("f", 0, new byte[10], null, ct)).BytesRead);
        Assert.Equal(2, emulator.Requests);

        // The offset is kept: the next request is signed for the service's clock at once.
        Assert.NotNull(await store.HeadAsync("f", ct));
        Assert.Equal(3, emulator.Requests);
    }

    [Fact]
    public async Task AKeyIsBoundedByItsBytesOfUtf8AsTheServiceCountsThem()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using S3Emulator emulator = new S3Emulator();
        await using S3ObjectStore store = Store(emulator);

        // 400 characters of three bytes each are 1 200 bytes: refused before any request.
        await Assert.ThrowsAsync<ArgumentException>(async () => await store.HeadAsync(new string('€', 400), ct));
        Assert.Null(await store.HeadAsync(new string('€', 341), ct));
        Assert.Equal(1, emulator.Requests);
    }

    [Fact]
    public async Task ABucketsRegionIsLearnedFromItsOwnAnswerAtTheFirstRequest()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using S3Emulator emulator = new S3Emulator { Region = "ap-southeast-2" };
        emulator.Seed(Bucket, "f", new byte[10]);
        await using S3ObjectStore store = Store(emulator, region: null);
        Assert.NotNull(await store.HeadAsync("f", ct));
        Assert.NotNull(await store.HeadAsync("f", ct));
        Assert.Equal(["HEAD", "HEAD", "HEAD"], emulator.Log.Select(line => line.Split(' ')[0]));
    }

    [Fact]
    public async Task ABucketIsAddressedByHostOnAmazonAndByPathOnAnEmulatorsAddress()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using S3Emulator emulator = new S3Emulator();
        emulator.Seed(Bucket, "a/b c", new byte[1]);
        await using (S3ObjectStore byHost = Store(emulator))
        {
            Assert.NotNull(await byHost.HeadAsync("a/b c", ct));
        }

        await using (S3ObjectStore byPath = Store(emulator, endpoint: new Uri("http://127.0.0.1:8080")))
        {
            Assert.NotNull(await byPath.HeadAsync("a/b c", ct));
        }

        Assert.Equal(["HEAD /a/b%20c", "HEAD /lake/a/b%20c"], emulator.Log);
        Assert.Throws<ArgumentException>(() => Store(emulator, endpoint: new Uri("http://storage.example.com")));
        Assert.Throws<ArgumentException>(() => Store(emulator, endpoint: new Uri("https://storage.example.com/path")));
    }

    [Fact]
    public async Task AFileOfUnknownLengthOpensAtALocationInOneRequest()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using S3Emulator emulator = new S3Emulator();
        emulator.Seed(Bucket, "readings/one.vortex", await System.IO.File.ReadAllBytesAsync(await LocationTests.ReadingsFileAsync(), ct));
        await using S3ObjectStore lake = Store(emulator);
        await using VortexSession session = VortexSession.Create(o => o.ObjectStores.Add("s3://lake", lake));
        await using (VortexFile file = await session.OpenAsync("s3://lake/readings/one.vortex", cancellationToken: ct))
        {
            Assert.Equal(1, emulator.Requests);
            Assert.Equal(LocationTests.ReadingRows, await file.Scan<LocationTests.Reading>().CountAsync(ct));
        }

        Assert.All(emulator.Log.Skip(1), line => Assert.StartsWith("GET /readings/one.vortex bytes=", line, StringComparison.Ordinal));
    }

    [Fact]
    public async Task AReadAllocatesAConstantPerRequestAndNothingPerByte()
    {
        // The ratchet of the bytes a request allocates: the signature, the request and its headers,
        // the client's own, and nothing that grows with the body, which lands in the caller's memory.
        // A lean handler answers, so that what is measured is the store's and the client's.
        CancellationToken ct = TestContext.Current.CancellationToken;
        byte[] body = new byte[1 << 20];
        using LeanHandler handler = new LeanHandler(body);
        await using S3ObjectStore store = new S3ObjectStore(new S3StoreOptions
        {
            Bucket = Bucket,
            Region = "eu-west-3",
            Credentials = StoreCredentials.AccessKey(S3Emulator.AccessKey, S3Emulator.Secret),
            Handler = handler,
        });
        string token = LeanHandler.ETag;
        byte[] destination = new byte[1 << 20];
        long small = 0;
        long large = 0;
        for (int round = 0; round < 3; round++)
        {
            small = await PerReadAsync(store, destination.AsMemory(0, 4_096), token, ct);
            large = await PerReadAsync(store, destination, token, ct);
        }

        Console.WriteLine($"S3 READ: {small} B allocated per 4 KiB read, {large} B per 1 MiB read.");
        // 3 224 B when the ceiling was set, the lean handler's answer included.
        Assert.InRange(small, 1, 4_096);
        Assert.InRange(large - small, -512, 512);
    }

    private static async Task<long> PerReadAsync(S3ObjectStore store, Memory<byte> destination, string token, CancellationToken ct)
    {
        const int Reads = 50;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < Reads; i++)
        {
            await store.ReadAsync("data/one.vortex", 0, destination, token, ct);
        }

        return (GC.GetAllocatedBytesForCurrentThread() - before) / Reads;
    }

    /// <summary>A handler that answers every read with the range asked of one object, at the least cost it can.</summary>
    private sealed class LeanHandler(byte[] body) : HttpMessageHandler
    {
        internal const string ETag = "\"lean\"";

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string range = request.Headers.GetValues("Range").First();
            int dash = range.IndexOf('-', StringComparison.Ordinal);
            int last = int.Parse(range.AsSpan(dash + 1), System.Globalization.CultureInfo.InvariantCulture);
            HttpResponseMessage response = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent(body, 0, last + 1),
                RequestMessage = request,
            };
            response.Content.Headers.TryAddWithoutValidation("Content-Range", $"bytes 0-{last}/{body.Length}");
            response.Headers.TryAddWithoutValidation("ETag", ETag);
            return Task.FromResult(response);
        }
    }

    private static (int, long) Pick(ObjectRead read) => (read.BytesRead, read.ObjectLength);

    /// <summary>A partial answer that announces the whole content and sends <paramref name="sent"/> bytes of it.</summary>
    private static HttpResponseMessage Short(byte[] content, int sent, bool stall = false)
    {
        HttpResponseMessage response = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new FaultyContent(content, sent, stall) };
        response.Content.Headers.TryAddWithoutValidation("Content-Range", $"bytes 0-{content.Length - 1}/{content.Length}");
        response.Headers.TryAddWithoutValidation("ETag", "\"short\"");
        return response;
    }
}
