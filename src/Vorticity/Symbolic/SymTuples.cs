using System;
using System.Collections.Generic;
using Vorticity.Expressions;

namespace Vorticity;

/// <summary>
/// Searches by tuples of columns, as SQL's <c>(a, b) IN ((1, 'x'), (2, 'y'))</c>:
/// <c>scan.Where(r =&gt; (r.Service, r.Host).In(pairs))</c> keeps the rows whose values of the columns are, together, one of
/// the tuples.
/// </summary>
/// <remarks>
/// A tuple that holds a null, or a value its column cannot hold exactly, matches no row and leaves the set; a row with a
/// null value is unknown where a tuple holds its other values, false otherwise. Beside the tuples, the filter holds each
/// column's set of their values, by which the columns' structures prune, and a dataset whose sort key leads with the
/// columns finds the tuples' granules in its sparse index. The columns are the scan's own: no function of one, no path
/// in a variant.
/// </remarks>
public static class SymTuples
{
    extension<T1, T2>((Sym<T1> First, Sym<T2> Second) columns)
    {
        /// <summary>The rows whose values of the two columns are one of <paramref name="values"/>.</summary>
        /// <param name="values">The tuples.</param>
        /// <returns>The predicate; none when no tuple can match.</returns>
        /// <exception cref="ArgumentException">A column is a function of one, or a path in a variant.</exception>
        public Predicate In(params ReadOnlySpan<(T1, T2)> values) => Pairs(columns.First.Column, columns.Second.Column, values);

        /// <summary>The rows whose values of the two columns are one of <paramref name="values"/>.</summary>
        /// <param name="values">The tuples, read once.</param>
        /// <returns>The predicate; none when no tuple can match.</returns>
        /// <exception cref="ArgumentException">A column is a function of one, or a path in a variant.</exception>
        public Predicate In(IEnumerable<(T1, T2)> values)
        {
            ArgumentNullException.ThrowIfNull(values);
            return Pairs<T1, T2>(columns.First.Column, columns.Second.Column, [.. values]);
        }
    }

    extension<T1, T2, T3>((Sym<T1> First, Sym<T2> Second, Sym<T3> Third) columns)
    {
        /// <summary>The rows whose values of the three columns are one of <paramref name="values"/>.</summary>
        /// <param name="values">The tuples.</param>
        /// <returns>The predicate; none when no tuple can match.</returns>
        /// <exception cref="ArgumentException">A column is a function of one, or a path in a variant.</exception>
        public Predicate In(params ReadOnlySpan<(T1, T2, T3)> values) => Triples(columns.First.Column, columns.Second.Column, columns.Third.Column, values);

        /// <summary>The rows whose values of the three columns are one of <paramref name="values"/>.</summary>
        /// <param name="values">The tuples, read once.</param>
        /// <returns>The predicate; none when no tuple can match.</returns>
        /// <exception cref="ArgumentException">A column is a function of one, or a path in a variant.</exception>
        public Predicate In(IEnumerable<(T1, T2, T3)> values)
        {
            ArgumentNullException.ThrowIfNull(values);
            return Triples<T1, T2, T3>(columns.First.Column, columns.Second.Column, columns.Third.Column, [.. values]);
        }
    }

    extension<T1, T2, T3, T4>((Sym<T1> First, Sym<T2> Second, Sym<T3> Third, Sym<T4> Fourth) columns)
    {
        /// <summary>The rows whose values of the four columns are one of <paramref name="values"/>.</summary>
        /// <param name="values">The tuples.</param>
        /// <returns>The predicate; none when no tuple can match.</returns>
        /// <exception cref="ArgumentException">A column is a function of one, or a path in a variant.</exception>
        public Predicate In(params ReadOnlySpan<(T1, T2, T3, T4)> values) =>
            Quadruples(columns.First.Column, columns.Second.Column, columns.Third.Column, columns.Fourth.Column, values);

        /// <summary>The rows whose values of the four columns are one of <paramref name="values"/>.</summary>
        /// <param name="values">The tuples, read once.</param>
        /// <returns>The predicate; none when no tuple can match.</returns>
        /// <exception cref="ArgumentException">A column is a function of one, or a path in a variant.</exception>
        public Predicate In(IEnumerable<(T1, T2, T3, T4)> values)
        {
            ArgumentNullException.ThrowIfNull(values);
            return Quadruples<T1, T2, T3, T4>(columns.First.Column, columns.Second.Column, columns.Third.Column, columns.Fourth.Column, [.. values]);
        }
    }

    private static Predicate Pairs<T1, T2>(ColumnSym first, ColumnSym second, ReadOnlySpan<(T1, T2)> values)
    {
        List<FilterLiteral> flat = new List<FilterLiteral>(values.Length * 2);
        foreach ((T1 a, T2 b) in values)
        {
            if (SymLowering.TryExact(first, a, out FilterLiteral x) && SymLowering.TryExact(second, b, out FilterLiteral y))
            {
                flat.Add(x);
                flat.Add(y);
            }
        }

        return SymLowering.Tuples([first, second], flat);
    }

    private static Predicate Triples<T1, T2, T3>(ColumnSym first, ColumnSym second, ColumnSym third, ReadOnlySpan<(T1, T2, T3)> values)
    {
        List<FilterLiteral> flat = new List<FilterLiteral>(values.Length * 3);
        foreach ((T1 a, T2 b, T3 c) in values)
        {
            if (SymLowering.TryExact(first, a, out FilterLiteral x) && SymLowering.TryExact(second, b, out FilterLiteral y)
                && SymLowering.TryExact(third, c, out FilterLiteral z))
            {
                flat.Add(x);
                flat.Add(y);
                flat.Add(z);
            }
        }

        return SymLowering.Tuples([first, second, third], flat);
    }

    private static Predicate Quadruples<T1, T2, T3, T4>(
        ColumnSym first, ColumnSym second, ColumnSym third, ColumnSym fourth, ReadOnlySpan<(T1, T2, T3, T4)> values)
    {
        List<FilterLiteral> flat = new List<FilterLiteral>(values.Length * 4);
        foreach ((T1 a, T2 b, T3 c, T4 d) in values)
        {
            if (SymLowering.TryExact(first, a, out FilterLiteral w) && SymLowering.TryExact(second, b, out FilterLiteral x)
                && SymLowering.TryExact(third, c, out FilterLiteral y) && SymLowering.TryExact(fourth, d, out FilterLiteral z))
            {
                flat.Add(w);
                flat.Add(x);
                flat.Add(y);
                flat.Add(z);
            }
        }

        return SymLowering.Tuples([first, second, third, fourth], flat);
    }
}
