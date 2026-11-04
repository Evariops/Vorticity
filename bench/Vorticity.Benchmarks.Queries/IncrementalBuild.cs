using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.IO;

namespace Vorticity.Benchmarks.Queries;

/// <summary>
/// A dataset built the way a client's ingestion builds one: each hour's rows drawn in time order and written as objects
/// of some minutes each, several hours at once, then appended hour after hour and compacted as they come, so that the
/// past days lie in objects of their own and the last hours still in level 0 and the levels above it. What <c>--qp</c>
/// builds the client's spans and log records with.
/// </summary>
internal static class IncrementalBuild
{
    /// <summary>Whether each compaction job is printed as it ends: its trigger, style, objects, rows and bytes (<c>--print-jobs</c>).</summary>
    internal static bool PrintJobs { get; set; }

    /// <summary>
    /// <paramref name="hours"/> hours of <paramref name="perHour"/> rows each, the rows of an hour drawn by the generator
    /// <paramref name="hourly"/> gives for it, <paramref name="parallel"/> hours written at once, objects of
    /// <paramref name="partMinutes"/> minutes; the rows written. A <paramref name="clock"/> the handle runs by is set to the
    /// end of each hour before its objects are appended, as they arrive when the hour ends: set after, it ran a full hour ahead
    /// of the newest arrival, the delay that calms a partition, and the day being written was consolidated at every hour.
    /// </summary>
    internal static async Task<long> FillAsync<TRecord>(
        IObjectStore store, DatasetOptions options, int hours, long perHour, int parallel, int partMinutes, Func<int, Func<DateTimeOffset, TRecord>> hourly,
        IdiomLayouts.DataClock? clock = null)
        where TRecord : IVortexRecord<TRecord>
    {
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, TRecord.Schema, options);

        // The indexes the handle's writes build, declared to the dataset: its jobs build them into what they write too, where
        // the handle's options reach its own writes alone.
        await dataset.IndexAsync(options.Write.Indexes);
        Dictionary<string, Pass> passes = new(StringComparer.Ordinal);
        using ActivityListener? listener = PrintJobs ? ListenToPasses(passes) : null;
        Task<List<ObjectDraft>>[] written = new Task<List<ObjectDraft>>[hours];
        int next = 0;
        int appended = 0;

        // The hours written ahead hold their rows in their drafts' stores, which the session's jobs' budget counts: one past
        // the hour being written starts while that budget is less than half reserved, so that the drafts leave the jobs
        // their share, as an ingestion that appends as it goes would. The 4 hours ahead of S1's logs held 9.7 GB at its
        // peak, beside the consolidation's 14.
        QueryMemoryBudget share = options.Session.Options.JobMemoryBudget ?? QueryMemoryBudget.Jobs;
        void Start()
        {
            while (next < hours && next < Volatile.Read(ref appended) + parallel
                && (next == Volatile.Read(ref appended) || share.ReservedBytes < share.CeilingBytes / 2))
            {
                int hour = next++;
                Func<DateTimeOffset, TRecord> draw = hourly(hour);
                written[hour] = Task.Run(() => WriteHourAsync(dataset, hour, perHour, partMinutes, draw));
            }
        }

