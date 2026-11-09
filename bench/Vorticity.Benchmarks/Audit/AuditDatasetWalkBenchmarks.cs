// Audit DS-02, DS-03: what a walk of a dataset's object tree costs, per object, in time and bytes.
//
// A dataset of `Objects` synthetic object entries (four summarised columns, about 200 bytes an
// entry, the size the tree is sized for) is committed into an in-memory store and opened. A walk
// enumerates every positioned object of the version, with no pruner (`All`) or with a pruner that
// keeps one region in 97 (`Pruned`, which reads each entry's summaries). The objects are never
// opened: the walk is the planning floor every dataset question pays before any data is read.
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using BenchmarkDotNet.Attributes;

using Vorticity.Dataset;
using Vorticity.Expressions;
using Vorticity.Scanning;

namespace Vorticity.Benchmarks.Audit;

/// <summary>A full walk of a dataset version's objects, with and without a summary pruner.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public class AuditDatasetWalkBenchmarks
{
    /// <summary>Object entries in the dataset's tree.</summary>
    [Params(10_000, 100_000)]
    public int Objects { get; set; }

    private MemoryObjectStore _store = null!;
    private VortexDataset _dataset = null!;
    private SummaryPruner _pruner = null!;

    /// <summary>Commits the objects and opens the dataset.</summary>
    [GlobalSetup]
    public void Setup() => SetupAsync().GetAwaiter().GetResult();

    private async Task SetupAsync()
    {
        _store = new MemoryObjectStore();
        List<DatasetOperation> operations = new List<DatasetOperation>(Objects);
        for (int i = 0; i < Objects; i++)
        {
            operations.Add(new DatasetOperation.AddObject(
                Encoding.UTF8.GetBytes($"k{i:D9}"),
                new ObjectEntry(CommitKey.ForData($"{i:x8}"), (UInt128)(uint)i + 1, 1_000, 1 << 20, (UInt128)(uint)i, Summaries(i))));
        }

        CommitOptions options = new CommitOptions
        {
            Seed = 0xA0D1_7000_5EED,
            Template = new CommitHeader
            {
                Version = 1,
                Schema = Types.Serialization.DTypeProtobuf.Serialize(VortexTypes.ToDType(Schema(), new Types.DTypeArena())),
                Chunker = new ChunkerSettings(
                    ProllyBoundaryRule.DefaultMinBytes, ProllyBoundaryRule.DefaultTargetBytes, ProllyBoundaryRule.DefaultMaxBytes),
            },
        };

        await DatasetCommitter.CommitAsync(_store, operations, options, CancellationToken.None).ConfigureAwait(false);
        _dataset = await VortexDataset.OpenAsync(_store).ConfigureAwait(false);
        _pruner = new SummaryPruner(Expr.Eq(Expr.Field("region"), Expr.Literal(FilterLiteral.From("r042"))));
        _keys = new byte[Objects][];
        for (int i = 0; i < Objects; i++)
        {
            _keys[i] = Encoding.UTF8.GetBytes($"k{i:D9}");
        }

        Console.WriteLine($"// {Objects} objects, depth {_dataset.Depth}, kept by the pruner: {Pruned()}, found: {Find()}");
    }

    /// <summary>Closes the dataset.</summary>
    [GlobalCleanup]
    public void Cleanup()
    {
        _dataset.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _store.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    /// <summary>Every object of the version, no pruner.</summary>
    [Benchmark]
    public long All() => WalkAsync(null).GetAwaiter().GetResult();

    /// <summary>Every object a summary pruner on one region keeps.</summary>
    [Benchmark]
    public long Pruned() => WalkAsync(_pruner).GetAwaiter().GetResult();

    /// <summary>A thousand keys spread over the tree, each found from the root down (DS-02's FindAsync).</summary>
    [Benchmark(OperationsPerInvoke = Lookups)]
    public long Find() => FindAsync().GetAwaiter().GetResult();

    private const int Lookups = 1_000;

    private async Task<long> FindAsync()
    {
        DatasetSnapshot snapshot = _dataset.Snapshot;
        DatasetTree tree = null!;
        foreach ((int _, DatasetTree occupied) in snapshot.Levels.Occupied())
        {
            tree = occupied;
        }

        long rows = 0;
        int step = Objects / Lookups;
        for (int i = 0; i < Lookups; i++)
        {
            if (await tree.FindAsync(_keys[i * step], snapshot.Pages, CancellationToken.None).ConfigureAwait(false) is { } found)
            {
                rows += found.Rows;
            }
        }

        return rows;
    }

    private byte[][] _keys = null!;

    private async Task<long> WalkAsync(SummaryPruner? pruner)
    {
        long rows = 0;
        await foreach (PositionedObject held in _dataset.Snapshot.WalkAsync(pruner, 0, long.MaxValue, null, CancellationToken.None)
            .ConfigureAwait(false))
        {
            rows += held.Entry.Rows;
        }

        return rows;
    }

    private static VortexSchema Schema() => VortexSchema.Create(
    [
        new VortexField("k", VortexType.Int64),
        new VortexField("amount", VortexType.Float64),
        new VortexField("tenant", VortexType.Utf8),
        new VortexField("region", VortexType.Utf8),
    ]);

    private static ObjectSummaries Summaries(int i) => ObjectSummaries.From(
    [
        new ColumnSummary("k", FilterLiteral.From((long)i), true, FilterLiteral.From(i + 999L), true, true, 0, true),
        new ColumnSummary("amount", FilterLiteral.From(i * 1.5), true, FilterLiteral.From(i * 2.5), true, true, 3, true),
        new ColumnSummary("tenant", FilterLiteral.From($"t{i:D6}"), true, FilterLiteral.From($"t{i + 40:D6}"), true, true, 0, true),
        new ColumnSummary("region", FilterLiteral.From($"r{i % 97:D3}"), true, FilterLiteral.From($"r{i % 97:D3}"), true, true, 0, true),
    ]);
}
