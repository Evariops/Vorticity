using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Buffers;
using Vorticity.Dataset;
using Vorticity.IO;

namespace Vorticity.Benchmarks.Queries;

/// <summary>
/// A maintainer that comes back to a backlog (<c>--micro backlog</c>): <c>--appends</c> appends (300) of <c>--rows</c> metric
/// records each (30 000), every one spanning the keys of every series, as the flushes of an ingestion do, none compacted;
/// then the maintainer's loop, a refresh and a compaction step at a time until none is due. Each job prints what it took,
/// the most its blocks held, and what the process held, by sort; and every ten seconds, what the process holds then.
/// </summary>
/// <remarks>
/// <para>
/// <c>--memory-limit SIZE</c>, such as <c>2g</c>, holds the process to that limit as a container's would
/// (<see cref="VortexMemory.SetLimit"/>): the collector's heap to three quarters of it, the jobs' budget to its share. <c>--series</c> sets the series (20 000), <c>--store DIR</c> where the dataset lies, and
/// <c>--compact-only</c> compacts the backlog an earlier run left there, copied first so that it stays whole. With
/// <c>--bytes SIZE</c>, such as <c>25g</c>, the appends go on until the backlog weighs that much, whatever <c>--appends</c>
/// says. <c>--degree N</c> sets the lanes the session lends a job, one a core without it. Each job's line says its time and
/// the processor time the process spent meanwhile, which says how many cores the job held. The last line says the refusals,
/// the bytes the jobs read and wrote, and the bytes written over the backlog's.
/// </para>
/// <para>
/// The records are a gauge's, as an ingestion of metrics writes them: a service, a metric, the hash of the series'
/// attributes, an instant and a value, and two variant columns, the series' attributes and its service's resource
/// attributes, constant along a series, which a job's decoders expand row by row.
/// </para>
/// </remarks>
internal static class BacklogBench
{
    private static readonly DateTimeOffset Start = new DateTimeOffset(2026, 11, 15, 0, 0, 0, TimeSpan.Zero);

    // The jobs given up since the last job ran to its end, the budget refusing them or their price: the library's own count
    // of compactions that failed, read on its meter.
    private static long s_refused;

    private static readonly MeterListener Refusals = Listen();

