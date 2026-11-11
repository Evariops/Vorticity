// What a key walk over a partitioned dataset pays a key, its walks of every partition and level looked over
// at each step.
//
// A dataset partitioned by day, sorted by `Key`, of 256 days of 4,096 rows each (one object a day), in an in-memory store.
// Every key walked in order through a dataset key cursor on `Key`, each read.
using System;
using System.Threading.Tasks;

using BenchmarkDotNet.Attributes;

using Vorticity.Dataset;

namespace Vorticity.Benchmarks.Audit;

/// <summary>A key walk over the partitions of a dataset.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public partial class AuditPartitionWalkBenchmarks
{
    private const int Days = 256;
    private const int RowsPerDay = 4_096;

    private static readonly DateTime Start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private MemoryObjectStore _store = null!;
    private VortexDataset _dataset = null!;

    /// <summary>A row: its instant and its key.</summary>
    [VortexRecord]
    public partial record struct Timed([property: VortexColumn(TimeZone = "UTC")] DateTime At, long Key);

    /// <summary>Writes the dataset, an append a day.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _store = new MemoryObjectStore();
        _dataset = BuildAsync().GetAwaiter().GetResult();
        Console.WriteLine($"// {_dataset.ObjectCount} objects; {Walk()} keys walked");
    }

    /// <summary>Disposes the dataset and its store.</summary>
    [GlobalCleanup]
    public void Cleanup()
    {
        _dataset.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _store.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    /// <summary>Every key, walked in order.</summary>
    [Benchmark(OperationsPerInvoke = Days * RowsPerDay)]
    public long Walk() => WalkAsync().GetAwaiter().GetResult();

    private async Task<long> WalkAsync()
    {
        long keys = 0;
        await using DatasetKeyCursor cursor = await DatasetKeyCursor.OpenAsync(_dataset.Snapshot, nameof(Timed.Key), distinct: false, indexes: true, System.Threading.CancellationToken.None).ConfigureAwait(false);
        for (bool on = await cursor.SeekFirstAsync().ConfigureAwait(false); on; on = await cursor.NextAsync().ConfigureAwait(false))
        {
            keys++;
        }

        return keys;
    }

    private async Task<VortexDataset> BuildAsync()
    {
        VortexDataset dataset = await VortexDataset.CreateAsync(_store, Timed.Schema, new DatasetOptions
        {
            Seed = 0xA0D1_7021_5EED,
            Layout = new DatasetLayout
            {
                Partition = LayoutValue.Column(nameof(Timed.At)).Truncate(CalendarUnit.Day),
                SortKey = [LayoutValue.Column(nameof(Timed.Key))],
            },
        }).ConfigureAwait(false);
        Timed[] rows = new Timed[RowsPerDay];
        for (int day = 0; day < Days; day++)
        {
            for (int i = 0; i < RowsPerDay; i++)
            {
                rows[i] = new Timed(Start.AddDays(day).AddSeconds(i * 20), ((long)day * RowsPerDay) + i);
            }

            await using ObjectDraft draft = dataset.StartObject();
            await draft.Writer.WriteAsync<Timed>(rows).ConfigureAwait(false);
            await dataset.AppendAsync(draft).ConfigureAwait(false);
        }

        return dataset;
    }
}
