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
/// What each column's statistics over the blocks cost the writer and weigh (<c>--micro blocks</c>): the time-window design's
/// requests over some hours, in the order of a key by emitter (service, version, host, instant), cut in blocks on the
/// service as a dataset's job cuts them; each column's sheet, counts and statistics fed the file's batches read back
/// canonical, the statistics timed alone, the best of several rounds; then each column's statistics and where its values
/// live, their bytes a block, against the 15 ns a row and the 200 bytes a block the plan allows.
/// </summary>
/// <remarks>Run it with <c>DOTNET_TieredCompilation=0</c>.</remarks>
internal static class BlocksMicro
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

        // The blocks, as a job cuts them: at a change of service once a block holds its least rows, and full.
        List<long> blockRows = [];
        long filled = 0;
        for (int i = 0; i < rows; i++)
        {
            if (filled == BlockTable.MostRows || (filled >= BlockTable.LeastRows && requests[i].Service != requests[i - 1].Service))
            {
                blockRows.Add(filled);
                filled = 0;
            }

            filled++;
        }

        blockRows.Add(filled);
        byte[][] keys = [.. blockRows.Select(_ => Array.Empty<byte>())];
        BlockTable table = BlockTable.Of(blockRows, keys, keys);

        byte[] file = await WriteAsync(requests);
        await using MemorySegmentSource source = new MemorySegmentSource(file);
        await using VortexFile opened = await VortexFile.OpenAsync(source, new VortexOpenOptions());
        Vorticity.Types.DType schema = VortexTypes.ToDType(GeneratedRequest.Schema, new Vorticity.Types.DTypeArena());
        int columns = GeneratedRequest.Schema.Count;
        double[] best = new double[columns];
        double[] ends = new double[columns];
        Array.Fill(best, double.MaxValue);
        Array.Fill(ends, double.MaxValue);
        (byte[] Blocks, byte[]? Where)?[] built = new (byte[], byte[]?)?[columns];
        for (int round = 0; round < Math.Max(rounds, 2); round++)
        {
            CountsPlan counts = CountsPlan.For(schema)!;
            BlockCuts cuts = new BlockCuts();
            for (int b = 0, end = 0; b < table.Count - 1; b++)
            {
                end += (int)table.Rows[b];
                cuts.Add(end);
            }

            ColumnSheetBuilder[] sheets = new ColumnSheetBuilder[columns];
            ColumnCountsBuilder[] counters = new ColumnCountsBuilder[columns];
            ColumnBlocksBuilder[] builders = new ColumnBlocksBuilder[columns];
            for (int column = 0; column < columns; column++)
            {
                sheets[column] = new ColumnSheetBuilder();
                counters[column] = new ColumnCountsBuilder(sheets[column], counts, column);
                builders[column] = new ColumnBlocksBuilder(sheets[column], counts, column, cuts, schema.GetField(column));
            }

            long[] ticks = new long[columns];
            int[] nodes = new int[columns];
            await foreach (RecordBatch batch in opened.ScanBuilder().ExecuteAsync())
            {
                for (int column = 0; column < columns; column++)
                {
                    nodes[column] = batch.Arena.GetNode(batch.RootIndex).GetFieldIndex(column);
                }

                counts.Prepare(batch.Arena, nodes, batch.RowCount);
                for (int column = 0; column < columns; column++)
                {
                    sheets[column].Accumulate(batch.Arena, nodes[column], 0, batch.RowCount);
                    counters[column].Accumulate(batch.Arena, nodes[column], 0, batch.RowCount);
                    long started = Stopwatch.GetTimestamp();
                    builders[column].Accumulate(batch.Arena, nodes[column], 0, batch.RowCount);
                    ticks[column] += Stopwatch.GetTimestamp() - started;
                }
            }

            for (int column = 0; column < columns; column++)
            {
                counters[column].Settle();
            }

            for (int column = 0; column < columns; column++)
            {
                long started = Stopwatch.GetTimestamp();
                built[column] = builders[column].Finish(table);
                ends[column] = Math.Min(ends[column], (Stopwatch.GetTimestamp() - started) * 1e9 / Stopwatch.Frequency / rows);
                best[column] = Math.Min(best[column], ticks[column] * 1e9 / Stopwatch.Frequency / rows);
                builders[column].Dispose();
                counters[column].Dispose();
            }
        }

        double perRow = 0;
        long bytes = 0;
        long whereBytes = 0;
        List<string> lines = [];
        for (int column = 0; column < columns; column++)
        {
            perRow += best[column] + ends[column];
            long held = built[column]?.Blocks.Length ?? 0;
            long where = built[column]?.Where?.Length ?? 0;
            bytes += held;
            whereBytes += where;
            ColumnBlocks? parsed = built[column] is { } pair ? ColumnBlocks.From(pair.Blocks) : null;
            int listed = 0;
            for (int b = 0; parsed?.Distinct is { } distinct && b < parsed.Count; b++)
            {
                listed += distinct[b] <= ColumnBlocks.ListedValues ? 1 : 0;
            }

            lines.Add(string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"  {GeneratedRequest.Schema[column].Name,-12} {best[column],5:F1} ns a row, its end {ends[column],4:F1}; " +
                $"{(double)held / table.Count,6:F1} B a block{(parsed is null ? ", none" : parsed.HoldsValueLists ? $", values listed in {listed} blocks" : string.Empty)}" +
                $"{(parsed?.Measure is { } kind ? $", a measure of {kind}" : string.Empty)}; where {where / 1e3,7:F1} KB" +
                $"{(built[column]?.Where is { } placed && ColumnWhere.From(placed) is { } index ? $", {index.Values.Length:N0} values, {(double)where / index.Values.Length:F1} B a value" : string.Empty)}"));
        }

        Console.WriteLine(string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{rows:N0} requests over {hours} h by emitter, {table.Count:N0} blocks of {(double)rows / table.Count:N0} rows: " +
            $"{perRow:F1} ns a row for every column's statistics (the plan allows 15); {(double)bytes / table.Count:F1} B a block " +
            $"(the plan reckons 200), and {whereBytes / 1e3:F1} KB of where each value lives, {(double)whereBytes / table.Count:F1} B a block"));
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
