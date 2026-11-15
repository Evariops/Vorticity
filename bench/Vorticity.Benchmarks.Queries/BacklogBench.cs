using System;
using System.Collections.Generic;
using System.Diagnostics;
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
/// <c>--compact-only</c> compacts the backlog an earlier run left there, copied first so that it stays whole.
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

    internal static async Task<int> RunAsync(string root, int appends, int rows, int series, long memoryLimit, bool compactOnly, int latency)
    {
        if (memoryLimit > 0)
        {
            VortexMemory.SetLimit(memoryLimit);
        }

        string backlog = Path.Combine(root, "backlog");
        string work = Path.Combine(root, "work");
        using VortexKeyring keyring = VortexKeyring.FromKeys(new VortexKey("backlog", new byte[32]));
        await using VortexSession session = VortexSession.Create(options => options.Keyring = keyring);
        using Sampler sampler = new Sampler();
        Stopwatch clock = Stopwatch.StartNew();

        if (!compactOnly)
        {
            Fresh(backlog);
            await BuildAsync(session, backlog, appends, rows, series);
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"backlog of {appends} appends of {rows} rows built in {clock.Elapsed.TotalSeconds:F0} s; {sampler.Peaks()}"));
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
            $"maintainer on {maintainer.RowCount:N0} rows in {maintainer.ObjectCount} objects, lag {maintainer.Lag}; jobs' budget {Mib(QueryMemoryBudget.JobsOf(session).CeilingBytes)} MiB of a limit of {Mib(VortexDiagnostics.GetMemoryReport().LimitBytes)} MiB"));
        int jobs = 0;
        long mostHeld = 0;
        while (true)
        {
            sampler.Reset();
            Stopwatch job = Stopwatch.StartNew();
            await maintainer.RefreshAsync();
            CompactionResult? result = await maintainer.CompactAsync();
            if (result is null)
            {
                break;
            }

            jobs++;
            mostHeld = Math.Max(mostHeld, result.MemoryReserved);
            Console.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"job {jobs}: {result.Trigger} L{result.FromLevel}->L{result.ToLevel}, {result.ObjectsIn} -> {result.ObjectsOut} objects ({result.ObjectsMoved} moved), {result.Rows:N0} rows, {result.BytesIn / 1e6:F1} -> {result.BytesOut / 1e6:F1} MB, blocks held {Mib(result.MemoryReserved)} MiB, {result.RangesAtOnce} at once, scratch {Mib(result.ScratchBytes)} MiB, {job.Elapsed.TotalSeconds:F1} s; {sampler.Peaks()}"));
        }

        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"done: {jobs} jobs in {clock.Elapsed.TotalSeconds:F0} s, {maintainer.ObjectCount} objects left, the most a job's blocks held {Mib(mostHeld)} MiB; {Sampler.Now()}"));
        return 0;
    }

    /// <summary>The backlog: appends of metric records each spanning every series' keys, none compacted.</summary>
    private static async Task BuildAsync(VortexSession session, string root, int appends, int rows, int series)
    {
        IObjectStore store = new FileObjectStore(root);
        await using VortexDataset writer = await VortexDataset.CreateAsync(store, MetricRow.Schema, new DatasetOptions
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
        });

        MetricGenerator generator = new MetricGenerator(series, 31);
        MetricRow[] batch = new MetricRow[rows];
        for (int a = 0; a < appends; a++)
        {
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

    /// <summary>
    /// The process's resident bytes, the blocks alive, those the pool keeps, and the heap past the blocks, sampled every 50 ms,
    /// and their peaks since the last reset.
    /// </summary>
    private sealed class Sampler : IDisposable
    {
        private readonly Timer _timer;
        private long _resident;
        private long _blocks;
        private long _parked;
        private long _slabs;
        private long _beside;

        internal Sampler() => _timer = new Timer(_ => Sample(), null, 0, 50);

        internal void Reset()
        {
            Interlocked.Exchange(ref _resident, 0);
            Interlocked.Exchange(ref _blocks, 0);
            Interlocked.Exchange(ref _parked, 0);
            Interlocked.Exchange(ref _slabs, 0);
            Interlocked.Exchange(ref _beside, 0);
        }

        internal string Peaks() => string.Create(
            CultureInfo.InvariantCulture,
            $"peaks: resident {Mib(Interlocked.Read(ref _resident))} MiB, blocks {Mib(Interlocked.Read(ref _blocks))}, of which the pool keeps {Mib(Interlocked.Read(ref _parked))}, slabs {Mib(Interlocked.Read(ref _slabs))}, heap beside the blocks and slabs {Mib(Interlocked.Read(ref _beside))}");

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
                $"now: resident {Mib(MemoryPeaks.Read().Working)} MiB, blocks {Mib(blocks)}, slabs {Mib(Slabs.HeldBytes)}, heap beside them {Mib(GC.GetTotalMemory(false) - Pinned())}; reserved {Mib(report.ReservedBytes)}, rented {Mib(report.RentedBytes)}, kept {Mib(report.RetainedBytes)} [{kept}], rest {Mib(report.RestBytes)}, ceiling {Mib(report.CeilingBytes)} of {Mib(report.LimitBytes)}");
        }

        public void Dispose() => _timer.Dispose();

        private void Sample()
        {
            long blocks = BlockOwner.LiveBytes;
            Raise(ref _resident, MemoryPeaks.Read().Working);
            Raise(ref _blocks, blocks);
            Raise(ref _parked, MemoryAccount.Pool.ParkedBytes);
            Raise(ref _slabs, Slabs.HeldBytes);
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
