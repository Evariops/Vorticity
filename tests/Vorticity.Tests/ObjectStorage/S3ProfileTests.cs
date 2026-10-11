// The clouds that speak S3's API, each through a profile that says where it differs: its endpoint and
// region, how it addresses a bucket, what it is taken to do until the contract tests against it prove
// more, and what a dataset may ask of it.
using System;
using System.Buffers;
using System.IO.Pipelines;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.IO;
using Vorticity.ObjectStorage;
using Xunit;

namespace Vorticity.Tests.ObjectStorage;

public sealed class S3ProfileTests
{
    private const ObjectStoreCapabilities Proven = ObjectStoreCapabilities.ListsInOrder | ObjectStoreCapabilities.ReplacesOnTokenMatch;

    [Fact]
    public async Task EachFactoryTakesItsServicesEndpointRegionAndPrudentCapabilities()
    {
        StoreCredentials Keys() => StoreCredentials.AccessKey("id", "secret");
        await using S3ObjectStore r2 = S3ObjectStore.ForR2("account", "media", Keys());
        await using S3ObjectStore ovh = S3ObjectStore.ForOvhcloud("gra", "archive", Keys());
        await using S3ObjectStore scaleway = S3ObjectStore.ForScaleway("fr-par", "exports", Keys());
        await using S3ObjectStore amazon = new S3ObjectStore(new S3StoreOptions { Bucket = "lake", Region = "eu-west-3", Credentials = Keys() });

        Assert.Equal((S3Profile.R2, Proven), (r2.Profile, r2.Capabilities));
        Assert.Equal((S3Profile.Ovhcloud, ObjectStoreCapabilities.ReplacesOnTokenMatch), (ovh.Profile, ovh.Capabilities));
        Assert.Equal((S3Profile.Scaleway, Proven), (scaleway.Profile, scaleway.Capabilities));
        Assert.Equal((S3Profile.Amazon, Proven | ObjectStoreCapabilities.KeepsLocks | ObjectStoreCapabilities.ReadsSuffix), (amazon.Profile, amazon.Capabilities));
    }

    [Theory]
    [InlineData("https://account.r2.cloudflarestorage.com", "auto", "account.r2.cloudflarestorage.com", "/lake/k")]
    [InlineData("https://account.eu.r2.cloudflarestorage.com", "auto", "account.eu.r2.cloudflarestorage.com", "/lake/k")]
    [InlineData("https://s3.gra.io.cloud.ovh.net", "gra", "lake.s3.gra.io.cloud.ovh.net", "/k")]
    [InlineData("https://s3.fr-par.scw.cloud", "fr-par", "lake.s3.fr-par.scw.cloud", "/k")]
    [InlineData("https://s3.eu-west-3.amazonaws.com", "eu-west-3", "lake.s3.eu-west-3.amazonaws.com", "/k")]
    [InlineData("http://localhost:8080", "us-east-1", "localhost:8080", "/lake/k")]
    public async Task AnEndpointNamesItsProfileItsRegionAndHowItAddressesABucket(string endpoint, string region, string host, string path)
    {
        // What an endpoint from AWS_ENDPOINT_URL_S3 is enough for: the store signs for the region the
        // service expects and sends to the host it routes by.
        CancellationToken ct = TestContext.Current.CancellationToken;
        using S3Emulator emulator = new S3Emulator { Region = region };
        emulator.Seed("lake", "k", [1, 2, 3]);
        await using S3ObjectStore store = new S3ObjectStore(new S3StoreOptions
        {
            Bucket = "lake",
            Endpoint = new Uri(endpoint),
            Region = endpoint.Contains("amazonaws", StringComparison.Ordinal) ? region : null,
            Credentials = StoreCredentials.AccessKey(S3Emulator.AccessKey, S3Emulator.Secret),
            Handler = emulator,
        });
        Assert.NotNull(await store.HeadAsync("k", ct));
        Assert.Equal(host, emulator.Hosts.Single());
        Assert.Equal($"HEAD {path}", emulator.Log.Single());
    }

    [Fact]
    public async Task AServiceWhoseListingOrderIsUnprovenHostsNoDatasetUntilItsOptionsSaySo()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using S3Emulator emulator = new S3Emulator { Region = "gra" };
        await using (S3ObjectStore unproven = Ovh(emulator, listsInOrder: null))
        {
            await Assert.ThrowsAsync<NotSupportedException>(async () => await VortexDataset.CreateAsync(unproven, LocationTests.Reading.Schema, cancellationToken: ct));
        }

