// What a question answered by a dataset's rollups pays to choose them, every rollup decoded at each question.
//
// A dataset of 1,024 hours of hits (64 rows an hour, an append an hour, compacted), partitioned by the hour, rolled up
// with a fan-out of 2: about two thousand rollups of every rank. The question: the hits by service, which the rollups'
// totals answer.
using System;
using System.Threading.Tasks;

using BenchmarkDotNet.Attributes;

using Vorticity.Dataset;

namespace Vorticity.Benchmarks.Audit;

/// <summary>A count by service a dataset's rollups answer.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public partial class AuditRollupChoiceBenchmarks
{
    private const int Hours = 1_024;
    private const int RowsPerHour = 64;

    private static readonly DateTimeOffset Start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly string[] Services = ["api", "auth", "billing", "cart"];

    private MemoryObjectStore _store = null!;
    private VortexDataset _dataset = null!;

    /// <summary>A hit: its instant, service, status and bytes.</summary>
    [VortexRecord]
    public partial record struct Hit(DateTimeOffset At, string Service, short Status, long Bytes);

    /// <summary>A service's count.</summary>
    [VortexRecord]
    public partial record struct ServiceCount(string Service, long Count);

    /// <summary>Writes, compacts and rolls up the dataset.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _store = new MemoryObjectStore();
        _dataset = BuildAsync().GetAwaiter().GetResult();
        Console.WriteLine($"// {_dataset.ObjectCount} objects; {Count()} services counted");
    }

    /// <summary>Disposes the dataset and its store.</summary>
    [GlobalCleanup]
    public void Cleanup()
    {
        _dataset.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _store.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    /// <summary>The hits by service.</summary>
    [Benchmark]
    public long Count() => CountAsync().GetAwaiter().GetResult();

    private async Task<long> CountAsync()
    {
        long read = 0;
        await foreach (Columns<ServiceCount> batch in _dataset.Scan<Hit>().GroupBy(h => h.Service).Select(g => (g.Key, g.Count())).As<ServiceCount>())
        {
            read += Vorticity.Bench.Scenarios.Consume.Typed(batch);
        }

        return read;
    }

    private async Task<VortexDataset> BuildAsync()
    {
        VortexDataset dataset = await VortexDataset.CreateAsync(_store, Hit.Schema, new DatasetOptions
        {
            Seed = 0x5EED_0D_5009,
            SheetObjectBytes = 0,
            ContentsObjectBytes = 0,
            RollupFanout = 2,
            Layout = new DatasetLayout
            {
                Partition = LayoutValue.Column(nameof(Hit.At)).Truncate(CalendarUnit.Hour),
                SortKey = [LayoutValue.Column(nameof(Hit.At))],
                Organization = LayoutOrganization.Leveled,
            },
        }).ConfigureAwait(false);
        Random random = new Random(89);
        Hit[] hits = new Hit[RowsPerHour];
        for (int hour = 0; hour < Hours; hour++)
        {
            for (int i = 0; i < RowsPerHour; i++)
            {
                hits[i] = new Hit(Start.AddHours(hour).AddSeconds(i * 50), Services[random.Next(Services.Length)], 200, random.NextInt64(100, 100_000));
            }

            await using ObjectDraft draft = dataset.StartObject();
            await draft.Writer.WriteAsync<Hit>(hits).ConfigureAwait(false);
            await dataset.AppendAsync(draft).ConfigureAwait(false);
        }

        while (await dataset.CompactAsync(new CompactionOptions { LevelZeroCeiling = 1 }).ConfigureAwait(false) is not null)
        {
        }

        int put = await dataset.RollUpAsync().ConfigureAwait(false);
        Console.WriteLine($"// {put} rollups put");
        return dataset;
    }
}
