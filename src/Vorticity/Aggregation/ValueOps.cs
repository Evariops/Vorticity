using System;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using Vorticity.Types.Numerics;

namespace Vorticity.Aggregating;

/// <summary>
/// A built-in aggregate over values of a fixed width: what one value, one value repeated, and a
/// dense span of valid values each do to the state. The drivers call it monomorphised, on the value
/// of the op their slot holds: empty for most, what the run fixed for every group for a variance,
/// which its groups' states then leave out.
/// </summary>
/// <typeparam name="TValue">The storage value.</typeparam>
/// <typeparam name="TState">The state of one group.</typeparam>
internal interface IValueOp<TValue, TState>
    where TValue : unmanaged
{
    TState Seed();

    void Add(ref TState state, TValue value);

    /// <summary>
    /// As <see cref="Add"/>, with no branch on the value where the op can tell: an extreme of integers
    /// stores its choice whatever it is. The slot folds with it while its
    /// groups have seen few rows, their extremes moving at random; past that, the branch predicts.
    /// </summary>
    void AddSelected(ref TState state, TValue value);

    /// <summary>Folds <paramref name="count"/> rows holding <paramref name="value"/>: a run, a constant block, a dictionary entry.</summary>
    void AddWeighted(ref TState state, TValue value, long count);

    /// <summary>Folds a dense span of valid values; where the kernels are vectorised.</summary>
    void AddSpan(ref TState state, ReadOnlySpan<TValue> values);

    void Merge(ref TState into, in TState other);

    /// <summary>
    /// Folds the rows of <paramref name="block"/> that <paramref name="words"/> set, 64 rows a word
    /// and at most <see cref="WordFold.Run"/> words, none of them empty: row <c>i</c> when bit
    /// <c>i % 64</c> of word <c>i / 64</c> is set.
    /// </summary>
    void AddWords(ref TState state, ReadOnlySpan<TValue> block, ReadOnlySpan<ulong> words);

    /// <summary>The mean a sum's state holds, its total over its count, for a mean that shares the sum's slot; null with no value.</summary>
    /// <exception cref="NotSupportedException">The state is not a sum's.</exception>
    double? Mean(in TState state) => throw new NotSupportedException($"{typeof(TState).Name} holds no mean.");
}

/// <summary>A running sum at the widened type, and how many values it holds.</summary>
internal struct SumState<TAcc>
{
    internal TAcc Sum;
    internal long Count;
}

/// <summary>The smallest or largest value seen, if any.</summary>
internal struct ExtremeState<TValue>
{
    internal bool Has;
    internal TValue Value;
}

/// <summary>
/// A sum of signed integers in 128 bits: exact for fewer than 2^64 values whatever their order, so
/// neither the order of the rows nor the cut of a parallel aggregation decides whether it throws;
/// only the conversion to the result's type is checked.
/// </summary>
internal readonly struct SignedSum<TValue> : IValueOp<TValue, SumState<Int128>>
    where TValue : unmanaged, IBinaryInteger<TValue>
{
    public SumState<Int128> Seed() => default;

    public void Add(ref SumState<Int128> state, TValue value)
    {
        state.Sum += Int128.CreateTruncating(value);
        state.Count++;
    }

    public void AddSelected(ref SumState<Int128> state, TValue value) => Add(ref state, value);

    public void AddWeighted(ref SumState<Int128> state, TValue value, long count)
    {
        state.Sum += Int128.CreateTruncating(value) * count;
        state.Count += count;
    }

    public void AddSpan(ref SumState<Int128> state, ReadOnlySpan<TValue> values)
    {
        state.Sum += SumKernels.Signed(values);
        state.Count += values.Length;
    }

    public void Merge(ref SumState<Int128> into, in SumState<Int128> other)
    {
        into.Sum += other.Sum;
        into.Count += other.Count;
    }

    public double? Mean(in SumState<Int128> state) => state.Count == 0 ? null : double.CreateTruncating(state.Sum) / state.Count;

    [SkipLocalsInit]
    public void AddWords(ref SumState<Int128> state, ReadOnlySpan<TValue> block, ReadOnlySpan<ulong> words)
    {
        Span<TValue> selected = stackalloc TValue[WordFold.Run * 64];
        selected = selected[..block.Length];
        WordFold.Select(block, words, TValue.Zero, selected);
        state.Sum += SumKernels.Signed(selected);
        state.Count += WordFold.Count(words);
    }
}

