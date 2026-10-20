using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.Indexes;
using Vorticity.IO;
using Vorticity.Scanning;
using Vorticity.Types;

namespace Vorticity.Benchmarks.Queries;

/// <summary>
/// What sorted slices weigh against the same rows in time order (<c>--micro slicebytes</c>): a second
/// of the requests of the time-window design per 50 000 rows, a million rows by default
/// (<c>--rows</c>), cut in slices as a dataset of periods cuts them, each slice sorted by the synopsis
/// columns the rows' distinct counts pick, then by instant. Both files are written under the default
/// profile and the size-first one, and each column's bytes are read from the file's chunk directory.
/// </summary>
internal static class SliceBytesMicro
{
    private static readonly string[] Methods = ["GET", "GET", "GET", "GET", "POST", "POST", "PUT", "DELETE", "PATCH", "HEAD", "OPTIONS", "TRACE"];

    private static readonly short[] Statuses = [200, 200, 200, 200, 200, 200, 200, 200, 201, 204, 304, 400, 404, 404, 500, 503];

    internal static async Task<int> Run(int rows)
    {
        SliceRequest[] inTime = Generate(rows);
        string scratch = Path.Combine(Path.GetTempPath(), $"vortex-slicebytes-{Guid.NewGuid():N}");
        Directory.CreateDirectory(scratch);
        try
        {
            // The rows as batches, once, for the counts and the sort.
            string plain = Path.Combine(scratch, "plain.vortex");
            await using (VortexFileWriter writer = VortexSession.Default.CreateWriter<SliceRequest>(plain))
            {
                await writer.WriteAsync<SliceRequest>(inTime);
                await writer.CompleteAsync();
            }

            await using VortexFile file = await VortexFile.OpenAsync(plain);
            List<RecordBatch> batches = [];
            await foreach (RecordBatch batch in file.Scan<SliceRequest>().ToBatchesAsync())
            {
                batches.Add(batch);
            }

            long[] instants = [.. inTime.Select(r => (r.At.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) / 10)];
            List<TimeSlice> slices = TimeSlicer.Cut(instants, TimeUnit.Microseconds);
            ClusteringKey key = ClusteringKey.For([nameof(SliceRequest.At)], file.DType)!;
            int[] candidates = SynopsisOrder.Candidates(file.DType, key, [nameof(SliceRequest.RequestId)]);
            CardinalitySketchSet sketches = new CardinalitySketchSet(candidates.Length);
            foreach (RecordBatch batch in batches)
            {
                SynopsisOrder.Count(batch, candidates, sketches.Sketches);
            }

            int[] order = SynopsisOrder.Choose([.. candidates.Select((field, i) => (field, sketches.Sketches[i].Estimate()))], rows);
            Console.WriteLine(
                $"{rows} rows, {slices.Count} slice(s), sorted by {string.Join(", ", order.Select(field => file.DType.GetFieldName(field)))}, then {nameof(SliceRequest.At)}");

            int instant = file.DType.IndexOfField(nameof(SliceRequest.At));
            foreach (CompressionProfile profile in new[] { CompressionProfile.Auto, CompressionProfile.Smallest })
            {
                VortexWriteOptions options = new VortexWriteOptions { Compression = profile, Indexes = IndexPolicy.None, CarriesSlices = true };
                long[] timeColumns = await WriteAsync(scratch, file.DType, batches, new SliceTable(TimeUnit.Microseconds, slices), instant, options, sorted: false);
                long[] slicedColumns = await WriteAsync(scratch, file.DType, batches, new SliceTable(TimeUnit.Microseconds, slices, order), instant, options, sorted: true);

                // What an object of a dataset of levels weighs: its rows in time order, with the run on the key.
                long[] keyed = await WriteAsync(scratch, file.DType, batches, new SliceTable(TimeUnit.Microseconds, slices), instant, key.Applied(options), sorted: false);
                Console.WriteLine($"{profile}: {"column",-11} {"time order",10} {"sorted",10}  bytes a row");
                for (int field = 0; field < file.DType.FieldCount; field++)
                {
                    Console.WriteLine(
                        $"  {file.DType.GetFieldName(field),-20} {(double)timeColumns[field] / rows,10:F3} {(double)slicedColumns[field] / rows,10:F3}");
                }

                Console.WriteLine($"  {"file",-20} {(double)timeColumns[^1] / rows,10:F3} {(double)slicedColumns[^1] / rows,10:F3}");
                Console.WriteLine($"  {"file, run on the key",-20} {(double)keyed[^1] / rows,10:F3}");

                // The same sorted slices with their synopsis: the synopsis columns in the part, the tables
                // the choice keeps under the default budget, every verdict.
                (long synopsisFile, WriteReport report) = await WriteWithSynopsisAsync(
                    scratch, file.DType, batches, new SliceTable(TimeUnit.Microseconds, slices, order), instant, options);
                long tableBytes = 0;
                foreach (IndexWriteReport table in report.Indexes.Where(r => r.Kind == IndexKinds.SynopsisTable))
                {
                    tableBytes += table.Bytes;
                    Console.WriteLine($"  {table.Column,-40} {table.Reason}");
                }

                Console.WriteLine(
                    $"  {"file with synopsis",-20} {(double)synopsisFile / rows,10:F3} bytes a row: the tables {tableBytes} bytes, {(double)tableBytes / rows:F3} a row, " +
                    $"{100.0 * tableBytes / report.Bytes.Data:F2} % of the data's {report.Bytes.Data}");
            }

            foreach (RecordBatch batch in batches)
            {
                batch.Dispose();
            }
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
        }

        return 0;
    }

