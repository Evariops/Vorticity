using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Buffers;
using Vorticity.Columns;
using Vorticity.File;
using Vorticity.IO;
using Vorticity.Scanning;
using Vorticity.Serialization.Schemas;
using Vorticity.Tests.File;
using Xunit;

namespace Vorticity.Tests.Scan;

/// <summary>
/// That the degree of parallelism bounds what is actually in flight, set per scan or once for the
/// process.
/// </summary>
/// <remarks>
/// <para>
/// A ceiling nothing measures is a comment. The other tests of the degree check that a scan at a
/// degree returns the same rows as one at another; none of them observes concurrency, so a pump
/// that ignored the number entirely would pass every one of them.
/// </para>
/// <para>
/// What is counted is overlapping <c>ReadManyAsync</c> calls. Each lane issues exactly one per
/// batch and waits for it, so the number of reads in flight is the number of lanes at work, and
/// the source is the only place both are visible. The source holds each read open briefly so that
/// an overlap which exists has time to be seen; without that the lanes would have to be unlucky to
/// be caught together, and the test would pass by missing them.
/// </para>
/// </remarks>
[Collection(nameof(AllocationCollection))]
public sealed class DegreeOfParallelismTests
{
    private const string Multi = "distributions/high_cardinality_i64_r8193";
    private const int BatchRows = 500;

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    public async Task APerScanDegreeBoundsWhatIsInFlight(int degree)
    {
        (int peak, int batches) = await ScanAsync(builder => builder.WithDegreeOfParallelism(degree));

        Report($"per scan, degree {degree}", peak, batches);
        Assert.True(batches > degree, "the file must produce more batches than lanes to be a test");
        Assert.InRange(peak, 1, degree);
    }

    [Fact]
    public async Task TheProcessDefaultAppliesToAScanThatAsksForNothing()
    {
        Assert.Equal(1, ScanBuilder.DefaultDegreeOfParallelism);

        (int sequential, int batches) = await ScanAsync(builder => builder);
        Report("process default 1", sequential, batches);
        Assert.Equal(1, sequential);

        int peak;
        ScanBuilder.DefaultDegreeOfParallelism = 3;
        try
        {
            (peak, batches) = await ScanAsync(builder => builder);
        }
        finally
        {
            ScanBuilder.DefaultDegreeOfParallelism = 1;
        }

        Report("process default 3", peak, batches);
        Assert.InRange(peak, 1, 3);

        // The point of the setting: a scan that names no degree is not stuck at one.
        Assert.True(peak > 1, $"a default of 3 left the scan sequential, peak {peak}");
    }

    [Fact]
    public async Task APerScanDegreeOverridesTheProcessDefault()
    {
        int peak;
        ScanBuilder.DefaultDegreeOfParallelism = 4;
        try
        {
            (peak, int batches) = await ScanAsync(builder => builder.WithDegreeOfParallelism(1));
            Report("default 4, scan asks 1", peak, batches);
        }
        finally
        {
            ScanBuilder.DefaultDegreeOfParallelism = 1;
        }

        Assert.Equal(1, peak);
    }

    [Fact]
    public void TheProcessDefaultIsReadWhenTheBuilderIsMade()
    {
        try
        {
            ScanBuilder.DefaultDegreeOfParallelism = 2;
            Assert.Equal(2, ScanBuilder.DefaultDegreeOfParallelism);
        }
        finally
        {
            ScanBuilder.DefaultDegreeOfParallelism = 1;
        }

        Assert.Equal(1, ScanBuilder.DefaultDegreeOfParallelism);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void TheProcessDefaultRejectsANonPositiveValue(int degree) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => ScanBuilder.DefaultDegreeOfParallelism = degree);

    private static void Report(string what, int peak, int batches) =>
        Console.Out.Write(
            string.Create(
                CultureInfo.InvariantCulture,
                $"DEGREE: {what} peaked at {peak} concurrent reads over {batches} batches\n"));

    private static async Task<(int Peak, int Batches)> ScanAsync(
        Func<ScanBuilder, ScanBuilder> configure)
    {
        byte[] bytes = await System.IO.File.ReadAllBytesAsync(Corpus.Path(Multi));
        OverlapCountingSource source = new OverlapCountingSource(new TestSegmentSource(bytes));
        await using VortexFile file = await VortexFile.OpenAsync(
            source, VortexOpenOptions.Default, CancellationToken.None);

        int batches = 0;
        await foreach (RecordBatch batch in configure(file.ScanBuilder().WithMaxBatchRows(BatchRows))
                           .ExecuteAsync())
        {
            batches++;
        }

        return (source.Peak, batches);
    }

    /// <summary>Records the largest number of <c>ReadManyAsync</c> calls ever open at once.</summary>
    private sealed class OverlapCountingSource : ISegmentSource
    {
        private readonly ISegmentSource _inner;
        private int _inFlight;
        private int _peak;

        internal OverlapCountingSource(ISegmentSource inner) => _inner = inner;

        internal int Peak => Volatile.Read(ref _peak);

        public ValueTask<long> GetLengthAsync(CancellationToken cancellationToken) =>
            _inner.GetLengthAsync(cancellationToken);

        public ValueTask<SegmentOwner> ReadAsync(SegmentSpec spec, CancellationToken cancellationToken) =>
            _inner.ReadAsync(spec, cancellationToken);

        public ValueTask<SegmentOwner> ReadRangeAsync(
            long offset, int length, int alignment, CancellationToken cancellationToken) =>
            _inner.ReadRangeAsync(offset, length, alignment, cancellationToken);

        public async ValueTask ReadManyAsync(SegmentRequestSet requests, CancellationToken cancellationToken)
        {
            int now = Interlocked.Increment(ref _inFlight);
            RaisePeak(now);
            try
            {
                // Held open on purpose. Lanes that overlap for a microsecond overlap all the same,
                // but a counter sampled at one instant would rarely catch them; a millisecond makes
                // the overlap wide enough that the count is about the pump and not about luck.
                await Task.Delay(1, cancellationToken).ConfigureAwait(false);
                await _inner.ReadManyAsync(requests, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }

        public ValueTask DisposeAsync() => _inner.DisposeAsync();

        private void RaisePeak(int observed)
        {
            int seen = Volatile.Read(ref _peak);
            while (observed > seen)
            {
                int previous = Interlocked.CompareExchange(ref _peak, observed, seen);
                if (previous == seen)
                {
                    return;
                }

                seen = previous;
            }
        }
    }
}
