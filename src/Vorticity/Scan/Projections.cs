using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Aggregating;

namespace Vorticity;

/// <summary>
/// One value per row a scan keeps, read as the scan reads its batches:
/// <c>await foreach (string city in file.Scan&lt;Reading&gt;().Select(r =&gt; r.City))</c>.
/// </summary>
/// <typeparam name="TResult">The value's type.</typeparam>
/// <remarks>
/// Enumerated once, by the pattern of <c>await foreach</c>, as a scan is: what returns a stream to
/// await carries <c>Async</c> in its name, and <c>Select</c> builds a query. The values come as the
/// batches of the column, each read into values once: an <c>await</c> per batch, none per value,
/// and a string per text value.
/// </remarks>
public sealed class Projection<TResult>
{
    private readonly ProjectionQuery _query;
    private CancellationToken _cancellationToken;

    internal Projection(ProjectionQuery query) => _query = query;

    /// <summary>What the scan did; valid once the projection has been enumerated.</summary>
    public ScanMetrics Metrics => ScanMetrics.From(_query.Counters);

    /// <summary>Makes <paramref name="cancellationToken"/> cancel the enumeration, as <c>WithCancellation</c> does a stream's.</summary>
    /// <param name="cancellationToken">Cancels the scan at a batch boundary.</param>
    /// <returns>This projection.</returns>
    public Projection<TResult> WithCancellation(CancellationToken cancellationToken)
    {
        _cancellationToken = cancellationToken;
        return this;
    }

    /// <summary>The values past the first <paramref name="count"/> rows the scan keeps.</summary>
    /// <param name="count">The rows to pass over.</param>
    /// <returns>The same projection, which delivers fewer.</returns>
    /// <exception cref="System.ArgumentOutOfRangeException"><paramref name="count"/> is negative.</exception>
    public Projection<TResult> Skip(int count) => new Projection<TResult>((ProjectionQuery)_query.Limit(count, long.MaxValue));

    /// <summary>The values of the first <paramref name="count"/> rows the scan keeps; the scan reads no further.</summary>
    /// <param name="count">The rows to deliver at most.</param>
    /// <returns>The same projection, which delivers fewer.</returns>
    /// <exception cref="System.ArgumentOutOfRangeException"><paramref name="count"/> is negative.</exception>
    public Projection<TResult> Take(int count) => new Projection<TResult>((ProjectionQuery)_query.Limit(0, count));

    /// <summary>The distinct values, each the first time it is met: a group by the value with no aggregate, which streams.</summary>
    /// <returns>The distinct values, in the order they are first met.</returns>
    public Aggregation<TResult> Distinct() => new Aggregation<TResult>(_query.Distinct());

    /// <summary>The values as a scan of a record of one member, which takes the value.</summary>
    /// <typeparam name="TRecord">The record, whose one member is of the value's type or its nullable form.</typeparam>
    /// <returns>A scan of the projection, which reads its scan when its sink runs.</returns>
    /// <exception cref="VortexSchemaException">The record has another number of members, or its member does not take the value.</exception>
    public Scan<TRecord> As<TRecord>()
        where TRecord : IVortexRecord<TRecord> =>
        Aggregation.Scan<TRecord>(_query);

    /// <summary>What the scan will read, from statistics, zone maps and indexes, without reading a data segment.</summary>
    /// <param name="cancellationToken">Cancels the reads of the structures consulted.</param>
    /// <returns>The plan.</returns>
    public ValueTask<ScanPlan> ExplainAsync(CancellationToken cancellationToken = default) => _query.ExplainAsync(cancellationToken);

    /// <summary>Runs the scan and enumerates the values.</summary>
    /// <param name="cancellationToken">Cancels the scan at a batch boundary, with the token <see cref="WithCancellation"/> gave.</param>
    /// <returns>The enumerator.</returns>
    public IAsyncEnumerator<TResult> GetAsyncEnumerator(CancellationToken cancellationToken = default) =>
        ValueEnumerator<TResult>.Of(_query, _cancellationToken, cancellationToken);

