// What a text aggregate pays for a dictionary's size at every range a batch is folded in.
//
// One batch of 65,536 rows whose column is a dictionary of V strings, walked range by range as a
// distinct count or an extreme walks it when a grouping by a sorted or run-end key folds the batch
// in R ranges. The sink only adds up the lengths it is handed, so what is measured is the walk.
// `Original` is the walk that cleared and swept a table of the dictionary's size at every range
// (`BytesWalkBefore.cs`); `Library` is the library's, which pays for the range's rows.
using System;
using System.Buffers.Binary;
using System.Runtime.InteropServices;

using BenchmarkDotNet.Attributes;

using Vorticity.Aggregating;
using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Types;

namespace Vorticity.Benchmarks.Complexity;

/// <summary>One dictionary batch walked range by range, against the dictionary's size.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public class DictionaryRangeBenchmarks
{
    /// <summary>The dictionary's values.</summary>
    [Params(1_024, 65_536)]
    public int Values { get; set; }

    /// <summary>The ranges the batch is folded in, one group each.</summary>
    [Params(1, 16, 256, 4_096)]
    public int Ranges { get; set; }

    private const int Rows = 65_536;

    private CanonicalArena _arena = null!;
    private int _node;
    private long _batch;
    private MaskCache _originalRows;
    private MaskCache _libraryRows;
    private bool[] _present = [];
    private CodeSet _distinct;

    /// <summary>Builds the batch.</summary>
    [GlobalSetup]
    public void Setup()
    {
        DTypeArena types = new DTypeArena();
        DType dtype = types.Utf8(Nullability.NonNullable);
        _arena = new CanonicalArena();
        VortexBuffer heap = _arena.Allocate(Values * 16, 1, out Span<byte> heapBytes);
        VortexBuffer views = _arena.Allocate(Values * 16, 16, out Span<byte> viewBytes);
        for (int i = 0; i < Values; i++)
        {
            Span<byte> value = heapBytes.Slice(i * 16, 16);
            "value-"u8.CopyTo(value);
            i.TryFormat(value[6..], out int digits);
            int length = 6 + digits;
            Span<byte> view = viewBytes.Slice(i * 16, 16);
            view.Clear();
            BinaryPrimitives.WriteInt32LittleEndian(view, length);
            value[..length].CopyTo(view[4..]);
        }

        int values = _arena.AddVarBinView(dtype, Values, Validity.NonNullable, views, [heap]);
        VortexBuffer codes = _arena.Allocate(Rows * sizeof(uint), sizeof(uint), out Span<byte> codeBytes);
        Span<uint> code = MemoryMarshal.Cast<byte, uint>(codeBytes);
        Random random = new Random(18);
        for (int row = 0; row < Rows; row++)
        {
            code[row] = (uint)random.Next(Values);
        }

        _node = _arena.AddDictionary(dtype, Rows, Validity.NonNullable, codes, values);
    }

    /// <summary>Gives the arena's blocks back.</summary>
    [GlobalCleanup]
    public void Cleanup() => _arena.Reset();

    /// <summary>The batch walked range by range, a table of the dictionary cleared and swept at each.</summary>
    [Benchmark(Baseline = true)]
    public long Original()
    {
        BatchInput input = new BatchInput(++_batch, _arena, _node, Rows, default);
        LengthSink sink = default;
        int step = Rows / Ranges;
        for (int r = 0; r < Ranges; r++)
        {
            BytesWalkBefore.Range(ref sink, input, r * step, (r + 1) * step, r, ref _originalRows, ref _present);
        }

        return sink.Total;
    }

    /// <summary>The batch walked range by range by the library.</summary>
    [Benchmark]
    public long Library()
    {
        BatchInput input = new BatchInput(++_batch, _arena, _node, Rows, default);
        LengthSink sink = default;
        int step = Rows / Ranges;
        for (int r = 0; r < Ranges; r++)
        {
            BytesWalk.Range(ref sink, input, r * step, (r + 1) * step, r, ref _libraryRows, ref _distinct);
        }

        return sink.Total;
    }

    private struct LengthSink : IBytesSink
    {
        internal long Total;

        public void Take(int group, ReadOnlySpan<byte> value) => Total += value.Length + group;
    }
}
