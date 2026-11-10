// Audit VW-07: what writing a chunk's indices costs, a dictionary's codes or a string column's offsets narrowed from ints
// to the width the column needs: 65,536 codes below 200 to one byte, below 50,000 to two, and offsets kept at eight.
using System;

using BenchmarkDotNet.Attributes;

using Vorticity.Writing;

namespace Vorticity.Benchmarks.Audit;

/// <summary>A chunk's indices narrowed.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public class AuditIndexNarrowBenchmarks
{
    private const int Rows = 65_536;

    private int[] _values = [];
    private byte[] _into = [];

    /// <summary>The bytes each index takes.</summary>
    [Params(1, 2, 8)]
    public int Width { get; set; }

    /// <summary>Draws the indices.</summary>
    [GlobalSetup]
    public void Setup()
    {
        Random random = new Random(67);
        int bound = Width == 1 ? 200 : 50_000;
        _values = new int[Rows];
        for (int i = 0; i < Rows; i++)
        {
            _values[i] = random.Next(bound);
        }

        _into = new byte[Rows * Width];
    }

    /// <summary>The indices written; their first byte.</summary>
    [Benchmark]
    public byte Narrow()
    {
        ArrayBlobWriter.WriteIndices(_values, Width, _into);
        return _into[0];
    }
}
