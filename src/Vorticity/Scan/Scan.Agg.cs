using System;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Aggregating;

namespace Vorticity;

public sealed partial class Scan<TRecord>
{
    /// <summary>One answer over the rows the scan keeps.</summary>
    /// <typeparam name="T1">The answer's type.</typeparam>
    /// <param name="aggregate">A lambda over the scan's aggregates.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>The answer.</returns>
    public async ValueTask<T1> AggAsync<T1>(Func<Aggregates<TRecord>, Sym<T1>> aggregate, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(aggregate);
        ResultNode<T1> n1 = AggregationPlan.Result(aggregate(new Aggregates<TRecord>(Binding)));
        AggregationOutcome outcome = await Host.RunAsync(new AggregationPlan([n1], []), cancellationToken).ConfigureAwait(false);
        return n1.Bind(outcome)(0);
    }

    /// <summary>Two answers over the rows the scan keeps, computed in one pass.</summary>
    /// <typeparam name="T1">The first answer's type.</typeparam>
    /// <typeparam name="T2">The second answer's type.</typeparam>
    /// <param name="aggregates">A lambda over the scan's aggregates.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>The answers.</returns>
    public async ValueTask<(T1, T2)> AggAsync<T1, T2>(Func<Aggregates<TRecord>, (Sym<T1>, Sym<T2>)> aggregates, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(aggregates);
        (Sym<T1> s1, Sym<T2> s2) = aggregates(new Aggregates<TRecord>(Binding));
        ResultNode<T1> n1 = AggregationPlan.Result(s1);
        ResultNode<T2> n2 = AggregationPlan.Result(s2);
        AggregationOutcome outcome = await Host.RunAsync(new AggregationPlan([n1, n2], []), cancellationToken).ConfigureAwait(false);
        return (n1.Bind(outcome)(0), n2.Bind(outcome)(0));
    }

    /// <summary>Three answers over the rows the scan keeps, computed in one pass.</summary>
    /// <typeparam name="T1">The first answer's type.</typeparam>
    /// <typeparam name="T2">The second answer's type.</typeparam>
    /// <typeparam name="T3">The third answer's type.</typeparam>
    /// <param name="aggregates">A lambda over the scan's aggregates.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>The answers.</returns>
    public async ValueTask<(T1, T2, T3)> AggAsync<T1, T2, T3>(
        Func<Aggregates<TRecord>, (Sym<T1>, Sym<T2>, Sym<T3>)> aggregates, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(aggregates);
        (Sym<T1> s1, Sym<T2> s2, Sym<T3> s3) = aggregates(new Aggregates<TRecord>(Binding));
        ResultNode<T1> n1 = AggregationPlan.Result(s1);
        ResultNode<T2> n2 = AggregationPlan.Result(s2);
        ResultNode<T3> n3 = AggregationPlan.Result(s3);
        AggregationOutcome outcome = await Host.RunAsync(new AggregationPlan([n1, n2, n3], []), cancellationToken).ConfigureAwait(false);
        return (n1.Bind(outcome)(0), n2.Bind(outcome)(0), n3.Bind(outcome)(0));
    }

    /// <summary>Four answers over the rows the scan keeps, computed in one pass.</summary>
    /// <typeparam name="T1">The first answer's type.</typeparam>
    /// <typeparam name="T2">The second answer's type.</typeparam>
    /// <typeparam name="T3">The third answer's type.</typeparam>
    /// <typeparam name="T4">The fourth answer's type.</typeparam>
    /// <param name="aggregates">A lambda over the scan's aggregates.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>The answers.</returns>
    public async ValueTask<(T1, T2, T3, T4)> AggAsync<T1, T2, T3, T4>(
        Func<Aggregates<TRecord>, (Sym<T1>, Sym<T2>, Sym<T3>, Sym<T4>)> aggregates, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(aggregates);
        (Sym<T1> s1, Sym<T2> s2, Sym<T3> s3, Sym<T4> s4) = aggregates(new Aggregates<TRecord>(Binding));
        ResultNode<T1> n1 = AggregationPlan.Result(s1);
        ResultNode<T2> n2 = AggregationPlan.Result(s2);
        ResultNode<T3> n3 = AggregationPlan.Result(s3);
        ResultNode<T4> n4 = AggregationPlan.Result(s4);
        AggregationOutcome outcome = await Host.RunAsync(new AggregationPlan([n1, n2, n3, n4], []), cancellationToken).ConfigureAwait(false);
        return (n1.Bind(outcome)(0), n2.Bind(outcome)(0), n3.Bind(outcome)(0), n4.Bind(outcome)(0));
    }

