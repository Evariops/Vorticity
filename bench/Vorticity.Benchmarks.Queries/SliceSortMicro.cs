using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.Indexes;

namespace Vorticity.Benchmarks.Queries;

/// <summary>
/// One time slice sorted as a dataset of periods writes it (<c>--micro slicesort</c>): a million spans
/// of a minute, sorted by region (5 values), status (5) and service (600), then by instant, the slice's
/// rows copied in, their keys encoded, the rows sorted by the keys and gathered once. Nothing is written:
/// the sort alone, per row.
/// </summary>
/// <remarks>Run it with <c>DOTNET_TieredCompilation=0</c>.</remarks>
internal static class SliceSortMicro
{
    private const int Rows = 1_000_000;

    internal static async Task<int> Run(int rounds)
    {
        Random random = new Random(5);
        DateTimeOffset start = new DateTimeOffset(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);
        SliceSpan[] rows = new SliceSpan[Rows];
        for (int i = 0; i < rows.Length; i++)
        {
            rows[i] = new SliceSpan(start.AddTicks(i * 600L), $"region-{random.Next(5)}", 200 + (random.Next(5) * 100), $"service-{random.Next(600)}");
        }

        string path = Path.Combine(Path.GetTempPath(), $"vortex-slicesort-{Guid.NewGuid():N}.vortex");
        try
        {
            await using (VortexFileWriter writer = VortexSession.Default.CreateWriter<SliceSpan>(path))
            {
                await writer.WriteAsync<SliceSpan>(rows);
                await writer.CompleteAsync();
            }

            await using VortexFile file = await VortexFile.OpenAsync(path);
            List<RecordBatch> batches = [];
            await foreach (RecordBatch batch in file.Scan<SliceSpan>().ToBatchesAsync())
            {
                batches.Add(batch);
            }

            long micros = start.ToUnixTimeMilliseconds() * 1000;
            SliceTable table = new SliceTable(
                TimeUnit.Microseconds,
                [new TimeSlice(micros, micros + 60_000_000, 0, Rows)],
                [file.DType.IndexOfField(nameof(SliceSpan.Region)), file.DType.IndexOfField(nameof(SliceSpan.Status)), file.DType.IndexOfField(nameof(SliceSpan.Service))]);
            double best = double.MaxValue;
            for (int round = 0; round < rounds; round++)
            {
                using SortedSlices sorter = new SortedSlices(table, file.DType.IndexOfField(nameof(SliceSpan.At)), file.DType);
                long begin = Stopwatch.GetTimestamp();
                foreach (RecordBatch batch in batches)
                {
                    await sorter.AppendAsync(batch, static (_, _) => ValueTask.CompletedTask, default);
                }

                best = Math.Min(best, Stopwatch.GetElapsedTime(begin).TotalMilliseconds);
            }

            Console.WriteLine($"slice of {Rows} rows, 3 synopsis columns: best {best:F1} ms, {best * 1e6 / Rows:F1} ns/row");
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
public partial record struct SliceSpan(DateTimeOffset At, string Region, int Status, string Service);
