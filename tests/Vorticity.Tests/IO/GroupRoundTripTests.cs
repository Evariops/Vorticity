// What a group by asks of an object store, counted rather than timed: its requests, which the store
// bills, and its dependent steps, the requests that found none in flight, each a round trip the
// query waited on before it could ask for more. On a store at 50 ms a round trip, a query's steps
// times 50 ms is most of its time; on a local file both are invisible to a timer.
//
// RATCHETS, like RoundTripCountTests, set at the counts: a step of PLAN-HIGH-CARDINALITY.md that
// adds a request or a step says which, and why, in its commit.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Buffers;
using Vorticity.IO;
using Vorticity.Serialization.Schemas;
using Xunit;

namespace Vorticity.Tests.IO;

/// <summary>Counts the requests and the dependent steps of group-by queries over a file read through a counting source.</summary>
public sealed partial class GroupRoundTripTests
{
    private const int Rows = 200_000;

    /// <summary>A query, its degree, and the requests and steps it may cost; steps are held at one lane alone.</summary>
    /// <remarks>
    /// Measured on 2026-10-06 (PLAN-HIGH-CARDINALITY.md, H0b): 25 chunks read in 22 requests, the
    /// reader joining neighbours, at any degree. The steps are not exact: the scan reads ahead, and
    /// whether a read joins the step of the one before or opens the next depends on which timer of
    /// the source fires first. Measured 8 to 11 at one lane, at 20 ms a request as at 50; the
    /// ceiling is the highest, and a regression that waits on the store a batch more than now adds
    /// some twenty. At four lanes, 5 or 6, which the lanes' interleaving decides, so not held.
    /// </remarks>
    private static readonly (string Query, int Degree, int Requests, int Steps)[] Ceilings =
    [
        ("group by a key of 10^4 values, count sum", 1, 22, 11),
        ("group by a key of 10^4 values, count sum", 4, 22, 0),
        ("order by count take 10", 1, 22, 11),
        ("count distinct over the scan", 1, 22, 11),
    ];