/// <summary>
/// A sum of signed integers in 64 bits, for a column whose statistics prove its rows times its
/// largest magnitude stay below 2^63: no sum of any of its rows can then overflow, in any order.
/// Half the state of <see cref="SignedSum{TValue}"/>, and an add a row.
/// </summary>
internal readonly struct NarrowSignedSum<TValue> : IValueOp<TValue, SumState<long>>
    where TValue : unmanaged, IBinaryInteger<TValue>
{
    public SumState<long> Seed() => default;

    public void Add(ref SumState<long> state, TValue value)
    {
        state.Sum += long.CreateTruncating(value);
        state.Count++;
    }

    public void AddSelected(ref SumState<long> state, TValue value) => Add(ref state, value);

    public void AddWeighted(ref SumState<long> state, TValue value, long count)
    {
        state.Sum += long.CreateTruncating(value) * count;
        state.Count += count;
    }

    public void AddSpan(ref SumState<long> state, ReadOnlySpan<TValue> values)
    {
        state.Sum += (long)SumKernels.Signed(values);
        state.Count += values.Length;
    }

    public void Merge(ref SumState<long> into, in SumState<long> other)
    {
        into.Sum += other.Sum;
        into.Count += other.Count;
    }

    public double? Mean(in SumState<long> state) => state.Count == 0 ? null : double.CreateTruncating(state.Sum) / state.Count;

    [SkipLocalsInit]
    public void AddWords(ref SumState<long> state, ReadOnlySpan<TValue> block, ReadOnlySpan<ulong> words)
    {
        Span<TValue> selected = stackalloc TValue[WordFold.Run * 64];
        selected = selected[..block.Length];
        WordFold.Select(block, words, TValue.Zero, selected);
        state.Sum += (long)SumKernels.Signed(selected);
        state.Count += WordFold.Count(words);
    }
}

/// <summary>A sum of unsigned integers in 64 bits, for a column whose statistics prove its rows times its largest value stay below 2^64.</summary>
internal readonly struct NarrowUnsignedSum<TValue> : IValueOp<TValue, SumState<ulong>>
    where TValue : unmanaged, IBinaryInteger<TValue>
{
    public SumState<ulong> Seed() => default;

    public void Add(ref SumState<ulong> state, TValue value)
    {
        state.Sum += ulong.CreateTruncating(value);
        state.Count++;
    }

    public void AddSelected(ref SumState<ulong> state, TValue value) => Add(ref state, value);

    public void AddWeighted(ref SumState<ulong> state, TValue value, long count)
    {
        state.Sum += ulong.CreateTruncating(value) * (ulong)count;
        state.Count += count;
    }

    public void AddSpan(ref SumState<ulong> state, ReadOnlySpan<TValue> values)
    {
        state.Sum += (ulong)SumKernels.Unsigned(values);
        state.Count += values.Length;
    }

    public void Merge(ref SumState<ulong> into, in SumState<ulong> other)
    {
        into.Sum += other.Sum;
        into.Count += other.Count;
    }

    public double? Mean(in SumState<ulong> state) => state.Count == 0 ? null : double.CreateTruncating(state.Sum) / state.Count;

    [SkipLocalsInit]
    public void AddWords(ref SumState<ulong> state, ReadOnlySpan<TValue> block, ReadOnlySpan<ulong> words)
    {
        Span<TValue> selected = stackalloc TValue[WordFold.Run * 64];
        selected = selected[..block.Length];
        WordFold.Select(block, words, TValue.Zero, selected);
        state.Sum += (ulong)SumKernels.Unsigned(selected);
        state.Count += WordFold.Count(words);
    }
}

