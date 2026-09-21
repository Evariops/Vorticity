using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Expressions;
using Vorticity.Layouts;
using Vorticity.Scanning;

namespace Vorticity;

/// <summary>
/// A scan for a file whose schema is not known when the program is compiled: columns by name,
/// filters parsed from text or interpolated. It is the only place in the surface where names are
/// strings.
/// </summary>
/// <remarks>Single-use and not thread-safe, like the typed scan.</remarks>
public sealed class Scan
{
    private readonly ScanSource _source;
    private readonly FieldMask? _projection;
    private readonly ScanMetrics _metrics = new ScanMetrics();
    private VortexExpr? _filter;
    private RowRange? _rows;
    private long[]? _take;
    private ScanOptions _options = ScanOptions.Default;
    private int _used;

    internal Scan(ScanSource source, ReadOnlySpan<string> columns)
    {
        _source = source;
        if (columns.IsEmpty)
        {
            Schema = source.Schema;
            return;
        }

        FieldMaskBuilder mask = new FieldMaskBuilder();
        foreach (string column in columns)
        {
            mask.Include(ToolPaths.Resolve(source.Schema, column));
        }

        _projection = mask.Build();
        Schema = ToolPaths.Project(source.Schema, _projection.Value);
    }

    /// <summary>The columns the scan delivers.</summary>
    internal VortexSchema Schema { get; }

    internal VortexSchema FileSchema => _source.Schema;

    /// <summary>Keeps the rows <paramref name="filter"/> is true for; several calls are joined by <c>and</c>.</summary>
    /// <param name="filter">The filter, from <see cref="VortexExpr.Parse"/> or combined with operators.</param>
    /// <returns>This scan.</returns>
    /// <exception cref="VortexSchemaException">The filter names a column the file does not have, or compares one with a literal of a type it cannot compare to.</exception>
    public Scan Where(VortexExpr filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        VortexExpr resolved = ToolPaths.Check(_source.Schema, filter);
        _filter = _filter is null ? resolved : Expr.And(_filter, resolved);
        return this;
    }

    /// <summary>Keeps the rows an interpolated filter is true for: <c>$"day &gt;= {min} and city = {city}"</c>, each hole typed.</summary>
    /// <param name="filter">The interpolated filter; a hole of a type its column does not take throws when it is appended.</param>
    /// <returns>This scan.</returns>
    public Scan Where([InterpolatedStringHandlerArgument("")] ref FilterHandler filter) => Where(filter.Build());

    /// <summary>Scans the rows of <paramref name="range"/> only.</summary>
    /// <param name="range">The rows, intersected with the file.</param>
    /// <returns>This scan.</returns>
    public Scan Rows(RowRange range)
    {
        if (_take is not null)
        {
            throw new InvalidOperationException("A scan selects rows by range or by index, not both.");
        }

        _rows = range;
        return this;
    }

    /// <summary>Scans the rows at <paramref name="indices"/> only, delivered in their blocks with a selection.</summary>
    /// <param name="indices">File rows, in any order.</param>
    /// <returns>This scan.</returns>
    public Scan Rows(params ReadOnlySpan<long> indices)
    {
        if (_rows is not null)
        {
            throw new InvalidOperationException("A scan selects rows by range or by index, not both.");
        }

        long[] sorted = indices.ToArray();
        Array.Sort(sorted);
        _take = sorted;
        return this;
    }

    /// <summary>Sets how the scan runs.</summary>
    /// <param name="options">The options.</param>
    /// <returns>This scan.</returns>
    public Scan With(ScanOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        return this;
    }

    /// <summary>What the sink did; valid once it has run.</summary>
    public ScanStatistics Statistics => ScanStatistics.From(_metrics, 0);

    /// <summary>Enumerates the batches as borrowed views.</summary>
    /// <param name="cancellationToken">Cancels at a batch boundary.</param>
    /// <returns>The enumerator.</returns>
    public AsyncEnumerator GetAsyncEnumerator(CancellationToken cancellationToken = default)
    {
        Begin();
        return new AsyncEnumerator(this, _source.BatchesAsync(Spec(), _metrics).GetAsyncEnumerator(cancellationToken), Schema, _source.Session);
    }

    /// <summary>The batches, each owned by the caller, who disposes it.</summary>
    /// <param name="cancellationToken">Cancels at a batch boundary.</param>
    /// <returns>The batches.</returns>
    public async IAsyncEnumerable<RecordBatch> ToBatchesAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Begin();
        await foreach (RecordBatch batch in _source.BatchesAsync(Spec(), _metrics).WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            yield return RecordBatch.Own(batch.Arena, batch.RootIndex, batch.StartRow, Schema, _source.Session);
        }

