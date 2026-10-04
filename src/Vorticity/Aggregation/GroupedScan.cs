using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Aggregating;

namespace Vorticity;

/// <summary>A scan grouped by a key, waiting for the aggregates of each group: <c>scan.GroupBy(r =&gt; r.City).Select(g =&gt; (g.Key, g.Count()))</c>.</summary>
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

    /// <summary>One answer per group.</summary>
    /// <typeparam name="T1">The answer's type.</typeparam>
    /// <param name="aggregate">A lambda over the group, returning its key or an aggregate.</param>
    /// <returns>The groups' answers, computed when enumerated.</returns>
    public Aggregation<T1> Select<T1>(Func<Group<TRecord, TKey>, Sym<T1>> aggregate)
    {
        ArgumentNullException.ThrowIfNull(aggregate);
        ResultNode<T1> n1 = Result(aggregate(Group()));
        return new Aggregation<T1>(new AggregationQuery(_scan.Host, Plan([n1]), [n1.Column("Item1", _keys)]));
    }

    /// <summary>Two answers per group, computed in one pass.</summary>
    /// <typeparam name="T1">The first answer's type.</typeparam>
    /// <typeparam name="T2">The second answer's type.</typeparam>
    /// <param name="aggregates">A lambda over the group, returning its key or aggregates.</param>
    /// <returns>The groups' answers, computed when enumerated.</returns>
    public Aggregation<(T1, T2)> Select<T1, T2>(Func<Group<TRecord, TKey>, (Sym<T1>, Sym<T2>)> aggregates)
    {
        ArgumentNullException.ThrowIfNull(aggregates);
        (Sym<T1> s1, Sym<T2> s2) = aggregates(Group());
        ResultNode<T1> n1 = Result(s1);
        ResultNode<T2> n2 = Result(s2);
        return new Aggregation<(T1, T2)>(_scan.Host, Plan([n1, n2]), outcome =>
        {
            Func<int, T1> r1 = n1.Bind(outcome);
            Func<int, T2> r2 = n2.Bind(outcome);
            return g => (r1(g), r2(g));
        });
    }

    /// <summary>Three answers per group, computed in one pass.</summary>
    /// <typeparam name="T1">The first answer's type.</typeparam>
    /// <typeparam name="T2">The second answer's type.</typeparam>
    /// <typeparam name="T3">The third answer's type.</typeparam>
    /// <param name="aggregates">A lambda over the group, returning its key or aggregates.</param>
    /// <returns>The groups' answers, computed when enumerated.</returns>
    public Aggregation<(T1, T2, T3)> Select<T1, T2, T3>(Func<Group<TRecord, TKey>, (Sym<T1>, Sym<T2>, Sym<T3>)> aggregates)
    {
        ArgumentNullException.ThrowIfNull(aggregates);
        (Sym<T1> s1, Sym<T2> s2, Sym<T3> s3) = aggregates(Group());
        ResultNode<T1> n1 = Result(s1);
        ResultNode<T2> n2 = Result(s2);
        ResultNode<T3> n3 = Result(s3);
        return new Aggregation<(T1, T2, T3)>(_scan.Host, Plan([n1, n2, n3]), outcome =>
        {
            Func<int, T1> r1 = n1.Bind(outcome);
            Func<int, T2> r2 = n2.Bind(outcome);
            Func<int, T3> r3 = n3.Bind(outcome);
            return g => (r1(g), r2(g), r3(g));
        });
    }

    /// <summary>Four answers per group, computed in one pass.</summary>
    /// <typeparam name="T1">The first answer's type.</typeparam>
    /// <typeparam name="T2">The second answer's type.</typeparam>
    /// <typeparam name="T3">The third answer's type.</typeparam>
    /// <typeparam name="T4">The fourth answer's type.</typeparam>
    /// <param name="aggregates">A lambda over the group, returning its key or aggregates.</param>
    /// <returns>The groups' answers, computed when enumerated.</returns>
    public Aggregation<(T1, T2, T3, T4)> Select<T1, T2, T3, T4>(Func<Group<TRecord, TKey>, (Sym<T1>, Sym<T2>, Sym<T3>, Sym<T4>)> aggregates)
    {
        ArgumentNullException.ThrowIfNull(aggregates);
        (Sym<T1> s1, Sym<T2> s2, Sym<T3> s3, Sym<T4> s4) = aggregates(Group());
        ResultNode<T1> n1 = Result(s1);
        ResultNode<T2> n2 = Result(s2);
        ResultNode<T3> n3 = Result(s3);
        ResultNode<T4> n4 = Result(s4);
        return new Aggregation<(T1, T2, T3, T4)>(_scan.Host, Plan([n1, n2, n3, n4]), outcome =>
        {
            Func<int, T1> r1 = n1.Bind(outcome);
            Func<int, T2> r2 = n2.Bind(outcome);
            Func<int, T3> r3 = n3.Bind(outcome);
            Func<int, T4> r4 = n4.Bind(outcome);
            return g => (r1(g), r2(g), r3(g), r4(g));
        });
    }

    /// <summary>Five answers per group, computed in one pass.</summary>
    /// <typeparam name="T1">The first answer's type.</typeparam>
    /// <typeparam name="T2">The second answer's type.</typeparam>
    /// <typeparam name="T3">The third answer's type.</typeparam>
    /// <typeparam name="T4">The fourth answer's type.</typeparam>
    /// <typeparam name="T5">The fifth answer's type.</typeparam>
    /// <param name="aggregates">A lambda over the group, returning its key or aggregates.</param>
    /// <returns>The groups' answers, computed when enumerated.</returns>
    public Aggregation<(T1, T2, T3, T4, T5)> Select<T1, T2, T3, T4, T5>(Func<Group<TRecord, TKey>, (Sym<T1>, Sym<T2>, Sym<T3>, Sym<T4>, Sym<T5>)> aggregates)
    {
        ArgumentNullException.ThrowIfNull(aggregates);
        (Sym<T1> s1, Sym<T2> s2, Sym<T3> s3, Sym<T4> s4, Sym<T5> s5) = aggregates(Group());
        ResultNode<T1> n1 = Result(s1);
        ResultNode<T2> n2 = Result(s2);
        ResultNode<T3> n3 = Result(s3);
        ResultNode<T4> n4 = Result(s4);
        ResultNode<T5> n5 = Result(s5);
        return new Aggregation<(T1, T2, T3, T4, T5)>(_scan.Host, Plan([n1, n2, n3, n4, n5]), outcome =>
        {
            Func<int, T1> r1 = n1.Bind(outcome);
            Func<int, T2> r2 = n2.Bind(outcome);
            Func<int, T3> r3 = n3.Bind(outcome);
            Func<int, T4> r4 = n4.Bind(outcome);
            Func<int, T5> r5 = n5.Bind(outcome);
            return g => (r1(g), r2(g), r3(g), r4(g), r5(g));
        });
    }

    /// <summary>Six answers per group, computed in one pass.</summary>
    /// <typeparam name="T1">The first answer's type.</typeparam>
    /// <typeparam name="T2">The second answer's type.</typeparam>
    /// <typeparam name="T3">The third answer's type.</typeparam>
    /// <typeparam name="T4">The fourth answer's type.</typeparam>
    /// <typeparam name="T5">The fifth answer's type.</typeparam>
    /// <typeparam name="T6">The sixth answer's type.</typeparam>
    /// <param name="aggregates">A lambda over the group, returning its key or aggregates.</param>
    /// <returns>The groups' answers, computed when enumerated.</returns>
    public Aggregation<(T1, T2, T3, T4, T5, T6)> Select<T1, T2, T3, T4, T5, T6>(
        Func<Group<TRecord, TKey>, (Sym<T1>, Sym<T2>, Sym<T3>, Sym<T4>, Sym<T5>, Sym<T6>)> aggregates)
    {
        ArgumentNullException.ThrowIfNull(aggregates);
        (Sym<T1> s1, Sym<T2> s2, Sym<T3> s3, Sym<T4> s4, Sym<T5> s5, Sym<T6> s6) = aggregates(Group());
        ResultNode<T1> n1 = Result(s1);
        ResultNode<T2> n2 = Result(s2);
        ResultNode<T3> n3 = Result(s3);
        ResultNode<T4> n4 = Result(s4);
        ResultNode<T5> n5 = Result(s5);
        ResultNode<T6> n6 = Result(s6);
        return new Aggregation<(T1, T2, T3, T4, T5, T6)>(_scan.Host, Plan([n1, n2, n3, n4, n5, n6]), outcome =>
        {
            Func<int, T1> r1 = n1.Bind(outcome);
            Func<int, T2> r2 = n2.Bind(outcome);
            Func<int, T3> r3 = n3.Bind(outcome);
            Func<int, T4> r4 = n4.Bind(outcome);
            Func<int, T5> r5 = n5.Bind(outcome);
            Func<int, T6> r6 = n6.Bind(outcome);
            return g => (r1(g), r2(g), r3(g), r4(g), r5(g), r6(g));
        });
    }

    /// <summary>Seven answers per group, computed in one pass.</summary>
    /// <typeparam name="T1">The first answer's type.</typeparam>
    /// <typeparam name="T2">The second answer's type.</typeparam>
    /// <typeparam name="T3">The third answer's type.</typeparam>
    /// <typeparam name="T4">The fourth answer's type.</typeparam>
    /// <typeparam name="T5">The fifth answer's type.</typeparam>
    /// <typeparam name="T6">The sixth answer's type.</typeparam>
    /// <typeparam name="T7">The seventh answer's type.</typeparam>
    /// <param name="aggregates">A lambda over the group, returning its key or aggregates.</param>
    /// <returns>The groups' answers, computed when enumerated.</returns>
    public Aggregation<(T1, T2, T3, T4, T5, T6, T7)> Select<T1, T2, T3, T4, T5, T6, T7>(
        Func<Group<TRecord, TKey>, (Sym<T1>, Sym<T2>, Sym<T3>, Sym<T4>, Sym<T5>, Sym<T6>, Sym<T7>)> aggregates)
    {
        ArgumentNullException.ThrowIfNull(aggregates);
        (Sym<T1> s1, Sym<T2> s2, Sym<T3> s3, Sym<T4> s4, Sym<T5> s5, Sym<T6> s6, Sym<T7> s7) = aggregates(Group());
        ResultNode<T1> n1 = Result(s1);
        ResultNode<T2> n2 = Result(s2);
        ResultNode<T3> n3 = Result(s3);
        ResultNode<T4> n4 = Result(s4);
        ResultNode<T5> n5 = Result(s5);
        ResultNode<T6> n6 = Result(s6);
        ResultNode<T7> n7 = Result(s7);
        return new Aggregation<(T1, T2, T3, T4, T5, T6, T7)>(_scan.Host, Plan([n1, n2, n3, n4, n5, n6, n7]), outcome =>
        {
            Func<int, T1> r1 = n1.Bind(outcome);
            Func<int, T2> r2 = n2.Bind(outcome);
            Func<int, T3> r3 = n3.Bind(outcome);
            Func<int, T4> r4 = n4.Bind(outcome);
            Func<int, T5> r5 = n5.Bind(outcome);
            Func<int, T6> r6 = n6.Bind(outcome);
            Func<int, T7> r7 = n7.Bind(outcome);
            return g => (r1(g), r2(g), r3(g), r4(g), r5(g), r6(g), r7(g));
        });
    }

    /// <summary>Eight answers per group, computed in one pass.</summary>
    /// <typeparam name="T1">The first answer's type.</typeparam>
    /// <typeparam name="T2">The second answer's type.</typeparam>
    /// <typeparam name="T3">The third answer's type.</typeparam>
    /// <typeparam name="T4">The fourth answer's type.</typeparam>
    /// <typeparam name="T5">The fifth answer's type.</typeparam>
    /// <typeparam name="T6">The sixth answer's type.</typeparam>
    /// <typeparam name="T7">The seventh answer's type.</typeparam>
    /// <typeparam name="T8">The eighth answer's type.</typeparam>
    /// <param name="aggregates">A lambda over the group, returning its key or aggregates.</param>
    /// <returns>The groups' answers, computed when enumerated.</returns>
    public Aggregation<(T1, T2, T3, T4, T5, T6, T7, T8)> Select<T1, T2, T3, T4, T5, T6, T7, T8>(
        Func<Group<TRecord, TKey>, (Sym<T1>, Sym<T2>, Sym<T3>, Sym<T4>, Sym<T5>, Sym<T6>, Sym<T7>, Sym<T8>)> aggregates)
    {
        ArgumentNullException.ThrowIfNull(aggregates);
        (Sym<T1> s1, Sym<T2> s2, Sym<T3> s3, Sym<T4> s4, Sym<T5> s5, Sym<T6> s6, Sym<T7> s7, Sym<T8> s8) = aggregates(Group());
        ResultNode<T1> n1 = Result(s1);
        ResultNode<T2> n2 = Result(s2);
        ResultNode<T3> n3 = Result(s3);
        ResultNode<T4> n4 = Result(s4);
        ResultNode<T5> n5 = Result(s5);
        ResultNode<T6> n6 = Result(s6);
        ResultNode<T7> n7 = Result(s7);
        ResultNode<T8> n8 = Result(s8);
        return new Aggregation<(T1, T2, T3, T4, T5, T6, T7, T8)>(_scan.Host, Plan([n1, n2, n3, n4, n5, n6, n7, n8]), outcome =>
        {
            Func<int, T1> r1 = n1.Bind(outcome);
            Func<int, T2> r2 = n2.Bind(outcome);
            Func<int, T3> r3 = n3.Bind(outcome);
            Func<int, T4> r4 = n4.Bind(outcome);
            Func<int, T5> r5 = n5.Bind(outcome);
            Func<int, T6> r6 = n6.Bind(outcome);
            Func<int, T7> r7 = n7.Bind(outcome);
            Func<int, T8> r8 = n8.Bind(outcome);
            return g => (r1(g), r2(g), r3(g), r4(g), r5(g), r6(g), r7(g), r8(g));
        });
    }

    private Group<TRecord, TKey> Group() => new Group<TRecord, TKey>(_scan.Binding, _key);

    /// <summary>The result a symbol of the lambda stands for: an aggregate, or a component of the key.</summary>
    private ResultNode<T> Result<T>(Sym<T> symbol) => AggregationPlan.Result(symbol, _components);

    private AggregationPlan Plan(ReadOnlySpan<SymNode> results) => new AggregationPlan(results, _keys);
}