    [Fact]
    public async Task AGroupByStaysWithinItsRoundTrips()
    {
        string path = await WriteAsync();
        try
        {
            List<string> over = [];
            System.Text.StringBuilder report = new System.Text.StringBuilder("GROUP BY ROUND TRIPS\n");
            foreach ((string query, int degree, int requestCeiling, int stepCeiling) in Ceilings)
            {
                (long requests, long steps) = await MeasureAsync(path, query, degree);
                report.Append(CultureInfo.InvariantCulture, $"    {query,-44} degree {degree}: {requests} requests (ceiling {requestCeiling}), {steps} steps (ceiling {stepCeiling})\n");
                if (requests > requestCeiling)
                {
                    over.Add($"{query} at degree {degree}: {requests} requests, ceiling {requestCeiling}");
                }

                if (degree == 1 && steps > stepCeiling)
                {
                    over.Add($"{query} at degree {degree}: {steps} dependent steps, ceiling {stepCeiling}");
                }
            }

            Console.Out.Write(report.ToString());
            Assert.True(over.Count == 0, string.Join("\n", over) + "\n" + report);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    // An aggregation's lane decodes on its own flow, nothing ahead, and reads ahead of its decode over
    // a source whose read is a round trip (PLAN-HIGH-CARDINALITY, R6): two splits in flight, so that
    // its reads pair up into half as many steps as a read at a time takes, for the same rows and the
    // same requests.
    [Fact]
    public async Task ALaneReadsAheadOfItsDecode()
    {
        string path = await WriteAsync();
        try
        {
            (long rows, long requests, long steps) alone = await LaneAsync(path, readAhead: false);
            (long rows, long requests, long steps) ahead = await LaneAsync(path, readAhead: true);
            Assert.Equal(Rows, alone.rows);
            Assert.Equal(Rows, ahead.rows);
            Assert.Equal(alone.requests, ahead.requests);
            Assert.True(alone.steps >= alone.requests - 1, $"{alone.steps} steps for {alone.requests} requests, a read at a time");
            Assert.InRange(ahead.steps, 1, (ahead.requests / 2) + 2);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    /// <summary>The rows, requests and steps of a scan as an aggregation's lane reads it: one lane, no prefetch.</summary>
    private static async Task<(long Rows, long Requests, long Steps)> LaneAsync(string path, bool readAhead)
    {
        CountingSource source = new CountingSource(MemoryMappedSegmentSource.Open(path));
        long rows = 0;
        await using (VortexFile file = await VortexFile.OpenAsync(source, new VortexOpenOptions { LeaveSourceOpen = true }, Ct))
        {
            source.Reset();
            ScanSpec spec = new ScanSpec
            {
                Options = new ScanOptions { DegreeOfParallelism = 1, Prefetch = 0 },
                ReadAhead = readAhead,
            };
            await foreach (RecordBatch batch in new FileScanSource(file).BatchesAsync(spec, new Vorticity.Scanning.ScanMetrics()).WithCancellation(Ct))
            {
                rows += batch.RowCount;
            }
        }

        await source.DisposeAsync();
        return (rows, source.Requests, source.Steps);
    }

    private static async Task<(long Requests, long Steps)> MeasureAsync(string path, string query, int degree)
    {
        CountingSource source = new CountingSource(MemoryMappedSegmentSource.Open(path));
        await using (VortexFile file = await VortexFile.OpenAsync(source, new VortexOpenOptions { LeaveSourceOpen = true }, Ct))
        {
            source.Reset();
            Scan<Row> scan = file.Scan<Row>().With(new ScanOptions { DegreeOfParallelism = degree });
            long total = query switch
            {
                "order by count take 10" => await CountAsync(scan.GroupBy(r => r.Key).OrderByDescending(g => g.Count()).Take(10).Select(g => (g.Key, g.Count(), g.Sum(r => r.Value))).As<KeyTotal>()),
                "count distinct over the scan" => await scan.CountDistinctAsync(r => r.Key, Ct),
                _ => await CountAsync(scan.GroupBy(r => r.Key).Select(g => (g.Key, g.Count(), g.Sum(r => r.Value))).As<KeyTotal>()),
            };
            Assert.True(total > 0);
        }

        await source.DisposeAsync();
        return (source.Requests, source.Steps);
    }

    private static async Task<long> CountAsync(Scan<KeyTotal> groups)
    {
        long count = 0;
        await foreach (KeyTotal group in groups.ToRecordsAsync(Ct))
        {
            count += group.Count;
        }

        return count;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A key of ten thousand values in no order and a value, in 25 chunks of a block.</summary>
    private static async Task<string> WriteAsync()
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "group-round-trips");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"rows-{Environment.ProcessId}-{Guid.NewGuid():N}.vortex");
        Row[] rows = new Row[Rows];
        for (int row = 0; row < Rows; row++)
        {
            ulong mix = (ulong)row * 0x9E37_79B9_7F4A_7C15UL;
            rows[row] = new Row((int)((mix >> 24) % 10_000), (long)((mix >> 8) % 100));
        }

        await using (VortexFileWriter writer = VortexSession.Default.CreateWriter<Row>(path, new VortexWriteOptions { RowBlockSize = 8_192, ChunkTargetBytes = 1 }))
        {
            await writer.WriteAsync<Row>(rows, Ct);
            await writer.CompleteAsync(Ct);
        }

        return path;
    }

    /// <summary>
    /// Counts the requests a reader asks of the source below, a set of ranges as one, and the steps:
    /// the requests that found none in flight. Each request waits 20 ms before it is served, so a
    /// request asked while another is still out is one of its step, and one asked after is the next.
    /// </summary>
    private sealed class CountingSource(ISegmentReader inner) : ISegmentReader
    {
        private int _inFlight;
        private long _requests;
        private long _steps;

        internal long Requests => Interlocked.Read(ref _requests);

        internal long Steps => Interlocked.Read(ref _steps);

        internal void Reset()
        {
            Interlocked.Exchange(ref _requests, 0);
            Interlocked.Exchange(ref _steps, 0);
        }

        public ValueTask<long> GetLengthAsync(CancellationToken cancellationToken) => inner.GetLengthAsync(cancellationToken);

        public async ValueTask<SegmentOwner> ReadAsync(SegmentSpec spec, CancellationToken cancellationToken)
        {
            await BeginAsync(cancellationToken);
            try
            {
                return await inner.ReadAsync(spec, cancellationToken);
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }

        public async ValueTask ReadManyAsync(SegmentRequestSet requests, CancellationToken cancellationToken)
        {
            await BeginAsync(cancellationToken);
            try
            {
                await inner.ReadManyAsync(requests, cancellationToken);
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }

        public async ValueTask<SegmentOwner> ReadRangeAsync(long offset, int length, int alignment, CancellationToken cancellationToken)
        {
            await BeginAsync(cancellationToken);
            try
            {
                return await inner.ReadRangeAsync(offset, length, alignment, cancellationToken);
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }

        public ValueTask DisposeAsync() => inner.DisposeAsync();

        private async Task BeginAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requests);
            if (Interlocked.Increment(ref _inFlight) == 1)
            {
                Interlocked.Increment(ref _steps);
            }

            await Task.Delay(20, cancellationToken);
        }
    }

    [VortexRecord]
    public partial record struct Row(int Key, long Value);

    [VortexRecord]
    public partial record struct KeyTotal(int Key, long Count, long Total);
}
