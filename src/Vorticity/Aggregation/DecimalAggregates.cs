using System;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Aggregating;

namespace Vorticity;

/// <summary>
/// The sum and the mean of a decimal column read as <see cref="VortexDecimal"/>, exact at any
/// precision: 38 digits while a sum fits them, 76 beyond, at the column's scale.
/// </summary>
/// <remarks>
/// Extensions rather than overloads of the generic members, whose <c>INumber</c> constraint a
/// <see cref="VortexDecimal"/> cannot meet: the compiler reaches an extension only when no member
/// applies, so a sum of text is still refused for the constraint it breaks, not for failing to
/// convert to a decimal it never asked for.
/// </remarks>
public static class DecimalAggregates
{
    extension<TRecord>(Aggregates<TRecord> aggregates)
    {
        /// <summary>The sum of a decimal <paramref name="column"/>, exact at any precision.</summary>
        /// <param name="column">The column.</param>
        /// <returns>The symbol of the sum, at the column's scale; zero when no row holds a value. Reading it throws <see cref="OverflowException"/> when the sum passes 76 digits.</returns>
        public Sym<VortexDecimal> Sum(Func<Probe<TRecord>, Sym<VortexDecimal>> column) =>
            Aggregators.SumDecimal(Aggregators.Input(aggregates.Binding, column));

        /// <summary>The sum of the non-null values of a nullable decimal <paramref name="column"/>.</summary>
        /// <param name="column">The column.</param>
        /// <returns>The symbol of the sum, at the column's scale; zero when no row holds a value.</returns>
        public Sym<VortexDecimal> Sum(Func<Probe<TRecord>, Sym<VortexDecimal?>> column) =>
            Aggregators.SumDecimal(Aggregators.Input(aggregates.Binding, column));

        /// <summary>The mean of a decimal <paramref name="column"/>, from its exact sum.</summary>
        /// <param name="column">The column.</param>
        /// <returns>The symbol of the mean; null when no row holds a value.</returns>
        public Sym<double?> Average(Func<Probe<TRecord>, Sym<VortexDecimal>> column) =>
            Aggregators.Average(Aggregators.Input(aggregates.Binding, column));

        /// <summary>The mean of the non-null values of a nullable decimal <paramref name="column"/>.</summary>
        /// <param name="column">The column.</param>
        /// <returns>The symbol of the mean; null when no row holds a value.</returns>
        public Sym<double?> Average(Func<Probe<TRecord>, Sym<VortexDecimal?>> column) =>
            Aggregators.Average(Aggregators.Input(aggregates.Binding, column));
    }

    extension<TRecord, TKey>(Group<TRecord, TKey> group)
    {
        /// <summary>The sum of a decimal <paramref name="column"/> in the group, exact at any precision.</summary>
        /// <param name="column">The column.</param>
        /// <returns>The symbol of the sum; reading it throws <see cref="OverflowException"/> when the sum passes 76 digits.</returns>
        public Sym<VortexDecimal> Sum(Func<Probe<TRecord>, Sym<VortexDecimal>> column) =>
            Aggregators.SumDecimal(Aggregators.Input(group.Binding, column));

        /// <summary>The sum of the non-null values of a nullable decimal <paramref name="column"/> in the group.</summary>
        /// <param name="column">The column.</param>
        /// <returns>The symbol of the sum.</returns>
        public Sym<VortexDecimal> Sum(Func<Probe<TRecord>, Sym<VortexDecimal?>> column) =>
            Aggregators.SumDecimal(Aggregators.Input(group.Binding, column));

        /// <summary>The mean of a decimal <paramref name="column"/> in the group.</summary>
        /// <param name="column">The column.</param>
        /// <returns>The symbol of the mean; null when no row of the group holds a value.</returns>
        public Sym<double?> Average(Func<Probe<TRecord>, Sym<VortexDecimal>> column) =>
            Aggregators.Average(Aggregators.Input(group.Binding, column));

        /// <summary>The mean of the non-null values of a nullable decimal <paramref name="column"/> in the group.</summary>
        /// <param name="column">The column.</param>
        /// <returns>The symbol of the mean.</returns>
        public Sym<double?> Average(Func<Probe<TRecord>, Sym<VortexDecimal?>> column) =>
            Aggregators.Average(Aggregators.Input(group.Binding, column));
    }

    extension<TRecord>(Scan<TRecord> scan)
        where TRecord : IVortexRecord<TRecord>
    {
        /// <summary>The sum of a decimal <paramref name="column"/> over the rows the scan keeps, exact at any precision.</summary>
        /// <param name="column">The column.</param>
        /// <param name="cancellationToken">Cancels the reads.</param>
        /// <returns>The sum, at the column's scale; zero when no row holds a value.</returns>
        /// <exception cref="OverflowException">The sum passes 76 digits.</exception>
        public ValueTask<VortexDecimal> SumAsync(Func<Probe<TRecord>, Sym<VortexDecimal>> column, CancellationToken cancellationToken = default) =>
            scan.ScalarAsync(Aggregators.SumDecimal(Aggregators.Input(scan.Binding, column)), cancellationToken);

        /// <summary>The sum of the non-null values of a nullable decimal <paramref name="column"/> over the rows the scan keeps.</summary>
        /// <param name="column">The column.</param>
        /// <param name="cancellationToken">Cancels the reads.</param>
        /// <returns>The sum, at the column's scale; zero when no row holds a value.</returns>
        /// <exception cref="OverflowException">The sum passes 76 digits.</exception>
        public ValueTask<VortexDecimal> SumAsync(Func<Probe<TRecord>, Sym<VortexDecimal?>> column, CancellationToken cancellationToken = default) =>
            scan.ScalarAsync(Aggregators.SumDecimal(Aggregators.Input(scan.Binding, column)), cancellationToken);

        /// <summary>The mean of a decimal <paramref name="column"/> over the rows the scan keeps, from its exact sum.</summary>
        /// <param name="column">The column.</param>
        /// <param name="cancellationToken">Cancels the reads.</param>
        /// <returns>The mean; null when no row holds a value.</returns>
        public ValueTask<double?> AverageAsync(Func<Probe<TRecord>, Sym<VortexDecimal>> column, CancellationToken cancellationToken = default) =>
            scan.ScalarAsync(Aggregators.Average(Aggregators.Input(scan.Binding, column)), cancellationToken);

        /// <summary>The mean of the non-null values of a nullable decimal <paramref name="column"/> over the rows the scan keeps.</summary>
        /// <param name="column">The column.</param>
        /// <param name="cancellationToken">Cancels the reads.</param>
        /// <returns>The mean; null when no row holds a value.</returns>
        public ValueTask<double?> AverageAsync(Func<Probe<TRecord>, Sym<VortexDecimal?>> column, CancellationToken cancellationToken = default) =>
            scan.ScalarAsync(Aggregators.Average(Aggregators.Input(scan.Binding, column)), cancellationToken);
    }
}
