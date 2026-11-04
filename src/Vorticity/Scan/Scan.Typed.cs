using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Expressions;
using Vorticity.Scanning;

namespace Vorticity;

/// <summary>
/// A scan of a file typed by <typeparamref name="TRecord"/>: composed with <see cref="Where"/>,
/// <see cref="Rows(RowRange)"/>, <see cref="OrderBy{TKey}"/> and <see cref="With"/>, then consumed by
/// one sink: an enumeration of borrowed columns, owned batches, rows, or an answer.
/// </summary>
/// <typeparam name="TRecord">The record whose members are the scan's columns.</typeparam>
/// <remarks>
/// A builder is single-use and not thread-safe: every composition call returns it, and a second
/// sink throws. <see cref="ExplainAsync(CancellationToken)"/> may be asked before the sink.
/// </remarks>
public sealed partial class Scan<TRecord>
    where TRecord : IVortexRecord<TRecord>
{
    private readonly ScanSource _source;
    private readonly ScanCounters _counters;
    private RecordBinding? _binding;
    private Predicate _filter = Predicate.All;
    private RowRange? _rows;
    private long[]? _take;
    private string? _orderPath;
    private bool _descending;
    private ScanOptions _options = ScanOptions.Default;
    private CancellationToken _cancellation;
    private VortexSchema? _schema;
    private int _used;

    internal Scan(ScanSource source)
        : this(source, new ScanCounters())
    {
    }

    /// <summary>A scan that reports <paramref name="metrics"/> as its own: a result's, whose reads are its query's.</summary>
    internal Scan(ScanSource source, ScanCounters metrics)
    {
        _source = source;
        _counters = metrics;
    }

    internal RecordBinding Binding => _binding ??= RecordBinding.For<TRecord>(_source.Schema, _source.Session.Options.Extensions);

    internal ScanSource Source => _source;

    /// <summary>Keeps the rows <paramref name="predicate"/> is true for; several calls are joined by <c>&amp;</c>.</summary>
    /// <param name="predicate">A lambda over the record's columns, run once, now.</param>
    /// <returns>This scan.</returns>
    /// <exception cref="VortexSchemaException">A literal or a column does not fit the file.</exception>
    public Scan<TRecord> Where(Func<Probe<TRecord>, Predicate> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        Predicate rows = predicate(new Probe<TRecord>(Binding));
        if (rows.Node is { } node && Aggregating.GroupPredicates.ReadsResults(node))
        {
            throw new InvalidOperationException("A filter on rows compares columns: an aggregate is a result of a group, filtered by a Where after the GroupBy.");
        }

        _filter &= rows;
        return this;
    }

    /// <summary>The distinct values a semi-join takes at most by default (spec G, 4): past them, filter its first question further.</summary>
    public const int SemiJoinValues = 1 << 20;

    /// <summary>
    /// Keeps the rows whose <paramref name="column"/> is one of the values <paramref name="values"/> gives, a semi-join
    /// (spec G, 4): the first question read now, each of its values once, the set an <c>In</c> of this scan's filter, which
    /// the column's localisation, postings and zone maps prune by and its rows are judged by.
    /// </summary>
    /// <typeparam name="TKey">The column's type.</typeparam>
    /// <param name="column">The column the values are looked for in.</param>
    /// <param name="values">The first question: a projection of one value a row, over any source.</param>
    /// <param name="maxValues">The distinct values taken at most.</param>
    /// <param name="cancellationToken">Cancels the first question.</param>
    /// <returns>This scan, the first question read; with no value, a scan that keeps no row and reads none.</returns>
    /// <exception cref="InvalidOperationException">The first question gives more than <paramref name="maxValues"/> distinct values.</exception>
    public async ValueTask<Scan<TRecord>> WhereInAsync<TKey>(
        Func<Probe<TRecord>, Sym<TKey>> column, Projection<TKey> values, int maxValues = SemiJoinValues, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(column);
        ArgumentNullException.ThrowIfNull(values);
        ArgumentOutOfRangeException.ThrowIfNegative(maxValues);
        HashSet<TKey> distinct = [];
        await foreach (TKey value in values.ToValuesAsync(cancellationToken).ConfigureAwait(false))
        {
            if (value is not null && distinct.Add(value) && distinct.Count > maxValues)
            {
                throw new InvalidOperationException(
                    $"The first question of a semi-join gives more than {maxValues:N0} values: filter it further, or take more of them.");
            }
        }

        TKey[] set = [.. distinct];
        return Where(record => column(record).In(set));
    }

    /// <summary>
    /// Keeps the rows whose values of two columns are, together, one of the tuples <paramref name="values"/> gives, a
    /// semi-join on a tuple, as SQL's <c>(a, b) IN (SELECT x, y …)</c>: the first question read now, each tuple once, a tuple
    /// holding a null left out; the set a filter of this scan (<see cref="SymTuples"/>), which the columns' sets of values
    /// prune by and its rows are judged by.
    /// </summary>
    /// <typeparam name="T1">The first column's type.</typeparam>
    /// <typeparam name="T2">The second column's type.</typeparam>
    /// <param name="columns">The columns the tuples are looked for in: <c>r =&gt; (r.Service, r.Host)</c>.</param>
    /// <param name="values">The first question: a projection of two values a row, <c>other.Select(r =&gt; (r.Service, r.Host))</c>.</param>
    /// <param name="maxValues">The distinct tuples taken at most.</param>
    /// <param name="cancellationToken">Cancels the first question.</param>
    /// <returns>This scan, the first question read; with no tuple, a scan that keeps no row and reads none.</returns>
    /// <exception cref="InvalidOperationException">The first question gives more than <paramref name="maxValues"/> distinct tuples.</exception>
    /// <exception cref="ArgumentException">The first question projects another number of columns, or a column is no column of the scan.</exception>
    public async ValueTask<Scan<TRecord>> WhereInAsync<T1, T2>(
        Func<Probe<TRecord>, (Sym<T1>, Sym<T2>)> columns, Projection values, int maxValues = SemiJoinValues, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(values);
        ArgumentOutOfRangeException.ThrowIfNegative(maxValues);
        HashSet<(T1, T2)> distinct = [];
        await foreach ((T1, T2) tuple in values.TuplesAsync<T1, T2>(cancellationToken).ConfigureAwait(false))
        {
            if (tuple.Item1 is not null && tuple.Item2 is not null && distinct.Add(tuple) && distinct.Count > maxValues)
            {
                throw TooManyTuples(maxValues);
            }
        }

        (T1, T2)[] set = [.. distinct];
        return Where(record => columns(record).In(set));
    }

    /// <summary>
    /// Keeps the rows whose values of three columns are, together, one of the tuples <paramref name="values"/> gives, a
    /// semi-join on a tuple (<see cref="WhereInAsync{T1, T2}(Func{Probe{TRecord}, ValueTuple{Sym{T1}, Sym{T2}}}, Projection, int, CancellationToken)"/>).
    /// </summary>
    /// <typeparam name="T1">The first column's type.</typeparam>
    /// <typeparam name="T2">The second column's type.</typeparam>
    /// <typeparam name="T3">The third column's type.</typeparam>
    /// <param name="columns">The columns the tuples are looked for in.</param>
    /// <param name="values">The first question: a projection of three values a row.</param>
    /// <param name="maxValues">The distinct tuples taken at most.</param>
    /// <param name="cancellationToken">Cancels the first question.</param>
    /// <returns>This scan, the first question read; with no tuple, a scan that keeps no row and reads none.</returns>
    /// <exception cref="InvalidOperationException">The first question gives more than <paramref name="maxValues"/> distinct tuples.</exception>
    /// <exception cref="ArgumentException">The first question projects another number of columns, or a column is no column of the scan.</exception>
    public async ValueTask<Scan<TRecord>> WhereInAsync<T1, T2, T3>(
        Func<Probe<TRecord>, (Sym<T1>, Sym<T2>, Sym<T3>)> columns, Projection values, int maxValues = SemiJoinValues, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(values);
        ArgumentOutOfRangeException.ThrowIfNegative(maxValues);
        HashSet<(T1, T2, T3)> distinct = [];
        await foreach ((T1, T2, T3) tuple in values.TuplesAsync<T1, T2, T3>(cancellationToken).ConfigureAwait(false))
        {
            if (tuple.Item1 is not null && tuple.Item2 is not null && tuple.Item3 is not null && distinct.Add(tuple) && distinct.Count > maxValues)
            {
                throw TooManyTuples(maxValues);
            }
        }

        (T1, T2, T3)[] set = [.. distinct];
        return Where(record => columns(record).In(set));
    }

    /// <summary>
    /// Keeps the rows whose values of four columns are, together, one of the tuples <paramref name="values"/> gives, a
    /// semi-join on a tuple (<see cref="WhereInAsync{T1, T2}(Func{Probe{TRecord}, ValueTuple{Sym{T1}, Sym{T2}}}, Projection, int, CancellationToken)"/>).
    /// </summary>
    /// <typeparam name="T1">The first column's type.</typeparam>
    /// <typeparam name="T2">The second column's type.</typeparam>
    /// <typeparam name="T3">The third column's type.</typeparam>
    /// <typeparam name="T4">The fourth column's type.</typeparam>
    /// <param name="columns">The columns the tuples are looked for in.</param>
    /// <param name="values">The first question: a projection of four values a row.</param>
    /// <param name="maxValues">The distinct tuples taken at most.</param>
    /// <param name="cancellationToken">Cancels the first question.</param>
    /// <returns>This scan, the first question read; with no tuple, a scan that keeps no row and reads none.</returns>
    /// <exception cref="InvalidOperationException">The first question gives more than <paramref name="maxValues"/> distinct tuples.</exception>
    /// <exception cref="ArgumentException">The first question projects another number of columns, or a column is no column of the scan.</exception>
    public async ValueTask<Scan<TRecord>> WhereInAsync<T1, T2, T3, T4>(
        Func<Probe<TRecord>, (Sym<T1>, Sym<T2>, Sym<T3>, Sym<T4>)> columns, Projection values, int maxValues = SemiJoinValues,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(values);
        ArgumentOutOfRangeException.ThrowIfNegative(maxValues);
        HashSet<(T1, T2, T3, T4)> distinct = [];
        await foreach ((T1, T2, T3, T4) tuple in values.TuplesAsync<T1, T2, T3, T4>(cancellationToken).ConfigureAwait(false))
        {
            if (tuple.Item1 is not null && tuple.Item2 is not null && tuple.Item3 is not null && tuple.Item4 is not null
                && distinct.Add(tuple) && distinct.Count > maxValues)
            {
                throw TooManyTuples(maxValues);
            }
        }

        (T1, T2, T3, T4)[] set = [.. distinct];
        return Where(record => columns(record).In(set));
    }

    private static InvalidOperationException TooManyTuples(int maxValues) =>
        new InvalidOperationException($"The first question of a semi-join gives more than {maxValues:N0} tuples: filter it further, or take more of them.");

    /// <summary>Scans the rows of <paramref name="range"/> only.</summary>
    /// <param name="range">The rows, in file coordinates; intersected with the file.</param>
    /// <returns>This scan.</returns>
    /// <exception cref="InvalidOperationException">The scan already selects rows by index.</exception>
    public Scan<TRecord> Rows(RowRange range)
    {
        if (_take is not null)
        {
            throw new InvalidOperationException("A scan selects rows by range or by index, not both.");
        }

        _rows = range;
        return this;
    }

    /// <summary>Scans the rows at <paramref name="indices"/> only, delivered in the blocks that hold them with a <see cref="Selection"/> of them.</summary>
    /// <param name="indices">File rows, in any order; sorted and deduplicated.</param>
    /// <returns>This scan.</returns>
    /// <exception cref="InvalidOperationException">The scan already selects a range.</exception>
    public Scan<TRecord> Rows(params ReadOnlySpan<long> indices)
    {
        if (_rows is not null)
        {
            throw new InvalidOperationException("A scan selects rows by range or by index, not both.");
        }

        long[] sorted = indices.ToArray();
        Compute.SpanSort.Sort(sorted);
        int distinct = 0;
        for (int i = 0; i < sorted.Length; i++)
        {
            if (i == 0 || sorted[i] != sorted[distinct - 1])
            {
                sorted[distinct++] = sorted[i];
            }
        }

        _take = sorted.AsSpan(0, distinct).ToArray();
        return this;
    }

    /// <summary>Delivers the rows in the ascending order of <paramref name="key"/>, from its key source: a column the statistics say is sorted, or a sorted-runs index.</summary>
    /// <typeparam name="TKey">The key column's type.</typeparam>
    /// <param name="key">The key column.</param>
    /// <returns>This scan.</returns>
    /// <remarks>A null key comes last; <c>orderby r.Key</c> in a query is this.</remarks>
    public Scan<TRecord> OrderBy<TKey>(Func<Probe<TRecord>, Sym<TKey>> key) => Ordered(key, descending: false);

    /// <summary>Delivers the rows in the descending order of <paramref name="key"/>, from its key source.</summary>
    /// <typeparam name="TKey">The key column's type.</typeparam>
    /// <param name="key">The key column.</param>
    /// <returns>This scan.</returns>
    /// <remarks>A null key comes last here too; <c>orderby r.Key descending</c> in a query is this.</remarks>
    public Scan<TRecord> OrderByDescending<TKey>(Func<Probe<TRecord>, Sym<TKey>> key) => Ordered(key, descending: true);

    private Scan<TRecord> Ordered<TKey>(Func<Probe<TRecord>, Sym<TKey>> key, bool descending)
    {
        ArgumentNullException.ThrowIfNull(key);
        ColumnSym column = key(new Probe<TRecord>(Binding)).Column;
        if (column.Field is Compute.FunctionFieldExpr or Compute.MappedFieldExpr)
        {
            throw new ArgumentException(
                $"'{column.Field.Key}' is a function of a column: a scan's order walks a column's key source, and a function of one has none. Order the groups of a GroupBy by it instead.",
                nameof(key));
        }

        _orderPath = column.Field.Path;
        _descending = descending;
        return this;
    }

    /// <summary>Sets how the scan runs.</summary>
    /// <param name="options">The options.</param>
    /// <returns>This scan.</returns>
    public Scan<TRecord> With(ScanOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        return this;
    }

    /// <summary>
    /// Cancels the enumeration at a batch boundary: <c>await foreach (var c in scan.WithCancellation(ct))</c>,
    /// since <c>await foreach</c> passes no token to <see cref="GetAsyncEnumerator"/>.
    /// </summary>
    /// <param name="cancellationToken">The token; a token passed to <see cref="GetAsyncEnumerator"/> itself takes precedence.</param>
    /// <returns>This scan.</returns>
    public Scan<TRecord> WithCancellation(CancellationToken cancellationToken)
    {
        _cancellation = cancellationToken;
        return this;
    }

    /// <summary>What the last sink did; valid once it has run.</summary>
    public ScanMetrics Metrics => ScanMetrics.From(_counters);

    /// <summary>Enumerates the batches as borrowed columns: <c>await foreach (var (day, celsius, city) in scan)</c>.</summary>
    /// <param name="cancellationToken">Cancels the scan at a batch boundary.</param>
    /// <returns>The enumerator; <c>Current</c> is valid until the next <c>MoveNextAsync</c>.</returns>
    public AsyncEnumerator GetAsyncEnumerator(CancellationToken cancellationToken = default)
    {
        Begin();
        RecordBinding binding = Binding;

        // The one sink whose consumer can read a column encoded: Column<T>.Encoding and its views.
        ScanSpec spec = Spec() with { KeepEncodings = true };
        CancellationToken token = cancellationToken.CanBeCanceled ? cancellationToken : _cancellation;
        return new AsyncEnumerator(this, _source.Rows.BatchesAsync(spec, _counters).GetAsyncEnumerator(token), binding);
    }

    /// <summary>The batches, each owned by the caller, who disposes it.</summary>
    /// <param name="cancellationToken">Cancels the scan at a batch boundary.</param>
    /// <returns>The batches.</returns>
    public async IAsyncEnumerable<RecordBatch> ToBatchesAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Begin();
        try
        {
            await foreach (RecordBatch batch in _source.Rows.BatchesAsync(Spec(), _counters).WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                yield return Own(batch);
            }
        }
        finally
        {
            End();
        }
    }

    /// <summary>The rows, one <typeparamref name="TRecord"/> each: a field copy per row, and an allocation per row for a string, a list or a nested class.</summary>
    /// <param name="cancellationToken">Cancels the scan at a batch boundary.</param>
    /// <returns>The rows; LINQ over them runs on the client, after the scan.</returns>
    public async IAsyncEnumerable<TRecord> ToRecordsAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Begin();
        RecordBinding binding = Binding;
        TRecord[] rows = [];
        try
        {
            await foreach (RecordBatch batch in _source.Rows.BatchesAsync(Spec(), _counters).WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                int count = Fill(batch, binding, ref rows);
                for (int i = 0; i < count; i++)
                {
                    yield return rows[i];
                }
            }
        }
        finally
        {
            if (rows.Length > 0)
            {
                ArrayPool<TRecord>.Shared.Return(rows, clearArray: RuntimeHelpers.IsReferenceOrContainsReferences<TRecord>());
            }

            End();
        }
    }

    /// <summary>The number of rows the scan keeps, answered from the statistics, the zone maps or an exact index when they settle it.</summary>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>The count.</returns>
    public async ValueTask<long> CountAsync(CancellationToken cancellationToken = default)
    {
        Begin();
        try
        {
            return (await _source.Rows.AnswerAsync(Query(LogicalShape.Count).Spec().Reading([]), LogicalShape.Count, _counters, cancellationToken).ConfigureAwait(false)).Rows;
        }
        finally
        {
            End();
        }
    }

    /// <summary>Whether the scan keeps at least one row, stopping at the first proof.</summary>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>Whether a row matches.</returns>
    public async ValueTask<bool> AnyAsync(CancellationToken cancellationToken = default)
    {
        Begin();
        try
        {
            return (await _source.Rows.AnswerAsync(Query(LogicalShape.Any).Spec().Reading([]), LogicalShape.Any, _counters, cancellationToken).ConfigureAwait(false)).Rows > 0;
        }
        finally
        {
            End();
        }
    }

    /// <summary>The smallest non-null value of <paramref name="column"/> among the rows the scan keeps; the default when there is none.</summary>
    /// <typeparam name="T">The column's type.</typeparam>
    /// <param name="column">The column.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>The minimum.</returns>
    public ValueTask<T?> MinAsync<T>(Func<Probe<TRecord>, Sym<T>> column, CancellationToken cancellationToken = default) =>
        ExtremeAsync(column, min: true, cancellationToken);

    /// <summary>The largest non-null value of <paramref name="column"/> among the rows the scan keeps; the default when there is none.</summary>
    /// <typeparam name="T">The column's type.</typeparam>
    /// <param name="column">The column.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>The maximum.</returns>
    public ValueTask<T?> MaxAsync<T>(Func<Probe<TRecord>, Sym<T>> column, CancellationToken cancellationToken = default) =>
        ExtremeAsync(column, min: false, cancellationToken);

    /// <summary>What the scan will do, worked out from what the source holds in memory, reading nothing (spec B, 7).</summary>
    /// <param name="cancellationToken">Cancels the explanation.</param>
    /// <returns>The plan.</returns>
    public ValueTask<ScanPlan> ExplainAsync(CancellationToken cancellationToken = default) =>
        _source.PlanAsync(Spec(), analyze: false, cancellationToken);

    /// <summary>
    /// What the scan will do; with <paramref name="analyze"/>, the structures its read would consult read as it reads them,
    /// statistics, zone maps, indexes, which give what it prunes exactly, and no data segment.
    /// </summary>
    /// <param name="analyze">Whether to read the structures the read would consult.</param>
    /// <param name="cancellationToken">Cancels the reads of the structures consulted.</param>
    /// <returns>The plan.</returns>
    public ValueTask<ScanPlan> ExplainAsync(bool analyze, CancellationToken cancellationToken = default) =>
        _source.PlanAsync(Spec(), analyze, cancellationToken);

    /// <summary>The spec the sinks of rows run: the rows of the question, in its order.</summary>
    internal ScanSpec Spec() => Query(_orderPath is null ? RowsShape.All : new RowsShape(_orderPath, _descending)).Spec();

    /// <summary>The question the scan asks, its answer of <paramref name="shape"/>.</summary>
    internal LogicalQuery Query(LogicalShape shape) => new LogicalQuery(_source.Logical, _source.Schema, shape)
    {
        Reads = Binding.Mask,

        // A predicate on a column through a table, as one on the column (spec G, 5).
        Filter = Compute.TableFunctions.Preimages(_filter.Node),
        MatchesNothing = _filter.IsNone,
        Range = _rows,
        Positions = _take,
        Options = _options,
    };

    internal ScanCounters Counters => _counters;

    internal Predicate Filter => _filter;

    /// <summary>Claims the builder for its one sink.</summary>
    internal void Begin()
    {
        if (Interlocked.Exchange(ref _used, 1) != 0)
        {
            throw new InvalidOperationException("A scan is single-use: build another one for another sink.");
        }

        _activity = VortexTelemetry.StartScan("typed");
    }

    /// <summary>Closes the sink: its statistics, its activity and the process counters; a second call does nothing.</summary>
    internal void End()
    {
        if (Interlocked.Exchange(ref _ended, 1) != 0)
        {
            return;
        }

        VortexTelemetry.ScanEnded(_counters, _activity);
        _activity = null;
    }

    private System.Diagnostics.Activity? _activity;
    private int _ended;

    private async ValueTask<T?> ExtremeAsync<T>(Func<Probe<TRecord>, Sym<T>> column, bool min, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(column);
        ColumnSym target = column(new Probe<TRecord>(Binding)).Column;
        Begin();
        FilterLiteral value;
        try
        {
            ExtremeShape extreme = new ExtremeShape(target.Field, min);
            value = (await _source.Rows.AnswerAsync(Query(extreme).Spec().Reading([target.Field]), extreme, _counters, cancellationToken).ConfigureAwait(false)).Value;
        }
        finally
        {
            End();
        }

        return LiteralValues.ToValue<T>(value, target.Type);
    }

    /// <summary>The schema every batch of this scan carries: the record's columns, in file order.</summary>
    /// <remarks>
    /// One instance for the whole scan, so that every owned batch shares it and binds the record
    /// once; a schema made per batch would miss the binding cache, which is keyed by the instance.
    /// </remarks>
    private VortexSchema Schema => _schema ??= ToolPaths.Project(_source.Schema, Binding.Mask);

    private RecordBatch Own(RecordBatch borrowed) =>
        RecordBatch.Own(borrowed.Arena, borrowed.RootIndex, borrowed.StartRow, Schema, _source.Session, borrowed.SelectionWords, borrowed.SelectedRows);

    private static int Fill(RecordBatch batch, RecordBinding binding, ref TRecord[] rows)
    {
        int count = batch.RowCount;
        if (rows.Length < count)
        {
            if (rows.Length > 0)
            {
                ArrayPool<TRecord>.Shared.Return(rows, clearArray: RuntimeHelpers.IsReferenceOrContainsReferences<TRecord>());
            }

            rows = ArrayPool<TRecord>.Shared.Rent(count);
        }

        Columns<TRecord> columns = new Columns<TRecord>(batch, batch.Arena, batch.RootIndex, binding, batch.StartRow, batch.SelectionWords, batch.SelectedRows, projected: true);
        TRecord.ReadRows(columns, rows.AsSpan(0, count));
        if (batch.SelectionWords.IsEmpty)
        {
            return count;
        }

        // A batch delivered whole under a selection yields only its selected rows, in order.
        int kept = 0;
        foreach (int row in columns.Selection)
        {
            rows[kept++] = rows[row];
        }

        return kept;
    }

    /// <summary>Enumerates the batches of a scan as borrowed <see cref="Columns{TRecord}"/>.</summary>
    public sealed class AsyncEnumerator : IAsyncDisposable
    {
        private readonly Scan<TRecord> _scan;
        private readonly IAsyncEnumerator<RecordBatch> _inner;
        private readonly RecordBinding _binding;

        internal AsyncEnumerator(Scan<TRecord> scan, IAsyncEnumerator<RecordBatch> inner, RecordBinding binding)
        {
            _scan = scan;
            _inner = inner;
            _binding = binding;
        }

        /// <summary>The current batch's columns, valid until the next <see cref="MoveNextAsync"/>.</summary>
        public Columns<TRecord> Current
        {
            get
            {
                RecordBatch batch = _inner.Current;
                return new Columns<TRecord>(batch, batch.Arena, batch.RootIndex, _binding, batch.StartRow, batch.SelectionWords, batch.SelectedRows, projected: true);
            }
        }

        /// <summary>Moves to the next batch, releasing the current one.</summary>
        /// <returns>Whether there is one.</returns>
        [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
        public async ValueTask<bool> MoveNextAsync()
        {
            if (await _inner.MoveNextAsync().ConfigureAwait(false))
            {
                return true;
            }

            _scan.End();
            return false;
        }

        /// <summary>Releases the scan's buffers.</summary>
        /// <returns>A task that completes when every buffer is back.</returns>
        public ValueTask DisposeAsync()
        {
            _scan.End();
            return _inner.DisposeAsync();
        }
    }
}
