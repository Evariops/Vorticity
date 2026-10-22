// A dataset's objects in a bucket: a data object goes up at the commit from the dataset's own buffer,
// whole or in parts, without the condition its fresh uid makes needless, on every service, R2
// included; a commit object keeps its condition; a draft given up sends nothing; an encrypted
// dataset's object goes sealed and its sealed length is recorded.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.IO;
using Vorticity.ObjectStorage;
using Vorticity.Types;
using Vorticity.Writing;
using Xunit;
using Reading = Vorticity.Tests.ObjectStorage.LocationTests.Reading;

namespace Vorticity.Tests.ObjectStorage;

public sealed partial class DatasetObjectTests
{
    private const int Part = 32 << 10;

    [Theory]
    [InlineData(null)]
    [InlineData("https://account.r2.cloudflarestorage.com")]
    public async Task ALargeDataObjectGoesUpInPartsAtTheCommitWithoutACondition(string? endpoint)
    {
        // On R2 as on S3: R2 takes no condition on the parts of an upload, and the data object's key,
        // ending with a fresh uid, needs none.
        CancellationToken ct = TestContext.Current.CancellationToken;
        using S3Emulator emulator = new S3Emulator { MinimumPartSize = Part, Region = endpoint is null ? "eu-west-3" : "auto" };
        ConcurrentQueue<string?> conditions = Conditions(emulator);
        await using S3ObjectStore store = Store(emulator, endpoint);
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Reading.Schema, new DatasetOptions(), ct);
        Reading[] rows = Rows(300_000);

        await using (ObjectDraft draft = dataset.StartObject())
        {
            await draft.Writer.WriteAsync<Reading>(rows, ct);

            // Nothing goes before the commit: the dataset reads the object's statistics from its buffer.
            Assert.DoesNotContain(emulator.Log, line => line.Contains("data/", StringComparison.Ordinal));
            await dataset.AppendAsync(draft, ct);
        }

        Assert.Null(Assert.Single(conditions));
        Assert.True(emulator.Log.Count(line => line.Contains("partNumber=", StringComparison.Ordinal)) > 2);
        Assert.Equal(0, emulator.OpenUploads);

