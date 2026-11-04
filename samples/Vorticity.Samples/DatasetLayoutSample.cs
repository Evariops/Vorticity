using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Vorticity.Dataset;

namespace Vorticity.Samples;

/// <summary>A call to a service, as a dataset laid out by day and by service keeps it.</summary>
[VortexRecord]
public partial record struct ServiceCall(DateTimeOffset At, string Service, short Status, double LatencyMs);

/// <summary>
/// The layout page: a dataset partitioned by day and sorted by service then instant, appended a quarter of an hour at a
/// time over two days; its small objects merged by size class within each day, then each day consolidated once calm; a
/// question on one service of one day, its plan and its reads; and the layout its questions would call for.
/// </summary>
internal static class DatasetLayoutSample
{
    private static readonly DateTimeOffset Start = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

    internal static async Task RunAsync()
    {
        await using CountingObjectStore store = new CountingObjectStore(new MemoryObjectStore(), ownsInner: true);

        // Data time, which a partition's calm follows here: the sample writes two days in a second.
        DataClock clock = new DataClock { Now = Start };
        DatasetOptions options = new DatasetOptions
        {
            Layout = new DatasetLayout
            {
                Partition = LayoutValue.Column(nameof(ServiceCall.At)).Truncate(CalendarUnit.Day),
                SortKey = [LayoutValue.Column(nameof(ServiceCall.Service)), LayoutValue.Column(nameof(ServiceCall.At))],
                IndexedPrefix = 1,
                MergeFanout = 4,
                CalmAfter = TimeSpan.FromHours(1),
            },
            TimeProvider = clock,
        };
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, ServiceCall.Schema, options);
        Console.WriteLine($"created: partition {dataset.Layout!.Partition}, key {string.Join(", ", dataset.Layout.SortKey)}, " +
            $"indexed prefix {dataset.Layout.IndexedPrefix}, {dataset.Layout.Organization?.ToString() ?? "organized as the layout calls for"}");

        // Two days of calls, appended a quarter of an hour at a time; after each append, the jobs due: the merges of a size
        // class within a day, and a day's consolidation once it is calm, an hour after its last row.
        Random random = new Random(11);
        Jobs jobs = new Jobs();
        for (int quarter = 0; quarter < 2 * 96; quarter++)
        {
            ServiceCall[] calls = [.. Enumerable.Range(0, 4_000).Select(i => new ServiceCall(
                Start.AddMinutes((quarter * 15) + (i * 15.0 / 4_000)),
                $"svc-{random.Next(40):D2}",
                random.Next(50) == 0 ? (short)500 : (short)200,
                Math.Round(random.NextDouble() * 200, 1)))];
            clock.Now = calls[^1].At;
            await using (ObjectDraft draft = dataset.StartObject())
            {
                await draft.Writer.WriteAsync<ServiceCall>(calls);
                await dataset.AppendAsync(draft);
            }

            while (await dataset.CompactAsync() is { } done)
            {
                jobs.Add(done);
            }
        }

        Console.WriteLine($"appended {dataset.RowCount} calls in 192 appends; {jobs}; {await LevelsAsync(dataset)}");
        foreach (PartitionCompaction partition in (await dataset.PlanCompactionAsync()).Partitions)
        {
            Console.WriteLine($"  {partition.Partition}: {partition.Objects} objects of its size classes, {partition.Bytes} bytes, {(partition.Calm ? "calm" : "not calm yet")}");
        }

        // Two hours after its last row, the second day is calm too: its objects are consolidated into objects disjoint by key.
        clock.Now = clock.Now.AddHours(2);
        jobs = new Jobs();
        while (await dataset.CompactAsync() is { } done)
        {
            jobs.Add(done);
        }

        Console.WriteLine($"two hours later: {jobs}; {await LevelsAsync(dataset)}");

        // One service of one day: the partition passes the other day over, the key's index the other services' granules. The
        // plan, analyzed, opens the object it reads as rows and reads its key's index; the read is a new handle's, cold.
        DateTimeOffset second = Start.AddDays(1);
        ScanPlan plan = await Calls(dataset).ExplainAsync(analyze: true);
        Console.WriteLine($"plan: {string.Join("; ", plan.Units.Select(unit => $"{unit.Alternative}, {unit.Detail}"))}");
        Console.WriteLine($"  cost: {plan.Cost?.Requests} requests, {plan.Cost?.Bytes} bytes, {plan.Cost?.Predicted.TotalMilliseconds:F0} ms cold");
        await using (VortexDataset cold = await VortexDataset.OpenAsync(store, options))
        {
            store.Reset();
            List<ServiceCall> read = await Calls(cold).ToRecordsAsync().ToListAsync();
            Console.WriteLine($"svc-07 on the second day: {read.Count} calls; {store.CountOf(ObjectOperation.Read)} reads, {store.BytesRead} bytes of {await BytesAsync(dataset)}");
            store.Reset();
            Console.WriteLine($"  counted: {await Calls(cold).CountAsync()} calls; {store.CountOf(ObjectOperation.Read)} reads, {store.BytesRead} bytes");
        }

        // The layout the questions asked would call for: the columns their filters read, with the bytes an order by each
        // would have spared the objects they read as rows. A count of the failed calls is answered by the objects' counts
        // and reads no row; the calls themselves are read from the rows.
        for (int asked = 0; asked < 3; asked++)
        {
            await dataset.Scan<ServiceCall>().Where(c => c.Status == 500).ToRecordsAsync().CountAsync();
        }

        LayoutAdvice advice = await dataset.RecommendLayoutAsync();
        Console.WriteLine($"advice: {string.Join(", ", advice.Components.Select(component => $"{component.Path} ({component.BytesAvoided} bytes spared)"))}; index the first {advice.IndexedPrefix}");

        Scan<ServiceCall> Calls(VortexDataset handle) =>
            handle.Scan<ServiceCall>().Where(c => c.Service == "svc-07" && c.At >= second && c.At < second.AddDays(1));
    }

    /// <summary>The objects of each level, by partition.</summary>
    private static async Task<string> LevelsAsync(VortexDataset dataset)
    {
        List<DataObject> objects = await dataset.ListObjectsAsync().ToListAsync();
        return string.Join(", ", objects.GroupBy(static held => held.Level).OrderBy(static level => level.Key)
            .Select(static level => $"level {level.Key}: {level.Count()} objects"));
    }

    /// <summary>The bytes of the dataset's objects.</summary>
    private static async Task<long> BytesAsync(VortexDataset dataset) =>
        (await dataset.ListObjectsAsync().ToListAsync()).Sum(static held => held.Bytes);

    /// <summary>The jobs a compaction ran, by what triggered them: their count and the rows they rewrote.</summary>
    private sealed class Jobs
    {
        private readonly SortedDictionary<CompactionTrigger, (int Jobs, long Rows)> _done = [];

        internal void Add(CompactionResult done)
        {
            (int jobs, long rows) = _done.GetValueOrDefault(done.Trigger);
            _done[done.Trigger] = (jobs + 1, rows + done.Rows);
        }

        public override string ToString() =>
            _done.Count == 0 ? "no job" : string.Join(", ", _done.Select(static job => $"{job.Value.Jobs} {job.Key} jobs rewrote {job.Value.Rows} rows"));
    }

    /// <summary>A clock the sample sets: the time of the last row appended.</summary>
    private sealed class DataClock : TimeProvider
    {
        internal DateTimeOffset Now { get; set; }

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
