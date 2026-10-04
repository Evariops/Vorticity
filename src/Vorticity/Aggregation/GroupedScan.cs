using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Aggregating;

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

    internal GroupedScan(Scan<TRecord> scan, TKey key, SymNode[] components)
    {
        _scan = scan;
        _key = key;
        _components = components;
        _keys = new ColumnShape[components.Length];
        for (int i = 0; i < components.Length; i++)
        {
            _keys[i] = new ColumnShape((ColumnSym)components[i]);
        }
    }

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

    private Group<TRecord, TKey> Group() => new Group<TRecord, TKey>(_scan.Binding, _key);

    private AggregationQuery Query(IResultNode[] nodes)
    {
        SymNode[] results = new SymNode[nodes.Length];
        for (int i = 0; i < nodes.Length; i++)
        {
            results[i] = (SymNode)nodes[i];
        }

        return new AggregationQuery(_scan.Host, new AggregationPlan(results, _keys), nodes, _keys);
    }
}

/// <summary>
/// The results of a grouped scan, one per group, computed in one pass when enumerated:
/// <c>await foreach (long count in aggregation)</c>.
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
    private readonly AggregationQuery _query;
    private CancellationToken _cancellationToken;

    internal Aggregation(AggregationQuery query) => _query = query;

    /// <summary>What the scan did; valid once the aggregation has been enumerated.</summary>
    public ScanStatistics Statistics => _query.Host.Statistics;

    /// <summary>What the aggregation computes, for the tests that hold two spellings of a query to one plan.</summary>
    internal AggregationPlan Plan => _query.Plan;

    /// <summary>Makes <paramref name="cancellationToken"/> cancel the enumeration, as <c>WithCancellation</c> does a stream's.</summary>
    /// <param name="cancellationToken">Cancels the scan at a batch boundary.</param>
    /// <returns>This aggregation.</returns>
    public Aggregation<TResult> WithCancellation(CancellationToken cancellationToken)
    {
        _cancellationToken = cancellationToken;
        return this;
    }

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
    public IAsyncEnumerator<TResult> GetAsyncEnumerator(CancellationToken cancellationToken = default) => Values(cancellationToken);

    /// <summary>The results as a stream, for the operators of <c>System.Linq.AsyncEnumerable</c>, which then run on the values delivered.</summary>
    /// <param name="cancellationToken">Cancels the scan at a batch boundary.</param>
    /// <returns>The results.</returns>
    public IAsyncEnumerable<TResult> ToValuesAsync(CancellationToken cancellationToken = default) => new ValueStream(this, cancellationToken);

    /// <summary>Runs the aggregation and collects the results, a batch at a time.</summary>
    /// <param name="cancellationToken">Cancels the scan at a batch boundary.</param>
    /// <returns>The results, in the order they are delivered.</returns>
    public async ValueTask<List<TResult>> ToListAsync(CancellationToken cancellationToken = default)
    {
        List<TResult> list = [];
        ValueEnumerator<TResult> values = Values(cancellationToken);
        await using (values.ConfigureAwait(false))
        {
            while (await values.NextBatchAsync().ConfigureAwait(false))
            {
                list.AddRange(values.Batch);
            }
        }

        return list;
    }

    /// <summary>Runs the aggregation and collects the results into an array, a batch at a time.</summary>
    /// <param name="cancellationToken">Cancels the scan at a batch boundary.</param>
    /// <returns>The results, in the order they are delivered.</returns>
    public async ValueTask<TResult[]> ToArrayAsync(CancellationToken cancellationToken = default) =>
        [.. await ToListAsync(cancellationToken).ConfigureAwait(false)];

    private ValueEnumerator<TResult> Values(CancellationToken cancellationToken)
    {
        CancellationTokenSource? linked = _cancellationToken.CanBeCanceled && cancellationToken.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(_cancellationToken, cancellationToken)
            : null;
        CancellationToken token = linked?.Token ?? (cancellationToken.CanBeCanceled ? cancellationToken : _cancellationToken);
        return new ValueEnumerator<TResult>(_query.Groups(token), _query.Columns[0].Record, _query.Session.Options.Extensions, linked);
    }

    /// <summary>The results as an <see cref="IAsyncEnumerable{T}"/>, enumerated once.</summary>
    private sealed class ValueStream(Aggregation<TResult> aggregation, CancellationToken cancellationToken) : IAsyncEnumerable<TResult>
    {
        public IAsyncEnumerator<TResult> GetAsyncEnumerator(CancellationToken token = default) =>
            aggregation.Values(token.CanBeCanceled ? token : cancellationToken);
    }
}

/// <summary>
/// The results of a grouped scan, several per group, computed in one pass and read through a
/// record: <c>Select(g =&gt; (g.Key, g.Count())).As&lt;CityCount&gt;()</c>.
/// </summary>
/// <remarks>
/// Several values have no .NET type until a record gives them one: the record names them, types
/// them, and makes the result a scan, read as batches or as records like a file's. An
/// <see cref="Aggregation"/> is therefore not enumerable; <see cref="As{TRecord}"/> is how it is read.
/// </remarks>
public sealed class Aggregation
{
    private readonly AggregationQuery _query;

    internal Aggregation(AggregationQuery query) => _query = query;

    /// <summary>What the scan did; valid once the result has been read.</summary>
    public ScanStatistics Statistics => _query.Host.Statistics;

    /// <summary>What the aggregation computes, for the tests that hold two spellings of a query to one plan.</summary>
    internal AggregationPlan Plan => _query.Plan;

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
    internal static Scan<TRecord> Scan<TRecord>(AggregationQuery query)
        where TRecord : IVortexRecord<TRecord>
    {
        AggregationQuery typed = query.As(TRecord.Schema, typeof(TRecord));
        return new Scan<TRecord>(new ResultScanSource(typed), typed.Metrics);
    }
}
