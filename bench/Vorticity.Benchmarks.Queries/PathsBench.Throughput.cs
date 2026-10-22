using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Dataset;

namespace Vorticity.Benchmarks.Queries;

internal static partial class PathsBench
{
    /// <summary>
    /// <c>--qps SECONDS</c>: the questions a node answers a second, those <c>--only</c> keeps asked in turn by each of
    /// <c>--clients</c> clients at once, each question at one lane and by its synopses where it aggregates, for so many
    /// seconds: warm, through one session whose synopsis cache holds every region after a first round of every question;
    /// then with no synopsis cache, every table read from the store, the requests a node asks of it. For each, the
    /// questions a second, the latencies' median and 99th percentile, the store's requests and bytes a second, and the
    /// cores the process kept busy.
    /// </summary>
    private static async Task ThroughputAsync(Settings settings, IReadOnlyList<Question> questions, Func<Question, Built> builtOf, string label)
    {
        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"throughput: {questions.Count} questions asked in turn for {settings.QpsSeconds:0.#} s, each at one lane; {label}"));
        Console.WriteLine($"{"cache",-10} {"clients",7} {"asked",7} {"q/s",9} {"p50 ms",9} {"p99 ms",9} {"req/s",9} {"MB/s",8} {"cores",6}");
        foreach (bool cached in new[] { true, false })
        {
            foreach (int clients in settings.Clients)
            {
                await ThroughputAsync(settings, questions, builtOf, cached, clients);
            }
        }
    }

    private static async Task ThroughputAsync(Settings settings, IReadOnlyList<Question> questions, Func<Question, Built> builtOf, bool cached, int clients)
    {
        string cache = Path.Combine(settings.Store, "cache", Guid.NewGuid().ToString("N"));
        Dictionary<Built, (FileObjectStore Files, CountingObjectStore Store, VortexDataset[] Datasets)> sets = [];
        try
        {
            await using VortexSession session = VortexSession.Create(o =>
            {
                o.MaxConcurrentReads = settings.MaxReads > 0 ? settings.MaxReads : o.MaxConcurrentReads;
                o.SynopsisCache = cached ? new SynopsisCache(cache, 8L << 30) : null;
                o.SegmentCache = settings.SegmentCacheBytes > 0 ? new SegmentCache(settings.SegmentCacheBytes) : null;
            });

            // A store, as remote as the profile says, and --handles handles a set the questions read, which the clients share in
            // turn.
            foreach (Question question in questions)
            {
                Built built = builtOf(question);
                if (sets.ContainsKey(built))
                {
                    continue;
                }

                FileObjectStore files = new FileObjectStore(built.Directory);
                LatencyObjectStore remote = new LatencyObjectStore(settings.KeepHandles ? new HeldFileObjectStore(files) : files)
                {
                    Latency = TimeSpan.FromMilliseconds(settings.Latency),
                    LatencyP99 = TimeSpan.FromMilliseconds(settings.LatencyP99),
                    ReadsPerSecondPerPrefix = settings.ReadsPerSecondPerPrefix,
                    LinkBitsPerSecond = settings.LinkBitsPerSecond,
                    BytesPerSecond = settings.BytesPerSecond,
                };
                CountingObjectStore store = new CountingObjectStore(remote);
                VortexDataset[] handles = new VortexDataset[Math.Max(settings.Handles, 1)];
                for (int h = 0; h < handles.Length; h++)
                {
                    handles[h] = await VortexDataset.OpenAsync(
                        store, new DatasetOptions { Session = session, MaxOpenObjects = settings.OpenObjects > 0 ? settings.OpenObjects : new DatasetOptions().MaxOpenObjects });
                }

                sets[built] = (files, store, handles);
            }

            // Every question once first: their code compiled, and, cached, every region they read held.
            foreach (Question question in questions)
            {
                foreach (VortexDataset handle in sets[builtOf(question)].Datasets)
                {
                    await AskOnceAsync(question, handle);
                }
            }

            foreach ((FileObjectStore _, CountingObjectStore store, VortexDataset[] _) in sets.Values)
            {
                store.Reset();
            }

            double cpu = ProcessCpu.Milliseconds();
            long begin = Stopwatch.GetTimestamp();
            long until = begin + (long)(settings.QpsSeconds * Stopwatch.Frequency);
            List<(int Question, double Milliseconds)>[] latencies = new List<(int, double)>[clients];
            Task[] running = new Task[clients];
            for (int c = 0; c < clients; c++)
            {
                int client = c;
                latencies[client] = [];
                running[client] = Task.Run(async () =>
                {
                    for (int turn = client; Stopwatch.GetTimestamp() < until; turn++)
                    {
                        Question question = questions[turn % questions.Count];
                        long asked = Stopwatch.GetTimestamp();
                        VortexDataset[] handles = sets[builtOf(question)].Datasets;
                        await AskOnceAsync(question, handles[client % handles.Length]);
                        latencies[client].Add((turn % questions.Count, (Stopwatch.GetTimestamp() - asked) * 1000.0 / Stopwatch.Frequency));
                    }
                });
            }

            await Task.WhenAll(running);
            double seconds = (Stopwatch.GetTimestamp() - begin) / (double)Stopwatch.Frequency;
            double busy = (ProcessCpu.Milliseconds() - cpu) / 1000.0;
            double[] all = [.. latencies.SelectMany(times => times).Select(time => time.Milliseconds).Order()];
            long requests = sets.Values.Sum(set => set.Store.Requests);
            long bytes = sets.Values.Sum(set => set.Store.BytesRead);
            Console.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"{(cached ? "warm" : "no cache"),-10} {clients,7} {all.Length,7} {all.Length / seconds,9:F1} {Percentile(all, 0.5),9:F1} {Percentile(all, 0.99),9:F1} {requests / seconds,9:F0} {bytes / seconds / 1e6,8:F1} {busy / seconds,6:F1}"));

            // With --qps-by-question, each question's asks and latencies under the load of all.
            if (settings.QpsByQuestion)
            {
                for (int q = 0; q < questions.Count; q++)
                {
                    double[] times = [.. latencies.SelectMany(asked => asked).Where(asked => asked.Question == q).Select(asked => asked.Milliseconds).Order()];
                    Console.WriteLine(string.Create(
                        CultureInfo.InvariantCulture,
                        $"  {questions[q].Name,-70} {times.Length,7} {Percentile(times, 0.5),9:F1} {Percentile(times, 0.99),9:F1}"));
                }
            }
        }
        finally
        {
            foreach ((FileObjectStore files, CountingObjectStore store, VortexDataset[] handles) in sets.Values)
            {
                foreach (VortexDataset handle in handles)
                {
                    await handle.DisposeAsync();
                }

                await store.DisposeAsync();
                await files.DisposeAsync();
            }

            if (Directory.Exists(cache))
            {
                Directory.Delete(cache, recursive: true);
            }
        }
    }

    /// <summary>Asks <paramref name="question"/> of <paramref name="dataset"/> at one lane, by its synopses where it aggregates, and reads its answer.</summary>
    private static async Task AskOnceAsync(Question question, VortexDataset dataset)
    {
        Asking asking = question.Ask(dataset, new ScanOptions { UseSynopses = question.Aggregates, DegreeOfParallelism = 1 });
        await using (asking.Batches)
        {
            while (await asking.Batches.MoveNextAsync())
            {
            }
        }
    }

    /// <summary>The value below which <paramref name="share"/> of the <paramref name="sorted"/> values lie; 0 for none.</summary>
    private static double Percentile(double[] sorted, double share) =>
        sorted.Length == 0 ? 0 : sorted[Math.Min(sorted.Length - 1, (int)Math.Floor(share * sorted.Length))];
}
