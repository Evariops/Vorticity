// What a consumer reading a text column row by row pays a value through the column's indexer.
//
// 65,536 texts of 8 to 40 bytes in a canonical view block (a third inline in their view, the rest on the heap); each
// read through Column<ReadOnlyMemory<byte>>[i], its length summed, against the views read with the block's node held.
using System;
using System.Buffers.Binary;
using System.Text;

using BenchmarkDotNet.Attributes;

using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Types;

namespace Vorticity.Benchmarks.Audit;

/// <summary>A text column read row by row.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public class AuditTextColumnBenchmarks
{
    private const int Rows = 65_536;

    private CanonicalArena _arena = null!;
    private int _node;
    private VortexType _type = null!;

    /// <summary>Builds the block.</summary>
    [GlobalSetup]
    public void Setup()
    {
        const int ViewSize = 16;
        Random random = new Random(73);
        byte[][] values = new byte[Rows][];
        int heapBytes = 0;
        for (int i = 0; i < Rows; i++)
        {
            values[i] = Encoding.UTF8.GetBytes(new string((char)('a' + (i % 26)), 8 + random.Next(33)));
            heapBytes += values[i].Length > 12 ? values[i].Length : 0;
        }

        _arena = new CanonicalArena();
        VortexBuffer views = _arena.Allocate(Rows * ViewSize, ViewSize, out Span<byte> viewBytes);
        VortexBuffer heap = _arena.Allocate(heapBytes, 1, out Span<byte> heapBuffer);
        int offset = 0;
        for (int i = 0; i < Rows; i++)
        {
            byte[] bytes = values[i];
            Span<byte> view = viewBytes.Slice(i * ViewSize, ViewSize);
            view.Clear();
            BinaryPrimitives.WriteUInt32LittleEndian(view, (uint)bytes.Length);
            if (bytes.Length <= 12)
            {
                bytes.CopyTo(view[4..]);
                continue;
            }

            bytes.AsSpan(0, 4).CopyTo(view[4..8]);
            BinaryPrimitives.WriteUInt32LittleEndian(view[12..16], (uint)offset);
            bytes.CopyTo(heapBuffer[offset..]);
            offset += bytes.Length;
        }

        DType dtype = new DTypeArena().Binary(Nullability.NonNullable);
        _node = _arena.AddVarBinView(dtype, Rows, Validity.NonNullable, views, [heap]);
        _type = VortexType.Binary;
    }

    /// <summary>Each value through the column's indexer.</summary>
    [Benchmark(Baseline = true, OperationsPerInvoke = Rows)]
    public long Indexer()
    {
        Column<ReadOnlyMemory<byte>> column = new Column<ReadOnlyMemory<byte>>(_arena, _node, _type, null);
        long length = 0;
        for (int i = 0; i < column.Length; i++)
        {
            length += column[i].Length;
        }

        return length;
    }

    /// <summary>Each value from the views, the block's node held.</summary>
    [Benchmark(OperationsPerInvoke = Rows)]
    public long Held()
    {
        CanonicalNode node = _arena.GetNode(_node);
        long length = 0;
        for (int i = 0; i < Rows; i++)
        {
            length += ViewValues.At(node, i).Length;
        }

        return length;
    }
}
