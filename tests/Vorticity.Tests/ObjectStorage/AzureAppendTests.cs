// Appends to a file on Azure: the old blob copied into the first blocks by Put Block From URL, its
// source authorized as the store is, the new bytes put as the blocks after them, and the list committed
// with If-Match; a blob smaller than the copy's threshold read back and sent again.
using System;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.IO;
using Vorticity.ObjectStorage;
using Xunit;
using Reading = Vorticity.Tests.ObjectStorage.LocationTests.Reading;

namespace Vorticity.Tests.ObjectStorage;

public sealed class AzureAppendTests
{
    private const string Lake = "lake";
    private const string Location = "az://lake/readings.vortex";
    private const int Threshold = 32 << 10;
    private const string Sas = "sv=2025-01-05&sr=c&sp=racwd&sig=emulator%2Bsig";

    [Theory]
    [InlineData("key")]
    [InlineData("sas")]
    [InlineData("token")]
    public async Task AnAppendCopiesTheOldBlobOnTheServiceAuthorizedAsTheStoreIs(string authorization)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using AzureEmulator emulator = authorization switch
        {
            "sas" => new AzureEmulator { Sas = Sas },
            "token" => new AzureEmulator { Token = "bearer-token" },
            _ => new AzureEmulator(),
        };
        AzureCredentials credentials = authorization switch
        {
            "sas" => AzureCredentials.SharedAccessSignature("?" + Sas),
            "token" => AzureCredentials.Token(_ => new ValueTask<AccessToken>(new AccessToken("bearer-token", DateTimeOffset.UtcNow.AddHours(1)))),
            _ => AzureCredentials.SharedKey(AzureEmulator.Account, AzureEmulator.Key),
        };
        await using VortexSession session = Session(Store(emulator, credentials));
        Reading[] rows = S3AppendTests.Rows(130_000);
        await WriteAsync(session, rows[..100_000], ct);
        byte[] old = emulator.BytesOf(Lake, "readings.vortex")!;
        Assert.True(old.Length >= Threshold);

        await AppendAsync(session, rows[100_000..], ct);

        Assert.Equal(1, emulator.CopiedBlocks);
        Assert.Equal(old, emulator.BytesOf(Lake, "readings.vortex")![..old.Length]);
        Assert.Equal(rows, await RowsAsync(session, ct));
        if (authorization == "key")
        {
            // A read-only SAS on the blob alone, signed with the key, which never travels.
            Assert.Contains("sr=b&sp=r&se=", emulator.LastSourceQuery, StringComparison.Ordinal);
            Assert.DoesNotContain(Uri.EscapeDataString(AzureEmulator.Key), emulator.LastSourceQuery, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ABlobBelowTheThresholdIsReadBackAndSentAgain()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using AzureEmulator emulator = new AzureEmulator();
        await using VortexSession session = Session(Store(emulator));
        Reading[] rows = S3AppendTests.Rows(3_000);
        await WriteAsync(session, rows[..2_000], ct);
        await AppendAsync(session, rows[2_000..], ct);

        Assert.Equal(0, emulator.CopiedBlocks);
        Assert.Equal(rows, await RowsAsync(session, ct));
    }

    [Fact]
    public async Task ABlobReplacedDuringTheAppendIsLeftAsTheOtherWriterLeftIt()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using AzureEmulator emulator = new AzureEmulator();
        await using VortexSession session = Session(Store(emulator));
        Reading[] rows = S3AppendTests.Rows(110_000);
        await WriteAsync(session, rows[..100_000], ct);

        await using VortexFileWriter writer = await session.OpenWriterAsync(Location, cancellationToken: ct);
        await writer.WriteAsync<Reading>(rows[100_000..], ct);
        emulator.Seed(Lake, "readings.vortex", [1, 2, 3]);

        ObjectStoreException refused = await Assert.ThrowsAsync<ObjectStoreException>(async () => await writer.CompleteAsync(ct));
        Assert.Contains("was replaced while rows were appended", refused.Message, StringComparison.Ordinal);
        Assert.Equal([1, 2, 3], emulator.BytesOf(Lake, "readings.vortex"));
    }

    private static AzureBlobObjectStore Store(AzureEmulator emulator, AzureCredentials? credentials = null) =>
        new AzureBlobObjectStore(new AzureBlobStoreOptions
        {
            Account = AzureEmulator.Account,
            Container = Lake,
            Credentials = credentials ?? AzureCredentials.SharedKey(AzureEmulator.Account, AzureEmulator.Key),
            Handler = emulator,
            BlockSize = Threshold,
            MinimumCopiedBytes = Threshold,
            Transport = new StoreTransportOptions { RetryBaseDelay = TimeSpan.Zero },
        });

    private static VortexSession Session(AzureBlobObjectStore store) => VortexSession.Create(o => o.ObjectStores.Add("az://lake", store));

    private static async Task WriteAsync(VortexSession session, Reading[] rows, CancellationToken ct)
    {
        await using VortexFileWriter writer = session.CreateWriter<Reading>(Location);
        await writer.WriteAsync<Reading>(rows, ct);
        await writer.CompleteAsync(ct);
    }

    private static async Task AppendAsync(VortexSession session, Reading[] rows, CancellationToken ct)
    {
        await using VortexFileWriter writer = await session.OpenWriterAsync(Location, cancellationToken: ct);
        await writer.WriteAsync<Reading>(rows, ct);
        await writer.CompleteAsync(ct);
    }

    private static async Task<Reading[]> RowsAsync(VortexSession session, CancellationToken ct)
    {
        await using VortexFile file = await session.OpenAsync(Location, cancellationToken: ct);
        return await LocationAppendTests.ReadAllAsync(file, ct);
    }
}
