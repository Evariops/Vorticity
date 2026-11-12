// What copying a text column of few values into strings costs, the column a dictionary.
//
// 8,192 rows of 12 texts of 20 to 31 bytes, a dictionary block (codes over a block of the 12), copied into a string
// array by Column<string>.CopyTo, as a consumer that wants .NET strings does.
using System;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;

using BenchmarkDotNet.Attributes;

using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Types;

namespace Vorticity.Benchmarks.Audit;

/// <summary>A dictionary's text column copied into strings.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public class AuditTextCopyBenchmarks
{
    private const int Rows = 8_192;
    private const int Values = 12;

    private CanonicalArena _arena = null!;
    private int _node;
    private string[] _into = null!;

    /// <summary>Builds the dictionary block.</summary>
    [GlobalSetup]
    public void Setup()
    {
        const int ViewSize = 16;
        _arena = new CanonicalArena();
        byte[][] values = new byte[Values][];
        int heapBytes = 0;
        for (int v = 0; v < Values; v++)
        {
            values[v] = Encoding.UTF8.GetBytes($"service-{v:D2}-{new string((char)('a' + v), 11 + v)}");
            heapBytes += values[v].Length;
        }

        VortexBuffer views = _arena.Allocate(Values * ViewSize, ViewSize, out Span<byte> viewBytes);
        VortexBuffer heap = _arena.Allocate(heapBytes, 1, out Span<byte> heapBuffer);
        int offset = 0;
        for (int v = 0; v < Values; v++)
        {
            Span<byte> view = viewBytes.Slice(v * ViewSize, ViewSize);
            view.Clear();
            BinaryPrimitives.WriteUInt32LittleEndian(view, (uint)values[v].Length);
            values[v].AsSpan(0, 4).CopyTo(view[4..8]);
            BinaryPrimitives.WriteUInt32LittleEndian(view[12..16], (uint)offset);
            values[v].CopyTo(heapBuffer[offset..]);
            offset += values[v].Length;
        }

        DTypeArena types = new DTypeArena();
        DType utf8 = types.Utf8(Nullability.NonNullable);
        int dictionary = _arena.AddVarBinView(utf8, Values, Validity.NonNullable, views, [heap]);
        VortexBuffer codes = _arena.Allocate(Rows * sizeof(uint), sizeof(uint), out Span<byte> codeBytes);
        Span<uint> code = MemoryMarshal.Cast<byte, uint>(codeBytes);
        Random random = new Random(79);
        for (int i = 0; i < Rows; i++)
        {
            code[i] = (uint)random.Next(Values);
        }

        _node = _arena.AddDictionary(utf8, Rows, Validity.NonNullable, codes, dictionary);
        _into = new string[Rows];
    }

    /// <summary>The column copied into strings.</summary>
    [Benchmark(OperationsPerInvoke = Rows)]
    public int Copy()
    {
        Column<string> column = new Column<string>(_arena, _node, VortexType.Utf8, null);
        column.CopyTo(_into);
        return _into[Rows / 2].Length;
    }
}
