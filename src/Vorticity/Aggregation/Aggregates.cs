using System;
using System.Numerics;
using Vorticity.Aggregating;

namespace Vorticity;

/// <summary>
/// The aggregates of a scan, for the lambda of <c>AggAsync</c>: each member is a symbol that stands
/// for one answer, and every answer the lambda returns is computed in one pass.
/// </summary>
/// <typeparam name="TRecord">The record the scan is typed by.</typeparam>
/// <remarks>
/// A null is skipped by every aggregate but <see cref="Count"/>, and so is a NaN by the sum, the
/// mean, the minimum and the maximum, as the file statistics skip it.
/// </remarks>
public readonly struct Aggregates<TRecord>
{
    private readonly RecordBinding? _binding;

    internal Aggregates(RecordBinding binding) => _binding = binding;

    /// <summary>The number of rows the scan keeps, nulls included.</summary>
    /// <returns>The symbol of the count.</returns>
    public Sym<long> Count() => Aggregators.Count();

    /// <summary>The number of distinct non-null values of <paramref name="column"/>.</summary>
    /// <typeparam name="T">The column's type.</typeparam>
    /// <param name="column">The column.</param>
    /// <returns>The symbol of the count.</returns>
    public Sym<long> CountDistinct<T>(Func<Probe<TRecord>, Sym<T>> column) =>
        Aggregators.CountDistinct(Aggregators.Input(_binding, column));

    /// <summary>The sum of <paramref name="column"/>, accumulated at the width the file statistics use: 64 bits for an integer, a double for a float.</summary>
    /// <typeparam name="T">The column's type.</typeparam>
    /// <param name="column">The column.</param>
    /// <returns>The symbol of the sum; zero when no row holds a value. Reading it throws <see cref="OverflowException"/> when the sum does not fit <typeparamref name="T"/>.</returns>
    public Sym<T> Sum<T>(Func<Probe<TRecord>, Sym<T>> column)
        where T : INumber<T> =>
        Aggregators.Sum<T>(Aggregators.Input(_binding, column));

    /// <summary>The sum of the non-null values of a nullable <paramref name="column"/>.</summary>
    /// <typeparam name="T">The column's type, without its nullability.</typeparam>
    /// <param name="column">The column.</param>
    /// <returns>The symbol of the sum; zero when no row holds a value.</returns>
    public Sym<T> Sum<T>(Func<Probe<TRecord>, Sym<T?>> column)
        where T : struct, INumber<T> =>
        Aggregators.Sum<T>(Aggregators.Input(_binding, column));

    /// <summary>The smallest non-null value of <paramref name="column"/>.</summary>
    /// <typeparam name="T">The column's type.</typeparam>
    /// <param name="column">The column.</param>
    /// <returns>The symbol of the minimum; the default of <typeparamref name="T"/> when no row holds a value.</returns>
    public Sym<T?> Min<T>(Func<Probe<TRecord>, Sym<T>> column) =>
        Aggregators.Extreme<T>(Aggregators.Input(_binding, column), max: false);

    /// <summary>The largest non-null value of <paramref name="column"/>.</summary>
    /// <typeparam name="T">The column's type.</typeparam>
    /// <param name="column">The column.</param>
    /// <returns>The symbol of the maximum; the default of <typeparamref name="T"/> when no row holds a value.</returns>
    public Sym<T?> Max<T>(Func<Probe<TRecord>, Sym<T>> column) =>
        Aggregators.Extreme<T>(Aggregators.Input(_binding, column), max: true);

    /// <summary>The mean of <paramref name="column"/>.</summary>
    /// <typeparam name="T">The column's type.</typeparam>
    /// <param name="column">The column.</param>
    /// <returns>The symbol of the mean; null when no row holds a value.</returns>
    public Sym<double?> Avg<T>(Func<Probe<TRecord>, Sym<T>> column)
        where T : INumber<T> =>
        Aggregators.Avg(Aggregators.Input(_binding, column));

    /// <summary>The mean of the non-null values of a nullable <paramref name="column"/>.</summary>
    /// <typeparam name="T">The column's type, without its nullability.</typeparam>
    /// <param name="column">The column.</param>
    /// <returns>The symbol of the mean; null when no row holds a value.</returns>
    public Sym<double?> Avg<T>(Func<Probe<TRecord>, Sym<T?>> column)
        where T : struct, INumber<T> =>
        Aggregators.Avg(Aggregators.Input(_binding, column));

    /// <summary>The state <typeparamref name="TAggregator"/> folds <paramref name="column"/> into.</summary>
    /// <typeparam name="T">The column's storage type.</typeparam>
    /// <typeparam name="TAggregator">The aggregator; an <see cref="IEncodedAggregator{T, TState}"/> reads the encoded blocks.</typeparam>
    /// <typeparam name="TState">The aggregator's state.</typeparam>
    /// <param name="column">The column.</param>
    /// <returns>The symbol of the state.</returns>
    public Sym<TState> Aggregate<T, TAggregator, TState>(Func<Probe<TRecord>, Sym<T>> column)
        where T : unmanaged
        where TAggregator : IAggregator<T, TState> =>
        Aggregators.Custom<T, TAggregator, TState>(Aggregators.Input(_binding, column));

    /// <summary>The state <typeparamref name="TAggregator"/> folds a nullable <paramref name="column"/> into; the aggregator reads its validity.</summary>
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