/// <summary>A sum of unsigned integers in 128 bits, exact whatever the order, as <see cref="SignedSum{TValue}"/> is.</summary>
internal readonly struct UnsignedSum<TValue> : IValueOp<TValue, SumState<UInt128>>
    where TValue : unmanaged, IBinaryInteger<TValue>
{
    public SumState<UInt128> Seed() => default;

    public void Add(ref SumState<UInt128> state, TValue value)
    {
        state.Sum += UInt128.CreateTruncating(value);
        state.Count++;
    }

    public void AddSelected(ref SumState<UInt128> state, TValue value) => Add(ref state, value);

    public void AddWeighted(ref SumState<UInt128> state, TValue value, long count)
    {
        state.Sum += UInt128.CreateTruncating(value) * (ulong)count;
        state.Count += count;
    }

    public void AddSpan(ref SumState<UInt128> state, ReadOnlySpan<TValue> values)
    {
        state.Sum += SumKernels.Unsigned(values);
        state.Count += values.Length;
    }

    public void Merge(ref SumState<UInt128> into, in SumState<UInt128> other)
    {
        into.Sum += other.Sum;
        into.Count += other.Count;
    }

    public double? Mean(in SumState<UInt128> state) => state.Count == 0 ? null : double.CreateTruncating(state.Sum) / state.Count;

    [SkipLocalsInit]
    public void AddWords(ref SumState<UInt128> state, ReadOnlySpan<TValue> block, ReadOnlySpan<ulong> words)
    {
        Span<TValue> selected = stackalloc TValue[WordFold.Run * 64];
        selected = selected[..block.Length];
        WordFold.Select(block, words, TValue.Zero, selected);
        state.Sum += SumKernels.Unsigned(selected);
        state.Count += WordFold.Count(words);
    }
}

/// <summary>
/// A sum of signed integers in 64 bits that no mean reads: the total alone, eight bytes a group where
/// <see cref="NarrowSignedSum{TValue}"/> keeps the count a mean needs beside it. The statistics prove
/// it cannot overflow, as for that one.
/// </summary>
internal readonly struct NarrowSignedTotal<TValue> : IValueOp<TValue, long>
    where TValue : unmanaged, IBinaryInteger<TValue>
{
    public long Seed() => 0;

    public void Add(ref long state, TValue value) => state += long.CreateTruncating(value);

    public void AddSelected(ref long state, TValue value) => Add(ref state, value);

    public void AddWeighted(ref long state, TValue value, long count) => state += long.CreateTruncating(value) * count;

    public void AddSpan(ref long state, ReadOnlySpan<TValue> values) => state += (long)SumKernels.Signed(values);

    public void Merge(ref long into, in long other) => into += other;

    [SkipLocalsInit]
    public void AddWords(ref long state, ReadOnlySpan<TValue> block, ReadOnlySpan<ulong> words)
    {
        Span<TValue> selected = stackalloc TValue[WordFold.Run * 64];
        selected = selected[..block.Length];
        WordFold.Select(block, words, TValue.Zero, selected);
        state += (long)SumKernels.Signed(selected);
    }
}

/// <summary>A sum of signed integers in 128 bits that no mean reads: the total alone, as <see cref="SignedSum{TValue}"/> without its count.</summary>
internal readonly struct SignedTotal<TValue> : IValueOp<TValue, Int128>
    where TValue : unmanaged, IBinaryInteger<TValue>
{
    public Int128 Seed() => default;

    public void Add(ref Int128 state, TValue value) => state += Int128.CreateTruncating(value);

    public void AddSelected(ref Int128 state, TValue value) => Add(ref state, value);

    public void AddWeighted(ref Int128 state, TValue value, long count) => state += Int128.CreateTruncating(value) * count;

    public void AddSpan(ref Int128 state, ReadOnlySpan<TValue> values) => state += SumKernels.Signed(values);

    public void Merge(ref Int128 into, in Int128 other) => into += other;

    [SkipLocalsInit]
    public void AddWords(ref Int128 state, ReadOnlySpan<TValue> block, ReadOnlySpan<ulong> words)
    {
        Span<TValue> selected = stackalloc TValue[WordFold.Run * 64];
        selected = selected[..block.Length];
        WordFold.Select(block, words, TValue.Zero, selected);
        state += SumKernels.Signed(selected);
    }
}

