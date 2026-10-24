using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Vorticity.File;
using Vorticity.Indexes;
using Vorticity.IO;
using Vorticity.Scanning;
using Vorticity.Writing;

namespace Vorticity.Benchmarks.Queries;

/// <summary>
/// What a column's counts by hour cost the writer and weigh (<c>--micro counts</c>): the time-window design's requests
/// over some hours, eleven columns, written on one thread to memory with a sheet of each column and with the counts
/// besides, the best of several rounds of each, interleaved; the difference a counted column a row, against the 10 ns
/// the plan allows, and each column's counts: its values, its entries, its bytes and its sketches' bytes.
/// </summary>
/// <remarks>Run it with <c>DOTNET_TieredCompilation=0</c>.</remarks>
internal static class CountsMicro
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

        VortexWriteOptions sheets = new VortexWriteOptions
        {
            DegreeOfParallelism = 1,
            Identity = new Guid("51515151-5151-4151-8151-515151515151"),
            ColumnSheetBytes = 0,
        };
        VortexWriteOptions counted = sheets with { CountsInstant = nameof(GeneratedRequest.At) };
        double without = double.MaxValue;
        double with = double.MaxValue;
        byte[] file = [];
        for (int round = 0; round < Math.Max(rounds, 2); round++)
        {
            (double seconds, _) = await WriteAsync(requests, sheets);
            without = Math.Min(without, seconds);
            (seconds, file) = await WriteAsync(requests, counted);
            with = Math.Min(with, seconds);
        }

        // Each column's counts, read back from the file's index directory, and the bytes of their sketches.
        await using MemorySegmentSource source = new MemorySegmentSource(file);
        await using VortexFile opened = await VortexFile.OpenAsync(source, new VortexOpenOptions());
        IndexDirectory directory = (await opened.ReadIndexDirectoryAsync())!;
        long bytes = 0;
        long entries = 0;
        long sketches = 0;
        Dictionary<uint, long> sketched = directory.Entries.Where(e => e.Kind == IndexKinds.ColumnSketches)
            .ToDictionary(e => e.ColumnPath[0], e => (long)e.Runs[0].Payload[0].Length);
        List<string> lines = [];
        foreach (IndexEntry entry in directory.Entries.Where(e => e.Kind == IndexKinds.ColumnCounts))
        {
            IndexSegment region = entry.Runs[0].Payload[0];
            ColumnCounts counts = ColumnCounts.From(file.AsSpan((int)region.Offset, (int)region.Length));
            long sketchBytes = sketched.GetValueOrDefault(entry.ColumnPath[0]);
            bytes += region.Length;
            entries += counts.Entries;
            sketches += sketchBytes;
            lines.Add(string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"  {GeneratedRequest.Schema[(int)entry.ColumnPath[0]].Name,-12} {counts.Groups.Length,7:N0} values {counts.Entries,8:N0} entries " +
                $"{region.Length / 1e3,9:F1} KB, {(double)region.Length / Math.Max(counts.Entries, 1),5:F1} B an entry, sketches {sketchBytes / 1e3,9:F1} KB, " +
                $"measures {string.Join(", ", counts.MeasureFields.Select(f => GeneratedRequest.Schema[f].Name))}"));
        }

        int columns = lines.Count;
        double perColumnRow = (with - without) * 1e9 / ((double)rows * Math.Max(columns, 1));
        Console.WriteLine(string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{rows:N0} requests over {hours} h, {GeneratedRequest.Schema.Count} columns, one thread: {without * 1e3:F0} ms with sheets, " +
            $"{with * 1e3:F0} ms with counts besides, {columns} columns counted, {perColumnRow:F1} ns a counted column a row " +
            $"(the plan allows 10); {entries:N0} entries in {bytes / 1e6:F2} MB, {(double)bytes / Math.Max(entries, 1):F1} B an entry, " +
            $"and {sketches / 1e6:F2} MB of sketches"));
        lines.ForEach(Console.WriteLine);
        await PerColumnAsync(opened, rows, rounds);
        return perColumnRow <= 10 ? 0 : 1;
    }

    /// <summary>
    /// What the plan and each column's counts cost apart, fed the file's batches read back canonical, the best of the
    /// rounds: the plan's reading of each batch's instants and measures, each column's counts, and their ends.
    /// </summary>
    private static async Task PerColumnAsync(VortexFile file, int rows, int rounds)
    {
        Vorticity.Types.DType schema = VortexTypes.ToDType(GeneratedRequest.Schema, new Vorticity.Types.DTypeArena());
        int columns = GeneratedRequest.Schema.Count;
        int instant = 0;
        double plan = double.MaxValue;
        double[] best = new double[columns];
        double[] ends = new double[columns];
        Array.Fill(best, double.MaxValue);
        Array.Fill(ends, double.MaxValue);
        for (int round = 0; round < Math.Max(rounds, 2); round++)
        {
            CountsPlan counts = CountsPlan.For(schema, nameof(GeneratedRequest.At))!;
            ColumnSheetBuilder[] sheets = new ColumnSheetBuilder[columns];
            ColumnCountsBuilder?[] builders = new ColumnCountsBuilder?[columns];
            for (int column = 0; column < columns; column++)
            {
                sheets[column] = new ColumnSheetBuilder();
                builders[column] = column == instant ? null : new ColumnCountsBuilder(sheets[column], counts, column);
            }

            long planTicks = 0;
            long[] ticks = new long[columns];
            int[] nodes = new int[columns];
            await foreach (RecordBatch batch in file.ScanBuilder().ExecuteAsync())
            {
                for (int column = 0; column < columns; column++)
                {
                    nodes[column] = batch.Arena.GetNode(batch.RootIndex).GetFieldIndex(column);
                }

                long started = Stopwatch.GetTimestamp();
                counts.Prepare(batch.Arena, nodes, batch.RowCount);
                planTicks += Stopwatch.GetTimestamp() - started;
                for (int column = 0; column < columns; column++)
                {
                    sheets[column].Accumulate(batch.Arena, nodes[column], 0, batch.RowCount);
                    if (builders[column] is { } builder)
                    {
                        started = Stopwatch.GetTimestamp();
                        builder.Accumulate(batch.Arena, nodes[column], 0, batch.RowCount);
                        ticks[column] += Stopwatch.GetTimestamp() - started;
                    }
                }
            }

            plan = Math.Min(plan, planTicks * 1e9 / Stopwatch.Frequency / rows);
            for (int column = 0; column < columns; column++)
            {
                if (builders[column] is not { } builder)
                {
                    continue;
                }

                long started = Stopwatch.GetTimestamp();
                builder.Settle();
                builder.Finish();
                ends[column] = Math.Min(ends[column], (Stopwatch.GetTimestamp() - started) * 1e9 / Stopwatch.Frequency / rows);
                best[column] = Math.Min(best[column], ticks[column] * 1e9 / Stopwatch.Frequency / rows);
            }
        }

        Console.WriteLine(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"  the plan, every column's: {plan,5:F1} ns a row"));
        for (int column = 0; column < columns; column++)
        {
            if (column != instant)
            {
                Console.WriteLine(string.Create(
                    System.Globalization.CultureInfo.InvariantCulture,
                    $"  {GeneratedRequest.Schema[column].Name,-12} {best[column],5:F1} ns a row, its end {ends[column],4:F1}"));
            }
        }
    }

    /// <summary>The rows written to memory under <paramref name="options"/>: the seconds it took, and the file.</summary>
    private static async Task<(double Seconds, byte[] File)> WriteAsync(GeneratedRequest[] requests, VortexWriteOptions options)
    {
        using MemoryStream stream = new MemoryStream();
        long started = Stopwatch.GetTimestamp();
        await using (VortexFileWriter writer = VortexFileWriter.Create(new StreamSegmentSink(stream), VortexTypes.ToDType(GeneratedRequest.Schema, new Vorticity.Types.DTypeArena()), options))
        {
            for (int at = 0; at < requests.Length; at += Batch)
            {
                await writer.WriteAsync<GeneratedRequest>(requests.AsSpan(at, Math.Min(Batch, requests.Length - at)));
            }

            await writer.CompleteAsync();
        }

        return (Stopwatch.GetElapsedTime(started).TotalSeconds, stream.ToArray());
    }
}
