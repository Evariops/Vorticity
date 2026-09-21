using System;
using System.Numerics;

namespace Vorticity.Aggregating;

/// <summary>
/// A built-in aggregate over values of a fixed width: what one value, one value repeated, and a
/// dense span of valid values each do to the state. The drivers call it monomorphised.
/// </summary>
/// <typeparam name="TValue">The storage value.</typeparam>
/// <typeparam name="TState">The state of one group.</typeparam>
internal interface IValueOp<TValue, TState>
    where TValue : unmanaged
{
    static abstract TState Seed();

    static abstract void Add(ref TState state, TValue value);

    /// <summary>Folds <paramref name="count"/> rows holding <paramref name="value"/>: a run, a constant block, a dictionary entry.</summary>
    static abstract void AddWeighted(ref TState state, TValue value, long count);

    /// <summary>Folds a dense span of valid values; where the kernels are vectorised.</summary>
    static abstract void AddSpan(ref TState state, ReadOnlySpan<TValue> values);

    static abstract void Merge(ref TState into, in TState other);
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

/// <summary>A sum of signed integers in 64 bits, checked.</summary>
internal readonly struct SignedSum<TValue> : IValueOp<TValue, SumState<long>>
    where TValue : unmanaged, IBinaryInteger<TValue>
{
    public static SumState<long> Seed() => default;

    public static void Add(ref SumState<long> state, TValue value)
    {
        state.Sum = checked(state.Sum + long.CreateTruncating(value));
        state.Count++;
    }

    public static void AddWeighted(ref SumState<long> state, TValue value, long count)
    {
        state.Sum = checked(state.Sum + checked(long.CreateTruncating(value) * count));
        state.Count += count;
    }

    public static void AddSpan(ref SumState<long> state, ReadOnlySpan<TValue> values)
    {
        state.Sum = checked(state.Sum + SumKernels.Signed(values));
        state.Count += values.Length;
    }

    public static void Merge(ref SumState<long> into, in SumState<long> other)
    {
        into.Sum = checked(into.Sum + other.Sum);
        into.Count += other.Count;
    }
}

/// <summary>A sum of unsigned integers in 64 bits, checked.</summary>
internal readonly struct UnsignedSum<TValue> : IValueOp<TValue, SumState<ulong>>
    where TValue : unmanaged, IBinaryInteger<TValue>
{
    public static SumState<ulong> Seed() => default;

    public static void Add(ref SumState<ulong> state, TValue value)
    {
        state.Sum = checked(state.Sum + ulong.CreateTruncating(value));
        state.Count++;
    }

    public static void AddWeighted(ref SumState<ulong> state, TValue value, long count)
    {
        state.Sum = checked(state.Sum + checked(ulong.CreateTruncating(value) * (ulong)count));
        state.Count += count;
    }

    public static void AddSpan(ref SumState<ulong> state, ReadOnlySpan<TValue> values)
    {
        state.Sum = checked(state.Sum + SumKernels.Unsigned(values));
        state.Count += values.Length;
    }

    public static void Merge(ref SumState<ulong> into, in SumState<ulong> other)
    {
        into.Sum = checked(into.Sum + other.Sum);
        into.Count += other.Count;
    }
}

/// <summary>A sum of floating-point values in a double, NaN skipped.</summary>
internal readonly struct FloatSum<TValue> : IValueOp<TValue, SumState<double>>
    where TValue : unmanaged, INumberBase<TValue>
{
    public static SumState<double> Seed() => default;

    public static void Add(ref SumState<double> state, TValue value)
    {
        double widened = double.CreateTruncating(value);
        if (!double.IsNaN(widened))
        {
            state.Sum += widened;
            state.Count++;
        }
    }

    public static void AddWeighted(ref SumState<double> state, TValue value, long count)
    {
        double widened = double.CreateTruncating(value);
        if (!double.IsNaN(widened))
        {
            state.Sum += widened * count;
            state.Count += count;
        }
    }

    public static void AddSpan(ref SumState<double> state, ReadOnlySpan<TValue> values)
    {
        state.Sum += SumKernels.Float(values, out long counted);
        state.Count += counted;
    }

    public static void Merge(ref SumState<double> into, in SumState<double> other)
    {
        into.Sum += other.Sum;
        into.Count += other.Count;
    }
}

/// <summary>A sum of unscaled decimals in 128 bits, checked.</summary>
internal readonly struct DecimalSum : IValueOp<Int128, SumState<Int128>>
{
    public static SumState<Int128> Seed() => default;

    public static void Add(ref SumState<Int128> state, Int128 value)
    {
        state.Sum = checked(state.Sum + value);
        state.Count++;
    }

    public static void AddWeighted(ref SumState<Int128> state, Int128 value, long count)
    {
        state.Sum = checked(state.Sum + checked(value * count));
        state.Count += count;
    }

    public static void AddSpan(ref SumState<Int128> state, ReadOnlySpan<Int128> values)
    {
        Int128 sum = state.Sum;
        foreach (Int128 value in values)
        {
            sum = checked(sum + value);
        }

        state.Sum = sum;
        state.Count += values.Length;
    }

    public static void Merge(ref SumState<Int128> into, in SumState<Int128> other)
    {
        into.Sum = checked(into.Sum + other.Sum);
        into.Count += other.Count;
    }
}

/// <summary>The smallest value, NaN skipped.</summary>
internal readonly struct MinOp<TValue> : IValueOp<TValue, ExtremeState<TValue>>
    where TValue : unmanaged, INumber<TValue>
{
    public static ExtremeState<TValue> Seed() => default;

    public static void Add(ref ExtremeState<TValue> state, TValue value)
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

    public static void AddWeighted(ref ExtremeState<TValue> state, TValue value, long count) => Add(ref state, value);

    public static void AddSpan(ref ExtremeState<TValue> state, ReadOnlySpan<TValue> values)
    {
        if (ExtremeKernels.TryMin(values, out TValue min))
        {
            Add(ref state, min);
        }
    }

    public static void Merge(ref ExtremeState<TValue> into, in ExtremeState<TValue> other)
    {
        if (other.Has)
        {
            Add(ref into, other.Value);
        }
    }
}

/// <summary>The largest value, NaN skipped.</summary>
internal readonly struct MaxOp<TValue> : IValueOp<TValue, ExtremeState<TValue>>
    where TValue : unmanaged, INumber<TValue>
{
    public static ExtremeState<TValue> Seed() => default;

    public static void Add(ref ExtremeState<TValue> state, TValue value)
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

    public static void AddWeighted(ref ExtremeState<TValue> state, TValue value, long count) => Add(ref state, value);

    public static void AddSpan(ref ExtremeState<TValue> state, ReadOnlySpan<TValue> values)
    {
        if (ExtremeKernels.TryMax(values, out TValue max))
        {
            Add(ref state, max);
        }
    }

    public static void Merge(ref ExtremeState<TValue> into, in ExtremeState<TValue> other)
    {
        if (other.Has)
        {
            Add(ref into, other.Value);
        }
    }
}