/// <summary>A sum of unsigned integers in 64 bits that no mean reads, as <see cref="NarrowUnsignedSum{TValue}"/> without its count.</summary>
internal readonly struct NarrowUnsignedTotal<TValue> : IValueOp<TValue, ulong>
    where TValue : unmanaged, IBinaryInteger<TValue>
{
    public ulong Seed() => 0;

    public void Add(ref ulong state, TValue value) => state += ulong.CreateTruncating(value);

    public void AddSelected(ref ulong state, TValue value) => Add(ref state, value);

    public void AddWeighted(ref ulong state, TValue value, long count) => state += ulong.CreateTruncating(value) * (ulong)count;

    public void AddSpan(ref ulong state, ReadOnlySpan<TValue> values) => state += (ulong)SumKernels.Unsigned(values);

    public void Merge(ref ulong into, in ulong other) => into += other;

    [SkipLocalsInit]
    public void AddWords(ref ulong state, ReadOnlySpan<TValue> block, ReadOnlySpan<ulong> words)
    {
        Span<TValue> selected = stackalloc TValue[WordFold.Run * 64];
        selected = selected[..block.Length];
        WordFold.Select(block, words, TValue.Zero, selected);
        state += (ulong)SumKernels.Unsigned(selected);
    }
}

/// <summary>A sum of unsigned integers in 128 bits that no mean reads, as <see cref="UnsignedSum{TValue}"/> without its count.</summary>
internal readonly struct UnsignedTotal<TValue> : IValueOp<TValue, UInt128>
    where TValue : unmanaged, IBinaryInteger<TValue>
{
    public UInt128 Seed() => default;

    public void Add(ref UInt128 state, TValue value) => state += UInt128.CreateTruncating(value);

    public void AddSelected(ref UInt128 state, TValue value) => Add(ref state, value);

    public void AddWeighted(ref UInt128 state, TValue value, long count) => state += UInt128.CreateTruncating(value) * (ulong)count;

    public void AddSpan(ref UInt128 state, ReadOnlySpan<TValue> values) => state += SumKernels.Unsigned(values);

    public void Merge(ref UInt128 into, in UInt128 other) => into += other;

    [SkipLocalsInit]
    public void AddWords(ref UInt128 state, ReadOnlySpan<TValue> block, ReadOnlySpan<ulong> words)
    {
        Span<TValue> selected = stackalloc TValue[WordFold.Run * 64];
        selected = selected[..block.Length];
        WordFold.Select(block, words, TValue.Zero, selected);
        state += SumKernels.Unsigned(selected);
    }
}

/// <summary>A sum of unscaled decimals in 128 bits, checked.</summary>
internal readonly struct DecimalSum : IValueOp<Int128, SumState<Int128>>
{
    public SumState<Int128> Seed() => default;

    public void Add(ref SumState<Int128> state, Int128 value)
    {
        state.Sum = checked(state.Sum + value);
        state.Count++;
    }

    public void AddSelected(ref SumState<Int128> state, Int128 value) => Add(ref state, value);

    public void AddWeighted(ref SumState<Int128> state, Int128 value, long count)
    {
        state.Sum = checked(state.Sum + checked(value * count));
        state.Count += count;
    }

    public void AddSpan(ref SumState<Int128> state, ReadOnlySpan<Int128> values)
    {
        Int128 sum = state.Sum;
        foreach (Int128 value in values)
        {
            sum = checked(sum + value);
        }

        state.Sum = sum;
        state.Count += values.Length;
    }

    public void Merge(ref SumState<Int128> into, in SumState<Int128> other)
    {
        into.Sum = checked(into.Sum + other.Sum);
        into.Count += other.Count;
    }

    public void AddWords(ref SumState<Int128> state, ReadOnlySpan<Int128> block, ReadOnlySpan<ulong> words) =>
        WordFold.Each<Int128, SumState<Int128>, DecimalSum>(in this, ref state, block, words);
}