    /// <summary>The values as a stream, for the operators of <c>System.Linq.AsyncEnumerable</c>, which then run on the values delivered.</summary>
    /// <param name="cancellationToken">Cancels the scan at a batch boundary.</param>
    /// <returns>The values.</returns>
    public IAsyncEnumerable<TResult> ToValuesAsync(CancellationToken cancellationToken = default) =>
        new ValueStream<TResult>(_query, _cancellationToken, cancellationToken);

    /// <summary>Runs the scan and collects the values, a batch at a time.</summary>
    /// <param name="cancellationToken">Cancels the scan at a batch boundary.</param>
    /// <returns>The values, in the order the scan delivers them.</returns>
    public ValueTask<List<TResult>> ToListAsync(CancellationToken cancellationToken = default) =>
        ValueStream<TResult>.ListAsync(ValueEnumerator<TResult>.Of(_query, _cancellationToken, cancellationToken));

    /// <summary>Runs the scan and collects the values into an array, a batch at a time.</summary>
    /// <param name="cancellationToken">Cancels the scan at a batch boundary.</param>
    /// <returns>The values, in the order the scan delivers them.</returns>
    public async ValueTask<TResult[]> ToArrayAsync(CancellationToken cancellationToken = default) =>
        [.. await ToListAsync(cancellationToken).ConfigureAwait(false)];
}

/// <summary>
/// Several values per row a scan keeps, read through a record:
/// <c>scan.Select(r =&gt; (r.Day, r.City)).As&lt;DayCity&gt;()</c>, a scan of computed columns.
/// </summary>
/// <remarks>
/// Several values have no .NET type until a record gives them one, so a <see cref="Projection"/> is
/// not enumerable: <see cref="As{TRecord}"/> is how it is read, as batches whose columns are the
/// scan's own, borrowed, or as records.
/// </remarks>
public sealed class Projection
{
    private readonly ProjectionQuery _query;

    internal Projection(ProjectionQuery query) => _query = query;

    /// <summary>What the scan did; valid once the projection has been read.</summary>
    public ScanMetrics Metrics => ScanMetrics.From(_query.Counters);

    /// <summary>The rows past the first <paramref name="count"/> the scan keeps.</summary>
    /// <param name="count">The rows to pass over.</param>
    /// <returns>The same projection, which delivers fewer.</returns>
    /// <exception cref="System.ArgumentOutOfRangeException"><paramref name="count"/> is negative.</exception>
    public Projection Skip(int count) => new Projection((ProjectionQuery)_query.Limit(count, long.MaxValue));

    /// <summary>The first <paramref name="count"/> rows the scan keeps; the scan reads no further.</summary>
    /// <param name="count">The rows to deliver at most.</param>
    /// <returns>The same projection, which delivers fewer.</returns>
    /// <exception cref="System.ArgumentOutOfRangeException"><paramref name="count"/> is negative.</exception>
    public Projection Take(int count) => new Projection((ProjectionQuery)_query.Limit(0, count));

    /// <summary>The distinct tuples of values, each the first time it is met: a group by them with no aggregate, which streams.</summary>
    /// <returns>The distinct tuples, read through a record.</returns>
    public Aggregation Distinct() => new Aggregation(_query.Distinct());

    /// <summary>The projection as a scan of <typeparamref name="TRecord"/>, whose members take the elements in order.</summary>
    /// <typeparam name="TRecord">The record, whose members, in declaration order, are of the elements' types or their nullable forms.</typeparam>
    /// <returns>A scan of the projection, which reads its scan when its sink runs.</returns>
    /// <exception cref="VortexSchemaException">The record has another number of members, or a member does not take its element.</exception>
    public Scan<TRecord> As<TRecord>()
        where TRecord : IVortexRecord<TRecord> =>
        Aggregation.Scan<TRecord>(_query);

    /// <summary>What the scan will read, from statistics, zone maps and indexes, without reading a data segment.</summary>
    /// <param name="cancellationToken">Cancels the reads of the structures consulted.</param>
    /// <returns>The plan.</returns>
    public ValueTask<ScanPlan> ExplainAsync(CancellationToken cancellationToken = default) => _query.ExplainAsync(cancellationToken);
}
