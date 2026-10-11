// Appends to a file on S3: the old object copied into the first parts of an upload on the service,
// the new bytes sent as the parts after them, the completion bound to the old ETag; an object smaller
// than a part, or on a service that copies no part, read back and sent again in one conditional put.
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.IO;
using Vorticity.ObjectStorage;
using Xunit;
using Reading = Vorticity.Tests.ObjectStorage.LocationTests.Reading;

namespace Vorticity.Tests.ObjectStorage;

public sealed class S3AppendTests
{
    private const int MinimumPart = 32 << 10;
    private const string Location = "s3://lake/readings.vortex";

    [Fact]
    public async Task AnAppendCopiesTheOldObjectOnTheServiceAndSendsOnlyTheNewBytes()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using S3Emulator emulator = new S3Emulator { MinimumPartSize = MinimumPart };
        await using VortexSession session = Session(Store(emulator));
        Reading[] rows = Rows(130_000);
        await WriteAsync(session, rows[..100_000], ct);
        byte[] old = emulator.BytesOf("lake", "readings.vortex")!;
        Assert.True(old.Length >= MinimumPart, $"the old object, {old.Length} bytes, must take a part to be copied");

        int before = emulator.Requests;
        await AppendAsync(session, rows[100_000..], ct);

        Assert.Equal(1, emulator.CopiedParts);
        Assert.DoesNotContain("PUT /readings.vortex", emulator.Log.Skip(before));
        byte[] grown = emulator.BytesOf("lake", "readings.vortex")!;
        Assert.Equal(old, grown[..old.Length]);
        Assert.Equal(0, emulator.OpenUploads);
        Assert.Equal(rows, await RowsAsync(session, ct));
    }

    [Theory]
    [InlineData(null, "eu-west-3", 2_000)]
    [InlineData("https://s3.gra.io.cloud.ovh.net", "gra", 100_000)]
    [InlineData("https://account.r2.cloudflarestorage.com", "auto", 100_000)]
    public async Task AnObjectSmallerThanAPartOrOnAServiceThatCopiesNoneIsSentAgainBoundToItsETag(string? endpoint, string region, int first)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using S3Emulator emulator = new S3Emulator { MinimumPartSize = MinimumPart, Region = region };
        await using VortexSession session = Session(Store(emulator, endpoint));
        Reading[] rows = Rows(first + 5_000);
        await WriteAsync(session, rows[..first], ct);
        await AppendAsync(session, rows[first..], ct);

        Assert.Equal(0, emulator.CopiedParts);
        Assert.Equal(rows, await RowsAsync(session, ct));
    }

    [Fact]
    public async Task AnObjectReplacedBeforeItsCopyStopsTheAppendAndAbortsItsUpload()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using S3Emulator emulator = new S3Emulator { MinimumPartSize = MinimumPart };
        await using VortexSession session = Session(Store(emulator));
        Reading[] rows = Rows(110_000);
        await WriteAsync(session, rows[..100_000], ct);
        TaskCompletionSource replaced = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        emulator.Hold = async (request, _, token) =>
        {
            if (request.Headers.Contains("x-amz-copy-source"))
            {
                await replaced.Task.WaitAsync(token);
            }
        };

        await using VortexFileWriter writer = await session.OpenWriterAsync(Location, cancellationToken: ct);
        await writer.WriteAsync<Reading>(rows[100_000..], ct);
        emulator.Seed("lake", "readings.vortex", [1, 2, 3]);
        replaced.SetResult();

        ObjectStoreException refused = await Assert.ThrowsAsync<ObjectStoreException>(async () => await writer.CompleteAsync(ct));
        Assert.Contains("was replaced while rows were appended", refused.Message, StringComparison.Ordinal);
        Assert.Equal([1, 2, 3], emulator.BytesOf("lake", "readings.vortex"));
        Assert.Equal(0, emulator.CopiedParts);
        Assert.Equal(0, emulator.OpenUploads);
    }

    [Fact]
    public async Task AnObjectReplacedAfterItsCopyFailsTheCompletionAndAbortsItsUpload()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using S3Emulator emulator = new S3Emulator { MinimumPartSize = MinimumPart };
        await using VortexSession session = Session(Store(emulator));
        Reading[] rows = Rows(110_000);
        await WriteAsync(session, rows[..100_000], ct);

        await using VortexFileWriter writer = await session.OpenWriterAsync(Location, cancellationToken: ct);
        await writer.WriteAsync<Reading>(rows[100_000..], ct);
        while (emulator.CopiedParts == 0)
        {
            await Task.Delay(5, ct);
        }

        emulator.Seed("lake", "readings.vortex", [1, 2, 3]);
        await Assert.ThrowsAsync<ObjectStoreException>(async () => await writer.CompleteAsync(ct));
        Assert.Equal([1, 2, 3], emulator.BytesOf("lake", "readings.vortex"));
        Assert.Equal(0, emulator.OpenUploads);
    }

    [Fact]
    public async Task AnAppendGivenUpAbortsItsUploadAndLeavesTheObjectAsItWas()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using S3Emulator emulator = new S3Emulator { MinimumPartSize = MinimumPart };
        await using VortexSession session = Session(Store(emulator));
        Reading[] rows = Rows(110_000);
        await WriteAsync(session, rows[..100_000], ct);
        byte[] old = emulator.BytesOf("lake", "readings.vortex")!;

        await using (VortexFileWriter writer = await session.OpenWriterAsync(Location, cancellationToken: ct))
        {
            await writer.WriteAsync<Reading>(rows[100_000..], ct);
        }

        Assert.Equal(old, emulator.BytesOf("lake", "readings.vortex"));
        Assert.Equal(0, emulator.OpenUploads);
    }

    internal static S3ObjectStore Store(S3Emulator emulator, string? endpoint = null, bool? copiesParts = null) =>
        new S3ObjectStore(new S3StoreOptions
        {
            Bucket = "lake",
            Region = endpoint is null ? "eu-west-3" : null,
            Endpoint = endpoint is null ? null : new Uri(endpoint),
            Credentials = S3Credentials.Static(S3Emulator.AccessKey, S3Emulator.Secret),
            Handler = emulator,
            CopiesParts = copiesParts,
            MinimumPartSize = MinimumPart,
            PartSize = MinimumPart,
            Transport = new StoreTransportOptions { RetryBaseDelay = TimeSpan.Zero },
        });

    /// <summary>Rows whose values vary, so that a file of them is not tiny.</summary>
    internal static Reading[] Rows(int count) =>
        [.. Enumerable.Range(0, count).Select(i => new Reading(i, ((i * 7919) % 4000) / 100.0))];

    private static VortexSession Session(S3ObjectStore store) => VortexSession.Create(o => o.ObjectStores.Add("s3://lake", store));

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
