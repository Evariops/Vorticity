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
/// sink throws. <see cref="ExplainAsync"/> may be asked before the sink.
/// </remarks>
public sealed partial class Scan<TRecord>
    where TRecord : IVortexRecord<TRecord>
{
    private readonly ScanSource _source;
    private readonly ScanMetrics _metrics = new ScanMetrics();
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
    {
        _source = source;
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
        _filter &= predicate(new Probe<TRecord>(Binding));
        return this;
    }

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
    public ScanStatistics Statistics => ScanStatistics.From(_metrics);

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
        return new AsyncEnumerator(this, _source.BatchesAsync(spec, _metrics).GetAsyncEnumerator(token), binding);
    }

    /// <summary>The batches, each owned by the caller, who disposes it.</summary>
    /// <param name="cancellationToken">Cancels the scan at a batch boundary.</param>
    /// <returns>The batches.</returns>
    public async IAsyncEnumerable<RecordBatch> ToBatchesAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Begin();
        try
        {
            await foreach (RecordBatch batch in _source.BatchesAsync(Spec(), _metrics).WithCancellation(cancellationToken).ConfigureAwait(false))
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
            await foreach (RecordBatch batch in _source.BatchesAsync(Spec(), _metrics).WithCancellation(cancellationToken).ConfigureAwait(false))
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
            return await _source.CountAsync(Spec(), _metrics, cancellationToken).ConfigureAwait(false);
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
            return await _source.AnyAsync(Spec(), _metrics, cancellationToken).ConfigureAwait(false);
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

    /// <summary>What the scan will do, from statistics, zone maps and indexes, without reading a data segment.</summary>
    /// <param name="cancellationToken">Cancels the reads of the structures consulted.</param>
    /// <returns>The plan.</returns>
    public ValueTask<ScanPlan> ExplainAsync(CancellationToken cancellationToken = default) =>
        _source.ExplainAsync(Spec(), cancellationToken);

    /// <summary>The spec the sinks run.</summary>
    internal ScanSpec Spec() => new ScanSpec
    {
        Projection = Binding.Mask,
        Filter = _filter.Node,
        MatchesNothing = _filter.IsNone,
        Rows = _rows,
        Take = _take,
        OrderPath = _orderPath,
        Descending = _descending,
        Options = _take is not null && _filter.IsAll ? _options with { Compact = false } : _options,
    };

    internal ScanMetrics Metrics => _metrics;

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

        VortexTelemetry.ScanEnded(_metrics, _activity);
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
            value = await _source.ExtremeAsync(Spec(), target.Field, min, _metrics, cancellationToken).ConfigureAwait(false);
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
