using System;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Vorticity.Aggregating;

/// <summary>
/// What a variance keeps of a group: the indexed sums of <c>x − c</c> and of <c>(x − c)²</c>, the
/// same bits in any order and under any cut, around a center fixed for the whole run, the middle of
/// the column's bounds where the statistics hold them. The closer the center to a group's mean, the
/// fewer digits the difference of the two sums cancels.
/// </summary>
internal struct VarianceState
{
    internal IndexedSum Shifted;
    internal IndexedSum Squares;

    /// <summary>The center <c>c</c>, the same for every group of the run.</summary>
    internal double Center;

    /// <summary>What a stored value is worth: 1, or <c>10^-scale</c> for the unscaled integer of a decimal.</summary>
    internal double Unit;

    /// <summary>The sample variance, over <c>n − 1</c>; null below two values.</summary>
    internal readonly double? Variance
    {
        get
        {
            long count = Shifted.Count;
            if (count < 2)
            {
                return null;
            }

            double sum = Shifted.Value;
            double variance = (Squares.Value - (sum * (sum / count))) / (count - 1);

            // A rounding below zero is no spread at all; a NaN, from an infinity, stays one.
            return variance < 0 ? 0 : variance;
        }
    }
}

/// <summary>A variance's sums over a column of <typeparamref name="TValue"/>, each value moved by the center first.</summary>
internal readonly struct VarianceOp<TValue> : IValueOp<TValue, VarianceState>
    where TValue : unmanaged, INumberBase<TValue>
{
    public static VarianceState Seed() => new VarianceState { Unit = 1 };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Add(ref VarianceState state, TValue value)
    {
        double shifted = (double.CreateTruncating(value) * state.Unit) - state.Center;
        state.Shifted.Add(shifted);
        state.Squares.Add(shifted * shifted);
    }

    public static void AddWeighted(ref VarianceState state, TValue value, long count)
    {
        double shifted = (double.CreateTruncating(value) * state.Unit) - state.Center;
        state.Shifted.AddWeighted(shifted, count);
        state.Squares.AddWeighted(shifted * shifted, count);
    }

    [SkipLocalsInit]
    public static void AddSpan(ref VarianceState state, ReadOnlySpan<TValue> values)
    {
        Span<double> moved = stackalloc double[IndexedSum.Chunk];
        for (int start = 0; start < values.Length; start += IndexedSum.Chunk)
        {
            ReadOnlySpan<TValue> chunk = values.Slice(start, Math.Min(IndexedSum.Chunk, values.Length - start));
            Span<double> shifted = moved[..chunk.Length];
            for (int i = 0; i < chunk.Length; i++)
            {
                shifted[i] = (double.CreateTruncating(chunk[i]) * state.Unit) - state.Center;
            }

            state.Shifted.AddSpan(shifted);
            for (int i = 0; i < shifted.Length; i++)
            {
                shifted[i] *= shifted[i];
            }

            state.Squares.AddSpan(shifted);
        }
    }

    public static void Merge(ref VarianceState into, in VarianceState other)
    {
        into.Shifted.Merge(in other.Shifted);
        into.Squares.Merge(in other.Squares);
    }

    /// <remarks>The rows left out are left out, not read as zero: a zero moved by the center is a value.</remarks>
    [SkipLocalsInit]
    public static void AddWords(ref VarianceState state, ReadOnlySpan<TValue> block, ReadOnlySpan<ulong> words)
    {
        Span<TValue> selected = stackalloc TValue[WordFold.Run * 64];
        int count = 0;
        for (int w = 0; w < words.Length; w++)
        {
            ulong word = words[w];
            while (word != 0)
            {
                selected[count++] = block[(w << 6) + BitOperations.TrailingZeroCount(word)];
                word &= word - 1;
            }
        }

        AddSpan(ref state, selected[..count]);
    }
}