/// <summary>
/// A sum of unscaled decimals of up to 38 digits, exact whatever their count: two such values can
/// overflow 128 bits, so the loop keeps an <see cref="Int128"/> total and spills it into 320 bits
/// only when the next value would overflow it.
/// </summary>
internal readonly struct WideDecimalSum : IValueOp<Int128, SumState<WideSum>>
{
    public SumState<WideSum> Seed() => default;

    public void Add(ref SumState<WideSum> state, Int128 value)
    {
        state.Sum.Add(value);
        state.Count++;
    }

    public void AddSelected(ref SumState<WideSum> state, Int128 value) => Add(ref state, value);

    public void AddWeighted(ref SumState<WideSum> state, Int128 value, long count)
    {
        state.Sum.AddProduct(value, count);
        state.Count += count;
    }

    public void AddSpan(ref SumState<WideSum> state, ReadOnlySpan<Int128> values)
    {
        NarrowTotals.Add(ref state.Sum, values);
        state.Count += values.Length;
    }

    public void Merge(ref SumState<WideSum> into, in SumState<WideSum> other)
    {
        into.Sum.Merge(in other.Sum);
        into.Count += other.Count;
    }

    public void AddWords(ref SumState<WideSum> state, ReadOnlySpan<Int128> block, ReadOnlySpan<ulong> words) =>
        WordFold.Each<Int128, SumState<WideSum>, WideDecimalSum>(in this, ref state, block, words);
}

/// <summary>A sum of unscaled decimals of more than 38 digits, exact whatever their count, through an <see cref="Int256"/> total spilled into 320 bits.</summary>
internal readonly struct Decimal256Sum : IValueOp<Int256, SumState<WideSum>>
{
    public SumState<WideSum> Seed() => default;

    public void Add(ref SumState<WideSum> state, Int256 value)
    {
        state.Sum.Add(in value);
        state.Count++;
    }

    public void AddSelected(ref SumState<WideSum> state, Int256 value) => Add(ref state, value);

    public void AddWeighted(ref SumState<WideSum> state, Int256 value, long count)
    {
        state.Sum.AddProduct(in value, count);
        state.Count += count;
    }

    public void AddSpan(ref SumState<WideSum> state, ReadOnlySpan<Int256> values)
    {
        NarrowTotals.Add(ref state.Sum, values);
        state.Count += values.Length;
    }

    public void Merge(ref SumState<WideSum> into, in SumState<WideSum> other)
    {
        into.Sum.Merge(in other.Sum);
        into.Count += other.Count;
    }

    public void AddWords(ref SumState<WideSum> state, ReadOnlySpan<Int256> block, ReadOnlySpan<ulong> words) =>
        WordFold.Each<Int256, SumState<WideSum>, Decimal256Sum>(in this, ref state, block, words);
}

/// <summary>The smallest or the largest value of a type with an order and no NaN: a decimal of 256 bits, which no vector kernel folds.</summary>
/// <typeparam name="TValue">The value.</typeparam>
/// <typeparam name="TMax">Whether the largest is kept rather than the smallest.</typeparam>
internal readonly struct OrderedExtremeOp<TValue, TMax> : IValueOp<TValue, ExtremeState<TValue>>
    where TValue : unmanaged, IComparable<TValue>
    where TMax : struct, IFlag
{
    public ExtremeState<TValue> Seed() => default;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Add(ref ExtremeState<TValue> state, TValue value)
    {
        int order = value.CompareTo(state.Value);
        if (!state.Has || (TMax.Value ? order > 0 : order < 0))
        {
            state.Value = value;
            state.Has = true;
        }
    }

    public void AddSelected(ref ExtremeState<TValue> state, TValue value) => Add(ref state, value);

    public void AddWeighted(ref ExtremeState<TValue> state, TValue value, long count) => Add(ref state, value);

    public void AddSpan(ref ExtremeState<TValue> state, ReadOnlySpan<TValue> values)
    {
        foreach (TValue value in values)
        {
            Add(ref state, value);
        }
    }

    public void Merge(ref ExtremeState<TValue> into, in ExtremeState<TValue> other)
    {
        if (other.Has)
        {
            Add(ref into, other.Value);
        }
    }

    public void AddWords(ref ExtremeState<TValue> state, ReadOnlySpan<TValue> block, ReadOnlySpan<ulong> words) =>
        WordFold.Each<TValue, ExtremeState<TValue>, OrderedExtremeOp<TValue, TMax>>(in this, ref state, block, words);
}