    private static MeterListener Listen()
    {
        MeterListener listener = new MeterListener
        {
            InstrumentPublished = static (instrument, listening) =>
            {
                if (instrument.Meter.Name == VortexDiagnostics.MeterName && instrument.Name == "vortex.dataset.compactions")
                {
                    listening.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<long>(static (_, value, tags, _) =>
        {
            foreach (KeyValuePair<string, object?> tag in tags)
            {
                if (tag.Key == "vortex.outcome" && tag.Value is "failed")
                {
                    Interlocked.Add(ref s_refused, value);
                }
            }
        });
        listener.Start();
        return listener;
    }

    internal static async Task<int> RunAsync(string root, int appends, int rows, int series, long memoryLimit, bool compactOnly, int latency, int scans = 0, bool buildOnly = false, long bytes = 0, int degree = 0, bool digest = false)
    {
        if (memoryLimit > 0)
        {
            VortexMemory.SetLimit(memoryLimit);
        }

        string backlog = Path.Combine(root, "backlog");
        string work = Path.Combine(root, "work");
        using VortexKeyring keyring = VortexKeyring.FromKeys(new VortexKey("backlog", new byte[32]));

        // --degree N: the lanes the session lends a job, as a host sets them; the session's default, one a core, without it.
        await using VortexSession session = VortexSession.Create(options =>
        {
            options.Keyring = keyring;
            if (degree > 0)
            {
                options.MaxDegreeOfParallelism = degree;
            }
        });
        using Sampler sampler = new Sampler();
        Stopwatch clock = Stopwatch.StartNew();

        if (!compactOnly)
        {
            Fresh(backlog);
            (long read, int made) = await BuildAsync(session, backlog, appends, rows, series, scans, bytes);
            Console.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"backlog of {made} appends of {rows} rows, {BytesOf(backlog) / 1e6:F0} MB, built in {clock.Elapsed.TotalSeconds:F0} s, {scans} scans reading {read:N0} rows meanwhile; {sampler.Peaks()}; {Sampler.Now()}"));
            if (buildOnly)
            {
                return 0;
            }
        }

        // The backlog stays whole: the maintainer compacts a copy of it.
        Fresh(work);
        foreach (string file in Directory.EnumerateFiles(backlog, "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(work, Path.GetRelativePath(backlog, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            System.IO.File.Copy(file, target);
        }

        sampler.Reset();
        clock.Restart();
        using Timer timeline = new Timer(
            _ => Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"[{clock.Elapsed.TotalSeconds,5:F0} s] {Sampler.Now()}")),
            null,
            10_000,
            10_000);

        IObjectStore store = new LatencyObjectStore(new FileObjectStore(work)) { Latency = TimeSpan.FromMilliseconds(latency) };
        await using VortexDataset maintainer = await VortexDataset.OpenAsync(store, new DatasetOptions { Session = session, Encrypted = true });
        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"maintainer on {maintainer.RowCount:N0} rows in {maintainer.ObjectCount} objects, lag {maintainer.Lag}; jobs' budget {Mib(QueryMemoryBudget.JobsOf(session).CeilingBytes)} MiB of a limit of {Mib(VortexDiagnostics.GetMemoryReport().LimitBytes)} MiB; degree {session.Options.MaxDegreeOfParallelism}"));
        using Process process = Process.GetCurrentProcess();
        int jobs = 0;
        long mostHeld = 0;
        long backlogBytes = BytesOf(work);
        long bytesRead = 0;
        long bytesWritten = 0;
        long refusals = 0;
        while (true)
        {
            sampler.Reset();
            Interlocked.Exchange(ref s_refused, 0);
            Stopwatch job = Stopwatch.StartNew();
            process.Refresh();
            TimeSpan processor = process.TotalProcessorTime;
            long lanes = session.WriteLanes.HeldTicks;
            long slabsMade = Slabs.Slab.Made;
            int fullCollections = GC.CollectionCount(2);
            await maintainer.RefreshAsync();
            CompactionResult? result = await maintainer.CompactAsync();
            if (result is null)
            {
                break;
            }

            jobs++;
            process.Refresh();
            double processorSeconds = (process.TotalProcessorTime - processor).TotalSeconds;
            double laneSeconds = (double)(session.WriteLanes.HeldTicks - lanes) / Stopwatch.Frequency;
            mostHeld = Math.Max(mostHeld, result.MemoryReserved);
            bytesRead += result.BytesIn;
            bytesWritten += result.BytesOut;
            refusals += Interlocked.Read(ref s_refused);
            Console.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"job {jobs}: {result.Trigger} L{result.FromLevel}->L{result.ToLevel}, {result.ObjectsIn} -> {result.ObjectsOut} objects ({result.ObjectsMoved} moved), {result.Rows:N0} rows, {result.BytesIn / 1e6:F1} -> {result.BytesOut / 1e6:F1} MB, blocks held {Mib(result.MemoryReserved)} MiB, {result.RangesAtOnce} at once, {Interlocked.Read(ref s_refused)} refused before, scratch {Mib(result.ScratchBytes)} MiB, {job.Elapsed.TotalSeconds:F1} s, {processorSeconds:F1} s of processor, {laneSeconds:F1} s of write lanes, {result.WriterBatches:N0} writer batches of {(double)result.Rows / Math.Max(1, result.WriterBatches):F0} rows, {result.WriterBatchesAcross:N0} across, {result.WriterBatchesFlown:N0} flown, {GC.CollectionCount(2) - fullCollections} full collections, {Slabs.Slab.Made - slabsMade} slabs made; {sampler.Peaks()}"));
        }

        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"done: {jobs} jobs in {clock.Elapsed.TotalSeconds:F0} s, {maintainer.ObjectCount} objects left, the most a job's blocks held {Mib(mostHeld)} MiB, {refusals} refused; read {bytesRead / 1e6:F0} MB and wrote {bytesWritten / 1e6:F0} MB of a backlog of {backlogBytes / 1e6:F0} MB, amplification {(double)bytesWritten / Math.Max(1, backlogBytes):F2}; {Sampler.Now()}"));
        if (digest)
        {
            Console.WriteLine(await DigestAsync(maintainer));
        }

        // At rest: two turns of the slow timer, which an idle process gets within a minute, then a full collection. What the
        // library keeps then, what the questions are left, and the holes of the pinned heap, which a hard limit counts.
        HeldMemory.LetGoUnused(collection: false);
        GC.Collect();
        HeldMemory.LetGoUnused(collection: false);
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();

        // Aggressive, as the budget's own collection is when a question needs the room: the collector gives back the regions
        // it keeps committed past its heap.
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);

        // The budget reads the collection as a question's first reservation would, before the report.
        _ = QueryMemoryBudget.Process.CanReserve(0);
        GCGenerationInfo pinned = GC.GetGCMemoryInfo(GCKind.FullBlocking).GenerationInfo[4];
        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"rest: {Sampler.Now()}; pinned heap {Mib(pinned.SizeAfterBytes)} MiB, {Mib(pinned.FragmentationAfterBytes)} of it free"));
        return 0;
    }

    /// <summary>
    /// The backlog: appends of metric records each spanning every series' keys, none compacted; while <paramref name="scans"/>
    /// readers scan the dataset in batches, each again and again from its latest version, as the readers of a pipeline's
    /// source would. The rows they read.
    /// </summary>
    private static async Task<(long Read, int Appends)> BuildAsync(VortexSession session, string root, int appends, int rows, int series, int scans, long bytes)
    {
        IObjectStore store = new FileObjectStore(root);
        DatasetOptions options = new DatasetOptions
        {
            Session = session,
            Encrypted = true,
            Layout = new DatasetLayout
            {
                Partition = LayoutValue.Column(nameof(MetricRow.TimeUnix)).Truncate(CalendarUnit.Day),
                SortKey =
                [
                    LayoutValue.Column(nameof(MetricRow.ServiceName)), LayoutValue.Column(nameof(MetricRow.MetricName)),
                    LayoutValue.Column(nameof(MetricRow.AttributesHash)), LayoutValue.Column(nameof(MetricRow.TimeUnix)),
                ],
            },
        };
        await using VortexDataset writer = await VortexDataset.CreateAsync(store, MetricRow.Schema, options);

        using CancellationTokenSource built = new CancellationTokenSource();
        long read = 0;
        Task[] readers = [.. Enumerable.Range(0, scans).Select(_ => Task.Run(async () =>
        {
            await using VortexDataset reader = await VortexDataset.OpenAsync(store, options);
            try
            {
                while (true)
                {
                    await reader.RefreshAsync(built.Token);
                    await foreach (RecordBatch batch in reader.Scan<MetricRow>().ToBatchesAsync().WithCancellation(built.Token))
                    {
                        Interlocked.Add(ref read, batch.RowCount);
                        batch.Dispose();
                    }
                }
            }
            catch (OperationCanceledException) when (built.IsCancellationRequested)
            {
            }
        }))];

        MetricGenerator generator = new MetricGenerator(series, 31);
        MetricRow[] batch = new MetricRow[rows];
        int a = 0;
        for (; bytes > 0 || a < appends; a++)
        {
            // Past --bytes, the backlog is built: its size read every fifty appends.
            if (bytes > 0 && a % 50 == 0 && a > 0 && BytesOf(root) >= bytes)
            {
                break;
            }

            // An append is a flush of a few seconds: a point of each series it reaches, in arrival order.
            DateTimeOffset at = Start.AddSeconds(a * 10.0);
            for (int i = 0; i < rows; i++)
            {
                batch[i] = generator.Next(at.AddTicks(i % TimeSpan.TicksPerSecond));
            }

            await using ObjectDraft draft = writer.StartObject();
            await draft.Writer.WriteAsync<MetricRow>(batch);
            await writer.AppendAsync(draft);
        }

        await built.CancelAsync();
        await Task.WhenAll(readers);
        return (Interlocked.Read(ref read), a);
    }

    /// <summary>
    /// What the compacted dataset holds, whatever order its rows come in: their count, and the sum and the exclusive or of a
    /// hash of each, every field in it, the variants as their JSON. Two runs that wrote the same rows print the same line.
    /// </summary>
    private static async Task<string> DigestAsync(VortexDataset dataset)
    {
        long rows = 0;
        ulong sum = 0;
        ulong xor = 0;
        byte[] buffer = new byte[4096];
        await foreach (MetricRow row in dataset.Scan<MetricRow>().ToRecordsAsync())
        {
            string text = string.Create(
                CultureInfo.InvariantCulture,
                $"{row.TimeUnix.UtcTicks}\u0001{row.ServiceName}\u0001{row.MetricName}\u0001{row.AttributesHash}\u0001{BitConverter.DoubleToInt64Bits(row.Value)}\u0001{row.Attributes.ToJson()}\u0001{row.ResourceAttributes.ToJson()}");
            int length = System.Text.Encoding.UTF8.GetMaxByteCount(text.Length);
            if (length > buffer.Length)
            {
                buffer = new byte[length];
            }

            ulong hash = Vorticity.Aggregating.CardinalitySlot.Hash(buffer.AsSpan(0, System.Text.Encoding.UTF8.GetBytes(text, buffer)));
            sum += hash;
            xor ^= hash;
            rows++;
        }

        return string.Create(CultureInfo.InvariantCulture, $"digest: {rows:N0} rows, sum {sum:x16}, xor {xor:x16}");
    }

    /// <summary>The bytes of every file under <paramref name="root"/>.</summary>
    private static long BytesOf(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Sum(file => new FileInfo(file).Length);

    private static void Fresh(string root)
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }

        Directory.CreateDirectory(root);
    }

    private static string Mib(long bytes) => (bytes / (1024.0 * 1024)).ToString("F0", CultureInfo.InvariantCulture);

    /// <summary>
    /// The process's resident bytes, the blocks alive, those the pool keeps, and the heap past the blocks, sampled every 50 ms,
    /// and their peaks since the last reset.
    /// </summary>
    private sealed class Sampler : IDisposable
    {
        private readonly Timer _timer;
        private readonly LargeBlocks? _large;
        private long _resident;
        private long _blocks;
        private long _parked;
        private long _slabs;
        private long _free;
        private long _beside;
        private long _native;
        private long _growth;

        // The resident bytes of the last second's samples, one every 50 ms: how much faster the process grows than a reading of
        // its container's memory, a second apart, sees.
        private readonly long[] _second = new long[20];
        private int _at;

        internal Sampler()
        {
            if (BlockPool.Watch is null)
            {
                _large = new LargeBlocks();
                BlockPool.Watch = _large;
            }

            _timer = new Timer(_ => Sample(), null, 0, 50);
        }

        internal void Reset()
        {
            Interlocked.Exchange(ref _resident, 0);
            Interlocked.Exchange(ref _blocks, 0);
            Interlocked.Exchange(ref _parked, 0);
            Interlocked.Exchange(ref _slabs, 0);
            Interlocked.Exchange(ref _free, 0);
            Interlocked.Exchange(ref _beside, 0);
            Interlocked.Exchange(ref _native, 0);
            Interlocked.Exchange(ref _growth, 0);
            _large?.Reset();
        }

        internal string Peaks() => string.Create(
            CultureInfo.InvariantCulture,
            $"peaks: resident {Mib(Interlocked.Read(ref _resident))} MiB, blocks {Mib(Interlocked.Read(ref _blocks))}, of which the pool keeps {Mib(Interlocked.Read(ref _parked))} and past 16 MiB are rented {Mib(_large?.Peak ?? 0)}, slabs {Mib(Interlocked.Read(ref _slabs))} of which free {Mib(Interlocked.Read(ref _free))}, heap beside the blocks and slabs {Mib(Interlocked.Read(ref _beside))}, resident past the committed heap {Mib(Interlocked.Read(ref _native))}, resident nothing counts grown in a second {Mib(Interlocked.Read(ref _growth))}");

        /// <summary>The pinned bytes of the blocks: the slabs whole, and the blocks of an array of their own.</summary>
        private static long Pinned() => Slabs.HeldBytes + BlockOwner.LiveBytes - Slabs.PieceBytes;

        /// <summary>What the process and the library hold now.</summary>
        internal static string Now()
        {
            MemoryReport report = VortexDiagnostics.GetMemoryReport();
            long blocks = BlockOwner.LiveBytes;
            string kept = string.Join(" ", report.RetainedByHolder.Where(h => h.Value > 0).OrderBy(h => h.Key, StringComparer.Ordinal).Select(h => $"{h.Key} {Mib(h.Value)}"));
            return string.Create(
                CultureInfo.InvariantCulture,
                $"now: resident {Mib(MemoryPeaks.Read().Working)} MiB, blocks {Mib(blocks)}, slabs {Mib(Slabs.HeldBytes)} of which free {Mib(Slabs.FreeBytes)}, heap beside them {Mib(GC.GetTotalMemory(false) - Pinned())}; reserved {Mib(report.ReservedBytes)}, rented {Mib(report.RentedBytes)}, kept {Mib(report.RetainedBytes)} [{kept}], rest {Mib(report.RestBytes)}, ceiling {Mib(report.CeilingBytes)} of {Mib(report.LimitBytes)}");
        }

        public void Dispose()
        {
            _timer.Dispose();
            if (_large is not null && ReferenceEquals(BlockPool.Watch, _large))
            {
                BlockPool.Watch = null;
            }
        }

        /// <summary>
        /// The bytes of the blocks past the slabs' largest piece rented and not given back, each a pinned array of its own,
        /// and their peak since the last reset: what decides whether they go into slabs too.
        /// </summary>
        private sealed class LargeBlocks : BlockPool.IBlockWatch
        {
            private long _live;
            private long _peak;

            internal long Peak => Interlocked.Read(ref _peak);

            internal void Reset() => Interlocked.Exchange(ref _peak, Interlocked.Read(ref _live));

            public void Rented(BlockOwner owner)
            {
                if (owner.Capacity > Slabs.LargestBytes)
                {
                    Raise(ref _peak, Interlocked.Add(ref _live, owner.Capacity));
                }
            }

            public void Released(BlockOwner owner)
            {
                if (owner.Capacity > Slabs.LargestBytes)
                {
                    Interlocked.Add(ref _live, -owner.Capacity);
                }
            }
        }

        private void Sample()
        {
            long blocks = BlockOwner.LiveBytes;
            long resident = MemoryPeaks.Read().Working;
            Raise(ref _resident, resident);
            Raise(ref _native, resident - GC.GetGCMemoryInfo().TotalCommittedBytes);

            // What no reservation and no holder of kept memory counts: what a reading of the container's memory, a second
            // apart, learns of late, and its margin covers. The timer's callbacks never overlap: it fires again only 50 ms
            // after, and a sample takes far less.
            long unaccounted = resident - QueryMemoryBudget.Process.ReservedBytes - HeldMemory.TotalBytes();
            long before = _second[_at];
            _second[_at] = unaccounted;
            _at = (_at + 1) % _second.Length;
            if (before != 0)
            {
                Raise(ref _growth, unaccounted - before);
            }

            Raise(ref _blocks, blocks);
            Raise(ref _parked, MemoryAccount.Pool.ParkedBytes);
            Raise(ref _slabs, Slabs.HeldBytes);
            Raise(ref _free, Slabs.FreeBytes);
            Raise(ref _beside, GC.GetTotalMemory(false) - Pinned());
        }

        private static void Raise(ref long peak, long value)
        {
            long seen = Interlocked.Read(ref peak);
            while (value > seen && Interlocked.CompareExchange(ref peak, value, seen) != seen)
            {
                seen = Interlocked.Read(ref peak);
            }
        }
    }
}

