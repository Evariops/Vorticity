// A compound filter's connectives: `a AND b`, `a OR b` and `NOT a` over a verdict per row, which
// `FilterEvaluator` applies in place as it folds a predicate tree, and `IN` lists fold with `OR`.
//
// The states are drawn at random over the three, so no connective's answer is one state; each
// invocation runs over the same bytes, which `AND` and `OR` leave as they are after the first
// (both are idempotent) and `NOT` flips, and the kernels do the same work whatever the bytes.
using System;
using System.Collections.Generic;

using BenchmarkDotNet.Attributes;

using Vorticity.Compute;

namespace Vorticity.Benchmarks;

/// <summary><c>Trilean.And</c>, <c>Or</c> and <c>Not</c> over a batch and over a chunk.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Kernel)]
public class TrileanLogicBenchmarks
{
    /// <summary>A scan batch's rows, and a chunk's.</summary>
    [Params(8192, 65536)]
    public int Rows { get; set; }

    private byte[] _left = [];
    private byte[] _right = [];

    /// <summary>What one invocation reads: a byte per row of each operand.</summary>
    public static (long Rows, long Bytes) BenchmarkWork(
        string method, IReadOnlyDictionary<string, object?> parameters)
    {
        int rows = (int)parameters["Rows"]!;
        return (rows, method == nameof(Not) ? rows : 2L * rows);
    }

    [GlobalSetup]
    public void Setup()
    {
        Random random = new Random(20260926);
        _left = new byte[Rows];
        _right = new byte[Rows];
        for (int i = 0; i < Rows; i++)
        {
            _left[i] = (byte)random.Next(3);
            _right[i] = (byte)random.Next(3);
        }
    }

    [Benchmark(Description = "and")]
    public byte And()
    {
        Trilean.And(_left, _right);
        return _left[0];
    }

    [Benchmark(Description = "or")]
    public byte Or()
    {
        Trilean.Or(_left, _right);
        return _left[0];
    }

    [Benchmark(Description = "not")]
    public byte Not()
    {
        Trilean.Not(_left);
        return _left[0];
    }
}
