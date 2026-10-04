using System;
using System.Runtime.CompilerServices;
using Vorticity.Aggregating;

namespace Vorticity;

public sealed partial class Scan<TRecord>
{
    /// <summary>Groups the rows the scan keeps by one column; the group's key is its symbol, <c>g.Key</c>.</summary>
    /// <typeparam name="T">The key column's type.</typeparam>
    /// <param name="key">The key column.</param>
    /// <returns>The grouped scan.</returns>
    /// <remarks><c>group r by r.City into g</c> in a query is this.</remarks>
    public GroupedScan<TRecord, Sym<T>> GroupBy<T>(Func<Probe<TRecord>, Sym<T>> key)
    {
        ArgumentNullException.ThrowIfNull(key);
        Sym<T> symbol = key(new Probe<TRecord>(Binding));
        return new GroupedScan<TRecord, Sym<T>>(this, symbol, [KeyComponent(symbol, 0)]);
    }

    /// <summary>
    /// Groups the rows the scan keeps by a tuple of columns, of any length: <c>r =&gt; (r.City, r.Day)</c>.
    /// The group's key is the tuple itself, under the names it was given or inferred: <c>g.Key.City</c>.
    /// </summary>
    /// <typeparam name="TKey">The tuple of the key's symbols.</typeparam>
    /// <param name="keys">The key columns.</param>
    /// <returns>The grouped scan.</returns>
    /// <exception cref="ArgumentException">A component of the tuple is not a symbol of the scan's columns.</exception>
    /// <remarks>
    /// The tuple is taken whole so that its names are the key's: an overload per arity would bind
    /// first and lose them. <c>group r by (r.City, r.Day) into g</c> in a query is this.
    /// </remarks>
    public GroupedScan<TRecord, TKey> GroupBy<TKey>(Func<Probe<TRecord>, TKey> keys)
        where TKey : struct, ITuple
    {
        ArgumentNullException.ThrowIfNull(keys);
        TKey tuple = keys(new Probe<TRecord>(Binding));
        ITuple components = tuple;
        SymNode[] nodes = new SymNode[components.Length];
        for (int i = 0; i < nodes.Length; i++)
        {
            nodes[i] = components[i] is ISymbol symbol
                ? KeyComponent(symbol, i)
                : throw new ArgumentException(
                    $"Component {i + 1} of the key is not a symbol of the scan's columns but {components[i] ?? "null"}: a key is grouped by columns, r => (r.City, r.Day).",
                    nameof(keys));
        }

        return new GroupedScan<TRecord, TKey>(this, tuple, nodes);
    }

    /// <summary>The node of a key component, checked to be a column of a type that groups.</summary>
    private static SymNode KeyComponent(ISymbol symbol, int index)
    {
        if (symbol.Node is not ColumnSym column)
        {
            throw new ArgumentException(
                $"Component {index + 1} of the key, '{symbol.Node}', is not a column of the scan: an aggregate or a group's key is a result, not a key.");
        }

        ColumnShape shape = new ColumnShape(column);
        return shape.Kind != StorageKind.Unsupported ? column : throw shape.Unsupported("a group key");
    }
}
