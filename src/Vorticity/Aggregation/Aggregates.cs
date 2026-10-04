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
/// A null is skipped by every aggregate but <see cref="Count()"/>, and so is a NaN by the sum, the
/// mean, the minimum and the maximum, as the file statistics skip it.
/// </remarks>
public readonly struct Aggregates<TRecord>
{
    private readonly RecordBinding? _binding;
    private readonly RowFilter? _filter;

    internal Aggregates(RecordBinding binding) => _binding = binding;

    private Aggregates(RecordBinding? binding, RowFilter? filter)
    {
        _binding = binding;
        _filter = filter;
    }

    /// <summary>The binding the aggregates read their columns through, for the extensions that add aggregates.</summary>
    internal RecordBinding? Binding => _binding;

    /// <summary>The rows the aggregates read, for filtered aggregates; null for every row the scan keeps.</summary>
    internal RowFilter? Filter => _filter;

    /// <summary>
    /// The aggregates of the rows the scan keeps where <paramref name="predicate"/> is true: SQL's
    /// <c>FILTER (WHERE …)</c>, beside the aggregates of every row in the same pass.
    /// </summary>
    /// <param name="predicate">A predicate over the rows, in the language of a scan's <c>Where</c>.</param>
    /// <returns>The filtered aggregates.</returns>
    /// <exception cref="InvalidOperationException">The predicate compares an aggregate: a filter keeps rows.</exception>
    public Aggregates<TRecord> Where(Func<Probe<TRecord>, Predicate> predicate) =>
        new Aggregates<TRecord>(_binding, RowFilter.And(_filter, Aggregators.Rows(_binding, predicate), holds: true));

    /// <summary>The number of rows the scan keeps, nulls included.</summary>
    /// <returns>The symbol of the count.</returns>
    public Sym<long> Count() => Aggregators.Filtered(Aggregators.Count(), _filter);

    /// <summary>The number of rows where <paramref name="predicate"/> is true: <c>Where(predicate).Count()</c>.</summary>
    /// <param name="predicate">A predicate over the rows.</param>
    /// <returns>The symbol of the count.</returns>
    public Sym<long> Count(Func<Probe<TRecord>, Predicate> predicate) => Where(predicate).Count();

    /// <summary>Whether <paramref name="predicate"/> is true for a row.</summary>
    /// <param name="predicate">A predicate over the rows.</param>
    /// <returns>The symbol of the answer; false when no row is kept.</returns>
    public Sym<bool> Any(Func<Probe<TRecord>, Predicate> predicate) =>
        Aggregators.Exists(RowFilter.And(_filter, Aggregators.Rows(_binding, predicate), holds: true), all: false);

    /// <summary>Whether <paramref name="predicate"/> is true for every row: no row where it is false or unknown.</summary>
    /// <param name="predicate">A predicate over the rows.</param>
    /// <returns>The symbol of the answer; true when no row is kept.</returns>
    public Sym<bool> All(Func<Probe<TRecord>, Predicate> predicate) =>
        Aggregators.Exists(RowFilter.And(_filter, Aggregators.Rows(_binding, predicate), holds: false), all: true);

    /// <summary>The number of distinct non-null values of <paramref name="column"/>.</summary>
    /// <typeparam name="T">The column's type.</typeparam>
    /// <param name="column">The column.</param>
    /// <returns>The symbol of the count.</returns>
    public Sym<long> CountDistinct<T>(Func<Probe<TRecord>, Sym<T>> column) =>
        Aggregators.Filtered(Aggregators.CountDistinct(Aggregators.Input(_binding, column)), _filter);

    /// <summary>The sum of <paramref name="column"/>, accumulated exactly in 128 bits for an integer, in a double for a float, as unscaled 128 bits for a decimal.</summary>
    /// <typeparam name="T">The column's type.</typeparam>
    /// <param name="column">The column.</param>
    /// <returns>The symbol of the sum; zero when no row holds a value. Reading it throws <see cref="OverflowException"/> when the sum does not fit <typeparamref name="T"/>.</returns>
    public Sym<T> Sum<T>(Func<Probe<TRecord>, Sym<T>> column)
        where T : INumber<T> =>
        Aggregators.Filtered(Aggregators.Sum<T>(Aggregators.Input(_binding, column)), _filter);

    /// <summary>The sum of the non-null values of a nullable <paramref name="column"/>.</summary>
    /// <typeparam name="T">The column's type, without its nullability.</typeparam>
    /// <param name="column">The column.</param>
    /// <returns>The symbol of the sum; zero when no row holds a value.</returns>
    public Sym<T> Sum<T>(Func<Probe<TRecord>, Sym<T?>> column)
        where T : struct, INumber<T> =>
        Aggregators.Filtered(Aggregators.Sum<T>(Aggregators.Input(_binding, column)), _filter);

    /// <summary>The smallest non-null value of <paramref name="column"/>.</summary>
    /// <typeparam name="T">The column's type.</typeparam>
    /// <param name="column">The column.</param>
    /// <returns>The symbol of the minimum; the default of <typeparamref name="T"/> when no row holds a value.</returns>
    public Sym<T?> Min<T>(Func<Probe<TRecord>, Sym<T>> column) =>
        Aggregators.Filtered(Aggregators.Extreme<T>(Aggregators.Input(_binding, column), max: false), _filter);

    /// <summary>The largest non-null value of <paramref name="column"/>.</summary>
    /// <typeparam name="T">The column's type.</typeparam>
    /// <param name="column">The column.</param>
    /// <returns>The symbol of the maximum; the default of <typeparamref name="T"/> when no row holds a value.</returns>
    public Sym<T?> Max<T>(Func<Probe<TRecord>, Sym<T>> column) =>
        Aggregators.Filtered(Aggregators.Extreme<T>(Aggregators.Input(_binding, column), max: true), _filter);

    /// <summary>The mean of <paramref name="column"/>.</summary>
    /// <typeparam name="T">The column's type.</typeparam>
    /// <param name="column">The column.</param>
    /// <returns>The symbol of the mean; null when no row holds a value.</returns>
    public Sym<double?> Average<T>(Func<Probe<TRecord>, Sym<T>> column)
        where T : INumber<T> =>
        Aggregators.Filtered(Aggregators.Average(Aggregators.Input(_binding, column)), _filter);

    /// <summary>The mean of the non-null values of a nullable <paramref name="column"/>.</summary>
    /// <typeparam name="T">The column's type, without its nullability.</typeparam>
    /// <param name="column">The column.</param>
    /// <returns>The symbol of the mean; null when no row holds a value.</returns>
    public Sym<double?> Average<T>(Func<Probe<TRecord>, Sym<T?>> column)
        where T : struct, INumber<T> =>
        Aggregators.Filtered(Aggregators.Average(Aggregators.Input(_binding, column)), _filter);

    /// <summary>The sample variance of <paramref name="column"/>, over <c>n − 1</c>.</summary>
    /// <typeparam name="T">The column's type.</typeparam>
    /// <param name="column">The column.</param>
    /// <returns>The symbol of the variance; null below two values.</returns>
    public Sym<double?> Variance<T>(Func<Probe<TRecord>, Sym<T>> column)
        where T : INumber<T> =>
        Aggregators.Filtered(Aggregators.Variance(Aggregators.Input(_binding, column), deviation: false), _filter);

    /// <summary>The sample variance of the non-null values of a nullable <paramref name="column"/>.</summary>
    /// <typeparam name="T">The column's type, without its nullability.</typeparam>
    /// <param name="column">The column.</param>
    /// <returns>The symbol of the variance; null below two values.</returns>
    public Sym<double?> Variance<T>(Func<Probe<TRecord>, Sym<T?>> column)
        where T : struct, INumber<T> =>
        Aggregators.Filtered(Aggregators.Variance(Aggregators.Input(_binding, column), deviation: false), _filter);

    /// <summary>The sample standard deviation of <paramref name="column"/>: the square root of its variance.</summary>
    /// <typeparam name="T">The column's type.</typeparam>
    /// <param name="column">The column.</param>
    /// <returns>The symbol of the standard deviation; null below two values.</returns>
    public Sym<double?> StandardDeviation<T>(Func<Probe<TRecord>, Sym<T>> column)
        where T : INumber<T> =>
        Aggregators.Filtered(Aggregators.Variance(Aggregators.Input(_binding, column), deviation: true), _filter);

    /// <summary>The sample standard deviation of the non-null values of a nullable <paramref name="column"/>.</summary>
    /// <typeparam name="T">The column's type, without its nullability.</typeparam>
    /// <param name="column">The column.</param>
    /// <returns>The symbol of the standard deviation; null below two values.</returns>
    public Sym<double?> StandardDeviation<T>(Func<Probe<TRecord>, Sym<T?>> column)
        where T : struct, INumber<T> =>
        Aggregators.Filtered(Aggregators.Variance(Aggregators.Input(_binding, column), deviation: true), _filter);

    /// <summary>The state <typeparamref name="TAggregator"/> folds <paramref name="column"/> into.</summary>
    /// <typeparam name="T">The column's storage type.</typeparam>
    /// <typeparam name="TAggregator">The aggregator; an <see cref="IEncodedAggregator{T, TState}"/> reads the encoded blocks.</typeparam>
    /// <typeparam name="TState">The aggregator's state.</typeparam>
    /// <param name="column">The column.</param>
    /// <returns>The symbol of the state.</returns>
    public Sym<TState> Aggregate<T, TAggregator, TState>(Func<Probe<TRecord>, Sym<T>> column)
        where T : unmanaged
        where TAggregator : IAggregator<T, TState> =>
        Aggregators.Filtered(Aggregators.Custom<T, TAggregator, TState>(Aggregators.Input(_binding, column)), _filter);

    /// <summary>The state <typeparamref name="TAggregator"/> folds a nullable <paramref name="column"/> into; the aggregator reads its validity.</summary>
    /// <typeparam name="T">The column's storage type.</typeparam>
    /// <typeparam name="TAggregator">The aggregator.</typeparam>
    /// <typeparam name="TState">The aggregator's state.</typeparam>
    /// <param name="column">The column.</param>
    /// <returns>The symbol of the state.</returns>
    public Sym<TState> Aggregate<T, TAggregator, TState>(Func<Probe<TRecord>, Sym<T?>> column)
        where T : unmanaged
        where TAggregator : IAggregator<T, TState> =>
        Aggregators.Filtered(Aggregators.Custom<T, TAggregator, TState>(Aggregators.Input(_binding, column)), _filter);
}
