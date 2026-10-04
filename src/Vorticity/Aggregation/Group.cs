using System;
using System.Numerics;
using Vorticity.Aggregating;

namespace Vorticity;

/// <summary>
/// One group of a grouped scan, for the lambda of <c>Select</c>: its key, and the aggregates of
/// <see cref="Aggregates{TRecord}"/> over the group's rows.
/// </summary>
/// <typeparam name="TRecord">The record the scan is typed by.</typeparam>
/// <typeparam name="TKey">The key: the symbol of one column, or the tuple of symbols of a composite key, under the names the key's lambda gave them.</typeparam>
public readonly struct Group<TRecord, TKey>
{
    private readonly RecordBinding? _binding;
    private readonly RowFilter? _filter;

    internal Group(RecordBinding binding, TKey key)
    {
        _binding = binding;
        Key = key;
    }

    private Group(RecordBinding? binding, TKey key, RowFilter? filter)
    {
        _binding = binding;
        Key = key;
        _filter = filter;
    }

    /// <summary>The group's key: the symbol of its column, or the tuple of symbols of a composite key, each a result of the group.</summary>
    public TKey Key { get; }

    /// <summary>The binding the aggregates read their columns through, for the extensions that add aggregates.</summary>
    internal RecordBinding? Binding => _binding;

    /// <summary>The rows of the group its aggregates read, for a filtered group; null for every row.</summary>
    internal RowFilter? Filter => _filter;

    /// <summary>
    /// The group's rows where <paramref name="predicate"/> is true, with every aggregate, and its key:
    /// SQL's <c>FILTER (WHERE …)</c>. Two of them keep the rows both keep.
    /// </summary>
    /// <param name="predicate">A predicate over the rows, in the language of a scan's <c>Where</c>.</param>
    /// <returns>The filtered group, whose aggregates read those rows alone.</returns>
    /// <exception cref="InvalidOperationException">The predicate compares an aggregate: a filtered group keeps rows.</exception>
    public Group<TRecord, TKey> Where(Func<Probe<TRecord>, Predicate> predicate) =>
        new Group<TRecord, TKey>(_binding, Key, RowFilter.And(_filter, Aggregators.Rows(_binding, predicate), holds: true));

    /// <summary>The number of rows of the group, nulls included.</summary>
    /// <returns>The symbol of the count.</returns>
    public Sym<long> Count() => Aggregators.Filtered(Aggregators.Count(), _filter);

    /// <summary>The number of rows of the group where <paramref name="predicate"/> is true: <c>Where(predicate).Count()</c>.</summary>
    /// <param name="predicate">A predicate over the rows.</param>
    /// <returns>The symbol of the count.</returns>
    public Sym<long> Count(Func<Probe<TRecord>, Predicate> predicate) => Where(predicate).Count();

    /// <summary>Whether <paramref name="predicate"/> is true for a row of the group.</summary>
    /// <param name="predicate">A predicate over the rows.</param>
    /// <returns>The symbol of the answer; false for a filtered group that keeps no row.</returns>
    public Sym<bool> Any(Func<Probe<TRecord>, Predicate> predicate) =>
        Aggregators.Exists(RowFilter.And(_filter, Aggregators.Rows(_binding, predicate), holds: true), all: false);

    /// <summary>Whether <paramref name="predicate"/> is true for every row of the group: no row where it is false or unknown.</summary>
    /// <param name="predicate">A predicate over the rows.</param>
    /// <returns>The symbol of the answer; true for a filtered group that keeps no row.</returns>
    public Sym<bool> All(Func<Probe<TRecord>, Predicate> predicate) =>
        Aggregators.Exists(RowFilter.And(_filter, Aggregators.Rows(_binding, predicate), holds: false), all: true);

    /// <summary>The number of distinct non-null values of <paramref name="column"/> in the group.</summary>
    /// <typeparam name="T">The column's type.</typeparam>
    /// <param name="column">The column.</param>
    /// <returns>The symbol of the count.</returns>
    public Sym<long> CountDistinct<T>(Func<Probe<TRecord>, Sym<T>> column) =>
        Aggregators.Filtered(Aggregators.CountDistinct(Aggregators.Input(_binding, column)), _filter);

    /// <summary>The sum of <paramref name="column"/> in the group.</summary>
    /// <typeparam name="T">The column's type.</typeparam>
    /// <param name="column">The column.</param>
    /// <returns>The symbol of the sum; reading it throws <see cref="OverflowException"/> when the sum does not fit <typeparamref name="T"/>.</returns>
    public Sym<T> Sum<T>(Func<Probe<TRecord>, Sym<T>> column)
        where T : INumber<T> =>
        Aggregators.Filtered(Aggregators.Sum<T>(Aggregators.Input(_binding, column)), _filter);

    /// <summary>The sum of the non-null values of a nullable <paramref name="column"/> in the group.</summary>
    /// <typeparam name="T">The column's type, without its nullability.</typeparam>
    /// <param name="column">The column.</param>
    /// <returns>The symbol of the sum.</returns>
    public Sym<T> Sum<T>(Func<Probe<TRecord>, Sym<T?>> column)
        where T : struct, INumber<T> =>
        Aggregators.Filtered(Aggregators.Sum<T>(Aggregators.Input(_binding, column)), _filter);

    /// <summary>The smallest non-null value of <paramref name="column"/> in the group.</summary>
    /// <typeparam name="T">The column's type.</typeparam>
    /// <param name="column">The column.</param>
    /// <returns>The symbol of the minimum.</returns>
    public Sym<T?> Min<T>(Func<Probe<TRecord>, Sym<T>> column) =>
        Aggregators.Filtered(Aggregators.Extreme<T>(Aggregators.Input(_binding, column), max: false), _filter);

    /// <summary>The largest non-null value of <paramref name="column"/> in the group.</summary>
    /// <typeparam name="T">The column's type.</typeparam>
    /// <param name="column">The column.</param>
    /// <returns>The symbol of the maximum.</returns>
    public Sym<T?> Max<T>(Func<Probe<TRecord>, Sym<T>> column) =>
        Aggregators.Filtered(Aggregators.Extreme<T>(Aggregators.Input(_binding, column), max: true), _filter);

    /// <summary>The mean of <paramref name="column"/> in the group.</summary>
    /// <typeparam name="T">The column's type.</typeparam>
    /// <param name="column">The column.</param>
    /// <returns>The symbol of the mean; null when no row of the group holds a value.</returns>
    public Sym<double?> Average<T>(Func<Probe<TRecord>, Sym<T>> column)
        where T : INumber<T> =>
        Aggregators.Filtered(Aggregators.Average(Aggregators.Input(_binding, column)), _filter);

    /// <summary>The mean of the non-null values of a nullable <paramref name="column"/> in the group.</summary>
    /// <typeparam name="T">The column's type, without its nullability.</typeparam>
    /// <param name="column">The column.</param>
    /// <returns>The symbol of the mean.</returns>
    public Sym<double?> Average<T>(Func<Probe<TRecord>, Sym<T?>> column)
        where T : struct, INumber<T> =>
        Aggregators.Filtered(Aggregators.Average(Aggregators.Input(_binding, column)), _filter);

    /// <summary>The state <typeparamref name="TAggregator"/> folds <paramref name="column"/> into, per group.</summary>
    /// <typeparam name="T">The column's storage type.</typeparam>
    /// <typeparam name="TAggregator">The aggregator.</typeparam>
    /// <typeparam name="TState">The aggregator's state.</typeparam>
    /// <param name="column">The column.</param>
    /// <returns>The symbol of the state.</returns>
    public Sym<TState> Aggregate<T, TAggregator, TState>(Func<Probe<TRecord>, Sym<T>> column)
        where T : unmanaged
        where TAggregator : IAggregator<T, TState> =>
        Aggregators.Filtered(Aggregators.Custom<T, TAggregator, TState>(Aggregators.Input(_binding, column)), _filter);

    /// <summary>The state <typeparamref name="TAggregator"/> folds a nullable <paramref name="column"/> into, per group.</summary>
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