/// <summary>
/// The answers of a grouped scan, one per group, computed in one pass when enumerated:
/// <c>await foreach (long count in aggregation)</c>.
/// </summary>
/// <typeparam name="TResult">The answer of one group: a value, or the tuple the lambda of <c>Select</c> returned.</typeparam>
/// <remarks>
/// Enumerated once, like the scan it runs, and by the pattern of <c>await foreach</c> rather than
/// as an <see cref="IAsyncEnumerable{T}"/>, as a scan is: what returns a stream to await carries
/// <c>Async</c> in its name, and <c>Select</c> builds a query. <see cref="ToValuesAsync"/> hands the
/// answers to <c>System.Linq.AsyncEnumerable</c>. The answers come as batches of the result's
/// column, each read into values once, so that a batch costs an <c>await</c> and a value none.
/// </remarks>
public sealed class Aggregation<TResult>
{
    private readonly AggregationHost _host;
    private readonly AggregationPlan _plan;
    private readonly AggregationQuery? _query;
    private readonly Func<AggregationOutcome, Func<int, TResult>>? _bind;
    private CancellationToken _cancellationToken;

    /// <summary>One value per group, read from the column of the result's batches.</summary>
    internal Aggregation(AggregationQuery query)
    {
        _host = query.Host;
        _plan = query.Plan;
        _query = query;
    }

