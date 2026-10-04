using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Aggregating;
using Vorticity.Arrays;
using Vorticity.Compute;
using Vorticity.Expressions;
using Vorticity.Layouts;
using Vorticity.Scanning;
using Vorticity.Types;
using Vorticity.Writing;

namespace Vorticity;

/// <summary>An element of a projection, whatever its type: a column of the scan, delivered as a column of the result.</summary>
internal interface IProjectionElement
{
    /// <summary>The scan's column the element reads.</summary>
    ColumnSym Source { get; }

    /// <summary>The element's column, of the column's own type, named <paramref name="name"/>.</summary>
    ProjectedColumn Column(string name);

    /// <summary>The element's column as a record's member takes it: named and typed by the member.</summary>
    /// <exception cref="VortexSchemaException">The member's column does not take the element's values.</exception>
    ProjectedColumn Column(VortexField member, int position, Type record);

    /// <summary>The element as component <paramref name="component"/> of a key: what a <c>Distinct</c> groups by.</summary>
    IResultNode Key(int component);
}

/// <summary>A column of the scan, projected: <c>Select(r =&gt; r.City)</c>.</summary>
/// <typeparam name="T">The column's .NET type.</typeparam>
internal sealed class ProjectionElement<T> : IProjectionElement
{
    internal ProjectionElement(ColumnSym source) => Source = source;

    public ColumnSym Source { get; }

    public ProjectedColumn Column(string name) => new ReadColumn(name, Source.Type, Source.Field);

    public ProjectedColumn Column(VortexField member, int position, Type record)
    {
        VortexType target = member.Type;
        if (!ClrFit.Fits(ClrShape.For<T>.Value, target, Source.Extensions, out string? reason))
        {
            throw new VortexSchemaException(
                $"Element {position + 1} of the projection, '{Source.Field.Path}', is of type {ClrFit.Name(typeof(T))}, which member '{member.Name}' of {record.Name}, a column of {target}, does not take: {reason}");
        }

        if (Source.Type.IsNullable && !target.IsNullable)
        {
            throw new VortexSchemaException(
                $"Element {position + 1} of the projection, '{Source.Field.Path}', may be null, and member '{member.Name}' of {record.Name} is a column of {target}, which holds none: declare the member nullable.");
        }

        // The column itself when the member stores it alike; its values through their .NET type when not.
        return Source.Type.NonNullable.Equals(target.NonNullable)
            ? new ReadColumn(member.Name, target, Source.Field)
            : new ConvertedColumn<T>(member.Name, target, Source.Field, Source.Type);
    }

    public IResultNode Key(int component) => new KeyNode<T>(component, Source.Field.Path);
}

/// <summary>One column of a projection's result, and how a batch of the scan gives it.</summary>
internal abstract class ProjectedColumn
{
    protected ProjectedColumn(string name, VortexType type)
    {
        Name = name;
        Type = type;
    }

    internal string Name { get; }

    internal VortexType Type { get; }

    /// <summary>The column's node in a batch of the scan, in the batch's arena.</summary>
    internal abstract int Node(CanonicalArena arena, int root, int rows, VortexSession session);

    /// <summary>Gives back what the column kept between batches.</summary>
    internal virtual void Release()
    {
    }
}

/// <summary>The scan's column, as it lies in the batch: its encoding kept, nothing copied.</summary>
internal sealed class ReadColumn(string name, VortexType type, FieldExpr field) : ProjectedColumn(name, type)
{
    internal override int Node(CanonicalArena arena, int root, int rows, VortexSession session) => FilterEvaluator.Resolve(arena, root, field, rows);
}

/// <summary>The scan's column read as <typeparamref name="T"/> and written into a column of another storage: a timestamp of another unit.</summary>
internal sealed class ConvertedColumn<T>(string name, VortexType type, FieldExpr field, VortexType source) : ProjectedColumn(name, type)
{
    private T[] _values = [];
    private ColumnStore? _store;

    internal override int Node(CanonicalArena arena, int root, int rows, VortexSession session)
    {
        int node = FilterEvaluator.Resolve(arena, root, field, rows);
        if (_values.Length < rows)
        {
            _values = new T[Math.Max(rows, _values.Length * 2)];
        }

        VortexSessionOptions options = session.Options;
        ResultValues.Copy(null, arena, node, source, options.Extensions, _values, null);
        _store ??= ColumnStores.Create(VortexTypes.ToDType(Type, new DTypeArena()), options.EnginePool, options.Extensions);
        _store.Truncate(0);
        ResultValues.Append(_store, _values, rows, null);
        return _store.Build(arena, rows);
    }

