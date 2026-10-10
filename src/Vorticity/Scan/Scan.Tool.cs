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
    private readonly ScanCounters _counters = new ScanCounters();
    private VortexExpr? _filter;
    private RowRange? _rows;
    private long[]? _take;
    private ScanOptions _options = ScanOptions.Default;
    private CancellationToken _cancellation;
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
    /// <param name="indices">File rows, in any order; sorted and deduplicated.</param>
    /// <returns>This scan.</returns>
    public Scan Rows(params ReadOnlySpan<long> indices)
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

    /// <summary>Sets how the scan runs.</summary>
    /// <param name="options">The options.</param>
    /// <returns>This scan.</returns>
    public Scan With(ScanOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        return this;
    }

    /// <summary>
    /// Cancels the enumeration at a batch boundary: <c>await foreach (BatchView b in scan.WithCancellation(ct))</c>,
    /// since <c>await foreach</c> passes no token to <see cref="GetAsyncEnumerator"/>.
    /// </summary>
    /// <param name="cancellationToken">The token; a token passed to <see cref="GetAsyncEnumerator"/> itself takes precedence.</param>
    /// <returns>This scan.</returns>
    public Scan WithCancellation(CancellationToken cancellationToken)
    {
        _cancellation = cancellationToken;
        return this;
    }

    /// <summary>What the sink did; valid once it has run.</summary>
    public ScanMetrics Metrics => ScanMetrics.From(_counters);

    /// <summary>Enumerates the batches as borrowed views.</summary>
    /// <param name="cancellationToken">Cancels at a batch boundary.</param>
    /// <returns>The enumerator.</returns>
    public AsyncEnumerator GetAsyncEnumerator(CancellationToken cancellationToken = default)
    {
        Begin();
        CancellationToken token = cancellationToken.CanBeCanceled ? cancellationToken : _cancellation;
        return new AsyncEnumerator(this, _source.BatchesAsync(Spec(), _counters).GetAsyncEnumerator(token), Schema, _source.Session);
    }

    /// <summary>The batches, each owned by the caller, who disposes it.</summary>
    /// <param name="cancellationToken">Cancels at a batch boundary.</param>
    /// <returns>The batches.</returns>
    public async IAsyncEnumerable<RecordBatch> ToBatchesAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Begin();
        try
        {
            await foreach (RecordBatch batch in _source.BatchesAsync(Spec(), _counters).WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                yield return RecordBatch.Own(batch.Arena, batch.RootIndex, batch.StartRow, Schema, _source.Session, batch.SelectionWords, batch.SelectedRows);
            }
        }
        finally
        {
            End();
        }
    }

    /// <summary>The number of rows the scan keeps.</summary>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>The count.</returns>
    public async ValueTask<long> CountAsync(CancellationToken cancellationToken = default)
    {
        Begin();
        try
        {
            return await _source.CountAsync(Spec(), _counters, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            End();
        }
    }

    /// <summary>Whether the scan keeps at least one row.</summary>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>Whether a row matches.</returns>
    public async ValueTask<bool> AnyAsync(CancellationToken cancellationToken = default)
    {
        Begin();
        try
        {
            return await _source.AnyAsync(Spec(), _counters, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            End();
        }
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
            VortexTelemetry.ScanEnded(_counters, _activity);
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
            ClrShape shape = ClrShape.For<T>.Value;
            if (shape.Kind is ClrKind.Signed or ClrKind.Unsigned or ClrKind.Float)
            {
                // A number is a value, not a span over the column's storage: any numeric column
                // takes it, and the check of the whole filter places it against the column's width
                // and type as it places a number typed inline. Only a column that is not numeric
                // refuses it, and says why in the words a member's binding would use.
                VortexType numeric = column.Type.Kind == VortexTypeKind.Extension && column.Type.StorageType is { Kind: VortexTypeKind.Primitive } storage
                    ? storage
                    : column.Type;
                if (numeric.Kind != VortexTypeKind.Primitive)
                {
                    ClrFit.Require<T>(column.Type, $"Column '{column.Name}'", null);
                }
            }
            else
            {
                ClrFit.Require<T>(column.Type, $"Column '{column.Name}'", null);
            }

            ColumnSym target = new ColumnSym(new FieldExpr(column.Name), column.Type, null, null, -1, []);
            if (shape.Kind is ClrKind.Decimal or ClrKind.VortexDecimal or ClrKind.Integer128 or ClrKind.UInteger128 or ClrKind.BigInteger)
            {
                // Refused here when it has more digits than the column keeps; the check of the
                // whole filter then scales the exact text once, as it scales a number typed inline.
                // An integer of 128 bits or more is a decimal of scale 0, and goes the same way.
                _ = ToolPaths.ExactDecimal(target, value);
                _values.Add(FilterLiteral.From(value is IFormattable formattable
                    ? formattable.ToString(null, CultureInfo.InvariantCulture)
                    : value.ToString()!));
            }
            else if (shape.Kind is ClrKind.TimeOnly or ClrKind.DateTime or ClrKind.DateTimeOffset
                && column.Type.ExtensionId is ExtensionIds.Time or ExtensionIds.Timestamp)
            {
                // As round-trip text, to every tick: which stored value it meets depends on the
                // operator it is compared with, and the check of the whole filter knows that one.
                _values.Add(FilterLiteral.From(value switch
                {
                    TimeOnly time => time.ToString("O", CultureInfo.InvariantCulture),
                    DateTime instant => (column.Type.TimeZone is not null && instant.Kind == DateTimeKind.Local
                        ? instant.ToUniversalTime()
                        : DateTime.SpecifyKind(instant, instant.Kind == DateTimeKind.Utc ? DateTimeKind.Utc : DateTimeKind.Unspecified)).ToString("O", CultureInfo.InvariantCulture),
                    _ => ((DateTimeOffset)(object)value).ToString("O", CultureInfo.InvariantCulture),
                }));
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
