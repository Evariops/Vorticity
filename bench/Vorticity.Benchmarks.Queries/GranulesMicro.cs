using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Vorticity.Indexes;
using Vorticity.IO;
using Vorticity.Scanning;
using Vorticity.Writing;

namespace Vorticity.Benchmarks.Queries;

/// <summary>
/// What each column's statistics over the granules and its locate cost the writer and weigh (<c>--micro granules</c>): the
/// time-window design's requests over some hours, in the order of a key by emitter (service, version, host, instant), in
/// granules of the writer's block rows; each column's sheet, counts, statistics and locate fed the file's batches read back
/// canonical, the statistics and the locate timed alone, the best of several rounds; then each column's statistics and
/// where its values lie, their bytes a granule, against the 15 ns a row and the 200 bytes a granule the plan allows.
/// </summary>
/// <remarks>Run it with <c>DOTNET_TieredCompilation=0</c>.</remarks>
internal static class GranulesMicro
{
    private const int Batch = 65_536;

    internal static async Task<int> RunAsync(int rows, int hours, int rounds)
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
        byte[] file = await WriteAsync(requests);
        await using MemorySegmentSource source = new MemorySegmentSource(file);
        await using VortexFile opened = await VortexFile.OpenAsync(source, new VortexOpenOptions());
        Vorticity.Types.DType schema = VortexTypes.ToDType(GeneratedRequest.Schema, new Vorticity.Types.DTypeArena());
        int columns = GeneratedRequest.Schema.Count;
        double[] best = new double[columns];
        double[] ends = new double[columns];
        Array.Fill(best, double.MaxValue);
        Array.Fill(ends, double.MaxValue);
        (byte[] Statistics, byte[]? Locate)?[] built = new (byte[], byte[]?)?[columns];
        for (int round = 0; round < Math.Max(rounds, 2); round++)
        {
            CountsPlan counts = CountsPlan.For(schema)!;
            ColumnSheetBuilder[] sheets = new ColumnSheetBuilder[columns];
            ColumnCountsBuilder[] counters = new ColumnCountsBuilder[columns];
            GranuleStatsBuilder[] builders = new GranuleStatsBuilder[columns];
            LocateBuilder[] locates = new LocateBuilder[columns];
            for (int column = 0; column < columns; column++)
            {
                sheets[column] = new ColumnSheetBuilder();
                counters[column] = new ColumnCountsBuilder(sheets[column], counts, column);
                builders[column] = new GranuleStatsBuilder(sheets[column], counts, column, schema.GetField(column));
                locates[column] = new LocateBuilder(sheets[column]);
            }

            long[] ticks = new long[columns];
            int[] nodes = new int[columns];
            long row = 0;
            await foreach (RecordBatch batch in opened.ScanBuilder().ExecuteAsync())
            {
                for (int column = 0; column < columns; column++)
                {
                    nodes[column] = batch.Arena.GetNode(batch.RootIndex).GetFieldIndex(column);
                }

                counts.Prepare(batch.Arena, nodes, batch.RowCount);

                // The batch in pieces that end where a granule does, each granule closed as the writer closes its block.
                for (int from = 0; from < batch.RowCount;)
                {
                    int count = (int)Math.Min(batch.RowCount - from, granuleRows - (row % granuleRows));
                    bool closes = (row + count) % granuleRows == 0;
                    for (int column = 0; column < columns; column++)
                    {
                        sheets[column].Accumulate(batch.Arena, nodes[column], from, count);
                        counters[column].Accumulate(batch.Arena, nodes[column], from, count);
                        long started = Stopwatch.GetTimestamp();
                        builders[column].Accumulate(batch.Arena, nodes[column], from, count);
                        locates[column].Accumulate(batch.Arena, nodes[column], from, count);
                        if (closes)
                        {
                            builders[column].CloseBlock();
                            locates[column].CloseBlock();
                        }

                        ticks[column] += Stopwatch.GetTimestamp() - started;
                    }

                    from += count;
                    row += count;
                }
            }

            for (int column = 0; column < columns; column++)
            {
                counters[column].Settle();
            }

            for (int column = 0; column < columns; column++)
            {
                long started = Stopwatch.GetTimestamp();
                built[column] = builders[column].Finish(rows) is { } statistics
                    ? (statistics.Statistics, locates[column].Finish(rows, granuleRows))
                    : null;
                ends[column] = Math.Min(ends[column], (Stopwatch.GetTimestamp() - started) * 1e9 / Stopwatch.Frequency / rows);
                best[column] = Math.Min(best[column], ticks[column] * 1e9 / Stopwatch.Frequency / rows);
                builders[column].Dispose();
                locates[column].Dispose();
                counters[column].Dispose();
            }
        }

        double perRow = 0;
        long bytes = 0;
        long locateBytes = 0;
        List<string> lines = [];
        for (int column = 0; column < columns; column++)
        {
            perRow += best[column] + ends[column];
            long held = built[column]?.Statistics.Length ?? 0;
            long lying = built[column]?.Locate?.Length ?? 0;
            bytes += held;
            locateBytes += lying;
            GranuleStats? parsed = built[column] is { } pair ? GranuleStats.From(pair.Statistics) : null;
            int listed = 0;
            for (int g = 0; parsed?.Distinct is { } distinct && g < parsed.Count; g++)
            {
                listed += distinct[g] <= GranuleStats.ListedValues ? 1 : 0;
            }

            lines.Add(string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"  {GeneratedRequest.Schema[column].Name,-12} {best[column],5:F1} ns a row, its end {ends[column],4:F1}; " +
                $"{(double)held / granules,6:F1} B a granule{(parsed is null ? ", none" : parsed.HoldsValueLists ? $", values listed in {listed} granules" : string.Empty)}" +
                $"{(parsed?.Measure is { } kind ? $", a measure of {kind}" : string.Empty)}; locate {lying / 1e3,7:F1} KB" +
                $"{(built[column]?.Locate is { } placed && ColumnLocate.From(placed) is { } index ? $", {index.Values.Count:N0} values, {(double)lying / index.Values.Count:F1} B a value" : string.Empty)}"));
        }

        Console.WriteLine(string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{rows:N0} requests over {hours} h by emitter, {granules:N0} granules of {granuleRows:N0} rows: " +
            $"{perRow:F1} ns a row for every column's statistics and locate (the plan allows 15); {(double)bytes / granules:F1} B a granule " +
            $"(the plan reckons 200), and {locateBytes / 1e3:F1} KB of where each value lies, {(double)locateBytes / granules:F1} B a granule"));
        lines.ForEach(Console.WriteLine);
        return perRow <= 15 ? 0 : 1;
    }

    /// <summary>The rows written to memory, with no index.</summary>
    private static async Task<byte[]> WriteAsync(GeneratedRequest[] requests)
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

        return stream.ToArray();
    }
}
