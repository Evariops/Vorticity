// A canonical column against a literal, the kernel under every filter a scan evaluates on decoded
// values: one verdict per row, a byte each.
//
// The scalar loop was kept on purpose, because on NEON a vector compare has to narrow its mask
// back to bytes and that costs more than the compare saves. On AVX-512 nothing is narrowed: a
// compare writes a mask register, and 64 rows' verdicts are one word spread back to bytes by a
// shuffle. This class measures the entry point, with and without nulls, at the widths that matter.
using System;
using System.Collections.Generic;

using BenchmarkDotNet.Attributes;

using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Compute;
using Vorticity.Expressions;
using Vorticity.Types;

namespace Vorticity.Benchmarks;

/// <summary><c>column &lt; literal</c> over 65 536 rows, by type and validity.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Kernel)]
public class CompareKernelBenchmarks
{
    private const int Rows = 1 << 16;

    [Params("i64", "i32", "u8", "f64")]
    public string Type { get; set; } = "i64";

    private PType Physical => Type switch { "i32" => PType.I32, "u8" => PType.U8, "f64" => PType.F64, _ => PType.I64 };

    /// <summary>Whether a quarter of the rows are null, which the nullable loop answers.</summary>
    [Params(false, true)]
    public bool Nulls { get; set; }

    private CanonicalArena _arena = new CanonicalArena();
    private int _node;
    private FilterLiteral _literal;
    private byte[] _states = [];

    /// <summary>What one invocation reads: every row's value, and writes a byte per row.</summary>
    public static (long Rows, long Bytes) BenchmarkWork(
        string method, IReadOnlyDictionary<string, object?> parameters)
    {
        int width = parameters.TryGetValue(nameof(Type), out object? value) && value is string t ? t switch { "i32" => 4, "u8" => 1, _ => 8 } : 8;
        return (Rows, Rows * (long)(width + 1));
    }

    [GlobalSetup]
    public void Setup()
    {
        Random random = new Random(20260926);
        DTypeArena types = new DTypeArena();
        _arena = new CanonicalArena();
        PType ptype = Physical;
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

        _node = _arena.AddPrimitive(
            types.Primitive(ptype, Nulls ? Nullability.Nullable : Nullability.NonNullable), Rows, validity, ptype, values);
        _literal = ptype switch
        {
            PType.F64 => FilterLiteral.From(500.0),
            PType.U8 => FilterLiteral.From(128UL),
            _ => FilterLiteral.From(0L),
        };
        _states = new byte[Rows];
    }

    [Benchmark(Description = "column < literal")]
    public byte Less()
    {
        ComparisonKernels.Compare(_arena, _node, ComparisonOp.Less, _literal, _states);
        return _states[Rows - 1];
    }
}
