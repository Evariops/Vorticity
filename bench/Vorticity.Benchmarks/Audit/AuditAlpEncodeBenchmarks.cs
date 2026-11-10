// Audit WR-03: what ALP's encoding of a float column costs a value, on the vector widths a machine has.
//
// 65,536 doubles of two decimals (prices): the exponents chosen over their samples, then the values encoded. Run as it
// is (AVX-512 here), and under DOTNET_EnableAVX512=0 (AVX2: the 128-bit tier, the encoder having no 256-bit one, and its
// sampling scalar).
using System;

using BenchmarkDotNet.Attributes;

using Vorticity.Writing;

namespace Vorticity.Benchmarks.Audit;

/// <summary>ALP's choice of exponents and its encoding, per value.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public class AuditAlpEncodeBenchmarks
{
    private const int Values = 65_536;

    private readonly double[] _values = new double[Values];
    private readonly long[] _encoded = new long[Values];
    private readonly int[] _indices = new int[Values];
    private readonly double[] _patches = new double[Values];
    private int _e;
    private int _f;

    /// <summary>One value in how many is not a price but random bits, a patch: none, or one in ten.</summary>
    [Params(0, 10)]
    public int PatchEvery { get; set; }

    /// <summary>Draws the prices and chooses their exponents.</summary>
    [GlobalSetup]
    public void Setup()
    {
        Random random = new Random(7);
        for (int i = 0; i < Values; i++)
        {
            _values[i] = PatchEvery > 0 && i % PatchEvery == 0 ? BitConverter.Int64BitsToDouble(random.NextInt64() >> 2) : Math.Round(random.NextDouble() * 10_000, 2);
        }

        (_e, _f) = AlpPlan.BestExponents(_values);
        Console.WriteLine($"// exponents {_e}, {_f}; {Encode()} patches");
    }

    /// <summary>The exponents chosen over the values' samples.</summary>
    [Benchmark(OperationsPerInvoke = Values)]
    public int Choose()
    {
        (int e, int f) = AlpPlan.BestExponents(_values);
        return e + f;
    }

    /// <summary>The values encoded at the chosen exponents.</summary>
    [Benchmark(OperationsPerInvoke = Values)]
    public int Encode() => AlpPlan.Encode(_values, _e, _f, _encoded, _indices, _patches);
}
