using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.Expressions;
using Vorticity.Indexes;
using Vorticity.Keys;
using Vorticity.Scanning;

namespace Vorticity.Benchmarks.Queries;

/// <summary>
/// The time order of an object sorted slice by slice, against the same rows in time order
/// (<c>--micro sliceorder</c>): four million spans, slices of a minute, half a million rows each or
/// <c>--slice-rows</c>, sorted by status (5 values), region (5) and service (600), then by instant. A
/// read of every row in time order, a seek and a rank from a cursor just opened, and a page of a
/// hundred rows from an instant: what the slice table costs where a sorted instant cost nothing.
/// </summary>
/// <remarks>Run it with <c>DOTNET_TieredCompilation=0</c>.</remarks>
internal static class SliceOrderMicro
{
    private const int Rows = 4_000_000;
    private const int Probes = 16;

    internal static async Task<int> Run(int rounds, int sliceRows)
    {
        Random random = new Random(5);
        DateTimeOffset start = new DateTimeOffset(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);
        long spacing = TimeSpan.TicksPerMinute / sliceRows;
        SliceSpan[] inTime = new SliceSpan[Rows];
        for (int i = 0; i < inTime.Length; i++)
        {
            inTime[i] = new SliceSpan(start.AddTicks(i * spacing), $"region-{random.Next(5)}", 200 + (random.Next(5) * 100), $"service-{random.Next(600)}");
        }

        long[] instants = [.. inTime.Select(span => (span.At.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) / 10)];
        List<TimeSlice> slices = TimeSlicer.Cut(instants, TimeUnit.Microseconds, targetRows: sliceRows);
        List<SliceSpan> sorted = new List<SliceSpan>(Rows);
        foreach (TimeSlice slice in slices)
        {
            sorted.AddRange(inTime.Skip((int)slice.FirstRow).Take((int)slice.Rows)
                .OrderBy(span => span.Status).ThenBy(span => span.Region, StringComparer.Ordinal).ThenBy(span => span.Service, StringComparer.Ordinal).ThenBy(span => span.At));
        }

        string sliced = Path.Combine(Path.GetTempPath(), $"vortex-sliceorder-{Guid.NewGuid():N}.vortex");
        string timed = Path.Combine(Path.GetTempPath(), $"vortex-timeorder-{Guid.NewGuid():N}.vortex");
        try
        {
            VortexWriteOptions options = new VortexWriteOptions { Indexes = IndexPolicy.None, CarriesSlices = true };
            await using (VortexFileWriter writer = VortexSession.Default.CreateWriter<SliceSpan>(sliced, options))
            {
                await writer.WriteAsync<SliceSpan>([.. sorted]);
                writer.DeclareSlices(nameof(SliceSpan.At), new SliceTable(TimeUnit.Microseconds, slices, [2, 1, 3]));
                await writer.CompleteAsync();
            }

            await using (VortexFileWriter writer = VortexSession.Default.CreateWriter<SliceSpan>(timed, new VortexWriteOptions { Indexes = IndexPolicy.None }))
            {
                await writer.WriteAsync<SliceSpan>(inTime);
                await writer.CompleteAsync();
            }

            long[] keys = [.. Enumerable.Range(0, Probes).Select(_ => instants[random.Next(Rows)])];
            Console.WriteLine($"{Rows} rows, {slices.Count} slices of about {Rows / slices.Count} rows; best of {rounds}");
            foreach ((string name, string path) in new[] { ("time order", timed), ("sorted slices", sliced) })
            {
                await using VortexFile file = await VortexFile.OpenAsync(path);
                KeyPlan plan = await file.Keys(nameof(SliceSpan.At)).ExplainAsync();
                double read = await BestAsync(rounds, async () =>
                {
                    long count = 0;
                    await foreach (RecordBatch batch in file.ScanBuilder().InKeyOrder(nameof(SliceSpan.At)).ExecuteAsync())
                    {
                        count += batch.RowCount;
                        batch.Dispose();
                    }

                    return count;
                });

                double seek = await BestAsync(rounds, async () =>
                {
                    foreach (long key in keys)
                    {
                        await using KeyCursor cursor = await file.Keys(nameof(SliceSpan.At)).OpenAsync();
                        await cursor.SeekAsync(FilterLiteral.From(key), SeekMode.AtOrAfter);
                    }

                    return 0;
                });

                double rank = await BestAsync(rounds, async () =>
                {
                    long sum = 0;
                    foreach (long key in keys)
                    {
                        await using KeyCursor cursor = await file.Keys(nameof(SliceSpan.At)).OpenAsync();
                        sum += await cursor.RankAsync(FilterLiteral.From(key));
                    }

                    return sum;
                });

                double page = await BestAsync(rounds, async () =>
                {
                    long count = 0;
                    foreach (long key in keys)
                    {
                        VortexExpr from = Expr.Ge(Expr.Field(nameof(SliceSpan.At)), Expr.Literal(FilterLiteral.From(key)));
                        await foreach (RecordBatch batch in file.ScanBuilder().Where(from).InKeyOrder(nameof(SliceSpan.At)).WithMaxBatchRows(100).ExecuteAsync())
                        {
                            count += batch.RowCount;
                            batch.Dispose();
                            break;
                        }
                    }

                    return count;
                });

                Console.WriteLine(
                    $"{name,-14} {plan.Source,-12} read {read * 1e6 / Rows,6:F1} ns/row   seek {seek / Probes,7:F2} ms   rank {rank / Probes,7:F2} ms   page {page / Probes,7:F2} ms");
            }
        }
        finally
        {
            System.IO.File.Delete(sliced);
            System.IO.File.Delete(timed);
        }

        return 0;
    }

    /// <summary>The best time of <paramref name="rounds"/> runs of <paramref name="work"/>, in milliseconds.</summary>
    private static async Task<double> BestAsync(int rounds, Func<Task<long>> work)
    {
        double best = double.MaxValue;
        for (int round = 0; round < rounds; round++)
        {
            long begin = Stopwatch.GetTimestamp();
            await work();
            best = Math.Min(best, Stopwatch.GetElapsedTime(begin).TotalMilliseconds);
        }

        return best;
    }
}
