// The writer's ALP candidate for one float column: the exponent search over a sample, the encode of
// every row with its patches, and the size estimate -- what every float column of every chunk pays
// whether ALP wins or not.
//
// The values are decimals of two places, the column ALP is for, with one in a hundred made
// irrational so the patches have work, in doubles and in singles, with and without nulls.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

using BenchmarkDotNet.Attributes;

using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Types;
using Vorticity.Writing;

namespace Vorticity.Benchmarks;

/// <summary><c>AlpPlan.TryBuild</c> over 65 536 floats.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Kernel)]
public class AlpEncodeBenchmarks
{
    private const int Rows = 1 << 16;

    [Params("f64", "f32")]
    public string Type { get; set; } = "f64";

    [Params(false, true)]
    public bool Nulls { get; set; }

    private CanonicalArena _arena = new CanonicalArena();
    private int _node;
    private long _plain;

    public static (long Rows, long Bytes) BenchmarkWork(
        string method, IReadOnlyDictionary<string, object?> parameters)
    {
        int width = parameters.TryGetValue(nameof(Type), out object? value) && value is "f32" ? 4 : 8;
        return (Rows, Rows * (long)width);
    }

    [GlobalSetup]
    public void Setup()
    {
        Random random = new Random(20260926);
        DTypeArena types = new DTypeArena();
        _arena = new CanonicalArena();
        bool single = Type == "f32";
        PType ptype = single ? PType.F32 : PType.F64;
        int width = ptype.ByteWidth();
        VortexBuffer values = _arena.Allocate(Rows * width, width, out Span<byte> bytes);
        for (int i = 0; i < Rows; i++)
        {
            double value = random.Next(100) == 0 ? random.NextDouble() * Math.PI : random.Next(1_000_000) / 100.0;
            if (single)
            {
                MemoryMarshal.Cast<byte, float>(bytes)[i] = (float)value;
            }
            else
            {
                MemoryMarshal.Cast<byte, double>(bytes)[i] = value;
            }
        }

        Validity validity = Validity.NonNullable;
        if (Nulls)
        {
            VortexBuffer bitmap = _arena.Allocate(Rows / 8, 1, out Span<byte> valid);
            for (int i = 0; i < valid.Length; i++)
            {
                valid[i] = (byte)(random.Next(256) | random.Next(256) | random.Next(256));
            }

            validity = Validity.Bitmap(_arena.AddBool(types.Bool(Nullability.NonNullable), Rows, Validity.NonNullable, bitmap, 0));
        }

        _node = _arena.AddPrimitive(types.Primitive(ptype, Nulls ? Nullability.Nullable : Nullability.NonNullable), Rows, validity, ptype, values);
        _plain = (long)Rows * width;
    }

    [Benchmark(Description = "alp plan")]
    public int Build()
    {
        AlpPlan? plan = AlpPlan.TryBuild(_arena, _node, _plain);
        int patches = plan?.PatchCount ?? -1;
        plan?.Release();
        return patches;
    }
}
