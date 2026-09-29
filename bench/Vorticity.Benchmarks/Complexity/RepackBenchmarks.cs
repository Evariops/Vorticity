// What a repack costs, against the objects a dataset holds.
//
// A dataset of `Objects` entries, added in one commit, is repacked for a version no fragment
// names: every leaf is walked, and none moves. Run in a checkout of the original and in the tree.
using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using BenchmarkDotNet.Attributes;

using Vorticity.Dataset;
using Vorticity.Expressions;
using Vorticity.Scanning;
using Vorticity.Types;

namespace Vorticity.Benchmarks.Complexity;

/// <summary>A repack that moves nothing, against the dataset's entries.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public class RepackBenchmarks
{
    /// <summary>Entries of the dataset.</summary>
    [Params(1_000, 100_000)]
    public int Objects { get; set; }

    private MemoryObjectStore _store = null!;
    private VortexDataset _dataset = null!;

    /// <summary>Creates the dataset and its entries, and checks a repack moves nothing.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _store = new MemoryObjectStore();
        _dataset = BuildAsync().GetAwaiter().GetResult();
        if (Repack() != OperationOutcome.AlreadyThere)
        {
            throw new InvalidOperationException("The repack moved something.");
        }
    }

    /// <summary>Disposes the dataset and its store.</summary>
    [GlobalCleanup]
    public void Cleanup()
    {
        _dataset.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _store.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    /// <summary>A repack of a version no fragment names.</summary>
    [Benchmark]
    public OperationOutcome Repack() =>
        _dataset.RepackAsync([ulong.MaxValue - 1], CancellationToken.None).AsTask().GetAwaiter().GetResult().Outcome;

    private async Task<VortexDataset> BuildAsync()
    {
        DTypeArena types = new DTypeArena();
        DType schema = types.Struct(["k"], [types.Primitive(PType.I64, Nullability.NonNullable)], Nullability.NonNullable);
        VortexDataset dataset = await VortexDataset.CreateAsync(_store, schema, new DatasetOptions()).ConfigureAwait(false);
        DatasetOperation[] adds = new DatasetOperation[Objects];
        for (int i = 0; i < Objects; i++)
        {
            ObjectSummaries summaries = ObjectSummaries.From(
            [
                new ColumnSummary("k", FilterLiteral.From((long)i), true, FilterLiteral.From(i + 999L), true, true, 0, true),
            ]);
            adds[i] = new DatasetOperation.AddObject(
                Encoding.UTF8.GetBytes($"k{i:D9}"),
                new ObjectEntry(CommitKey.ForData($"{i:x8}"), (UInt128)(uint)i + 1, 1_000, 1 << 20, (UInt128)(uint)i, summaries));
        }

        await dataset.CommitAsync(adds, CancellationToken.None).ConfigureAwait(false);
        return dataset;
    }
}
