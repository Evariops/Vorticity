// Audit AG-10: what counting a span of 64-bit integers into a percentile's sketch costs once its dense window holds their
// buckets, as a percentile of durations folds them. The ISA under test is the process's: run once as is and once with
// DOTNET_EnableAVX512=0.
//
// 65,536 positive values drawn between 1,000 and 1,000,000, a sketch of 1 % relative error, primed with them.
using System;

using BenchmarkDotNet.Attributes;

using Vorticity.Aggregating;

namespace Vorticity.Benchmarks.Audit;

/// <summary>A span of longs counted into a sketch.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public class AuditQuantileAddBenchmarks
{
    private const int Rows = 65_536;

    private readonly long[] _values = new long[Rows];
    private QuantileSketch _sketch = null!;

    /// <summary>Draws the values and primes the sketch.</summary>
    [GlobalSetup]
    public void Setup()
    {
        Random random = new Random(1016);
        for (int i = 0; i < Rows; i++)
        {
            _values[i] = random.NextInt64(1_000, 1_000_000);
        }

        _sketch = new QuantileSketch(QuantileSketch.BitsFor(0.01));
        _sketch.AddAll<long>(_values);
        _sketch.AddAll<long>(_values);
    }

    /// <summary>The span counted, by value.</summary>
    [Benchmark(OperationsPerInvoke = Rows)]
    public long Add() => _sketch.AddAll<long>(_values);
}
