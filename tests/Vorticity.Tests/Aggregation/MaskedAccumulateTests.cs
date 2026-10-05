using System;
using System.Linq;
using System.Numerics;

using Vorticity.Aggregating;
using Xunit;

namespace Vorticity.Tests.Aggregation;

/// <summary>
/// A fixed-width aggregate folding the rows a mask holds against the same rows added one at a
/// time: masks empty, sparse, either side of the density a word is selected at, dense, full and
/// full but one, and each word its own of those, over ranges that start and end inside a word
/// and span more words than a run selects at once, for each op that selects a word
/// whole -- signed and unsigned sums, float sums with NaN among the values, minimum and maximum.
/// </summary>
public sealed class MaskedAccumulateTests
{
    private const int Rows = 3_000;

    [Fact]
    public void SignedSumsFoldTheMaskedRows() =>
        Run<int, SumState<Int128>, SignedSum<int>>(random => random.Next(), (a, b) => a.Sum == b.Sum && a.Count == b.Count);

    [Fact]
    public void NarrowSumsFoldTheMaskedRows()
    {
        Run<sbyte, SumState<Int128>, SignedSum<sbyte>>(random => (sbyte)random.Next(), (a, b) => a.Sum == b.Sum && a.Count == b.Count);
        Run<ushort, SumState<UInt128>, UnsignedSum<ushort>>(random => (ushort)random.Next(), (a, b) => a.Sum == b.Sum && a.Count == b.Count);
        Run<byte, SumState<UInt128>, UnsignedSum<byte>>(random => (byte)random.Next(), (a, b) => a.Sum == b.Sum && a.Count == b.Count);
    }

    [Fact]
    public void FloatSumsFoldTheMaskedNumbers()
    {
        Run<double, IndexedSum, IndexedFloatSum<double>>(random => Number(random), Same);
        Run<float, IndexedSum, IndexedFloatSum<float>>(random => (float)Number(random), Same);
    }

    [Fact]
    public void ExtremesAreTheMaskedRowsExtremes()
    {
        Run<long, ExtremeState<long>, MinOp<long>>(random => random.NextInt64(), (a, b) => a.Has == b.Has && a.Value == b.Value);
        Run<long, ExtremeState<long>, MaxOp<long>>(random => random.NextInt64(), (a, b) => a.Has == b.Has && a.Value == b.Value);
        Run<double, ExtremeState<double>, MinOp<double>>(random => Number(random), (a, b) => a.Has == b.Has && a.Value == b.Value);
        Run<short, ExtremeState<short>, MaxOp<short>>(random => (short)random.Next(), (a, b) => a.Has == b.Has && a.Value == b.Value);
    }

    private static double Number(Random random) => random.Next(8) == 0 ? double.NaN : (random.NextDouble() - 0.3) * 1000;

    // A float sum is the same bits whatever the path its values take.
    private static bool Same(IndexedSum a, IndexedSum b) =>
        a.Count == b.Count && BitConverter.DoubleToInt64Bits(a.Value) == BitConverter.DoubleToInt64Bits(b.Value);

    private static void Run<TValue, TState, TOp>(Func<Random, TValue> next, Func<TState, TState, bool> same)
        where TValue : unmanaged
        where TOp : IValueOp<TValue, TState>
    {
        Random random = new Random(20260927);
        TValue[] values = new TValue[Rows];
        for (int i = 0; i < Rows; i++)
        {
            values[i] = next(random);
        }

        // Rows kept out of 64 a word: none, a few, one either side of the selected density, half,
        // most, all but one and all; then, as -1, each word's own share of those, so that runs of
        // dense words, full or not, end on sparse and empty ones.
        int[] shares = [0, 3, WordFold.Dense - 1, WordFold.Dense, 32, 60, 63, 64];
        foreach (int kept in shares.Append(-1))
        {
            ulong[] mask = new ulong[(Rows + 63) / 64];
            for (int w = 0; w < mask.Length; w++)
            {
                int share = kept >= 0 ? kept : shares[random.Next(shares.Length)];
                for (int bit = 0; bit < 64; bit++)
                {
                    if (share == 64 || random.Next(64) < share)
                    {
                        mask[w] |= 1UL << bit;
                    }
                }
            }

            foreach ((int start, int end) in new[] { (0, Rows), (0, 640), (5, 2997), (64, 128), (70, 71), (130, 130), (1, 63), (1, 1100), (100, 2111) })
            {
                TState expected = TOp.Seed();
                for (int row = start; row < end; row++)
                {
                    if ((mask[row >> 6] >> (row & 63) & 1) != 0)
                    {
                        TOp.Add(ref expected, values[row]);
                    }
                }

                TState actual = TOp.Seed();
                FixedSlot<TValue, TState, TOp, object>.Accumulate(ref actual, values, mask, start, end);
                Assert.True(same(actual, expected), $"{typeof(TOp).Name}: {kept} of 64 over [{start}, {end}): {actual} against {expected}");
            }
        }
    }
}
