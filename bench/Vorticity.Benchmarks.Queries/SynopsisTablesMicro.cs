using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.Indexes;

namespace Vorticity.Benchmarks.Queries;

/// <summary>
/// What the synopsis tables cost a job (<c>--micro synopsistables</c>): a million spans over twenty slices
/// of 50 000 rows, sorted by region (5 values), status (5) and service (600), with a duration, a size and a
/// count of retries, a quarter of them null, as measures. Each slice sorted is counted at every depth
/// (<see cref="SynopsisTables.AddSlice"/>), then the object's tables are built and merged
/// (<see cref="SynopsisTables.Build"/>): per row, against the sort itself.
/// </summary>
/// <remarks>Run it with <c>DOTNET_TieredCompilation=0</c>.</remarks>
internal static class SynopsisTablesMicro
{
    private const int Rows = 1_000_000;

    private const int SliceRows = 50_000;

    internal static async Task<int> Run(int rounds)
    {
        Random random = new Random(5);
        DateTimeOffset start = new DateTimeOffset(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);
        SynopsisSpan[] rows = new SynopsisSpan[Rows];
        for (int i = 0; i < rows.Length; i++)
        {
            rows[i] = new SynopsisSpan(
                start.AddTicks(i * 600L),
                $"region-{random.Next(5)}",
                200 + (random.Next(5) * 100),
                $"service-{random.Next(600)}",
                random.NextInt64(1_000_000),
                random.NextDouble() * 1e6,
                random.Next(4) == 0 ? null : random.Next(10));
        }

        string path = Path.Combine(Path.GetTempPath(), $"vortex-synopsistables-{Guid.NewGuid():N}.vortex");
        try
        {
            await using (VortexFileWriter writer = VortexSession.Default.CreateWriter<SynopsisSpan>(path))
            {
                await writer.WriteAsync<SynopsisSpan>(rows);
                await writer.CompleteAsync();
            }

            await using VortexFile file = await VortexFile.OpenAsync(path);
            List<RecordBatch> batches = [];
            await foreach (RecordBatch batch in file.Scan<SynopsisSpan>().ToBatchesAsync())
            {
                batches.Add(batch);
            }

            long micros = start.ToUnixTimeMilliseconds() * 1000;
            List<TimeSlice> slices = [];
            for (int slice = 0; slice < Rows / SliceRows; slice++)
            {
                long first = micros + (slice * 3_000_000L);
                slices.Add(new TimeSlice(first, first + 3_000_000, (long)slice * SliceRows, SliceRows));
            }

            int instant = file.DType.IndexOfField(nameof(SynopsisSpan.At));
            SliceTable table = new SliceTable(
                TimeUnit.Microseconds,
                slices,
                [file.DType.IndexOfField(nameof(SynopsisSpan.Region)), file.DType.IndexOfField(nameof(SynopsisSpan.Status)), file.DType.IndexOfField(nameof(SynopsisSpan.Service))]);
            int[] measures = SynopsisTables.Measures(file.DType, instant, table.Order);
            double bestTotal = double.MaxValue;
            double bestCount = double.MaxValue;
            double bestBuild = double.MaxValue;
            int tables = 0;
            long tableRows = 0;
            for (int round = 0; round < rounds; round++)
            {
                using SortedSlices sorter = new SortedSlices(table, instant, file.DType);
                using SynopsisTables synopsis = new SynopsisTables(file.DType, TimeUnit.Microseconds, measures);
                int next = 0;
                long counting = 0;
                ValueTask Count(RecordBatch sorted, System.Threading.CancellationToken _)
                {
                    long begin = Stopwatch.GetTimestamp();
                    synopsis.AddSlice(sorted, table.Slices[next].Start, sorter.Order, sorter.SortedCodes);
                    counting += Stopwatch.GetTimestamp() - begin;
                    next++;
                    return ValueTask.CompletedTask;
                }

                long total = Stopwatch.GetTimestamp();
                foreach (RecordBatch batch in batches)
                {
                    await sorter.AppendAsync(batch, Count, default);
                }

                double totalMs = Stopwatch.GetElapsedTime(total).TotalMilliseconds;
                long built = Stopwatch.GetTimestamp();
                List<(SynopsisTableInfo Table, int Node)> result = synopsis.Build();
                double buildMs = Stopwatch.GetElapsedTime(built).TotalMilliseconds;
                bestTotal = Math.Min(bestTotal, totalMs);
                bestCount = Math.Min(bestCount, counting * 1000.0 / Stopwatch.Frequency);
                bestBuild = Math.Min(bestBuild, buildMs);
                tables = result.Count;
                tableRows = 0;
                foreach ((SynopsisTableInfo info, int _) in result)
                {
                    tableRows += info.Rows;
                }
            }

            // What a question pays to group a row instead: the engine on one lane, by the three columns, a count
            // and the three measures summed, the price of a row the synopsis choice weighs tables with.
            double bestGroup = double.MaxValue;
            await using (VortexSession session = VortexSession.Create(options => options.MaxDegreeOfParallelism = 1))
            {
                await using VortexFile grouped = await session.OpenAsync(path);
                for (int round = 0; round < rounds; round++)
                {
                    long begin = Stopwatch.GetTimestamp();
                    long groups = 0;
                    await foreach (Columns<SynopsisGroup> part in grouped.Scan<SynopsisSpan>()
                        .GroupBy(s => (s.Region, s.Status, s.Service))
                        .Select(g => (g.Key.Region, g.Key.Status, g.Key.Service, g.Count(), g.Sum(s => s.Duration), g.Sum(s => s.Bytes), g.Sum(s => s.Retries)))
                        .As<SynopsisGroup>())
                    {
                        groups += Vorticity.Bench.Scenarios.Consume.Typed(part);
                    }

                    bestGroup = Math.Min(bestGroup, Stopwatch.GetElapsedTime(begin).TotalMilliseconds);
                }
            }

            Console.WriteLine($"{Rows} rows in {Rows / SliceRows} slices, 3 synopsis columns, {measures.Length} measures; best of {rounds}");
            Console.WriteLine($"sort and count   {bestTotal,8:F1} ms  {bestTotal * 1e6 / Rows,6:F1} ns/row");
            Console.WriteLine($"  count slices   {bestCount,8:F1} ms  {bestCount * 1e6 / Rows,6:F1} ns/row");
            Console.WriteLine($"build tables     {bestBuild,8:F1} ms  {bestBuild * 1e6 / Rows,6:F1} ns/row, {tables} tables of {tableRows} rows");
            Console.WriteLine($"group, one lane  {bestGroup,8:F1} ms  {bestGroup * 1e6 / Rows,6:F1} ns/row, read and decode included");
            foreach (RecordBatch batch in batches)
            {
                batch.Dispose();
            }
        }
        finally
        {
            System.IO.File.Delete(path);
        }

        return 0;
    }
}

[VortexRecord]
internal partial record struct SynopsisSpan(DateTimeOffset At, string Region, int Status, string Service, long Duration, double Bytes, int? Retries);

[VortexRecord]
internal partial record struct SynopsisGroup(string Region, int Status, string Service, long Rows, long Duration, double Bytes, long? Retries);
