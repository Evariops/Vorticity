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

    /// <summary>The sum of <paramref name="column"/> over the rows the scan keeps, as a long, which no sum of narrower integers overflows on the way.</summary>
    /// <param name="column">The column.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>The sum; zero when no row holds a value.</returns>
    public ValueTask<long> SumAsync(Func<Probe<TRecord>, Sym<sbyte>> column, CancellationToken cancellationToken = default) => WidenedAsync<long, sbyte>(column, cancellationToken);

    /// <inheritdoc cref="SumAsync(Func{Probe{TRecord}, Sym{sbyte}}, CancellationToken)"/>
    public ValueTask<long> SumAsync(Func<Probe<TRecord>, Sym<sbyte?>> column, CancellationToken cancellationToken = default) => WidenedAsync<long, sbyte?>(column, cancellationToken);

    /// <inheritdoc cref="SumAsync(Func{Probe{TRecord}, Sym{sbyte}}, CancellationToken)"/>
    public ValueTask<long> SumAsync(Func<Probe<TRecord>, Sym<short>> column, CancellationToken cancellationToken = default) => WidenedAsync<long, short>(column, cancellationToken);

    /// <inheritdoc cref="SumAsync(Func{Probe{TRecord}, Sym{sbyte}}, CancellationToken)"/>
    public ValueTask<long> SumAsync(Func<Probe<TRecord>, Sym<short?>> column, CancellationToken cancellationToken = default) => WidenedAsync<long, short?>(column, cancellationToken);

    /// <inheritdoc cref="SumAsync(Func{Probe{TRecord}, Sym{sbyte}}, CancellationToken)"/>
    public ValueTask<long> SumAsync(Func<Probe<TRecord>, Sym<int>> column, CancellationToken cancellationToken = default) => WidenedAsync<long, int>(column, cancellationToken);

    /// <inheritdoc cref="SumAsync(Func{Probe{TRecord}, Sym{sbyte}}, CancellationToken)"/>
    public ValueTask<long> SumAsync(Func<Probe<TRecord>, Sym<int?>> column, CancellationToken cancellationToken = default) => WidenedAsync<long, int?>(column, cancellationToken);

    /// <summary>The sum of <paramref name="column"/> over the rows the scan keeps, as an unsigned long, which no sum of narrower integers overflows on the way.</summary>
    /// <param name="column">The column.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>The sum; zero when no row holds a value.</returns>
    public ValueTask<ulong> SumAsync(Func<Probe<TRecord>, Sym<byte>> column, CancellationToken cancellationToken = default) => WidenedAsync<ulong, byte>(column, cancellationToken);

    /// <inheritdoc cref="SumAsync(Func{Probe{TRecord}, Sym{byte}}, CancellationToken)"/>
    public ValueTask<ulong> SumAsync(Func<Probe<TRecord>, Sym<byte?>> column, CancellationToken cancellationToken = default) => WidenedAsync<ulong, byte?>(column, cancellationToken);

    /// <inheritdoc cref="SumAsync(Func{Probe{TRecord}, Sym{byte}}, CancellationToken)"/>
    public ValueTask<ulong> SumAsync(Func<Probe<TRecord>, Sym<ushort>> column, CancellationToken cancellationToken = default) => WidenedAsync<ulong, ushort>(column, cancellationToken);

    /// <inheritdoc cref="SumAsync(Func{Probe{TRecord}, Sym{byte}}, CancellationToken)"/>
    public ValueTask<ulong> SumAsync(Func<Probe<TRecord>, Sym<ushort?>> column, CancellationToken cancellationToken = default) => WidenedAsync<ulong, ushort?>(column, cancellationToken);

    /// <inheritdoc cref="SumAsync(Func{Probe{TRecord}, Sym{byte}}, CancellationToken)"/>
    public ValueTask<ulong> SumAsync(Func<Probe<TRecord>, Sym<uint>> column, CancellationToken cancellationToken = default) => WidenedAsync<ulong, uint>(column, cancellationToken);

    /// <inheritdoc cref="SumAsync(Func{Probe{TRecord}, Sym{byte}}, CancellationToken)"/>
    public ValueTask<ulong> SumAsync(Func<Probe<TRecord>, Sym<uint?>> column, CancellationToken cancellationToken = default) => WidenedAsync<ulong, uint?>(column, cancellationToken);

    /// <summary>The sum of <paramref name="column"/> over the rows the scan keeps, as a double: the reproducible sum of its values.</summary>
    /// <param name="column">The column.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>The sum; zero when no row holds a value.</returns>
    public ValueTask<double> SumAsync(Func<Probe<TRecord>, Sym<float>> column, CancellationToken cancellationToken = default) => WidenedAsync<double, float>(column, cancellationToken);

    /// <inheritdoc cref="SumAsync(Func{Probe{TRecord}, Sym{float}}, CancellationToken)"/>
    public ValueTask<double> SumAsync(Func<Probe<TRecord>, Sym<float?>> column, CancellationToken cancellationToken = default) => WidenedAsync<double, float?>(column, cancellationToken);

    /// <inheritdoc cref="SumAsync(Func{Probe{TRecord}, Sym{float}}, CancellationToken)"/>
    public ValueTask<double> SumAsync(Func<Probe<TRecord>, Sym<Half>> column, CancellationToken cancellationToken = default) => WidenedAsync<double, Half>(column, cancellationToken);

    /// <inheritdoc cref="SumAsync(Func{Probe{TRecord}, Sym{float}}, CancellationToken)"/>
    public ValueTask<double> SumAsync(Func<Probe<TRecord>, Sym<Half?>> column, CancellationToken cancellationToken = default) => WidenedAsync<double, Half?>(column, cancellationToken);

    /// <summary>The mean of <paramref name="column"/> over the rows the scan keeps: the sum and the null count of the statistics when they settle it.</summary>
    /// <typeparam name="T">The column's type.</typeparam>
    /// <param name="column">The column.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>The mean; null when no row holds a value.</returns>
    public ValueTask<double?> AverageAsync<T>(Func<Probe<TRecord>, Sym<T>> column, CancellationToken cancellationToken = default)
        where T : INumber<T> =>
        ScalarAsync(Aggregators.Average(Aggregators.Input(Binding, column)), cancellationToken);

    private ValueTask<TSum> WidenedAsync<TSum, TColumn>(Func<Probe<TRecord>, Sym<TColumn>> column, CancellationToken cancellationToken)
        where TSum : INumber<TSum> =>
        ScalarAsync(Aggregators.Sum<TSum>(Aggregators.Input(Binding, column)), cancellationToken);

    /// <summary>The mean of the non-null values of a nullable <paramref name="column"/> over the rows the scan keeps.</summary>
    /// <typeparam name="T">The column's type, without its nullability.</typeparam>
    /// <param name="column">The column.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>The mean; null when no row holds a value.</returns>
    public ValueTask<double?> AverageAsync<T>(Func<Probe<TRecord>, Sym<T?>> column, CancellationToken cancellationToken = default)
        where T : struct, INumber<T> =>
        ScalarAsync(Aggregators.Average(Aggregators.Input(Binding, column)), cancellationToken);

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
