// Audit DS-04 and DS-05: what a question over a dataset of many objects pays to plan, besides its reads.
//
// Two datasets of 2,097,152 rows (an `At` an hour of 4,096 rows rising, a sorted `Key`, a `Service` of 8
// values, a pseudo-random `Value` in [0, 1)) in an in-memory store:
// - "small": 2,048 objects of 1,024 rows, one append each, left uncompacted;
// - "sealed": periods of an hour, 512 of them, appended in parts of 1,024 rows and compacted, each sealed
//   into one object that keeps its columns' counts, which an aggregation's states may come from.
// The questions: a group by service of every row (the counts answer it on "sealed"); of the rows whose value
// is below one half (read as rows); of a range of keys that cuts two objects at its edges; and a filtered
// count. Each walks the tree once or more to find its objects, and weighs each object's ways to answer.
using System;
using System.Threading.Tasks;

using BenchmarkDotNet.Attributes;

using Vorticity.Dataset;

namespace Vorticity.Benchmarks.Audit;

/// <summary>Questions over the many objects of a dataset.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public partial class AuditDatasetQuestionBenchmarks
{
    private const int Rows = 2_097_152;
    private const int RowsPerHour = 4_096;

    private static readonly DateTime Start = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);

    private MemoryObjectStore _store = null!;
    private VortexDataset _dataset = null!;

    /// <summary>The dataset: "small" objects left uncompacted, or "sealed" periods of an hour.</summary>
    [Params("small", "sealed")]
    public string Shape { get; set; } = "small";

    /// <summary>The objects that stay open between questions: the default, 32, or every one.</summary>
    [Params(32, 4_096)]
    public int MaxOpen { get; set; } = 32;

    /// <summary>An event: its instant and key, rising; its service; its value.</summary>
    [VortexRecord]
    public partial record struct Event(DateTime At, long Key, string Service, double Value);

    /// <summary>A service's count.</summary>
    [VortexRecord]
    public partial record struct ServiceCount(string Service, long Count);

    /// <summary>A service's count, sum and greatest value.</summary>
    [VortexRecord]
    public partial record struct ServiceSum(string Service, long Count, double Sum, double Max);

    /// <summary>Writes the dataset.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _store = new MemoryObjectStore();
        _dataset = BuildAsync().GetAwaiter().GetResult();
        int objects = 0;
        int counted = 0;
        foreach (PositionedObject held in _dataset.ScanBuilder().ObjectsAsync().ToBlockingEnumerable())
        {
            objects++;
            counted += held.Entry.Contents is { Counts.Count: > 0 } ? 1 : 0;
        }

        // What the objects a question opens keep while they stay open: the heap after a full collection, before and after.
        long before = GC.GetTotalMemory(forceFullCollection: true);
        long answers = GroupByAll() + GroupBy() + GroupByRange() + Count();
        long after = GC.GetTotalMemory(forceFullCollection: true);
        Console.WriteLine($"// {Shape}, {MaxOpen} open: {objects} objects, {counted} with counts; answers {answers}; "
            + $"held after the questions {(after - before) / 1024.0:F0} KB, {(after - before) / (double)Math.Min(objects, MaxOpen) / 1024.0:F1} KB an open object");
    }

    /// <summary>Disposes the dataset and its store.</summary>
    [GlobalCleanup]
    public void Cleanup()
    {
        _dataset.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _store.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    /// <summary>Every row, counted by service.</summary>
    [Benchmark]
    public long GroupByAll() => GroupsAsync(_dataset.Scan<Event>()).GetAwaiter().GetResult();

    /// <summary>The rows whose value is below one half, counted by service.</summary>
    [Benchmark]
    public long GroupBy() => GroupsAsync(_dataset.Scan<Event>().Where(e => e.Value < 0.5)).GetAwaiter().GetResult();

    /// <summary>The rows of a range of keys that cuts two objects, counted by service.</summary>
    [Benchmark]
    public long GroupByRange() => GroupsAsync(_dataset.Scan<Event>()
        .Where(e => e.Key >= (300L * 1_024) + 100 && e.Key < (1_700L * 1_024) + 900)).GetAwaiter().GetResult();

    /// <summary>
    /// Every row's value summed and its greatest, by service (audit AG-21): on "sealed", each object's states answer it,
    /// a batch of states an object, whose measure's columns are found by name.
    /// </summary>
    [Benchmark]
    public long GroupBySums() => SumsAsync(_dataset.Scan<Event>()).GetAwaiter().GetResult();

    /// <summary>The rows whose value is below one half.</summary>
    [Benchmark]
    public long Count() => _dataset.Scan<Event>().Where(e => e.Value < 0.5).CountAsync().AsTask().GetAwaiter().GetResult();

    private static async Task<long> GroupsAsync(Scan<Event> scan)
    {
        long read = 0;
        await foreach (Columns<ServiceCount> batch in scan.GroupBy(e => e.Service).Select(g => (g.Key, g.Count())).As<ServiceCount>())
        {
            read += Vorticity.Bench.Scenarios.Consume.Typed(batch);
        }

        return read;
    }

    private static async Task<long> SumsAsync(Scan<Event> scan)
    {
        long read = 0;
        await foreach (Columns<ServiceSum> batch in scan.GroupBy(e => e.Service)
            .Select(g => (g.Key, g.Count(), g.Sum(e => e.Value), g.Max(e => e.Value))).As<ServiceSum>())
        {
            read += Vorticity.Bench.Scenarios.Consume.Typed(batch);
        }

        return read;
    }

    private async Task<VortexDataset> BuildAsync()
    {
        bool sealedHours = Shape == "sealed";
        DatasetOptions options = sealedHours
            ? new DatasetOptions
            {
                Seed = 0xA0D1_7004_5EED,
                Layout = new DatasetLayout
                {
                    Partition = LayoutValue.Column(nameof(Event.At)).Truncate(CalendarUnit.Hour),
                    SortKey = [LayoutValue.Column(nameof(Event.Key))],
                    Organization = LayoutOrganization.Leveled,
                },
                ContentsObjectBytes = 0,
                SheetObjectBytes = 0,
                MaxOpenObjects = MaxOpen,
            }
            : new DatasetOptions
            {
                Seed = 0xA0D1_7004_5EED,
                Layout = new DatasetLayout { SortKey = [LayoutValue.Column(nameof(Event.Key))] },
                MaxOpenObjects = MaxOpen,
            };

        VortexDataset dataset = await VortexDataset.CreateAsync(_store, Event.Schema, options).ConfigureAwait(false);
        string[] services = ["auth", "billing", "catalog", "checkout", "search", "shipping", "users", "web"];
        Event[] rows = new Event[1_024];
        for (int part = 0; part < Rows / rows.Length; part++)
        {
            Random random = new Random(part);
            for (int i = 0; i < rows.Length; i++)
            {
                long key = ((long)part * rows.Length) + i;
                rows[i] = new Event(Start.AddHours(key / RowsPerHour).AddMilliseconds(key % RowsPerHour), key, services[random.Next(services.Length)], random.NextDouble());
            }

            await using ObjectDraft draft = dataset.StartObject();
            await draft.Writer.WriteAsync<Event>(rows).ConfigureAwait(false);
            await dataset.AppendAsync(draft).ConfigureAwait(false);
        }

        while (sealedHours && await dataset.CompactAsync(new CompactionOptions { LevelZeroCeiling = 2, TargetBytesAtLevelOne = 1L << 30 }).ConfigureAwait(false) is not null)
        {
        }

        return dataset;
    }
}
