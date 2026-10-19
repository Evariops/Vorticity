using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.Expressions;
using Vorticity.Scanning;

namespace Vorticity.Benchmarks.Queries;

/// <summary>
/// The life of a row in a dataset (<c>--micro lifecycle</c>): requests generated over hours, a share of them
/// late, appended by several writers at once, each through a handle of its own, compacted after each round of
/// appends by a handle of its own while another deletes a request now and then. It counts what a row costs from
/// its append to its last rewrite: the rows and bytes the jobs rewrote per row appended, by why they ran, the seals
/// and their time, the jobs abandoned and the bytes they had written; and what a question finds: the objects a
/// window of a quarter of an hour, an hour, a day and thirty days ending at the newest row covers, sampled every
/// hour of data.
/// </summary>
/// <remarks>
/// <c>--style periods</c> compacts by periods of an hour (<c>--period-hours 24</c> for a day), the default;
/// <c>--style leveled</c> compacts by levels, as a dataset without periods does. Data time runs as fast as the
/// machine writes, so the bench says what the policy writes, not whether a machine keeps up.
/// </remarks>
internal static class LifecycleBench
{
    private static readonly (string Name, TimeSpan Width)[] Windows =
        [("15 min", TimeSpan.FromMinutes(15)), ("1 h", TimeSpan.FromHours(1)), ("1 day", TimeSpan.FromDays(1)), ("30 days", TimeSpan.FromDays(30))];

    internal static async Task<int> Run(int hours, double rate, int writers, int batchSeconds, bool leveled, int periodHours, int deletesPerHour, int fanout, bool late)
    {
        await using MemoryObjectStore memory = new MemoryObjectStore();
        await using CountingObjectStore store = new CountingObjectStore(memory);
        DatasetOptions options = new DatasetOptions
        {
            ClusteringKey = [nameof(GeneratedRequest.At)],
            Periods = leveled ? null : new PeriodOptions { Lateness = TimeSpan.FromMinutes(5), Length = periodHours >= 24 ? SliceWidth.Day : SliceWidth.Hour },
            Identifiers = [nameof(GeneratedRequest.RequestId)],
        };
        VortexDataset[] handles = new VortexDataset[writers];
        handles[0] = await VortexDataset.CreateAsync(store, GeneratedRequest.Schema, options);
        for (int w = 1; w < writers; w++)
        {
            handles[w] = await VortexDataset.OpenAsync(store);
        }

        await using VortexDataset compactor = await VortexDataset.OpenAsync(store);
        await using VortexDataset deleter = await VortexDataset.OpenAsync(store);
        CompactionOptions compaction = fanout > 0 ? new CompactionOptions { Fanout = fanout } : new CompactionOptions();
        TimeUnit unit = PeriodOptions.InstantOf(compactor.Key);

        Ledger ledger = new Ledger();
        RequestGenerator generator = new RequestGenerator(41);
        Random random = new Random(43);
        Queue<string> recent = new Queue<string>();
        List<GeneratedRequest[]> round = new List<GeneratedRequest[]>(writers);
        long appended = 0;
        DateTimeOffset newest = QuestionBench.Start;
        DateTimeOffset nextHour = QuestionBench.Start.AddHours(1);
        double deleteEvery = deletesPerHour > 0 ? 3_600.0 / deletesPerHour : 0;
        DateTimeOffset nextDelete = deletesPerHour > 0 ? QuestionBench.Start.AddSeconds(deleteEvery) : DateTimeOffset.MaxValue;
        long begin = Stopwatch.GetTimestamp();
        try
        {
            foreach (GeneratedRequest[] batch in generator.Appends(QuestionBench.Start, TimeSpan.FromHours(hours), rate, TimeSpan.FromSeconds(batchSeconds), writers, late))
            {
                round.Add(batch);
                if (round.Count < writers)
                {
                    continue;
                }

                // A round: each writer appends its batch at once, as writers of the same service do.
                await Task.WhenAll(round.Select((rows, w) => AppendAsync(handles[w], rows)));
                foreach (GeneratedRequest[] rows in round)
                {
                    appended += rows.Length;
                    newest = rows.Max(r => r.At) > newest ? rows.Max(r => r.At) : newest;
                    recent.Enqueue(rows[random.Next(rows.Length)].RequestId);
                    if (recent.Count > 64)
                    {
                        recent.Dequeue();
                    }
                }

                round.Clear();

                // Then the jobs that are due, while a delete of a request a few minutes old runs beside them.
                Task deleting = Task.CompletedTask;
                if (newest >= nextDelete && recent.Count > 0)
                {
                    nextDelete = nextDelete.AddSeconds(deleteEvery);
                    deleting = DeleteAsync(deleter, recent.Peek(), ledger);
                }

                await Task.WhenAll(DrainAsync(compactor, compaction, ledger), deleting);
                if (newest >= nextHour)
                {
                    nextHour = nextHour.AddHours(1);
                    await SampleAsync(compactor, unit, newest, ledger);
                }
            }

            await DrainAsync(compactor, compaction, ledger);
            await SampleAsync(compactor, unit, newest, ledger);
        }
        finally
        {
            foreach (VortexDataset handle in handles)
            {
                await handle.DisposeAsync();
            }
        }

        Print(hours, rate, writers, batchSeconds, leveled, periodHours, late, appended, store, ledger, Stopwatch.GetElapsedTime(begin));
        return 0;
    }

