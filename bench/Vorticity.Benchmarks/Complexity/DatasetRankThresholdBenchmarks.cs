// Where a rank seek on a dataset's key cursor should stop walking and select.
//
// The dataset of DatasetRankSeekBenchmarks: 1,048,576 rows appended as M objects, interleaved or
// disjoint. A fresh cursor seeks one rank, of PerObject entries per object, either walking to it
// from the first or selecting it; the seek walks below one entry per object and selects from there.
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using BenchmarkDotNet.Attributes;

using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Columns;
using Vorticity.Dataset;
using Vorticity.Types;
using Vorticity.Writing;

namespace Vorticity.Benchmarks.Complexity;

/// <summary>One rank seek on a fresh cursor, walked or selected, against the objects and the rank.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public class DatasetRankThresholdBenchmarks
{
    /// <summary>Rows of the dataset, whatever the objects: the cursor's entries.</summary>
    private const int Rows = 1 << 20;

    /// <summary>Objects the dataset's rows are spread over.</summary>
    [Params(4, 16, 64)]
    public int Objects { get; set; }

    /// <summary>How the objects' keys meet.</summary>
    [Params(DatasetRankSeekBenchmarks.Overlap.Interleaved, DatasetRankSeekBenchmarks.Overlap.Disjoint)]
    public DatasetRankSeekBenchmarks.Overlap Layout { get; set; }

    /// <summary>The rank sought, in entries per object.</summary>
    [Params(1, 2, 4, 32)]
    public int PerObject { get; set; }

    private MemoryObjectStore _store = null!;
    private VortexDataset _dataset = null!;

    /// <summary>Builds the dataset, one append per object, and checks both ways land on one key.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _store = new MemoryObjectStore();
        _dataset = BuildAsync().GetAwaiter().GetResult();
        if (Walk() != Select() || Walk() < 0)
        {
            throw new InvalidOperationException("The walk and the selection disagree.");
        }
    }

    /// <summary>Disposes the dataset and its store.</summary>
    [GlobalCleanup]
    public void Cleanup()
    {
        _dataset.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _store.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    /// <summary>A cursor opened, and walked from its first entry to the rank.</summary>
    [Benchmark]
    public long Walk() => WalkAsync().GetAwaiter().GetResult();

    /// <summary>A cursor opened, and the rank selected.</summary>
    [Benchmark]
    public long Select() => SelectAsync().GetAwaiter().GetResult();

    private async Task<long> WalkAsync()
    {
        await using DatasetKeyCursor cursor = await DatasetKeyCursor.OpenAsync(_dataset).ConfigureAwait(false);
        long rank = (long)PerObject * Objects;
        bool found = await cursor.SeekFirstAsync().ConfigureAwait(false);
        for (long step = 0; found && step < rank; step++)
        {
            found = await cursor.NextAsync().ConfigureAwait(false);
        }

        return found ? cursor.Key.SignedValue : -1;
    }

    private async Task<long> SelectAsync()
    {
        await using DatasetKeyCursor cursor = await DatasetKeyCursor.OpenAsync(_dataset).ConfigureAwait(false);
        return await cursor.SelectAsync((long)PerObject * Objects).ConfigureAwait(false) ? cursor.Key.SignedValue : -1;
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
            keyValues[i] = Layout == DatasetRankSeekBenchmarks.Overlap.Interleaved
                ? ((long)i * Objects) + part
                : ((long)part * perObject) + i;
            measureValues[i] = keyValues[i] * 0.5;
        }

        int key = arena.AddPrimitive(i64, perObject, Validity.NonNullable, PType.I64, keys);
        int measure = arena.AddPrimitive(f64, perObject, Validity.NonNullable, PType.F64, measures);
        int root = arena.AddStruct(schema, perObject, Validity.NonNullable, [key, measure]);
        yield return new RecordBatch(arena, root, 0);
        await Task.CompletedTask.ConfigureAwait(false);
    }
}