        await using S3ObjectStore checkedOrder = Ovh(emulator, listsInOrder: true);
        await using VortexDataset dataset = await VortexDataset.CreateAsync(checkedOrder, LocationTests.Reading.Schema, cancellationToken: ct);
        Assert.Equal(1UL, dataset.Version);
    }

    [Fact]
    public async Task ALockedDatasetIsRefusedOnAServiceThatKeepsNoLocks()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using S3Emulator emulator = new S3Emulator { Region = "auto" };
        await using S3ObjectStore r2 = new S3ObjectStore(new S3StoreOptions
        {
            Bucket = "lake",
            Endpoint = new Uri("https://account.r2.cloudflarestorage.com"),
            Credentials = StoreCredentials.AccessKey(S3Emulator.AccessKey, S3Emulator.Secret),
            Handler = emulator,
        });
        NotSupportedException refused = await Assert.ThrowsAsync<NotSupportedException>(
            async () => await VortexDataset.CreateAsync(r2, LocationTests.Reading.Schema, new DatasetOptions { LockedStore = true }, ct));
        Assert.Contains("KeepsLocks", refused.Message, StringComparison.Ordinal);
        Assert.Equal(0, emulator.Requests);

        // The same dataset unlocked lives there.
        await using VortexDataset plain = await VortexDataset.CreateAsync(r2, LocationTests.Reading.Schema, cancellationToken: ct);
        Assert.False(plain.LockedStore);
    }

    [Fact]
    public async Task ALockedDatasetWaitsForTheOptionsToSayTheServiceKeepsLocksWhereNoTestHasProvenIt()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using S3Emulator emulator = new S3Emulator { Region = "fr-par" };
        DatasetOptions locked = new DatasetOptions { LockedStore = true };
        await using (S3ObjectStore unproven = Scaleway(emulator))
        {
            await Assert.ThrowsAsync<NotSupportedException>(async () => await VortexDataset.CreateAsync(unproven, LocationTests.Reading.Schema, locked, ct));
        }

        await using S3ObjectStore checkedLocks = Scaleway(emulator, keepsLocks: true);
        await using VortexDataset dataset = await VortexDataset.CreateAsync(checkedLocks, LocationTests.Reading.Schema, locked, ct);
        Assert.True(dataset.LockedStore);
    }

    [Fact]
    public async Task ABucketUnderObjectLockThatRefusesAPutWithoutAChecksumNamesTheOptionThatSendsOne()
    {
        // The refusal's text words the message and decides nothing: a store sends what its option says.
        CancellationToken ct = TestContext.Current.CancellationToken;
        using S3Emulator emulator = new S3Emulator { Region = "fr-par", RequireChecksum = true };
        await using (S3ObjectStore plain = Scaleway(emulator))
        {
            ObjectStoreException refused = await Assert.ThrowsAsync<ObjectStoreException>(async () => await PutAsync(plain, "first", 100, ct));
            Assert.Contains("PutChecksum.ContentMd5", refused.Message, StringComparison.Ordinal);
            Assert.Equal((400, "InvalidRequest"), (refused.StatusCode, refused.ErrorCode));
            Assert.Equal(["PUT /first"], emulator.Log);
        }

        await using S3ObjectStore md5 = Scaleway(emulator, checksum: PutChecksum.ContentMd5);
        Assert.Equal(PutOutcome.Created, await PutAsync(md5, "second", 100, ct));
        Assert.Equal(2, emulator.Requests);
    }

    [Fact]
    public async Task APartIsSizedToFitTheCountOfPartsTheServiceTakes()
    {
        await using S3ObjectStore scaleway = S3ObjectStore.ForScaleway("fr-par", "b", StoreCredentials.AccessKey("id", "secret"));
        await using S3ObjectStore amazon = new S3ObjectStore(new S3StoreOptions { Bucket = "b", Region = "eu-west-3", Credentials = StoreCredentials.AccessKey("id", "secret") });

        // 20 GiB: 16 MiB parts are 1 280, past Scaleway's 1 000 and under S3's 10 000.
        Assert.Equal(21 << 20, scaleway.PartSizeFor(20L << 30));
        Assert.Equal(16 << 20, amazon.PartSizeFor(20L << 30));
        Assert.Equal(16 << 20, scaleway.PartSizeFor(-1));

        // 200 GiB on S3: 10 000 parts of 21 MiB.
        Assert.Equal(21 << 20, amazon.PartSizeFor(200L << 30));
    }

    private static S3ObjectStore Scaleway(S3Emulator emulator, bool? keepsLocks = null, PutChecksum checksum = PutChecksum.Default) => new S3ObjectStore(new S3StoreOptions
    {
        Bucket = "lake",
        Endpoint = new Uri("https://s3.fr-par.scw.cloud"),
        Credentials = StoreCredentials.AccessKey(S3Emulator.AccessKey, S3Emulator.Secret),
        Handler = emulator,
        KeepsLocks = keepsLocks,
        Checksum = checksum,
    });

    private static S3ObjectStore Ovh(S3Emulator emulator, bool? listsInOrder) => new S3ObjectStore(new S3StoreOptions
    {
        Bucket = "lake",
        Endpoint = new Uri("https://s3.gra.io.cloud.ovh.net"),
        Credentials = StoreCredentials.AccessKey(S3Emulator.AccessKey, S3Emulator.Secret),
        Handler = emulator,
        ListsInOrder = listsInOrder,
    });

    private static ValueTask<PutOutcome> PutAsync(S3ObjectStore store, string key, int length, CancellationToken ct)
    {
        byte[] content = new byte[length];
        return store.PutAsync(key, PipeReader.Create(new ReadOnlySequence<byte>(content)), length, PutCondition.None, ct);
    }
}