    private static async Task AppendAsync(VortexDataset dataset, GeneratedRequest[] rows)
    {
        await using ObjectDraft draft = dataset.StartObject();
        await draft.Writer.WriteAsync<GeneratedRequest>(rows);
        await dataset.AppendAsync(draft);
    }

    private static async Task DeleteAsync(VortexDataset dataset, string request, Ledger ledger)
    {
        // A handle reads the version it last saw: the writers' appends since are not in it until it refreshes.
        await dataset.RefreshAsync();
        RowChangeResult result = await dataset.DeleteAsync(VortexExpr.Parse($"RequestId = '{request}'"));
        lock (ledger)
        {
            ledger.Deletes++;
            ledger.Deleted += result.Rows;
        }
    }

    /// <summary>Runs the jobs that are due until none is, each counted by why it ran.</summary>
    private static async Task DrainAsync(VortexDataset dataset, CompactionOptions options, Ledger ledger)
    {
        await dataset.RefreshAsync();
        while (true)
        {
            long begin = Stopwatch.GetTimestamp();
            CompactionResult? result = await dataset.CompactAsync(options);
            if (result is null)
            {
                return;
            }

            double milliseconds = Stopwatch.GetElapsedTime(begin).TotalMilliseconds;
            lock (ledger)
            {
                Jobs jobs = ledger.ByTrigger.TryGetValue(result.Trigger, out Jobs? known) ? known : ledger.ByTrigger[result.Trigger] = new Jobs();
                if (result.Outcome == OperationOutcome.Abandoned)
                {
                    jobs.Abandoned++;
                    jobs.AbandonedBytes += result.BytesOut;
                    continue;
                }

                jobs.Count++;
                jobs.ObjectsIn += result.ObjectsIn;
                jobs.Moved += result.ObjectsMoved;
                jobs.Rows += result.Rows;
                jobs.BytesOut += result.BytesOut;
                jobs.Milliseconds += milliseconds;
                jobs.Longest = Math.Max(jobs.Longest, milliseconds);
            }
        }
    }

