// S3 Express One Zone's directory buckets: requests at the zone's endpoint signed for s3express with a
// session that CreateSession issues for five minutes and the store renews a minute before; listings in
// no order and without start-after, which a dataset refuses; native appends at the object's end.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.IO;
using Vorticity.ObjectStorage;
using Vorticity.Tests.Dataset;
using Xunit;
using Reading = Vorticity.Tests.ObjectStorage.LocationTests.Reading;

namespace Vorticity.Tests.ObjectStorage;

public sealed class S3DirectoryBucketTests
{
    private const string Bucket = "lake--euw3-az1--x-s3";
    private const string Location = "s3://" + Bucket + "/readings.vortex";

    [Fact]
    public async Task ADirectoryBucketIsReachedAtItsZoneWithASessionCreatedOnce()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using S3Emulator emulator = new S3Emulator();
        await using S3ObjectStore store = Store(emulator);
        Assert.True(store.IsDirectoryBucket);

        Assert.Equal(PutOutcome.Created, await store.PutIfAbsentAsync("a/one", Encoding.ASCII.GetBytes("0123456789"), ct));
        byte[] destination = new byte[4];
        ObjectRead read = await store.ReadAsync("a/one", 3, destination, null, ct);
        Assert.Equal((4, 10L), (read.BytesRead, read.ObjectLength));
        Assert.NotNull(await store.HeadAsync("a/one", ct));

