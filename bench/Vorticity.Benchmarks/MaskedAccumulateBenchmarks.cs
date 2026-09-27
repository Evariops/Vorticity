// A fixed-width aggregate over a filtered or nullable column folds the rows its mask holds, and a
// word of that mask neither empty nor full was folded a row at a time.
//
// The ported arm is the loop as it was: a count of trailing zeros and an add a row kept. The
// shipped arm is `FixedSlot.Accumulate`, which selects a run of dense words whole -- the rows left
// out read as a value that cannot move the aggregate -- and folds the run with the dense kernel. Both
// run in one process, so tiered PGO is best turned off (`DOTNET_TieredPGO=0`).
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;

using BenchmarkDotNet.Attributes;

using Vorticity.Aggregating;

namespace Vorticity.Benchmarks;

/// <summary>An aggregate of 65 536 rows under a mask: ported loop against the shipped fold.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Kernel)]
public class MaskedAccumulateBenchmarks
{
    private const int Rows = 1 << 16;

    /// <summary>The aggregate and its column's type.</summary>
    [Params("sum i32", "sum f64", "min i64")]
    public string Op { get; set; } = "sum i32";

    /// <summary>Rows kept out of 64, on average.</summary>
    [Params(8, 32, 56, 64)]
    public int Kept { get; set; }

    private int[] _ints = [];
    private double[] _doubles = [];
    private long[] _longs = [];
    private ulong[] _mask = [];

    /// <summary>What one invocation reads: a value per row.</summary>
    public static (long Rows, long Bytes) BenchmarkWork(
        string method, IReadOnlyDictionary<string, object?> parameters) =>
        (Rows, Rows * (parameters.TryGetValue(nameof(Op), out object? o) && o is "sum i32" ? 4L : 8L));

    [GlobalSetup]
    public void Setup()
    {
        Random random = new Random(20260927);
        _ints = new int[Rows];
        _doubles = new double[Rows];
        _longs = new long[Rows];
        _mask = new ulong[Rows / 64];
        for (int i = 0; i < Rows; i++)
        {
            _ints[i] = random.Next();
            _doubles[i] = random.NextDouble() * 1000;
            _longs[i] = random.NextInt64();
            if (random.Next(64) < Kept)
            {
                _mask[i >> 6] |= 1UL << (i & 63);
            }
        }
    }

    [Benchmark(Baseline = true, Description = "fold, ported")]
    public double Ported() => Op switch
    {
        "sum i32" => (double)Old<int, SumState<Int128>, SignedSum<int>>(_ints).Sum,
        "sum f64" => Old<double, SumState<double>, FloatSum<double>>(_doubles).Sum,
        _ => Old<long, ExtremeState<long>, MinOp<long>>(_longs).Value,
    };

    [Benchmark(Description = "fold, shipped")]
    public double Shipped() => Op switch
    {
        "sum i32" => (double)New<int, SumState<Int128>, SignedSum<int>>(_ints).Sum,
        "sum f64" => New<double, SumState<double>, FloatSum<double>>(_doubles).Sum,
        _ => New<long, ExtremeState<long>, MinOp<long>>(_longs).Value,
    };

    private TState New<TValue, TState, TOp>(TValue[] values)
        where TValue : unmanaged
        where TOp : IValueOp<TValue, TState>
    {
        TState state = TOp.Seed();
        FixedSlot<TValue, TState, TOp, object>.Accumulate(ref state, values, _mask, 0, Rows);
        return state;
    }

    private TState Old<TValue, TState, TOp>(TValue[] values)
        where TValue : unmanaged
        where TOp : IValueOp<TValue, TState>
    {
        TState state = TOp.Seed();
        Ported<TValue, TState, TOp>(ref state, values, _mask, 0, Rows);
        return state;
    }

    /// <summary>`FixedSlot.Accumulate` as it was, folding into the group's state through a reference.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Ported<TValue, TState, TOp>(ref TState state, ReadOnlySpan<TValue> values, ReadOnlySpan<ulong> rows, int start, int end)
        where TValue : unmanaged
        where TOp : IValueOp<TValue, TState>
    {
        if (end <= start)
        {
            return;
        }

        if (rows.IsEmpty)
        {
            TOp.AddSpan(ref state, values[start..end]);
            return;
        }

        int first = start >> 6;
        int last = (end - 1) >> 6;
        for (int w = first; w <= last; w++)
        {
            ulong word = rows[w];
            int baseRow = w << 6;
            if (w == first)
            {
                word &= ulong.MaxValue << (start & 63);
            }

            if (w == last && end - baseRow < 64)
            {
                word &= (1UL << (end - baseRow)) - 1;
            }

            if (word == ulong.MaxValue)
            {
                TOp.AddSpan(ref state, values.Slice(baseRow, 64));
                continue;
            }

            while (word != 0)
            {
                TOp.Add(ref state, values[baseRow + BitOperations.TrailingZeroCount(word)]);
                word &= word - 1;
            }
        }
    }
}
