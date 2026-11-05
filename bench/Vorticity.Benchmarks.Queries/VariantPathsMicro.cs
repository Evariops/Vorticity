using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Vorticity.Dataset;

namespace Vorticity.Benchmarks.Queries;

/// <summary>
/// What the paths of a window cost (<c>--micro variantpaths --store DIR</c>): the log records of one source over one hour of a
/// dataset of them, <c>--service</c> and <c>--hour</c>, their attributes' paths against a count of the same rows, at each of
/// <c>--degrees</c>: cold, a session of its own over a store that waits <c>--latency-ms</c> a request, and warm, the same
/// session asked ten times again, the best of <c>--rounds</c> each; then the paths' counts against the census the exact option makes. With
/// <c>--loop SECONDS</c>, the warm paths question alone, asked again for that long at the first degree, for a profiler.
/// </summary>
internal static class VariantPathsMicro
{
    internal static async Task<int> Run(string root, string service, int hour, int[] degrees, int rounds, int latencyMs, double loopSeconds = 0)
    {
        DateTimeOffset from = QuestionBench.Start.AddHours(hour);
        DateTimeOffset to = from.AddHours(1);
        Scan<LogRow> Window(VortexDataset dataset) =>
            dataset.Scan<LogRow>().Where(r => r.ServiceName == service & r.At >= from & r.At < to);

        if (loopSeconds > 0)
        {
            await using VortexSession looped = VortexSession.Create(options => options.MaxDegreeOfParallelism = degrees[0]);
            await using VortexDataset dataset = await VortexDataset.OpenAsync(new FileObjectStore(root), new DatasetOptions { Session = looped });
            int listed = (await Window(dataset).PathsAsync(r => r.Attributes)).Count;
            Console.WriteLine($"loop: begins, process {Environment.ProcessId}");
            int asked = 0;
            double best = double.MaxValue;
            long start = Stopwatch.GetTimestamp();
            while (Stopwatch.GetElapsedTime(start).TotalSeconds < loopSeconds)
            {
                long begin = Stopwatch.GetTimestamp();
                listed = (await Window(dataset).PathsAsync(r => r.Attributes)).Count;
                best = Math.Min(best, Stopwatch.GetElapsedTime(begin).TotalMilliseconds);
                asked++;
            }

            Console.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"{service}, hour {hour}, {degrees[0]} lanes: paths asked {asked} times, {Stopwatch.GetElapsedTime(start).TotalMilliseconds / asked:F1} ms each, best {best:F1} ms, {listed} listed"));
            return 0;
        }

        foreach (int degree in degrees)
        {
            (double countCold, long countRequests, double countWarm, long rows) = await TimeAsync(root, degree, rounds, latencyMs, async dataset => await Window(dataset).CountAsync());
            (double pathsCold, long pathsRequests, double pathsWarm, long listed) = await TimeAsync(root, degree, rounds, latencyMs, async dataset => (await Window(dataset).PathsAsync(r => r.Attributes)).Count);
            (_, _, double exactWarm, _) = await TimeAsync(root, degree, 1, latencyMs, async dataset => (await Window(dataset).PathsAsync(r => r.Attributes, new PathsOptions { Exact = true })).Count, repeats: 2);
            Console.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"{service}, hour {hour}, {degree} lane{(degree > 1 ? "s" : string.Empty)}: {rows:N0} rows; count {countCold:F1} ms cold ({countRequests} requests), {countWarm:F1} ms warm; paths {pathsCold:F1} ms cold ({pathsRequests} requests), {pathsWarm:F1} ms warm, {listed} listed; exact {exactWarm:F1} ms warm"));
        }

        // The answer against the truth, at the last degree: every path held listed, and how near the estimates fall.
        await using VortexSession session = VortexSession.Create(options => options.MaxDegreeOfParallelism = degrees[^1]);
        await using VortexDataset opened = await VortexDataset.OpenAsync(new FileObjectStore(root), new DatasetOptions { Session = session });
        VariantPaths found = await Window(opened).PathsAsync(r => r.Attributes);
        VariantPaths truth = await Window(opened).PathsAsync(r => r.Attributes, new PathsOptions { Exact = true });
        Dictionary<string, long> held = truth.ToDictionary(Key, path => path.Rows);
        int missing = truth.Count(path => !found.Any(f => Key(f) == Key(path)));
        long estimated = found.Where(path => !path.Exact).Sum(path => path.Rows);
        long trueOfEstimated = found.Where(path => !path.Exact).Sum(path => held.GetValueOrDefault(Key(path)));
        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"paths held {truth.Count}, listed {found.Count} ({found.Count(path => !held.ContainsKey(Key(path)))} held by no kept row), missing {missing}; {found.Count(path => path.Exact)} exact; estimated rows {estimated:N0} against {trueOfEstimated:N0}; complete {found.Complete}"));
        Console.WriteLine($"read: {found.Reading}");
        foreach (VariantPath extra in found.Where(path => !held.ContainsKey(Key(path))))
        {
            Console.WriteLine($"  held by no kept row: {extra}");
        }

        return missing == 0 ? 0 : 1;
    }

    /// <summary>
    /// What <paramref name="question"/> costs cold, a session of its own over a store that waits a round trip a request, and
    /// warm, the same session asked again: the best of <paramref name="rounds"/> of each, the requests of a cold one, and
    /// its answer.
    /// </summary>
    private static async Task<(double Cold, long Requests, double Warm, long Answer)> TimeAsync(
        string root, int degree, int rounds, int latencyMs, Func<VortexDataset, Task<long>> question, int repeats = WarmRepeats)
    {
        double cold = double.MaxValue;
        double warm = double.MaxValue;
        long requests = 0;
        long answer = 0;
        for (int round = 0; round < rounds; round++)
        {
            CountingObjectStore counted = new CountingObjectStore(new LatencyObjectStore(new FileObjectStore(root)) { Latency = TimeSpan.FromMilliseconds(latencyMs) });
            await using VortexSession session = VortexSession.Create(options => options.MaxDegreeOfParallelism = degree);
            await using VortexDataset dataset = await VortexDataset.OpenAsync(counted, new DatasetOptions { Session = session });
            counted.Reset();
            long begin = Stopwatch.GetTimestamp();
            answer = await question(dataset);
            cold = Math.Min(cold, Stopwatch.GetElapsedTime(begin).TotalMilliseconds);
            requests = counted.Requests;

            // Warm, the same session asked again and again: a question asked once more after a cold one ran code the JIT had
            // not yet optimised, three times slower than the same question asked on.
            for (int again = 0; again < repeats; again++)
            {
                begin = Stopwatch.GetTimestamp();
                await question(dataset);
                warm = Math.Min(warm, Stopwatch.GetElapsedTime(begin).TotalMilliseconds);
            }
        }

        return (cold, requests, warm, answer);
    }

    /// <summary>The warm questions of a round, the best of which is kept: the exact option's, two.</summary>
    private const int WarmRepeats = 10;

    private static string Key(VariantPath path) => $"{path.Path}#{path.Class}";
}
