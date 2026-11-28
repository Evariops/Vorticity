using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Buffers;
using Vorticity.Dataset;
using Vorticity.IO;

namespace Vorticity.Benchmarks.Queries;

/// <summary>
/// <c>--micro host</c>: the library inside a host that answers for a long while, which is where the pinned heap of the
/// collector shows what a short bench never does. Read under each collector by <c>bench/host.sh</c>.
/// </summary>
/// <remarks>
/// <para>
/// <c>--datasets</c> datasets (4), sealed under one key, each appended <c>--objects</c> objects (6) of <c>--object-rows</c>
/// log records (100,000), whose attributes are variants. Then, for <c>--minutes</c> (6): a follower that reads every row of
/// every dataset, attributes included, every <c>--period</c> seconds (5), through handles it keeps; <c>--clients</c> page
/// clients (3), each asking a count by service of a dataset after another, without a pause; and the host's own work, which
/// pins a block of 4 KiB for each request it buffers, 256 of them every 10 seconds, keeps one in a thousand for good, and
/// makes <c>--garbage</c> MiB a second (64) of short-lived objects, as a service answering requests does.
/// </para>
/// <para>
/// Every <c>--sample</c> seconds (30), a line: the pinned heap and its free space after the last full collection, the pinned
/// bytes and the slabs of the library, the full collections and the time paused, the ceiling, the rest and the work's
/// reservations, the reads done and refused. At the end, the free space of the pinned heap in each half of the run, whose
/// growth is what the host would see over hours. The scenario reads the collector, the report and the slabs' count, which
/// the library had before this scenario was written, so that it runs unchanged on an older tree to compare.
/// </para>
/// </remarks>
internal static class HostScenario
{
    private static readonly DateTimeOffset Start = new DateTimeOffset(2026, 11, 24, 0, 0, 0, TimeSpan.Zero);

