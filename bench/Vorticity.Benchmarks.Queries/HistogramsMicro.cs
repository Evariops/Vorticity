using System;
using System.Buffers;
using System.IO;
using System.Threading.Tasks;
using Vorticity.Aggregating;
using Vorticity.IO;
using Vorticity.Writing;

namespace Vorticity.Benchmarks.Queries;

/// <summary>
/// A measure's histograms over the granules weighed at two grids (<c>--micro histograms</c>): the time-window design's
/// requests over some hours, in the order of a key by emitter (service, version, host, instant), in granules of the writer's
/// block rows; each granule's histogram of the requests' durations encoded at three bits of mantissa, the grid the writer
/// keeps (within 5.9 %), and at six, the grid a quantile asks by default (within 0.77 %): its bytes a granule and a row,
/// against the file's bytes a row. Then what a quantile over the last quarter of an hour of a day at the design's density
/// reads through each grid, against the rows of the durations it reads.
/// </summary>
internal static class HistogramsMicro
{
    private const int Batch = 65_536;

    // A day of the design's requests, and a quarter of an hour of it.
    private const long DayRows = 432_000_000;
    private const long QuarterRows = DayRows / 96;

    internal static async Task<int> RunAsync(int rows, int hours)
    {
        RequestGenerator generator = new RequestGenerator(17);
        DateTimeOffset start = new DateTimeOffset(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);
        long step = TimeSpan.FromHours(hours).Ticks / rows;
        GeneratedRequest[] requests = new GeneratedRequest[rows];
        for (int i = 0; i < rows; i++)
        {
            requests[i] = generator.Next(start.AddTicks(i * step));
        }

        Array.Sort(requests, static (left, right) =>
        {
            int order = string.CompareOrdinal(left.Service, right.Service);
            order = order != 0 ? order : string.CompareOrdinal(left.Version, right.Version);
            order = order != 0 ? order : string.CompareOrdinal(left.Host, right.Host);
            return order != 0 ? order : left.At.CompareTo(right.At);
        });

        int granuleRows = new VortexWriteOptions().BlockRows;
        int granules = (rows + granuleRows - 1) / granuleRows;
        long[] durations = Array.ConvertAll(requests, static request => request.DurationNs);
        long fileBytes = await FileBytesAsync(requests);
        Console.WriteLine($"{rows} requests over {hours} h, {granules} granules of {granuleRows} rows, the file {fileBytes / (double)rows:F1} bytes a row");
        Console.WriteLine($"{"grid",-26} {"B/granule",10} {"B/row",7} {"% of file",10} {"quarter, MB",12}");
        foreach (int bits in (int[])[3, 6])
        {
            QuantileSketch sketch = new QuantileSketch(bits);
            ArrayBufferWriter<byte> encoded = new ArrayBufferWriter<byte>(4_096);
            long bytes = 0;
            for (int granule = 0; granule < granules; granule++)
            {
                int first = granule * granuleRows;
                sketch.Clear();
                sketch.AddAll<long>(durations.AsSpan(first, Math.Min(granuleRows, rows - first)));
                encoded.Clear();
                sketch.Encode(encoded);
                bytes += encoded.WrittenCount + Varint(encoded.WrittenCount);
            }

            double perRow = bytes / (double)rows;
            Console.WriteLine($"{$"{bits} bits of mantissa",-26} {bytes / (double)granules,10:F0} {perRow,7:F3} {100 * perRow * rows / fileBytes,10:F2} {perRow * QuarterRows / 1e6,12:F2}");
        }

        double durationsPerRow = await DurationBytesAsync(requests) / (double)rows;
        Console.WriteLine($"{"the durations as rows",-26} {"",10} {durationsPerRow,7:F3} {"",10} {durationsPerRow * QuarterRows / 1e6,12:F2}");
        return 0;
    }

    /// <summary>The bytes of a varint holding <paramref name="value"/>.</summary>
    private static int Varint(int value) => value < 1 << 7 ? 1 : value < 1 << 14 ? 2 : value < 1 << 21 ? 3 : 4;

    /// <summary>The bytes the requests take written whole.</summary>
    private static async Task<long> FileBytesAsync(GeneratedRequest[] requests)
    {
        using MemoryStream stream = new MemoryStream();
        VortexWriteOptions options = new VortexWriteOptions { DegreeOfParallelism = 1, Identity = new Guid("b10cb10c-b10c-4b10-8b10-b10cb10cb10c") };
        await using (VortexFileWriter writer = VortexFileWriter.Create(new StreamSegmentSink(stream), VortexTypes.ToDType(GeneratedRequest.Schema, new Vorticity.Types.DTypeArena()), options))
        {
            for (int at = 0; at < requests.Length; at += Batch)
            {
                await writer.WriteAsync<GeneratedRequest>(requests.AsSpan(at, Math.Min(Batch, requests.Length - at)));
            }

            await writer.CompleteAsync();
        }

        return stream.Length;
    }

    /// <summary>The bytes the durations alone take written as a column.</summary>
    private static async Task<long> DurationBytesAsync(GeneratedRequest[] requests)
    {
        using MemoryStream stream = new MemoryStream();
        Vorticity.Types.DTypeArena types = new Vorticity.Types.DTypeArena();
        VortexWriteOptions options = new VortexWriteOptions { DegreeOfParallelism = 1, Identity = new Guid("b10cb10c-b10c-4b10-8b10-b10cb10cb10d") };
        await using (VortexFileWriter writer = VortexFileWriter.Create(new StreamSegmentSink(stream), VortexTypes.ToDType(Duration.Schema, types), options))
        {
            Duration[] column = Array.ConvertAll(requests, static request => new Duration(request.DurationNs));
            for (int at = 0; at < column.Length; at += Batch)
            {
                await writer.WriteAsync<Duration>(column.AsSpan(at, Math.Min(Batch, column.Length - at)));
            }

            await writer.CompleteAsync();
        }

        return stream.Length;
    }
}

/// <summary>A request's duration alone, a column of its own.</summary>
[VortexRecord]
public partial record struct Duration(long DurationNs);
