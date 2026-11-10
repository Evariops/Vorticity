// Audit DS-08: what a question answered granule by granule pays a granule, its filter judged on each one's statistics.
//
// 2,560,000 calls of two services, four routes each, 200 pods, sorted by the service, the route, then the instant, written
// in one day and consolidated into one object of about 300 granules (DatasetGranuleJudgmentTests' layout, four times the
// rows). The question: the calls of each pod on one route, which the granules' statistics answer — the route's refute or
// prove the filter granule by granule, the pod's count its rows.
using System;
using System.Linq;
using System.Threading.Tasks;

using BenchmarkDotNet.Attributes;

using Vorticity.Dataset;

namespace Vorticity.Benchmarks.Audit;

/// <summary>A group by answered by an object's granules.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public partial class AuditGranuleSettleBenchmarks
{
    private const int Rows = 2_560_000;

    private static readonly DateTime Start = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);

    private MemoryObjectStore _store = null!;
    private VortexDataset _dataset = null!;

    /// <summary>A call: its instant, service, route, pod and status.</summary>
    [VortexRecord]
    public partial record struct Call(DateTime At, string Service, string Route, string Pod, short Status);

    /// <summary>A pod's count.</summary>
    [VortexRecord]
    public partial record struct PodCount(string Pod, long Count);

    /// <summary>Writes the dataset.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _store = new MemoryObjectStore();
        _dataset = BuildAsync().GetAwaiter().GetResult();
        int objects = 0;
        foreach (PositionedObject held in _dataset.ScanBuilder().ObjectsAsync().ToBlockingEnumerable())
        {
            objects++;
        }

        Console.WriteLine($"// {objects} objects; {Pods()} pods");
    }

    /// <summary>Disposes the dataset and its store.</summary>
    [GlobalCleanup]
    public void Cleanup()
    {
        _dataset.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _store.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    /// <summary>Each pod's calls on one route, read.</summary>
    [Benchmark]
    public long Pods() => PodsAsync().GetAwaiter().GetResult();

    private async Task<long> PodsAsync()
    {
        long read = 0;
        await foreach (Columns<PodCount> batch in _dataset.Scan<Call>().Where(c => c.Route == "/svc0/r1")
            .GroupBy(c => c.Pod).Select(g => (g.Key, g.Count())).As<PodCount>())
        {
            read += Vorticity.Bench.Scenarios.Consume.Typed(batch);
        }

        return read;
    }

    private async Task<VortexDataset> BuildAsync()
    {
        Random random = new Random(53);
        Call[] calls = [.. Enumerable.Range(0, Rows).Select(i => new Call(
            Start.AddMilliseconds(i * 11L), $"svc-{i % 2}", $"/svc{i % 2}/r{(i / 2) % 4}", $"pod-{random.Next(200):D3}", (short)(random.Next(10) == 0 ? 500 : 200)))];
        VortexDataset dataset = await VortexDataset.CreateAsync(_store, Call.Schema, new DatasetOptions
        {
            Layout = new DatasetLayout
            {
                Partition = LayoutValue.Column(nameof(Call.At)).Truncate(CalendarUnit.Day),
                SortKey = [LayoutValue.Column(nameof(Call.Service)), LayoutValue.Column(nameof(Call.Route)), LayoutValue.Column(nameof(Call.At))],
                IndexedPrefix = 1,
                ObjectBytes = 1L << 30,
                MergeFanout = 4,
                CalmAfter = null,
            },
            ContentsObjectBytes = 0,
            SheetObjectBytes = 0,
        }).ConfigureAwait(false);
        for (int part = 0; part < 4; part++)
        {
            await using (ObjectDraft draft = dataset.StartObject())
            {
                await draft.Writer.WriteAsync<Call>(calls[(part * (Rows / 4))..((part + 1) * (Rows / 4))]).ConfigureAwait(false);
                await dataset.AppendAsync(draft).ConfigureAwait(false);
            }

            while (await dataset.CompactAsync().ConfigureAwait(false) is not null)
            {
            }
        }

        await dataset.ConsolidateAsync().ConfigureAwait(false);
        return dataset;
    }
}
