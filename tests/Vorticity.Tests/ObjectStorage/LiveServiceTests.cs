// What only the services themselves can prove, beyond the store contract the suite already runs
// against them: an upload in parts the service assembles, the suffix reads a profile takes as absent
// until proven, and a dataset that lives in a bucket. Each test skips while the environment names no
// bucket for its service.
using System;
using System.Buffers;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.IO;
using Xunit;

namespace Vorticity.Tests.ObjectStorage;

public sealed class LiveServiceTests
{
    [Theory]
    [InlineData("s3-live")]
    [InlineData("azure-live")]
    [InlineData("gcs-live")]
    public async Task AnObjectPastThePartThresholdGoesUpInPartsAndReadsBackWhole(string kind)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        Assert.SkipWhen(!LiveStores.Named(kind), $"{LiveStores.VariableOf(kind)} names no bucket.");
        await using IObjectStore store = LiveStores.Open(kind);

        // 40 MiB, past the 32 MiB threshold: three parts of 16 MiB at most, assembled by the service.
        byte[] content = new byte[40 << 20];
        new Random(40).NextBytes(content);
        Assert.Equal(PutOutcome.Created, await store.PutAsync("parts.bin", new ReadOnlySequence<byte>(content), PutCondition.None, ct));

        ObjectHead head = (await store.HeadAsync("parts.bin", ct))!.Value;
        Assert.Equal(content.Length, head.Length);
        byte[] back = new byte[content.Length];
        ObjectRead read = await store.ReadAsync("parts.bin", 0, back, head.Token, ct);
        Assert.Equal((content.Length, (long)content.Length), (read.BytesRead, read.ObjectLength));
        Assert.True(content.AsSpan().SequenceEqual(back));
    }

    [Theory]
    [InlineData("s3-live")]
    [InlineData("gcs-live")]
    public async Task ASuffixReadSaysTheObjectsLengthWithItsLastBytes(string kind)
    {
        // What sets ReadsSuffix for the service under test, which its profile takes as absent until this
        // passes against it. Azure reads no suffix.
        CancellationToken ct = TestContext.Current.CancellationToken;
        Assert.SkipWhen(!LiveStores.Named(kind), $"{LiveStores.VariableOf(kind)} names no bucket.");
        await using IObjectStore store = LiveStores.Open(kind, readsSuffix: true);
        await store.PutAsync("tail", new ReadOnlySequence<byte>(Encoding.ASCII.GetBytes("0123456789")), PutCondition.None, ct);

        byte[] four = new byte[4];
        ObjectRead tail = await store.ReadTailAsync("tail", four, ct);
        Assert.Equal((4, 10L, "6789"), (tail.BytesRead, tail.ObjectLength, Encoding.ASCII.GetString(four)));

        // A suffix longer than the object brings it whole.
        byte[] larger = new byte[64];
        ObjectRead whole = await store.ReadTailAsync("tail", larger, ct);
        Assert.Equal((10, 10L, "0123456789"), (whole.BytesRead, whole.ObjectLength, Encoding.ASCII.GetString(larger, 0, 10)));
    }

    [Theory]
    [InlineData("s3-live")]
    [InlineData("azure-live")]
    [InlineData("gcs-live")]
    public async Task ADatasetIsCreatedAppendedToAndReadBackInTheBucket(string kind)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        Assert.SkipWhen(!LiveStores.Named(kind), $"{LiveStores.VariableOf(kind)} names no bucket.");
        await using IObjectStore store = LiveStores.Open(kind);
        await using (VortexDataset dataset = await VortexDataset.CreateAsync(store, LocationTests.Reading.Schema, cancellationToken: ct))
        {
            for (int i = 0; i < 3; i++)
            {
                await AppendAsync(dataset, 1_000, ct);
            }
        }

        await using VortexDataset reopened = await VortexDataset.OpenAsync(store, cancellationToken: ct);
        Assert.Equal(4UL, reopened.Version);
        Assert.Equal(3_000, await reopened.Scan<LocationTests.Reading>().CountAsync(ct));
    }

    private static async Task AppendAsync(VortexDataset dataset, int rows, CancellationToken ct)
    {
        await using ObjectDraft draft = dataset.StartObject();
        await draft.Writer.WriteAsync<LocationTests.Reading>([.. Enumerable.Range(0, rows).Select(i => new LocationTests.Reading(i, i * 0.25))], ct);
        await dataset.AppendAsync(draft, ct);
    }
}
