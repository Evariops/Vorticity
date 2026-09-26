// The writer's bit-packing of one integer column: the transform, the patches found, the pack.
//
// Every integer column the writer compresses goes through it -- bit-packed, frame of reference,
// a dictionary's codes -- and it transformed every block into sixty-four-bit words whatever the
// element's width, then narrowed them back one at a time for the pack. The cases are the four
// widths under a frame of reference, with and without nulls, with a few values that do not fit and
// become patches.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

using BenchmarkDotNet.Attributes;

using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Types;
using Vorticity.Writing;

namespace Vorticity.Benchmarks;

/// <summary>Packing 65 536 integers at the width the writer's plan chose.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Kernel)]
public class BitPackWriteBenchmarks
{
    private const int Rows = 1 << 16;

    [Params("u8", "u16", "u32", "u64")]
    public string Type { get; set; } = "u32";

    [Params(false, true)]
    public bool Nulls { get; set; }

    private CanonicalArena _arena = new CanonicalArena();
    private int _node;
    private BitPackPlan _plan;

    public static (long Rows, long Bytes) BenchmarkWork(
        string method, IReadOnlyDictionary<string, object?> parameters)
    {
        int width = parameters.TryGetValue(nameof(Type), out object? value) && value is string t
            ? t switch { "u8" => 1, "u16" => 2, "u32" => 4, _ => 8 }
            : 8;
        return (Rows, Rows * (long)width);
    }

    [GlobalSetup]
    public void Setup()
    {
        Random random = new Random(20260926);
        DTypeArena types = new DTypeArena();
        _arena = new CanonicalArena();
        PType ptype = Type switch { "u8" => PType.U8, "u16" => PType.U16, "u32" => PType.U32, _ => PType.U64 };
        int width = ptype.ByteWidth();

        // A reference, a spread that packs at about half the width, and one value in 500 that does
        // not fit and becomes a patch.
        ulong reference = Type switch { "u8" => 20, "u16" => 1_000, "u32" => 1_000_000, _ => 1UL << 40 };
        int bits = width * 4;
        VortexBuffer values = _arena.Allocate(Rows * width, width, out Span<byte> bytes);
        for (int i = 0; i < Rows; i++)
        {
            ulong value = reference + ((ulong)random.NextInt64() & ((1UL << bits) - 1));
            if (random.Next(500) == 0)
            {
                value = reference + ((ulong)random.NextInt64() & ((1UL << Math.Min(bits * 2, 63)) - 1));
            }

            switch (width)
            {
                case 1: bytes[i] = (byte)Math.Min(value, byte.MaxValue); break;
                case 2: MemoryMarshal.Cast<byte, ushort>(bytes)[i] = (ushort)Math.Min(value, ushort.MaxValue); break;
                case 4: MemoryMarshal.Cast<byte, uint>(bytes)[i] = (uint)Math.Min(value, uint.MaxValue); break;
                default: MemoryMarshal.Cast<byte, ulong>(bytes)[i] = value; break;
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

        _node = _arena.AddPrimitive(
            types.Primitive(ptype, Nulls ? Nullability.Nullable : Nullability.NonNullable), Rows, validity, ptype, values);
        _plan = BitPackPlan.TryBuild(_arena, _arena.GetNode(_node)) ?? throw new InvalidOperationException("No bit-packing plan.");
    }

    [Benchmark(Description = "pack")]
    public int Pack() => ArrayBlobWriter.Patches(_arena, _arena.GetNode(_node), _plan).Indices.Length;
}
