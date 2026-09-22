// What a k-way merge costs as k grows.
//
// The merge picks the input holding the smallest key and then works out how many of its rows may go
// before another input takes over, which is two passes over the open inputs per run emitted. With
// the objects interleaved row by row every run is one row, so the passes are paid as often as they
// can be and the curve against the fan-in is the cost of the choosing and nothing else.
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using BenchmarkDotNet.Attributes;

using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Columns;
using Vorticity.Dataset;
using Vorticity.Types;
using Vorticity.Writing;

namespace Vorticity.Benchmarks;

/// <summary>The merge's cost against the number of objects it holds open.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public class MergeProbes
{
    /// <summary>
    /// Rows the dataset holds, whatever the fan-in.
    /// </summary>
    /// <remarks>
    /// Held constant on purpose: an object count that also moved the row count would give a curve
    /// in which the merge's own growth and the work of reading more rows cannot be told apart.
    /// </remarks>
    private const int Rows = 16_384;

    /// <summary>
    /// How many objects of level zero the merge holds open at once.
    /// </summary>
    /// <remarks>
    /// The design bounds this at the level-zero ceiling plus one cursor per level above it, so
    /// four and sixteen bracket what a dataset reaches and sixty-four is past it -- which is the
    /// point, since a cost that does not show at sixty-four does not show at twelve either.
    /// </remarks>
    public static IEnumerable<int> FanIn => [4, 16, 64];

    private readonly Dictionary<int, VortexDataset> _datasets = [];
    private readonly List<MemoryObjectStore> _stores = [];

    /// <summary>Builds one dataset per fan-in, each with its objects interleaved row by row.</summary>
    [GlobalSetup]
    public void Setup()
    {
        foreach (int objects in FanIn)
        {
            MemoryObjectStore store = new MemoryObjectStore();
            _stores.Add(store);
            _datasets[objects] = BuildAsync(store, objects).GetAwaiter().GetResult();
        }
    }

    /// <summary>Disposes the datasets and their stores.</summary>
    [GlobalCleanup]
    public void Cleanup()
    {
        foreach (VortexDataset dataset in _datasets.Values)
        {
            dataset.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        foreach (MemoryObjectStore store in _stores)
        {
            store.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        _datasets.Clear();
        _stores.Clear();
    }

    /// <summary>Walks every row of the dataset in key order.</summary>
    [Benchmark]
    [ArgumentsSource(nameof(FanIn))]
    public long KeyOrderedMerge(int objects)
    {
        return WalkAsync(_datasets[objects]).GetAwaiter().GetResult();
    }

    private static async Task<long> WalkAsync(VortexDataset dataset)
    {
        long rows = 0;
        await foreach (RecordBatch batch in dataset.ScanBuilder().InKeyOrder("key").ExecuteAsync()
            .WithCancellation(CancellationToken.None).ConfigureAwait(false))
        {
            rows += batch.RowCount;
            batch.Dispose();
        }

        return rows;
    }

    private static async Task<VortexDataset> BuildAsync(MemoryObjectStore store, int objects)
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

        VortexDataset dataset = await VortexDataset.CreateAsync(store, schema, options).ConfigureAwait(false);
        for (int residue = 0; residue < objects; residue++)
        {
            await dataset.AppendAsync(Batches(types, schema, residue, objects)).ConfigureAwait(false);
        }

        return dataset;
    }

    /// <summary>One object's rows: the keys congruent to <paramref name="residue"/>.</summary>
    private static async IAsyncEnumerable<RecordBatch> Batches(
        DTypeArena types, DType schema, int residue, int objects)
    {
        DType i64 = types.Primitive(PType.I64, Nullability.NonNullable);
        DType f64 = types.Primitive(PType.F64, Nullability.NonNullable);

        int perObject = Rows / objects;
        CanonicalArena arena = new CanonicalArena();
        VortexBuffer keys = arena.Allocate(perObject * sizeof(long), sizeof(long), out Span<byte> keyBytes);
        VortexBuffer measures = arena.Allocate(perObject * sizeof(double), sizeof(double), out Span<byte> measureBytes);
        Span<long> keyValues = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, long>(keyBytes);
        Span<double> measureValues = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, double>(measureBytes);
        for (int i = 0; i < perObject; i++)
        {
            keyValues[i] = ((long)i * objects) + residue;
            measureValues[i] = keyValues[i] * 0.5;
        }

        int key = arena.AddPrimitive(i64, perObject, Validity.NonNullable, PType.I64, keys);
        int measure = arena.AddPrimitive(f64, perObject, Validity.NonNullable, PType.F64, measures);
        int root = arena.AddStruct(schema, perObject, Validity.NonNullable, [key, measure]);
        yield return new RecordBatch(arena, root, 0);
        await Task.CompletedTask.ConfigureAwait(false);
    }
}
