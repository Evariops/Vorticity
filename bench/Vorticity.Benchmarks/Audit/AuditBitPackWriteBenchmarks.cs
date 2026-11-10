// Audit VW-07: what bit-packing a chunk of integers costs, each block of 1,024 rows transformed, its nulls cleared, its
// patches found and packed, under each instruction set (run with DOTNET_EnableAVX512=0 for AVX2). 65,536 uints of
// sizes around a reference, one in 512 too wide for the width, every one valid or one in sixteen null.
using System;
using System.Runtime.InteropServices;

using BenchmarkDotNet.Attributes;

using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Types;
using Vorticity.Writing;

namespace Vorticity.Benchmarks.Audit;

/// <summary>A chunk of integers bit-packed.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public class AuditBitPackWriteBenchmarks
{
    private const int Rows = 65_536;

    private CanonicalArena _arena = null!;
    private int _node;
    private BitPackPlan _plan;
    private byte[] _packed = [];
    private int[] _indices = [];
    private ulong[] _values = [];

    /// <summary>Whether one row in sixteen is null.</summary>
    [Params(false, true)]
    public bool Nulls { get; set; }

    /// <summary>Draws the chunk and plans it.</summary>
    [GlobalSetup]
    public void Setup()
    {
        Random random = new Random(71);
        DTypeArena types = new DTypeArena();
        _arena = new CanonicalArena();
        VortexBuffer buffer = _arena.Allocate(Rows * sizeof(uint), sizeof(uint), out Span<byte> bytes);
        Span<uint> values = MemoryMarshal.Cast<byte, uint>(bytes);
        for (int i = 0; i < Rows; i++)
        {
            values[i] = 1_000_000u + (uint)random.Next(4_096) + (i % 512 == 0 ? 1u << 20 : 0);
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

        _node = _arena.AddPrimitive(types.Primitive(PType.U32, Nulls ? Nullability.Nullable : Nullability.NonNullable), Rows, validity, PType.U32, buffer);
        _plan = BitPackPlan.TryBuild(_arena, _arena.GetNode(_node)) ?? throw new InvalidOperationException("The chunk does not pack.");
        _packed = new byte[ArrayBlobWriter.PackedBytes(Rows, _plan.BitWidth)];
        _indices = new int[_plan.Exceptions];
        _values = new ulong[_plan.Exceptions];
    }

    /// <summary>The chunk planned: its transform, reference and width; the width.</summary>
    [Benchmark]
    public int Plan() => BitPackPlan.TryBuild(_arena, _arena.GetNode(_node))?.BitWidth ?? -1;

    /// <summary>The chunk packed; its first byte.</summary>
    [Benchmark]
    public byte Pack()
    {
        ArrayBlobWriter.Pack(_arena, _arena.GetNode(_node), in _plan, PType.U32, Rows, _packed, _indices, _values);
        return _packed[0];
    }
}
