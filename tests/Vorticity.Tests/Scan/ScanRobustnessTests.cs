// Malformed input, hostile callers, and the row counts the corpus was built around.
//
// The scan parses no wire structure of its own - it consumes a LayoutTree the layouts component
// already validated - so a truncation test at every structural boundary it parses has
// nothing to bite on here. What it does own is the boundary between a caller's mistake and a file's:
// a bad projection path, a negative range or a disposed enumerator is Argument*/ObjectDisposed, and
// a broken file is VortexFormatException. The truncation cases below assert the second half of that
// - a truncated file must never reach the scan as an IndexOutOfRangeException.
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Vorticity.Buffers;
using Vorticity.Columns;
using Vorticity.File;
using Vorticity.IO;
using Vorticity.Scan;
using Vorticity.Serialization.Schemas;
using Xunit;

namespace Vorticity.Tests.Scan;

public sealed class ScanRobustnessTests
{
    [Theory]
    [InlineData("types/i64_nonnull_r0", 0)]
    [InlineData("types/i64_nonnull_r1", 1)]
    [InlineData("types/i64_nonnull_r1023", 1023)]
    [InlineData("types/i64_nonnull_r1024", 1024)]
    [InlineData("types/i64_nonnull_r1025", 1025)]
    [InlineData("types/i64_nonnull_r8191", 8191)]
    [InlineData("types/i64_nonnull_r8192", 8192)]
    [InlineData("types/i64_nonnull_r8193", 8193)]
    public async Task TheBoundaryRowCountsScanWholeAndInPieces(string entry, long rows)
    {
        Decoders.EnsureRegistered();
        await using VortexFile file = await VortexFile.OpenAsync(Corpus.Path(entry), CancellationToken.None);
        Assert.Equal(rows, file.RowCount);

        int[] caps = [1, 7, 1023, 1024, 1025, 8191, 8192, 8193, int.MaxValue];
        for (int c = 0; c < caps.Length; c++)
        {
            // A one-row cap over 8193 rows is 8193 batches; keep the sweep honest but bounded.
            if (caps[c] < 7 && rows > 1025)
            {
                continue;
            }

            long seen = 0;
            long expectedStart = 0;
            await foreach (RecordBatch batch in file.Scan().WithMaxBatchRows(caps[c]).ExecuteAsync())
            {
                Assert.Equal(expectedStart, batch.StartRow);
                expectedStart += batch.RowCount;
                seen += batch.RowCount;
            }

            Assert.Equal(rows, seen);
        }
    }

    [Fact]
    public async Task AZeroLengthFileIsAFormatError()
    {
        string path = TempPath();
        try
        {
            await System.IO.File.WriteAllBytesAsync(path, []);
            await Assert.ThrowsAsync<VortexFormatException>(async () =>
            {
                await using VortexFile file = await VortexFile.OpenAsync(path, CancellationToken.None);
            });
        }
        finally
        {
            Delete(path);
        }
    }

    [Theory]
    [InlineData(8)]
    [InlineData(64)]
    [InlineData(1024)]
    [InlineData(50_000)]
    [InlineData(100_000)]
    public async Task ATruncatedFileIsAFormatErrorNotACrash(int keep)
    {
        Decoders.EnsureRegistered();
        byte[] whole = await System.IO.File.ReadAllBytesAsync(Corpus.Path("containers/uncompressed_canonical"));
        Assert.True(keep < whole.Length);

        string path = TempPath();
        try
        {
            await System.IO.File.WriteAllBytesAsync(path, whole.AsMemory(0, keep).ToArray());

            // Either the open rejects it or the scan does; nothing else is acceptable, and in
            // particular no IndexOutOfRange, no OverflowException and no wrong answer.
            await Assert.ThrowsAsync<VortexFormatException>(async () =>
            {
                await using VortexFile file = await VortexFile.OpenAsync(path, CancellationToken.None);
                await foreach (RecordBatch batch in file.Scan().ExecuteAsync())
                {
                    Assert.NotNull(batch);
                }
            });
        }
        finally
        {
            Delete(path);
        }
    }

    [Fact]
    public void ScanRejectsANullFile()
    {
        Assert.Throws<ArgumentNullException>(() => VortexFileScanExtensions.Scan(null!));
    }

    [Fact]
    public async Task ProjectRejectsANullArray()
    {
        await using VortexFile file = await VortexFile.OpenAsync(
            Corpus.Path("containers/uncompressed_canonical"), CancellationToken.None);

        Assert.Throws<ArgumentNullException>(() => file.Scan().Project((string[])null!));
        Assert.Throws<ArgumentNullException>(() => file.Scan().Project((string)null!));
    }

