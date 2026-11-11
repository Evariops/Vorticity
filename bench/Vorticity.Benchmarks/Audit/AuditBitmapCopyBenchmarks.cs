// What bitmaps cost made from bytes of 0 or 1 (a bool column's validity or values packed), and
// copied from a bit off a byte boundary (a slice's validity concatenated), on AVX-512 and under DOTNET_EnableAVX512=0.
//
// 65,536 bits.
using System;

using BenchmarkDotNet.Attributes;

using Vorticity.Arrays.Decoders.Canonical;

namespace Vorticity.Benchmarks.Audit;

/// <summary>Bitmaps packed and copied.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public class AuditBitmapCopyBenchmarks
{
    private const int Bits = 65_536;

    private readonly byte[] _bytes = new byte[Bits];
    private readonly byte[] _bits = new byte[(Bits / 8) + 8];
    private readonly byte[] _copy = new byte[(Bits / 8) + 8];

    /// <summary>Draws the bytes and the bits.</summary>
    [GlobalSetup]
    public void Setup()
    {
        Random random = new Random(79);
        for (int i = 0; i < Bits; i++)
        {
            _bytes[i] = (byte)random.Next(2);
        }

        random.NextBytes(_bits);
    }

    /// <summary>Bytes packed into bits, by bit.</summary>
    [Benchmark(OperationsPerInvoke = Bits)]
    public byte Pack()
    {
        BitmapKernels.PackBytes(_bytes, _copy);
        return _copy[7];
    }

    /// <summary>Bits copied from bit 3 to bit 0, by bit.</summary>
    [Benchmark(OperationsPerInvoke = Bits)]
    public byte Copy()
    {
        BitmapKernels.CopyRange(_bits, 3, _copy, 0, Bits);
        return _copy[7];
    }
}