        // The session's request first, then every request signed with it.
        Assert.Equal(1, emulator.SessionsCreated);
        Assert.Equal("GET /?session", emulator.Log.First());
        Assert.All(emulator.Hosts, host => Assert.Equal($"{Bucket}.s3express-euw3-az1.eu-west-3.amazonaws.com", host));
    }

    [Fact]
    public async Task ASessionIsRenewedAMinuteBeforeItExpires()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        ManualClock clock = new ManualClock(DateTimeOffset.UtcNow);
        using S3Emulator emulator = new S3Emulator { Now = () => clock.GetUtcNow() };
        await using S3ObjectStore store = Store(emulator, clock);
        emulator.Seed(Bucket, "one", [1, 2, 3]);

        await store.HeadAsync("one", ct);
        clock.Advance(TimeSpan.FromMinutes(3));
        await store.HeadAsync("one", ct);
        Assert.Equal(1, emulator.SessionsCreated);

        clock.Advance(TimeSpan.FromSeconds(90));
        await store.HeadAsync("one", ct);
        Assert.Equal(2, emulator.SessionsCreated);
    }

    [Fact]
    public async Task AnExpiredSessionRefusedByTheServiceIsCreatedAgain()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        ManualClock clock = new ManualClock(DateTimeOffset.UtcNow);
        using S3Emulator emulator = new S3Emulator { Now = () => clock.GetUtcNow(), SessionLifetime = TimeSpan.FromMinutes(30) };
        await using S3ObjectStore store = Store(emulator, clock);
        emulator.Seed(Bucket, "one", [1, 2, 3]);
        await store.HeadAsync("one", ct);

        // The service ends the session sooner than it said, in a read's answer, whose body names the code.
        emulator.Intercept = (request, index) => index == 2 ? S3Emulator.Error(System.Net.HttpStatusCode.BadRequest, "ExpiredToken", "The provided token has expired.") : null;
        byte[] destination = new byte[3];
        Assert.Equal(3, (await store.ReadAsync("one", 0, destination, null, ct)).BytesRead);
        Assert.Equal(2, emulator.SessionsCreated);
    }

    [Fact]
    public async Task AListingAsksForTheDirectoryAndTakesNoStartAfter()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using S3Emulator emulator = new S3Emulator { PageSize = 2 };
        await using S3ObjectStore store = Store(emulator);
        foreach (string key in new[] { "logs/a", "logs/ab", "logs/b", "logs/c", "other/a", "logsz" })
        {
            emulator.Seed(Bucket, key, [1]);
        }

        List<string> listed = await store.ListAsync("logs/a", null, ct).Select(o => o.Key).ToListAsync(ct);
        Assert.Equal(["logs/a", "logs/ab"], listed.Order(StringComparer.Ordinal));
        List<string> after = await store.ListAsync("logs/", "logs/ab", ct).Select(o => o.Key).ToListAsync(ct);
        Assert.Equal(["logs/b", "logs/c"], after.Order(StringComparer.Ordinal));
        Assert.DoesNotContain(emulator.Log, line => line.Contains("start-after", StringComparison.Ordinal));
        Assert.Equal(ObjectStoreCapabilities.ReadsSuffix, store.Capabilities);
    }

    [Fact]
    public async Task ADatasetRefusesADirectoryBucketAndDeletesCarryACrc32()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using S3Emulator emulator = new S3Emulator();
        await using S3ObjectStore store = Store(emulator);
        NotSupportedException refused = await Assert.ThrowsAsync<NotSupportedException>(
            async () => await VortexDataset.CreateAsync(store, Reading.Schema, cancellationToken: ct));
        Assert.Contains("ordinal order", refused.Message, StringComparison.Ordinal);
        Assert.Equal(0, emulator.Requests);

        emulator.Seed(Bucket, "a", [1]);
        emulator.Seed(Bucket, "b", [2]);
        await store.DeleteAsync(["a", "b"], ct);
        Assert.Equal(0, emulator.Count(Bucket));
    }

    [Fact]
    public async Task AFileOnADirectoryBucketTakesANativeAppendAtItsEnd()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using S3Emulator emulator = new S3Emulator();
        await using VortexSession session = VortexSession.Create(o => o.ObjectStores.Add("s3://" + Bucket, Store(emulator)));
        Reading[] rows = S3AppendTests.Rows(30_000);
        await WriteAsync(session, rows[..20_000], ct);
        byte[] old = emulator.BytesOf(Bucket, "readings.vortex")!;

        await using (VortexFileWriter writer = await session.OpenWriterAsync(Location, cancellationToken: ct))
        {
            await writer.WriteAsync<Reading>(rows[20_000..], ct);
            await writer.CompleteAsync(ct);
        }

        Assert.Equal(1, emulator.Appends);
        Assert.Equal(0, emulator.CopiedParts);
        Assert.Equal(old, emulator.BytesOf(Bucket, "readings.vortex")![..old.Length]);
        await using VortexFile file = await session.OpenAsync(Location, cancellationToken: ct);
        Assert.Equal(rows, await LocationAppendTests.ReadAllAsync(file, ct));
    }

    [Fact]
    public async Task ANativeAppendToAnObjectGrownMeanwhileIsRefused()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using S3Emulator emulator = new S3Emulator();
        await using VortexSession session = VortexSession.Create(o => o.ObjectStores.Add("s3://" + Bucket, Store(emulator)));
        Reading[] rows = S3AppendTests.Rows(30_000);
        await WriteAsync(session, rows[..20_000], ct);

        await using VortexFileWriter writer = await session.OpenWriterAsync(Location, cancellationToken: ct);
        await writer.WriteAsync<Reading>(rows[20_000..], ct);
        emulator.Seed(Bucket, "readings.vortex", [1, 2, 3]);

        ObjectStoreException refused = await Assert.ThrowsAsync<ObjectStoreException>(async () => await writer.CompleteAsync(ct));
        Assert.Contains("was replaced while rows were appended", refused.Message, StringComparison.Ordinal);
        Assert.Equal([1, 2, 3], emulator.BytesOf(Bucket, "readings.vortex"));
    }

    [Fact]
    public void ADirectoryBucketNeedsItsRegionAndANameThatNamesAZone()
    {
        Assert.Equal("euw3-az1", S3ObjectStore.ZoneOf(Bucket));
        Assert.Null(S3ObjectStore.ZoneOf("lake"));
        Assert.Throws<ArgumentException>(() => S3ObjectStore.ZoneOf("--x-s3"));
        ArgumentException refused = Assert.Throws<ArgumentException>(() => new S3ObjectStore(new S3StoreOptions
        {
            Bucket = Bucket,
            Credentials = S3Credentials.Static(S3Emulator.AccessKey, S3Emulator.Secret),
        }));
        Assert.Contains("needs its region", refused.Message, StringComparison.Ordinal);
    }

    private static S3ObjectStore Store(S3Emulator emulator, TimeProvider? clock = null) =>
        new S3ObjectStore(new S3StoreOptions
        {
            Bucket = Bucket,
            Region = "eu-west-3",
            Credentials = S3Credentials.Static(S3Emulator.AccessKey, S3Emulator.Secret),
            Handler = emulator,
            Transport = new StoreTransportOptions { RetryBaseDelay = TimeSpan.Zero, TimeProvider = clock ?? TimeProvider.System },
        });

    private static async Task WriteAsync(VortexSession session, Reading[] rows, CancellationToken ct)
    {
        await using VortexFileWriter writer = session.CreateWriter<Reading>(Location);
        await writer.WriteAsync<Reading>(rows, ct);
        await writer.CompleteAsync(ct);
    }
}
