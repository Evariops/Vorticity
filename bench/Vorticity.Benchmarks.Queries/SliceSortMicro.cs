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
/// the sort alone, per row. With <c>--deep</c>, a duration drawn from 500 000 values sorts after the service,
/// a key almost unique to each row with those before it, as a large object's object-level rule admits one.
/// </summary>
/// <remarks>Run it with <c>DOTNET_TieredCompilation=0</c>.</remarks>
internal static class SliceSortMicro
{
    private const int Rows = 1_000_000;

    internal static Task<int> Run(int rounds, bool deep)
    {
        Random random = new Random(5);
        DateTimeOffset start = new DateTimeOffset(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);
        if (deep)
        {
            DeepSpan[] spans = new DeepSpan[Rows];
            for (int i = 0; i < spans.Length; i++)
            {
                spans[i] = new DeepSpan(
                    start.AddTicks(i * 600L), $"region-{random.Next(5)}", 200 + (random.Next(5) * 100), $"service-{random.Next(600)}", random.NextInt64(Rows / 2));
            }

            return SortAsync(spans, rounds, start, [nameof(DeepSpan.Region), nameof(DeepSpan.Status), nameof(DeepSpan.Service), nameof(DeepSpan.Duration)]);
        }

        SliceSpan[] rows = new SliceSpan[Rows];
        for (int i = 0; i < rows.Length; i++)
        {
            rows[i] = new SliceSpan(start.AddTicks(i * 600L), $"region-{random.Next(5)}", 200 + (random.Next(5) * 100), $"service-{random.Next(600)}");
        }

        return SortAsync(rows, rounds, start, [nameof(SliceSpan.Region), nameof(SliceSpan.Status), nameof(SliceSpan.Service)]);
    }

    private static async Task<int> SortAsync<TRecord>(TRecord[] rows, int rounds, DateTimeOffset start, string[] order)
        where TRecord : IVortexRecord<TRecord>
    {
        string path = Path.Combine(Path.GetTempPath(), $"vortex-slicesort-{Guid.NewGuid():N}.vortex");
        try
        {
            await using (VortexFileWriter writer = VortexSession.Default.CreateWriter<TRecord>(path))
            {
                await writer.WriteAsync<TRecord>(rows);
                await writer.CompleteAsync();
            }

            await using VortexFile file = await VortexFile.OpenAsync(path);
            List<RecordBatch> batches = [];
            await foreach (RecordBatch batch in file.Scan<TRecord>().ToBatchesAsync())
            {
                batches.Add(batch);
            }

            long micros = start.ToUnixTimeMilliseconds() * 1000;
            int[] fields = Array.ConvertAll(order, file.DType.IndexOfField);
            SliceTable table = new SliceTable(TimeUnit.Microseconds, [new TimeSlice(micros, micros + 60_000_000, 0, Rows)], fields);
            int instant = file.DType.IndexOfField(nameof(SliceSpan.At));
            double best = double.MaxValue;
            for (int round = 0; round < rounds; round++)
            {
                using SortedSlices sorter = new SortedSlices(table, instant, file.DType);
                long begin = Stopwatch.GetTimestamp();
                foreach (RecordBatch batch in batches)
                {
                    await sorter.AppendAsync(batch, static (_, _) => ValueTask.CompletedTask, default);
                }

                await sorter.DrainAsync();
                best = Math.Min(best, Stopwatch.GetElapsedTime(begin).TotalMilliseconds);
            }

            // One pass more, untimed, folds the sorted rows into a checksum: two builds that sort alike print the same.
            long checksum = 0;
            using (SortedSlices sorter = new SortedSlices(table, instant, file.DType))
            {
                foreach (RecordBatch batch in batches)
                {
                    await sorter.AppendAsync(
                        batch,
                        (sorted, _) =>
                        {
                            checksum = Fold<TRecord>(checksum, sorted.Batch, fields);
                            return ValueTask.CompletedTask;
                        },
                        default);
                }

                await sorter.DrainAsync();
            }

            Console.WriteLine($"slice of {Rows} rows, {order.Length} synopsis columns: best {best:F1} ms, {best * 1e6 / Rows:F1} ns/row, checksum {checksum:x16}");
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

    /// <summary>The rows of a sorted slice folded in order, their region, status and service, and a duration when sorted by one, into <paramref name="checksum"/>.</summary>
    private static long Fold<TRecord>(long checksum, RecordBatch sorted, int[] fields)
        where TRecord : IVortexRecord<TRecord>
    {
        Columns<TRecord> columns = sorted.As<TRecord>();
        Column<string> regions = columns.Column<string>(fields[0]);
        ReadOnlySpan<int> statuses = columns.Column<int>(fields[1]).Values;
        Column<string> services = columns.Column<string>(fields[2]);
        ReadOnlySpan<long> durations = fields.Length > 3 ? columns.Column<long>(fields[3]).Values : default;
        for (int row = 0; row < sorted.RowCount; row++)
        {
            ReadOnlySpan<byte> region = regions[row];
            ReadOnlySpan<byte> service = services[row];
            checksum = (checksum * 1_000_003) ^ (statuses[row] + (region[^1] * 1_009L) + (service.Length * 65_537L) + (service[^1] * 257L));
            checksum ^= durations.IsEmpty ? 0 : durations[row] * 31;
        }

        return checksum;
    }
}

[VortexRecord]
public partial record struct SliceSpan(DateTimeOffset At, string Region, int Status, string Service);

[VortexRecord]
public partial record struct DeepSpan(DateTimeOffset At, string Region, int Status, string Service, long Duration);
