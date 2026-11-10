// Audit DS-16: what a lookup pays for the objects it cannot look up, read as any, ahead, their batches copied.
//
// 16 appends of 65,536 rows (a rising key, an identifier of 16,384 values drawn at random, a message of 48 characters, a
// value), an identifier declared but no object large enough to keep a table of contents, so that no object takes the
// lookup: each is read as a scan reads any, 16 at once, and every batch that keeps a row is copied to wait for its turn.
// The question asks the rows of one identifier, some 4 an object, scattered: their values are summed.
using System;
using System.Linq;
using System.Threading.Tasks;

using BenchmarkDotNet.Attributes;

using Vorticity.Dataset;

namespace Vorticity.Benchmarks.Audit;

/// <summary>The rows of one identifier over objects that keep no index of it.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public partial class AuditLookupAheadBenchmarks
{
    private const int Appends = 16;
    private const int RowsPerAppend = 65_536;

    private MemoryObjectStore _store = null!;
    private VortexDataset _dataset = null!;

    /// <summary>A row: its key, its identifier, its message and its value.</summary>
    [VortexRecord]
    public partial record struct Entry(long Key, long Id, string Message, double Value);

    /// <summary>Writes the dataset.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _store = new MemoryObjectStore();
        _dataset = BuildAsync().GetAwaiter().GetResult();
        Console.WriteLine($"// {_dataset.ObjectCount} objects; {Sought():F1} summed over the identifier's rows");
    }

    /// <summary>Disposes the dataset and its store.</summary>
    [GlobalCleanup]
    public void Cleanup()
    {
        _dataset.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _store.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    /// <summary>The rows of one identifier, their values summed.</summary>
    [Benchmark]
    public double Sought() => SumAsync().GetAwaiter().GetResult();

    private async Task<double> SumAsync()
    {
        double sum = 0;
        await foreach (Entry entry in _dataset.Scan<Entry>().Where(e => e.Id == 4_321).ToRecordsAsync().ConfigureAwait(false))
        {
            sum += entry.Value;
        }

        return sum;
    }

    private async Task<VortexDataset> BuildAsync()
    {
        VortexDataset dataset = await VortexDataset.CreateAsync(_store, Entry.Schema, new DatasetOptions
        {
            Layout = new DatasetLayout { SortKey = [LayoutValue.Column(nameof(Entry.Key))], CalmAfter = null },
            Identifiers = [nameof(Entry.Id)],
            ContentsObjectBytes = long.MaxValue,
        }).ConfigureAwait(false);
        Random random = new Random(67);
        long key = 0;
        for (int append = 0; append < Appends; append++)
        {
            Entry[] entries = [.. Enumerable.Range(0, RowsPerAppend).Select(_ => new Entry(
                key++, random.Next(16_384), $"message {random.NextInt64():X16} {random.NextInt64():X16} end", random.NextDouble()))];
            await using ObjectDraft draft = dataset.StartObject();
            await draft.Writer.WriteAsync<Entry>(entries).ConfigureAwait(false);
            await dataset.AppendAsync(draft).ConfigureAwait(false);
        }

        return dataset;
    }
}
