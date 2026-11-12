// What a cold walk of a dataset's objects pays the versions whose pages its tree holds, the start of each
// version's pages read before its first page.
//
// 1,024 appends of 256 rows to a dataset sorted by its key, each its own version, in an in-memory store that answers each
// request in 5 ms. A handle opened afresh walks every object: the tree's leaves lie in the commit objects of the versions
// that wrote them.
using System;
using System.Threading.Tasks;

using BenchmarkDotNet.Attributes;

using Vorticity.Dataset;

namespace Vorticity.Benchmarks.Audit;

/// <summary>A cold walk of a dataset of 1,024 versions.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public partial class AuditColdWalkBenchmarks
{
    private const int Appends = 1_024;
    private const int RowsPerAppend = 256;

    private MemoryObjectStore _store = null!;

    /// <summary>A row: its key and a value.</summary>
    [VortexRecord]
    public partial record struct Keyed(long Key, double Value);

    /// <summary>Writes the dataset, then slows its store.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _store = new MemoryObjectStore();
        BuildAsync().GetAwaiter().GetResult();
        _store.Latency = TimeSpan.FromMilliseconds(5);
        Console.WriteLine($"// {Walk()} objects walked");
    }

    /// <summary>Disposes the store.</summary>
    [GlobalCleanup]
    public void Cleanup() => _store.DisposeAsync().AsTask().GetAwaiter().GetResult();

    /// <summary>A handle opened afresh, every object walked.</summary>
    [Benchmark]
    public int Walk() => WalkAsync().GetAwaiter().GetResult();

    private async Task<int> WalkAsync()
    {
        await using VortexDataset dataset = await VortexDataset.OpenAsync(_store).ConfigureAwait(false);
        int objects = 0;
        await foreach (PositionedObject held in dataset.ScanBuilder().ObjectsAsync().ConfigureAwait(false))
        {
            objects += held.Entry.Rows > 0 ? 1 : 0;
        }

        return objects;
    }

    private async Task BuildAsync()
    {
        await using VortexDataset dataset = await VortexDataset.CreateAsync(_store, Keyed.Schema, new DatasetOptions
        {
            Layout = new DatasetLayout { SortKey = [LayoutValue.Column(nameof(Keyed.Key))], CalmAfter = null },
        }).ConfigureAwait(false);
        Keyed[] rows = new Keyed[RowsPerAppend];
        Random random = new Random(3);
        for (int append = 0; append < Appends; append++)
        {
            for (int i = 0; i < rows.Length; i++)
            {
                rows[i] = new Keyed(random.NextInt64(), random.NextDouble());
            }

            await using ObjectDraft draft = dataset.StartObject();
            await draft.Writer.WriteAsync<Keyed>(rows).ConfigureAwait(false);
            await dataset.AppendAsync(draft).ConfigureAwait(false);
        }
    }
}
