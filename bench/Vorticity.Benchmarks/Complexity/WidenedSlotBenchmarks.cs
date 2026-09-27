// What a sum pays for the number of groups a batch is folded in.
//
// One batch of 65,536 values summed over R ranges of one group each, as a grouping by a sorted or
// run-end key folds a batch: a range per key. Decimals stored in 64 bits are read as 128-bit
// values, so the aggregate widens them; decimals stored in 128 bits and 64-bit integers are read
// where they are. `Original` is the aggregate that read the block again at every range, widening
// it each time (`FixedSlotBefore.cs`); `Library` is the library's, which widens it once per batch.
using System;
using System.Runtime.InteropServices;

using BenchmarkDotNet.Attributes;

using Vorticity.Aggregating;
using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Types;
using Vorticity.Types.Numerics;

namespace Vorticity.Benchmarks.Complexity;

/// <summary>A narrow decimal summed over one batch, against the ranges it is folded in.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public class WidenedSlotBenchmarks
{
    /// <summary>The ranges the batch is folded in, one group each.</summary>
    [Params(1, 16, 256, 4_096)]
    public int Ranges { get; set; }

    /// <summary>The column summed.</summary>
    [Params(Summed.Decimal64, Summed.Decimal128, Summed.Int64)]
    public Summed Column { get; set; }

    /// <summary>A column's storage.</summary>
    public enum Summed
    {
        /// <summary>Decimals stored in 64 bits, which the sum widens.</summary>
        Decimal64,

        /// <summary>Decimals stored in 128 bits, which the sum reads in place.</summary>
        Decimal128,

        /// <summary>64-bit integers, which the sum reads in place.</summary>
        Int64,
    }

    private const int Rows = 65_536;

    private CanonicalArena _arena = null!;
    private int _node;
    private long _batch;
    private AggregateSlot<Int128> _original = null!;
    private AggregateSlot<Int128> _library = null!;

    /// <summary>Builds the batch and both aggregates.</summary>
    [GlobalSetup]
    public void Setup()
    {
        DTypeArena types = new DTypeArena();
        _arena = new CanonicalArena();
        switch (Column)
        {
            case Summed.Decimal64:
            {
                VortexBuffer buffer = _arena.Allocate(Rows * sizeof(long), sizeof(long), out Span<byte> bytes);
                Fill(MemoryMarshal.Cast<byte, long>(bytes));
                DType dtype = types.Decimal(18, 2, Nullability.NonNullable);
                _node = _arena.AddDecimal(dtype, Rows, Validity.NonNullable, DecimalStorageType.I64, 18, 2, buffer);
                _original = new FixedSlotBefore<Int128, SumState<Int128>, DecimalSum, Int128>(StorageKind.Decimal, static s => s.Sum);
                _library = new FixedSlot<Int128, SumState<Int128>, DecimalSum, Int128>(StorageKind.Decimal, static s => s.Sum);
                break;
            }

            case Summed.Decimal128:
            {
                VortexBuffer buffer = _arena.Allocate(Rows * 16, 16, out Span<byte> bytes);
                Span<Int128> values = MemoryMarshal.Cast<byte, Int128>(bytes);
                for (int i = 0; i < Rows; i++)
                {
                    values[i] = (i * 37L) - 1_000_000;
                }

                DType dtype = types.Decimal(38, 2, Nullability.NonNullable);
                _node = _arena.AddDecimal(dtype, Rows, Validity.NonNullable, DecimalStorageType.I128, 38, 2, buffer);
                _original = new FixedSlotBefore<Int128, SumState<Int128>, DecimalSum, Int128>(StorageKind.Decimal, static s => s.Sum);
                _library = new FixedSlot<Int128, SumState<Int128>, DecimalSum, Int128>(StorageKind.Decimal, static s => s.Sum);
                break;
            }

            default:
            {
                VortexBuffer buffer = _arena.Allocate(Rows * sizeof(long), sizeof(long), out Span<byte> bytes);
                Fill(MemoryMarshal.Cast<byte, long>(bytes));
                DType dtype = types.Primitive(PType.I64, Nullability.NonNullable);
                _node = _arena.AddPrimitive(dtype, Rows, Validity.NonNullable, PType.I64, buffer);
                _original = new FixedSlotBefore<long, SumState<Int128>, SignedSum<long>, Int128>(StorageKind.Primitive, static s => s.Sum);
                _library = new FixedSlot<long, SumState<Int128>, SignedSum<long>, Int128>(StorageKind.Primitive, static s => s.Sum);
                break;
            }
        }

        _original.EnsureGroups(Ranges);
        _library.EnsureGroups(Ranges);
    }

    /// <summary>Gives the arena's blocks back.</summary>
    [GlobalCleanup]
    public void Cleanup() => _arena.Reset();

    /// <summary>The batch folded range by range, widened at every range.</summary>
    [Benchmark(Baseline = true)]
    public Int128 Original() => Fold(_original);

    /// <summary>The batch folded range by range by the library's aggregate.</summary>
    [Benchmark]
    public Int128 Library() => Fold(_library);

    private static void Fill(Span<long> values)
    {
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = (i * 37L) - 1_000_000;
        }
    }

    private Int128 Fold(AggregateSlot<Int128> slot)
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
