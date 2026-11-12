// What finding an aggregate's column in a batch by its path costs, as the engine does for each column of
// each batch: a struct of eight fields, the sixth named, and a field two levels down.
using System;

using BenchmarkDotNet.Attributes;

using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Compute;
using Vorticity.Expressions;
using Vorticity.Types;

namespace Vorticity.Benchmarks.Audit;

/// <summary>A column of a batch found by its path.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public class AuditResolveBenchmarks
{
    private const int Rows = 1_024;

    private CanonicalArena _arena = null!;
    private int _root;
    private FieldExpr _flat = null!;
    private FieldExpr _nested = null!;

    /// <summary>Builds the batch.</summary>
    [GlobalSetup]
    public void Setup()
    {
        DTypeArena types = new DTypeArena();
        DType i64 = types.Primitive(PType.I64, Nullability.NonNullable);
        _arena = new CanonicalArena(Rows * 8 * 10 + 4096);
        string[] names = ["alpha", "bravo", "charlie", "delta", "echo", "foxtrot", "golf", "hotel"];
        int[] fields = new int[names.Length];
        DType[] dtypes = new DType[names.Length];
        for (int f = 0; f < names.Length - 1; f++)
        {
            VortexBuffer buffer = _arena.Allocate(Rows * 8, 8, out Span<byte> _);
            fields[f] = _arena.AddPrimitive(i64, Rows, Validity.NonNullable, PType.I64, buffer);
            dtypes[f] = i64;
        }

        VortexBuffer inner = _arena.Allocate(Rows * 8, 8, out Span<byte> _);
        int leaf = _arena.AddPrimitive(i64, Rows, Validity.NonNullable, PType.I64, inner);
        DType innerType = types.Struct(["leaf"], [i64], Nullability.NonNullable);
        fields[^1] = _arena.AddStruct(innerType, Rows, Validity.NonNullable, [leaf]);
        dtypes[^1] = innerType;
        _root = _arena.AddStruct(types.Struct(names, dtypes, Nullability.NonNullable), Rows, Validity.NonNullable, fields);
        _flat = Expr.Field("foxtrot");
        _nested = Expr.Field("hotel.leaf");
    }

    /// <summary>A field of the root.</summary>
    [Benchmark]
    public int Flat() => FilterEvaluator.Resolve(_arena, _root, _flat, Rows);

    /// <summary>A field of a field.</summary>
    [Benchmark]
    public int Nested() => FilterEvaluator.Resolve(_arena, _root, _nested, Rows);
}
