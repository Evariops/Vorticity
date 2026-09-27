// What a distinct count over a fixed-width dictionary pays for the dictionary's size at every range
// a batch is folded in.
//
// One batch of 65,536 rows whose column is a dictionary of V integers, counted distinct per group
// over R ranges, as a grouping by a sorted or run-end key folds the batch. Every value of every
// range has been seen before, as in a batch after the first. `Original` is the aggregate that
// cleared and swept a table of the dictionary's size at every range (`FixedDistinctSlotBefore.cs`);
// `Library` is the library's, which pays for the range's rows.
using System;
using System.Runtime.InteropServices;

using BenchmarkDotNet.Attributes;

using Vorticity.Aggregating;
using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Types;

namespace Vorticity.Benchmarks.Complexity;

/// <summary>One dictionary batch counted distinct range by range, against the dictionary's size.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public class DictionaryDistinctBenchmarks
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
    private AggregateSlot<long> _original = null!;
    private AggregateSlot<long> _library = null!;

    /// <summary>Builds the batch and counts it once with both aggregates.</summary>
    [GlobalSetup]
    public void Setup()
    {
        DTypeArena types = new DTypeArena();
        DType dtype = types.Primitive(PType.I64, Nullability.NonNullable);
        _arena = new CanonicalArena();
        VortexBuffer buffer = _arena.Allocate(Values * sizeof(long), sizeof(long), out Span<byte> bytes);
        Span<long> value = MemoryMarshal.Cast<byte, long>(bytes);
        for (int i = 0; i < Values; i++)
        {
            value[i] = i * 7L;
        }

        int values = _arena.AddPrimitive(dtype, Values, Validity.NonNullable, PType.I64, buffer);
        VortexBuffer codes = _arena.Allocate(Rows * sizeof(uint), sizeof(uint), out Span<byte> codeBytes);
        Span<uint> code = MemoryMarshal.Cast<byte, uint>(codeBytes);
        Random random = new Random(18);
        for (int row = 0; row < Rows; row++)
        {
            code[row] = (uint)random.Next(Values);
        }

        _node = _arena.AddDictionary(dtype, Rows, Validity.NonNullable, codes, values);
        _original = new FixedDistinctSlotBefore<long>(StorageKind.Primitive);
        _library = new FixedDistinctSlot<long>(StorageKind.Primitive);
        _original.EnsureGroups(Ranges);
        _library.EnsureGroups(Ranges);
        Original();
        Library();
    }

    /// <summary>Gives the arena's blocks back.</summary>
    [GlobalCleanup]
    public void Cleanup() => _arena.Reset();

    /// <summary>The batch counted range by range, a table of the dictionary cleared and swept at each.</summary>
    [Benchmark(Baseline = true)]
    public long Original() => Fold(_original);

    /// <summary>The batch counted range by range by the library's aggregate.</summary>
    [Benchmark]
    public long Library() => Fold(_library);

    private long Fold(AggregateSlot<long> slot)
    {
        BatchInput input = new BatchInput(++_batch, _arena, _node, Rows, default);
        int step = Rows / Ranges;
        for (int r = 0; r < Ranges; r++)
        {
            slot.StepRange(input, r * step, (r + 1) * step, r);
        }

        return slot.Result(Ranges - 1);
    }
}
