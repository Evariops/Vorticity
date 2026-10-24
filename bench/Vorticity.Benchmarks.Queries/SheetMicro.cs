using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Vorticity.File;
using Vorticity.IO;
using Vorticity.Scanning;
using Vorticity.Writing;

namespace Vorticity.Benchmarks.Queries;

/// <summary>
/// What a column sheet costs the writer (<c>--micro sheets</c>): a minute of the time-window design's requests, eleven
/// columns, written on one thread to memory with and without a sheet of each column, the best of several rounds of
/// each, interleaved; the difference a column a row, against the 6 ns the design allows.
/// </summary>
/// <remarks>Run it with <c>DOTNET_TieredCompilation=0</c>.</remarks>
internal static class SheetMicro
{
    private const int Batch = 65_536;

    internal static async Task<int> RunAsync(int rows, int rounds)
    {
        RequestGenerator generator = new RequestGenerator(17);
        DateTimeOffset start = new DateTimeOffset(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);
        GeneratedRequest[] requests = new GeneratedRequest[rows];
        for (int i = 0; i < rows; i++)
        {
            requests[i] = generator.Next(start.AddTicks(i * 200L));
        }

        VortexWriteOptions plain = new VortexWriteOptions { DegreeOfParallelism = 1, Identity = new Guid("51515151-5151-4151-8151-515151515151") };
        VortexWriteOptions sheets = plain with { ColumnSheetBytes = 0 };
        double without = double.MaxValue;
        double with = double.MaxValue;
        long plainBytes = 0;
        long sheetBytes = 0;
        for (int round = 0; round < Math.Max(rounds, 2); round++)
        {
            (double seconds, long bytes) = await WriteAsync(requests, plain);
            without = Math.Min(without, seconds);
            plainBytes = bytes;
            (seconds, bytes) = await WriteAsync(requests, sheets);
            with = Math.Min(with, seconds);
            sheetBytes = bytes;
        }

        int columns = GeneratedRequest.Schema.Count;
        double perColumnRow = (with - without) * 1e9 / ((double)rows * columns);
        Console.WriteLine(string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{rows:N0} requests, {columns} columns, one thread: {without * 1e3:F0} ms without sheets, {with * 1e3:F0} ms with them, " +
            $"{perColumnRow:F1} ns a column a row (the design allows 6); {(sheetBytes - plainBytes) / 1e3:F1} KB of sheets"));

        // Each column alone: its batches read back canonical, then a builder fed them, the best of the rounds.
        await PerColumnAsync(requests, plain, rounds);
        return perColumnRow <= 6 ? 0 : 1;
    }

    /// <summary>What a sheet of each column costs alone, fed the file's batches read back canonical.</summary>
    private static async Task PerColumnAsync(GeneratedRequest[] requests, VortexWriteOptions options, int rounds)
    {
        using MemoryStream stream = new MemoryStream();
        await using (VortexFileWriter writer = VortexFileWriter.Create(new StreamSegmentSink(stream), VortexTypes.ToDType(GeneratedRequest.Schema, new Vorticity.Types.DTypeArena()), options))
        {
            for (int at = 0; at < requests.Length; at += Batch)
            {
                await writer.WriteAsync<GeneratedRequest>(requests.AsSpan(at, Math.Min(Batch, requests.Length - at)));
            }

            await writer.CompleteAsync();
        }

        byte[] bytes = stream.ToArray();
        await using MemorySegmentSource source = new MemorySegmentSource(bytes);
        await using VortexFile file = await VortexFile.OpenAsync(source, new VortexOpenOptions());
        int columns = GeneratedRequest.Schema.Count;
        double[] best = new double[columns];
        Array.Fill(best, double.MaxValue);
        for (int round = 0; round < Math.Max(rounds, 2); round++)
        {
            long[] ticks = new long[columns];
            ColumnSheetBuilder[] builders = new ColumnSheetBuilder[columns];
            for (int column = 0; column < columns; column++)
            {
                builders[column] = new ColumnSheetBuilder();
            }

            await foreach (RecordBatch batch in file.ScanBuilder().ExecuteAsync())
            {
                for (int column = 0; column < columns; column++)
                {
                    ColumnSheetBuilder builder = builders[column];
                    int node = batch.Arena.GetNode(batch.RootIndex).GetFieldIndex(column);
                    long started = Stopwatch.GetTimestamp();
                    builder.Accumulate(batch.Arena, node, 0, batch.RowCount);
                    ticks[column] += Stopwatch.GetTimestamp() - started;
                }
            }

            for (int column = 0; column < columns; column++)
            {
                best[column] = Math.Min(best[column], ticks[column] * 1e9 / Stopwatch.Frequency / requests.Length);
            }
        }

        for (int column = 0; column < columns; column++)
        {
            Console.WriteLine(string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"  {GeneratedRequest.Schema[column].Name,-12} {best[column],5:F1} ns a row"));
        }
    }

    /// <summary>The rows written to memory under <paramref name="options"/>: the seconds it took, and the file's bytes.</summary>
    private static async Task<(double Seconds, long Bytes)> WriteAsync(GeneratedRequest[] requests, VortexWriteOptions options)
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

        return (Stopwatch.GetElapsedTime(started).TotalSeconds, stream.Length);
    }
}
