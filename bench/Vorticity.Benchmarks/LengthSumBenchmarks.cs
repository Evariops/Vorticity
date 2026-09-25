using System;
using System.Collections.Generic;

using BenchmarkDotNet.Attributes;

using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Types;

namespace Vorticity.Benchmarks;

/// <summary>
/// The sum of a column's lengths alone, which sizes the heap of every encoding that tiles one:
/// the library's <c>ViewKernels.SumLengths</c> against the frozen copy of the view kernels, in one
/// process and on one clock.
/// </summary>
/// <remarks>
/// <c>u32</c> and <c>i32</c> are the two widths files give lengths in, 0 to 60 bytes each. The arms
/// are checked against each other before anything is timed.
/// </remarks>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Kernel)]
[MinColumn]
[MedianColumn]
[CyclesColumn]
public class LengthSumBenchmarks
{
    private byte[] _lengths = [];
    private PType _type;

    /// <summary>The lengths' physical type.</summary>
    [ParamsSource(nameof(Shapes))]
    public string Shape { get; set; } = "u32";

    /// <summary>Lengths summed in one call.</summary>
    [ParamsSource(nameof(RowCounts))]
    public int Rows { get; set; } = 1024;

    /// <summary>Every shape in every profile.</summary>
    public static IEnumerable<string> Shapes => ["u32", "i32"];

    /// <summary>Lengths in the first-level cache, and a scan window's.</summary>
    public static IEnumerable<int> RowCounts => [1024, 131_072];

    /// <summary>What one invocation reads: a length a row.</summary>
    /// <param name="method">Unused; every arm reads the same.</param>
    /// <param name="parameters">The case's rows.</param>
    public static (long Rows, long Bytes) BenchmarkWork(string method, IReadOnlyDictionary<string, object?> parameters)
    {
        int rows = (int)parameters[nameof(Rows)]!;
        return (rows, (long)rows * 4);
    }

    [GlobalSetup]
    public void Setup()
    {
        _type = Shape == "u32" ? PType.U32 : PType.I32;
        _lengths = new byte[Rows * 4];
        Random random = new Random(20260925);
        for (int row = 0; row < Rows; row++)
        {
            BitConverter.TryWriteBytes(_lengths.AsSpan(row * 4), random.Next(61));
        }

        if (Original() != Current())
        {
            throw new InvalidOperationException($"{Shape}: the library's sum differs from the original's.");
        }
    }

    [Benchmark(Baseline = true)]
    public long Original() => ViewKernelsOriginal.SumLengths(_lengths, _type, default, Rows).Total;

    [Benchmark]
    public long Current() => ViewKernels.SumLengths(_lengths, _type, default, Rows).Total;
}
