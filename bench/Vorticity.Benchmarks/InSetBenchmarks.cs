// `column IN (...)` over an integer column, with the candidates hashed once for the scan as the
// filter evaluator does from four candidates up: one verdict per row, a byte each.
//
// The values fall in [0, 1000) and the candidates are drawn from the same range, so a row matches
// with odds of one in a thousand per candidate: the shape of an id filter, almost every row a miss.
using System;
using System.Collections.Generic;

using BenchmarkDotNet.Attributes;

using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Compute;
using Vorticity.Expressions;
using Vorticity.Types;

namespace Vorticity.Benchmarks;

/// <summary><c>ComparisonKernels.In</c> with a prepared set, over 65 536 rows.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Kernel)]
public class InSetBenchmarks
{
    private const int Rows = 1 << 16;

    [Params("i64", "i32")]
    public string Type { get; set; } = "i64";

    /// <summary>How many candidates the <c>IN</c> lists.</summary>
    [Params(4, 8, 16, 64)]
    public int Candidates { get; set; }

    private CanonicalArena _arena = new CanonicalArena();
    private int _node;
    private FilterLiteral[] _literals = [];
    private InSet? _set;
    private byte[] _states = [];
    private byte[] _scratch = [];

    /// <summary>What one invocation reads: every row's value, and writes a byte per row.</summary>
    public static (long Rows, long Bytes) BenchmarkWork(
        string method, IReadOnlyDictionary<string, object?> parameters) =>
        (Rows, Rows * ((parameters.TryGetValue(nameof(Type), out object? t) && t is "i32" ? 4L : 8L) + 1));

    [GlobalSetup]
    public void Setup()
    {
        Random random = new Random(20260926);
        DTypeArena types = new DTypeArena();
        _arena = new CanonicalArena();
        PType ptype = Type == "i32" ? PType.I32 : PType.I64;
        int width = ptype.ByteWidth();
        VortexBuffer values = _arena.Allocate(Rows * width, width, out Span<byte> bytes);
        for (int i = 0; i < Rows; i++)
        {
            long value = random.Next(1000);
            if (ptype == PType.I32)
            {
                BitConverter.TryWriteBytes(bytes.Slice(i * 4, 4), (int)value);
            }
            else
            {
                BitConverter.TryWriteBytes(bytes.Slice(i * 8, 8), value);
            }
        }

        _node = _arena.AddPrimitive(types.Primitive(ptype, Nullability.NonNullable), Rows, Validity.NonNullable, ptype, values);
        _literals = new FilterLiteral[Candidates];
        for (int i = 0; i < Candidates; i++)
        {
            _literals[i] = FilterLiteral.From((long)random.Next(1000));
        }

        _set = InSet.TryBuild(_literals, signed: true);
        _states = new byte[Rows];
        _scratch = new byte[Rows];
    }

    [Benchmark(Description = "column in (...)")]
    public byte In()
    {
        ComparisonKernels.In(_arena, _node, _literals, _states, _scratch, _set);
        return _states[Rows - 1];
    }
}
