// The ALP-RD candidate for a float column ALP refuses: a cut chosen from a sample, then every row
// split in two, its high part coded against a dictionary of eight patterns at most and its low part
// kept, the rows no pattern holds recorded as exceptions.
//
// 65 536 values of full precision in a range of a few binades, the kind ALP cannot scale to
// integers, with and without a quarter of them null.
using System;
using System.Collections.Generic;

using BenchmarkDotNet.Attributes;

using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Types;
using Vorticity.Writing;

namespace Vorticity.Benchmarks;

/// <summary><c>AlpRdPlan.TryBuild</c> over 65 536 floats.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Kernel)]
public class AlpRdSplitBenchmarks
{
    private const int Rows = 1 << 16;

    [Params("f64", "f32")]
    public string Type { get; set; } = "f64";

    [Params(false, true)]
    public bool Nulls { get; set; }

    private readonly CanonicalArena _arena = new CanonicalArena();
    private int _node;
    private long _plain;

    /// <summary>What one invocation reads: every row's value.</summary>
    public static (long Rows, long Bytes) BenchmarkWork(
        string method, IReadOnlyDictionary<string, object?> parameters) =>
        (Rows, Rows * (parameters.TryGetValue(nameof(Type), out object? t) && t is "f32" ? 4L : 8L));

    [GlobalSetup]
    public void Setup()
    {
        Random random = new Random(20260927);
        DTypeArena types = new DTypeArena();
        PType ptype = Type == "f32" ? PType.F32 : PType.F64;
        int width = ptype.ByteWidth();
        VortexBuffer values = _arena.Allocate(Rows * width, width, out Span<byte> bytes);
        for (int i = 0; i < Rows; i++)
        {
            double value = 10 + (random.NextDouble() * 90);
            if (width == 4)
            {
                BitConverter.TryWriteBytes(bytes.Slice(i * 4, 4), (float)value);
            }
            else
            {
                BitConverter.TryWriteBytes(bytes.Slice(i * 8, 8), value);
            }
        }

        Validity validity = Validity.NonNullable;
        if (Nulls)
        {
            VortexBuffer bits = _arena.Allocate(Rows / 8, 1, out Span<byte> valid);
            for (int i = 0; i < valid.Length; i++)
            {
                valid[i] = (byte)(random.Next(256) | random.Next(256));
            }

            validity = Validity.Bitmap(_arena.AddBool(types.Bool(Nullability.NonNullable), Rows, Validity.NonNullable, bits, 0));
        }

        _node = _arena.AddPrimitive(
            types.Primitive(ptype, Nulls ? Nullability.Nullable : Nullability.NonNullable), Rows, validity, ptype, values);
        _plain = (long)Rows * width;
    }

    [Benchmark(Description = "alp-rd plan")]
    public long Plan()
    {
        AlpRdPlan? plan = AlpRdPlan.TryBuild(_arena, _node, _plain);
        long size = plan?.EncodedSize ?? -1;
        plan?.Release();
        return size;
    }
}