    /// <summary>The rows written in time order or in sorted slices: each column's bytes, then the file's.</summary>
    private static async Task<long[]> WriteAsync(
        string scratch, DType schema, List<RecordBatch> batches, SliceTable table, int instant, VortexWriteOptions options, bool sorted)
    {
        string path = Path.Combine(scratch, $"{Guid.NewGuid():N}.vortex");
        await using (VortexFileWriter writer = VortexSession.Default.CreateWriter<SliceRequest>(path, options))
        {
            if (sorted)
            {
                using SortedSlices slices = new SortedSlices(table, instant, schema);
                foreach (RecordBatch batch in batches)
                {
                    await slices.AppendAsync(batch, (rows, token) => writer.WriteBatchAsync(rows, token), default);
                }
            }
            else
            {
                foreach (RecordBatch batch in batches)
                {
                    await writer.WriteBatchAsync(batch);
                }
            }

            writer.DeclareSlices(nameof(SliceRequest.At), table);
            await writer.CompleteAsync();
        }

        byte[] bytes = await System.IO.File.ReadAllBytesAsync(path);
        SliceReader reader = (await SliceReader.OpenAsync(new MemorySegmentSource(bytes), bytes.Length, schema, null, default))!;
        long[] columns = new long[schema.FieldCount + 1];
        for (int field = 0; field < schema.FieldCount; field++)
        {
            columns[field] = await reader.ColumnBytesAsync(field, default);
        }

        columns[^1] = bytes.Length;
        return columns;
    }

    /// <summary>
    /// The rows written in sorted slices with their synopsis, as a dataset's job writes them: the synopsis
    /// columns in the part, the tables counted from each slice as it is sorted, then chosen. The file's
    /// bytes, and the report that holds each table's verdict.
    /// </summary>
    private static async Task<(long Bytes, WriteReport Report)> WriteWithSynopsisAsync(
        string scratch, DType schema, List<RecordBatch> batches, SliceTable table, int instant, VortexWriteOptions options)
    {
        string path = Path.Combine(scratch, $"{Guid.NewGuid():N}.vortex");
        WriteReport report;
        VortexWriteOptions synopsis = options with { SynopsisColumns = [.. table.Order.Where(e => e >= 0).Select(schema.GetFieldName)] };
        await using (VortexFileWriter writer = VortexSession.Default.CreateWriter<SliceRequest>(path, synopsis))
        {
            using SortedSlices slices = new SortedSlices(table, instant, schema);
            using SynopsisTables tables = new SynopsisTables(
                schema, table.Unit, SynopsisTables.Measures(schema, instant, table.Order), writer, slices: table.Slices);
            int next = 0;
            foreach (RecordBatch batch in batches)
            {
                await slices.AppendAsync(batch, async (rows, token) =>
                {
                    await tables.AddSliceAsync(rows, table.Slices[next++].Start, slices.Order, slices.SortedCodes, cancellationToken: token);
                    await writer.WriteBatchAsync(rows, token);
                }, default);
            }

            writer.DeclareSlices(nameof(SliceRequest.At), slices.Table);
            writer.DeclareSynopsisTables(await tables.FinishAsync());
            report = await writer.CompleteAsync();
        }

        return (new FileInfo(path).Length, report);
    }

    /// <summary>
    /// Requests at 50 000 a second: 600 services of 2 to 3 versions and about 17 endpoints each, every
    /// endpoint served by about 17 of 10 000 hosts, five regions, skewed methods and statuses, a unique
    /// request id, a log-normal duration.
    /// </summary>
    private static SliceRequest[] Generate(int rows)
    {
        Random random = new Random(17);
        DateTimeOffset start = new DateTimeOffset(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);
        SliceRequest[] requests = new SliceRequest[rows];
        long ticks = 0;
        for (int i = 0; i < rows; i++)
        {
            // Arrivals at random, 20 µs apart on average, to the microsecond.
            ticks += (long)(-Math.Log(1 - random.NextDouble()) * 20) * 10;
            int service = random.Next(600);
            int endpoint = (service * 17) + random.Next(17);
            requests[i] = new SliceRequest(
                start.AddTicks(ticks),
                $"service-{service}",
                $"/api/v1/endpoint-{endpoint}",
                Methods[random.Next(Methods.Length)],
                Statuses[random.Next(Statuses.Length)],
                $"region-{random.Next(5)}",
                $"2026.{service}.{random.Next(2 + (service % 2))}",
                $"host-{((endpoint * 17) + random.Next(17)) % 10_000}",
                Guid.NewGuid().ToString("N"),
                (long)Math.Exp(13 + (random.NextDouble() * 4)),
                random.NextDouble());
        }

        return requests;
    }

    /// <summary>A sketch a candidate column.</summary>
    private sealed class CardinalitySketchSet(int count)
    {
        internal Vorticity.Aggregating.CardinalitySketch[] Sketches { get; } =
            [.. Enumerable.Range(0, count).Select(_ => new Vorticity.Aggregating.CardinalitySketch())];
    }
}

[VortexRecord]
public partial record struct SliceRequest(
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
