// What a distinct count over a fixed-width column pays for values built to share one hash.
//
// A batch of N rows of 64-bit integers counted distinct in one group, by a new aggregate each time
// as a query starts one. `Distinct` rows are all different, `Repeats` draw from 1,024 values so
// that most rows find theirs, and `Forged` are all different and all of the form (k << 32) | k,
// whose default hash folds the halves together into zero: a set that hashes them so keeps them in
// one chain and walks it at every insert. Run in a checkout of the original and in the tree,
// alternately.
using System;
using System.Runtime.InteropServices;

using BenchmarkDotNet.Attributes;

using Vorticity.Aggregating;
using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Types;

namespace Vorticity.Benchmarks.Complexity;

/// <summary>A batch counted distinct by a new aggregate, forged values against random ones.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public class DistinctSetFloodBenchmarks
{
    /// <summary>The batch's rows.</summary>
    [Params(16_384, 65_536)]
    public int Rows { get; set; }

    /// <summary>The rows' values.</summary>
    [Params(Column.Distinct, Column.Repeats, Column.Forged)]
    public Column Values { get; set; }

    /// <summary>A column's values.</summary>
    public enum Column
    {
        /// <summary>Every row a different random value.</summary>
        Distinct,

        /// <summary>Rows drawn from 1,024 random values.</summary>
        Repeats,

        /// <summary>Every row a different value whose halves are equal.</summary>
        Forged,
    }

    private CanonicalArena _arena = null!;
    private int _node;
    private long _batch;

    /// <summary>Builds the batch.</summary>
    [GlobalSetup]
    public void Setup()
    {
        DType dtype = new DTypeArena().Primitive(PType.I64, Nullability.NonNullable);
        _arena = new CanonicalArena();
        VortexBuffer buffer = _arena.Allocate(Rows * sizeof(long), sizeof(long), out Span<byte> bytes);
        Span<long> values = MemoryMarshal.Cast<byte, long>(bytes);
        Random random = new Random(23);
        long[] domain = new long[1_024];
        for (int i = 0; i < domain.Length; i++)
        {
            domain[i] = random.NextInt64();
        }

        for (int row = 0; row < Rows; row++)
        {
            values[row] = Values switch
            {
                Column.Distinct => random.NextInt64(),
                Column.Repeats => domain[random.Next(domain.Length)],
                _ => ((long)(row + 1) << 32) | (uint)(row + 1),
            };
        }

        _node = _arena.AddPrimitive(dtype, Rows, Validity.NonNullable, PType.I64, buffer);
        if (Count() != (Values == Column.Repeats ? domain.Length : Rows))
        {
            throw new InvalidOperationException("The batch does not hold the values it was built with.");
        }
    }

    /// <summary>Gives the arena's blocks back.</summary>
    [GlobalCleanup]
    public void Cleanup() => _arena.Reset();

    /// <summary>The batch counted distinct by the library's aggregate.</summary>
    [Benchmark]
    public long Count()
    {
        FixedDistinctSlot<long> slot = new FixedDistinctSlot<long>(StorageKind.Primitive);
        slot.EnsureGroups(1);
        BatchInput input = new BatchInput(++_batch, _arena, _node, Rows, default);
        slot.StepRange(input, 0, Rows, 0);
        return slot.Result(0);
    }
}
