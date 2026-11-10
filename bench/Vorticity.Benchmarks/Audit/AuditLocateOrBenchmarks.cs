// Audit IN-09: what or-ing a value's granules into a question's map costs, from a file's index of where each value lies:
// a file of 4,096 granules, 256 values each in a quarter of them at random, kept as maps; every value's granules or-ed in,
// as a prefix that every value starts with asks.
using System;

using BenchmarkDotNet.Attributes;

using Vorticity.Indexes;

namespace Vorticity.Benchmarks.Audit;

/// <summary>Values' granules or-ed into a map.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public class AuditLocateOrBenchmarks
{
    private const int Granules = 4_096;
    private const int Values = 256;

    private readonly ulong[] _map = new ulong[Granules / 64];
    private ColumnLocate _locate = null!;

    /// <summary>Builds the index.</summary>
    [GlobalSetup]
    public void Setup()
    {
        Random random = new Random(1025);
        byte[]?[] values = new byte[]?[Values];
        int[] starts = new int[Values + 1];
        System.Collections.Generic.List<int> granules = [];
        for (int v = 0; v < Values; v++)
        {
            values[v] = [(byte)'k', (byte)(v >> 8), (byte)v];
            starts[v] = granules.Count;
            for (int g = 0; g < Granules; g++)
            {
                if (random.Next(4) == 0)
                {
                    granules.Add(g);
                }
            }
        }

        starts[Values] = granules.Count;
        byte[] bytes = ColumnLocate.ToBytes(Granules * 64L, 64, new ArrayValues(values), starts, granules.ToArray(), most: Granules);
        _locate = ColumnLocate.From(bytes) ?? throw new InvalidOperationException("The index does not read back.");
    }

    /// <summary>Every value's granules or-ed in, by value.</summary>
    [Benchmark(OperationsPerInvoke = Values)]
    public ulong Or()
    {
        Array.Clear(_map);
        for (int v = 0; v < Values; v++)
        {
            _locate.Or(v, _map);
        }

        return _map[^1];
    }
}
