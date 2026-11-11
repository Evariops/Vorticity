// What ordering two keys of an index costs, as a fence table's bisection asks it: 64-bit signed keys that
// share their high bytes (rising identifiers), and keys drawn at random.
using System;
using System.Runtime.InteropServices;

using BenchmarkDotNet.Attributes;

using Vorticity.Indexes;
using Vorticity.Types;

namespace Vorticity.Benchmarks.Audit;

/// <summary>Index keys ordered.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public class AuditKeyCompareBenchmarks
{
    private const int Pairs = 4_096;

    private readonly byte[] _keys = new byte[(Pairs + 1) * 8];
    private KeyLayout _layout;

    /// <summary>Whether the keys rise from one another, sharing their high bytes, or are drawn at random.</summary>
    [Params(true, false)]
    public bool Rising { get; set; }

    /// <summary>Draws the keys.</summary>
    [GlobalSetup]
    public void Setup()
    {
        DTypeArena types = new DTypeArena();
        if (!KeyLayout.TryOf(types.Primitive(PType.I64, Nullability.NonNullable), out _layout))
        {
            throw new InvalidOperationException("No layout.");
        }

        Random random = new Random(1026);
        Span<long> keys = MemoryMarshal.Cast<byte, long>(_keys);
        for (int i = 0; i < keys.Length; i++)
        {
            keys[i] = Rising ? 1_700_000_000_000L + random.Next(1_000) : random.NextInt64(long.MinValue, long.MaxValue);
        }
    }

    /// <summary>Neighbouring keys ordered, by pair.</summary>
    [Benchmark(OperationsPerInvoke = Pairs)]
    public int Compare()
    {
        int sum = 0;
        ReadOnlySpan<byte> keys = _keys;
        for (int i = 0; i < Pairs; i++)
        {
            sum += _layout.Compare(keys.Slice(i * 8, 8), keys.Slice((i + 1) * 8, 8));
        }

        return sum;
    }
}
