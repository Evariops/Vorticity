// Appends to a file on Cloud Storage: the new bytes put as a temporary object, composed after the old
// object under its name on the service, conditioned on the old generation, the temporary object deleted
// whatever happens; an object below the threshold read back and sent again.
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.IO;
using Vorticity.ObjectStorage;
using Xunit;
using Reading = Vorticity.Tests.ObjectStorage.LocationTests.Reading;

namespace Vorticity.Tests.ObjectStorage;

public sealed class GoogleAppendTests
{
    private const string Bucket = "warehouse";
    private const string Location = "gs://warehouse/readings.vortex";
    private const int Threshold = 32 << 10;

    [Fact]
    public async Task AnAppendComposesTheOldObjectAndTheNewBytesOnTheService()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using GoogleEmulator emulator = new GoogleEmulator();
        await using VortexSession session = Session(Store(emulator));
        Reading[] rows = S3AppendTests.Rows(130_000);
        await WriteAsync(session, rows[..100_000], ct);
        byte[] old = emulator.BytesOf(Bucket, "readings.vortex")!;
        Assert.True(old.Length >= Threshold);

        await AppendAsync(session, rows[100_000..], ct);

        Assert.Equal(1, emulator.Composed);
        Assert.Equal(old, emulator.BytesOf(Bucket, "readings.vortex")![..old.Length]);
        Assert.Equal(1, emulator.Count(Bucket));
        Assert.Equal(rows, await RowsAsync(session, ct));
    }

    [Fact]
    public async Task AnObjectBelowTheThresholdIsReadBackAndSentAgain()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using GoogleEmulator emulator = new GoogleEmulator();
        await using VortexSession session = Session(Store(emulator));
        Reading[] rows = S3AppendTests.Rows(3_000);
        await WriteAsync(session, rows[..2_000], ct);
        await AppendAsync(session, rows[2_000..], ct);

        Assert.Equal(0, emulator.Composed);
        Assert.Equal(rows, await RowsAsync(session, ct));
    }

    [Fact]
    public async Task AnObjectReplacedDuringTheAppendIsLeftAsTheOtherWriterLeftItAndNoTemporaryObjectRemains()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using GoogleEmulator emulator = new GoogleEmulator();
        await using VortexSession session = Session(Store(emulator));
        Reading[] rows = S3AppendTests.Rows(110_000);
        await WriteAsync(session, rows[..100_000], ct);

        await using VortexFileWriter writer = await session.OpenWriterAsync(Location, cancellationToken: ct);
        await writer.WriteAsync<Reading>(rows[100_000..], ct);
        emulator.Seed(Bucket, "readings.vortex", [1, 2, 3]);

        ObjectStoreException refused = await Assert.ThrowsAsync<ObjectStoreException>(async () => await writer.CompleteAsync(ct));
        Assert.Contains("was replaced while rows were appended", refused.Message, StringComparison.Ordinal);
        Assert.Equal([1, 2, 3], emulator.BytesOf(Bucket, "readings.vortex"));
        Assert.Equal(1, emulator.Count(Bucket));
    }

    [Fact]
    public async Task AnAppendWhoseCompositionLostItsAnswerLearnsItLanded()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using GoogleEmulator emulator = new GoogleEmulator();
        await using VortexSession session = Session(Store(emulator));
        Reading[] rows = S3AppendTests.Rows(110_000);
        await WriteAsync(session, rows[..100_000], ct);
        int lost = 0;
        emulator.LoseAnswer = request => request.RequestUri!.AbsolutePath.EndsWith("/compose", StringComparison.Ordinal) && Interlocked.Increment(ref lost) == 1;

        await AppendAsync(session, rows[100_000..], ct);

        Assert.Equal(1, emulator.Composed);
        Assert.Equal(rows, await RowsAsync(session, ct));
    }

    private static GoogleCloudStorageObjectStore Store(GoogleEmulator emulator) =>
        new GoogleCloudStorageObjectStore(new GoogleCloudStorageOptions
        {
            Bucket = Bucket,
            Credentials = GoogleCredentials.Anonymous(),
            Handler = emulator,
            PartSize = Threshold,
            MinimumComposedBytes = Threshold,
            Transport = new StoreTransportOptions { RetryBaseDelay = TimeSpan.Zero },
        });

    private static VortexSession Session(GoogleCloudStorageObjectStore store) => VortexSession.Create(o => o.ObjectStores.Add("gs://warehouse", store));

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