    internal override void Release()
    {
        _store?.Release();
        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
        {
            Array.Clear(_values);
        }
    }
}

/// <summary>
/// The rows of a scan as columns of values, a batch at a time as the scan reads them: a projection.
/// Its <c>Skip</c> and <c>Take</c> are positions among the rows the scan keeps; with no filter and
/// no order they are a range of the file's rows, which the scan reads alone.
/// </summary>
internal sealed class ProjectionQuery : ResultQuery
{
    private VortexSchema? _schema;

    internal ProjectionQuery(AggregationHost host, IProjectionElement[] elements)
        : this(host, elements, Natural(elements), 0, long.MaxValue)
    {
    }

    private ProjectionQuery(AggregationHost host, IProjectionElement[] elements, ProjectedColumn[] columns, long skip, long take)
    {
        Host = host;
        Elements = elements;
        Columns = columns;
        Skip = skip;
        Take = take;
    }

    internal AggregationHost Host { get; }

    internal IProjectionElement[] Elements { get; }

    internal ProjectedColumn[] Columns { get; }

    internal long Skip { get; }

    internal long Take { get; }

    internal override VortexSchema Schema
    {
        get
        {
            if (_schema is null)
            {
                VortexField[] fields = new VortexField[Columns.Length];
                for (int i = 0; i < fields.Length; i++)
                {
                    fields[i] = new VortexField(Columns[i].Name, Columns[i].Type);
                }

                _schema = VortexSchema.Create(fields);
            }

            return _schema;
        }
    }

    internal override VortexSession Session => Host.Source.Session;

    internal override ScanMetrics Metrics => Host.Metrics;

    internal override IAsyncEnumerator<RecordBatch> Batches(CancellationToken cancellationToken) => new ProjectedBatches(this, cancellationToken);

    internal override ValueTask<ScanPlan> ExplainAsync(CancellationToken cancellationToken) =>
        Host.Source.ExplainAsync(SourceSpec(keepEncodings: true, out _), cancellationToken);

    internal override ResultQuery As(VortexSchema record, Type type)
    {
        if (record.Count != Elements.Length)
        {
            throw new VortexSchemaException(
                $"{type.Name} has {record.Count} members and the projection {Elements.Length} elements: a record takes the elements in order, one member each.");
        }

        ProjectedColumn[] columns = new ProjectedColumn[Elements.Length];
        for (int i = 0; i < columns.Length; i++)
        {
            columns[i] = Elements[i].Column(record[i], i, type);
        }

        return new ProjectionQuery(Host, Elements, columns, Skip, Take);
    }

    internal override ResultQuery Limit(long skip, long take)
    {
        (long from, long count) = Within(Skip, Take, skip, take);
        return new ProjectionQuery(Host, Elements, Columns, from, count);
    }

    /// <summary>The distinct values of the projection, each the first time it is met.</summary>
    internal DistinctQuery Distinct() => new DistinctQuery(this);

    /// <summary>
    /// What the projection asks of its scan: the columns its elements read, compacted batches, and
    /// the rows of its window when they are a range of the file's.
    /// </summary>
    /// <param name="keepEncodings">Whether a dictionary or run-end column may stay encoded.</param>
    /// <param name="narrowed">Whether the window became the scan's rows, which the projection then delivers whole.</param>
    internal ScanSpec SourceSpec(bool keepEncodings, out bool narrowed)
    {
        ScanSpec spec = Host.Spec();
        FieldMaskBuilder mask = new FieldMaskBuilder();
        foreach (IProjectionElement element in Elements)
        {
            mask.Include(element.Source.FieldPath);
        }

        spec = spec with { Projection = mask.Build(), KeepEncodings = keepEncodings, Options = spec.Options with { Compact = true } };
        narrowed = false;
        if ((Skip > 0 || Take != long.MaxValue) && Host.Source is FileScanSource file
            && spec.Filter is null && !spec.MatchesNothing && spec.OrderPath is null && spec.Take is null)
        {
            RowRange whole = new RowRange(0, file.File.RowCount);
            RowRange rows = spec.Rows is { } asked ? asked.Intersect(whole) : whole;
            long start = Math.Min(rows.Start + Skip, rows.End);
            long end = Take == long.MaxValue ? rows.End : Math.Min(rows.End, start + Take);
            spec = spec with { Rows = new RowRange(start, end) };
            narrowed = true;
        }

        return spec;
    }

