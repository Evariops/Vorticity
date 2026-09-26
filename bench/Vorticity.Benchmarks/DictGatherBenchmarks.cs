// A dictionary expanded through its codes, the gather under every dictionary-encoded column a scan
// decodes: one value row per code, the codes a byte each, as a dictionary of up to 256 values keeps
// them.
//
// The dictionaries are small, the shape of a low-cardinality column -- a status, a country, a
// category -- where the whole dictionary fits a register or two and the gather can be a shuffle.
using System;
using System.Collections.Generic;

using BenchmarkDotNet.Attributes;

using Vorticity.Arrays.Decoders.Compressed;
using Vorticity.Types;

namespace Vorticity.Benchmarks;

/// <summary><c>RowKernels.Gather</c> of 65 536 rows through byte codes.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Kernel)]
public class DictGatherBenchmarks
{
    private const int Rows = 1 << 16;

    /// <summary>Bytes per value row.</summary>
    [Params(1, 2, 4, 8)]
    public int Width { get; set; }

    /// <summary>Values in the dictionary.</summary>
    [Params(12, 100)]
    public int Entries { get; set; }

    private byte[] _codes = [];
    private byte[] _values = [];
    private byte[] _destination = [];

    /// <summary>What one invocation reads and writes: a code and a value row per row.</summary>
    public static (long Rows, long Bytes) BenchmarkWork(
        string method, IReadOnlyDictionary<string, object?> parameters) =>
        (Rows, Rows * (1L + (parameters.TryGetValue(nameof(Width), out object? w) && w is int width ? width : 4)));

    [GlobalSetup]
    public void Setup()
    {
        Random random = new Random(20260926);
        _codes = new byte[Rows];
        for (int i = 0; i < Rows; i++)
        {
            _codes[i] = (byte)random.Next(Entries);
        }

        _values = new byte[Entries * Width];
        random.NextBytes(_values);
        _destination = new byte[Rows * Width];
    }

    [Benchmark(Description = "dictionary gather")]
    public int Gather() =>
        RowKernels.Gather(_codes, PType.U8, _values, Width, Entries, _destination, Rows);
}
