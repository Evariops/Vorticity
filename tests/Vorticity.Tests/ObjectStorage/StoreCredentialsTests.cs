// One set of credentials for every store: what each store takes and refuses, credentials shared by
// stores that outlive one another, keys from a source kept and renewed, a refusal that renews only the
// keys it names, and requests sent unsigned to a bucket anyone may read.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.IO;
using Vorticity.ObjectStorage;
using Vorticity.ObjectStorage.Credentials;
using Vorticity.Tests.Dataset;
using Xunit;

namespace Vorticity.Tests.ObjectStorage;

public sealed class StoreCredentialsTests
{
    private const string Bucket = "lake";

    [Fact]
    public void AStoreRefusesCredentialsItsServiceDoesNotTake()
    {
        StoreCredentials key = StoreCredentials.AccessKey("AKID", "secret");
        StoreCredentials signature = StoreCredentials.SharedAccessSignature("sv=2025-01-05&sig=x");
        StoreCredentials tokens = StoreCredentials.Token(_ => new ValueTask<AccessToken>(new AccessToken("t", DateTimeOffset.UtcNow.AddHours(1))));

        ArgumentException s3 = Assert.Throws<ArgumentException>(() => new S3ObjectStore(new S3StoreOptions { Bucket = Bucket, Region = "eu-west-3", Credentials = signature }));
        Assert.Contains("a shared access signature", s3.Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => new S3ObjectStore(new S3StoreOptions { Bucket = Bucket, Region = "eu-west-3", Credentials = tokens }));
        Assert.Throws<ArgumentException>(() => new AzureBlobObjectStore(new AzureBlobStoreOptions { Account = "a", Container = Bucket, Credentials = key }));
        Assert.Throws<ArgumentException>(() => new GoogleCloudStorageObjectStore(new GoogleCloudStorageOptions { Bucket = Bucket, Credentials = key }));
        Assert.Throws<ArgumentException>(() => new GoogleCloudStorageObjectStore(new GoogleCloudStorageOptions { Bucket = Bucket, Credentials = signature }));

        // The environment's credentials and none at all serve every store.
        foreach (StoreCredentials any in new[] { StoreCredentials.FromEnvironment(), StoreCredentials.Anonymous() })
        {
            Assert.NotNull(any.ForS3);
            Assert.NotNull(any.ForAzure);
            Assert.NotNull(any.ForGoogle);
        }
    }

