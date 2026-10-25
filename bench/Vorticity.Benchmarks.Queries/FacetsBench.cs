using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Vorticity.Dataset;

namespace Vorticity.Benchmarks.Queries;

/// <summary>
/// The time-window design's worst case of many facets (<c>--micro facets</c>): spans with a dozen repeating
/// facets and a route under them (<see cref="SpanGenerator"/>), over hours clustered by hour, in memory behind a store that
/// counts and waits. It compacts such a table, then asks questions on the shallow facets and on the route, cold and
/// warm, by the synopses where they answer and by the rows, as <c>--micro questions</c> does.
/// </summary>
internal static class FacetsBench
{
    internal static async Task<int> Run(int hours, double rate, int latency, int rounds, int budget)
    {
        await using MemoryObjectStore bytes = new MemoryObjectStore();
        await using CountingObjectStore store = new CountingObjectStore(bytes);
        long begin = Stopwatch.GetTimestamp();
        long rows = 0;
        await using (VortexDataset dataset = await VortexDataset.CreateAsync(store, GeneratedSpan.Schema, new DatasetOptions
        {
            ClusteringKey = [nameof(GeneratedSpan.At)],
            Clustering = new ClusteringOptions { Value = ClusteringValue.Truncate(CalendarUnit.Hour) },
            Write = new VortexWriteOptions { Indexes = IndexPolicy.Auto.WithBudgetPerMille(budget) },
        }))
        {
            SpanGenerator generator = new SpanGenerator(31);
            int perMinute = (int)(rate * 60);
            for (int minute = 0; minute < hours * 60; minute++)
            {
                GeneratedSpan[] batch = new GeneratedSpan[perMinute];
                for (int i = 0; i < batch.Length; i++)
                {
                    batch[i] = generator.Next(QuestionBench.Start.AddMinutes(minute).AddTicks(i * (TimeSpan.TicksPerMinute / perMinute)));
                }

                await using ObjectDraft draft = dataset.StartObject();
                await draft.Writer.WriteAsync<GeneratedSpan>(batch);
                await dataset.AppendAsync(draft);
                rows += batch.Length;
            }

            await QuestionBench.CompactAsync(dataset);
        }

        Console.WriteLine(
            $"{rows} spans over {hours} hours clustered by hour, written and compacted in {Stopwatch.GetElapsedTime(begin).TotalSeconds:F1} s; " +
            $"indexes up to {budget} ‰ of the data; {latency} ms a round trip; warm is the best of {rounds}");
        bytes.Latency = TimeSpan.FromMilliseconds(latency);
        Console.WriteLine($"{"question",-58} {"path",-8} {"",-4} {"requests",8} {"steps",6} {"MB",8} {"states",9} {"rows",9} {"ms",9} {"cpu ms",8}");
        (string Name, Func<Scan<GeneratedSpan>, Aggregation> Ask)[] questions =
        [
            ("errors per service and per hour", scan => scan.Where(s => s.Status == "error").GroupBy(s => (s.Service, s.At.Truncate(CalendarUnit.Hour))).Select(g => (g.Key.Service, g.Key.Item2, g.Count()))),
            ("server spans per region and kind", scan => scan.Where(s => s.Environment == "prod").GroupBy(s => (s.Region, s.Kind)).Select(g => (g.Key.Region, g.Key.Kind, g.Count()))),
            ("a count per route, the deepest facet", scan => scan.GroupBy(s => s.Route).Select(g => (g.Key, g.Count()))),
            ("one service's routes under a status, per hour", scan => scan.Where(s => s.Service == "svc-001" && s.HttpStatus >= 500).GroupBy(s => (s.Route, s.At.Truncate(CalendarUnit.Hour))).Select(g => (g.Key.Route, g.Key.Item2, g.Count()))),
            ("the mean duration per database system", scan => scan.Where(s => s.DbSystem != null).GroupBy(s => s.DbSystem).Select(g => (g.Key, g.Count(), g.Average(s => s.DurationNs)))),
        ];
        foreach ((string name, Func<Scan<GeneratedSpan>, Aggregation> ask) in questions)
        {
            await QuestionBench.AskAsync(bytes, store, name, ask, rounds);
        }

        return 0;
    }
}
