// Audit WR-03 (read side): what ALP's decoding of a double column costs a value, on the vector widths a machine has.
//
// 65,536 integers as ALP encodes prices of two decimals, decoded at their exponents. Run as it is (AVX-512 here), and
// under DOTNET_EnableAVX512=0 (AVX2, which has no conversion from 64-bit integers to doubles).
using System;

using BenchmarkDotNet.Attributes;

using Vorticity.Arrays.Decoders.Compressed;

namespace Vorticity.Benchmarks.Audit;

/// <summary>ALP's decoding of doubles, per value.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public class AuditAlpDecodeBenchmarks
{
    private const int Values = 65_536;

    private readonly long[] _encoded = new long[Values];
    private readonly double[] _decoded = new double[Values];

    /// <summary>Draws the integers.</summary>
    [GlobalSetup]
    public void Setup()
    {
        Random random = new Random(11);
        for (int i = 0; i < Values; i++)
        {
            _encoded[i] = random.NextInt64(0, 1_000_000);
        }
    }

    /// <summary>The integers decoded to doubles of two decimals.</summary>
    [Benchmark(OperationsPerInvoke = Values)]
    public double Decode()
    {
        AlpTables.DecodeDouble(_encoded, _decoded, 2, 0);
        return _decoded[Values - 1];
    }
}
