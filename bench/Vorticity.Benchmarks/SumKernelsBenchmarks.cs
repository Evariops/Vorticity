// The sums an ungrouped aggregation takes of a dense span: `SumKernels.Signed`, `Unsigned` and
// `Float`, over a million values of each width a column may have -- and a million doubles with
// one NaN among them, which a float sum takes twice; and longs of the whole range, whose sums leave
// 64 bits, next to longs of 40 bits, whose sums do not.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

using BenchmarkDotNet.Attributes;

using Vorticity.Aggregating;

namespace Vorticity.Benchmarks;

/// <summary>Sums of 1 048 576 values.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Kernel)]
public class SumKernelsBenchmarks
{
    private const int Rows = 1 << 20;

    [Params("u8", "i16", "i64", "i64 small", "f64", "f64 nan", "f32")]
    public string Type { get; set; } = "u8";

    private byte[] _bytes = [];

    /// <summary>What one invocation reads: every value.</summary>
    public static (long Rows, long Bytes) BenchmarkWork(
        string method, IReadOnlyDictionary<string, object?> parameters) =>
        (Rows, Rows * (parameters.TryGetValue(nameof(Type), out object? t) ? t switch { "u8" => 1L, "i16" => 2L, "f32" => 4L, _ => 8L } : 8L));

    [GlobalSetup]
    public void Setup()
    {
        Random random = new Random(20260927);
        _bytes = new byte[Rows * 8];
        random.NextBytes(_bytes);
        if (Type is "f64" or "f64 nan")
        {
            Span<double> doubles = MemoryMarshal.Cast<byte, double>(_bytes);
            for (int i = 0; i < doubles.Length; i++)
            {
                doubles[i] = random.NextDouble() * 1000;
            }

            if (Type is "f64 nan")
            {
                doubles[Rows / 2] = double.NaN;
            }
        }
        else if (Type is "i64 small")
        {
            Span<long> longs = MemoryMarshal.Cast<byte, long>(_bytes);
            for (int i = 0; i < longs.Length; i++)
            {
                longs[i] >>= 24;
            }
        }
        else if (Type is "f32")
        {
            Span<float> singles = MemoryMarshal.Cast<byte, float>(_bytes);
            for (int i = 0; i < singles.Length; i++)
            {
                singles[i] = (float)(random.NextDouble() * 1000);
            }
        }
    }

    [Benchmark(Description = "sum")]
    public double Sum() => Type switch
    {
        "u8" => (double)SumKernels.Unsigned(_bytes.AsSpan(0, Rows)),
        "i16" => (double)SumKernels.Signed(MemoryMarshal.Cast<byte, short>(_bytes.AsSpan())[..Rows]),
        "i64" or "i64 small" => (double)SumKernels.Signed(MemoryMarshal.Cast<byte, long>(_bytes.AsSpan())[..Rows]),
        "f64" or "f64 nan" => SumKernels.Float(MemoryMarshal.Cast<byte, double>(_bytes.AsSpan())[..Rows], out _),
        _ => SumKernels.Float(MemoryMarshal.Cast<byte, float>(_bytes.AsSpan())[..Rows], out _),
    };
}
