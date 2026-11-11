// What Pco's encoding of a chunk of integers costs where the values share a divisor, each latent split into
// its multiple of the divisor and what is left: 65,536 instants in microseconds taken at milliseconds, rising with jitter.
using System;
using System.Runtime.InteropServices;

using BenchmarkDotNet.Attributes;

using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Types;
using Vorticity.Writing;

namespace Vorticity.Benchmarks.Audit;

/// <summary>A chunk of instants encoded by Pco.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public class AuditPcoEncodeBenchmarks
{
    private const int Rows = 65_536;

    private CanonicalArena _arena = null!;
    private int _node;

    /// <summary>Draws the chunk.</summary>
    [GlobalSetup]
    public void Setup()
    {
        Random random = new Random(83);
        _arena = new CanonicalArena();
        VortexBuffer buffer = _arena.Allocate(Rows * sizeof(long), sizeof(long), out Span<byte> bytes);
        Span<long> values = MemoryMarshal.Cast<byte, long>(bytes);
        long at = 1_762_000_000_000L;
        for (int i = 0; i < Rows; i++)
        {
            at += random.Next(0, 40);
            values[i] = (at + random.Next(-500, 500)) * 1_000;
        }

        _node = _arena.AddPrimitive(new DTypeArena().Primitive(PType.I64, Nullability.NonNullable), Rows, Validity.NonNullable, PType.I64, buffer);
        if (Encode() == 0)
        {
            throw new InvalidOperationException("Pco took nothing.");
        }
    }

    /// <summary>The chunk encoded, its rentals handed back; its bytes.</summary>
    [Benchmark]
    public long Encode()
    {
        if (PcoPlan.TryBuild(_arena, _node, long.MaxValue) is not { } plan)
        {
            return 0;
        }

        long held = plan.EncodedSize;
        plan.Release();
        return held;
    }
}
