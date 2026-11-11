// What a sum of a dense column of 8- or 16-bit integers costs, as an aggregate's span of values takes it.
// The ISA under test is the process's: run once as is and once with DOTNET_EnableAVX512=0 for the AVX2 path.
//
// 65,536 values drawn at random, every row.
using System;
using System.Runtime.InteropServices;

using BenchmarkDotNet.Attributes;

using Vorticity.Aggregating;

namespace Vorticity.Benchmarks.Audit;

/// <summary>Narrow integer columns summed.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public class AuditNarrowSumsBenchmarks
{
    private const int Rows = 65_536;

    private byte[] _bytes = [];

    /// <summary>Draws the values.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _bytes = new byte[Rows * 2];
        new Random(1010).NextBytes(_bytes);
    }

    /// <summary>Unsigned bytes, by row.</summary>
    [Benchmark(OperationsPerInvoke = Rows)]
    public ulong UInt8() => (ulong)SumKernels.Unsigned<byte>(_bytes.AsSpan(0, Rows));

    /// <summary>Signed bytes, by row.</summary>
    [Benchmark(OperationsPerInvoke = Rows)]
    public long Int8() => (long)SumKernels.Signed(MemoryMarshal.Cast<byte, sbyte>(_bytes.AsSpan(0, Rows)));

    /// <summary>Unsigned words, by row.</summary>
    [Benchmark(OperationsPerInvoke = Rows)]
    public ulong UInt16() => (ulong)SumKernels.Unsigned(MemoryMarshal.Cast<byte, ushort>(_bytes));

    /// <summary>Signed words, by row.</summary>
    [Benchmark(OperationsPerInvoke = Rows)]
    public long Int16() => (long)SumKernels.Signed(MemoryMarshal.Cast<byte, short>(_bytes));
}
