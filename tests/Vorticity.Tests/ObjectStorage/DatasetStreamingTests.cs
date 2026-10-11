// A dataset's data object past its threshold goes up while it is written: the store reads the sink's
// chunks as they fill and sends its parts, the commit waits for the last of them and the completion.
// On a service that holds a conditional put whole, R2 here, the object goes without the condition its
// fresh uid makes needless; a draft given up aborts its upload.
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.IO;
using Vorticity.ObjectStorage;
using Xunit;
using Reading = Vorticity.Tests.ObjectStorage.LocationTests.Reading;

namespace Vorticity.Tests.ObjectStorage;

public sealed class DatasetStreamingTests
{
    private const int Part = 32 << 10;

    [Fact]
    public async Task AnObjectPastTheThresholdGoesUpInPartsWhileItIsWrittenAndOnItsKeysAbsence()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using S3Emulator emulator = new S3Emulator { MinimumPartSize = Part };
        ConcurrentQueue<string?> conditions = Conditions(emulator);
        await using S3ObjectStore store = Store(emulator);
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Reading.Schema, Options(), ct);
        Reading[] rows;

        await using (ObjectDraft draft = dataset.StartObject())
        {
            // Before the commit: the upload starts while the rows are written.
            rows = await WriteUntilAsync(draft, () => emulator.Log.Any(line => line.EndsWith("?uploads", StringComparison.Ordinal)), ct);
            Assert.True(draft.Sink.IsStreaming);
            await dataset.AppendAsync(draft, ct);
        }

        Assert.Equal("*", Assert.Single(conditions));
        Assert.True(emulator.Log.Count(line => line.Contains("partNumber=", StringComparison.Ordinal)) > 2);
        Assert.Equal(0, emulator.OpenUploads);
        Assert.Equal(rows, (await dataset.Scan<Reading>().ToRecordsAsync(ct).ToListAsync(ct)).ToArray());
        Assert.True((await dataset.VerifyAsync(cancellationToken: ct)).Holds);
    }

    [Fact]
    public async Task OnR2ALargeDataObjectGoesUpInPartsWithoutTheConditionItsUidMakesNeedless()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using S3Emulator emulator = new S3Emulator { MinimumPartSize = Part, Region = "auto" };
        ConcurrentQueue<string?> conditions = Conditions(emulator);
        await using S3ObjectStore store = Store(emulator, "https://account.r2.cloudflarestorage.com");
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Reading.Schema, Options(), ct);
        Reading[] rows = S3AppendTests.Rows(300_000);

        await using (ObjectDraft draft = dataset.StartObject())
        {
            await draft.Writer.WriteAsync<Reading>(rows, ct);
            await dataset.AppendAsync(draft, ct);
        }

        // In parts, and with no condition, where a conditional put would have been held whole.
        Assert.Null(Assert.Single(conditions));
        Assert.Equal(rows, (await dataset.Scan<Reading>().ToRecordsAsync(ct).ToListAsync(ct)).ToArray());
    }

    [Fact]
    public async Task ADraftGivenUpAbortsTheUploadItStarted()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using S3Emulator emulator = new S3Emulator { MinimumPartSize = Part };
        await using S3ObjectStore store = Store(emulator);
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Reading.Schema, Options(), ct);
        int objects = emulator.Count("lake");

        await using (ObjectDraft draft = dataset.StartObject())
        {
            await WriteUntilAsync(draft, () => emulator.OpenUploads == 1, ct);
        }

        Assert.Equal(0, emulator.OpenUploads);
        Assert.Equal(objects, emulator.Count("lake"));
    }

    [Fact]
    public async Task AnEncryptedDatasetStreamsItsSealedObjectAndRecordsItsSealedLength()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using VortexKeyring keyring = Vorticity.Tests.Sealing.SealedObjects.Keyring();
        await using VortexSession session = VortexSession.Create(o => o.Keyring = keyring);
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await VortexDataset.CreateAsync(
            store, Reading.Schema, Options() with { Session = session, Encrypted = true }, ct);
        Reading[] rows = S3AppendTests.Rows(300_000);

        await using (ObjectDraft draft = dataset.StartObject())
        {
            await draft.Writer.WriteAsync<Reading>(rows, ct);
            await dataset.AppendAsync(draft, ct);
            Assert.True(draft.Sink.IsStreaming);
        }

        Assert.Equal(rows, (await dataset.Scan<Reading>().ToRecordsAsync(ct).ToListAsync(ct)).ToArray());
        Assert.True((await dataset.VerifyAsync(cancellationToken: ct)).Holds);
    }

    [Fact]
    public async Task ASmallObjectGoesInOnePutAtTheCommit()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using S3Emulator emulator = new S3Emulator { MinimumPartSize = Part };
        await using S3ObjectStore store = Store(emulator);
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Reading.Schema, Options(), ct);

        await using (ObjectDraft draft = dataset.StartObject())
        {
            await draft.Writer.WriteAsync<Reading>(S3AppendTests.Rows(100), ct);
            await dataset.AppendAsync(draft, ct);
            Assert.False(draft.Sink.IsStreaming);
        }

        Assert.DoesNotContain(emulator.Log, line => line.Contains("uploads", StringComparison.Ordinal));
    }

    /// <summary>The <c>If-None-Match</c> of every completion of an upload, null for none.</summary>
    private static ConcurrentQueue<string?> Conditions(S3Emulator emulator)
    {
        ConcurrentQueue<string?> conditions = new ConcurrentQueue<string?>();
        emulator.Intercept = (request, _) =>
        {
            if (request.Method == HttpMethod.Post && request.RequestUri!.Query.StartsWith("?uploadId=", StringComparison.Ordinal))
            {
                conditions.Enqueue(request.Headers.TryGetValues("If-None-Match", out var values) ? values.Single() : null);
            }

            return null;
        };
        return conditions;
    }

    private static S3ObjectStore Store(S3Emulator emulator, string? endpoint = null) =>
        new S3ObjectStore(new S3StoreOptions
        {
            Bucket = "lake",
            Region = endpoint is null ? "eu-west-3" : null,
            Endpoint = endpoint is null ? null : new Uri(endpoint),
            Credentials = S3Credentials.Static(S3Emulator.AccessKey, S3Emulator.Secret),
            Handler = emulator,
            MinimumPartSize = Part,
            PartSize = Part,
            PartThreshold = 2 * Part,
            Transport = new StoreTransportOptions { RetryBaseDelay = TimeSpan.Zero },
        });

    private static DatasetOptions Options() => new DatasetOptions { StreamObjectsFromBytes = 2 * Part };

    /// <summary>
    /// Writes rows into the draft, each batch flushed to its sink, until <paramref name="condition"/>
    /// holds: the store reads what a flush publishes, and waits for the next to learn whether the object
    /// ended, as a reader of a pipe does.
    /// </summary>
    /// <returns>Every row written, in order.</returns>
    private static async Task<Reading[]> WriteUntilAsync(ObjectDraft draft, Func<bool> condition, CancellationToken ct)
    {
        Stopwatch waited = Stopwatch.StartNew();
        System.Collections.Generic.List<Reading> written = [];
        while (!condition())
        {
            Assert.True(waited.Elapsed < TimeSpan.FromSeconds(30), "the condition never held");
            Reading[] batch = [.. S3AppendTests.Rows(written.Count + 20_000).Skip(written.Count)];
            await draft.Writer.WriteAsync<Reading>(batch, ct);
            await draft.Writer.FlushAsync(ct);
            written.AddRange(batch);
            await Task.Delay(5, ct);
        }

        return [.. written];
    }
}