    /// <summary>Five answers over the rows the scan keeps, computed in one pass.</summary>
    /// <typeparam name="T1">The first answer's type.</typeparam>
    /// <typeparam name="T2">The second answer's type.</typeparam>
    /// <typeparam name="T3">The third answer's type.</typeparam>
    /// <typeparam name="T4">The fourth answer's type.</typeparam>
    /// <typeparam name="T5">The fifth answer's type.</typeparam>
    /// <param name="aggregates">A lambda over the scan's aggregates.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>The answers.</returns>
    public async ValueTask<(T1, T2, T3, T4, T5)> AggAsync<T1, T2, T3, T4, T5>(
        Func<Aggregates<TRecord>, (Sym<T1>, Sym<T2>, Sym<T3>, Sym<T4>, Sym<T5>)> aggregates, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(aggregates);
        (Sym<T1> s1, Sym<T2> s2, Sym<T3> s3, Sym<T4> s4, Sym<T5> s5) = aggregates(new Aggregates<TRecord>(Binding));
        ResultNode<T1> n1 = AggregationPlan.Result(s1);
        ResultNode<T2> n2 = AggregationPlan.Result(s2);
        ResultNode<T3> n3 = AggregationPlan.Result(s3);
        ResultNode<T4> n4 = AggregationPlan.Result(s4);
        ResultNode<T5> n5 = AggregationPlan.Result(s5);
        AggregationOutcome outcome = await Host.RunAsync(new AggregationPlan([n1, n2, n3, n4, n5], []), cancellationToken).ConfigureAwait(false);
        return (n1.Bind(outcome)(0), n2.Bind(outcome)(0), n3.Bind(outcome)(0), n4.Bind(outcome)(0), n5.Bind(outcome)(0));
    }

    /// <summary>Six answers over the rows the scan keeps, computed in one pass.</summary>
    /// <typeparam name="T1">The first answer's type.</typeparam>
    /// <typeparam name="T2">The second answer's type.</typeparam>
    /// <typeparam name="T3">The third answer's type.</typeparam>
    /// <typeparam name="T4">The fourth answer's type.</typeparam>
    /// <typeparam name="T5">The fifth answer's type.</typeparam>
    /// <typeparam name="T6">The sixth answer's type.</typeparam>
    /// <param name="aggregates">A lambda over the scan's aggregates.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>The answers.</returns>
    public async ValueTask<(T1, T2, T3, T4, T5, T6)> AggAsync<T1, T2, T3, T4, T5, T6>(
        Func<Aggregates<TRecord>, (Sym<T1>, Sym<T2>, Sym<T3>, Sym<T4>, Sym<T5>, Sym<T6>)> aggregates, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(aggregates);
        (Sym<T1> s1, Sym<T2> s2, Sym<T3> s3, Sym<T4> s4, Sym<T5> s5, Sym<T6> s6) = aggregates(new Aggregates<TRecord>(Binding));
        ResultNode<T1> n1 = AggregationPlan.Result(s1);
        ResultNode<T2> n2 = AggregationPlan.Result(s2);
        ResultNode<T3> n3 = AggregationPlan.Result(s3);
        ResultNode<T4> n4 = AggregationPlan.Result(s4);
        ResultNode<T5> n5 = AggregationPlan.Result(s5);
        ResultNode<T6> n6 = AggregationPlan.Result(s6);
        AggregationOutcome outcome = await Host.RunAsync(new AggregationPlan([n1, n2, n3, n4, n5, n6], []), cancellationToken).ConfigureAwait(false);
        return (n1.Bind(outcome)(0), n2.Bind(outcome)(0), n3.Bind(outcome)(0), n4.Bind(outcome)(0), n5.Bind(outcome)(0), n6.Bind(outcome)(0));
    }

