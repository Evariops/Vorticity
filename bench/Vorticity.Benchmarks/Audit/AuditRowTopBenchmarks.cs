// Audit AG-02: what a top of rows by a column the layout does not order costs.
//
// A dataset of 64 objects of 65,536 rows (a sorted `Key`, a pseudo-random `Value`, a `Label`) in an in-memory store is
// asked the 100 rows of the least value, and of the greatest: the places of the best rows first, from the column alone,
// each candidate row weighed against the last kept once the top holds its rows, then those rows read. The rows are read.
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using BenchmarkDotNet.Attributes;

using Vorticity.Dataset;

namespace Vorticity.Benchmarks.Audit;

/// <summary>The rows of the least and greatest values of a column over a dataset.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public partial class AuditRowTopBenchmarks
{
    private const int Objects = 64;
    private const int RowsPerObject = 65_536;

    private MemoryObjectStore _store = null!;
    private VortexDataset _dataset = null!;

    /// <summary>A row: its key, rising; its value; its label.</summary>
    [VortexRecord]
    public partial record struct Item(long Key, long Value, string Label);

    /// <summary>Writes the dataset.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _store = new MemoryObjectStore();
        _dataset = BuildAsync().GetAwaiter().GetResult();
        Console.WriteLine($"// least {Least()}, greatest {Greatest()}");
    }

    /// <summary>Disposes the dataset and its store.</summary>
    [GlobalCleanup]
    public void Cleanup()
    {
        _dataset.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _store.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    /// <summary>The 100 rows of the least value.</summary>
    [Benchmark]
    public long Least() => ReadAsync(_dataset.Scan<Item>().OrderBy(r => r.Value).Select(r => r.Label).Take(100)).GetAwaiter().GetResult();

    /// <summary>The 100 rows of the greatest value.</summary>
    [Benchmark]
    public long Greatest() => ReadAsync(_dataset.Scan<Item>().OrderByDescending(r => r.Value).Select(r => r.Label).Take(100)).GetAwaiter().GetResult();

    private static async Task<long> ReadAsync(Projection<string> labels)
    {
        long read = 0;
        await foreach (string label in labels)
        {
            read += label.Length;
        }

        return read;
    }

    private async Task<VortexDataset> BuildAsync()
    {
        VortexDataset dataset = await VortexDataset.CreateAsync(_store, Item.Schema, new DatasetOptions
        {
            Seed = 0xA0D1_7002_5EED,
            Layout = new DatasetLayout { SortKey = [LayoutValue.Column(nameof(Item.Key))] },
        }).ConfigureAwait(false);

        Item[] rows = new Item[RowsPerObject];
        for (int part = 0; part < Objects; part++)
        {
            Random random = new Random(part);
            for (int i = 0; i < rows.Length; i++)
            {
                long key = ((long)part * RowsPerObject) + i;
                rows[i] = new Item(key, random.NextInt64(), $"item-{key % 997}");
            }

            await using ObjectDraft draft = dataset.StartObject();
            await draft.Writer.WriteAsync<Item>(rows).ConfigureAwait(false);
            await dataset.AppendAsync(draft).ConfigureAwait(false);
        }

        return dataset;
    }
}
