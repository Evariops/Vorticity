// Audit AG-10: what grouping a batch by an integer key costs once every value of the span its statistics bound is met:
// each row's group is its value less the least, the span numbered whole.
//
// A key bound to [0, 4095], every value met by a first batch; then batches of 65,536 rows drawn among them, by width.
using System;
using System.Runtime.InteropServices;

using BenchmarkDotNet.Attributes;

using Vorticity.Aggregating;
using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Expressions;
using Vorticity.Types;

namespace Vorticity.Benchmarks.Audit;

/// <summary>A batch grouped by a key numbered whole.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public class AuditDirectNumbersBenchmarks
{
    private const int Rows = 65_536;
    private const int Span = 4_096;

    private readonly int[] _groups = new int[Rows];
    private CanonicalArena _arena = null!;
    private int _node;
    private GroupKeys _keys = null!;

    /// <summary>The key's width in bytes.</summary>
    [Params(2, 4, 8)]
    public int Width { get; set; }

    /// <summary>Draws the batch and meets every value.</summary>
    [GlobalSetup]
    public void Setup()
    {
        switch (Width)
        {
            case 2:
                Make<short>(PType.I16, VortexType.Int16);
                break;
            case 4:
                Make<int>(PType.I32, VortexType.Int32);
                break;
            default:
                Make<long>(PType.I64, VortexType.Int64);
                break;
        }
    }

    /// <summary>The batch grouped, by row.</summary>
    [Benchmark(OperationsPerInvoke = Rows)]
    public bool Assign() => _keys.Assign(_arena, [_node], Rows, [], _groups, new GroupRanges());

    private void Make<T>(PType ptype, VortexType type)
        where T : unmanaged, IEquatable<T>, IComparable<T>, System.Numerics.INumberBase<T>
    {
        DTypeArena types = new DTypeArena();
        DType dtype = types.Primitive(ptype, Nullability.NonNullable);
        FixedKeys<T> keys = new FixedKeys<T>(new ColumnShape(new ColumnSym(Expr.Field("k"), type, null, null, -1, [])), sorted: false, new KeyBounds(0, Span - 1));
        if (!keys.NumberWhole(groupBytes: 16))
        {
            throw new InvalidOperationException("The span is not numbered whole.");
        }

        T[] all = new T[Span];
        for (int i = 0; i < Span; i++)
        {
            all[i] = T.CreateTruncating(i);
        }

        (CanonicalArena first, int firstNode) = Column(all, ptype, dtype);
        keys.Assign(first, [firstNode], Span, [], new int[Span], new GroupRanges());

        T[] values = new T[Rows];
        Random random = new Random(1012);
        for (int i = 0; i < Rows; i++)
        {
            values[i] = T.CreateTruncating(random.Next(Span));
        }

        (_arena, _node) = Column(values, ptype, dtype);
        _keys = keys;
    }

    private static (CanonicalArena Arena, int Node) Column<T>(T[] values, PType ptype, DType dtype)
        where T : unmanaged
    {
        int size = Marshal.SizeOf<T>();
        CanonicalArena arena = new CanonicalArena(values.Length * size + 4096);
        VortexBuffer buffer = arena.Allocate(values.Length * size, size, out Span<byte> bytes);
        MemoryMarshal.AsBytes(values.AsSpan()).CopyTo(bytes);
        return (arena, arena.AddPrimitive(dtype, values.Length, Validity.NonNullable, ptype, buffer));
    }
}