    /// <summary>A tuple per group, read from the states, for the selections by arity of the 0.4 surface.</summary>
    internal Aggregation(AggregationHost host, AggregationPlan plan, Func<AggregationOutcome, Func<int, TResult>> bind)
    {
        _host = host;
        _plan = plan;
        _bind = bind;
    }

    /// <summary>What the scan did; valid once the aggregation has been enumerated.</summary>
    public ScanStatistics Statistics => _host.Statistics;

    /// <summary>What the aggregation computes, for the tests that hold two spellings of a query to one plan.</summary>
    internal AggregationPlan Plan => _plan;

    /// <summary>Makes <paramref name="cancellationToken"/> cancel the enumeration, as <c>WithCancellation</c> does a stream's.</summary>
    /// <param name="cancellationToken">Cancels the scan at a batch boundary.</param>
    /// <returns>This aggregation.</returns>
    public Aggregation<TResult> WithCancellation(CancellationToken cancellationToken)
    {
        _cancellationToken = cancellationToken;
        return this;
    }

    /// <summary>What the pass will read, from statistics, zone maps and indexes, without reading a data segment.</summary>
    /// <param name="cancellationToken">Cancels the reads of the structures consulted.</param>
    /// <returns>The plan.</returns>
    public ValueTask<ScanPlan> ExplainAsync(CancellationToken cancellationToken = default) =>
        _host.ExplainAsync(_plan, cancellationToken);

