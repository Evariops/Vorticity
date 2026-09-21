using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Aggregating;

namespace Vorticity;

/// <summary>A scan grouped by a key, waiting for the aggregates of each group: <c>scan.GroupBy(r =&gt; r.City).AggAsync(g =&gt; (g.Key, g.Count()))</c>.</summary>
/// <typeparam name="TRecord">The record the scan is typed by.</typeparam>
/// <typeparam name="TKey">The key: one column's type, or the tuple of a composite key.</typeparam>
/// <remarks>
/// A key read from a dictionary block groups by code, a key the statistics say is sorted groups by
/// run, any other key by hash; the groups arrive in key order, nulls last, in the first two cases,
/// and in an unspecified order otherwise. The memory is one state per group.
/// </remarks>
public sealed class GroupedScan<TRecord, TKey>
    where TRecord : IVortexRecord<TRecord>
{
    private readonly Scan<TRecord> _scan;
    private readonly ColumnShape[] _keys;
    private readonly Func<GroupKeys, Func<int, TKey>> _key;

    internal GroupedScan(Scan<TRecord> scan, ColumnShape[] keys, Func<GroupKeys, Func<int, TKey>> key)
    {
        _scan = scan;
        _keys = keys;
        _key = key;
    }

    /// <summary>One answer per group.</summary>
    /// <typeparam name="T1">The answer's type.</typeparam>
    /// <param name="aggregate">A lambda over the group, returning its key or an aggregate.</param>
    /// <returns>The groups' answers, computed when enumerated.</returns>
    public Aggregation<T1> AggAsync<T1>(Func<Group<TRecord, TKey>, Sym<T1>> aggregate)
    {
        ArgumentNullException.ThrowIfNull(aggregate);
        ResultNode<T1> n1 = AggregationPlan.Result(aggregate(Group()));
        return new Aggregation<T1>(_scan.Host, Plan([n1]), outcome => n1.Bind(outcome));
    }

    /// <summary>Two answers per group, computed in one pass.</summary>
    /// <typeparam name="T1">The first answer's type.</typeparam>
    /// <typeparam name="T2">The second answer's type.</typeparam>
    /// <param name="aggregates">A lambda over the group, returning its key or aggregates.</param>
    /// <returns>The groups' answers, computed when enumerated.</returns>
    public Aggregation<(T1, T2)> AggAsync<T1, T2>(Func<Group<TRecord, TKey>, (Sym<T1>, Sym<T2>)> aggregates)
    {
        ArgumentNullException.ThrowIfNull(aggregates);
        (Sym<T1> s1, Sym<T2> s2) = aggregates(Group());
        ResultNode<T1> n1 = AggregationPlan.Result(s1);
        ResultNode<T2> n2 = AggregationPlan.Result(s2);
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
    public Aggregation<(T1, T2, T3)> AggAsync<T1, T2, T3>(Func<Group<TRecord, TKey>, (Sym<T1>, Sym<T2>, Sym<T3>)> aggregates)
    {
        ArgumentNullException.ThrowIfNull(aggregates);
        (Sym<T1> s1, Sym<T2> s2, Sym<T3> s3) = aggregates(Group());
        ResultNode<T1> n1 = AggregationPlan.Result(s1);
        ResultNode<T2> n2 = AggregationPlan.Result(s2);
        ResultNode<T3> n3 = AggregationPlan.Result(s3);
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
    public Aggregation<(T1, T2, T3, T4)> AggAsync<T1, T2, T3, T4>(Func<Group<TRecord, TKey>, (Sym<T1>, Sym<T2>, Sym<T3>, Sym<T4>)> aggregates)
    {
        ArgumentNullException.ThrowIfNull(aggregates);
        (Sym<T1> s1, Sym<T2> s2, Sym<T3> s3, Sym<T4> s4) = aggregates(Group());
        ResultNode<T1> n1 = AggregationPlan.Result(s1);
        ResultNode<T2> n2 = AggregationPlan.Result(s2);
        ResultNode<T3> n3 = AggregationPlan.Result(s3);
        ResultNode<T4> n4 = AggregationPlan.Result(s4);
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
    public Aggregation<(T1, T2, T3, T4, T5)> AggAsync<T1, T2, T3, T4, T5>(Func<Group<TRecord, TKey>, (Sym<T1>, Sym<T2>, Sym<T3>, Sym<T4>, Sym<T5>)> aggregates)
    {
        ArgumentNullException.ThrowIfNull(aggregates);
        (Sym<T1> s1, Sym<T2> s2, Sym<T3> s3, Sym<T4> s4, Sym<T5> s5) = aggregates(Group());
        ResultNode<T1> n1 = AggregationPlan.Result(s1);
        ResultNode<T2> n2 = AggregationPlan.Result(s2);
        ResultNode<T3> n3 = AggregationPlan.Result(s3);
        ResultNode<T4> n4 = AggregationPlan.Result(s4);
        ResultNode<T5> n5 = AggregationPlan.Result(s5);
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
    public Aggregation<(T1, T2, T3, T4, T5, T6)> AggAsync<T1, T2, T3, T4, T5, T6>(
        Func<Group<TRecord, TKey>, (Sym<T1>, Sym<T2>, Sym<T3>, Sym<T4>, Sym<T5>, Sym<T6>)> aggregates)
    {
        ArgumentNullException.ThrowIfNull(aggregates);
        (Sym<T1> s1, Sym<T2> s2, Sym<T3> s3, Sym<T4> s4, Sym<T5> s5, Sym<T6> s6) = aggregates(Group());
        ResultNode<T1> n1 = AggregationPlan.Result(s1);
        ResultNode<T2> n2 = AggregationPlan.Result(s2);
        ResultNode<T3> n3 = AggregationPlan.Result(s3);
        ResultNode<T4> n4 = AggregationPlan.Result(s4);
        ResultNode<T5> n5 = AggregationPlan.Result(s5);
        ResultNode<T6> n6 = AggregationPlan.Result(s6);
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
    public Aggregation<(T1, T2, T3, T4, T5, T6, T7)> AggAsync<T1, T2, T3, T4, T5, T6, T7>(
        Func<Group<TRecord, TKey>, (Sym<T1>, Sym<T2>, Sym<T3>, Sym<T4>, Sym<T5>, Sym<T6>, Sym<T7>)> aggregates)
    {
        ArgumentNullException.ThrowIfNull(aggregates);
        (Sym<T1> s1, Sym<T2> s2, Sym<T3> s3, Sym<T4> s4, Sym<T5> s5, Sym<T6> s6, Sym<T7> s7) = aggregates(Group());
        ResultNode<T1> n1 = AggregationPlan.Result(s1);
        ResultNode<T2> n2 = AggregationPlan.Result(s2);
        ResultNode<T3> n3 = AggregationPlan.Result(s3);
        ResultNode<T4> n4 = AggregationPlan.Result(s4);
        ResultNode<T5> n5 = AggregationPlan.Result(s5);
        ResultNode<T6> n6 = AggregationPlan.Result(s6);
        ResultNode<T7> n7 = AggregationPlan.Result(s7);
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
    public Aggregation<(T1, T2, T3, T4, T5, T6, T7, T8)> AggAsync<T1, T2, T3, T4, T5, T6, T7, T8>(
        Func<Group<TRecord, TKey>, (Sym<T1>, Sym<T2>, Sym<T3>, Sym<T4>, Sym<T5>, Sym<T6>, Sym<T7>, Sym<T8>)> aggregates)
    {
        ArgumentNullException.ThrowIfNull(aggregates);
        (Sym<T1> s1, Sym<T2> s2, Sym<T3> s3, Sym<T4> s4, Sym<T5> s5, Sym<T6> s6, Sym<T7> s7, Sym<T8> s8) = aggregates(Group());
        ResultNode<T1> n1 = AggregationPlan.Result(s1);
        ResultNode<T2> n2 = AggregationPlan.Result(s2);
        ResultNode<T3> n3 = AggregationPlan.Result(s3);
        ResultNode<T4> n4 = AggregationPlan.Result(s4);
        ResultNode<T5> n5 = AggregationPlan.Result(s5);
        ResultNode<T6> n6 = AggregationPlan.Result(s6);
        ResultNode<T7> n7 = AggregationPlan.Result(s7);
        ResultNode<T8> n8 = AggregationPlan.Result(s8);
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

    private Group<TRecord, TKey> Group() => new Group<TRecord, TKey>(_scan.Binding, new Sym<TKey>(new KeyNode<TKey>(-1, _key)));

    private AggregationPlan Plan(ReadOnlySpan<SymNode> results) => new AggregationPlan(results, _keys);
}

/// <summary>
/// The answers of a grouped scan, one per group, computed in one pass when enumerated:
/// <c>await foreach (var (city, total) in aggregation)</c>.
/// </summary>
/// <typeparam name="TResult">The answer of one group: a value, or the tuple the lambda of <c>AggAsync</c> returned.</typeparam>
/// <remarks>Enumerated once, like the scan it runs.</remarks>
public sealed class Aggregation<TResult> : IAsyncEnumerable<TResult>
{
    private readonly AggregationHost _host;
    private readonly AggregationPlan _plan;
    private readonly Func<AggregationOutcome, Func<int, TResult>> _bind;

    internal Aggregation(AggregationHost host, AggregationPlan plan, Func<AggregationOutcome, Func<int, TResult>> bind)
    {
        _host = host;
        _plan = plan;
        _bind = bind;
    }

    /// <summary>What the scan did; valid once the aggregation has been enumerated.</summary>
    public ScanStatistics Statistics => _host.Statistics;

    /// <summary>What the pass will read, from statistics, zone maps and indexes, without reading a data segment.</summary>
    /// <param name="cancellationToken">Cancels the reads of the structures consulted.</param>
    /// <returns>The plan.</returns>
    public ValueTask<ScanPlan> ExplainAsync(CancellationToken cancellationToken = default) =>
        _host.ExplainAsync(_plan, cancellationToken);

    /// <summary>Runs the aggregation, then enumerates the groups' answers.</summary>
    /// <param name="cancellationToken">Cancels the scan at a batch boundary.</param>
    /// <returns>The enumerator.</returns>
    public IAsyncEnumerator<TResult> GetAsyncEnumerator(CancellationToken cancellationToken = default) =>
        EnumerateAsync(cancellationToken).GetAsyncEnumerator(cancellationToken);

    private async IAsyncEnumerable<TResult> EnumerateAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        AggregationOutcome outcome = await _host.RunAsync(_plan, cancellationToken).ConfigureAwait(false);
        Func<int, TResult> read = _bind(outcome);
        foreach (int group in outcome.Order)
        {
            yield return read(group);
        }
    }
}
