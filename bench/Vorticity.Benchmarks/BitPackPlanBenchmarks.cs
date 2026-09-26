// The bit-pack candidate priced for an integer column whose least value is not zero: every row's
// width counted relative to that value, a frame of reference, which the ingest pass cannot count
// because it learns the least value only once the chunk is whole.
//
// 65 536 values around a large base, with and without a quarter of them null, signed so that the
// zigzag widths are counted beside the framed ones.
using System;
using System.Collections.Generic;

using BenchmarkDotNet.Attributes;

using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Types;
using Vorticity.Writing;

namespace Vorticity.Benchmarks;

/// <summary><c>BitPackPlan.TryBuild</c> of a framed integer column.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Kernel)]
public class BitPackPlanBenchmarks
{
    private const int Rows = 1 << 16;

    [Params("i64", "i32")]
    public string Type { get; set; } = "i64";

    [Params(false, true)]
    public bool Nulls { get; set; }

    private readonly CanonicalArena _arena = new CanonicalArena();
    private int _node;

    /// <summary>What one invocation reads: every row's value.</summary>
    public static (long Rows, long Bytes) BenchmarkWork(
        string method, IReadOnlyDictionary<string, object?> parameters) =>
        (Rows, Rows * (parameters.TryGetValue(nameof(Type), out object? t) && t is "i32" ? 4L : 8L));

    [GlobalSetup]
    public void Setup()
    {
        Random random = new Random(20260927);
        DTypeArena types = new DTypeArena();
        PType ptype = Type == "i32" ? PType.I32 : PType.I64;
        int width = ptype.ByteWidth();
        VortexBuffer values = _arena.Allocate(Rows * width, width, out Span<byte> bytes);
        for (int i = 0; i < Rows; i++)
        {
            long value = 1_000_000 + random.Next(5_000);
            if (width == 4)
            {
                BitConverter.TryWriteBytes(bytes.Slice(i * 4, 4), (int)value);
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
    }

    [Benchmark(Description = "bitpack plan")]
    public long Plan() => BitPackPlan.TryBuild(_arena, _arena.GetNode(_node))?.BitWidth ?? -1;
}
