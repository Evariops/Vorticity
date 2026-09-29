// What the FSST trainer pays to draw its sample from a column whose values sit apart.
//
// A column of N rows, empty but for its values: `Dense` has a one-byte value on every row, `Tail`
// has its 20,480 one-byte values in its last rows, as a mostly null column whose values came late,
// and `Sparse` a 32-byte value on every 256th row. The sampler draws 16 KiB, a random row at a
// time, taking the first non-empty row from it. `Original` walks there at every draw
// (`FsstSampleBefore.cs`); `Library` walks while the walk is short and searches a list of the
// non-empty rows once it is not. Both draw the same sample, which the setup checks.
using System;

using BenchmarkDotNet.Attributes;

using Vorticity.Writing;

namespace Vorticity.Benchmarks.Complexity;

/// <summary>An FSST training sample drawn from a column, against where its values sit.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public class FsstSampleBenchmarks
{
    /// <summary>The column's rows.</summary>
    [Params(65_536, 1 << 20)]
    public int Rows { get; set; }

    /// <summary>Where the values sit.</summary>
    [Params(Layout.Dense, Layout.Tail, Layout.Sparse)]
    public Layout Values { get; set; }

    /// <summary>Where a column's values sit.</summary>
    public enum Layout
    {
        /// <summary>A one-byte value on every row.</summary>
        Dense,

        /// <summary>20,480 one-byte values in the last rows.</summary>
        Tail,

        /// <summary>A 32-byte value on every 256th row.</summary>
        Sparse,
    }

    private int[] _starts = [];
    private int[] _lengths = [];
    private FsstSymbols.Line[] _into = [];

    /// <summary>Lays the column out and checks both samplers draw the same lines.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _starts = new int[Rows];
        _lengths = new int[Rows];
        int at = 0;
        for (int row = 0; row < Rows; row++)
        {
            int length = Values switch
            {
                Layout.Dense => 1,
                Layout.Tail => row >= Rows - 20_480 ? 1 : 0,
                _ => row % 256 == 0 ? 32 : 0,
            };
            _starts[row] = at;
            _lengths[row] = length;
            at += length;
        }

        _into = new FsstSymbols.Line[(1 << 14) + 1];
        FsstSymbols.Line[] original = new FsstSymbols.Line[_into.Length];
        FsstSampleBefore.MakeSample(_starts, _lengths, original, out int expected, out _);
        FsstSymbols.MakeSample(_starts, _lengths, _into, out int drawn, out _);
        if (drawn != expected)
        {
            throw new InvalidOperationException("The samplers drew different samples.");
        }

        for (int i = 0; i < drawn; i++)
        {
            if (_into[i].Start != original[i].Start || _into[i].Length != original[i].Length)
            {
                throw new InvalidOperationException("The samplers drew different samples.");
            }
        }
    }

    /// <summary>The sample drawn by walking from every random row to the next non-empty one.</summary>
    [Benchmark(Baseline = true)]
    public int Original()
    {
        FsstSampleBefore.MakeSample(_starts, _lengths, _into, out int drawn, out _);
        return drawn;
    }

    /// <summary>The sample drawn by the library's sampler.</summary>
    [Benchmark]
    public int Library()
    {
        FsstSymbols.MakeSample(_starts, _lengths, _into, out int drawn, out _);
        return drawn;
    }
}
