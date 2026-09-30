using System;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Aggregating;
using Vorticity.Scanning;

namespace Vorticity;

public sealed partial class Scan<TRecord>
{
    private ScanHost? _host;

    internal AggregationHost Host => _host ??= new ScanHost(this);

    /// <summary>
    /// The sum of <paramref name="column"/> over the rows the scan keeps: from the file statistics when
    /// the scan is the whole file, block by block in the encoded form otherwise.
    /// </summary>
    /// <typeparam name="T">The column's type.</typeparam>
    /// <param name="column">The column.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>The sum; zero when no row holds a value.</returns>
    /// <exception cref="OverflowException">The sum, exact in 128 bits for integers, does not fit <typeparamref name="T"/>.</exception>
    public ValueTask<T> SumAsync<T>(Func<Probe<TRecord>, Sym<T>> column, CancellationToken cancellationToken = default)
        where T : INumber<T> =>
        ScalarAsync(Aggregators.Sum<T>(Aggregators.Input(Binding, column)), cancellationToken);

    /// <summary>The sum of the non-null values of a nullable <paramref name="column"/> over the rows the scan keeps.</summary>
    /// <typeparam name="T">The column's type, without its nullability.</typeparam>
    /// <param name="column">The column.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>The sum; zero when no row holds a value.</returns>
    /// <exception cref="OverflowException">The sum, exact in 128 bits for integers, does not fit <typeparamref name="T"/>.</exception>
    public ValueTask<T> SumAsync<T>(Func<Probe<TRecord>, Sym<T?>> column, CancellationToken cancellationToken = default)
        where T : struct, INumber<T> =>
        ScalarAsync(Aggregators.Sum<T>(Aggregators.Input(Binding, column)), cancellationToken);

    /// <summary>The mean of <paramref name="column"/> over the rows the scan keeps: the sum and the null count of the statistics when they settle it.</summary>
    /// <typeparam name="T">The column's type.</typeparam>
    /// <param name="column">The column.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>The mean; null when no row holds a value.</returns>
    public ValueTask<double?> AvgAsync<T>(Func<Probe<TRecord>, Sym<T>> column, CancellationToken cancellationToken = default)
        where T : INumber<T> =>
        ScalarAsync(Aggregators.Avg(Aggregators.Input(Binding, column)), cancellationToken);

    /// <summary>The mean of the non-null values of a nullable <paramref name="column"/> over the rows the scan keeps.</summary>
    /// <typeparam name="T">The column's type, without its nullability.</typeparam>
    /// <param name="column">The column.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>The mean; null when no row holds a value.</returns>
    public ValueTask<double?> AvgAsync<T>(Func<Probe<TRecord>, Sym<T?>> column, CancellationToken cancellationToken = default)
        where T : struct, INumber<T> =>
        ScalarAsync(Aggregators.Avg(Aggregators.Input(Binding, column)), cancellationToken);

    /// <summary>The number of distinct non-null values of <paramref name="column"/> among the rows the scan keeps; a dictionary block counts its codes, not its rows.</summary>
    /// <typeparam name="T">The column's type.</typeparam>
    /// <param name="column">The column.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>The count.</returns>
    public ValueTask<long> CountDistinctAsync<T>(Func<Probe<TRecord>, Sym<T>> column, CancellationToken cancellationToken = default) =>
        ScalarAsync(Aggregators.CountDistinct(Aggregators.Input(Binding, column)), cancellationToken);

    /// <summary>The state <typeparamref name="TAggregator"/> folds <paramref name="column"/> into over the rows the scan keeps, a state per chunk merged at the end.</summary>
    /// <typeparam name="T">The column's storage type.</typeparam>
    /// <typeparam name="TAggregator">The aggregator; an <see cref="IEncodedAggregator{T, TState}"/> reads the encoded blocks.</typeparam>
    /// <typeparam name="TState">The aggregator's state.</typeparam>
    /// <param name="column">The column.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>The state.</returns>
    public ValueTask<TState> AggregateAsync<T, TAggregator, TState>(Func<Probe<TRecord>, Sym<T>> column, CancellationToken cancellationToken = default)
        where T : unmanaged
        where TAggregator : IAggregator<T, TState> =>
        ScalarAsync(Aggregators.Custom<T, TAggregator, TState>(Aggregators.Input(Binding, column)), cancellationToken);

    /// <summary>The state <typeparamref name="TAggregator"/> folds a nullable <paramref name="column"/> into; the aggregator reads its validity.</summary>
    /// <typeparam name="T">The column's storage type.</typeparam>
    /// <typeparam name="TAggregator">The aggregator.</typeparam>
    /// <typeparam name="TState">The aggregator's state.</typeparam>
    /// <param name="column">The column.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>The state.</returns>
    public ValueTask<TState> AggregateAsync<T, TAggregator, TState>(Func<Probe<TRecord>, Sym<T?>> column, CancellationToken cancellationToken = default)
        where T : unmanaged
        where TAggregator : IAggregator<T, TState> =>
        ScalarAsync(Aggregators.Custom<T, TAggregator, TState>(Aggregators.Input(Binding, column)), cancellationToken);

    internal async ValueTask<T> ScalarAsync<T>(Sym<T> aggregate, CancellationToken cancellationToken)
    {
        ResultNode<T> node = AggregationPlan.Result(aggregate);
        AggregationOutcome outcome = await Host.RunAsync(new AggregationPlan([node], []), cancellationToken).ConfigureAwait(false);
        return node.Bind(outcome)(0);
    }

    /// <summary>The scan as the aggregation engine sees it.</summary>
    private sealed class ScanHost : AggregationHost
    {
        private readonly Scan<TRecord> _scan;

        internal ScanHost(Scan<TRecord> scan) => _scan = scan;

        internal override ScanSource Source => _scan.Source;

        internal override ScanMetrics Metrics => _scan.Metrics;

        internal override ScanStatistics Statistics => _scan.Statistics;

        internal override ScanSpec Spec() => _scan.Spec();

        internal override void Begin() => _scan.Begin();

        internal override void End() => _scan.End();
    }
}
