using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.Indexes;
using Vorticity.IO;
using Vorticity.Types;

namespace Vorticity.Benchmarks.Queries;

/// <summary>
/// <c>--micro load</c>: the write path of a load of sealed periods at the time-window design's density, a few seconds a
/// round. <c>--periods</c> hours of <c>--minutes</c> minutes of requests each, at <c>--rate</c> a second, <c>--parallel</c> of
/// them at once, each minute a part drawn by a generator of its own, into a store of its own under <c>--store</c> each
/// round (<see cref="DesignBuild.LoadAsync"/> loads days of hours so); with <c>--period-hours</c> H past 1, the periods are
/// days of H hours of <c>--minutes</c> minutes each, a day's object and its hours' boundaries at a smaller size. Each round
/// says its rows and bytes a second, the processor time it took, the bytes it allocated, its collections and how long
/// they paused the threads, the most memory it held, and each period how long its plan, its write and its finish took;
/// <c>--disk</c> first measures what the store's disk takes in a second.
/// </summary>
internal static class LoadMicro
{
    internal static async Task<int> Run(string root, int periods, int parallel, int minutes, double rate, int rounds, int budget, int writeDegree, bool disk, int periodHours = 1)
    {
        Directory.CreateDirectory(root);
        if (disk)
        {
            double written = await DiskAsync(root);
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"disk: {written / 1e9:F2} GB a second written, 4 files of 1 GB at once, through"));
        }

        long perHour = (long)(rate * 3_600);
        long perMinute = perHour / 60;
        long rows = periods * periodHours * minutes * perMinute;
        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"load: {periods} periods of {periodHours} h of {minutes} min at {rate:N0} rows a second, {rows / 1e6:F1} M rows a round, {parallel} at once, write degree {writeDegree}, store {root}"));
        List<double> times = [];
        for (int round = 0; round < rounds; round++)
        {
            string directory = Path.Combine(root, "round");
            string scratch = Path.Combine(root, "scratch");
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }

            Directory.CreateDirectory(scratch);
            await using VortexSession session = VortexSession.Create(options => options.MaxDegreeOfParallelism = writeDegree);
            await using VortexDataset dataset = await VortexDataset.CreateAsync(new FileObjectStore(directory), GeneratedRequest.Schema, new DatasetOptions
            {
                Session = session,
                ClusteringKey = [nameof(GeneratedRequest.At)],
                Periods = new PeriodOptions { Lateness = TimeSpan.FromMinutes(5), Length = periodHours > 1 ? SliceWidth.Day : SliceWidth.Hour },
                Identifiers = [nameof(GeneratedRequest.RequestId)],
                Write = DesignBuild.WriteOptions(budget, scratch),
            });

            DType row = dataset.Snapshot.Schema.DType;
            TimeUnit unit = PeriodOptions.InstantOf(dataset.Key);
            PeriodRows[] loads = new PeriodRows[periods];
            for (int p = 0; p < periods; p++)
            {
                // A period is an hour, or a day whose first hours hold the rows; a part is a minute of one of them.
                int first = periodHours > 1 ? p * 24 : p;
                loads[p] = new PeriodRows(
                    TimeSlicer.InstantOf(QuestionBench.Start.AddHours(first), unit),
                    [.. Enumerable.Range(0, periodHours * minutes).Select(at => (Func<CancellationToken, IAsyncEnumerable<RecordBatch>>)(token =>
                        DesignBuild.RowsAsync(row, unit, first + (at / minutes), at % minutes * perMinute, perMinute, perHour, 1_000 + ((first + (at / minutes)) * 60) + (at % minutes), token)))]);
            }

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            using Process self = Process.GetCurrentProcess();
            TimeSpan cpu = self.TotalProcessorTime;
            long allocated = GC.GetTotalAllocatedBytes(precise: true);
            int[] collections = [GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2)];
            TimeSpan paused = GC.GetTotalPauseDuration();
            TimeSpan held = session.WriteLanes.Held;
            IReadOnlyList<LoadedPeriod> loaded;
            WriteTimings timings = new WriteTimings();
            double seconds;
            long peak;
            using (PeakMemory memory = new PeakMemory())
            {
                long begin = Stopwatch.GetTimestamp();
                loaded = await DatasetCompactor.LoadPeriodsAsync(dataset, loads, parallel, timings: timings);
                seconds = Stopwatch.GetElapsedTime(begin).TotalSeconds;
                peak = memory.Peak;
            }

            self.Refresh();
            double used = (self.TotalProcessorTime - cpu).TotalSeconds;
            double occupied = (session.WriteLanes.Held - held).TotalSeconds / (writeDegree * seconds);
            long bytes = loaded.Sum(p => p.Bytes);
            times.Add(seconds);
            Console.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"round {round + 1}: {seconds:F2} s, {loaded.Sum(p => p.Rows) / seconds / 1e6:F2} M rows/s, {bytes / seconds / 1e6:F0} MB/s ({bytes / 1e9:F2} GB), cpu {used:F1} s ({used / seconds:F1} cores), lanes {occupied:P0} of {writeDegree}, {(GC.GetTotalAllocatedBytes(precise: true) - allocated) / 1e9:F2} GB allocated, gc {GC.CollectionCount(0) - collections[0]}/{GC.CollectionCount(1) - collections[1]}/{GC.CollectionCount(2) - collections[2]} pausing {(GC.GetTotalPauseDuration() - paused).TotalSeconds:F2} s, peak private {peak / 1e9:F2} GB"));
            Console.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"  stages, summed over the periods: feed {timings.Feed.TotalSeconds:F2} s, copy {timings.Copy.TotalSeconds:F2} s, join {timings.Join.TotalSeconds:F2} s, sort {timings.Sort.TotalSeconds:F2} s, wait {timings.Wait.TotalSeconds:F2} s | synopsis {timings.Synopsis.TotalSeconds:F2} s, write {timings.Write.TotalSeconds:F2} s, lag {timings.Lag.TotalSeconds:F2} s | drain {timings.Drain.TotalSeconds:F2} s, tables {timings.Tables.TotalSeconds:F2} s, close {timings.Close.TotalSeconds:F2} s"));
            foreach (LoadedPeriod period in loaded)
            {
                Console.WriteLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"  period {TimeSlicer.TimeOf(period.Period, unit):HH:mm}: plan {period.Plan.TotalSeconds:F2} s, write {period.Write.TotalSeconds:F2} s, finish {period.Finish.TotalSeconds:F2} s, commit {period.Commit.TotalSeconds:F2} s, {period.Bytes / 1e6:F0} MB"));
            }
        }

        times.Sort();
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"median {times[times.Count / 2]:F2} s, {rows / times[times.Count / 2] / 1e6:F2} M rows/s"));
        return 0;
    }

    /// <summary>The bytes a second four files of 1 GB written at once take on the disk of <paramref name="root"/>, past the cache.</summary>
    private static async Task<double> DiskAsync(string root)
    {
        const int Files = 4;
        const int Chunk = 8 << 20;
        const long Length = 1L << 30;
        byte[] data = new byte[Chunk];
        Random.Shared.NextBytes(data);
        string[] paths = [.. Enumerable.Range(0, Files).Select(i => Path.Combine(root, $"disk-{i}.bin"))];
        long begin = Stopwatch.GetTimestamp();
        await Task.WhenAll(paths.Select(path => Task.Run(async () =>
        {
            await using FileStream stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 0, FileOptions.WriteThrough | FileOptions.Asynchronous);
            for (long written = 0; written < Length; written += Chunk)
            {
                await stream.WriteAsync(data);
            }
        })));
        double seconds = Stopwatch.GetElapsedTime(begin).TotalSeconds;
        foreach (string path in paths)
        {
            System.IO.File.Delete(path);
        }

        return Files * Length / seconds;
    }

    /// <summary>The most private bytes the process holds while it is open, sampled every 100 ms on a thread of its own.</summary>
    private sealed class PeakMemory : IDisposable
    {
        private readonly Thread _thread;
        private volatile bool _stop;
        private long _peak;

        internal PeakMemory()
        {
            _thread = new Thread(Sample) { IsBackground = true, Name = "peak memory" };
            _thread.Start();
        }

        internal long Peak => Interlocked.Read(ref _peak);

        private void Sample()
        {
            using Process self = Process.GetCurrentProcess();
            while (!_stop)
            {
                self.Refresh();
                long bytes = self.PrivateMemorySize64;
                if (bytes > Interlocked.Read(ref _peak))
                {
                    Interlocked.Exchange(ref _peak, bytes);
                }

                Thread.Sleep(100);
            }
        }

        public void Dispose()
        {
            _stop = true;
            _thread.Join();
        }
    }
}