    [Fact]
    public async Task ProjectWithNoPathsLeavesTheProjectionAll()
    {
        Decoders.EnsureRegistered();
        await using VortexFile file = await VortexFile.OpenAsync(
            Corpus.Path("containers/uncompressed_canonical"), CancellationToken.None);

        BatchAsyncEnumerable scan = Assert.IsType<BatchAsyncEnumerable>(file.Scan().Project().ExecuteAsync());
        Assert.True(scan.Projection.IsAll);

        BatchAsyncEnumerable byIndex =
            Assert.IsType<BatchAsyncEnumerable>(file.Scan().ProjectFields(ReadOnlySpan<int>.Empty).ExecuteAsync());
        Assert.True(byIndex.Projection.IsAll);
    }

    [Fact]
    public async Task OneBuilderCanLaunchTwoIndependentScans()
    {
        Decoders.EnsureRegistered();
        await using VortexFile file = await VortexFile.OpenAsync(
            Corpus.Path("containers/uncompressed_canonical"), CancellationToken.None);

        ScanBuilder builder = file.Scan();
        IAsyncEnumerable<RecordBatch> first = builder.ExecuteAsync();

        // Mutating the builder afterwards must not reach the enumerable already handed out.
        builder.Project("strs");
        IAsyncEnumerable<RecordBatch> second = builder.ExecuteAsync();

        Assert.True(Assert.IsType<BatchAsyncEnumerable>(first).Projection.IsAll);
        Assert.False(Assert.IsType<BatchAsyncEnumerable>(second).Projection.IsAll);
    }

    [Fact]
    public async Task AFailingReadPropagatesAndReleasesEverything()
    {
        Decoders.EnsureRegistered();
        FailAfterSource source = new FailAfterSource(
            MemoryMappedSegmentSource.Open(Corpus.Path("distributions/high_cardinality_i64_r8193")), 2);
        await using VortexFile file = await VortexFile.OpenAsync(
            source, new VortexOpenOptions(), CancellationToken.None);

        IAsyncEnumerator<RecordBatch> enumerator =
            file.Scan().WithMaxBatchRows(1000).ExecuteAsync().GetAsyncEnumerator();

        Assert.True(await enumerator.MoveNextAsync());
        Assert.True(await enumerator.MoveNextAsync());
        await Assert.ThrowsAsync<IOException>(async () => await enumerator.MoveNextAsync());

        await enumerator.DisposeAsync();
        Assert.Equal(0, source.Live);
        Assert.False(source.AnyOverReleased);
    }

    // The same failure, delivered the way a REAL source delivers it. FailAfterSource above throws
    // from a non-async method, so its ValueTask is never created and MoveNextAsync's own
    // `catch { lane.Context.ResetBatch(); throw; }` runs. Every async ISegmentSource - including the
    // in-tree RandomAccessSegmentSource - instead returns a faulted ValueTask, and the failure
    // surfaces inside OnReadCompleted, whose catch must reset the lane, not only publish the
    // exception.
    //
    // What leaks is the SegmentRequestSet: AbandonPending releases the owners but deliberately
    // leaves the set "registered but unpopulated, ready to retry" (ISegmentSource), so a lane that
    // is never reset carries the failed split's specs into the next batch and issues one coalesced
    // read covering a superset of what that batch needs - breaking the one-read-per-split property
    // ScanIoTests.ExactlyOneReadManyPerBatch exists to protect.
    [Fact]
    public async Task AReadThatFaultsAsynchronouslyStillResetsTheLane()
    {
        Decoders.EnsureRegistered();

        // Three chunks of 100 rows, so consecutive splits need DISTINCT segments. Within one chunk
        // every split registers the same spec and SegmentRequestSet.Add dedupes it, which would
        // hide the leak.
        RecordingFaultingSource source = new RecordingFaultingSource(
            MemoryMappedSegmentSource.Open(Corpus.Path("containers/chunked_stream_3")), failOnCall: 2);
        await using VortexFile file = await VortexFile.OpenAsync(
            source, new VortexOpenOptions(), CancellationToken.None);

        IAsyncEnumerator<RecordBatch> enumerator =
            file.Scan().WithMaxBatchRows(100).ExecuteAsync().GetAsyncEnumerator();

        Assert.True(await enumerator.MoveNextAsync());
        await Assert.ThrowsAsync<IOException>(async () => await enumerator.MoveNextAsync());

        // Still legal to keep going: the enumerator is neither disposed nor drained, and the
        // synchronous path visibly supports it.
        Assert.True(await enumerator.MoveNextAsync());

        Assert.Equal(3, source.RequestCounts.Count);
        Assert.Equal(1, source.RequestCounts[0]);
        Assert.Equal(1, source.RequestCounts[1]);
        // Two here means the retried batch dragged the failed split's registration along.
        Assert.Equal(1, source.RequestCounts[2]);

        // The retried batch must ask for its own segment, not the failed one's.
        Assert.NotEqual(source.FirstSpecs[1], source.FirstSpecs[2]);

        await enumerator.DisposeAsync();
        Assert.Equal(0, source.Live);
        Assert.False(source.AnyOverReleased);
    }

