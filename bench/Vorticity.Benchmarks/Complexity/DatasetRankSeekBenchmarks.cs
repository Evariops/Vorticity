// What positioning a dataset's key cursor at a rank costs, against how far the rank lies.
//
// A dataset of 1,048,576 rows appended as M objects, interleaved (object r holds the keys
// congruent to r, every object open from the first entries on) or disjoint (object r holds the
// r-th run of keys, each waiting for its turn). A cursor is opened and sixteen ranks are sought in
// turn, either near the first entry or spread over the whole dataset: the original walks to each
// from the first, one step per entry. Run in a checkout of the original and in the tree.
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

/// <summary>Sixteen rank seeks on a dataset's key cursor, against the objects and how deep the ranks lie.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public class DatasetRankSeekBenchmarks
{
    /// <summary>Rows of the dataset, whatever the objects: the cursor's entries.</summary>
    private const int Rows = 1 << 20;

    /// <summary>Ranks sought per call.</summary>
    private const int Probes = 16;

    /// <summary>Objects the dataset's rows are spread over.</summary>
    [Params(4, 16, 64)]
    public int Objects { get; set; }

    /// <summary>How the objects' keys meet.</summary>
    [Params(Overlap.Interleaved, Overlap.Disjoint)]
    public Overlap Layout { get; set; }

    /// <summary>How far the ranks lie.</summary>
    [Params(Reach.Near, Reach.Far)]
    public Reach Depth { get; set; }

    /// <summary>How each object's keys sit against the others'.</summary>
    public enum Overlap
    {
        /// <summary>Object r holds the keys congruent to r.</summary>
        Interleaved,

        /// <summary>Object r holds the r-th run of keys.</summary>
        Disjoint,
    }

    /// <summary>Where the ranks sought lie.</summary>
    public enum Reach
    {
        /// <summary>Within the first 4,096 entries.</summary>
        Near,

        /// <summary>Spread over every entry.</summary>
        Far,
    }

    private MemoryObjectStore _store = null!;
    private VortexDataset _dataset = null!;

    /// <summary>Builds the dataset, one append per object, and checks a seek lands where a walk does.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _store = new MemoryObjectStore();
        _dataset = BuildAsync().GetAwaiter().GetResult();
        if (Seek() != Probes)
        {
            throw new InvalidOperationException("A rank was not found.");
        }
    }

    /// <summary>Disposes the dataset and its store.</summary>
    [GlobalCleanup]
    public void Cleanup()
    {
        _dataset.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _store.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    /// <summary>A cursor opened, and every probe's rank sought in turn.</summary>
    [Benchmark]
    public long Seek() => SeekAsync().GetAwaiter().GetResult();

    private async Task<long> SeekAsync()
    {
        long found = 0;
        await using DatasetKeyCursor cursor = await DatasetKeyCursor.OpenAsync(_dataset).ConfigureAwait(false);
        long span = Depth == Reach.Near ? 4_096 : Rows;
        for (int probe = 0; probe < Probes; probe++)
        {
            long rank = ((2L * probe) + 1) * span / (2 * Probes);
            if (await cursor.SeekRankAsync(rank).ConfigureAwait(false))
            {
                // The keys are the ranks: every key appears once, from zero.
                found += cursor.Key.SignedValue == rank ? 1 : 0;
            }
        }

        return found;
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
