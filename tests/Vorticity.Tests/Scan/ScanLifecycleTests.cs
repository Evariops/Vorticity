// Cancellation, disposal, refcounts and concurrency - contract §13.4.
//
// The abandoned enumeration is the case that leaks, and it is the reason DisposeAsync releases
// everything whether the scan completed, threw, or was walked away from. Nothing else in the suite
// exercises it: a `foreach` that runs to completion disposes the last batch on the way out, so a
// scan that only released on completion would look perfect.
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Vorticity.Columns;
using Vorticity.File;
using Vorticity.IO;
using Vorticity.Scan;
using Xunit;

namespace Vorticity.Tests.Scan;

public sealed class ScanLifecycleTests
{
    private const string Multi = "distributions/high_cardinality_i64_r8193";

    [Fact]
    public async Task CancellationBeforeTheFirstBatch()
    {
        Decoders.EnsureRegistered();
        await using VortexFile file = await VortexFile.OpenAsync(Corpus.Path(Multi), CancellationToken.None);

        using CancellationTokenSource cts = new CancellationTokenSource();
        await cts.CancelAsync();

        IAsyncEnumerator<RecordBatch> enumerator =
            file.Scan().ExecuteAsync().GetAsyncEnumerator(cts.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await enumerator.MoveNextAsync());
        await enumerator.DisposeAsync();
    }

