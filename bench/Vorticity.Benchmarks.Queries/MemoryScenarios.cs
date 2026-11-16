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
/// What a process holds, by sort, under the two loads that put its memory to the test, read through the library's report and
/// never through its internals, so that every change of how the memory is held is judged by the same numbers.
/// </summary>
/// <remarks>
/// <para>
/// <c>--micro maintainer</c> runs a maintainer: <c>--datasets</c> datasets (7) sealed under one key, each appended an object of
/// <c>--rate</c> log records a second (100) every <c>--flush</c> seconds (2) of a simulated <c>--minutes</c> (20), and between
/// two rounds of appends one compaction of each dataset in turn through handles of its own, with the caches the maintainer
/// gives them; then the process left idle for <c>--idle</c> seconds (10).
/// </para>
/// <para>
/// <c>--micro reader</c> runs a reader: <c>--tables</c> tables (8) of <c>--retentions</c> datasets each (5), a handle open on
/// every dataset, and a question by service over each table's datasets as a set; with <c>--file-rows N</c>, then a file of
/// so many rows read through the process's segment cache.
/// </para>
/// <para>
/// Both print, at each checkpoint, the process's resident bytes and their peak, the collector's committed bytes, what lies
/// outside the managed heap, and the library's report: reserved, rented, retained by holder. The stores lie on the local disk
/// under <c>--store</c>, behind <c>--latency</c> milliseconds a request (20).
/// </para>
/// </remarks>
internal static class MemoryScenarios
{
    private static readonly DateTimeOffset Start = new DateTimeOffset(2026, 11, 14, 0, 0, 0, TimeSpan.Zero);

