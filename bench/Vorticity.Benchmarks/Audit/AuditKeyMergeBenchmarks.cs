// Audit DS-30: what a key-ordered read of a dataset pays its merge of objects whose keys do not interleave.
//
// A dataset of 2,097,152 rows (a sorted `Key`, a `Value`) in an in-memory store, its sort key `Key`: 256 objects of 8,192
// rows, each its own run of keys, as a level's objects are. Every row read in key order, its values consumed: the merge
// opens each object once the one before is past its bound, and cuts each batch into the run below that bound.
using System;
using System.Threading.Tasks;

using BenchmarkDotNet.Attributes;

using Vorticity.Dataset;

namespace Vorticity.Benchmarks.Audit;

/// <summary>A key-ordered read of a dataset of disjoint objects.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public partial class AuditKeyMergeBenchmarks
{
    private const int Rows = 2_097_152;
    private const int RowsPerObject = 8_192;

    private MemoryObjectStore _store = null!;
    private VortexDataset _dataset = null!;

    /// <summary>A row: its key and its value.</summary>
    [VortexRecord]
    public partial record struct Keyed(long Key, double Value);

    /// <summary>Writes the dataset.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _store = new MemoryObjectStore();
        _dataset = BuildAsync().GetAwaiter().GetResult();
        Console.WriteLine($"// {Ordered()} rows read in key order");
    }

    /// <summary>Disposes the dataset and its store.</summary>
    [GlobalCleanup]
    public void Cleanup()
    {
        _dataset.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _store.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    /// <summary>Every row, in key order, read.</summary>
    [Benchmark]
    public long Ordered() => OrderedAsync().GetAwaiter().GetResult();

    private async Task<long> OrderedAsync()
    {
        long read = 0;
        await foreach (RecordBatch batch in _dataset.ScanBuilder().InKeyOrder(nameof(Keyed.Key)).ExecuteAsync().ConfigureAwait(false))
        {
            read += Vorticity.Bench.Scenarios.Consume.Batch(batch);
        }

        return read;
    }

    private async Task<VortexDataset> BuildAsync()
    {
        DatasetOptions options = new DatasetOptions
        {
            Seed = 0xA0D1_7030_5EED,
            Layout = new DatasetLayout { SortKey = [LayoutValue.Column(nameof(Keyed.Key))] },
        };

        VortexDataset dataset = await VortexDataset.CreateAsync(_store, Keyed.Schema, options).ConfigureAwait(false);
        Keyed[] rows = new Keyed[RowsPerObject];
        Random random = new Random(53);
        for (int part = 0; part < Rows / RowsPerObject; part++)
        {
            for (int i = 0; i < rows.Length; i++)
            {
                rows[i] = new Keyed(((long)part * RowsPerObject) + i, random.NextDouble());
            }

            await using ObjectDraft draft = dataset.StartObject();
            await draft.Writer.WriteAsync<Keyed>(rows).ConfigureAwait(false);
            await dataset.AppendAsync(draft).ConfigureAwait(false);
        }

        return dataset;
    }
}