        // The commit object keeps its condition.
        Assert.Contains(emulator.Log, line => line.StartsWith("PUT", StringComparison.Ordinal) && line.Contains("commit", StringComparison.Ordinal));
        Assert.Equal(rows, (await dataset.Scan<Reading>().ToRecordsAsync(ct).ToListAsync(ct)).ToArray());
        Assert.True((await dataset.VerifyAsync(cancellationToken: ct)).Holds);
    }

    [Fact]
    public async Task ASmallObjectGoesInOnePutAtTheCommit()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using S3Emulator emulator = new S3Emulator { MinimumPartSize = Part };
        await using S3ObjectStore store = Store(emulator);
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Reading.Schema, new DatasetOptions(), ct);

        await using (ObjectDraft draft = dataset.StartObject())
        {
            await draft.Writer.WriteAsync<Reading>(Rows(100), ct);
            await dataset.AppendAsync(draft, ct);
        }

        Assert.DoesNotContain(emulator.Log, line => line.Contains("uploads", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ADraftGivenUpSendsNothing()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using S3Emulator emulator = new S3Emulator { MinimumPartSize = Part };
        await using S3ObjectStore store = Store(emulator);
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Reading.Schema, new DatasetOptions(), ct);
        int objects = emulator.Count("lake");

        await using (ObjectDraft draft = dataset.StartObject())
        {
            await draft.Writer.WriteAsync<Reading>(Rows(300_000), ct);
        }

        Assert.DoesNotContain(emulator.Log, line => line.Contains("/data/", StringComparison.Ordinal));
        Assert.Equal((objects, 0), (emulator.Count("lake"), emulator.OpenUploads));
    }

    [Fact]
    public async Task AnEncryptedDatasetsObjectGoesSealedAndItsSealedLengthIsRecorded()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using VortexKeyring keyring = Vorticity.Tests.Sealing.SealedObjects.Keyring();
        await using VortexSession session = VortexSession.Create(o => o.Keyring = keyring);
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await VortexDataset.CreateAsync(
            store, Reading.Schema, new DatasetOptions { Session = session, Encrypted = true }, ct);
        Reading[] rows = Rows(300_000);

        await using (ObjectDraft draft = dataset.StartObject())
        {
            await draft.Writer.WriteAsync<Reading>(rows, ct);
            await dataset.AppendAsync(draft, ct);
        }

        Assert.Equal(rows, (await dataset.Scan<Reading>().ToRecordsAsync(ct).ToListAsync(ct)).ToArray());
        Assert.True((await dataset.VerifyAsync(cancellationToken: ct)).Holds);
    }

    /// <summary>
    /// A load whose object streams to the store and then fails before its end, here because the period's second read gives
    /// ten rows fewer than its first, gives its upload up: the failure is the load's, and the bucket holds no open upload.
    /// </summary>
    [Fact]
    public async Task AStreamedObjectThatFailsBeforeItsEndGivesItsUploadUp()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using S3Emulator emulator = new S3Emulator { MinimumPartSize = Part };
        await using S3ObjectStore store = Store(emulator);
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Logged.Schema, new DatasetOptions
        {
            ClusteringKey = [nameof(Logged.At)],
            Periods = new PeriodOptions { Lateness = TimeSpan.FromMinutes(5), Length = SliceWidth.Hour, SliceTargetRows = 50_000 },
            StreamedObjectBytes = 64 << 10,
        }, ct);
        // Noise that no encoding shrinks, written block after block, streams the object long before the variant's long
        // chunks go out at its end.
        DateTimeOffset noon = new DateTimeOffset(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);
        Random random = new Random(0x5EA1);
        Logged[] rows = [.. Enumerable.Range(0, 400_000).Select(i => new Logged(
            noon.AddMilliseconds(i * 5),
            random.NextInt64(),
            Variant.Object(("route", Variant.From($"/r/{i % 37}")), ("size", Variant.From((long)i * 7_919 % 100_003)))))];
        byte[] whole = await FileOfAsync(rows, ct);
        byte[] fewer = await FileOfAsync(rows[..^10], ct);
        int reads = 0;
        PeriodRows hour = new PeriodRows(
            TimeSlicer.InstantOf(noon, TimeUnit.Microseconds), [token => BatchesAsync(Interlocked.Increment(ref reads) == 1 ? whole : fewer, token)]);

        await Assert.ThrowsAsync<DatasetIntegrityException>(async () => await DatasetCompactor.LoadPeriodsAsync(dataset, [hour], degree: 1, cancellationToken: ct));
        Assert.Equal(2, reads);
        Assert.Contains(emulator.Log, line => line.Contains("?uploads", StringComparison.Ordinal));

        // The upload is given up as the put sees its content fail, behind the load's failure.
        System.Diagnostics.Stopwatch waited = System.Diagnostics.Stopwatch.StartNew();
        while (emulator.OpenUploads > 0 && waited.Elapsed < TimeSpan.FromSeconds(10))
        {
            await Task.Delay(10, ct);
        }

        Assert.Equal(0, emulator.OpenUploads);
    }

    [VortexRecord]
    public partial record struct Logged(DateTimeOffset At, long Noise, Variant Attributes);

    /// <summary><paramref name="rows"/> as a file of their own.</summary>
    private static async Task<byte[]> FileOfAsync(Logged[] rows, CancellationToken ct)
    {
        using MemoryStream stream = new MemoryStream();
        await using (VortexFileWriter writer = VortexFileWriter.Create(
            new StreamSegmentSink(stream), VortexTypes.ToDType(Logged.Schema, new DTypeArena()), new VortexWriteOptions()))
        {
            await writer.WriteAsync<Logged>(rows, ct);
            await writer.CompleteAsync(ct);
        }

        return stream.ToArray();
    }

    /// <summary>The rows of <paramref name="file"/>, in its order, as a load reads a period.</summary>
    private static async IAsyncEnumerable<RecordBatch> BatchesAsync(byte[] file, [EnumeratorCancellation] CancellationToken ct)
    {
        await using VortexFile open = await VortexFile.OpenAsync(new MemorySegmentSource(file), VortexOpenOptions.Default);
        await foreach (RecordBatch batch in open.Scan<Logged>().With(new ScanOptions { BatchRows = 4_096 }).ToBatchesAsync(ct))
        {
            yield return batch;
        }
    }

    /// <summary>Rows whose temperatures repeat in no short cycle, so that their chunks do not compress to nothing.</summary>
    internal static Reading[] Rows(int count) =>
        [.. Enumerable.Range(0, count).Select(i => new Reading(i, ((i * 7919) % 4000) / 100.0))];

    /// <summary>The <c>If-None-Match</c> of every put of a data object, whole or as the completion of its upload, null for none.</summary>
    private static ConcurrentQueue<string?> Conditions(S3Emulator emulator)
    {
        ConcurrentQueue<string?> conditions = new ConcurrentQueue<string?>();
        emulator.Intercept = (request, _) =>
        {
            bool data = request.RequestUri!.AbsolutePath.Contains("/data/", StringComparison.Ordinal);
            bool completion = request.Method == HttpMethod.Post && request.RequestUri.Query.StartsWith("?uploadId=", StringComparison.Ordinal);
            bool whole = request.Method == HttpMethod.Put && request.RequestUri.Query.Length == 0;
            if (data && (completion || whole))
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
            Credentials = StoreCredentials.AccessKey(S3Emulator.AccessKey, S3Emulator.Secret),
            Handler = emulator,
            MinimumPartSize = Part,
            PartSize = Part,
            PartThreshold = 2 * Part,
            Transport = new StoreTransportOptions { RetryBaseDelay = TimeSpan.Zero },
        });
}
