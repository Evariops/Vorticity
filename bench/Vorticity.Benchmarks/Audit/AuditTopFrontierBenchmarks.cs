// Audit AG-10: what narrowing a batch to the rows that reach a top's frontier costs, every row selected: a group key's
// frontier (a top of groups by their key) and a column's extreme (a top of groups by their one maximum), the bits of
// each word of 64 rows built from the column.
//
// A batch of 65,536 random 64-bit values, the frontier at the middle, from the least and from the greatest.
using System;
using System.Runtime.InteropServices;

using BenchmarkDotNet.Attributes;

using Vorticity.Aggregating;
using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Expressions;
using Vorticity.Types;

namespace Vorticity.Benchmarks.Audit;

/// <summary>A batch narrowed to a top's frontier.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public class AuditTopFrontierBenchmarks
{
    private const int Rows = 65_536;

    private readonly ulong[] _narrowed = new ulong[Rows / 64];
    private CanonicalArena _arena = null!;
    private int _node;
    private ColumnShape _shape = null!;
    private FixedKeys<long> _keys = null!;
    private int _frontier;

    /// <summary>From the greatest.</summary>
    [Params(false, true)]
    public bool Descending { get; set; }

    /// <summary>Draws the batch and makes the frontier's group.</summary>
    [GlobalSetup]
    public void Setup()
    {
        long[] values = new long[Rows];
        Random random = new Random(1011);
        for (int i = 0; i < Rows; i++)
        {
            values[i] = random.NextInt64(-1_000_000, 1_000_000);
        }

        DTypeArena types = new DTypeArena();
        DType dtype = types.Primitive(PType.I64, Nullability.NonNullable);
        _arena = new CanonicalArena(Rows * 8 + 4096);
        VortexBuffer buffer = _arena.Allocate(Rows * 8, 8, out Span<byte> bytes);
        values.AsSpan().CopyTo(MemoryMarshal.Cast<byte, long>(bytes));
        _node = _arena.AddPrimitive(dtype, Rows, Validity.NonNullable, PType.I64, buffer);
        _shape = new ColumnShape(new ColumnSym(Expr.Field("k"), VortexType.Int64, null, null, -1, []));

        // The frontier's group: the middle value, grouped first.
        _keys = new FixedKeys<long>(_shape, sorted: false);
        CanonicalArena first = new CanonicalArena(4096);
        VortexBuffer one = first.Allocate(8, 8, out Span<byte> middle);
        MemoryMarshal.Write(middle, 0L);
        int node = first.AddPrimitive(dtype, 1, Validity.NonNullable, PType.I64, one);
        int[] groups = new int[1];
        _keys.Assign(first, [node], 1, [], groups, new GroupRanges());
        _frontier = groups[0];
    }

    /// <summary>A group key's frontier, by row.</summary>
    [Benchmark(OperationsPerInvoke = Rows)]
    public bool Key() => _keys.Narrow(_arena, [_node], Rows, [], _frontier, Descending, _narrowed);

    /// <summary>A column's extreme, by row.</summary>
    [Benchmark(OperationsPerInvoke = Rows)]
    public bool Extreme() => ValueFrontier.Narrow(_arena, _node, _shape, Rows, [], 0, Descending, _narrowed);
}
