// What sorted slices weigh: requests of the time-window design, written in time order and in sorted
// slices, under the default profile and the size-first one. A ratchet on the bytes of the sorted file,
// and on what the instant costs once it no longer ascends through the file.
//
// THIS IS A RATCHET, NOT A GATE. The ceilings sit just above the measured bytes: a change that makes
// sorted slices heavier turns red, one that makes them lighter passes, and the ceiling then comes down
// by hand. 60 000 rows are a slice of a little over a second, where a minute of the design holds three
// million: the gain grows with the rows a combination of the synopsis columns holds
// (`vortex-queries --micro slicebytes`, a million rows: 40.35 bytes a row against 42.03 under the
// default profile, 36.94 against 38.36 under the size-first one, and the instant 0.75 and 2.30 bytes a
// row dearer).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Aggregating;
using Vorticity.Dataset;
using Vorticity.Indexes;
using Vorticity.IO;
using Vorticity.Types;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed partial class SortedSliceBytesTests
{
    private const int Rows = 60_000;

    private static readonly string[] Methods = ["GET", "GET", "GET", "GET", "POST", "POST", "PUT", "DELETE", "PATCH", "HEAD", "OPTIONS", "TRACE"];

    private static readonly short[] Statuses = [200, 200, 200, 200, 200, 200, 200, 200, 201, 204, 304, 400, 404, 404, 500, 503];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// The bytes a row of the sorted file, at most, under each profile; and under each, the bytes a
    /// row the sorted instant costs over the instant in time order, at most. Measured on 2026-10-19:
    /// 41.659 and 38.336 bytes a row against 42.476 and 38.505 in time order, the instant 0.275 and
    /// 1.792 bytes a row dearer.
    /// </summary>
    [Theory]
    [InlineData(CompressionProfile.Auto, 41.70, 0.30)]
    [InlineData(CompressionProfile.Smallest, 38.38, 1.82)]
    public async Task ASortedSliceWeighsAtMostItsCeiling(CompressionProfile profile, double ceiling, double instantCeiling)
    {
        Request[] rows = Generate();
        (DType schema, List<RecordBatch> batches) = await BatchesAsync(rows);
        try
        {
            long[] instants = [.. rows.Select(r => (r.At.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) / 10)];
            List<TimeSlice> slices = TimeSlicer.Cut(instants, TimeUnit.Microseconds);
            ClusteringKey key = ClusteringKey.For([nameof(Request.At)], schema)!;
            int[] candidates = SynopsisOrder.Candidates(schema, key, [nameof(Request.RequestId)]);
            CardinalitySketch[] sketches = [.. candidates.Select(_ => new CardinalitySketch())];
            foreach (RecordBatch batch in batches)
            {
                SynopsisOrder.Count(batch, candidates, sketches);
            }

            int[] order = SynopsisOrder.Choose([.. candidates.Select((field, i) => (field, sketches[i].Estimate()))], Rows);
            Assert.Equal(["Region", "Method", "Status", "Service", "Version", "Host", "Endpoint"], order.Select(field => schema.GetFieldName(field)));

            int instant = schema.IndexOfField(nameof(Request.At));
            VortexWriteOptions options = new VortexWriteOptions { Compression = profile, Indexes = IndexPolicy.None, CarriesSlices = true };
            (long timeFile, long timeInstant) = await WriteAsync(schema, batches, new SliceTable(TimeUnit.Microseconds, slices), instant, options, sorted: false);
            (long sortedFile, long sortedInstant) = await WriteAsync(schema, batches, new SliceTable(TimeUnit.Microseconds, slices, order), instant, options, sorted: true);

            double perRow = (double)sortedFile / Rows;
            double instantCost = (double)(sortedInstant - timeInstant) / Rows;
            string measured = $"sorted {perRow:F3} bytes a row against {(double)timeFile / Rows:F3} in time order, the instant {instantCost:F3} dearer";
            Assert.True(perRow <= ceiling, measured);
            Assert.True(sortedFile < timeFile, measured);
            Assert.True(instantCost <= instantCeiling, measured);
        }
        finally
        {
            foreach (RecordBatch batch in batches)
            {
                batch.Dispose();
            }
        }
    }

    /// <summary>The rows written in time order or in sorted slices: the file's bytes, and the instant's.</summary>
    private static async Task<(long File, long Instant)> WriteAsync(
        DType schema, List<RecordBatch> batches, SliceTable table, int instant, VortexWriteOptions options, bool sorted)
    {
        string path = Path.Combine(Path.GetTempPath(), $"vorticity-slice-bytes-{Guid.NewGuid():N}.vortex");
        try
        {
            await using (VortexFileWriter writer = VortexSession.Default.CreateWriter<Request>(path, options))
            {
                if (sorted)
                {
                    using SortedSlices slices = new SortedSlices(table, instant, schema);
                    foreach (RecordBatch batch in batches)
                    {
                        await slices.AppendAsync(batch, (rows, token) => writer.WriteBatchAsync(rows.Batch, token), Ct);
                    }

                    await slices.DrainAsync();
                }
                else
                {
                    foreach (RecordBatch batch in batches)
                    {
                        await writer.WriteBatchAsync(batch, Ct);
                    }
                }

                writer.DeclareSlices(nameof(Request.At), table);
                await writer.CompleteAsync(Ct);
            }

            byte[] bytes = await System.IO.File.ReadAllBytesAsync(path, Ct);
            SliceReader reader = (await SliceReader.OpenAsync(new MemorySegmentSource(bytes), bytes.Length, schema, null, Ct))!;
            return (bytes.Length, await reader.ColumnBytesAsync(instant, Ct));
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    /// <summary>The rows as the batches a scan of them gives, and their schema.</summary>
    private static async Task<(DType Schema, List<RecordBatch> Batches)> BatchesAsync(Request[] rows)
    {
        string path = Path.Combine(Path.GetTempPath(), $"vorticity-slice-rows-{Guid.NewGuid():N}.vortex");
        try
        {
            await using (VortexFileWriter writer = VortexSession.Default.CreateWriter<Request>(path))
            {
                await writer.WriteAsync<Request>(rows, Ct);
                await writer.CompleteAsync(Ct);
            }

            await using VortexFile file = await VortexFile.OpenAsync(path, Ct);
            List<RecordBatch> batches = [];
            await foreach (RecordBatch batch in file.Scan<Request>().ToBatchesAsync(Ct))
            {
                batches.Add(batch);
            }

            return (file.DType, batches);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    /// <summary>
    /// Requests at random, 20 µs apart on average: 600 services of 2 to 3 versions and about 17
    /// endpoints each, every endpoint served by about 17 of 10 000 hosts, five regions, skewed methods
    /// and statuses, a unique request id, a log-normal duration.
    /// </summary>
    private static Request[] Generate()
    {
        Random random = new Random(17);
        DateTimeOffset start = new DateTimeOffset(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);
        Request[] requests = new Request[Rows];
        Span<byte> id = stackalloc byte[16];
        long ticks = 0;
        for (int i = 0; i < Rows; i++)
        {
            ticks += (long)(-Math.Log(1 - random.NextDouble()) * 20) * 10;
            int service = random.Next(600);
            int endpoint = (service * 17) + random.Next(17);
            random.NextBytes(id);
            requests[i] = new Request(
                start.AddTicks(ticks),
                $"service-{service}",
                $"/api/v1/endpoint-{endpoint}",
                Methods[random.Next(Methods.Length)],
                Statuses[random.Next(Statuses.Length)],
                $"region-{random.Next(5)}",
                $"2026.{service}.{random.Next(2 + (service % 2))}",
                $"host-{((endpoint * 17) + random.Next(17)) % 10_000}",
                Convert.ToHexStringLower(id),
                (long)Math.Exp(13 + (random.NextDouble() * 4)),
                random.NextDouble());
        }

        return requests;
    }

    [VortexRecord]
    public partial record struct Request(
        DateTimeOffset At,
        string Service,
        string Endpoint,
        string Method,
        short Status,
        string Region,
        string Version,
        string Host,
        string RequestId,
        long DurationNs,
        double Weight);
}
