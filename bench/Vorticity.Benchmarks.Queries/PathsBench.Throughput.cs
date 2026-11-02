using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Vorticity.Dataset;

namespace Vorticity.Benchmarks.Queries;

internal static partial class PathsBench
{
    /// <summary>
    /// <c>--qps SECONDS</c>: the questions a node answers a second, those <c>--only</c> keeps asked in turn by each of
    /// <c>--clients</c> clients at once, each question at one lane and by its synopses where it aggregates, for so many
    /// seconds: warm, through one session whose synopsis cache holds every region after a first round of every question;
    /// then with no synopsis cache, every region read from the store, the requests a node asks of it; then, under
    /// <c>--qps-processes N</c>, in this process and N − 1 copies of it at once, over one cache shared by its root, each
    /// reading what the others kept. For each, the questions a second, the latencies' median and 99th percentile, the
    /// store's requests and bytes a second, and the cores the process kept busy; and for each question, its asks, its
    /// latencies under the load of all, and the bytes its first asking read.
    /// </summary>
    private static async Task ThroughputAsync(Settings settings, IReadOnlyList<Question> questions, Func<Question, Built> builtOf, string label)
    {
        // A copy the bench started runs the shared phase alone, over the root it was given; its first process reads its lines.
        if (settings.QpsPhase == "shared" && settings.SharedCache is { } given)
        {
            foreach (int clients in settings.Clients)
            {
                await ThroughputAsync(settings, questions, builtOf, Phase.Shared, clients, given);
            }

            return;
        }

        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"throughput: {questions.Count} questions asked in turn for {settings.QpsSeconds:0.#} s, each at one lane; {label}"));
        Console.WriteLine($"{"cache",-10} {"clients",7} {"asked",7} {"q/s",9} {"p50 ms",9} {"p99 ms",9} {"req/s",9} {"MB/s",8} {"cores",6}");
        foreach (Phase phase in (Phase[])[Phase.Warm, Phase.NoCache])
        {
            foreach (int clients in settings.Clients)
            {
                await ThroughputAsync(settings, questions, builtOf, phase, clients, settings.SharedCache);
            }
        }

        for (int c = 0; settings.QpsProcesses > 1 && c < settings.Clients.Length; c++)
        {
            await SharedAsync(settings, questions, builtOf, settings.Clients[c]);
        }
    }

    /// <summary>A phase of <c>--qps</c>: a cache of its own, none, or one shared with other processes by its root.</summary>
    private enum Phase
    {
        Warm,
        NoCache,
        Shared,
    }

    /// <summary>
    /// The shared phase at <paramref name="clients"/> clients: this process and <c>--qps-processes</c> − 1 copies of the
    /// bench, started with the same arguments, ask the questions at once over one synopsis cache shared by a root made for
    /// the run; the copies' lines printed after this one's, the root deleted after them.
    /// </summary>
    private static async Task SharedAsync(Settings settings, IReadOnlyList<Question> questions, Func<Question, Built> builtOf, int clients)
    {
        string root = Path.Combine(settings.Store, "cache", "shared-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        List<string> arguments = [];
        for (int i = 0; i < settings.Arguments.Length; i++)
        {
            // The options the copy is given anew, and their values, left out.
            if (settings.Arguments[i] is "--qps-processes" or "--qps-phase" or "--shared-cache" or "--clients")
            {
                i++;
                continue;
            }

            arguments.Add(settings.Arguments[i]);
        }

        arguments.AddRange(["--qps-phase", "shared", "--shared-cache", root, "--clients", clients.ToString(CultureInfo.InvariantCulture)]);
        List<(Process Process, Task<string> Output)> copies = [];
        try
        {
            for (int p = 1; p < settings.QpsProcesses; p++)
            {
                ProcessStartInfo start = new ProcessStartInfo(Environment.ProcessPath!) { RedirectStandardOutput = true, UseShellExecute = false };
                foreach (string argument in arguments)
                {
                    start.ArgumentList.Add(argument);
                }

                Process copy = Process.Start(start)!;
                copies.Add((copy, copy.StandardOutput.ReadToEndAsync()));
            }

            await ThroughputAsync(settings, questions, builtOf, Phase.Shared, clients, root);
            for (int p = 0; p < copies.Count; p++)
            {
                await copies[p].Process.WaitForExitAsync();
                foreach (string line in (await copies[p].Output).Split('\n'))
                {
                    if (line.StartsWith("shared", StringComparison.Ordinal) || line.StartsWith("  ", StringComparison.Ordinal))
                    {
                        Console.WriteLine($"  process {p + 2}: {line.TrimEnd()}");
                    }
                }
            }
        }
        finally
        {
            foreach ((Process copy, _) in copies)
            {
                copy.Dispose();
            }

            DeleteCache(root);
        }
    }

    private static async Task ThroughputAsync(
        Settings settings, IReadOnlyList<Question> questions, Func<Question, Built> builtOf, Phase phase, int clients, string? sharedRoot)
    {
        string cache = Path.Combine(settings.Store, "cache", Guid.NewGuid().ToString("N"));
        Dictionary<Built, (FileObjectStore Files, CountingObjectStore Store, VortexDataset[] Datasets)> sets = [];
        VortexSession? session = null;
        try
        {
            Settings.Cold();
            session = VortexSession.Create(o =>
            {
                o.MaxConcurrentReads = settings.MaxReads > 0 ? settings.MaxReads : o.MaxConcurrentReads;
                o.SynopsisCache = phase == Phase.NoCache ? null
                    : sharedRoot is { } root ? SynopsisCache.OpenShared(root, 8L << 30)
                    : new SynopsisCache(cache, 8L << 30);
                o.SegmentCache = settings.SegmentCache();
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

            // Every question once first: their code compiled, and, cached, every region they read held; the bytes the first
            // asking of each read, alone.
            long[] first = new long[questions.Count];
            for (int q = 0; q < questions.Count; q++)
            {
                (_, CountingObjectStore store, VortexDataset[] handles) = sets[builtOf(questions[q])];
                long before = store.BytesRead;
                await AskOnceAsync(questions[q], handles[0]);
                first[q] = store.BytesRead - before;
                for (int h = 1; h < handles.Length; h++)
                {
                    await AskOnceAsync(questions[q], handles[h]);
                }
            }

            // Then each in turn, untimed, for as long as --warm says, and the JIT's recompilations done: a phase timed right after
            // one ask of each ran their code at the JIT's first tier at its start, where the phase after it did not, 266
            // questions a second warm against 366 with no synopsis cache, which costs a question 1 to 2 % (2026-11-02).
            long warmUntil = Stopwatch.GetTimestamp() + (long)(settings.WarmSeconds * Stopwatch.Frequency);
            for (int turn = 0; Stopwatch.GetTimestamp() < warmUntil; turn++)
            {
                Question question = questions[turn % questions.Count];
                await AskOnceAsync(question, sets[builtOf(question)].Datasets[0]);
            }

            await JitCalmAsync();
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
            string name = phase switch { Phase.Warm => "warm", Phase.NoCache => "no cache", _ => "shared" };
            Console.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"{name,-10} {clients,7} {all.Length,7} {all.Length / seconds,9:F1} {Percentile(all, 0.5),9:F1} {Percentile(all, 0.99),9:F1} {requests / seconds,9:F0} {bytes / seconds / 1e6,8:F1} {busy / seconds,6:F1}"));

            // A cache that shares a root says what it read from the directories of the others, and what it kept itself.
            if (sharedRoot is not null && session.Options.SynopsisCache is { } shared)
            {
                Console.WriteLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"  shared cache {shared.Directory}: {shared.PeerHits} reads from the other directories, {shared.Hits} hits, {shared.Misses} misses, {shared.HeldBytes / 1e6:F1} MB kept"));
            }

            // Each question's asks and latencies under the load of all, and the bytes its first asking read.
            for (int q = 0; q < questions.Count; q++)
            {
                double[] times = [.. latencies.SelectMany(asked => asked).Where(asked => asked.Question == q).Select(asked => asked.Milliseconds).Order()];
                Console.WriteLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"  {questions[q].Name,-70} {times.Length,7} {Percentile(times, 0.5),9:F1} {Percentile(times, 0.99),9:F1} {first[q] / 1e3,10:F1} KB first"));
            }
        }
        finally
        {
            // The handles first, then the stores, then the session and its cache, which closes the cache's files: its
            // directory is deleted once nothing holds them.
            foreach ((FileObjectStore files, CountingObjectStore store, VortexDataset[] handles) in sets.Values)
            {
                foreach (VortexDataset handle in handles)
                {
                    await handle.DisposeAsync();
                }

                await store.DisposeAsync();
                await files.DisposeAsync();
            }

            if (session is not null)
            {
                await session.DisposeAsync();
            }

            DeleteCache(cache);
        }
    }

    /// <summary>
    /// Deletes the cache directory <paramref name="cache"/>, if any, once its files are closed: a view a finalizer has not
    /// yet released keeps one open a moment, so a refused delete waits for the finalizers and tries again, then says so.
    /// </summary>
    private static void DeleteCache(string cache)
    {
        for (int attempt = 0; Directory.Exists(cache); attempt++)
        {
            try
            {
                Directory.Delete(cache, recursive: true);
            }
            catch (Exception refused) when (refused is IOException or UnauthorizedAccessException)
            {
                if (attempt == 3)
                {
                    Console.WriteLine($"  the cache {cache} was left: {refused.Message}");
                    return;
                }

                GC.Collect();
                GC.WaitForPendingFinalizers();
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