    private static ProjectedColumn[] Natural(IProjectionElement[] elements)
    {
        ProjectedColumn[] columns = new ProjectedColumn[elements.Length];
        for (int i = 0; i < columns.Length; i++)
        {
            columns[i] = elements[i].Column($"Item{i + 1}");
        }

        return columns;
    }
}

/// <summary>
/// The batches of a projection: each batch of the scan, its kept rows, as a struct of the
/// elements' columns over the batch's own nodes. Borrowed, as the scan's are.
/// </summary>
internal sealed class ProjectedBatches : IAsyncEnumerator<RecordBatch>
{
    private readonly ProjectionQuery _query;
    private readonly CancellationToken _cancellationToken;
    private readonly ScanSpec _spec;
    private readonly long _skip;
    private readonly long _end;
    private readonly DType _dtype;
    private IAsyncEnumerator<RecordBatch>? _inner;
    private RecordBatch? _current;
    private int[] _rows = [];
    private int[] _children = [];
    private long _position;
    private bool _begun;
    private bool _ended;

    internal ProjectedBatches(ProjectionQuery query, CancellationToken cancellationToken)
    {
        _query = query;
        _cancellationToken = cancellationToken;
        _spec = query.SourceSpec(keepEncodings: true, out bool narrowed);
        _skip = narrowed ? 0 : query.Skip;
        _end = narrowed || query.Take == long.MaxValue ? long.MaxValue : query.Skip + query.Take;
        _dtype = VortexTypes.ToDType(query.Schema, new DTypeArena());
    }

    public RecordBatch Current => _current ?? throw new InvalidOperationException("The stream has no current batch.");

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    public async ValueTask<bool> MoveNextAsync()
    {
        if (_inner is null)
        {
            _query.Host.Begin();
            _begun = true;
            _inner = _query.Host.Source.BatchesAsync(_spec, _query.Metrics).GetAsyncEnumerator(_cancellationToken);
        }

        // Once the window is served the scan is asked for nothing more: a Take stops the reads.
        while (_position < _end && await _inner.MoveNextAsync().ConfigureAwait(false))
        {
            if (Project(_inner.Current))
            {
                return true;
            }
        }

        _current?.Dispose();
        End();
        return false;
    }

    public async ValueTask DisposeAsync()
    {
        _current?.Dispose();
        if (_inner is not null)
        {
            await _inner.DisposeAsync().ConfigureAwait(false);
        }

        foreach (ProjectedColumn column in _query.Columns)
        {
            column.Release();
        }

        End();
    }

    private void End()
    {
        if (_begun && !_ended)
        {
            _ended = true;
            _query.Host.End();
        }
    }

    /// <summary>The kept rows of <paramref name="batch"/> inside the window, as the current batch; false when there are none.</summary>
    private bool Project(RecordBatch batch)
    {
        CanonicalArena arena = batch.Arena;
        int root = batch.RootIndex;
        int rows = batch.RowCount;
        ReadOnlySpan<ulong> selection = batch.SelectionWords;
        if (!selection.IsEmpty)
        {
            // A batch delivered whole, its kept rows marked: those rows alone.
            rows = Keep(arena, ref root, new Selection(selection, rows, batch.SelectedRows));
        }

        long first = _position;
        _position += rows;
        int low = (int)Math.Clamp(_skip - first, 0, rows);
        int high = (int)Math.Clamp(_end - first, 0, rows);
        if (high <= low)
        {
            return false;
        }

        ProjectedColumn[] columns = _query.Columns;
        Scratch.Grow(ref _children, columns.Length);
        for (int c = 0; c < columns.Length; c++)
        {
            _children[c] = columns[c].Node(arena, root, rows, _query.Session);
        }

        int result = arena.AddStruct(_dtype, rows, Validity.NonNullable, _children.AsSpan(0, columns.Length));
        if (low > 0 || high < rows)
        {
            Scratch.Grow(ref _rows, high - low);
            for (int i = low; i < high; i++)
            {
                _rows[i - low] = i;
            }

            result = CanonicalFilter.Apply(arena, result, _rows.AsSpan(0, high - low));
        }

        _current?.Dispose();
        _current = RecordBatch.Over(arena, result, first + low - _skip, _current);
        return true;
    }

    private int Keep(CanonicalArena arena, ref int root, Selection selection)
    {
        Scratch.Grow(ref _rows, selection.Count);
        int count = 0;
        foreach (int row in selection)
        {
            _rows[count++] = row;
        }

        root = CanonicalFilter.Apply(arena, root, _rows.AsSpan(0, count));
        return count;
    }
}
