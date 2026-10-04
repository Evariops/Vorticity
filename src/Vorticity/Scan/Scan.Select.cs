using System;
using System.Runtime.CompilerServices;

namespace Vorticity;

public sealed partial class Scan<TRecord>
{
    /// <summary>One value per row the scan keeps: <c>scan.Select(r =&gt; r.City)</c>, <c>select r.City</c> in a query.</summary>
    /// <typeparam name="T">The value's type.</typeparam>
    /// <param name="selector">A lambda over the record's columns, returning one of them; run once, now.</param>
    /// <returns>The values, in file order or in the scan's key order, read when enumerated.</returns>
    /// <exception cref="InvalidOperationException">The lambda returns a symbol that is not a column of the scan.</exception>
    public Projection<T> Select<T>(Func<Probe<TRecord>, Sym<T>> selector)
    {
        ArgumentNullException.ThrowIfNull(selector);
        IProjectionElement element = ((ISymbol)selector(new Probe<TRecord>(Binding))).Projected(0);
        return new Projection<T>(new ProjectionQuery(Host, [element]));
    }

    /// <summary>
    /// Several values per row the scan keeps, read through a record:
    /// <c>scan.Select(r =&gt; (r.Day, r.City)).As&lt;DayCity&gt;()</c>.
    /// </summary>
    /// <param name="selector">A lambda over the record's columns, returning a tuple of any length of them; run once, now.</param>
    /// <returns>The values, which <see cref="Projection.As{TRecord}"/> reads.</returns>
    /// <exception cref="ArgumentException">An element of the tuple is not a symbol.</exception>
    /// <exception cref="InvalidOperationException">An element is not a column of the scan.</exception>
    public Projection Select(Func<Probe<TRecord>, ITuple> selector)
    {
        ArgumentNullException.ThrowIfNull(selector);
        ITuple tuple = selector(new Probe<TRecord>(Binding)) ?? throw new ArgumentException("The selector returned no tuple.", nameof(selector));
        IProjectionElement[] elements = new IProjectionElement[tuple.Length];
        for (int i = 0; i < elements.Length; i++)
        {
            elements[i] = tuple[i] is ISymbol symbol
                ? symbol.Projected(i)
                : throw new ArgumentException(
                    $"Element {i + 1} of the projection is not a symbol but {tuple[i]?.GetType().Name ?? "null"}: a projection reads columns of the scan.",
                    nameof(selector));
        }

        return new Projection(new ProjectionQuery(Host, elements));
    }
}