    internal static async Task<int> RunAsync(
        string root, int datasets, int objects, int objectRows, int minutes, int period, int clients, int latency, int garbage, int sample)
    {
        Fresh(root);
        IReadOnlyDictionary<string, object> settings = GC.GetConfigurationVariables();
        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"host: server collector {GCSettings.IsServerGC}, {settings.GetValueOrDefault("HeapCount")} heaps, dynamic adaptation {settings.GetValueOrDefault("GCDynamicAdaptationMode")}, heap limit {Mib(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes)} MiB; {datasets} datasets of {objects} objects of {objectRows} rows, {minutes} min"));

        using VortexKeyring keyring = VortexKeyring.FromKeys(new VortexKey("host", new byte[32]));
        await using VortexSession session = VortexSession.Create(options => options.Keyring = keyring);
        List<VortexDataset> handles = [];
        LogGenerator generator = new LogGenerator(41);
        Stopwatch setup = Stopwatch.StartNew();
        for (int d = 0; d < datasets; d++)
        {
            LatencyObjectStore store = new LatencyObjectStore(new FileObjectStore(Path.Combine(root, $"dataset-{d}"))) { Latency = TimeSpan.FromMilliseconds(latency) };
            await using (VortexDataset writer = await VortexDataset.CreateAsync(store, LogRow.Schema, new DatasetOptions
            {
                Session = session,
                Encrypted = true,
                Layout = new DatasetLayout { Partition = LayoutValue.Column(nameof(LogRow.At)).Truncate(CalendarUnit.Day), SortKey = [LayoutValue.Column(nameof(LogRow.ServiceName)), LayoutValue.Column(nameof(LogRow.At))] },
            }))
            {
                LogRow[] batch = new LogRow[objectRows];
                for (int o = 0; o < objects; o++)
                {
                    for (int i = 0; i < batch.Length; i++)
                    {
                        batch[i] = generator.Next(Start.AddHours(o).AddTicks(i * 10_000L));
                    }

                    await using ObjectDraft draft = writer.StartObject();
                    await draft.Writer.WriteAsync<LogRow>(batch);
                    await writer.AppendAsync(draft);
                }
            }

            handles.Add(await VortexDataset.OpenAsync(store, new DatasetOptions { Session = session }));
        }

        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"host: written in {setup.Elapsed.TotalSeconds:F1} s"));
        Counts counts = new Counts();
        using CancellationTokenSource stop = new CancellationTokenSource(TimeSpan.FromMinutes(minutes));
        Task follower = FollowAsync(handles, period, counts, stop.Token);
        Task[] pages = [.. Enumerable.Range(0, clients).Select(client => PagesAsync(handles, client, counts, stop.Token))];
        Task host = HostAsync(garbage, stop.Token);

        Console.WriteLine("s\tpoh_MiB\tpoh_free_MiB\tslabs_MiB\tslabs_made\tgen2\tpaused_pct\tceiling_MiB\trest_MiB\treserved_MiB\tfollower_passes\tquestions\trefused");
        List<(double Seconds, long PinnedFree)> samples = [];
        Stopwatch clock = Stopwatch.StartNew();
        long slabsBefore = Slabs.Slab.Made;
        int gen2Before = GC.CollectionCount(2);
        TimeSpan pausedBefore = GC.GetTotalPauseDuration();
        double last = 0;
        while (!stop.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(sample), stop.Token);
            }
            catch (OperationCanceledException)
            {
            }

            double now = clock.Elapsed.TotalSeconds;
            GCGenerationInfo pinned = GC.GetGCMemoryInfo(GCKind.Any).GenerationInfo[4];
            MemoryReport report = VortexDiagnostics.GetMemoryReport();
            long slabsMade = Slabs.Slab.Made;
            int gen2 = GC.CollectionCount(2);
            TimeSpan paused = GC.GetTotalPauseDuration();
            samples.Add((now, pinned.FragmentationAfterBytes));
            Console.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"{now:F0}\t{Mib(pinned.SizeAfterBytes)}\t{Mib(pinned.FragmentationAfterBytes)}\t{Mib(Slabs.HeldBytes)}\t{slabsMade - slabsBefore}\t{gen2 - gen2Before}\t{100 * (paused - pausedBefore).TotalSeconds / Math.Max(0.001, now - last):F1}\t{Mib(report.CeilingBytes)}\t{Mib(report.RestBytes)}\t{Mib(report.ReservedBytes)}\t{counts.FollowerPasses}\t{counts.Questions}\t{counts.Refused}"));
            slabsBefore = slabsMade;
            gen2Before = gen2;
            pausedBefore = paused;
            last = now;
        }

        await Task.WhenAll([follower, host, .. pages]);
        foreach (VortexDataset handle in handles)
        {
            await handle.DisposeAsync();
        }

        int half = samples.Count / 2;
        double first = half == 0 ? 0 : samples.Take(half).Average(s => s.PinnedFree);
        double second = samples.Count - half == 0 ? 0 : samples.Skip(half).Average(s => s.PinnedFree);
        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"host: pinned heap free {Mib((long)first)} MiB in the first half, {Mib((long)second)} MiB in the second; {counts.FollowerPasses} follower passes, {counts.Questions} questions, {counts.Refused} refused, {counts.Failed} failed otherwise; {Slabs.Slab.Made} slabs made, {GC.CollectionCount(2)} full collections, {GC.GetTotalPauseDuration().TotalSeconds:F1} s paused"));
        return 0;
    }

    /// <summary>The follower: every row of every dataset, attributes included, every <paramref name="period"/> seconds.</summary>
    private static async Task FollowAsync(List<VortexDataset> handles, int period, Counts counts, CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            Stopwatch pass = Stopwatch.StartNew();
            foreach (VortexDataset handle in handles)
            {
                await counts.RunAsync(async () =>
                {
                    await handle.RefreshAsync(stop);
                    long rows = 0;
                    await foreach (LogRow row in handle.Scan<LogRow>().ToRecordsAsync().WithCancellation(stop))
                    {
                        rows++;
                    }

                    return rows;
                });
            }

            Interlocked.Increment(ref counts.FollowerPasses);
            TimeSpan rest = TimeSpan.FromSeconds(period) - pass.Elapsed;
            if (rest > TimeSpan.Zero)
            {
                try
                {
                    await Task.Delay(rest, stop);
                }
                catch (OperationCanceledException)
                {
                }
            }
        }
    }

    /// <summary>A page client: a count by service of one dataset after another, without a pause.</summary>
    private static async Task PagesAsync(List<VortexDataset> handles, int client, Counts counts, CancellationToken stop)
    {
        for (int i = client; !stop.IsCancellationRequested; i++)
        {
            VortexDataset handle = handles[i % handles.Count];
            await counts.RunAsync(async () => (long)(await handle.Scan<LogRow>().GroupBy(r => r.ServiceName).Select(g => (g.Key, g.Count()))
                .As<ServiceRows>().ToRecordsAsync().ToListAsync()).Count);
            Interlocked.Increment(ref counts.Questions);
        }
    }

    /// <summary>
    /// The host's own work: a pinned block of 4 KiB a request it buffers, 256 every 10 seconds, one in a thousand kept for
    /// good, as a web server's pool of pinned blocks does; and <paramref name="garbage"/> MiB a second of short-lived objects.
    /// </summary>
    private static async Task HostAsync(int garbage, CancellationToken stop)
    {
        List<byte[]> kept = [];
        long made = 0;
        Stopwatch wave = Stopwatch.StartNew();
        while (!stop.IsCancellationRequested)
        {
            if (wave.Elapsed >= TimeSpan.FromSeconds(10) || made == 0)
            {
                wave.Restart();
                byte[][] buffered = new byte[256][];
                for (int i = 0; i < buffered.Length; i++)
                {
                    buffered[i] = GC.AllocateUninitializedArray<byte>(4096, pinned: true);
                    if (++made % 1000 == 0)
                    {
                        kept.Add(buffered[i]);
                    }
                }

                GC.KeepAlive(buffered);
            }

            // A tenth of a second's garbage, in objects of 1 KiB.
            int pieces = garbage * 1024 / 10;
            for (int i = 0; i < pieces; i++)
            {
                GC.KeepAlive(new byte[1024]);
            }

            try
            {
                await Task.Delay(100, stop);
            }
            catch (OperationCanceledException)
            {
            }
        }

        GC.KeepAlive(kept);
    }

    private static void Fresh(string root)
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }

        Directory.CreateDirectory(root);
    }

    private static string Mib(long bytes) => (bytes / (1024.0 * 1024)).ToString("F0", CultureInfo.InvariantCulture);

    /// <summary>What the readers did: passes, questions, refusals and other failures, counted as they end.</summary>
    private sealed class Counts
    {
        internal long FollowerPasses;
        internal long Questions;
        internal long Refused;
        internal long Failed;

        /// <summary>Runs one read: a refusal of memory is counted and the read given up, as the host's follower and pages do.</summary>
        internal async Task RunAsync(Func<Task<long>> read)
        {
            try
            {
                _ = await read();
            }
            catch (VortexMemoryException)
            {
                Interlocked.Increment(ref Refused);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                if (Interlocked.Increment(ref Failed) <= 3)
                {
                    Console.Error.WriteLine($"host: a read failed: {e.GetType().Name}: {e.Message}");
                }
            }
        }
    }
}