    private static string TempPath() =>
        System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "vorticity-scan-" + Guid.NewGuid().ToString("N") + ".vortex");

    private static void Delete(string path)
    {
        try
        {
            System.IO.File.Delete(path);
        }
        catch (IOException)
        {
            // A test must not fail because the temp directory is busy.
        }
    }

    /// <summary>
    /// A tracking source whose <c>ReadManyAsync</c> is an <c>async</c> method - so its failure
    /// arrives as a FAULTED ValueTask, not an inline throw - and which records the request set it
    /// was handed on each call.
    /// </summary>
    private sealed class RecordingFaultingSource : ISegmentSource
    {
        private readonly TrackingSegmentSource _inner;
        private readonly int _failOnCall;
        private int _calls;

        internal RecordingFaultingSource(ISegmentSource inner, int failOnCall)
        {
            _inner = new TrackingSegmentSource(inner);
            _failOnCall = failOnCall;
        }

        internal List<int> RequestCounts { get; } = new List<int>();

        internal List<SegmentSpec> FirstSpecs { get; } = new List<SegmentSpec>();

        internal int Live => _inner.LiveOwners;

        internal bool AnyOverReleased => _inner.AnyOverReleased;

        public ValueTask<long> GetLengthAsync(CancellationToken cancellationToken) =>
            _inner.GetLengthAsync(cancellationToken);

        public ValueTask<SegmentOwner> ReadAsync(SegmentSpec spec, CancellationToken cancellationToken) =>
            _inner.ReadAsync(spec, cancellationToken);

        public async ValueTask ReadManyAsync(SegmentRequestSet requests, CancellationToken cancellationToken)
        {
            RequestCounts.Add(requests.Count);
            FirstSpecs.Add(requests.Count > 0 ? requests.GetSpec(0) : default);

            // Suspend first: the faulted ValueTask must be the asynchronous kind, which is what
            // routes the failure through BatchAsyncEnumerator.OnReadCompleted.
            await Task.Yield();

            if (++_calls == _failOnCall)
            {
                throw new IOException("the disk went away");
            }

            await _inner.ReadManyAsync(requests, cancellationToken);
        }

        public ValueTask<SegmentOwner> ReadRangeAsync(
            long offset, int length, int alignment, CancellationToken cancellationToken) =>
            _inner.ReadRangeAsync(offset, length, alignment, cancellationToken);

        public ValueTask DisposeAsync() => _inner.DisposeAsync();
    }

    /// <summary>A tracking source that starts failing after <c>n</c> successful batched reads.</summary>
    private sealed class FailAfterSource : ISegmentSource
    {
        private readonly TrackingSegmentSource _inner;
        private readonly int _allow;
        private int _calls;

        internal FailAfterSource(ISegmentSource inner, int allow)
        {
            _inner = new TrackingSegmentSource(inner);
            _allow = allow;
        }

        internal int Live => _inner.LiveOwners;

        internal bool AnyOverReleased => _inner.AnyOverReleased;

        public ValueTask<long> GetLengthAsync(CancellationToken cancellationToken) =>
            _inner.GetLengthAsync(cancellationToken);

        public ValueTask<SegmentOwner> ReadAsync(SegmentSpec spec, CancellationToken cancellationToken) =>
            _inner.ReadAsync(spec, cancellationToken);

        public ValueTask ReadManyAsync(SegmentRequestSet requests, CancellationToken cancellationToken)
        {
            if (++_calls > _allow)
            {
                throw new IOException("the disk went away");
            }

            return _inner.ReadManyAsync(requests, cancellationToken);
        }

        public ValueTask<SegmentOwner> ReadRangeAsync(
            long offset, int length, int alignment, CancellationToken cancellationToken) =>
            _inner.ReadRangeAsync(offset, length, alignment, cancellationToken);

        public ValueTask DisposeAsync() => _inner.DisposeAsync();
    }
}
