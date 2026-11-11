// What a filtered Count, Any and Max over many objects cost, at one lane and at eight.
//
// A dataset of 32 objects of 65,536 rows each (sorted `key`, a pseudo-random `measure` in [0, 1)) is
// written into an in-memory store. Every question carries a filter on `measure` that no object's summary
// settles, so each object is opened and its rows judged: the count, the existence of a value absent from
// every object, and the largest `key` among the rows kept, which the walk order (keys rising) finds in
// the last object.
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using BenchmarkDotNet.Attributes;

using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Dataset;
using Vorticity.Expressions;
using Vorticity.Scanning;
using Vorticity.Types;

namespace Vorticity.Benchmarks.Audit;

/// <summary>Filtered Count, Any and Max over the objects of a dataset, against the scan's degree of parallelism.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public class AuditDatasetAnswersBenchmarks
{
    private const int Objects = 32;
    private const int RowsPerObject = 65_536;

    /// <summary>The scan's degree of parallelism.</summary>
    [Params(1, 2, 4, 8, 16)]
    public int Degree { get; set; }

    private MemoryObjectStore _store = null!;
    private VortexDataset _dataset = null!;
    private ScanOptions _options = null!;

    /// <summary>Writes the dataset, one append per object.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _store = new MemoryObjectStore();
        _dataset = BuildAsync().GetAwaiter().GetResult();
        _options = new ScanOptions { DegreeOfParallelism = Degree };
        Console.WriteLine($"// count {Count()}, any {Any()}, max {Max()}");
    }

    /// <summary>Disposes the dataset and its store.</summary>
    [GlobalCleanup]
    public void Cleanup()
    {
        _dataset.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _store.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    /// <summary>The rows whose measure is below one half.</summary>
    [Benchmark]
    public long Count() => Scan(Expr.Lt(Expr.Field("measure"), Expr.Literal(FilterLiteral.From(0.5)))).CountAsync().AsTask().GetAwaiter().GetResult();

    /// <summary>Whether a row's measure lies in a band no row falls in, which every object's bounds allow.</summary>
    [Benchmark]
    public bool Any() => Scan(Expr.And(
            Expr.Gt(Expr.Field("measure"), Expr.Literal(FilterLiteral.From(0.25))),
            Expr.Lt(Expr.Field("measure"), Expr.Literal(FilterLiteral.From(0.25 + 1e-12)))))
        .AnyAsync().AsTask().GetAwaiter().GetResult();

    /// <summary>The largest key among the rows whose measure is below one half.</summary>
    [Benchmark]
    public long Max() => Scan(Expr.Lt(Expr.Field("measure"), Expr.Literal(FilterLiteral.From(0.5))))
        .MaxAsync("key").AsTask().GetAwaiter().GetResult().SignedValue;

    private DatasetScanBuilder Scan(VortexExpr filter) => _dataset.ScanBuilder().WithOptions(_options, keepEncodings: false).Where(filter);

    private async Task<VortexDataset> BuildAsync()
    {
        DTypeArena types = new DTypeArena();
        DType schema = types.Struct(
            ["key", "measure"],
            [types.Primitive(PType.I64, Nullability.NonNullable), types.Primitive(PType.F64, Nullability.NonNullable)],
            Nullability.NonNullable);

        DatasetOptions options = new DatasetOptions
        {
            Seed = 0xA0D1_7001_5EED,
            Layout = new DatasetLayout { SortKey = [LayoutValue.Column("key")] },
        };

        VortexDataset dataset = await VortexDataset.CreateAsync(_store, schema, options).ConfigureAwait(false);
        for (int part = 0; part < Objects; part++)
        {
            await dataset.AppendAsync(Batches(types, schema, part)).ConfigureAwait(false);
        }

        return dataset;
    }

    /// <summary>One object's rows: a run of keys, and a measure drawn from a generator seeded by the object.</summary>
    private static async IAsyncEnumerable<RecordBatch> Batches(DTypeArena types, DType schema, int part)
    {
        DType i64 = types.Primitive(PType.I64, Nullability.NonNullable);
        DType f64 = types.Primitive(PType.F64, Nullability.NonNullable);
        Random random = new Random(part);
        CanonicalArena arena = new CanonicalArena();
        VortexBuffer keys = arena.Allocate(RowsPerObject * sizeof(long), sizeof(long), out Span<byte> keyBytes);
        VortexBuffer measures = arena.Allocate(RowsPerObject * sizeof(double), sizeof(double), out Span<byte> measureBytes);
        Span<long> keyValues = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, long>(keyBytes);
        Span<double> measureValues = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, double>(measureBytes);
        for (int i = 0; i < RowsPerObject; i++)
        {
            keyValues[i] = ((long)part * RowsPerObject) + i;
            measureValues[i] = random.NextDouble();
        }

        int key = arena.AddPrimitive(i64, RowsPerObject, Validity.NonNullable, PType.I64, keys);
        int measure = arena.AddPrimitive(f64, RowsPerObject, Validity.NonNullable, PType.F64, measures);
        int root = arena.AddStruct(schema, RowsPerObject, Validity.NonNullable, [key, measure]);
        yield return new RecordBatch(arena, root, 0);
        await Task.CompletedTask.ConfigureAwait(false);
    }
}