/// <summary>A compile-time boolean, so that a generic op's branch on it is specialised away.</summary>
internal interface IFlag
{
    static abstract bool Value { get; }
}

internal readonly struct Yes : IFlag
{
    public static bool Value => true;
}

internal readonly struct No : IFlag
{
    public static bool Value => false;
}

/// <summary>What the extremes of a type of values may take for granted.</summary>
internal static class ExtremeOps
{
    /// <summary>Whether <typeparamref name="TValue"/> is an integer, which has no NaN: a constant of each instantiation.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static bool Integral<TValue>() =>
        typeof(TValue) == typeof(long) || typeof(TValue) == typeof(int) || typeof(TValue) == typeof(short) || typeof(TValue) == typeof(sbyte)
        || typeof(TValue) == typeof(ulong) || typeof(TValue) == typeof(uint) || typeof(TValue) == typeof(ushort) || typeof(TValue) == typeof(byte)
        || typeof(TValue) == typeof(Int128) || typeof(TValue) == typeof(UInt128);
}

/// <summary>The smallest value, NaN skipped.</summary>
internal readonly struct MinOp<TValue> : IValueOp<TValue, ExtremeState<TValue>>
    where TValue : unmanaged, INumber<TValue>
{
    public ExtremeState<TValue> Seed() => default;

    public void Add(ref ExtremeState<TValue> state, TValue value)
    {
        if (TValue.IsNaN(value))
        {
            return;
        }

        if (!state.Has || value < state.Value)
        {
            state.Value = value;
            state.Has = true;
        }
    }

    /// <remarks>An integer, no NaN: the value stored whatever it is, the choice a select.</remarks>
    public void AddSelected(ref ExtremeState<TValue> state, TValue value)
    {
        if (ExtremeOps.Integral<TValue>())
        {
            TValue current = state.Value;
            state.Value = state.Has && current <= value ? current : value;
            state.Has = true;
            return;
        }

        Add(ref state, value);
    }

    public void AddWeighted(ref ExtremeState<TValue> state, TValue value, long count) => Add(ref state, value);

    public void AddSpan(ref ExtremeState<TValue> state, ReadOnlySpan<TValue> values)
    {
        if (ExtremeKernels.TryMin(values, out TValue min))
        {
            Add(ref state, min);
        }
    }

    public void Merge(ref ExtremeState<TValue> into, in ExtremeState<TValue> other)
    {
        if (other.Has)
        {
            Add(ref into, other.Value);
        }
    }

    /// <remarks>The rows left out read as the first row kept, which cannot move an extreme.</remarks>
    [SkipLocalsInit]
    public void AddWords(ref ExtremeState<TValue> state, ReadOnlySpan<TValue> block, ReadOnlySpan<ulong> words)
    {
        Span<TValue> selected = stackalloc TValue[WordFold.Run * 64];
        selected = selected[..block.Length];
        WordFold.Select(block, words, block[BitOperations.TrailingZeroCount(words[0])], selected);
        AddSpan(ref state, selected);
    }
}

/// <summary>The largest value, NaN skipped.</summary>
internal readonly struct MaxOp<TValue> : IValueOp<TValue, ExtremeState<TValue>>
    where TValue : unmanaged, INumber<TValue>
{
    public ExtremeState<TValue> Seed() => default;

    public void Add(ref ExtremeState<TValue> state, TValue value)
    {
        if (TValue.IsNaN(value))
        {
            return;
        }

        if (!state.Has || value > state.Value)
        {
            state.Value = value;
            state.Has = true;
        }
    }

    /// <remarks>An integer, no NaN: the value stored whatever it is, the choice a select.</remarks>
    public void AddSelected(ref ExtremeState<TValue> state, TValue value)
    {
        if (ExtremeOps.Integral<TValue>())
        {
            TValue current = state.Value;
            state.Value = state.Has && current >= value ? current : value;
            state.Has = true;
            return;
        }

        Add(ref state, value);
    }

    public void AddWeighted(ref ExtremeState<TValue> state, TValue value, long count) => Add(ref state, value);

    public void AddSpan(ref ExtremeState<TValue> state, ReadOnlySpan<TValue> values)
    {
        if (ExtremeKernels.TryMax(values, out TValue max))
        {
            Add(ref state, max);
        }
    }

    public void Merge(ref ExtremeState<TValue> into, in ExtremeState<TValue> other)
    {
        if (other.Has)
        {
            Add(ref into, other.Value);
        }
    }

    /// <remarks>The rows left out read as the first row kept, which cannot move an extreme.</remarks>
    [SkipLocalsInit]
    public void AddWords(ref ExtremeState<TValue> state, ReadOnlySpan<TValue> block, ReadOnlySpan<ulong> words)
    {
        Span<TValue> selected = stackalloc TValue[WordFold.Run * 64];
        selected = selected[..block.Length];
        WordFold.Select(block, words, block[BitOperations.TrailingZeroCount(words[0])], selected);
        AddSpan(ref state, selected);
    }
}

