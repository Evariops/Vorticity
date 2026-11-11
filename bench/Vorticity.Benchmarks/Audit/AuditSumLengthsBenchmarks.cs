// What the lengths that cut a decoded heap of texts cost summed, as bytes (short texts) or 32-bit integers.
//
// 65,536 lengths, every row.
using System;
using System.Runtime.InteropServices;

using BenchmarkDotNet.Attributes;

using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Types;

namespace Vorticity.Benchmarks.Audit;

/// <summary>Lengths summed.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public class AuditSumLengthsBenchmarks
{
    private const int Rows = 65_536;

    private byte[] _bytes = [];
    private byte[] _ints = [];

    /// <summary>Draws the lengths.</summary>
    [GlobalSetup]
    public void Setup()
    {
        Random random = new Random(97);
        _bytes = new byte[Rows];
        random.NextBytes(_bytes);
        int[] ints = new int[Rows];
        for (int i = 0; i < Rows; i++)
        {
            ints[i] = random.Next(1_000);
        }

        _ints = MemoryMarshal.AsBytes(ints.AsSpan()).ToArray();
    }

    /// <summary>Byte lengths summed, by row.</summary>
    [Benchmark(OperationsPerInvoke = Rows)]
    public long Bytes() => ViewKernels.SumLengths(_bytes, PType.U8, default, Rows).Total;

    /// <summary>32-bit lengths summed, by row.</summary>
    [Benchmark(OperationsPerInvoke = Rows)]
    public long Ints() => ViewKernels.SumLengths(_ints, PType.U32, default, Rows).Total;
}
