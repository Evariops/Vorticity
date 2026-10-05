using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Aggregating;
using Vorticity.Expressions;

namespace Vorticity;

/// <summary>A scan grouped by a key, waiting for the results of each group: <c>scan.GroupBy(r =&gt; r.City).Select(g =&gt; (g.Key, g.Count()))</c>.</summary>
/// <typeparam name="TRecord">The record the scan is typed by.</typeparam>
/// <typeparam name="TKey">The key: the symbol of one column, or the tuple of symbols of a composite key.</typeparam>
/// <remarks>
/// A key read from a dictionary block groups by code, a key the statistics say is sorted groups by
/// run, any other key by hash; the groups arrive in key order, nulls last, in the first two cases,
/// and in an unspecified order otherwise. The memory is one state per group.
/// </remarks>
public sealed class GroupedScan<TRecord, TKey>
    where TRecord : IVortexRecord<TRecord>
{
    private readonly Scan<TRecord> _scan;
    private readonly TKey _key;
    private readonly SymNode[] _components;
    private readonly ColumnShape[] _keys;
    private readonly VortexExpr? _rows;
    private readonly GroupOperator[] _operators;

    internal GroupedScan(Scan<TRecord> scan, TKey key, SymNode[] components)
        : this(scan, key, components, Shapes(components), null, [])
    {
    }

    private GroupedScan(Scan<TRecord> scan, TKey key, SymNode[] components, ColumnShape[] keys, VortexExpr? rows, GroupOperator[] operators)
    {
        _scan = scan;
        _key = key;
        _components = components;
        _keys = keys;
        _rows = rows;
        _operators = operators;
    }

    /// <summary>
    /// Keeps the groups <paramref name="predicate"/> is true for: <c>Where(g =&gt; g.Count() &gt; 10)</c>,
    /// <c>where g.Count() &gt; 10</c> in a query. A conjunct that names only components of the key,
    /// before any order, <c>Skip</c> or <c>Take</c>, filters the rows instead, and prunes as their
    /// filter does.
    /// </summary>
    /// <param name="predicate">A lambda over the group, comparing its key's components and its aggregates; run once, now.</param>
    /// <returns>The groups kept.</returns>
    /// <exception cref="InvalidOperationException">The predicate compares a column that is neither a component of the key nor an aggregate, or a custom aggregator's state.</exception>
    public GroupedScan<TRecord, TKey> Where(Func<Group<TRecord, TKey>, Predicate> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        Predicate kept = predicate(Group());
        if (kept.IsAll)
        {
            return this;
        }

        if (kept.IsNone)
        {
            return With(_rows, new GroupWindow(0, 0));
        }

        // A conjunct on the key alone keeps whole groups, which its rows' filter keeps too, as long
        // as no order or window has chosen among the groups before it.
        bool rowsDecide = Array.TrueForAll(_operators, op => op is GroupFilter);
        List<VortexExpr> conjuncts = [];
        GroupPredicates.Conjuncts(kept.Node!, conjuncts);
        List<VortexExpr> rows = [];
        List<VortexExpr> results = [];
        List<FieldExpr> fields = [];
        foreach (VortexExpr conjunct in conjuncts)
        {
            fields.Clear();
            GroupPredicates.Fields(conjunct, fields);
            bool onResults = false;
            foreach (FieldExpr field in fields)
            {
                onResults |= field is ResultFieldExpr;
                if (field is not ResultFieldExpr && ComponentOf(field) < 0)
                {
                    throw new InvalidOperationException(
                        $"'{field.Path}' is a column of the rows, neither a component of the key nor an aggregate: a filter on groups compares the group's results.");
                }
            }

            if (!onResults && rowsDecide)
            {
                rows.Add(conjunct);
            }
            else
            {
                results.Add(GroupPredicates.Rewrite(conjunct, field => field as ResultFieldExpr ?? KeyField(ComponentOf(field))));
            }
        }

        VortexExpr? pushed = GroupPredicates.And(_rows, rows);
        if (results.Count == 0)
        {
            return new GroupedScan<TRecord, TKey>(_scan, _key, _components, _keys, pushed, _operators);
        }

        VortexExpr filter = GroupPredicates.And(null, results)!;
        fields.Clear();
        GroupPredicates.Fields(filter, fields);
        List<ResultFieldExpr> read = [];
        foreach (FieldExpr field in fields)
        {
            if (field is ResultFieldExpr result && !read.Exists(known => known.Path == result.Path))
            {
                read.Add(result);
            }
        }

        return With(pushed, new GroupFilter(filter, [.. read]));
    }

    /// <summary>The groups past the first <paramref name="count"/>, in the order they come at this point.</summary>
    /// <param name="count">The groups to pass over.</param>
    /// <returns>The groups kept.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is negative.</exception>
    /// <remarks>
    /// Without an order before it, on a key that does not stream, which groups it passes over is
    /// not promised: on several lanes, the groups come in the order the lanes met them.
    /// </remarks>
    public GroupedScan<TRecord, TKey> Skip(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        return With(_rows, new GroupWindow(count, long.MaxValue));
    }

    /// <summary>The first <paramref name="count"/> groups, in the order they come at this point.</summary>
    /// <param name="count">The groups to keep at most.</param>
    /// <returns>The groups kept.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is negative.</exception>
    /// <remarks>
    /// Without an order before it, on a key that does not stream, which groups it keeps is not
    /// promised: on several lanes, the groups come in the order the lanes met them, which their
    /// queue of ranges decides. An order makes them the first: <c>OrderBy(g =&gt; g.Key).Take(n)</c>.
    /// </remarks>
    public GroupedScan<TRecord, TKey> Take(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        return With(_rows, new GroupWindow(0, count));
    }

    /// <summary>The groups in the ascending order of <paramref name="key"/>, nulls last: <c>orderby g.Count()</c> in a query.</summary>
    /// <typeparam name="T">The result's type.</typeparam>
    /// <param name="key">A lambda over the group, returning a component of its key or an aggregate.</param>
    /// <returns>The groups in order; ties are broken by <c>ThenBy</c>, then by the group's key.</returns>
    /// <exception cref="ArgumentException">The result has no order: a custom aggregator's state.</exception>
    /// <exception cref="InvalidOperationException">The result is neither a component of the key nor an aggregate.</exception>
    public OrderedGroupedScan<TRecord, TKey> OrderBy<T>(Func<Group<TRecord, TKey>, Sym<T>> key) => Ordered(Keys(key, descending: false), first: true);

    /// <summary>The groups in the descending order of <paramref name="key"/>, nulls last: <c>orderby g.Count() descending</c> in a query.</summary>
    /// <typeparam name="T">The result's type.</typeparam>
    /// <param name="key">A lambda over the group, returning a component of its key or an aggregate.</param>
    /// <returns>The groups in order; ties are broken by <c>ThenBy</c>, then by the group's key.</returns>
    /// <exception cref="ArgumentException">The result has no order: a custom aggregator's state.</exception>
    /// <exception cref="InvalidOperationException">The result is neither a component of the key nor an aggregate.</exception>
    public OrderedGroupedScan<TRecord, TKey> OrderByDescending<T>(Func<Group<TRecord, TKey>, Sym<T>> key) => Ordered(Keys(key, descending: true), first: true);

    /// <summary>The groups in the ascending order of the elements of a tuple, the first deciding: <c>orderby g.Key</c> on a composite key.</summary>
    /// <param name="keys">A lambda over the group, returning a tuple of components of its key and aggregates.</param>
    /// <returns>The groups in order.</returns>
    /// <exception cref="ArgumentException">An element is not a symbol, or has no order.</exception>
    /// <exception cref="InvalidOperationException">An element is neither a component of the key nor an aggregate.</exception>
    public OrderedGroupedScan<TRecord, TKey> OrderBy(Func<Group<TRecord, TKey>, ITuple> keys) => Ordered(Keys(keys, descending: false), first: true);

    /// <summary>The groups in the descending order of the elements of a tuple, the first deciding.</summary>
    /// <param name="keys">A lambda over the group, returning a tuple of components of its key and aggregates.</param>
    /// <returns>The groups in order.</returns>
    /// <exception cref="ArgumentException">An element is not a symbol, or has no order.</exception>
    /// <exception cref="InvalidOperationException">An element is neither a component of the key nor an aggregate.</exception>
    public OrderedGroupedScan<TRecord, TKey> OrderByDescending(Func<Group<TRecord, TKey>, ITuple> keys) => Ordered(Keys(keys, descending: true), first: true);

    /// <summary>One result per group: <c>Select(g =&gt; g.Count())</c>.</summary>
    /// <typeparam name="T">The result's type.</typeparam>
    /// <param name="result">A lambda over the group, returning its key, a component of it, or an aggregate.</param>
    /// <returns>The groups' results, computed when enumerated.</returns>
    /// <exception cref="InvalidOperationException">The lambda returns a symbol that is neither a key nor an aggregate of the group.</exception>
    /// <exception cref="VortexSchemaException">The result is a custom state no column holds.</exception>
    public Aggregation<T> Select<T>(Func<Group<TRecord, TKey>, Sym<T>> result)
    {
        ArgumentNullException.ThrowIfNull(result);
        IResultNode node = AggregationPlan.Result(result(Group()), _components);
        return new Aggregation<T>(Query([node]));
    }

    /// <summary>
    /// Several results per group, computed in one pass and read through a record:
    /// <c>Select(g =&gt; (g.Key, g.Count(), g.Average(x =&gt; x.Celsius))).As&lt;CityMean&gt;()</c>.
    /// </summary>
    /// <param name="results">A lambda over the group, returning a tuple of any length of its key's components and its aggregates.</param>
    /// <returns>The groups' results, which <see cref="Aggregation.As{TRecord}"/> reads.</returns>
    /// <exception cref="ArgumentException">An element of the tuple is not a symbol.</exception>
    /// <exception cref="InvalidOperationException">An element is neither a key nor an aggregate of the group.</exception>
    /// <exception cref="VortexSchemaException">An element is a custom state no column holds.</exception>
    public Aggregation Select(Func<Group<TRecord, TKey>, ITuple> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        return new Aggregation(Query(Elements(results(Group()), _components)));
    }

    /// <summary>The results of a tuple a selection's lambda returned, in order.</summary>
    internal static IResultNode[] Elements(ITuple? tuple, SymNode[] components)
    {
        ArgumentNullException.ThrowIfNull(tuple);
        IResultNode[] nodes = new IResultNode[tuple.Length];
        for (int i = 0; i < nodes.Length; i++)
        {
            nodes[i] = tuple[i] is ISymbol symbol
                ? symbol.Result(components)
                : throw new ArgumentException(
                    $"Element {i + 1} of the selection is not a symbol but {tuple[i]?.GetType().Name ?? "null"}: a result is a key or an aggregate of the group.");
        }

        return nodes;
    }

    private static ColumnShape[] Shapes(SymNode[] components)
    {
        ColumnShape[] keys = new ColumnShape[components.Length];
        for (int i = 0; i < components.Length; i++)
        {
            keys[i] = new ColumnShape((ColumnSym)components[i]);
        }

        return keys;
    }

    private Group<TRecord, TKey> Group() => new Group<TRecord, TKey>(_scan.Binding, _key);

    /// <summary>A first order, or the next key of the order the last operator is.</summary>
    internal OrderedGroupedScan<TRecord, TKey> Ordered(OrderKey[] keys, bool first)
    {
        if (first || _operators.Length == 0 || _operators[^1] is not GroupOrder previous)
        {
            return new OrderedGroupedScan<TRecord, TKey>(With(_rows, new GroupOrder(keys)));
        }

        GroupOperator[] operators = [.. _operators];
        operators[^1] = new GroupOrder([.. previous.Keys, .. keys]);
        return new OrderedGroupedScan<TRecord, TKey>(new GroupedScan<TRecord, TKey>(_scan, _key, _components, _keys, _rows, operators));
    }

    internal OrderKey[] Keys<T>(Func<Group<TRecord, TKey>, Sym<T>> key, bool descending)
    {
        ArgumentNullException.ThrowIfNull(key);
        return [new OrderKey(OrderField(key(Group())), descending)];
    }

    internal OrderKey[] Keys(Func<Group<TRecord, TKey>, ITuple> keys, bool descending)
    {
        ArgumentNullException.ThrowIfNull(keys);
        ITuple tuple = keys(Group()) ?? throw new ArgumentException("The lambda returned no tuple.", nameof(keys));
        OrderKey[] order = new OrderKey[tuple.Length];
        for (int i = 0; i < order.Length; i++)
        {
            order[i] = new OrderKey(
                OrderField(tuple[i] as ISymbol ?? throw new ArgumentException($"Element {i + 1} of the order is not a symbol but {tuple[i]?.GetType().Name ?? "null"}.", nameof(keys))),
                descending);
        }

        return order;
    }

    /// <summary>The column of the groups' results an order key reads: an aggregate's, or a component of the key's.</summary>
    private ResultFieldExpr OrderField(ISymbol symbol)
    {
        if (symbol.Node is IAggregateNode { Kind: AggregateKind.Custom } custom)
        {
            throw new ArgumentException($"'{custom}' is the state of a custom aggregator, which has no order: order by a value a built-in aggregate delivers.");
        }

        FieldExpr field = symbol.Compared.Field;
        if (field is ResultFieldExpr result)
        {
            return result;
        }

        int component = ComponentOf(field);
        return component >= 0
            ? KeyField(component)
            : throw new InvalidOperationException(
                $"'{field.Path}' is a column of the rows, neither a component of the key nor an aggregate: groups are ordered by their results.");
    }

    private GroupedScan<TRecord, TKey> With(VortexExpr? rows, GroupOperator next) =>
        new GroupedScan<TRecord, TKey>(_scan, _key, _components, _keys, rows, [.. _operators, next]);

    /// <summary>The component of the key <paramref name="field"/> reads, or -1.</summary>
    private int ComponentOf(FieldExpr field)
    {
        for (int i = 0; i < _components.Length; i++)
        {
            if (string.Equals(((ColumnSym)_components[i]).Field.Path, field.Path, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    private static ResultFieldExpr KeyField(int component) => new ResultFieldExpr($"$key{component}", null, component);

    private AggregationQuery Query(IResultNode[] nodes)
    {
        // Every aggregate the operators read is computed with the selection's, and not delivered.
        List<SymNode> results = [];
        foreach (IResultNode node in nodes)
        {
            results.Add((SymNode)node);
        }

        foreach (GroupOperator op in _operators)
        {
            if (op is GroupFilter filter)
            {
                foreach (ResultFieldExpr field in filter.Fields)
                {
                    if (field.Result is SymNode aggregate)
                    {
                        results.Add(aggregate);
                    }
                }
            }
            else if (op is GroupOrder order)
            {
                foreach (OrderKey key in order.Keys)
                {
                    if (key.Field.Result is SymNode aggregate)
                    {
                        results.Add(aggregate);
                    }
                }
            }
        }

        return new AggregationQuery(_scan.Host, new AggregationPlan([.. results], _keys), nodes, _keys, _rows, _operators);
    }
}

/// <summary>
/// A scan grouped by a key whose groups are in an order: <c>OrderBy(g =&gt; g.Key.Hour).ThenByDescending(g =&gt; g.Count())</c>.
/// </summary>
/// <typeparam name="TRecord">The record the scan is typed by.</typeparam>
/// <typeparam name="TKey">The key: the symbol of one column, or the tuple of symbols of a composite key.</typeparam>
/// <remarks>
/// A result orders as its type: numbers by value, −0.0 equal to +0.0 and NaN after +∞, text and
/// binary bytewise, <c>false</c> before <c>true</c>, temporal values in time; a null comes last in
/// both directions. Ties past the last key are broken by the group's key, so the order is the same
/// at every degree of parallelism. An order followed by <c>Take</c> ranks the groups in a heap of
/// the ones it keeps.
/// </remarks>
public sealed class OrderedGroupedScan<TRecord, TKey>
    where TRecord : IVortexRecord<TRecord>
{
    private readonly GroupedScan<TRecord, TKey> _grouped;

    internal OrderedGroupedScan(GroupedScan<TRecord, TKey> grouped) => _grouped = grouped;

    /// <summary>Breaks the ties of the order so far by the ascending order of <paramref name="key"/>.</summary>
    /// <typeparam name="T">The result's type.</typeparam>
    /// <param name="key">A lambda over the group, returning a component of its key or an aggregate.</param>
    /// <returns>The groups in order.</returns>
    /// <exception cref="ArgumentException">The result has no order.</exception>
    /// <exception cref="InvalidOperationException">The result is neither a component of the key nor an aggregate.</exception>
    public OrderedGroupedScan<TRecord, TKey> ThenBy<T>(Func<Group<TRecord, TKey>, Sym<T>> key) => _grouped.Ordered(_grouped.Keys(key, descending: false), first: false);

    /// <summary>Breaks the ties of the order so far by the descending order of <paramref name="key"/>.</summary>
    /// <typeparam name="T">The result's type.</typeparam>
    /// <param name="key">A lambda over the group, returning a component of its key or an aggregate.</param>
    /// <returns>The groups in order.</returns>
    /// <exception cref="ArgumentException">The result has no order.</exception>
    /// <exception cref="InvalidOperationException">The result is neither a component of the key nor an aggregate.</exception>
    public OrderedGroupedScan<TRecord, TKey> ThenByDescending<T>(Func<Group<TRecord, TKey>, Sym<T>> key) => _grouped.Ordered(_grouped.Keys(key, descending: true), first: false);

    /// <summary>Breaks the ties of the order so far by the ascending order of the elements of a tuple.</summary>
    /// <param name="keys">A lambda over the group, returning a tuple of components of its key and aggregates.</param>
    /// <returns>The groups in order.</returns>
    public OrderedGroupedScan<TRecord, TKey> ThenBy(Func<Group<TRecord, TKey>, ITuple> keys) => _grouped.Ordered(_grouped.Keys(keys, descending: false), first: false);

    /// <summary>Breaks the ties of the order so far by the descending order of the elements of a tuple.</summary>
    /// <param name="keys">A lambda over the group, returning a tuple of components of its key and aggregates.</param>
    /// <returns>The groups in order.</returns>
    public OrderedGroupedScan<TRecord, TKey> ThenByDescending(Func<Group<TRecord, TKey>, ITuple> keys) => _grouped.Ordered(_grouped.Keys(keys, descending: true), first: false);

    /// <summary>Keeps the groups <paramref name="predicate"/> is true for, in the order so far.</summary>
    /// <param name="predicate">A lambda over the group, comparing its key's components and its aggregates.</param>
    /// <returns>The groups kept, in order.</returns>
    public GroupedScan<TRecord, TKey> Where(Func<Group<TRecord, TKey>, Predicate> predicate) => _grouped.Where(predicate);

    /// <summary>The groups past the first <paramref name="count"/> in the order.</summary>
    /// <param name="count">The groups to pass over.</param>
    /// <returns>The groups kept, in order.</returns>
    public GroupedScan<TRecord, TKey> Skip(int count) => _grouped.Skip(count);

    /// <summary>The first <paramref name="count"/> groups in the order: a top-k, ranked in a heap of them.</summary>
    /// <param name="count">The groups to keep at most.</param>
    /// <returns>The groups kept, in order.</returns>
    public GroupedScan<TRecord, TKey> Take(int count) => _grouped.Take(count);

    /// <summary>One result per group, in the order: <c>Select(g =&gt; g.Count())</c>.</summary>
    /// <typeparam name="T">The result's type.</typeparam>
    /// <param name="result">A lambda over the group, returning its key, a component of it, or an aggregate.</param>
    /// <returns>The groups' results, computed when enumerated.</returns>
    public Aggregation<T> Select<T>(Func<Group<TRecord, TKey>, Sym<T>> result) => _grouped.Select(result);

    /// <summary>Several results per group, in the order, read through a record.</summary>
    /// <param name="results">A lambda over the group, returning a tuple of any length of its key's components and its aggregates.</param>
    /// <returns>The groups' results, which <see cref="Aggregation.As{TRecord}"/> reads.</returns>
    public Aggregation Select(Func<Group<TRecord, TKey>, ITuple> results) => _grouped.Select(results);
}

/// <summary>
/// The results of a query, one value each, computed when enumerated: a group's of a grouped scan,
/// <c>await foreach (long count in scan.GroupBy(r =&gt; r.City).Select(g =&gt; g.Count()))</c>, or the
/// distinct values of a projection.
/// </summary>
/// <typeparam name="TResult">The result of one group.</typeparam>
/// <remarks>
/// Enumerated once, like the scan it runs, and by the pattern of <c>await foreach</c> rather than
/// as an <see cref="IAsyncEnumerable{T}"/>, as a scan is: what returns a stream to await carries
/// <c>Async</c> in its name, and <c>Select</c> builds a query. <see cref="ToValuesAsync"/> hands the
/// results to <c>System.Linq.AsyncEnumerable</c>. The results come as batches of the result's
/// column, each read into values once, so that a batch costs an <c>await</c> and a value none.
/// </remarks>
public sealed class Aggregation<TResult>
{
    private readonly ResultQuery _query;
    private CancellationToken _cancellationToken;

    internal Aggregation(ResultQuery query) => _query = query;

    /// <summary>What the scan did; valid once the aggregation has been enumerated.</summary>
    public ScanStatistics Statistics => ScanStatistics.From(_query.Metrics);

    /// <summary>What the aggregation computes, for the tests that hold two spellings of a query to one plan.</summary>
    internal AggregationPlan Plan => ((AggregationQuery)_query).Plan;

    /// <summary>The query, for the tests that read what its run held.</summary>
    internal ResultQuery Query => _query;

    /// <summary>Makes <paramref name="cancellationToken"/> cancel the enumeration, as <c>WithCancellation</c> does a stream's.</summary>
    /// <param name="cancellationToken">Cancels the scan at a batch boundary.</param>
    /// <returns>This aggregation.</returns>
    public Aggregation<TResult> WithCancellation(CancellationToken cancellationToken)
    {
        _cancellationToken = cancellationToken;
        return this;
    }

    /// <summary>The results past the first <paramref name="count"/>, in the order they are delivered.</summary>
    /// <param name="count">The results to pass over.</param>
    /// <returns>The same query, which delivers fewer.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is negative.</exception>
    public Aggregation<TResult> Skip(int count) => new Aggregation<TResult>(_query.Limit(count, long.MaxValue));

    /// <summary>The first <paramref name="count"/> results, in the order they are delivered; the query stops reading once they are, where its input streams.</summary>
    /// <param name="count">The results to deliver at most.</param>
    /// <returns>The same query, which delivers fewer.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is negative.</exception>
    public Aggregation<TResult> Take(int count) => new Aggregation<TResult>(_query.Limit(0, count));

    /// <summary>The result as a scan of a record of one member, which takes the value: <c>Select(g =&gt; g.Count()).As&lt;Total&gt;()</c>.</summary>
    /// <typeparam name="TRecord">The record, whose one member is of the result's type or its nullable form.</typeparam>
    /// <returns>A scan of the result, which runs the query when its sink runs.</returns>
    /// <exception cref="VortexSchemaException">The record has another number of members, or its member does not take the result.</exception>
    public Scan<TRecord> As<TRecord>()
        where TRecord : IVortexRecord<TRecord> =>
        Aggregation.Scan<TRecord>(_query);

    /// <summary>What the pass will read, from statistics, zone maps and indexes, without reading a data segment.</summary>
    /// <param name="cancellationToken">Cancels the reads of the structures consulted.</param>
    /// <returns>The plan.</returns>
    public ValueTask<ScanPlan> ExplainAsync(CancellationToken cancellationToken = default) =>
        _query.ExplainAsync(cancellationToken);

    /// <summary>Runs the aggregation, then enumerates the groups' results.</summary>
    /// <param name="cancellationToken">Cancels the scan at a batch boundary, with the token <see cref="WithCancellation"/> gave.</param>
    /// <returns>The enumerator.</returns>
    public IAsyncEnumerator<TResult> GetAsyncEnumerator(CancellationToken cancellationToken = default) =>
        ValueEnumerator<TResult>.Of(_query, _cancellationToken, cancellationToken);

    /// <summary>The results as a stream, for the operators of <c>System.Linq.AsyncEnumerable</c>, which then run on the values delivered.</summary>
    /// <param name="cancellationToken">Cancels the scan at a batch boundary.</param>
    /// <returns>The results.</returns>
    public IAsyncEnumerable<TResult> ToValuesAsync(CancellationToken cancellationToken = default) =>
        new ValueStream<TResult>(_query, _cancellationToken, cancellationToken);

    /// <summary>Runs the aggregation and collects the results, a batch at a time.</summary>
    /// <param name="cancellationToken">Cancels the scan at a batch boundary.</param>
    /// <returns>The results, in the order they are delivered.</returns>
    public ValueTask<List<TResult>> ToListAsync(CancellationToken cancellationToken = default) =>
        ValueStream<TResult>.ListAsync(ValueEnumerator<TResult>.Of(_query, _cancellationToken, cancellationToken));

    /// <summary>Runs the aggregation and collects the results into an array, a batch at a time.</summary>
    /// <param name="cancellationToken">Cancels the scan at a batch boundary.</param>
    /// <returns>The results, in the order they are delivered.</returns>
    public async ValueTask<TResult[]> ToArrayAsync(CancellationToken cancellationToken = default) =>
        [.. await ToListAsync(cancellationToken).ConfigureAwait(false)];
}

/// <summary>
/// The results of a query, several values each, computed when read through a record: a grouped
/// scan's, <c>Select(g =&gt; (g.Key, g.Count())).As&lt;CityCount&gt;()</c>, or the distinct tuples of
/// a projection.
/// </summary>
/// <remarks>
/// Several values have no .NET type until a record gives them one: the record names them, types
/// them, and makes the result a scan, read as batches or as records like a file's. An
/// <see cref="Aggregation"/> is therefore not enumerable; <see cref="As{TRecord}"/> is how it is read.
/// </remarks>
public sealed class Aggregation
{
    private readonly ResultQuery _query;

    internal Aggregation(ResultQuery query) => _query = query;

    /// <summary>The query, for the tests that read what its run held.</summary>
    internal ResultQuery Query => _query;

    /// <summary>What the scan did; valid once the result has been read.</summary>
    public ScanStatistics Statistics => ScanStatistics.From(_query.Metrics);

    /// <summary>What the aggregation computes, for the tests that hold two spellings of a query to one plan.</summary>
    internal AggregationPlan Plan => ((AggregationQuery)_query).Plan;

    /// <summary>The results past the first <paramref name="count"/>, in the order they are delivered.</summary>
    /// <param name="count">The results to pass over.</param>
    /// <returns>The same query, which delivers fewer.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is negative.</exception>
    public Aggregation Skip(int count) => new Aggregation(_query.Limit(count, long.MaxValue));

    /// <summary>The first <paramref name="count"/> results, in the order they are delivered; the query stops reading once they are, where its input streams.</summary>
    /// <param name="count">The results to deliver at most.</param>
    /// <returns>The same query, which delivers fewer.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is negative.</exception>
    public Aggregation Take(int count) => new Aggregation(_query.Limit(0, count));

    /// <summary>
    /// The result as a scan of <typeparamref name="TRecord"/>, whose members take the elements of the
    /// selection in order: <c>Select(g =&gt; (g.Key, g.Count())).As&lt;CityCount&gt;()</c>.
    /// </summary>
    /// <typeparam name="TRecord">The record, whose members, in declaration order, are of the elements' types or their nullable forms.</typeparam>
    /// <returns>A scan of the result, which runs the query when its sink runs: batches, records, or another query.</returns>
    /// <exception cref="VortexSchemaException">The record has another number of members, or a member does not take its element.</exception>
    public Scan<TRecord> As<TRecord>()
        where TRecord : IVortexRecord<TRecord> =>
        Scan<TRecord>(_query);

    /// <summary>What the pass will read, from statistics, zone maps and indexes, without reading a data segment.</summary>
    /// <param name="cancellationToken">Cancels the reads of the structures consulted.</param>
    /// <returns>The plan.</returns>
    public ValueTask<ScanPlan> ExplainAsync(CancellationToken cancellationToken = default) =>
        _query.ExplainAsync(cancellationToken);

    /// <summary>A scan of a query's result, typed by <typeparamref name="TRecord"/>.</summary>
    internal static Scan<TRecord> Scan<TRecord>(ResultQuery query)
        where TRecord : IVortexRecord<TRecord>
    {
        ResultQuery typed = query.As(TRecord.Schema, typeof(TRecord));
        return new Scan<TRecord>(new ResultScanSource(typed), typed.Metrics);
    }
}

/// <summary>The values of a result of one value as an <see cref="IAsyncEnumerable{T}"/>, for <c>System.Linq</c>.</summary>
internal sealed class ValueStream<T>(ResultQuery query, CancellationToken own, CancellationToken given) : IAsyncEnumerable<T>
{
    public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default) =>
        ValueEnumerator<T>.Of(query, own, cancellationToken.CanBeCanceled ? cancellationToken : given);

    /// <summary>Collects every value, a batch at a time: an await per batch, none per value.</summary>
    internal static async ValueTask<List<T>> ListAsync(ValueEnumerator<T> values)
    {
        List<T> list = [];
        await using (values.ConfigureAwait(false))
        {
            while (await values.NextBatchAsync().ConfigureAwait(false))
            {
                list.AddRange(values.Batch);
            }
        }

        return list;
    }
}