    internal static async Task<int> MaintainerAsync(string root, int datasets, int minutes, double rate, int flush, int latency, int idle)
    {
        Fresh(root);
        using VortexKeyring keyring = VortexKeyring.FromKeys(new VortexKey("maintainer", new byte[32]));
        await using VortexSession session = VortexSession.Create(options => options.Keyring = keyring);
        using Sampler sampler = new Sampler();
        Print(sampler, "start");

        List<VortexDataset> writers = [];
        List<VortexDataset> maintainers = [];
        List<CountingObjectStore> counted = [];
        Stopwatch clock = Stopwatch.StartNew();
        for (int d = 0; d < datasets; d++)
        {
            CountingObjectStore store = new CountingObjectStore(
                new LatencyObjectStore(new FileObjectStore(Path.Combine(root, $"dataset-{d}"))) { Latency = TimeSpan.FromMilliseconds(latency) });
            counted.Add(store);
            writers.Add(await VortexDataset.CreateAsync(store, LogRow.Schema, new DatasetOptions
            {
                Session = session,
                Encrypted = true,
                Layout = new DatasetLayout { Partition = LayoutValue.Column(nameof(LogRow.At)).Truncate(CalendarUnit.Day), SortKey = [LayoutValue.Column(nameof(LogRow.ServiceName)), LayoutValue.Column(nameof(LogRow.At))] },
            }));
            maintainers.Add(await VortexDataset.OpenAsync(store, new DatasetOptions { Session = session }));
        }

        LogGenerator generator = new LogGenerator(23);
        int perFlush = Math.Max(1, (int)(rate * flush));
        int flushes = minutes * 60 / flush;
        long rows = 0;
        int jobs = 0;
        for (int f = 0; f < flushes; f++)
        {
            DateTimeOffset at = Start.AddSeconds((double)f * flush);
            foreach (VortexDataset writer in writers)
            {
                LogRow[] batch = new LogRow[perFlush];
                for (int i = 0; i < batch.Length; i++)
                {
                    batch[i] = generator.Next(at.AddTicks(i * (TimeSpan.TicksPerSecond * flush / perFlush)));
                }

                await using ObjectDraft draft = writer.StartObject();
                await draft.Writer.WriteAsync<LogRow>(batch);
                await writer.AppendAsync(draft);
                rows += batch.Length;
            }

            // One compaction of each dataset in turn, as the maintainer runs them, each handle refreshed first.
            foreach (VortexDataset maintainer in maintainers)
            {
                await maintainer.RefreshAsync();
                jobs += await maintainer.CompactAsync() is null ? 0 : 1;
            }

            if ((f + 1) % Math.Max(1, flushes / 10) == 0)
            {
                Print(sampler, string.Create(CultureInfo.InvariantCulture, $"{f + 1} flushes, {rows} rows, {jobs} jobs"));
            }
        }

        foreach (VortexDataset dataset in writers.Concat(maintainers))
        {
            await dataset.DisposeAsync();
        }

        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"done: {jobs} jobs in {clock.Elapsed.TotalSeconds:F1} s, {counted.Sum(s => s.Requests)} requests, {counted.Sum(s => s.BytesRead) / 1e6:F1} MB read"));
        await Task.Delay(TimeSpan.FromSeconds(idle));
        Print(sampler, string.Create(CultureInfo.InvariantCulture, $"idle {idle} s, handles closed"));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"peak: resident {Mib(sampler.PeakResident)} MiB, outside the heap {Mib(sampler.PeakOutside)} MiB"));
        return 0;
    }

    internal static async Task<int> ReaderAsync(string root, int tables, int retentions, int rows, int latency, int fileRows)
    {
        Fresh(root);
        using Sampler sampler = new Sampler();
        Print(sampler, "start");
        LogGenerator generator = new LogGenerator(29);
        List<VortexDatasetSet> sets = [];
        List<VortexDataset> handles = [];
        List<CountingObjectStore> counted = [];
        for (int t = 0; t < tables; t++)
        {
            List<VortexDataset> members = [];
            for (int r = 0; r < retentions; r++)
            {
                CountingObjectStore store = new CountingObjectStore(
                    new LatencyObjectStore(new FileObjectStore(Path.Combine(root, $"table-{t}", $"retention-{r}"))) { Latency = TimeSpan.FromMilliseconds(latency) });
                counted.Add(store);
                VortexDataset dataset = await VortexDataset.CreateAsync(store, LogRow.Schema, new DatasetOptions
                {
                    Layout = new DatasetLayout { Partition = LayoutValue.Column(nameof(LogRow.At)).Truncate(CalendarUnit.Day), SortKey = [LayoutValue.Column(nameof(LogRow.ServiceName)), LayoutValue.Column(nameof(LogRow.At))] },
                });
                for (int part = 0; part < 4; part++)
                {
                    LogRow[] batch = new LogRow[rows / 4];
                    for (int i = 0; i < batch.Length; i++)
                    {
                        batch[i] = generator.Next(Start.AddHours(part).AddTicks(i * 10_000L));
                    }

                    await using ObjectDraft draft = dataset.StartObject();
                    await draft.Writer.WriteAsync<LogRow>(batch);
                    await dataset.AppendAsync(draft);
                }

                members.Add(dataset);
                handles.Add(dataset);
            }

            sets.Add(new VortexDatasetSet([.. members]));
        }

        Print(sampler, string.Create(CultureInfo.InvariantCulture, $"{handles.Count} handles open"));
        for (int round = 0; round < 3; round++)
        {
            // Before the last round, the rest of an idle process: two turns of the slow timer, the memory kept down to its
            // floors, which the round after asks the store again for what they did not keep.
            if (round == 2)
            {
                long started = Stopwatch.GetTimestamp();
                HeldMemory.LetGoUnused(collection: false);
                HeldMemory.LetGoUnused(collection: false);
                double turns = Stopwatch.GetElapsedTime(started).TotalMicroseconds;
                GC.Collect();
                Print(sampler, string.Create(CultureInfo.InvariantCulture, $"at rest, the memory kept down to its floors in two turns of {turns / 2:F0} µs each"));
            }

            foreach (CountingObjectStore store in counted)
            {
                store.Reset();
            }

            long groups = 0;
            foreach (VortexDatasetSet set in sets)
            {
                groups += (await set.Scan<LogRow>().GroupBy(r => r.ServiceName).Select(g => (g.Key, g.Count()))
                    .As<ServiceRows>().ToRecordsAsync().ToListAsync()).Count;
            }

            long requests = counted.Sum(store => store.Requests);
            long bytes = counted.Sum(store => store.BytesRead);
            Print(sampler, string.Create(CultureInfo.InvariantCulture, $"round {round + 1}{(round == 2 ? ", after the rest" : string.Empty)}: {groups} groups over {sets.Count} sets, {requests} requests, {bytes / 1e6:F1} MB read"));
        }

        foreach (VortexDataset handle in handles)
        {
            await handle.DisposeAsync();
        }

        Print(sampler, "handles closed");
        if (fileRows > 0)
        {
            await FileAsync(sampler, root, fileRows, latency);
        }

        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"peak: resident {Mib(sampler.PeakResident)} MiB, outside the heap {Mib(sampler.PeakOutside)} MiB"));
        return 0;
    }

    /// <summary>
    /// A file of <paramref name="rows"/> log records read through the process's segment cache, as a store serves it: a
    /// question by service asked twice, then again after the rest, the requests each round made and what the cache keeps.
    /// </summary>
    private static async Task FileAsync(Sampler sampler, string root, int rows, int latency)
    {
        string path = Path.Combine(root, "logs.vortex");
        LogGenerator generator = new LogGenerator(31);
        LogRow[] records = new LogRow[Math.Min(rows, 100_000)];
        await using (VortexFileWriter writer = VortexSession.Default.CreateWriter<LogRow>(path))
        {
            for (int written = 0; written < rows; written += records.Length)
            {
                for (int i = 0; i < records.Length; i++)
                {
                    records[i] = generator.Next(Start.AddTicks((written + i) * 10_000L));
                }

                await writer.WriteAsync<LogRow>(records);
            }

            await writer.CompleteAsync();
        }

        LatencySource source = new LatencySource(path, TimeSpan.FromMilliseconds(latency));
        await using VortexFile file = await VortexSession.Default.OpenAsync(source);
        Print(sampler, string.Create(CultureInfo.InvariantCulture, $"a file of {rows} rows, {new FileInfo(path).Length / 1e6:F1} MB, open"));
        for (int round = 0; round < 3; round++)
        {
            if (round == 2)
            {
                HeldMemory.LetGoUnused(collection: false);
                HeldMemory.LetGoUnused(collection: false);
                GC.Collect();
                Print(sampler, "at rest, the memory kept down to its floors");
            }

            long before = source.Requests;
            int groups = (await file.Scan<LogRow>().GroupBy(r => r.ServiceName).Select(g => (g.Key, g.Count()))
                .As<ServiceRows>().ToRecordsAsync().ToListAsync()).Count;
            Print(sampler, string.Create(CultureInfo.InvariantCulture, $"file round {round + 1}{(round == 2 ? ", after the rest" : string.Empty)}: {groups} groups, {source.Requests - before} requests"));
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

    /// <summary>One checkpoint: the process, the collector, and the library's report by sort.</summary>
    private static void Print(Sampler sampler, string label)
    {
        (long resident, long committed) = Sampler.Read();
        MemoryReport report = VortexDiagnostics.GetMemoryReport();
        string retained = string.Join(", ", report.RetainedByHolder.OrderBy(h => h.Key, StringComparer.Ordinal).Select(h => string.Create(CultureInfo.InvariantCulture, $"{h.Key} {Mib(h.Value)}")));
        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"{label}: resident {Mib(resident)} (peak {Mib(sampler.PeakResident)}), heap committed {Mib(committed)}, outside the heap {Mib(resident - committed)} (peak {Mib(sampler.PeakOutside)}); reserved {Mib(report.ReservedBytes)}, rented {Mib(report.RentedBytes)}, retained {Mib(report.RetainedBytes)} [{retained}], ceiling {Mib(report.CeilingBytes)} of {Mib(report.LimitBytes)} MiB"));
    }

    private static string Mib(long bytes) => (bytes / (1024.0 * 1024)).ToString("F0", CultureInfo.InvariantCulture);

    /// <summary>The process's resident bytes and what lies outside the managed heap, sampled every 100 ms, and their peaks.</summary>
    private sealed class Sampler : IDisposable
    {
        private readonly Thread _thread;
        private volatile bool _stop;
        private long _peakResident;
        private long _peakOutside;

        internal Sampler()
        {
            _thread = new Thread(Run) { IsBackground = true, Name = "memory sampler" };
            _thread.Start();
        }

        internal long PeakResident => Interlocked.Read(ref _peakResident);

        internal long PeakOutside => Interlocked.Read(ref _peakOutside);

        /// <summary>The process's resident bytes, and the bytes the collector keeps committed.</summary>
        internal static (long Resident, long Committed) Read()
        {
            using Process self = Process.GetCurrentProcess();
            return (MemoryPeaks.Read().Working, GC.GetGCMemoryInfo().TotalCommittedBytes);
        }

        public void Dispose()
        {
            _stop = true;
            _thread.Join();
        }

        private void Run()
        {
            while (!_stop)
            {
                (long resident, long committed) = Read();
                Raise(ref _peakResident, resident);
                Raise(ref _peakOutside, resident - committed);
                Thread.Sleep(100);
            }
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

/// <summary>A service's rows, the answer of the reader's question.</summary>
[VortexRecord]
public partial record struct ServiceRows(string Service, long Rows);
