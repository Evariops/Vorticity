using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Vorticity.Dataset;

namespace Vorticity.Benchmarks.Queries;

/// <summary>
/// What answering from the synopses saves an aggregation (<c>--micro synopsisanswer</c>): a dataset of
/// periods of an hour, in memory, two million requests by default (<c>--rows</c>) over a few hours, by
/// service (20), method (5) and status (5), with a duration and a size; compacted until its hours are
/// sealed. Each question is asked with the synopses on and off, best of the rounds: its time, the table
/// rows it folded and the rows it read.
/// </summary>
/// <remarks>Run it with <c>DOTNET_TieredCompilation=0</c>.</remarks>
internal static class SynopsisAnswerMicro
{
    private static readonly DateTimeOffset Start = new(2026, 10, 19, 0, 0, 0, TimeSpan.Zero);

    internal static async Task<int> Run(long rows, int rounds)
    {
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, AnsweredRequest.Schema, new DatasetOptions
        {
            ClusteringKey = [nameof(AnsweredRequest.At)],
            Periods = new PeriodOptions { Lateness = TimeSpan.FromMinutes(5), Length = SliceWidth.Hour },
            Write = new VortexWriteOptions { Indexes = IndexPolicy.None.WithBudgetPerMille(1_000) },
        });

        // A row every 10 ms: two million rows are five and a half hours, appended a minute at a time.
        Random random = new Random(3);
        const int Part = 60_000;
        for (long at = 0; at < rows; at += Part)
        {
            AnsweredRequest[] part = new AnsweredRequest[(int)Math.Min(Part, rows - at)];
            for (int i = 0; i < part.Length; i++)
            {
                part[i] = new AnsweredRequest(
                    Start.AddMilliseconds((at + i) * 10),
                    $"service-{random.Next(20)}",
                    $"method-{random.Next(5)}",
                    200 + (random.Next(5) * 100),
                    random.NextDouble() * 1_000,
                    random.NextInt64(1, 1L << 30));
            }

            await using ObjectDraft draft = dataset.StartObject();
            await draft.Writer.WriteAsync<AnsweredRequest>(part);
            await dataset.AppendAsync(draft);
        }

        long begin = Stopwatch.GetTimestamp();
        while (await dataset.CompactAsync(new CompactionOptions { LevelZeroCeiling = 2 }) is not null)
        {
        }

        Console.WriteLine($"{rows} rows over {rows / 360_000.0:F1} hours, compacted in {Stopwatch.GetElapsedTime(begin).TotalSeconds:F1} s; best of {rounds}");
        Console.WriteLine($"{"question",-44} {"synopses ms",12} {"rows ms",10} {"speed-up",9} {"table rows",11} {"rows read",11} {"rows without",13}");
        DateTimeOffset from = Start.AddMinutes(37.5);
        DateTimeOffset to = Start.AddHours(4).AddMinutes(11);
        (string Name, Func<Scan<AnsweredRequest>, Vorticity.Aggregation> Ask)[] questions =
        [
            ("count by service", scan => scan.GroupBy(r => r.Service).Select(g => (g.Key, g.Count()))),
            ("sums and extremes by service and method", scan => scan.GroupBy(r => (r.Service, r.Method)).Select(g => (g.Key.Service, g.Key.Method, g.Count(), g.Sum(r => r.Bytes), g.Max(r => r.Duration), g.Average(r => r.Duration)))),
            ("count by service, status 500", scan => scan.Where(r => r.Status == 500).GroupBy(r => r.Service).Select(g => (g.Key, g.Count()))),
            ("count by hour and method", scan => scan.GroupBy(r => (r.At.Truncate(CalendarUnit.Hour), r.Method)).Select(g => (g.Key.Item1, g.Key.Method, g.Count()))),
            ("sum by method, a window that cuts slices", scan => scan.Where(r => r.At >= from && r.At < to).GroupBy(r => r.Method).Select(g => (g.Key, g.Count(), g.Sum(r => r.Bytes)))),
        ];

        foreach ((string name, Func<Scan<AnsweredRequest>, Vorticity.Aggregation> ask) in questions)
        {
            (double on, ScanMetrics metrics) = await TimedAsync(dataset, ask, synopses: true, rounds);
            (double off, ScanMetrics without) = await TimedAsync(dataset, ask, synopses: false, rounds);
            Console.WriteLine($"{name,-44} {on,12:F2} {off,10:F2} {off / on,8:F1}x {metrics.SynopsisRows,11} {metrics.Rows,11} {without.Rows,13}");
        }

        return 0;
    }

    private static async Task<(double Milliseconds, ScanMetrics Metrics)> TimedAsync(
        VortexDataset dataset, Func<Scan<AnsweredRequest>, Vorticity.Aggregation> ask, bool synopses, int rounds)
    {
        double best = double.MaxValue;
        ScanMetrics metrics = default;
        for (int round = 0; round <= rounds; round++)
        {
            Vorticity.Aggregation aggregation = ask(dataset.Scan<AnsweredRequest>().With(new ScanOptions { UseSynopses = synopses }));
            long begin = Stopwatch.GetTimestamp();
            long groups = 0;
            System.Collections.Generic.IAsyncEnumerator<RecordBatch> batches = aggregation.Query.Batches(default);
            await using (batches)
            {
                while (await batches.MoveNextAsync())
                {
                    groups += batches.Current.RowCount;
                }
            }

            // The first round warms the caches and the code, and is not counted.
            if (round > 0)
            {
                best = Math.Min(best, Stopwatch.GetElapsedTime(begin).TotalMilliseconds);
            }

            metrics = aggregation.Metrics;
            GC.KeepAlive(groups);
        }

        return (best, metrics);
    }
}

[VortexRecord]
public partial record struct AnsweredRequest(DateTimeOffset At, string Service, string Method, int Status, double Duration, long Bytes);
