using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Vorticity.Dataset;

namespace Vorticity.Bench.Churn;

/// <summary>The rows the dataset holds: a clustering key, and columns of each common kind.</summary>
[VortexRecord]
public partial record struct ChurnRow(long Key, long Stamp, double Value, int Group, string Label);

/// <summary>The load, the stream of small operations, and the checkpoints of reads between them.</summary>
internal static class Churn
{
    private const int KindCount = 3;

    private static readonly string[] Kinds = ["append", "update", "delete"];

    /// <summary>A label per residue, so that writing a row allocates no string.</summary>
    private static readonly string[] Labels = [.. Enumerable.Range(0, 64).Select(i => "label-" + i.ToString(CultureInfo.InvariantCulture))];

    /// <summary>Runs the whole thing and prints as it goes.</summary>
    internal static async Task RunAsync(ChurnOptions options)
    {
        string root = options.Directory ?? Path.Combine(Path.GetTempPath(), "vorticity-churn-" + Guid.NewGuid().ToString("N"));
        bool owned = options.Directory is null && !options.Memory;
        IObjectStore inner = options.Memory ? new MemoryObjectStore() : new FileObjectStore(root);
        CountingObjectStore store = new CountingObjectStore(inner, ownsInner: true);
        try
        {
            await RunAsync(options, store, options.Memory ? null : root).ConfigureAwait(false);
        }
        finally
        {
            await store.DisposeAsync().ConfigureAwait(false);
            if (owned && System.IO.Directory.Exists(root))
            {
                System.IO.Directory.Delete(root, recursive: true);
            }
        }
    }

    private static async Task RunAsync(ChurnOptions options, CountingObjectStore store, string? root)
    {
        DatasetOptions datasetOptions = new DatasetOptions
        {
            ClusteringKey = [ChurnRow.ColumnNames.Key],
            // The clustering key's run takes twelve bytes a row, which the default budget of a tenth of
            // the data refuses on any row narrower than 120 bytes once an object passes a mebibyte.
            Write = options.IndexBudget > 0
                ? new VortexWriteOptions { Indexes = IndexPolicy.Auto.WithBudgetPerMille(options.IndexBudget) }
                : new VortexWriteOptions(),
            MaxOpenObjects = options.OpenObjects > 0 ? options.OpenObjects : new DatasetOptions().MaxOpenObjects,
            // Vacuum then takes what the latest version does not reference as soon as it is a
            // second old, which is what keeps a long run's store from holding every rewrite.
            RetentionWindow = TimeSpan.FromSeconds(1),
        };

        VortexDataset dataset = await VortexDataset.CreateAsync(store, ChurnRow.Schema, datasetOptions).ConfigureAwait(false);
        await using (dataset.ConfigureAwait(false))
        {
            await LoadAsync(options, dataset, store).ConfigureAwait(false);
            await OperateAsync(options, dataset, store, root).ConfigureAwait(false);
        }
    }

