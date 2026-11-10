// Audit DS-22: what a walk of a composite sort key pays a key, its object's tuples read a granule at a time.
//
// A dataset sorted by (Service, Sequence), of 16 appends of 65,536 rows (40 services), compacted, in an in-memory store.
// Every key walked in order through the dataset's key cursor, its tuple's bytes read in place at each step: each object's
// rows are its entries, its tuples read through a scan of 8,192 rows a granule.
using System;
using System.Threading.Tasks;

using BenchmarkDotNet.Attributes;

using Vorticity.Dataset;

namespace Vorticity.Benchmarks.Audit;

/// <summary>A walk of a dataset's composite sort key.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public partial class AuditCompositeWalkBenchmarks
{
    private const int Appends = 16;
    private const int RowsPerAppend = 65_536;

    private MemoryObjectStore _store = null!;
    private VortexDataset _dataset = null!;

    /// <summary>A call: its service, its sequence and its latency.</summary>
    [VortexRecord]
    public partial record struct Call(string Service, long Sequence, double LatencyMs);

    /// <summary>Writes and compacts the dataset.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _store = new MemoryObjectStore();
        _dataset = BuildAsync().GetAwaiter().GetResult();
        Console.WriteLine($"// {_dataset.ObjectCount} objects; {Walk()} key bytes walked");
    }

    /// <summary>Disposes the dataset and its store.</summary>
    [GlobalCleanup]
    public void Cleanup()
    {
        _dataset.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _store.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    /// <summary>Every key, walked in order, its tuple's bytes read.</summary>
    [Benchmark(OperationsPerInvoke = Appends * RowsPerAppend)]
    public long Walk() => WalkAsync().GetAwaiter().GetResult();

    private async Task<long> WalkAsync()
    {
        long bytes = 0;
        await using DatasetKeyCursor cursor = await DatasetKeyCursor.OpenAsync(_dataset).ConfigureAwait(false);
        for (bool on = await cursor.SeekFirstAsync().ConfigureAwait(false); on; on = await cursor.NextAsync().ConfigureAwait(false))
        {
            bytes += cursor.KeyBytes.Length;
        }

        return bytes;
    }

    private async Task<VortexDataset> BuildAsync()
    {
        VortexDataset dataset = await VortexDataset.CreateAsync(_store, Call.Schema, new DatasetOptions
        {
            Seed = 0xC0_2207_5EED,
            Layout = new DatasetLayout
            {
                SortKey = [LayoutValue.Column(nameof(Call.Service)), LayoutValue.Column(nameof(Call.Sequence))],
                MergeFanout = 4,
                CalmAfter = null,
            },
        }).ConfigureAwait(false);
        Random random = new Random(23);
        Call[] rows = new Call[RowsPerAppend];
        long sequence = 0;
        for (int append = 0; append < Appends; append++)
        {
            for (int i = 0; i < rows.Length; i++)
            {
                rows[i] = new Call($"svc-{random.Next(40):D2}", sequence++, random.NextDouble() * 100);
            }

            await using ObjectDraft draft = dataset.StartObject();
            await draft.Writer.WriteAsync<Call>(rows).ConfigureAwait(false);
            await dataset.AppendAsync(draft).ConfigureAwait(false);
        }

        while (await dataset.CompactAsync().ConfigureAwait(false) is not null)
        {
        }

        return dataset;
    }
}
