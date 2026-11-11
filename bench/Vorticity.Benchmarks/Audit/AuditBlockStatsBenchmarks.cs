// What a writer's statistics of a block cost a value, on the vector widths a machine has.
//
// A block of 8,192 values, 64-bit integers or doubles drawn at random: its least and greatest. Run as it is (AVX-512 here),
// and under DOTNET_EnableAVX512=0 (AVX2: the 128-bit tier, the kernels having no 256-bit one) to see what AVX2 and NEON
// machines pay.
using System;

using BenchmarkDotNet.Attributes;

using Vorticity.Writing;

namespace Vorticity.Benchmarks.Audit;

/// <summary>A block's bounds, per value.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public class AuditBlockStatsBenchmarks
{
    private const int Values = 8_192;

    private readonly long[] _longs = new long[Values];
    private readonly int[] _ints = new int[Values];
    private readonly int[] _widths = new int[2 * 65];
    private readonly Arrays.CanonicalArena _arena = new Arrays.CanonicalArena();
    private int _nullable;

    /// <summary>Draws the values.</summary>
    [GlobalSetup]
    public void Setup()
    {
        Random random = new Random(17);
        for (int i = 0; i < Values; i++)
        {
            _longs[i] = random.NextInt64();
            _ints[i] = random.Next();
        }

        // A column of 8,192 nullable ints, one in ten null.
        Buffers.VortexBuffer buffer = _arena.Allocate(Values * 4, 4, out Span<byte> bytes);
        Span<int> values = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, int>(bytes);
        Buffers.VortexBuffer bits = _arena.Allocate(Values / 8, 8, out Span<byte> raw);
        raw.Clear();
        for (int i = 0; i < Values; i++)
        {
            values[i] = random.Next(1 << 20);
            if (random.Next(10) != 0)
            {
                raw[i >> 3] |= (byte)(1 << (i & 7));
            }
        }

        Types.DTypeArena types = new Types.DTypeArena();
        int mask = _arena.AddBool(types.Bool(Types.Nullability.NonNullable), Values, Arrays.Validity.NonNullable, bits, 0);
        _nullable = _arena.AddPrimitive(types.Primitive(Types.PType.I32, Types.Nullability.Nullable), Values, Arrays.Validity.Bitmap(mask), Types.PType.I32, buffer);
    }

    /// <summary>The bit widths of a block of 8,192 nullable ints, its nulls counted.</summary>
    [Benchmark(OperationsPerInvoke = Values)]
    public int NullableWidths()
    {
        Array.Clear(_widths);
        BlockStatsPass.Widths(_arena, _nullable, 0, Values, _widths);
        return _widths[0];
    }

    /// <summary>The bounds of 8,192 longs.</summary>
    [Benchmark(OperationsPerInvoke = Values)]
    public long Longs()
    {
        BlockStatsPass.Bounds<long>(_longs, out long min, out long max);
        return min ^ max;
    }

    /// <summary>The bounds of 8,192 ints.</summary>
    [Benchmark(OperationsPerInvoke = Values)]
    public int Ints()
    {
        BlockStatsPass.Bounds<int>(_ints, out int min, out int max);
        return min ^ max;
    }
}