/// <summary>
/// The smallest value, NaN skipped, in a state that is the value alone: the state starts at a value no
/// row holds, which says that none was seen. For a float that
/// is NaN, which no add keeps; for an integer, the top of its range where no row reaches it.
/// </summary>
internal readonly struct SeededMinOp<TValue> : IValueOp<TValue, TValue>
    where TValue : unmanaged, INumber<TValue>
{
    /// <summary>Not read: the slot starts from the seed its column leaves unreached.</summary>
    public TValue Seed() => throw new NotSupportedException("A seeded extreme starts from the seed its slot is given.");

    public void Add(ref TValue state, TValue value)
    {
        // A state still at NaN takes any number, which a NaN compares false against.
        if (!TValue.IsNaN(value) && !(state <= value))
        {
            state = value;
        }
    }

    /// <remarks>
    /// An integer, no NaN: the smaller of the two stored whatever it is, the choice a select. A
    /// branch on it missed as often as a group met a new minimum, while its groups had seen few rows.
    /// </remarks>
    public void AddSelected(ref TValue state, TValue value)
    {
        if (ExtremeOps.Integral<TValue>())
        {
            TValue current = state;
            state = current <= value ? current : value;
            return;
        }

        Add(ref state, value);
    }

    public void AddWeighted(ref TValue state, TValue value, long count) => Add(ref state, value);

    public void AddSpan(ref TValue state, ReadOnlySpan<TValue> values)
    {
        if (ExtremeKernels.TryMin(values, out TValue min))
        {
            Add(ref state, min);
        }
    }

    /// <remarks>A state that saw nothing holds the seed, which moves no other.</remarks>
    public void Merge(ref TValue into, in TValue other) => Add(ref into, other);

    /// <remarks>The rows left out read as the first row kept, which cannot move an extreme.</remarks>
    [SkipLocalsInit]
    public void AddWords(ref TValue state, ReadOnlySpan<TValue> block, ReadOnlySpan<ulong> words)
    {
        Span<TValue> selected = stackalloc TValue[WordFold.Run * 64];
        selected = selected[..block.Length];
        WordFold.Select(block, words, block[BitOperations.TrailingZeroCount(words[0])], selected);
        AddSpan(ref state, selected);
    }
}

