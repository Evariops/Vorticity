// Audit S1, CP-01: what packing the values a mask keeps costs per row, 32- and 64-bit values, half the rows kept. Run it
// as it is and under DOTNET_EnableAVX512=0 (AVX2) and DOTNET_EnableAVX2=0 (SSE).
using System;
using System.Numerics;

using BenchmarkDotNet.Attributes;

using Vorticity.Compute;

namespace Vorticity.Benchmarks.Audit;

/// <summary>The values of 65,536 rows a mask keeps half of, packed.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public class AuditMaskFilterBenchmarks
{
    private const int Rows = 65_536;

    private int[] _ints = null!;
    private long[] _longs = null!;
    private ulong[] _mask = null!;
    private int[] _intsKept = null!;
    private long[] _longsKept = null!;

    /// <summary>Draws the values and the mask.</summary>
    [GlobalSetup]
    public void Setup()
    {
        Random random = new Random(0xF1);
        _ints = new int[Rows];
        _longs = new long[Rows];
        _mask = new ulong[Rows / 64];
        for (int i = 0; i < Rows; i++)
        {
            _ints[i] = random.Next();
            _longs[i] = random.NextInt64();
        }

        int kept = 0;
        for (int w = 0; w < _mask.Length; w++)
        {
            _mask[w] = (ulong)random.NextInt64() ^ ((ulong)random.Next() << 33);
            kept += BitOperations.PopCount(_mask[w]);
        }

        _intsKept = new int[kept];
        _longsKept = new long[kept];
    }

    /// <summary>32-bit values.</summary>
    [Benchmark(OperationsPerInvoke = Rows)]
    public int Ints()
    {
        MaskFilter.Compress<int>(_ints, _mask, _intsKept);
        return _intsKept[^1];
    }

    /// <summary>64-bit values.</summary>
    [Benchmark(OperationsPerInvoke = Rows)]
    public long Longs()
    {
        MaskFilter.Compress<long>(_longs, _mask, _longsKept);
        return _longsKept[^1];
    }
}
