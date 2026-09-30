using System;
using System.Numerics;
using Vorticity.Aggregating;

namespace Vorticity;

/// <summary>
/// One group of a grouped scan, for the lambda of <c>AggAsync</c>: its key, and the aggregates of
/// <see cref="Aggregates{TRecord}"/> over the group's rows.
/// </summary>
/// <typeparam name="TRecord">The record the scan is typed by.</typeparam>
/// <typeparam name="TKey">The key: one column's type, or the tuple of a composite key, whose components are <c>Key.Item1</c> to <c>Key.Item4</c>.</typeparam>
public readonly struct Group<TRecord, TKey>
{
    private readonly RecordBinding? _binding;

    internal Group(RecordBinding binding, Sym<TKey> key)
    {
        _binding = binding;
        Key = key;
    }

    /// <summary>The group's key.</summary>
    public Sym<TKey> Key { get; }

    /// <summary>The binding the aggregates read their columns through, for the extensions that add aggregates.</summary>
    internal RecordBinding? Binding => _binding;

    /// <summary>The number of rows of the group, nulls included.</summary>
    /// <returns>The symbol of the count.</returns>
    public Sym<long> Count() => Aggregators.Count();

    /// <summary>The number of distinct non-null values of <paramref name="column"/> in the group.</summary>
    /// <typeparam name="T">The column's type.</typeparam>
    /// <param name="column">The column.</param>
    /// <returns>The symbol of the count.</returns>
    public Sym<long> CountDistinct<T>(Func<Probe<TRecord>, Sym<T>> column) =>
        Aggregators.CountDistinct(Aggregators.Input(_binding, column));

    /// <summary>The sum of <paramref name="column"/> in the group.</summary>
    /// <typeparam name="T">The column's type.</typeparam>
    /// <param name="column">The column.</param>
    /// <returns>The symbol of the sum; reading it throws <see cref="OverflowException"/> when the sum does not fit <typeparamref name="T"/>.</returns>
    public Sym<T> Sum<T>(Func<Probe<TRecord>, Sym<T>> column)
        where T : INumber<T> =>
        Aggregators.Sum<T>(Aggregators.Input(_binding, column));

    /// <summary>The sum of the non-null values of a nullable <paramref name="column"/> in the group.</summary>
    /// <typeparam name="T">The column's type, without its nullability.</typeparam>
    /// <param name="column">The column.</param>
    /// <returns>The symbol of the sum.</returns>
    public Sym<T> Sum<T>(Func<Probe<TRecord>, Sym<T?>> column)
        where T : struct, INumber<T> =>
        Aggregators.Sum<T>(Aggregators.Input(_binding, column));

    /// <summary>The smallest non-null value of <paramref name="column"/> in the group.</summary>
    /// <typeparam name="T">The column's type.</typeparam>
    /// <param name="column">The column.</param>
    /// <returns>The symbol of the minimum.</returns>
    public Sym<T?> Min<T>(Func<Probe<TRecord>, Sym<T>> column) =>
        Aggregators.Extreme<T>(Aggregators.Input(_binding, column), max: false);

    /// <summary>The largest non-null value of <paramref name="column"/> in the group.</summary>
    /// <typeparam name="T">The column's type.</typeparam>
    /// <param name="column">The column.</param>
    /// <returns>The symbol of the maximum.</returns>
    public Sym<T?> Max<T>(Func<Probe<TRecord>, Sym<T>> column) =>
        Aggregators.Extreme<T>(Aggregators.Input(_binding, column), max: true);

    /// <summary>The mean of <paramref name="column"/> in the group.</summary>
    /// <typeparam name="T">The column's type.</typeparam>
    /// <param name="column">The column.</param>
    /// <returns>The symbol of the mean; null when no row of the group holds a value.</returns>
    public Sym<double?> Avg<T>(Func<Probe<TRecord>, Sym<T>> column)
        where T : INumber<T> =>
        Aggregators.Avg(Aggregators.Input(_binding, column));

    /// <summary>The mean of the non-null values of a nullable <paramref name="column"/> in the group.</summary>
    /// <typeparam name="T">The column's type, without its nullability.</typeparam>
    /// <param name="column">The column.</param>
    /// <returns>The symbol of the mean.</returns>
    public Sym<double?> Avg<T>(Func<Probe<TRecord>, Sym<T?>> column)
        where T : struct, INumber<T> =>
        Aggregators.Avg(Aggregators.Input(_binding, column));

    /// <summary>The state <typeparamref name="TAggregator"/> folds <paramref name="column"/> into, per group.</summary>
    /// <typeparam name="T">The column's storage type.</typeparam>
    /// <typeparam name="TAggregator">The aggregator.</typeparam>
    /// <typeparam name="TState">The aggregator's state.</typeparam>
    /// <param name="column">The column.</param>
    /// <returns>The symbol of the state.</returns>
    public Sym<TState> Aggregate<T, TAggregator, TState>(Func<Probe<TRecord>, Sym<T>> column)
        where T : unmanaged
        where TAggregator : IAggregator<T, TState> =>
        Aggregators.Custom<T, TAggregator, TState>(Aggregators.Input(_binding, column));

    /// <summary>The state <typeparamref name="TAggregator"/> folds a nullable <paramref name="column"/> into, per group.</summary>
    /// <typeparam name="T">The column's storage type.</typeparam>
    /// <typeparam name="TAggregator">The aggregator.</typeparam>
    /// <typeparam name="TState">The aggregator's state.</typeparam>
    /// <param name="column">The column.</param>
    /// <returns>The symbol of the state.</returns>
    public Sym<TState> Aggregate<T, TAggregator, TState>(Func<Probe<TRecord>, Sym<T?>> column)
        where T : unmanaged
        where TAggregator : IAggregator<T, TState> =>
        Aggregators.Custom<T, TAggregator, TState>(Aggregators.Input(_binding, column));
}
