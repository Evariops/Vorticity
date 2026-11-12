// What a question on a key of a variant column pays for each object of level 0 it reads from the node's memo
// of shredded copies, once they are made.
//
// 64 appends of 4,096 documents (a rising id, attributes of a route and a status), left in level 0. The question counts the
// documents of a status of 500 or more; the first questions make each object's copy, the ones measured read the copies.
using System;
using System.Linq;
using System.Threading.Tasks;

using BenchmarkDotNet.Attributes;

using Vorticity.Dataset;
using Vorticity.Types.Variants;

namespace Vorticity.Benchmarks.Audit;

/// <summary>A count over a key of the attributes of 64 objects of level 0, served from their copies.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public partial class AuditLevelZeroKeysBenchmarks
{
    private const int Appends = 64;
    private const int RowsPerAppend = 4_096;

    private MemoryObjectStore _store = null!;
    private VortexDataset _dataset = null!;

    /// <summary>A document: its id and attributes.</summary>
    [VortexRecord]
    public partial record struct Doc(long Id, Variant Attributes);

    /// <summary>Writes the dataset and makes the copies.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _store = new MemoryObjectStore();
        _dataset = BuildAsync().GetAwaiter().GetResult();
        Errors();
        _dataset.LevelZero.WhenMadeAsync().GetAwaiter().GetResult();
        Console.WriteLine($"// {_dataset.ObjectCount} objects, {_dataset.LevelZero.Held} copies; {Errors()} errors");
    }

    /// <summary>Disposes the dataset and its store.</summary>
    [GlobalCleanup]
    public void Cleanup()
    {
        _dataset.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _store.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    /// <summary>The documents of a status of 500 or more, counted.</summary>
    [Benchmark]
    public long Errors() => _dataset.Scan<Doc>().Where(d => d.Attributes["status"].AsInt64() >= 500).CountAsync().AsTask().GetAwaiter().GetResult();

    private async Task<VortexDataset> BuildAsync()
    {
        VortexDataset dataset = await VortexDataset.CreateAsync(_store, Doc.Schema, new DatasetOptions
        {
            Layout = new DatasetLayout { SortKey = [LayoutValue.Column(nameof(Doc.Id))], CalmAfter = null },
        }).ConfigureAwait(false);
        Random random = new Random(43);
        long id = 0;
        for (int append = 0; append < Appends; append++)
        {
            Doc[] docs = [.. Enumerable.Range(0, RowsPerAppend).Select(_ => new Doc(
                id++, Variant.Object([("route", Variant.From($"/api/v1/r{random.Next(40)}")), ("status", Variant.From(random.Next(20) == 0 ? 503 : 200))])))];
            await using ObjectDraft draft = dataset.StartObject();
            await draft.Writer.WriteAsync<Doc>(docs).ConfigureAwait(false);
            await dataset.AppendAsync(draft).ConfigureAwait(false);
        }

        return dataset;
    }
}