/// <summary>The largest value, NaN skipped, in a state that is the value alone, as <see cref="SeededMinOp{TValue}"/>: an integer starts at the bottom of its range.</summary>
internal readonly struct SeededMaxOp<TValue> : IValueOp<TValue, TValue>
    where TValue : unmanaged, INumber<TValue>
{
    /// <summary>Not read: the slot starts from the seed its column leaves unreached.</summary>
    public TValue Seed() => throw new NotSupportedException("A seeded extreme starts from the seed its slot is given.");

    public void Add(ref TValue state, TValue value)
    {
        if (!TValue.IsNaN(value) && !(state >= value))
        {
            state = value;
        }
    }

    /// <remarks>An integer, no NaN: the larger of the two stored whatever it is, the choice a select.</remarks>
    public void AddSelected(ref TValue state, TValue value)
    {
        if (ExtremeOps.Integral<TValue>())
        {
            TValue current = state;
            state = current >= value ? current : value;
            return;
        }

        Add(ref state, value);
    }

    public void AddWeighted(ref TValue state, TValue value, long count) => Add(ref state, value);

    public void AddSpan(ref TValue state, ReadOnlySpan<TValue> values)
    {
        if (ExtremeKernels.TryMax(values, out TValue max))
        {
            Add(ref state, max);
        }
    }

    /// <remarks>A state that saw nothing holds the seed, which moves no other.</remarks>
    public void Merge(ref TValue into, in TValue other) => Add(ref into, other);

    /// <remarks>The rows left out read as the first row kept, which cannot move an extreme.</remarks>
    [SkipLocalsInit]
    public void AddWords(ref TValue state, ReadOnlySpan<TValue> block, ReadOnlySpan<ulong> words)
    {
        Span<TValue> selected = stackalloc TValue[WordFold.Run * 64];
        selected = selected[..block.Length];
        WordFold.Select(block, words, block[BitOperations.TrailingZeroCount(words[0])], selected);
        AddSpan(ref state, selected);
    }
}

/// <summary>The rows of a run of mask words, for <see cref="IValueOp{TValue, TState}.AddWords"/>.</summary>
internal static class WordFold
{
    /// <summary>
    /// A word holding fewer rows than this is folded a row at a time: a count of trailing zeros
    /// and an add a row cost less than selecting the word's 64 rows and folding them.
    /// </summary>
    internal const int Dense = 16;

    /// <summary>
    /// The most words selected before a fold: the dense kernel's fixed cost, a reduction across
    /// its lanes and a call, is paid once a run, not once a word.
    /// </summary>
    internal const int Run = 16;

    /// <summary>The rows <paramref name="words"/> hold, one at a time.</summary>
    internal static void Each<TValue, TState, TOp>(in TOp op, ref TState state, ReadOnlySpan<TValue> block, ReadOnlySpan<ulong> words)
        where TValue : unmanaged
        where TOp : struct, IValueOp<TValue, TState>
    {
        for (int w = 0; w < words.Length; w++)
        {
            ulong word = words[w];
            while (word != 0)
            {
                op.Add(ref state, block[(w << 6) + BitOperations.TrailingZeroCount(word)]);
                word &= word - 1;
            }
        }
    }

    /// <summary>How many rows <paramref name="words"/> hold.</summary>
    internal static long Count(ReadOnlySpan<ulong> words)
    {
        long count = 0;
        foreach (ulong word in words)
        {
            count += BitOperations.PopCount(word);
        }

        return count;
    }

    /// <summary>
    /// The whole of <paramref name="block"/> into <paramref name="into"/>, each row
    /// <paramref name="words"/> leave out read as <paramref name="fill"/>.
    /// </summary>
    /// <remarks>
    /// Where there are 512-bit vectors, a select a vector under lanes taken from the word, which a
    /// dense span's kernel then folds without a test a row; elsewhere a select a row.
    /// </remarks>
    internal static void Select<T>(ReadOnlySpan<T> block, ReadOnlySpan<ulong> words, T fill, Span<T> into)
        where T : unmanaged
    {
        Debug.Assert(block.Length == words.Length * 64 && into.Length == block.Length);
        if (Compute.WordBytes.IsAccelerated && Vector512<T>.IsSupported)
        {
            Compute.WordBytes spread = Compute.WordBytes.Create();
            Vector512<T> filler = Vector512.Create(fill);
            int lanes = Vector512<T>.Count;
            ref T from = ref MemoryMarshal.GetReference(block);
            ref T to = ref MemoryMarshal.GetReference(into);
            for (int w = 0; w < words.Length; w++)
            {
                ulong word = words[w];
                for (int group = 0; group < 64 / lanes; group++)
                {
                    nuint at = (nuint)((w << 6) + (group * lanes));
                    Vector512.ConditionalSelect(spread.Lanes<T>(word, group), Vector512.LoadUnsafe(ref from, at), filler).StoreUnsafe(ref to, at);
                }
            }

            return;
        }

        for (int i = 0; i < into.Length; i++)
        {
            into[i] = ((words[i >> 6] >> (i & 63)) & 1) != 0 ? block[i] : fill;
        }
    }
}
