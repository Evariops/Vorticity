// A pco IntMult page's two latent variables joined: each value the primary times the mode's base,
// plus the secondary, plus the shift, the last step of decoding a page of integers pco chose to
// split by a common multiple.
//
// The values are mutated in place, as the decoder does, so each invocation joins what the last
// left; a multiply-add of 64-bit integers costs the same whatever they hold.
using System;
using System.Collections.Generic;

using BenchmarkDotNet.Attributes;

using Vorticity.Arrays.Decoders.Compressed.Pco;

namespace Vorticity.Benchmarks;

/// <summary><c>PcoPageDecoder.Join</c> over 65 536 written primaries.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Kernel)]
public class PcoJoinBenchmarks
{
    private const int Rows = 1 << 16;

    /// <summary>Whether the secondary is written per value or one constant for the page.</summary>
    [Params("written", "constant")]
    public string Secondary { get; set; } = "written";

    private ulong[] _values = [];
    private ulong[] _secondary = [];

    /// <summary>What one invocation reads and writes: eight bytes per value, sixteen with a written secondary.</summary>
    public static (long Rows, long Bytes) BenchmarkWork(
        string method, IReadOnlyDictionary<string, object?> parameters) => (Rows, Rows * 16L);

    [GlobalSetup]
    public void Setup()
    {
        Random random = new Random(20260926);
        _values = new ulong[Rows];
        _secondary = new ulong[Rows];
        for (int i = 0; i < Rows; i++)
        {
            _values[i] = (ulong)random.NextInt64();
            _secondary[i] = (ulong)random.Next(1000);
        }
    }

    [Benchmark(Description = "intmult join")]
    public ulong Join()
    {
        PcoPageDecoder.Join(
            _values, PcoBatchShape.Written, 0, 0,
            _secondary, Secondary == "written" ? PcoBatchShape.Written : PcoBatchShape.Constant, 7, 1_000, 3);
        return _values[^1];
    }
}
