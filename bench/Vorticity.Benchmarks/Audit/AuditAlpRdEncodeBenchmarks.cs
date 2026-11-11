// What ALP-RD's encoding of a chunk of doubles costs, each value cut into the left part a small dictionary
// codes and the right part kept, under each instruction set (run with DOTNET_EnableAVX512=0 for AVX2). 65,536 doubles
// of a sensor, a few bits of noise on a slow wave, every one valid or one in sixteen null.
using System;
using System.Runtime.InteropServices;

using BenchmarkDotNet.Attributes;

using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Types;
using Vorticity.Writing;

namespace Vorticity.Benchmarks.Audit;

/// <summary>A chunk of doubles cut by ALP-RD.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public class AuditAlpRdEncodeBenchmarks
{
    private const int Rows = 65_536;

    private CanonicalArena _arena = null!;
    private int _node;

    /// <summary>Whether one row in sixteen is null.</summary>
    [Params(false, true)]
    public bool Nulls { get; set; }

    /// <summary>Draws the chunk.</summary>
    [GlobalSetup]
    public void Setup()
    {
        Random random = new Random(61);
        DTypeArena types = new DTypeArena();
        _arena = new CanonicalArena();
        VortexBuffer buffer = _arena.Allocate(Rows * sizeof(double), sizeof(double), out Span<byte> bytes);
        Span<double> values = MemoryMarshal.Cast<byte, double>(bytes);
        for (int i = 0; i < Rows; i++)
        {
            values[i] = (Math.Sin(i / 500.0) * 40.0) + 20.0 + (random.NextDouble() * 1e-3);
        }

        Validity validity = Validity.NonNullable;
        if (Nulls)
        {
            VortexBuffer bits = _arena.Allocate(Rows / 8, 8, out Span<byte> valid);
            valid.Fill(0xFF);
            for (int i = 0; i < Rows; i += 16)
            {
                valid[i / 8] &= (byte)~(1 << (i % 8));
            }

            validity = Validity.Bitmap(_arena.AddBool(types.Bool(Nullability.NonNullable), Rows, Validity.NonNullable, bits, 0));
        }

        _node = _arena.AddPrimitive(types.Primitive(PType.F64, Nulls ? Nullability.Nullable : Nullability.NonNullable), Rows, validity, PType.F64, buffer);
        if (Encode() == 0)
        {
            throw new InvalidOperationException("ALP-RD took nothing.");
        }
    }

    /// <summary>The chunk cut, its rentals handed back as the writer hands them; its codes and exceptions.</summary>
    [Benchmark]
    public long Encode()
    {
        if (AlpRdPlan.TryBuild(_arena, _node, Rows * sizeof(double)) is not { } plan)
        {
            return 0;
        }

        long held = plan.Codes.Length + plan.ExceptionRows.Length;
        plan.Release();
        return held;
    }
}
