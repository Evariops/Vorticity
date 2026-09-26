// A boolean column filled from CLR values, `ColumnBuilder<bool>.Append(ReadOnlySpan<bool>)`: a byte
// per value in, a bit per value into the builder's bitmap.
//
// 65 536 values at random, appended after three single ones so the span starts off a byte.
using System;
using System.Collections.Generic;

using BenchmarkDotNet.Attributes;

using Vorticity.Buffers;
using Vorticity.Types;
using Vorticity.Writing;

namespace Vorticity.Benchmarks;

/// <summary><c>BoolStore.Append</c> of 65 536 booleans.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Kernel)]
public class BoolAppendBenchmarks
{
    private const int Rows = 1 << 16;

    private readonly AlignedBufferPool _pool = new AlignedBufferPool();
    private BoolStore? _store;
    private bool[] _values = [];

    /// <summary>What one invocation reads: a byte per value.</summary>
    public static (long Rows, long Bytes) BenchmarkWork(
        string method, IReadOnlyDictionary<string, object?> parameters) => (Rows, Rows);

    [GlobalSetup]
    public void Setup()
    {
        Random random = new Random(20260927);
        _values = new bool[Rows];
        for (int i = 0; i < Rows; i++)
        {
            _values[i] = random.Next(2) == 0;
        }

        _store = new BoolStore(new DTypeArena().Bool(Nullability.NonNullable), _pool);
    }

    [Benchmark(Description = "bool append")]
    public int Append()
    {
        BoolStore store = _store!;
        store.Append(true);
        store.Append(false);
        store.Append(true);
        store.Append(_values);
        store.Truncate(0);
        return Rows;
    }
}
