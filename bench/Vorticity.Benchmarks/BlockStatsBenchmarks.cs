// The writer's ingest pass over one block of one column: the zone map's bounds and null count, the
// run boundaries, the order, the steps of a progression, the bit-width histograms and a string
// column's bytes, all from one call per range as `ColumnWriter` makes it.
//
// Every column of every chunk goes through it, so it is a fixed share of every write. The shapes are
// the ones its kernels branch on: random integers of two widths and doubles (bounds, order, runs,
// widths), a column of short runs (run boundaries), nulls (the masked kernels) and text (bytes and
// string runs).
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Runtime.InteropServices;

using BenchmarkDotNet.Attributes;

using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Types;
using Vorticity.Writing;

namespace Vorticity.Benchmarks;

/// <summary><c>BlockStatsPass.Accumulate</c> over one block of 65 536 rows.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Kernel)]
public class BlockStatsBenchmarks
{
    private const int Rows = 1 << 16;

    [Params("i64", "i32", "f64", "u16 runs", "i64 nulls", "text")]
    public string Shape { get; set; } = "i64";

    private CanonicalArena _arena = new CanonicalArena();
    private int _node;
    private int[] _widths = new int[BitPackWidths.Length];

    public static (long Rows, long Bytes) BenchmarkWork(
        string method, IReadOnlyDictionary<string, object?> parameters) => (Rows, Rows * 8L);

    [GlobalSetup]
    public void Setup()
    {
        Random random = new Random(20260926);
        DTypeArena types = new DTypeArena();
        _arena = new CanonicalArena();
        if (Shape == "text")
        {
            _node = Text(random, types);
            return;
        }

        (PType ptype, bool nulls) = Shape switch
        {
            "i32" => (PType.I32, false),
            "f64" => (PType.F64, false),
            "u16 runs" => (PType.U16, false),
            "i64 nulls" => (PType.I64, true),
            _ => (PType.I64, false),
        };

        int width = ptype.ByteWidth();
        VortexBuffer values = _arena.Allocate(Rows * width, width, out Span<byte> bytes);
        random.NextBytes(bytes);
        if (ptype == PType.F64)
        {
            Span<double> doubles = MemoryMarshal.Cast<byte, double>(bytes);
            for (int i = 0; i < Rows; i++)
            {
                doubles[i] = random.NextDouble() * 1e6;
            }
        }
        else if (ptype == PType.U16)
        {
            Span<ushort> shorts = MemoryMarshal.Cast<byte, ushort>(bytes);
            ushort value = 0;
            for (int i = 0; i < Rows; i++)
            {
                if (random.Next(8) == 0)
                {
                    value = (ushort)random.Next(1000);
                }

                shorts[i] = value;
            }
        }

        Validity validity = Validity.NonNullable;
        if (nulls)
        {
            VortexBuffer bitmap = _arena.Allocate(Rows / 8, 1, out Span<byte> valid);
            for (int i = 0; i < valid.Length; i++)
            {
                valid[i] = (byte)(random.Next(256) | random.Next(256) | random.Next(256));
            }

            validity = Validity.Bitmap(_arena.AddBool(types.Bool(Nullability.NonNullable), Rows, Validity.NonNullable, bitmap, 0));
        }

        _node = _arena.AddPrimitive(
            types.Primitive(ptype, nulls ? Nullability.Nullable : Nullability.NonNullable), Rows, validity, ptype, values);
    }

    [Benchmark(Description = "block stats")]
    public long Accumulate()
    {
        BlockStats stats = default;
        _widths.AsSpan().Clear();
        BlockStatsPass.Accumulate(_arena, _node, 0, Rows, ref stats, new PreviousRow(), _widths);
        return stats.Rows;
    }

    private int Text(Random random, DTypeArena types)
    {
        const int ViewSize = 16;
        byte[][] labels = new byte[64][];
        for (int i = 0; i < labels.Length; i++)
        {
            labels[i] = System.Text.Encoding.UTF8.GetBytes($"value-{i:D3}-{new string('x', random.Next(20))}");
        }

        int heapBytes = 0;
        int[] chosen = new int[Rows];
        for (int i = 0; i < Rows; i++)
        {
            chosen[i] = random.Next(8) == 0 ? random.Next(labels.Length) : (i == 0 ? 0 : chosen[i - 1]);
            heapBytes += labels[chosen[i]].Length > 12 ? labels[chosen[i]].Length : 0;
        }

        VortexBuffer views = _arena.Allocate(Rows * ViewSize, ViewSize, out Span<byte> viewBytes);
        VortexBuffer heap = _arena.Allocate(Math.Max(heapBytes, 1), 1, out Span<byte> heapSpan);
        int offset = 0;
        for (int i = 0; i < Rows; i++)
        {
            byte[] bytes = labels[chosen[i]];
            Span<byte> view = viewBytes.Slice(i * ViewSize, ViewSize);
            BinaryPrimitives.WriteUInt32LittleEndian(view, (uint)bytes.Length);
            if (bytes.Length <= 12)
            {
                bytes.CopyTo(view[4..]);
                continue;
            }

            bytes.AsSpan(0, 4).CopyTo(view[4..8]);
            BinaryPrimitives.WriteUInt32LittleEndian(view[12..16], (uint)offset);
            bytes.CopyTo(heapSpan[offset..]);
            offset += bytes.Length;
        }

        return _arena.AddVarBinView(types.Utf8(Nullability.NonNullable), Rows, Validity.NonNullable, views, [heap]);
    }
}