        End();
    }

    /// <summary>The number of rows the scan keeps.</summary>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>The count.</returns>
    public async ValueTask<long> CountAsync(CancellationToken cancellationToken = default)
    {
        Begin();
        long count = await _source.CountAsync(Spec(), _metrics, cancellationToken).ConfigureAwait(false);
        End();
        return count;
    }

    /// <summary>Whether the scan keeps at least one row.</summary>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>Whether a row matches.</returns>
    public async ValueTask<bool> AnyAsync(CancellationToken cancellationToken = default)
    {
        Begin();
        bool any = await _source.AnyAsync(Spec(), _metrics, cancellationToken).ConfigureAwait(false);
        End();
        return any;
    }

    /// <summary>What the scan will do, without reading a data segment.</summary>
    /// <param name="cancellationToken">Cancels the reads of the structures consulted.</param>
    /// <returns>The plan.</returns>
    public ValueTask<ScanPlan> ExplainAsync(CancellationToken cancellationToken = default) => _source.ExplainAsync(Spec(), cancellationToken);

    private ScanSpec Spec() => new ScanSpec
    {
        Projection = _projection,
        Filter = _filter,
        Rows = _rows,
        Take = _take,
        Options = _take is not null && _filter is null ? _options with { Compact = false } : _options,
    };

    private void Begin()
    {
        if (Interlocked.Exchange(ref _used, 1) != 0)
        {
            throw new InvalidOperationException("A scan is single-use: build another one for another sink.");
        }

        _activity = VortexTelemetry.StartScan("tool");
    }

    private void End()
    {
        if (Interlocked.Exchange(ref _ended, 1) == 0)
        {
            VortexTelemetry.ScanEnded(_metrics, _activity);
            _activity = null;
        }
    }

    private System.Diagnostics.Activity? _activity;
    private int _ended;

    /// <summary>Enumerates the batches of a tool scan as borrowed <see cref="BatchView"/>s.</summary>
    public sealed class AsyncEnumerator : IAsyncDisposable
    {
        private readonly Scan _scan;
        private readonly IAsyncEnumerator<RecordBatch> _inner;
        private readonly VortexSchema _schema;
        private readonly VortexSession _session;

        internal AsyncEnumerator(Scan scan, IAsyncEnumerator<RecordBatch> inner, VortexSchema schema, VortexSession session)
        {
            _scan = scan;
            _inner = inner;
            _schema = schema;
            _session = session;
        }

        /// <summary>The current batch, valid until the next <see cref="MoveNextAsync"/>.</summary>
        public BatchView Current
        {
            get
            {
                RecordBatch batch = _inner.Current;
                return new BatchView(batch, batch.Arena, batch.RootIndex, _schema, batch.StartRow, batch.SelectionWords, batch.SelectedRows);
            }
        }

        internal VortexSession Session => _session;

        /// <summary>Moves to the next batch, releasing the current one.</summary>
        /// <returns>Whether there is one.</returns>
        public ValueTask<bool> MoveNextAsync() => _inner.MoveNextAsync();

        /// <summary>Releases the scan's buffers.</summary>
        /// <returns>A task that completes when every buffer is back.</returns>
        public ValueTask DisposeAsync()
        {
            _scan.End();
            return _inner.DisposeAsync();
        }
    }
}

/// <summary>
/// Builds a filter from an interpolated string: the literal text is the filter grammar and every
/// hole is a typed value, checked against the column it is compared to as it is appended.
/// </summary>
[InterpolatedStringHandler]
public ref struct FilterHandler
{
    private readonly Scan _scan;
    private readonly StringBuilder _text;
    private readonly List<FilterLiteral> _values;

    /// <summary>Starts a filter for <paramref name="scan"/>, whose schema resolves the columns the holes are compared to.</summary>
    /// <param name="literalLength">The length of the literal text.</param>
    /// <param name="formattedCount">The number of holes.</param>
    /// <param name="scan">The scan the filter is for.</param>
    public FilterHandler(int literalLength, int formattedCount, Scan scan)
    {
        _scan = scan ?? throw new ArgumentNullException(nameof(scan));
        _text = new StringBuilder(literalLength + (formattedCount * 4));
        _values = new List<FilterLiteral>(formattedCount);
    }

    /// <summary>Appends literal filter text.</summary>
    /// <param name="literal">The text.</param>
    public readonly void AppendLiteral(string literal) => _text.Append(literal);

    /// <summary>Appends a typed hole, checked against the column it is compared to.</summary>
    /// <typeparam name="T">The value's type.</typeparam>
    /// <param name="value">The value.</param>
    /// <exception cref="VortexSchemaException">The column the hole is compared to does not take a <typeparamref name="T"/>.</exception>
    public readonly void AppendFormatted<T>(T value)
    {
        string text = _text.ToString();
        VortexField column = ToolPaths.ColumnBeforeHole(_scan.FileSchema, text);
        if (value is null)
        {
            _values.Add(FilterLiteral.Null);
        }
        else
        {
            ClrFit.Require<T>(column.Type, $"Column '{column.Name}'", null);
            ColumnSym target = new ColumnSym(new FieldExpr(column.Name), column.Type, null, null, -1, []);
            ClrShape shape = ClrShape.For<T>.Value;
            if (shape.Kind is ClrKind.Decimal or ClrKind.VortexDecimal)
            {
                // Refused here when it has more digits than the column keeps; the check of the
                // whole filter then scales the exact text once, as it scales a number typed inline.
                _ = ToolPaths.ExactDecimal(target, value);
                _values.Add(FilterLiteral.From(value is decimal d ? d.ToString(CultureInfo.InvariantCulture) : value.ToString()!));
            }
            else
            {
                _values.Add(SymLowering.Literal(target, shape, value));
            }
        }

        _text.Append('?').Append(_values.Count - 1);
    }

    internal readonly VortexExpr Build() => ExprText.Parse(_text.ToString(), _values);
}
