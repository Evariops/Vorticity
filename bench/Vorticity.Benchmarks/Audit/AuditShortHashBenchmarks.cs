// Audit AG-04: XXH3-64 of a value of 8 or 4 bytes, through the library's general entry and inline.
//
// 4,096 values hashed a call, each the hash a distinct count takes of a number.
using System;
using System.IO.Hashing;
using System.Runtime.InteropServices;

using BenchmarkDotNet.Attributes;

using Vorticity.Aggregating;

namespace Vorticity.Benchmarks.Audit;

/// <summary>A number's hash, the library's against the inline one.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public class AuditShortHashBenchmarks
{
    private const int Values = 4_096;

    private long[] _values = null!;

    /// <summary>Draws the values.</summary>
    [GlobalSetup]
    public void Setup()
    {
        Random random = new Random(67);
        _values = new long[Values];
        for (int i = 0; i < Values; i++)
        {
            _values[i] = random.NextInt64();
        }
    }

    /// <summary>Eight bytes through the library.</summary>
    [Benchmark(Baseline = true, OperationsPerInvoke = Values)]
    public ulong Library8()
    {
        ulong sum = 0;
        foreach (long value in _values)
        {
            long held = value;
            sum += XxHash3.HashToUInt64(MemoryMarshal.AsBytes(new ReadOnlySpan<long>(ref held)));
        }

        return sum;
    }

    /// <summary>Eight bytes inline.</summary>
    [Benchmark(OperationsPerInvoke = Values)]
    public ulong Inline8()
    {
        ulong sum = 0;
        foreach (long value in _values)
        {
            sum += ShortHash.Of8((ulong)value);
        }

        return sum;
    }

    /// <summary>Four bytes through the library.</summary>
    [Benchmark(OperationsPerInvoke = Values)]
    public ulong Library4()
    {
        ulong sum = 0;
        foreach (long value in _values)
        {
            int held = (int)value;
            sum += XxHash3.HashToUInt64(MemoryMarshal.AsBytes(new ReadOnlySpan<int>(ref held)));
        }

        return sum;
    }

    /// <summary>Four bytes inline.</summary>
    [Benchmark(OperationsPerInvoke = Values)]
    public ulong Inline4()
    {
        ulong sum = 0;
        foreach (long value in _values)
        {
            sum += ShortHash.Of4((uint)value);
        }

        return sum;
    }
}
