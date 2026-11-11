// What the set bits of a bitmap cost counted, a short run (800 rows, a hundred bytes), a batch's validity (8,192 rows) or
// a long selection (524,288), starting off a byte boundary as a slice's does.
using System;

using BenchmarkDotNet.Attributes;

using Vorticity.Arrays.Decoders.Canonical;

namespace Vorticity.Benchmarks.Audit;

/// <summary>A bitmap's set bits counted.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public class AuditCountSetBenchmarks
{
    private byte[] _bits = [];

    /// <summary>The bits counted.</summary>
    [Params(800, 8_192, 524_288)]
    public int Bits { get; set; }

    /// <summary>Draws the bits, about half set.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _bits = new byte[(Bits / 8) + 2];
        new Random(67).NextBytes(_bits);
    }

    /// <summary>The set bits from bit 3 on.</summary>
    [Benchmark]
    public int Count() => BitmapKernels.CountSet(_bits, 3, Bits);
}
