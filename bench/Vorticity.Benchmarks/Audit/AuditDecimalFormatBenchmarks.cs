// What a decimal costs formatted as text, its unscaled 256-bit value divided by 10^19 a chunk of digits.
//
// A decimal of 7 digits (scale 2), one of 38 digits (scale 4) and one of 70 digits (scale 10), each formatted into a
// buffer of its own.
using System;
using System.Numerics;

using BenchmarkDotNet.Attributes;

using Vorticity.Types.Numerics;

namespace Vorticity.Benchmarks.Audit;

/// <summary>Decimals formatted as text.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public class AuditDecimalFormatBenchmarks
{
    private readonly char[] _into = new char[VortexDecimal.MaxFormattedLength];
    private VortexDecimal _value;

    /// <summary>The digits of the unscaled value.</summary>
    [Params(7, 38, 70)]
    public int Digits { get; set; }

    /// <summary>Makes the value.</summary>
    [GlobalSetup]
    public void Setup()
    {
        BigInteger unscaled = BigInteger.Parse("1234567890123456789012345678901234567890123456789012345678901234567890"[..Digits], System.Globalization.CultureInfo.InvariantCulture);
        _value = VortexDecimal.FromBigInteger(-unscaled, (byte)Math.Max(Digits, 1), (sbyte)(Digits switch { 7 => 2, 38 => 4, _ => 10 }));
    }

    /// <summary>The value formatted.</summary>
    [Benchmark]
    public int Format() => _value.TryFormat(_into, out int written) ? written : 0;
}