        long begin = Stopwatch.GetTimestamp();
        long hourBegan = begin;
        Start();
        for (int hour = 0; hour < hours; hour++)
        {
            List<ObjectDraft> drafts = await written[hour];
            if (clock is not null)
            {
                clock.Now = QuestionBench.Start.AddHours(hour + 1);
            }

            foreach (ObjectDraft draft in drafts)
            {
                await using (draft)
                {
                    await dataset.AppendAsync(draft);
                }
            }

            Volatile.Write(ref appended, hour + 1);
            Start();
            long compacting = Stopwatch.GetTimestamp();
            int jobs = 0;
            while (true)
            {
                long jobBegan = Stopwatch.GetTimestamp();
                CompactionResult? done;

                // The process's memory while the job runs, the hours written ahead beside it included.
                using (PeakMemory? memory = PrintJobs ? new PeakMemory() : null)
                {
                    done = await dataset.CompactAsync(new CompactionOptions { LevelZeroCeiling = 2 });
                    if (done is not null && memory is not null)
                    {
                        Console.WriteLine(JobLine(done, Stopwatch.GetElapsedTime(jobBegan).TotalSeconds, passes, memory));
                    }
                }

                if (done is null)
                {
                    break;
                }

                jobs++;
            }

            // Each hour's own pace and what its compactions took beside the cumulative pace, so that a pace that wanes as
            // the dataset grows shows by the hour.
            long now = Stopwatch.GetTimestamp();
            double seconds = Stopwatch.GetElapsedTime(begin, now).TotalSeconds;
            Console.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"  hour {hour + 1} of {hours}: {perHour / Stopwatch.GetElapsedTime(hourBegan, now).TotalSeconds / 1e6:F2} M rows a second, {jobs} jobs in {Stopwatch.GetElapsedTime(compacting, now).TotalSeconds:F1} s; {(hour + 1) * perHour / seconds / 1e6:F2} M rows a second so far"));
            hourBegan = now;
        }

        await QuestionBench.CompactAsync(dataset);
        using Process self = Process.GetCurrentProcess();
        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"  built in {Stopwatch.GetElapsedTime(begin).TotalSeconds:F0} s: a peak of {self.PeakWorkingSet64 / 1e9:F2} GB in memory, {GC.GetTotalAllocatedBytes() / 1e9:F1} GB allocated"));
        return hours * perHour;
    }

    /// <summary>
    /// Listens to the passes of the compactions, adding the seconds of each to its name in <paramref name="took"/> as it ends,
    /// with the process's private bytes when it began and the most it held while it ran: the census of a job over a variant
    /// column, <c>plan</c>, then <c>write</c>.
    /// </summary>
    internal static ActivityListener ListenToPasses(Dictionary<string, Pass> took)
    {
        const string Prefix = "vortex.dataset.compaction.";
        Dictionary<Activity, PeakMemory> running = [];
        ActivityListener listener = new ActivityListener
        {
            ShouldListenTo = static source => source.Name == VortexDiagnostics.ActivitySourceName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> created) =>
                created.Name.StartsWith(Prefix, StringComparison.Ordinal) ? ActivitySamplingResult.AllData : ActivitySamplingResult.None,
            ActivityStarted = activity =>
            {
                lock (took)
                {
                    running[activity] = new PeakMemory();
                }
            },
            ActivityStopped = activity =>
            {
                lock (took)
                {
                    string pass = activity.OperationName[Prefix.Length..];
                    Pass sum = took.GetValueOrDefault(pass);
                    if (running.Remove(activity, out PeakMemory? memory))
                    {
                        memory.Dispose();
                        sum = sum with { From = sum.Seconds == 0 ? memory.First : sum.From, Peak = Math.Max(sum.Peak, memory.Peak) };
                    }

                    took[pass] = sum with { Seconds = sum.Seconds + activity.Duration.TotalSeconds };
                }
            },
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    /// <summary>The seconds and the memory of each pass of the job just ended, for its line; the passes then emptied for the next job.</summary>
    private static string Passes(Dictionary<string, Pass> took)
    {
        lock (took)
        {
            string passes = string.Concat(took.Select(static pass => string.Create(
                CultureInfo.InvariantCulture,
                $", {pass.Key} {pass.Value.Seconds:F1} s ({pass.Value.From / 1e9:F1} to {pass.Value.Peak / 1e9:F1} GB private)")));
            took.Clear();
            return passes;
        }
    }

    /// <summary>
    /// A job's line: its trigger, style, objects, rows and bytes, its seconds and those of its passes, the ranges it ran at
    /// once and what it reserved, and the process's memory while it ran, the passes then emptied for the next job.
    /// </summary>
    internal static string JobLine(CompactionResult done, double seconds, Dictionary<string, Pass> passes, PeakMemory memory) => string.Create(
        CultureInfo.InvariantCulture,
        $"    job {done.Trigger} {done.Style}: {done.ObjectsIn} objects in, {done.ObjectsOut} out, {done.ObjectsMoved} moved, {done.Rows:N0} rows, {done.BytesIn / 1e6:F1} MB in, {done.BytesOut / 1e6:F1} MB out, {seconds:F1} s{Passes(passes)}; {done.RangesAtOnce} ranges at once, {done.MemoryReserved / 1e9:F2} GB reserved, {done.ScratchBytes / 1e6:F1} MB spilled, a peak of {memory.Peak / 1e9:F1} GB private ({memory.PeakNative / 1e9:F1} native, {memory.PeakParked / 1e9:F1} parked)");

    /// <summary>A pass's seconds, the process's private bytes when it began, and the most it held while it ran.</summary>
    internal readonly record struct Pass(double Seconds, long From, long Peak);

    /// <summary>The objects of one hour, its rows in time order, each object <paramref name="partMinutes"/> minutes of them.</summary>
    private static async Task<List<ObjectDraft>> WriteHourAsync<TRecord>(
        VortexDataset dataset, int hour, long perHour, int partMinutes, Func<DateTimeOffset, TRecord> draw)
        where TRecord : IVortexRecord<TRecord>
    {
        DateTimeOffset start = QuestionBench.Start.AddHours(hour);
        long perPart = Math.Max(1, perHour * partMinutes / 60);
        double step = 3_600.0 / perHour;
        List<ObjectDraft> drafts = [];
        TRecord[] batch = new TRecord[1 << 16];
        try
        {
            for (long first = 0; first < perHour; first += perPart)
            {
                ObjectDraft draft = dataset.StartObject();
                drafts.Add(draft);
                long last = Math.Min(perHour, first + perPart);
                for (long row = first; row < last;)
                {
                    int count = (int)Math.Min(batch.Length, last - row);
                    for (int i = 0; i < count; i++, row++)
                    {
                        batch[i] = draw(start.AddSeconds(row * step));
                    }

                    await draft.Writer.WriteAsync<TRecord>(batch.AsSpan(0, count));
                }
            }

            return drafts;
        }
        catch
        {
            foreach (ObjectDraft draft in drafts)
            {
                await draft.DisposeAsync();
            }

            throw;
        }
    }
}
