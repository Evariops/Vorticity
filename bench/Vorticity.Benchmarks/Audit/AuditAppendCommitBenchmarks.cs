// Audit DS-67: what a commit pays an append, its entries summed, its header measured, its tree's edge rewritten.
//
// A dataset sorted by its key, 1,024 objects appended before, in an in-memory store; each operation appends one more
// object of 256 rows and commits it.
using System;
using System.Threading.Tasks;

using BenchmarkDotNet.Attributes;

using Vorticity.Dataset;

namespace Vorticity.Benchmarks.Audit;

/// <summary>An append of a small object to a dataset of a thousand.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public partial class AuditAppendCommitBenchmarks
{
    private const int Before = 1_024;
    private const int Rows = 256;

    private readonly Random _random = new Random(29);
    private readonly Keyed[] _rows = new Keyed[Rows];
    private MemoryObjectStore _store = null!;
    private VortexDataset _dataset = null!;

    /// <summary>A row: its key and a value.</summary>
    [VortexRecord]
    public partial record struct Keyed(long Key, double Value);

    /// <summary>Writes the objects appended before.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _store = new MemoryObjectStore();
        _dataset = VortexDataset.CreateAsync(_store, Keyed.Schema, new DatasetOptions
        {
            Layout = new DatasetLayout { SortKey = [LayoutValue.Column(nameof(Keyed.Key))], CalmAfter = null },
        }).AsTask().GetAwaiter().GetResult();
        for (int i = 0; i < Before; i++)
        {
            Append();
        }

        Console.WriteLine($"// {_dataset.ObjectCount} objects");
    }

    /// <summary>Disposes the dataset and its store.</summary>
    [GlobalCleanup]
    public void Cleanup()
    {
        _dataset.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _store.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    /// <summary>One object appended and committed.</summary>
    [Benchmark]
    public ulong Append() => AppendAsync().GetAwaiter().GetResult();

    private async Task<ulong> AppendAsync()
    {
        for (int i = 0; i < _rows.Length; i++)
        {
            _rows[i] = new Keyed(_random.NextInt64(), _random.NextDouble());
        }

        await using ObjectDraft draft = _dataset.StartObject();
        await draft.Writer.WriteAsync<Keyed>(_rows).ConfigureAwait(false);
        return await _dataset.AppendAsync(draft).ConfigureAwait(false);
    }
}