/// <summary>
/// The points of a gauge's series, drawn deterministically: <c>series</c> series of a service and a metric, each with
/// attributes of its own, constant along it, and its service's resource attributes; a point of each series in turn.
/// </summary>
internal sealed class MetricGenerator
{
    private static readonly string[] Metrics =
    [
        "http.server.request.duration", "http.server.active_requests", "process.cpu.utilization", "process.memory.usage",
        "db.client.connections.usage", "jvm.gc.duration", "messaging.process.duration", "rpc.server.duration",
    ];

    private readonly (string Service, string Metric, ulong Hash, Variant Attributes, Variant Resource)[] _series;
    private readonly Random _random;
    private int _next;

    internal MetricGenerator(int series, int seed)
    {
        _random = new Random(seed);
        int services = Math.Max(1, series / 400);
        Variant[] resources = new Variant[services];
        for (int s = 0; s < services; s++)
        {
            string name = $"svc-{s:D3}";
            resources[s] = Variant.Object(
            [
                ("k8s.cluster.name", Variant.From("turing")),
                ("k8s.namespace.name", Variant.From($"team-{s % 17}")),
                ("k8s.deployment.name", Variant.From(name)),
                ("k8s.pod.name", Variant.From($"{name}-{_random.Next():x8}-{_random.Next(1 << 16):x4}")),
                ("k8s.pod.uid", Variant.From($"{_random.NextInt64():x16}-{_random.NextInt64():x16}")),
                ("k8s.node.name", Variant.From($"node-{s % 23:D2}")),
                ("k8s.container.name", Variant.From(name)),
                ("deployment.environment.name", Variant.From("production")),
            ]);
        }

        _series = new (string, string, ulong, Variant, Variant)[series];
        for (int i = 0; i < series; i++)
        {
            int service = i % services;
            string metric = Metrics[(i / services) % Metrics.Length];
            Variant attributes = Variant.Object(
            [
                ("http.route", Variant.From($"/api/v{i % 3}/r{i % 211}/{{id}}")),
                ("http.request.method", Variant.From(i % 5 == 0 ? "POST" : "GET")),
                ("http.response.status_code", Variant.From((long)(i % 11 == 0 ? 500 : 200))),
                ("server.address", Variant.From($"{i % 97}.svc.cluster.local")),
                ("network.protocol.version", Variant.From("1.1")),
                ("otel.scope.name", Variant.From($"io.opentelemetry.instrumentation-{i % 7}")),
            ]);
            _series[i] = ($"svc-{service:D3}", metric, (ulong)_random.NextInt64(), attributes, resources[service]);
        }
    }

    /// <summary>The next series' point at <paramref name="at"/>.</summary>
    internal MetricRow Next(DateTimeOffset at)
    {
        (string service, string metric, ulong hash, Variant attributes, Variant resource) = _series[_next];
        _next = (_next + 1) % _series.Length;
        return new MetricRow(at, service, metric, hash, Math.Round(_random.NextDouble() * 1_000, 3), attributes, resource);
    }
}

/// <summary>A gauge's point, as an ingestion of metrics writes it.</summary>
[VortexRecord]
public partial record struct MetricRow(
    DateTimeOffset TimeUnix, string ServiceName, string MetricName, ulong AttributesHash, double Value, Variant Attributes, Variant ResourceAttributes);
