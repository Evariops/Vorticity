// What a run-end encoding's values cost expanded over their rows, runs of a few rows as a sorted column's
// repeated values make them, or long ones.
//
// 65,536 rows of 8-byte values in runs of 1 to 8 rows, or 64 to 512.
using System;
using System.Collections.Generic;

using BenchmarkDotNet.Attributes;

using Vorticity.Arrays;

namespace Vorticity.Benchmarks.Audit;

/// <summary>Run-end values expanded.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public class AuditRunEndBenchmarks
{
    private const int Rows = 65_536;

    private uint[] _ends = [];
    private byte[] _values = [];
    private readonly byte[] _rows = new byte[Rows * 8];

    /// <summary>The longest run.</summary>
    [Params(8, 512)]
    public int Longest { get; set; }

    /// <summary>Draws the runs and their values.</summary>
    [GlobalSetup]
    public void Setup()
    {
        Random random = new Random(103);
        List<uint> ends = [];
        int at = 0;
        while (at < Rows)
        {
            at = Math.Min(Rows, at + random.Next(Longest / 8, Longest + 1));
            ends.Add((uint)at);
        }

        _ends = [.. ends];
        _values = new byte[_ends.Length * 8];
        random.NextBytes(_values);
    }

    /// <summary>The runs expanded, by row.</summary>
    [Benchmark(OperationsPerInvoke = Rows)]
    public byte Expand()
    {
        CanonicalArena.Repeat<ulong>(_ends, _values, _rows);
        return _rows[^1];
    }
}