    /// <summary>Seven answers over the rows the scan keeps, computed in one pass.</summary>
    /// <typeparam name="T1">The first answer's type.</typeparam>
    /// <typeparam name="T2">The second answer's type.</typeparam>
    /// <typeparam name="T3">The third answer's type.</typeparam>
    /// <typeparam name="T4">The fourth answer's type.</typeparam>
    /// <typeparam name="T5">The fifth answer's type.</typeparam>
    /// <typeparam name="T6">The sixth answer's type.</typeparam>
    /// <typeparam name="T7">The seventh answer's type.</typeparam>
    /// <param name="aggregates">A lambda over the scan's aggregates.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>The answers.</returns>
    public async ValueTask<(T1, T2, T3, T4, T5, T6, T7)> AggAsync<T1, T2, T3, T4, T5, T6, T7>(
        Func<Aggregates<TRecord>, (Sym<T1>, Sym<T2>, Sym<T3>, Sym<T4>, Sym<T5>, Sym<T6>, Sym<T7>)> aggregates, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(aggregates);
        (Sym<T1> s1, Sym<T2> s2, Sym<T3> s3, Sym<T4> s4, Sym<T5> s5, Sym<T6> s6, Sym<T7> s7) = aggregates(new Aggregates<TRecord>(Binding));
        ResultNode<T1> n1 = AggregationPlan.Result(s1);
        ResultNode<T2> n2 = AggregationPlan.Result(s2);
        ResultNode<T3> n3 = AggregationPlan.Result(s3);
        ResultNode<T4> n4 = AggregationPlan.Result(s4);
        ResultNode<T5> n5 = AggregationPlan.Result(s5);
        ResultNode<T6> n6 = AggregationPlan.Result(s6);
        ResultNode<T7> n7 = AggregationPlan.Result(s7);
        AggregationOutcome outcome = await Host.RunAsync(new AggregationPlan([n1, n2, n3, n4, n5, n6, n7], []), cancellationToken).ConfigureAwait(false);
        return (n1.Bind(outcome)(0), n2.Bind(outcome)(0), n3.Bind(outcome)(0), n4.Bind(outcome)(0), n5.Bind(outcome)(0), n6.Bind(outcome)(0), n7.Bind(outcome)(0));
    }

    /// <summary>Eight answers over the rows the scan keeps, computed in one pass.</summary>
    /// <typeparam name="T1">The first answer's type.</typeparam>
    /// <typeparam name="T2">The second answer's type.</typeparam>
    /// <typeparam name="T3">The third answer's type.</typeparam>
    /// <typeparam name="T4">The fourth answer's type.</typeparam>
    /// <typeparam name="T5">The fifth answer's type.</typeparam>
    /// <typeparam name="T6">The sixth answer's type.</typeparam>
    /// <typeparam name="T7">The seventh answer's type.</typeparam>
    /// <typeparam name="T8">The eighth answer's type.</typeparam>
    /// <param name="aggregates">A lambda over the scan's aggregates.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>The answers.</returns>
    public async ValueTask<(T1, T2, T3, T4, T5, T6, T7, T8)> AggAsync<T1, T2, T3, T4, T5, T6, T7, T8>(
        Func<Aggregates<TRecord>, (Sym<T1>, Sym<T2>, Sym<T3>, Sym<T4>, Sym<T5>, Sym<T6>, Sym<T7>, Sym<T8>)> aggregates, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(aggregates);
        (Sym<T1> s1, Sym<T2> s2, Sym<T3> s3, Sym<T4> s4, Sym<T5> s5, Sym<T6> s6, Sym<T7> s7, Sym<T8> s8) = aggregates(new Aggregates<TRecord>(Binding));
        ResultNode<T1> n1 = AggregationPlan.Result(s1);
        ResultNode<T2> n2 = AggregationPlan.Result(s2);
        ResultNode<T3> n3 = AggregationPlan.Result(s3);
        ResultNode<T4> n4 = AggregationPlan.Result(s4);
        ResultNode<T5> n5 = AggregationPlan.Result(s5);
        ResultNode<T6> n6 = AggregationPlan.Result(s6);
        ResultNode<T7> n7 = AggregationPlan.Result(s7);
        ResultNode<T8> n8 = AggregationPlan.Result(s8);
        AggregationOutcome outcome = await Host.RunAsync(new AggregationPlan([n1, n2, n3, n4, n5, n6, n7, n8], []), cancellationToken).ConfigureAwait(false);
        return (n1.Bind(outcome)(0), n2.Bind(outcome)(0), n3.Bind(outcome)(0), n4.Bind(outcome)(0), n5.Bind(outcome)(0), n6.Bind(outcome)(0), n7.Bind(outcome)(0), n8.Bind(outcome)(0));
    }
}
