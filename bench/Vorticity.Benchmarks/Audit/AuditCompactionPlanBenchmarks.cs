// Audit DS-63: what a plan of the compactions pays a dataset of thousands of objects, as a driver asks it again and again.
//
// 4,096 appends of 64 rows to a dataset sorted by its key, compacted until no job is left, in an in-memory store: thousands
// of objects over the levels. The plan: what the policy would compact next, nothing at all once the levels are calm.
using System;
using System.Threading.Tasks;

using BenchmarkDotNet.Attributes;

using Vorticity.Dataset;

namespace Vorticity.Benchmarks.Audit;

/// <summary>A plan of the compactions over thousands of objects.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public partial class AuditCompactionPlanBenchmarks
{
    private const int Appends = 4_096;
    private const int Rows = 64;

    private MemoryObjectStore _store = null!;
    private VortexDataset _dataset = null!;

    /// <summary>A row: its key and a value.</summary>
    [VortexRecord]
    public partial record struct Keyed(long Key, double Value);

    /// <summary>Writes and compacts the dataset.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _store = new MemoryObjectStore();
        _dataset = BuildAsync().GetAwaiter().GetResult();
        Console.WriteLine($"// {_dataset.ObjectCount} objects; {Plan()} jobs planned");
    }

    /// <summary>Disposes the dataset and its store.</summary>
    [GlobalCleanup]
    public void Cleanup()
    {
        _dataset.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _store.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    /// <summary>The compactions planned.</summary>
    [Benchmark]
    public int Plan() => _dataset.PlanCompactionAsync().AsTask().GetAwaiter().GetResult().Jobs.Length;

    private async Task<VortexDataset> BuildAsync()
    {
        VortexDataset dataset = await VortexDataset.CreateAsync(_store, Keyed.Schema, new DatasetOptions
        {
            Layout = new DatasetLayout { SortKey = [LayoutValue.Column(nameof(Keyed.Key))], ObjectBytes = 2 << 10, CalmAfter = null },
        }).ConfigureAwait(false);
        Keyed[] rows = new Keyed[Rows];
        Random random = new Random(43);
        for (int append = 0; append < Appends; append++)
        {
            for (int i = 0; i < rows.Length; i++)
            {
                rows[i] = new Keyed(random.NextInt64(), random.NextDouble());
            }

            await using ObjectDraft draft = dataset.StartObject();
            await draft.Writer.WriteAsync<Keyed>(rows).ConfigureAwait(false);
            await dataset.AppendAsync(draft).ConfigureAwait(false);
            if (append % 64 == 63)
            {
                while (await dataset.CompactAsync().ConfigureAwait(false) is not null)
                {
                }
            }
        }

        return dataset;
    }
}
