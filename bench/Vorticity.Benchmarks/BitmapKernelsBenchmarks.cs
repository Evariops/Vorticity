// The bitmap kernels every validity passes through: counting a range's set bits, copying a range
// to another bit position, and packing a byte per boolean into a bit per boolean (`vortex.bytebool`).
//
// A million bits, half set at random; the copy starts three bits into its source, so every byte of
// the destination takes bits from two source bytes.
using System;
using System.Collections.Generic;

using BenchmarkDotNet.Attributes;

using Vorticity.Arrays.Decoders.Canonical;

namespace Vorticity.Benchmarks;

/// <summary><c>BitmapKernels.CountSet</c>, <c>CopyRange</c> and <c>PackBytes</c>.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Kernel)]
public class BitmapKernelsBenchmarks
{
    private const int Bits = 1 << 20;

    private byte[] _bits = [];
    private byte[] _copy = [];
    private byte[] _bytes = [];
    private byte[] _packed = [];

    /// <summary>What one invocation reads: a bit per value, or a byte for the pack.</summary>
    public static (long Rows, long Bytes) BenchmarkWork(
        string method, IReadOnlyDictionary<string, object?> parameters) =>
        (Bits, method == nameof(Pack) ? Bits : Bits / 8);

    [GlobalSetup]
    public void Setup()
    {
        Random random = new Random(20260927);
        _bits = new byte[(Bits / 8) + 1];
        random.NextBytes(_bits);
        _copy = new byte[(Bits / 8) + 1];
        _bytes = new byte[Bits];
        for (int i = 0; i < _bytes.Length; i++)
        {
            _bytes[i] = (byte)(random.Next(2) * random.Next(1, 256));
        }

        _packed = new byte[Bits / 8];
    }

    [Benchmark(Description = "count set")]
    public int Count() => BitmapKernels.CountSet(_bits, 5, Bits - 5);

    [Benchmark(Description = "copy range")]
    public byte Copy()
    {
        BitmapKernels.CopyRange(_bits, 3, _copy, 0, Bits - 8);
        return _copy[^2];
    }

    [Benchmark(Description = "pack bytes")]
    public byte Pack()
    {
        BitmapKernels.PackBytes(_bytes, _packed);
        return _packed[^1];
    }
}