    /// <summary>Runs the aggregation, then enumerates the groups' answers.</summary>
    /// <param name="cancellationToken">Cancels the scan at a batch boundary, with the token <see cref="WithCancellation"/> gave.</param>
    /// <returns>The enumerator.</returns>
    public IAsyncEnumerator<TResult> GetAsyncEnumerator(CancellationToken cancellationToken = default) =>
        _query is not null ? Values(cancellationToken) : EnumerateAsync(cancellationToken).GetAsyncEnumerator(cancellationToken);

    /// <summary>The answers as a stream, for the operators of <c>System.Linq.AsyncEnumerable</c>, which then run on the values delivered.</summary>
    /// <param name="cancellationToken">Cancels the scan at a batch boundary.</param>
    /// <returns>The answers.</returns>
    public IAsyncEnumerable<TResult> ToValuesAsync(CancellationToken cancellationToken = default) =>
        _query is not null ? new ValueStream(this, cancellationToken) : EnumerateAsync(cancellationToken);

    /// <summary>Runs the aggregation and collects the answers, a batch at a time.</summary>
    /// <param name="cancellationToken">Cancels the scan at a batch boundary.</param>
    /// <returns>The answers, in the order they are delivered.</returns>
    public async ValueTask<List<TResult>> ToListAsync(CancellationToken cancellationToken = default)
    {
        List<TResult> list = [];
        if (_query is null)
        {
            await foreach (TResult value in EnumerateAsync(cancellationToken).ConfigureAwait(false))
            {
                list.Add(value);
            }

            return list;
        }

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

    /// <summary>Runs the aggregation and collects the answers into an array, a batch at a time.</summary>
    /// <param name="cancellationToken">Cancels the scan at a batch boundary.</param>
    /// <returns>The answers, in the order they are delivered.</returns>
    public async ValueTask<TResult[]> ToArrayAsync(CancellationToken cancellationToken = default) =>
        [.. await ToListAsync(cancellationToken).ConfigureAwait(false)];

    private ValueEnumerator<TResult> Values(CancellationToken cancellationToken)
    {
        CancellationTokenSource? linked = _cancellationToken.CanBeCanceled && cancellationToken.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(_cancellationToken, cancellationToken)
            : null;
        CancellationToken token = linked?.Token ?? (cancellationToken.CanBeCanceled ? cancellationToken : _cancellationToken);
        ResultColumn column = _query!.Columns[0];
        return new ValueEnumerator<TResult>(_query.Batches(token), column.Record, _host.Source.Session.Options.Extensions, linked);
    }

    private async IAsyncEnumerable<TResult> EnumerateAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using CancellationTokenSource? linked = _cancellationToken.CanBeCanceled && cancellationToken.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(_cancellationToken, cancellationToken)
            : null;
        CancellationToken token = linked?.Token ?? (cancellationToken.CanBeCanceled ? cancellationToken : _cancellationToken);
        AggregationOutcome outcome = await _host.RunAsync(_plan, token).ConfigureAwait(false);
        Func<int, TResult> read = _bind!(outcome);
        foreach (int group in outcome.Order)
        {
            yield return read(group);
        }
    }

    /// <summary>The answers as an <see cref="IAsyncEnumerable{T}"/>, enumerated once.</summary>
    private sealed class ValueStream(Aggregation<TResult> aggregation, CancellationToken cancellationToken) : IAsyncEnumerable<TResult>
    {
        public IAsyncEnumerator<TResult> GetAsyncEnumerator(CancellationToken token = default) =>
            aggregation.Values(token.CanBeCanceled ? token : cancellationToken);
    }
}
