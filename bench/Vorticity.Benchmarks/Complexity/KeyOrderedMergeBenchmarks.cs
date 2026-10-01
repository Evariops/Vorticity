// What the merge of a dataset's objects into key order pays per run, as the objects it holds open grow.
//
// The merge emits one run at a time: the rows of one input that go before any other's. `Original`
// (KeyOrderedMergeBefore.cs) looks over every open input to choose the run and again to know where
// it ends, and over them once more to know whether the next object must open first; when the
// objects' keys interleave, every run is a row and the passes are paid for every row. `Library` is
// the merge itself. Both are driven the same way, over the objects of one version in the order of
// their bounds, each read in key order, as a key-ordered read of the dataset drives it.
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

using BenchmarkDotNet.Attributes;

using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Columns;
using Vorticity.Dataset;
using Vorticity.File;
using Vorticity.Scanning;
using Vorticity.Types;
using Vorticity.Writing;

namespace Vorticity.Benchmarks.Complexity;

/// <summary>A key-ordered merge of every row of a dataset, against the objects it merges.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public class KeyOrderedMergeBenchmarks
{
    /// <summary>Rows of the dataset, whatever the objects.</summary>
    private const int Rows = 16_384;

    private static readonly string[] Paths = ["key"];

    /// <summary>Objects the dataset's rows are spread over, all open at once when they interleave.</summary>
    [Params(4, 16, 64, 256)]
    public int Objects { get; set; }

    /// <summary>How the objects' keys meet.</summary>
    [Params(Overlap.Interleaved, Overlap.Disjoint)]
    public Overlap Layout { get; set; }

    /// <summary>How each object's keys sit against the others'.</summary>
    public enum Overlap
    {
        /// <summary>Object r holds the keys congruent to r: every run is a row.</summary>
        Interleaved,

        /// <summary>Object r holds the r-th run of keys: every run is an object's batch.</summary>
        Disjoint,
    }

    private MemoryObjectStore _store = null!;
    private VortexDataset _dataset = null!;

    /// <summary>Builds the dataset, one append per object.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _store = new MemoryObjectStore();
        _dataset = BuildAsync().GetAwaiter().GetResult();
    }

    /// <summary>Disposes the dataset and its store.</summary>
    [GlobalCleanup]
    public void Cleanup()
    {
        _dataset.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _store.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    /// <summary>The merge that looks over every open input for each run.</summary>
    [Benchmark(Baseline = true)]
    public long Original() => OriginalAsync().GetAwaiter().GetResult();

    /// <summary>The merge of the library.</summary>
    [Benchmark]
    public long Library() => LibraryAsync().GetAwaiter().GetResult();

    private async Task<long> OriginalAsync()
    {
        long rows = 0;
        KeyOrderedMergeBefore merge = new KeyOrderedMergeBefore(
            _dataset.Snapshot, ObjectsAsync(), Paths, descending: false, rankedTies: true,
            file => file.ScanBuilder().InKeyOrder(Paths, false), null, CancellationToken.None);
        await using (merge.ConfigureAwait(false))
        {
            while (await merge.MoveNextAsync().ConfigureAwait(false))
            {
                rows += merge.Current.RowCount;
            }
        }

        return rows;
    }

    private async Task<long> LibraryAsync()
    {
        long rows = 0;
        KeyOrderedMerge merge = new KeyOrderedMerge(
            _dataset.Snapshot, ObjectsAsync(), Paths, descending: false, rankedTies: true,
            (lease, _) => lease.File.ScanBuilder().InKeyOrder(Paths, false).ExecuteAsync(), null, CancellationToken.None);
        await using (merge.ConfigureAwait(false))
        {
            while (await merge.MoveNextAsync().ConfigureAwait(false))
            {
                rows += merge.Current.RowCount;
            }
        }

        return rows;
    }

    /// <summary>The version's objects in tree order, which on the clustering key is the order of their bounds.</summary>
    private async IAsyncEnumerable<MergeObject> ObjectsAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        long ordinal = 0;
        await foreach (PositionedObject held in _dataset.Snapshot
            .WalkAsync(null, 0, long.MaxValue, null, cancellationToken).ConfigureAwait(false))
        {
            yield return new MergeObject(held.Entry, VortexDataset.OrderOf(held.TreeKey), ordinal++, held.FirstRow);
        }
    }

    private async Task<VortexDataset> BuildAsync()
    {
        DTypeArena types = new DTypeArena();
        DType schema = types.Struct(
            ["key", "measure"],
            [types.Primitive(PType.I64, Nullability.NonNullable), types.Primitive(PType.F64, Nullability.NonNullable)],
            Nullability.NonNullable);

        DatasetOptions options = new DatasetOptions
        {
            Seed = 0xC1057E_5EED,
            Write = new VortexWriteOptions { RowBlockSize = 64, DataBlockTargetBytes = 8 << 10 },
            ClusteringKey = ["key"],
        };

        VortexDataset dataset = await VortexDataset.CreateAsync(_store, schema, options).ConfigureAwait(false);
        for (int part = 0; part < Objects; part++)
        {
            await dataset.AppendAsync(Batches(types, schema, part)).ConfigureAwait(false);
        }

        return dataset;
    }

    /// <summary>One object's rows.</summary>
    private async IAsyncEnumerable<RecordBatch> Batches(DTypeArena types, DType schema, int part)
    {
        DType i64 = types.Primitive(PType.I64, Nullability.NonNullable);
        DType f64 = types.Primitive(PType.F64, Nullability.NonNullable);

        int perObject = Rows / Objects;
        CanonicalArena arena = new CanonicalArena();
        VortexBuffer keys = arena.Allocate(perObject * sizeof(long), sizeof(long), out Span<byte> keyBytes);
        VortexBuffer measures = arena.Allocate(perObject * sizeof(double), sizeof(double), out Span<byte> measureBytes);
        Span<long> keyValues = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, long>(keyBytes);
        Span<double> measureValues = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, double>(measureBytes);
        for (int i = 0; i < perObject; i++)
        {
            keyValues[i] = Layout == Overlap.Interleaved ? ((long)i * Objects) + part : ((long)part * perObject) + i;
            measureValues[i] = keyValues[i] * 0.5;
        }

        int key = arena.AddPrimitive(i64, perObject, Validity.NonNullable, PType.I64, keys);
        int measure = arena.AddPrimitive(f64, perObject, Validity.NonNullable, PType.F64, measures);
        int root = arena.AddStruct(schema, perObject, Validity.NonNullable, [key, measure]);
        yield return new RecordBatch(arena, root, 0);
        await Task.CompletedTask.ConfigureAwait(false);
    }
}
