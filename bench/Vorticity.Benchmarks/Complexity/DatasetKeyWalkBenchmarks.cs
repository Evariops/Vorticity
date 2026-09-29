// What a walk of a dataset's clustering key pays per entry, as the objects it merges grow.
//
// A dataset's key cursor merges the key cursors of its objects. `Original` (DatasetKeyCursorBefore.cs)
// finds the next entry by a pass over every object, twice, and while an object waits to be opened
// encodes the smallest key into a new array to compare it with that object's lower bound: every
// entry costs a pass over the M objects and an allocation. `Library` is the cursor itself.
//
// Two layouts, both of objects appended one by one: interleaved, each object holding every M-th
// key, so that every object is open from the first entries on; and disjoint, each object holding a
// run of keys, so that an object waits for its turn, as the objects of a compacted level do.
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

namespace Vorticity.Benchmarks.Complexity;

/// <summary>A walk of every entry of a dataset's clustering key, against the objects it merges.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public class DatasetKeyWalkBenchmarks
{
    /// <summary>Rows of the dataset, whatever the objects: the walk's entries.</summary>
    private const int Rows = 16_384;

    /// <summary>Objects the dataset's rows are spread over.</summary>
    [Params(4, 16, 64, 256)]
    public int Objects { get; set; }

    /// <summary>How the objects' keys meet.</summary>
    [Params(Overlap.Interleaved, Overlap.Disjoint)]
    public Overlap Layout { get; set; }

    /// <summary>How each object's keys sit against the others'.</summary>
    public enum Overlap
    {
        /// <summary>Object r holds the keys congruent to r.</summary>
        Interleaved,

        /// <summary>Object r holds the r-th run of keys.</summary>
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

    /// <summary>The walk that passes over every object for each entry.</summary>
    [Benchmark(Baseline = true)]
    public long Original() => OriginalAsync().GetAwaiter().GetResult();

    /// <summary>The cursor of the library.</summary>
    [Benchmark]
    public long Library() => LibraryAsync().GetAwaiter().GetResult();

    private async Task<long> OriginalAsync()
    {
        long entries = 0;
        await using DatasetKeyCursorBefore cursor = await DatasetKeyCursorBefore.OpenAsync(_dataset).ConfigureAwait(false);
        if (await cursor.SeekFirstAsync().ConfigureAwait(false))
        {
            do
            {
                entries++;
            }
            while (await cursor.NextAsync().ConfigureAwait(false));
        }

        return entries;
    }

    private async Task<long> LibraryAsync()
    {
        long entries = 0;
        await using DatasetKeyCursor cursor = await DatasetKeyCursor.OpenAsync(_dataset).ConfigureAwait(false);
        if (await cursor.SeekFirstAsync().ConfigureAwait(false))
        {
            do
            {
                entries++;
            }
            while (await cursor.NextAsync().ConfigureAwait(false));
        }

        return entries;
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