    private static async Task LoadAsync(ChurnOptions options, VortexDataset dataset, CountingObjectStore store)
    {
        Stopwatch clock = Stopwatch.StartNew();
        ChurnRow[] rows = new ChurnRow[Math.Min(options.LoadChunk, 1 << 16)];
        for (long first = 0; first < options.Rows; first += options.LoadChunk)
        {
            long count = Math.Min(options.LoadChunk, options.Rows - first);
            await using ObjectDraft draft = dataset.StartObject();
            for (long at = 0; at < count; at += rows.Length)
            {
                int length = (int)Math.Min(rows.Length, count - at);
                for (int i = 0; i < length; i++)
                {
                    long row = first + at + i;
                    rows[i] = Row(2 * row, 0);
                }

                await draft.Writer.WriteAsync<ChurnRow>(rows.AsSpan(0, length)).ConfigureAwait(false);
            }

            await dataset.AppendAsync(draft).ConfigureAwait(false);
        }

        double appended = clock.Elapsed.TotalSeconds;
        int compactions = 0;
        while (await dataset.CompactAsync(Compaction(options)).ConfigureAwait(false) is not null)
        {
            compactions++;
        }

        CompactionPlan plan = await dataset.PlanCompactionAsync(Compaction(options)).ConfigureAwait(false);
        Console.Out.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"load: {dataset.RowCount} rows in {appended:F1} s, {compactions} compactions in {clock.Elapsed.TotalSeconds - appended:F1} s; " +
            $"{Shape(plan)}; {store.BytesWritten / (1024.0 * 1024.0):F1} MiB written"));
    }

    private static async Task OperateAsync(ChurnOptions options, VortexDataset dataset, CountingObjectStore store, string? root)
    {
        Random random = new Random(options.Seed);
        long keySpace = 2 * Math.Max(options.Rows, 1);
        long tail = keySpace;
        int weights = options.Appends + options.Updates + options.Deletes;
        if (weights == 0)
        {
            throw new ArgumentException("--mix gives every kind a weight of zero.");
        }

        Window window = new Window();
        Stopwatch run = Stopwatch.StartNew();
        List<Checkpoint> checkpoints = [await CheckpointAsync(0, dataset, store, root, options, random).ConfigureAwait(false)];
        ChurnRow[] appended = new ChurnRow[options.Batch];
        Console.Out.WriteLine();
        Console.Out.WriteLine(Window.Header);

        int op = 1;
        for (; op <= options.Ops; op++)
        {
            int pick = random.Next(weights);
            int kind = pick < options.Appends ? 0 : pick < options.Appends + options.Updates ? 1 : 2;
            long requests = store.Requests;
            long written = store.BytesWritten;
            long allocated = GC.GetTotalAllocatedBytes(precise: false);
            long stamp = op;
            long touched;
            long started = Stopwatch.GetTimestamp();
            switch (kind)
            {
                case 0:
                {
                    for (int i = 0; i < appended.Length; i++)
                    {
                        long key = options.Keys == KeyPlacement.Tail ? tail++ : (2 * random.NextInt64(keySpace / 2)) + 1;
                        appended[i] = Row(key, stamp);
                    }

                    await using ObjectDraft draft = dataset.StartObject();
                    await draft.Writer.WriteAsync<ChurnRow>(appended).ConfigureAwait(false);
                    await dataset.AppendAsync(draft).ConfigureAwait(false);
                    touched = appended.Length;
                    break;
                }

                case 1:
                {
                    long low = 2 * random.NextInt64(keySpace / 2);
                    long high = low + (2 * options.Batch);
                    RowChangeResult result = await dataset.UpdateAsync<ChurnRow>(
                        r => r.Key >= low & r.Key < high,
                        r => r with { Value = r.Value + 1, Stamp = stamp }).ConfigureAwait(false);
                    touched = result.Rows;
                    break;
                }

                default:
                {
                    long low = 2 * random.NextInt64(keySpace / 2);
                    long high = low + (2 * options.Batch);
                    RowChangeResult result = await dataset.DeleteAsync<ChurnRow>(r => r.Key >= low & r.Key < high).ConfigureAwait(false);
                    touched = result.Rows;
                    break;
                }
            }

            window.Add(
                kind,
                Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                store.Requests - requests,
                store.BytesWritten - written,
                GC.GetTotalAllocatedBytes(precise: false) - allocated,
                touched);

            if (options.Compact == CompactionCadence.Drain && op % Math.Max(options.CompactEvery, 1) == 0)
            {
                written = store.BytesWritten;
                started = Stopwatch.GetTimestamp();
                while (await dataset.CompactAsync(Compaction(options)).ConfigureAwait(false) is { } compacted)
                {
                    window.Compactions++;
                    window.CompactedIn += compacted.BytesIn;
                }

                window.CompactMs += Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                window.CompactWritten += store.BytesWritten - written;
            }

            if (options.VacuumEvery > 0 && op % options.VacuumEvery == 0)
            {
                started = Stopwatch.GetTimestamp();
                VacuumResult vacuumed = await dataset.VacuumAsync().ConfigureAwait(false);
                window.VacuumMs += Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                window.Vacuumed += vacuumed.Deleted.Length;
            }

            if (op % options.ReportInterval == 0)
            {
                CompactionPlan plan = await dataset.PlanCompactionAsync(Compaction(options)).ConfigureAwait(false);
                Console.Out.WriteLine(window.Line(op, Shape(plan)));
                window = new Window();
            }

            if (op % options.ProbeInterval == 0)
            {
                checkpoints.Add(await CheckpointAsync(op, dataset, store, root, options, random).ConfigureAwait(false));
                Console.Out.WriteLine();
                Console.Out.WriteLine(Window.Header);
            }

            if (options.Minutes > 0 && run.Elapsed.TotalMinutes >= options.Minutes)
            {
                Console.Out.WriteLine(string.Create(
                    CultureInfo.InvariantCulture, $"stopped after {op} operations: {run.Elapsed.TotalMinutes:F1} minutes"));
                break;
            }
        }

        if (checkpoints[^1].Op != Math.Min(op, options.Ops))
        {
            checkpoints.Add(await CheckpointAsync(Math.Min(op, options.Ops), dataset, store, root, options, random).ConfigureAwait(false));
        }

        Console.Out.WriteLine();
        Console.Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"operations: {run.Elapsed.TotalSeconds:F1} s"));
        Summarize(checkpoints);
    }

    /// <summary>The reads measured at a checkpoint, and what the store and the process hold.</summary>
    private static async Task<Checkpoint> CheckpointAsync(
        int op, VortexDataset dataset, CountingObjectStore store, string? root, ChurnOptions options, Random random)
    {
        long keySpace = 2 * Math.Max(options.Rows, 1);
        Checkpoint checkpoint = new Checkpoint(op);

        // A handle that has read nothing: what a reader starting now pays, then its first lookup.
        long requests = store.Requests;
        long started = Stopwatch.GetTimestamp();
        VortexDataset fresh = await VortexDataset.OpenAsync(store).ConfigureAwait(false);
        await using (fresh.ConfigureAwait(false))
        {
            checkpoint.Add("open", Stopwatch.GetElapsedTime(started).TotalMilliseconds, store.Requests - requests, 1);
            long key = 2 * random.NextInt64(keySpace / 2);
            requests = store.Requests;
            started = Stopwatch.GetTimestamp();
            await PointAsync(fresh, key).ConfigureAwait(false);
            checkpoint.Add("cold point", Stopwatch.GetElapsedTime(started).TotalMilliseconds, store.Requests - requests, 1);
        }

        foreach ((string name, int calls, Func<VortexDataset, Random, ValueTask<long>> probe) in Probes(options))
        {
            // One call first, unmeasured, so that a checkpoint compares warm reads with warm reads.
            _ = await probe(dataset, random).ConfigureAwait(false);
            requests = store.Requests;
            started = Stopwatch.GetTimestamp();
            for (int i = 0; i < calls; i++)
            {
                _ = await probe(dataset, random).ConfigureAwait(false);
            }

            checkpoint.Add(name, Stopwatch.GetElapsedTime(started).TotalMilliseconds, store.Requests - requests, calls);
        }

        long heap = GC.GetTotalMemory(forceFullCollection: true);
        using Process process = Process.GetCurrentProcess();
        process.Refresh();
        CompactionPlan plan = await dataset.PlanCompactionAsync(Compaction(options)).ConfigureAwait(false);
        (int commits, int data, long bytes) = root is null ? (0, 0, 0L) : StoreContent(root);
        checkpoint.Heap = heap;
        Console.Out.WriteLine();
        string content = root is null
            ? ""
            : string.Create(CultureInfo.InvariantCulture, $"store {commits} commits + {data} data objects, {bytes / (1024.0 * 1024.0):F1} MiB; ");
        Console.Out.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"checkpoint after {op} ops: version {dataset.Version}, {dataset.RowCount} rows, {Shape(plan)}; {content}" +
            $"heap {heap / (1024.0 * 1024.0):F1} MiB, working set {process.WorkingSet64 / (1024.0 * 1024.0):F0} MiB"));
        Console.Out.WriteLine(checkpoint.Table());
        return checkpoint;
    }

    /// <summary>The compaction the caller asks for: the library's defaults, or the sizes the command line gave.</summary>
    private static CompactionOptions Compaction(ChurnOptions options)
    {
        CompactionOptions compaction = new CompactionOptions();
        if (options.MaxObjectMiB > 0)
        {
            compaction = compaction with { MaxObjectBytes = (long)options.MaxObjectMiB << 20 };
        }

        if (options.LevelOneKiB > 0)
        {
            compaction = compaction with { TargetBytesAtLevelOne = (long)options.LevelOneKiB << 10 };
        }

        return compaction;
    }

    /// <summary>The reads a checkpoint measures, each with how many calls it averages over.</summary>
    private static (string Name, int Calls, Func<VortexDataset, Random, ValueTask<long>> Probe)[] Probes(ChurnOptions options)
    {
        long keySpace = 2 * Math.Max(options.Rows, 1);
        return
        [
            ("point", 20, (dataset, random) => PointAsync(dataset, 2 * random.NextInt64(keySpace / 2))),
            ("range 1k", 5, async (dataset, random) =>
            {
                long low = 2 * random.NextInt64(keySpace / 2);
                long high = low + 2_000;
                long rows = 0;
                await foreach (ChurnRow row in dataset.Scan<ChurnRow>().Where(r => r.Key >= low & r.Key < high).ToRecordsAsync().ConfigureAwait(false))
                {
                    rows += row.Key >= 0 ? 1 : 0;
                }

                return rows;
            }),
            ("seek +10", 20, (dataset, random) => SeekAsync(dataset, 2 * random.NextInt64(keySpace / 2), SeekOp.AtOrAfter)),
            ("seek back -10", 20, (dataset, random) => SeekAsync(dataset, 2 * random.NextInt64(keySpace / 2), SeekOp.AtOrBefore)),
            ("rows 100", 20, async (dataset, random) =>
            {
                long start = random.NextInt64(Math.Max(dataset.RowCount - 100, 1));
                long rows = 0;
                await foreach (ChurnRow row in dataset.Scan<ChurnRow>().Rows(RowRange.FromLength(start, 100)).ToRecordsAsync().ConfigureAwait(false))
                {
                    rows += row.Key >= 0 ? 1 : 0;
                }

                return rows;
            }),
            ("first 10", 5, async (dataset, _) =>
            {
                long rows = 0;
                await foreach (ChurnRow row in dataset.Scan<ChurnRow>().OrderBy(r => r.Key).ToRecordsAsync().ConfigureAwait(false))
                {
                    if (++rows == 10)
                    {
                        break;
                    }
                }

                return rows;
            }),
            ("count", 5, (dataset, _) => dataset.Scan<ChurnRow>().CountAsync()),
            ("count half", 5, (dataset, _) =>
            {
                long half = keySpace / 2;
                return dataset.Scan<ChurnRow>().Where(r => r.Key < half).CountAsync();
            }),
            ("max key", 5, (dataset, _) => dataset.Scan<ChurnRow>().MaxAsync(r => r.Key)),
            ("filter", 3, (dataset, _) => dataset.Scan<ChurnRow>().Where(r => r.Group == 7).CountAsync()),
            ("scan sum", 3, async (dataset, _) => (long)await dataset.Scan<ChurnRow>().SumAsync(r => r.Value).ConfigureAwait(false)),
        ];
    }

    /// <summary>
    /// A row whose columns other than the key are as a hash of it makes them: data that compresses as
    /// real measurements do rather than to nothing, which would make every object a few bytes a row.
    /// </summary>
    private static ChurnRow Row(long key, long stamp)
    {
        ulong mixed = Mix((ulong)key);
        return new ChurnRow(
            key,
            stamp == 0 ? (long)(Mix(mixed) >> 24) : stamp,
            (mixed >> 11) * (1.0 / (1UL << 53)) * 1000.0,
            (int)(mixed % 100),
            Labels[(int)((mixed >> 40) % (ulong)Labels.Length)]);
    }

    /// <summary>SplitMix64's finalizer.</summary>
    private static ulong Mix(ulong value)
    {
        value = (value ^ (value >> 30)) * 0xBF58476D1CE4E5B9UL;
        value = (value ^ (value >> 27)) * 0x94D049BB133111EBUL;
        return value ^ (value >> 31);
    }

    private static async ValueTask<long> PointAsync(VortexDataset dataset, long key)
    {
        long rows = 0;
        await foreach (ChurnRow row in dataset.Scan<ChurnRow>().Where(r => r.Key == key).ToRecordsAsync().ConfigureAwait(false))
        {
            rows += row.Key == key ? 1 : 0;
        }

        return rows;
    }

    private static async ValueTask<long> SeekAsync(VortexDataset dataset, long key, SeekOp op)
    {
        KeyCursor<long> cursor = await dataset.Scan<ChurnRow>().Keys(r => r.Key).OpenAsync().ConfigureAwait(false);
        await using (cursor.ConfigureAwait(false))
        {
            long steps = 0;
            if (!await cursor.SeekAsync(key, op).ConfigureAwait(false))
            {
                return 0;
            }

            for (int i = 0; i < 10; i++)
            {
                bool moved = op == SeekOp.AtOrBefore
                    ? await cursor.PrevAsync().ConfigureAwait(false)
                    : await cursor.NextAsync().ConfigureAwait(false);
                if (!moved)
                {
                    break;
                }

                steps++;
            }

            return steps;
        }
    }

    /// <summary>The level shape a plan reports: objects and MiB per level.</summary>
    private static string Shape(CompactionPlan plan)
    {
        StringBuilder text = new StringBuilder("levels");
        for (int level = 0; level < plan.ObjectsByLevel.Length; level++)
        {
            text.Append(CultureInfo.InvariantCulture, $" L{level}={plan.ObjectsByLevel[level]}/{plan.BytesByLevel[level] / (1024.0 * 1024.0):F1}M");
        }

        return text.ToString();
    }

    /// <summary>The objects the file store holds and their bytes.</summary>
    private static (int Commits, int Data, long Bytes) StoreContent(string root)
    {
        int commits = 0;
        int data = 0;
        long bytes = 0;
        foreach (FileInfo file in new DirectoryInfo(root).EnumerateFiles("*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(root, file.FullName);
            commits += relative.StartsWith("commit", StringComparison.Ordinal) ? 1 : 0;
            data += relative.StartsWith("data", StringComparison.Ordinal) ? 1 : 0;
            bytes += file.Length;
        }

        return (commits, data, bytes);
    }

    /// <summary>The probe matrix: one column per checkpoint.</summary>
    private static void Summarize(List<Checkpoint> checkpoints)
    {
        foreach ((string title, Func<Checkpoint.Probe, double> value, string format) in new (string, Func<Checkpoint.Probe, double>, string)[]
        {
            ("ms per call", probe => probe.Ms / probe.Calls, "F2"),
            ("requests per call", probe => (double)probe.Requests / probe.Calls, "F1"),
        })
        {
            Console.Out.WriteLine();
            StringBuilder header = new StringBuilder($"{title,-16}");
            foreach (Checkpoint checkpoint in checkpoints)
            {
                header.Append(CultureInfo.InvariantCulture, $"{"op " + checkpoint.Op.ToString(CultureInfo.InvariantCulture),12}");
            }

            Console.Out.WriteLine(header.ToString());
            foreach (string name in checkpoints[0].Probes.Select(probe => probe.Name))
            {
                StringBuilder line = new StringBuilder($"{name,-16}");
                foreach (Checkpoint checkpoint in checkpoints)
                {
                    Checkpoint.Probe probe = checkpoint.Probes.First(p => p.Name == name);
                    line.Append(CultureInfo.InvariantCulture, $"{value(probe).ToString(format, CultureInfo.InvariantCulture),12}");
                }

                Console.Out.WriteLine(line.ToString());
            }
        }

        StringBuilder heap = new StringBuilder($"{"heap MiB",-16}");
        foreach (Checkpoint checkpoint in checkpoints)
        {
            heap.Append(CultureInfo.InvariantCulture, $"{checkpoint.Heap / (1024.0 * 1024.0),12:F1}");
        }

        Console.Out.WriteLine(heap.ToString());
    }

    /// <summary>What one window of operations cost, per kind.</summary>
    private sealed class Window
    {
        internal const string Header =
            "     ops |  append ms mean   p99    max |  update ms mean   p99    max |  delete ms mean   p99    max | req/op | KiB written/op | alloc KiB/op | compactions ms/op KiB/op | vacuum ms | shape";

        private readonly List<double>[] _times = [[], [], []];
        private readonly long[] _requests = new long[KindCount];
        private readonly long[] _written = new long[KindCount];
        private readonly long[] _allocated = new long[KindCount];
        private readonly long[] _rows = new long[KindCount];

        internal int Compactions { get; set; }

        internal long CompactedIn { get; set; }

        internal long CompactWritten { get; set; }

        internal double CompactMs { get; set; }

        internal double VacuumMs { get; set; }

        internal int Vacuumed { get; set; }

        internal void Add(int kind, double ms, long requests, long written, long allocated, long rows)
        {
            _times[kind].Add(ms);
            _requests[kind] += requests;
            _written[kind] += written;
            _allocated[kind] += allocated;
            _rows[kind] += rows;
        }

        internal string Line(int op, string shape)
        {
            StringBuilder line = new StringBuilder(string.Create(CultureInfo.InvariantCulture, $"{op,8} |"));
            int ops = 0;
            long requests = 0;
            long written = 0;
            long allocated = 0;
            for (int kind = 0; kind < KindCount; kind++)
            {
                List<double> times = _times[kind];
                ops += times.Count;
                requests += _requests[kind];
                written += _written[kind];
                allocated += _allocated[kind];
                if (times.Count == 0)
                {
                    line.Append("                              |");
                    continue;
                }

                times.Sort();
                double p99 = times[Math.Min(times.Count - 1, (int)Math.Ceiling(times.Count * 0.99) - 1)];
                line.Append(CultureInfo.InvariantCulture, $" {times.Average(),14:F1} {p99,5:F0} {times[^1],6:F0} |");
            }

            ops = Math.Max(ops, 1);
            line.Append(CultureInfo.InvariantCulture, $" {(double)requests / ops,6:F1} | {written / 1024.0 / ops,14:F1} | {allocated / 1024.0 / ops,12:F0} |");
            line.Append(CultureInfo.InvariantCulture, $" {Compactions,11} {CompactMs / ops,5:F1} {CompactWritten / 1024.0 / ops,6:F0} | {VacuumMs,9:F0} | {shape}");
            return line.ToString();
        }
    }

    /// <summary>The reads measured at one checkpoint.</summary>
    private sealed class Checkpoint(int op)
    {
        internal int Op { get; } = op;

        internal List<Probe> Probes { get; } = [];

        internal long Heap { get; set; }

        internal void Add(string name, double ms, long requests, int calls) => Probes.Add(new Probe(name, ms, requests, calls));

        internal string Table()
        {
            StringBuilder table = new StringBuilder();
            foreach (Probe probe in Probes)
            {
                table.Append(CultureInfo.InvariantCulture, $"  {probe.Name,-14} {probe.Ms / probe.Calls,10:F2} ms {(double)probe.Requests / probe.Calls,8:F1} req");
                table.AppendLine();
            }

            return table.ToString().TrimEnd();
        }

        internal readonly record struct Probe(string Name, double Ms, long Requests, int Calls);
    }
}
