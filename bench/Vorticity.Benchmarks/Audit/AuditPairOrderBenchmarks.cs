// What checking that a column rises costs per row, as a writer's statistics check it: the registers clear the
// pairs of adjacent values, a scalar walk the rest. Run it under DOTNET_EnableAVX512=0 (AVX2) and DOTNET_EnableAVX2=0.
using System;

using BenchmarkDotNet.Attributes;

using Vorticity.Compute;

namespace Vorticity.Benchmarks.Audit;

/// <summary>Whether 65,536 rising values rise, 32- and 64-bit.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public class AuditPairOrderBenchmarks
{
    private const int Rows = 65_536;

    private int[] _ints = null!;
    private long[] _longs = null!;

    /// <summary>Draws rising values with ties.</summary>
    [GlobalSetup]
    public void Setup()
    {
        Random random = new Random(0x50);
        _ints = new int[Rows];
        _longs = new long[Rows];
        for (int i = 1; i < Rows; i++)
        {
            _ints[i] = _ints[i - 1] + random.Next(3);
            _longs[i] = _longs[i - 1] + random.Next(3);
        }
    }

    /// <summary>32-bit values.</summary>
    [Benchmark(OperationsPerInvoke = Rows)]
    public bool Ints() => Rises<int>(_ints);

    /// <summary>64-bit values.</summary>
    [Benchmark(OperationsPerInvoke = Rows)]
    public bool Longs() => Rises<long>(_longs);

    private static bool Rises<T>(ReadOnlySpan<T> values)
        where T : unmanaged, System.Numerics.INumber<T>
    {
        bool repeats = false;
        int i = PairOrder.Cleared<T, PairOrder.Rising>(values, ref repeats);
        for (; i < values.Length; i++)
        {
            if (values[i] < values[i - 1])
            {
                return false;
            }
        }

        return true;
    }
}
