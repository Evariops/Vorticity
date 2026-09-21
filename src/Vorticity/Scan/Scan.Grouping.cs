using System;
using Vorticity.Aggregating;

namespace Vorticity;

public sealed partial class Scan<TRecord>
{
    /// <summary>Groups the rows the scan keeps by one column, for <see cref="GroupedScan{TRecord, TKey}.AggAsync{T1}"/>.</summary>
    /// <typeparam name="TKey">The key column's type.</typeparam>
    /// <param name="key">The key column.</param>
    /// <returns>The grouped scan.</returns>
    public GroupedScan<TRecord, TKey> GroupBy<TKey>(Func<Probe<TRecord>, Sym<TKey>> key)
    {
        ArgumentNullException.ThrowIfNull(key);
        ColumnShape shape = KeyShape(key(new Probe<TRecord>(Binding)));
        return new GroupedScan<TRecord, TKey>(this, [shape], static keys => keys.Reader<TKey>(0));
    }

    /// <summary>Groups the rows the scan keeps by two columns; the key's components are <c>g.Key.Item1</c> and <c>g.Key.Item2</c>.</summary>
    /// <typeparam name="TKey1">The first key column's type.</typeparam>
    /// <typeparam name="TKey2">The second key column's type.</typeparam>
    /// <param name="keys">The key columns.</param>
    /// <returns>The grouped scan.</returns>
    public GroupedScan<TRecord, (TKey1, TKey2)> GroupBy<TKey1, TKey2>(Func<Probe<TRecord>, (Sym<TKey1>, Sym<TKey2>)> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        (Sym<TKey1> k1, Sym<TKey2> k2) = keys(new Probe<TRecord>(Binding));
        return new GroupedScan<TRecord, (TKey1, TKey2)>(this, [KeyShape(k1), KeyShape(k2)], static index =>
        {
            Func<int, TKey1> r1 = index.Reader<TKey1>(0);
            Func<int, TKey2> r2 = index.Reader<TKey2>(1);
            return g => (r1(g), r2(g));
        });
    }

    /// <summary>Groups the rows the scan keeps by three columns.</summary>
    /// <typeparam name="TKey1">The first key column's type.</typeparam>
    /// <typeparam name="TKey2">The second key column's type.</typeparam>
    /// <typeparam name="TKey3">The third key column's type.</typeparam>
    /// <param name="keys">The key columns.</param>
    /// <returns>The grouped scan.</returns>
    public GroupedScan<TRecord, (TKey1, TKey2, TKey3)> GroupBy<TKey1, TKey2, TKey3>(Func<Probe<TRecord>, (Sym<TKey1>, Sym<TKey2>, Sym<TKey3>)> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        (Sym<TKey1> k1, Sym<TKey2> k2, Sym<TKey3> k3) = keys(new Probe<TRecord>(Binding));
        return new GroupedScan<TRecord, (TKey1, TKey2, TKey3)>(this, [KeyShape(k1), KeyShape(k2), KeyShape(k3)], static index =>
        {
            Func<int, TKey1> r1 = index.Reader<TKey1>(0);
            Func<int, TKey2> r2 = index.Reader<TKey2>(1);
            Func<int, TKey3> r3 = index.Reader<TKey3>(2);
            return g => (r1(g), r2(g), r3(g));
        });
    }

    /// <summary>Groups the rows the scan keeps by four columns.</summary>
    /// <typeparam name="TKey1">The first key column's type.</typeparam>
    /// <typeparam name="TKey2">The second key column's type.</typeparam>
    /// <typeparam name="TKey3">The third key column's type.</typeparam>
    /// <typeparam name="TKey4">The fourth key column's type.</typeparam>
    /// <param name="keys">The key columns.</param>
    /// <returns>The grouped scan.</returns>
    public GroupedScan<TRecord, (TKey1, TKey2, TKey3, TKey4)> GroupBy<TKey1, TKey2, TKey3, TKey4>(
        Func<Probe<TRecord>, (Sym<TKey1>, Sym<TKey2>, Sym<TKey3>, Sym<TKey4>)> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        (Sym<TKey1> k1, Sym<TKey2> k2, Sym<TKey3> k3, Sym<TKey4> k4) = keys(new Probe<TRecord>(Binding));
        return new GroupedScan<TRecord, (TKey1, TKey2, TKey3, TKey4)>(this, [KeyShape(k1), KeyShape(k2), KeyShape(k3), KeyShape(k4)], static index =>
        {
            Func<int, TKey1> r1 = index.Reader<TKey1>(0);
            Func<int, TKey2> r2 = index.Reader<TKey2>(1);
            Func<int, TKey3> r3 = index.Reader<TKey3>(2);
            Func<int, TKey4> r4 = index.Reader<TKey4>(3);
            return g => (r1(g), r2(g), r3(g), r4(g));
        });
    }

    private static ColumnShape KeyShape<T>(Sym<T> key)
    {
        ColumnShape shape = new ColumnShape(key.Column);
        return shape.Kind != StorageKind.Unsupported ? shape : throw shape.Unsupported("a group key");
    }
}