    [Fact]
    public async Task CancellationBetweenBatches()
    {
        Decoders.EnsureRegistered();
        await using VortexFile file = await VortexFile.OpenAsync(Corpus.Path(Multi), CancellationToken.None);

        using CancellationTokenSource cts = new CancellationTokenSource();
        IAsyncEnumerator<RecordBatch> enumerator =
            file.Scan().WithMaxBatchRows(1000).ExecuteAsync().GetAsyncEnumerator(cts.Token);

        Assert.True(await enumerator.MoveNextAsync());

        // Sub-division is even, not "max then remainder": 8193 rows capped at 1000 gives nine
        // splits of 911, not eight of 1000 and one of 193 (upstream's subdivide_large_spans).
        Assert.Equal(911, enumerator.Current.RowCount);

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await enumerator.MoveNextAsync());
        await enumerator.DisposeAsync();
    }

    [Fact]
    public async Task CancellationDuringARead()
    {
        Decoders.EnsureRegistered();
        GatedSegmentSource source = new GatedSegmentSource(
            MemoryMappedSegmentSource.Open(Corpus.Path(Multi)));
        await using VortexFile file = await VortexFile.OpenAsync(
            source, new VortexOpenOptions(), CancellationToken.None);

        using CancellationTokenSource cts = new CancellationTokenSource();
        IAsyncEnumerator<RecordBatch> enumerator =
            file.Scan().ExecuteAsync().GetAsyncEnumerator(cts.Token);

        ValueTask<bool> pending = enumerator.MoveNextAsync();
        Assert.False(pending.IsCompleted, "a gated source must park the read");

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pending);
        await enumerator.DisposeAsync();
    }

    [Fact]
    public async Task TheAsynchronousReadPathProducesTheSameRows()
    {
        // The gated source never completes synchronously, so this is the only test that drives the
        // ManualResetValueTaskSourceCore branch of MoveNextAsync end to end.
        Decoders.EnsureRegistered();
        GatedSegmentSource source = new GatedSegmentSource(
            MemoryMappedSegmentSource.Open(Corpus.Path(Multi)));
        await using VortexFile file = await VortexFile.OpenAsync(
            source, new VortexOpenOptions(), CancellationToken.None);

        IAsyncEnumerator<RecordBatch> enumerator =
            file.Scan().WithMaxBatchRows(2000).ExecuteAsync().GetAsyncEnumerator();

        long rows = 0;
        while (true)
        {
            ValueTask<bool> move = enumerator.MoveNextAsync();
            if (!move.IsCompleted)
            {
                // Release the parked read from outside; the enumerator resumes on a pool thread.
                source.ReleaseAll();
            }

            if (!await move)
            {
                break;
            }

            rows += enumerator.Current.RowCount;
        }

        await enumerator.DisposeAsync();
        Assert.Equal(8193, rows);
    }

    [Fact]
    public async Task AbandoningAfterOneBatchReleasesEverySegment()
    {
        Decoders.EnsureRegistered();
        TrackingSegmentSource source = new TrackingSegmentSource(
            MemoryMappedSegmentSource.Open(Corpus.Path(Multi)));
        await using VortexFile file = await VortexFile.OpenAsync(
            source, new VortexOpenOptions(), CancellationToken.None);

        IAsyncEnumerator<RecordBatch> enumerator =
            file.Scan().WithMaxBatchRows(1000).ExecuteAsync().GetAsyncEnumerator();

        Assert.True(await enumerator.MoveNextAsync());
        Assert.True(source.OwnerCount > 0, "the first batch must have read something");
        Assert.True(source.LiveOwners > 0, "the current batch still holds its segments");

        // Walk away mid-stream.
        await enumerator.DisposeAsync();

        Assert.Equal(0, source.LiveOwners);
        Assert.False(source.AnyOverReleased);
    }

    [Fact]
    public async Task ACompletedScanReleasesEverySegment()
    {
        Decoders.EnsureRegistered();
        TrackingSegmentSource source = new TrackingSegmentSource(
            MemoryMappedSegmentSource.Open(Corpus.Path(Multi)));
        await using VortexFile file = await VortexFile.OpenAsync(
            source, new VortexOpenOptions(), CancellationToken.None);

        IAsyncEnumerator<RecordBatch> enumerator =
            file.Scan().WithMaxBatchRows(1000).ExecuteAsync().GetAsyncEnumerator();
        while (await enumerator.MoveNextAsync())
        {
            Assert.True(enumerator.Current.RowCount > 0);
        }

        await enumerator.DisposeAsync();

        Assert.True(source.OwnerCount >= 9);
        Assert.Equal(0, source.LiveOwners);
        Assert.False(source.AnyOverReleased);
    }

    [Fact]
    public async Task DisposeIsIdempotentAndMoveNextAfterItThrows()
    {
        Decoders.EnsureRegistered();
        await using VortexFile file = await VortexFile.OpenAsync(Corpus.Path(Multi), CancellationToken.None);

        IAsyncEnumerator<RecordBatch> enumerator = file.Scan().ExecuteAsync().GetAsyncEnumerator();
        Assert.True(await enumerator.MoveNextAsync());

        await enumerator.DisposeAsync();
        await enumerator.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await enumerator.MoveNextAsync());
    }

    [Fact]
    public async Task CurrentBeforeMoveNextThrows()
    {
        Decoders.EnsureRegistered();
        await using VortexFile file = await VortexFile.OpenAsync(Corpus.Path(Multi), CancellationToken.None);

        IAsyncEnumerator<RecordBatch> enumerator = file.Scan().ExecuteAsync().GetAsyncEnumerator();
        Assert.Throws<InvalidOperationException>(() => enumerator.Current);
        await enumerator.DisposeAsync();
    }

    [Fact]
    public async Task ThePreviousBatchIsDisposedByTheNextMoveNext()
    {
        Decoders.EnsureRegistered();
        await using VortexFile file = await VortexFile.OpenAsync(Corpus.Path(Multi), CancellationToken.None);

        IAsyncEnumerator<RecordBatch> enumerator =
            file.Scan().WithMaxBatchRows(1000).ExecuteAsync().GetAsyncEnumerator();

        Assert.True(await enumerator.MoveNextAsync());
        RecordBatch first = enumerator.Current;
        Assert.True(await enumerator.MoveNextAsync());

        // The arenas were reused, so the previous batch is dead. Touching it must say so rather
        // than hand back another batch's memory.
        Assert.Throws<ObjectDisposedException>(() => first.Column(0));
        await enumerator.DisposeAsync();
    }

    [Fact]
    public async Task TwoConcurrentEnumerationsOfOneFileAgree()
    {
        Decoders.EnsureRegistered();
        await using VortexFile file = await VortexFile.OpenAsync(Corpus.Path(Multi), CancellationToken.None);

        Task<long[]> left = Task.Run(() => Sum(file));
        Task<long[]> right = Task.Run(() => Sum(file));
        long[][] results = await Task.WhenAll(left, right);

        Assert.Equal(results[0].Length, results[1].Length);
        for (int i = 0; i < results[0].Length; i++)
        {
            Assert.Equal(results[0][i], results[1][i]);
        }
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(8)]
    public async Task ParallelSplitsProduceTheSameRowsInTheSameOrder(int degree)
    {
        Decoders.EnsureRegistered();
        await using VortexFile file = await VortexFile.OpenAsync(Corpus.Path(Multi), CancellationToken.None);

        long[] sequential = await Read(file, 1, 500);
        long[] parallel = await Read(file, degree, 500);

        Assert.Equal(sequential.Length, parallel.Length);
        for (int i = 0; i < sequential.Length; i++)
        {
            Assert.Equal(sequential[i], parallel[i]);
        }
    }

    [Fact]
    public async Task AbandoningAParallelScanReleasesEverySegment()
    {
        Decoders.EnsureRegistered();
        TrackingSegmentSource source = new TrackingSegmentSource(
            MemoryMappedSegmentSource.Open(Corpus.Path(Multi)));
        await using VortexFile file = await VortexFile.OpenAsync(
            source, new VortexOpenOptions(), CancellationToken.None);

        IAsyncEnumerator<RecordBatch> enumerator = file.Scan()
            .WithMaxBatchRows(500)
            .WithDegreeOfParallelism(4)
            .ExecuteAsync()
            .GetAsyncEnumerator();

        Assert.True(await enumerator.MoveNextAsync());
        Assert.True(await enumerator.MoveNextAsync());

        await enumerator.DisposeAsync();

        Assert.Equal(0, source.LiveOwners);
        Assert.False(source.AnyOverReleased);
    }

    [Fact]
    public async Task AParallelScanOfTheForgedFileStillThrowsNamed()
    {
        Decoders.EnsureRegistered();
        await using VortexFile file = await VortexFile.OpenAsync(
            Corpus.Forged("negative/unknown_encoding_id"), CancellationToken.None);

        VortexUnsupportedException error = await Assert.ThrowsAsync<VortexUnsupportedException>(async () =>
        {
            await foreach (RecordBatch batch in file.Scan()
                .WithDegreeOfParallelism(4)
                .WithMaxBatchRows(512)
                .ExecuteAsync())
            {
                Assert.NotNull(batch);
            }
        });

        Assert.Equal("vortex.unknown01", error.ComponentId);
    }

    private static async Task<long[]> Sum(VortexFile file) => await Read(file, 1, 700);

    private static async Task<long[]> Read(VortexFile file, int degree, int cap)
    {
        List<long> values = new List<long>();
        await foreach (RecordBatch batch in file.Scan()
            .WithDegreeOfParallelism(degree)
            .WithMaxBatchRows(cap)
            .ExecuteAsync())
        {
            ReadOnlySpan<long> span = batch.Column(0).AsPrimitive<long>().Values;
            for (int i = 0; i < span.Length; i++)
            {
                values.Add(span[i]);
            }
        }

        return values.ToArray();
    }
}
