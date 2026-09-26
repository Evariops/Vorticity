// Two columns of one type compared row by row, `a < b`, the kernel under a filter that relates two
// columns of a batch rather than a column and a literal: one verdict per row, a byte each.
using System;
using System.Collections.Generic;

using BenchmarkDotNet.Attributes;

using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Compute;
using Vorticity.Expressions;
using Vorticity.Types;

namespace Vorticity.Benchmarks;

/// <summary><c>ComparisonKernels.CompareColumns</c> over 65 536 rows, by type and validity.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Kernel)]
public class ColumnCompareBenchmarks
{
    private const int Rows = 1 << 16;

    [Params("i64", "i32", "f64")]
    public string Type { get; set; } = "i64";

    /// <summary>Whether a quarter of each side's rows are null.</summary>
    [Params(false, true)]
    public bool Nulls { get; set; }

    private CanonicalArena _arena = new CanonicalArena();
    private int _left;
    private int _right;
    private byte[] _states = [];

    /// <summary>What one invocation reads: both sides' values, and writes a byte per row.</summary>
    public static (long Rows, long Bytes) BenchmarkWork(
        string method, IReadOnlyDictionary<string, object?> parameters) =>
        (Rows, Rows * ((parameters.TryGetValue(nameof(Type), out object? t) && t is "i32" ? 8L : 16L) + 1));

    [GlobalSetup]
    public void Setup()
    {
        Random random = new Random(20260926);
        DTypeArena types = new DTypeArena();
        _arena = new CanonicalArena();
        PType ptype = Type switch { "i32" => PType.I32, "f64" => PType.F64, _ => PType.I64 };
        _left = Column(random, types, ptype);
        _right = Column(random, types, ptype);
        _states = new byte[Rows];
    }

    private int Column(Random random, DTypeArena types, PType ptype)
    {
        int width = ptype.ByteWidth();
        VortexBuffer values = _arena.Allocate(Rows * width, width, out Span<byte> bytes);
        random.NextBytes(bytes);
        if (ptype == PType.F64)
        {
            Span<double> doubles = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, double>(bytes);
            for (int i = 0; i < doubles.Length; i++)
            {
                doubles[i] = random.NextDouble() * 1000;
            }
        }

        Validity validity = Validity.NonNullable;
        if (Nulls)
        {
            VortexBuffer bits = _arena.Allocate(Rows / 8, 1, out Span<byte> valid);
            for (int i = 0; i < valid.Length; i++)
            {
                valid[i] = (byte)(random.Next(256) | random.Next(256));
            }

            validity = Validity.Bitmap(_arena.AddBool(types.Bool(Nullability.NonNullable), Rows, Validity.NonNullable, bits, 0));
        }

        return _arena.AddPrimitive(
            types.Primitive(ptype, Nulls ? Nullability.Nullable : Nullability.NonNullable), Rows, validity, ptype, values);
    }

    [Benchmark(Description = "column < column")]
    public byte Less()
    {
        ComparisonKernels.CompareColumns(_arena, _left, ComparisonOp.Less, _right, _states);
        return _states[Rows - 1];
    }
}
