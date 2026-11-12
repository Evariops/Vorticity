using System;
using System.Numerics;
using Vorticity.Aggregating;

namespace Vorticity;

/// <summary>
/// The points of a group, or of a scan, taken in the order of a column (<c>g.OrderedBy(r => r.At)</c>): a point is a row
/// whose order and value both hold one, and points of one order come in the order of their values. Its aggregates are
/// exact and the same bits at every degree of parallelism, however the rows lie in files, objects or levels.
/// </summary>
/// <typeparam name="TRecord">The record the scan is typed by.</typeparam>
/// <remarks>
/// <para>
/// The pass keeps each group's runs of points, the points of a piece of the source read in order, and folds them once it is
/// done. Rows in the order of the column, as a dataset sorted by it or by a key that ends with it keeps them, cost a few
/// bytes a group whatever their number. A group whose runs cross, a late point written into a later object, is read again
/// from the rows its runs lie between, its points then sorted: rows in no order cost their points.
/// </para>
/// <para>
/// A null order or value makes no point, nor does a float's NaN or infinity; a float's −0 is read as +0.
/// </para>
/// </remarks>
public readonly struct OrderedGroup<TRecord>
{
    private readonly RecordBinding? _binding;
    private readonly RowFilter? _filter;
    private readonly ColumnShape? _order;

    internal OrderedGroup(RecordBinding? binding, RowFilter? filter, ColumnShape order)
    {
        _binding = binding;
        _filter = filter;
        _order = order;
    }

    /// <summary>
    /// The growth of a counter: for each point after the first, its rise from the point before it, or its whole value
    /// where it fell, the counter having started again from zero. For 100, 130, 160, 5, 20: 30 + 30 + 5 + 15 = 80.
    /// </summary>
    /// <typeparam name="T">The column's type: an integer or a float.</typeparam>
    /// <param name="column">The counter's column.</param>
    /// <returns>
    /// The symbol of the growth: exact, rounded once to <typeparamref name="T"/>; 0 for a group of one point, null for one of
    /// none. Reading it throws <see cref="OverflowException"/> when an integer growth does not fit <typeparamref name="T"/>.
    /// </returns>
    public Sym<T?> Increase<T>(Func<Probe<TRecord>, Sym<T>> column)
        where T : struct, INumber<T> =>
        Aggregators.Filtered(Aggregators.Increase<T>(Aggregators.Input(_binding, column), Order), _filter);

    /// <inheritdoc cref="Increase{T}(Func{Probe{TRecord}, Sym{T}})"/>
    public Sym<T?> Increase<T>(Func<Probe<TRecord>, Sym<T?>> column)
        where T : struct, INumber<T> =>
        Aggregators.Filtered(Aggregators.Increase<T>(Aggregators.Input(_binding, column), Order), _filter);

    /// <summary>The value of the first point: the earliest in the order, the smallest value of those.</summary>
    /// <typeparam name="T">The column's type: an integer or a float.</typeparam>
    /// <param name="column">The column.</param>
    /// <returns>The symbol of the value; null for a group of no point.</returns>
    public Sym<T?> First<T>(Func<Probe<TRecord>, Sym<T>> column)
        where T : struct, INumber<T> =>
        Aggregators.Filtered(Aggregators.InOrder<T>(Aggregators.Input(_binding, column), Order, last: false), _filter);

    /// <inheritdoc cref="First{T}(Func{Probe{TRecord}, Sym{T}})"/>
    public Sym<T?> First<T>(Func<Probe<TRecord>, Sym<T?>> column)
        where T : struct, INumber<T> =>
        Aggregators.Filtered(Aggregators.InOrder<T>(Aggregators.Input(_binding, column), Order, last: false), _filter);

    /// <summary>The value of the last point: the latest in the order, the largest value of those.</summary>
    /// <typeparam name="T">The column's type: an integer or a float.</typeparam>
    /// <param name="column">The column.</param>
    /// <returns>The symbol of the value; null for a group of no point.</returns>
    public Sym<T?> Last<T>(Func<Probe<TRecord>, Sym<T>> column)
        where T : struct, INumber<T> =>
        Aggregators.Filtered(Aggregators.InOrder<T>(Aggregators.Input(_binding, column), Order, last: true), _filter);

    /// <inheritdoc cref="Last{T}(Func{Probe{TRecord}, Sym{T}})"/>
    public Sym<T?> Last<T>(Func<Probe<TRecord>, Sym<T?>> column)
        where T : struct, INumber<T> =>
        Aggregators.Filtered(Aggregators.InOrder<T>(Aggregators.Input(_binding, column), Order, last: true), _filter);

    private ColumnShape Order => _order ?? throw new InvalidOperationException("An ordered group is made by OrderedBy, inside the lambda of Select or AggregateAsync.");
}