    /// <summary>The objects each window ending at <paramref name="newest"/> covers: those whose instants reach into it.</summary>
    private static async Task SampleAsync(VortexDataset dataset, TimeUnit unit, DateTimeOffset newest, Ledger ledger)
    {
        await dataset.RefreshAsync();
        string instant = dataset.Key!.InstantPath;
        long end = TimeSlicer.InstantOf(newest, unit);
        long[] counts = new long[Windows.Length];
        long objects = 0;
        await foreach (PositionedObject held in dataset.Snapshot.WalkAsync(null, 0, long.MaxValue, null, default))
        {
            objects++;
            if (!held.Entry.Summaries.TryGet(instant, out ColumnSummary summary) || summary is not { HasMax: true, Max.Kind: FilterLiteralKind.Signed })
            {
                continue;
            }

            for (int w = 0; w < Windows.Length; w++)
            {
                if (summary.Max.SignedValue >= TimeSlicer.InstantOf(newest - Windows[w].Width, unit) && (!summary.HasMin || summary.Min.SignedValue <= end))
                {
                    counts[w]++;
                }
            }
        }

        ledger.Samples.Add((objects, counts));
    }

    private static void Print(
        int hours, double rate, int writers, int batchSeconds, bool leveled, int periodHours, bool late, long appended, CountingObjectStore store, Ledger ledger, TimeSpan elapsed)
    {
        Console.WriteLine(
            $"{appended} rows over {hours} hours at {rate}/s{(late ? string.Empty : ", none late")}, {writers} writers appending {batchSeconds} s each, " +
            $"{(leveled ? "leveled" : $"periods of {(periodHours >= 24 ? "a day" : "an hour")}")}; {elapsed.TotalSeconds:F1} s");
        Console.WriteLine($"{"jobs by trigger",-18} {"jobs",6} {"objects in",11} {"moved",6} {"rows/row",9} {"MB out",9} {"ms mean",8} {"ms max",8} {"abandoned",10} {"MB lost",8}");
        long rewritten = 0;
        foreach ((CompactionTrigger trigger, Jobs jobs) in ledger.ByTrigger.OrderBy(pair => pair.Key))
        {
            rewritten += jobs.Rows;
            Console.WriteLine(
                $"{trigger,-18} {jobs.Count,6} {jobs.ObjectsIn,11} {jobs.Moved,6} {(double)jobs.Rows / appended,9:F2} {jobs.BytesOut / 1e6,9:F1} " +
                $"{(jobs.Count == 0 ? 0 : jobs.Milliseconds / jobs.Count),8:F1} {jobs.Longest,8:F1} {jobs.Abandoned,10} {jobs.AbandonedBytes / 1e6,8:F1}");
        }

        Console.WriteLine(
            $"rows rewritten per row appended {(double)rewritten / appended:F2}; store: {store.Requests} requests, " +
            $"{(double)store.BytesWritten / appended:F0} bytes written per row; {ledger.Deletes} deletes of {ledger.Deleted} rows");
        if (ledger.Samples.Count > 0)
        {
            Console.WriteLine($"objects over a window ending at the newest row, {ledger.Samples.Count} samples, mean and max:");
            for (int w = 0; w < Windows.Length; w++)
            {
                Console.WriteLine($"  {Windows[w].Name,-8} {ledger.Samples.Average(s => s.Counts[w]),7:F1} {ledger.Samples.Max(s => s.Counts[w]),5}");
            }

            Console.WriteLine($"  {"all",-8} {ledger.Samples.Average(s => s.Objects),7:F1} {ledger.Samples.Max(s => s.Objects),5}");
        }
    }

    private sealed class Ledger
    {
        internal Dictionary<CompactionTrigger, Jobs> ByTrigger { get; } = [];

        internal List<(long Objects, long[] Counts)> Samples { get; } = [];

        internal long Deletes { get; set; }

        internal long Deleted { get; set; }
    }

    private sealed class Jobs
    {
        internal long Count { get; set; }

        internal long ObjectsIn { get; set; }

        internal long Moved { get; set; }

        internal long Rows { get; set; }

        internal long BytesOut { get; set; }

        internal double Milliseconds { get; set; }

        internal double Longest { get; set; }

        internal long Abandoned { get; set; }

        internal long AbandonedBytes { get; set; }
    }
}