    [Fact]
    public async Task CredentialsSharedByStoresOutliveTheStoreDisposedFirst()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using S3Emulator emulator = new S3Emulator();
        emulator.Seed(Bucket, "a", [1, 2, 3]);
        StoreCredentials shared = StoreCredentials.AccessKey(S3Emulator.AccessKey, S3Emulator.Secret);
        S3ObjectStore first = Store(emulator, shared);
        await using S3ObjectStore second = Store(emulator, shared);
        Assert.Equal(3, (await first.ReadAsync("a", 0, new byte[3], null, ct)).BytesRead);
        await first.DisposeAsync();
        Assert.Equal(3, (await second.ReadAsync("a", 0, new byte[3], null, ct)).BytesRead);
    }

    [Fact]
    public async Task KeysFromASourceAreKeptUntilFiveMinutesBeforeTheyExpire()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        ManualClock clock = new ManualClock(new DateTimeOffset(2026, 10, 11, 12, 0, 0, TimeSpan.Zero));
        int asked = 0;
        AwsKeySource keys = new AwsKeySource.Renewed(
            _ => new ValueTask<AwsKeys>(new AwsKeys($"ASIA{++asked}", "secret", "session", clock.Now.AddHours(1))), "keys from a source", clock);
        AwsKeys first = (await keys.GetAsync(ct))!;
        clock.Advance(TimeSpan.FromMinutes(54));
        Assert.Same(first, await keys.GetAsync(ct));
        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal("ASIA2", (await keys.GetAsync(ct))!.AccessKeyId);
        Assert.Contains("ASIA2", keys.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("secret", keys.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARefusalRenewsTheKeysItNamesAndNeverKeysRenewedSince()
    {
        // Keys without an expiry are kept until the service refuses their session token; the refusal of
        // a request signed with keys already replaced leaves the new ones alone.
        CancellationToken ct = TestContext.Current.CancellationToken;
        int asked = 0;
        AwsKeySource keys = new AwsKeySource.Renewed(
            _ => new ValueTask<AwsKeys>(new AwsKeys("ASIA", "secret", $"session-{++asked}")), "keys from a source", TimeProvider.System);
        AwsKeys first = (await keys.GetAsync(ct))!;
        Assert.Same(first, await keys.GetAsync(ct));
        keys.Expired("session-0");
        Assert.Same(first, await keys.GetAsync(ct));
        keys.Expired("session-1");
        AwsKeys second = (await keys.GetAsync(ct))!;
        Assert.Equal("session-2", second.SessionToken);
        keys.Expired("session-1");
        Assert.Same(second, await keys.GetAsync(ct));
        Assert.Equal(2, asked);
    }

    [Fact]
    public async Task KeysTheServiceFindsExpiredAreAskedForAgainAndTheRequestSent()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using S3Emulator emulator = new S3Emulator();
        emulator.Seed(Bucket, "a", [1, 2, 3]);
        emulator.Intercept = (request, _) =>
            request.Headers.TryGetValues("x-amz-security-token", out IEnumerable<string>? tokens) && tokens.Single() == "session-1"
                ? S3Emulator.Error(HttpStatusCode.Forbidden, "ExpiredToken", "The provided token has expired.")
                : null;
        int asked = 0;
        StoreCredentials source = StoreCredentials.AccessKey(_ =>
            new ValueTask<AccessKey>(new AccessKey(S3Emulator.AccessKey, S3Emulator.Secret, $"session-{Interlocked.Increment(ref asked)}")));
        await using S3ObjectStore store = Store(emulator, source);
        Assert.Equal(3, (await store.ReadAsync("a", 0, new byte[3], null, ct)).BytesRead);
        Assert.Equal((2, 2), (asked, emulator.Requests));
        Assert.Equal(3, (await store.ReadAsync("a", 0, new byte[3], null, ct)).BytesRead);
        Assert.Equal(2, asked);
    }

    [Fact]
    public async Task AnonymousCredentialsReadABucketAnyoneMayReadUnsigned()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using S3Emulator emulator = new S3Emulator { PublicRead = true };
        emulator.Seed(Bucket, "a", [1, 2, 3]);
        HttpRequestMessage? seen = null;
        emulator.Intercept = (request, _) =>
        {
            seen = request;
            return null;
        };
        await using S3ObjectStore store = Store(emulator, StoreCredentials.Anonymous());
        Assert.Equal(3, (await store.ReadAsync("a", 0, new byte[3], null, ct)).BytesRead);
        Assert.False(seen!.Headers.Contains("Authorization"));

        // A write is the bucket owner's: the refusal names no identity but the store's lack of one.
        ObjectStoreException refused = await Assert.ThrowsAsync<ObjectStoreException>(
            async () => await store.PutIfAbsentAsync("b", new byte[] { 4 }, ct));
        Assert.Contains("AccessDenied", refused.Message, StringComparison.Ordinal);
        Assert.Contains("no credentials", refused.Message, StringComparison.Ordinal);
    }

    private static S3ObjectStore Store(S3Emulator emulator, StoreCredentials credentials) =>
        new S3ObjectStore(new S3StoreOptions
        {
            Bucket = Bucket,
            Region = "eu-west-3",
            Credentials = credentials,
            Handler = emulator,
            Transport = new StoreTransportOptions { RetryBaseDelay = TimeSpan.Zero },
        });
}
