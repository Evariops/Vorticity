// What a lookup of an identifier costs, the identifier's column projected alone.
//
// A dataset of 32 objects of 65,536 rows each (sorted `key`, a unique `id` scattered over the whole range in every
// object, so that no object's bounds rule a value out) is written into an in-memory store, `id` declared its
// identifier, every object with a table of contents (as tests set it) so that the objects the walk keeps are looked up
// by their index rather than opened. A question asks the rows of one id, projected to that column; the rows found are
// read (Consume.Batch). The setup prints the objects the walk keeps and the rows delivered.
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using BenchmarkDotNet.Attributes;

using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Dataset;
using Vorticity.Expressions;
using Vorticity.Layouts;
using Vorticity.Scanning;
using Vorticity.Types;

namespace Vorticity.Benchmarks.Audit;

/// <summary>A projected lookup of one identifier over the objects of a dataset.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public class AuditDatasetLookupBenchmarks
{
    private const int Objects = 32;
    private const int RowsPerObject = 65_536;

    private MemoryObjectStore _store = null!;
    private VortexDataset _dataset = null!;
    private VortexExpr _filter = null!;

    /// <summary>Writes the dataset, one append per object.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _store = new MemoryObjectStore();
        _dataset = BuildAsync().GetAwaiter().GetResult();
        _filter = Expr.Eq(Expr.Field("id"), Expr.Literal(FilterLiteral.From(Id(17, 4_321))));
        Console.WriteLine($"// objects kept {KeptAsync().GetAwaiter().GetResult()}, rows delivered {Lookup()}");
    }

    /// <summary>Disposes the dataset and its store.</summary>
    [GlobalCleanup]
    public void Cleanup()
    {
        _dataset.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _store.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    /// <summary>The rows of one identifier, its column alone, read.</summary>
    [Benchmark]
    public long Lookup() => SumAsync().GetAwaiter().GetResult();

    private async Task<long> SumAsync()
    {
        long sum = 0;
        await foreach (RecordBatch batch in _dataset.ScanBuilder().Project(FieldMask.Single(1)).Where(_filter).ExecuteAsync().ConfigureAwait(false))
        {
            using (batch)
            {
                sum += Vorticity.Bench.Scenarios.Consume.Batch(batch);
            }
        }

        return sum;
    }

    private async Task<int> KeptAsync()
    {
        int kept = 0;
        await foreach (PositionedObject _ in _dataset.ScanBuilder().Project(FieldMask.Single(1)).Where(_filter).ObjectsAsync().ConfigureAwait(false))
        {
            kept++;
        }

        return kept;
    }

    /// <summary>The id of row <paramref name="row"/> of object <paramref name="part"/>: unique, scattered over the range.</summary>
    private static long Id(int part, int row) => ((((long)part * RowsPerObject) + row) * 2_654_435_761L) & 0xFFFF_FFFFL;

    private async Task<VortexDataset> BuildAsync()
    {
        DTypeArena types = new DTypeArena();
        DType schema = types.Struct(
            ["key", "id"],
            [types.Primitive(PType.I64, Nullability.NonNullable), types.Primitive(PType.I64, Nullability.NonNullable)],
            Nullability.NonNullable);

        DatasetOptions options = new DatasetOptions
        {
            Seed = 0xA0D1_7011_5EED,
            Layout = new DatasetLayout { SortKey = [LayoutValue.Column("key")] },
            Identifiers = ["id"],
            ContentsObjectBytes = 1,
        };

        VortexDataset dataset = await VortexDataset.CreateAsync(_store, schema, options).ConfigureAwait(false);
        for (int part = 0; part < Objects; part++)
        {
            await dataset.AppendAsync(Batches(types, schema, part)).ConfigureAwait(false);
        }

        return dataset;
    }

    /// <summary>One object's rows: a run of keys, and their ids.</summary>
    private static async IAsyncEnumerable<RecordBatch> Batches(DTypeArena types, DType schema, int part)
    {
        DType i64 = types.Primitive(PType.I64, Nullability.NonNullable);
        CanonicalArena arena = new CanonicalArena();
        VortexBuffer keys = arena.Allocate(RowsPerObject * sizeof(long), sizeof(long), out Span<byte> keyBytes);
        VortexBuffer ids = arena.Allocate(RowsPerObject * sizeof(long), sizeof(long), out Span<byte> idBytes);
        Span<long> keyValues = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, long>(keyBytes);
        Span<long> idValues = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, long>(idBytes);
        for (int i = 0; i < RowsPerObject; i++)
        {
            keyValues[i] = ((long)part * RowsPerObject) + i;
            idValues[i] = Id(part, i);
        }

        int key = arena.AddPrimitive(i64, RowsPerObject, Validity.NonNullable, PType.I64, keys);
        int id = arena.AddPrimitive(i64, RowsPerObject, Validity.NonNullable, PType.I64, ids);
        int root = arena.AddStruct(schema, RowsPerObject, Validity.NonNullable, [key, id]);
        yield return new RecordBatch(arena, root, 0);
        await Task.CompletedTask.ConfigureAwait(false);
    }
}
