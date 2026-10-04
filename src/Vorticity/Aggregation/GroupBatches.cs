using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.Scanning;
using Vorticity.Types;
using Vorticity.Writing;

namespace Vorticity.Aggregating;

/// <summary>
/// A grouped query as the result it delivers: the plan it runs, and the columns of its result, one
/// per element of its <c>select</c>. Every reading of the result, values, records or batches, is a
/// stream of <see cref="GroupBatches"/>.
/// </summary>
internal sealed class AggregationQuery : ResultQuery
{
    private VortexSchema? _schema;

    internal AggregationQuery(AggregationHost host, AggregationPlan plan, IResultNode[] nodes, ColumnShape[] keys)
        : this(host, plan, nodes, keys, Natural(nodes, keys), 0, long.MaxValue)
    {
    }

    private AggregationQuery(AggregationHost host, AggregationPlan plan, IResultNode[] nodes, ColumnShape[] keys, ResultColumn[] columns, long skip, long take)
    {
        Host = host;
        Plan = plan;
        Nodes = nodes;
        Keys = keys;
        Columns = columns;
        Skip = skip;
        Take = take;
    }

    internal AggregationHost Host { get; }

    internal AggregationPlan Plan { get; }

    /// <summary>The elements of the selection, in order.</summary>
    internal IResultNode[] Nodes { get; }

    /// <summary>The columns of the group key; none for the aggregates of a whole scan.</summary>
    internal ColumnShape[] Keys { get; }

    internal ResultColumn[] Columns { get; }

    /// <summary>The groups the result passes over before its first, in the order they are delivered.</summary>
    internal long Skip { get; }

    /// <summary>The groups the result delivers at most; <see cref="long.MaxValue"/> for every one.</summary>
    internal long Take { get; }

    /// <summary>The result's columns, by name and type.</summary>
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

    internal override ResultQuery As(VortexSchema record, Type type) => Typed(record, type);

    internal override ResultQuery Limit(long skip, long take)
    {
        (long from, long count) = Within(Skip, Take, skip, take);
        return new AggregationQuery(Host, Plan, Nodes, Keys, Columns, from, count);
    }

    internal override IVortexRecord? RecordOf(int column) => Columns[column].Record;

    /// <summary>The same query, its result's columns named and typed by the members of a record, in order.</summary>
    internal AggregationQuery Typed(VortexSchema record, Type type)
    {
        if (record.Count != Nodes.Length)
        {
            throw new VortexSchemaException(
                $"{type.Name} has {record.Count} members and the selection {Nodes.Length} elements: a record takes the elements in order, one member each.");
        }

        ResultColumn[] columns = new ResultColumn[Nodes.Length];
        for (int i = 0; i < columns.Length; i++)
        {
            columns[i] = Nodes[i].Column(record[i], Keys, i, type);
        }

        return new AggregationQuery(Host, Plan, Nodes, Keys, columns, Skip, Take);
    }

    /// <summary>The result's batches: the query runs when the first is asked for.</summary>
    internal override IAsyncEnumerator<RecordBatch> Batches(CancellationToken cancellationToken) => Groups(cancellationToken);

    /// <summary>The result's batches, as the stream that knows the run they come from.</summary>
    internal GroupBatches Groups(CancellationToken cancellationToken) => new GroupBatches(this, cancellationToken);

    internal override ValueTask<ScanPlan> ExplainAsync(CancellationToken cancellationToken) => Host.ExplainAsync(Plan, cancellationToken);

    /// <summary>Each element's column of its own type, named <c>Item1</c> and on as a tuple's elements are.</summary>
    private static ResultColumn[] Natural(IResultNode[] nodes, ColumnShape[] keys)
    {
        ResultColumn[] columns = new ResultColumn[nodes.Length];
        for (int i = 0; i < columns.Length; i++)
        {
            columns[i] = nodes[i].Column($"Item{i + 1}", keys);
        }

        return columns;
    }
}

/// <summary>
/// The batches of a grouped query's result: the groups in the order they are delivered, a batch of
/// them at a time, each column built in the stores a writer fills and viewed by the batch without a
/// copy. A batch is borrowed, valid until the next is asked for; the stores and the arena are
/// reused from one to the next, so that a batch allocates nothing once the first has sized them.
/// </summary>
internal sealed class GroupBatches : IAsyncEnumerator<RecordBatch>
{
    /// <summary>The groups of a batch, as many as the rows of a scan's batch, unless the scan's options say otherwise.</summary>
    internal const int BatchRows = 65_536;

    private readonly AggregationQuery _query;
    private readonly CancellationToken _cancellationToken;
    private readonly int _batchRows;
    private AggregationOutcome? _outcome;
    private int _next;
    private StructStore? _store;
    private CanonicalArena? _arena;
    private RecordBatch? _current;
    private bool _done;

    internal GroupBatches(AggregationQuery query, CancellationToken cancellationToken)
    {
        _query = query;
        _cancellationToken = cancellationToken;
        int asked = query.Host.Spec().Options.BatchRows;
        _batchRows = asked > 0 ? asked : BatchRows;
    }

    /// <summary>The current batch, valid until the next <see cref="MoveNextAsync"/>.</summary>
    public RecordBatch Current => _current ?? throw new InvalidOperationException("The stream has no current batch.");

    /// <summary>The result's columns.</summary>
    internal VortexSchema Schema => _query.Schema;

    /// <summary>The query's run: the merged states and keys, once the first batch has been asked for.</summary>
    internal AggregationOutcome? Outcome => _outcome;

    /// <summary>Moves to the next batch: the first runs the query, the others are built from its groups without an await.</summary>
    /// <returns>Whether there is one.</returns>
    public ValueTask<bool> MoveNextAsync()
    {
        if (_done)
        {
            return new ValueTask<bool>(false);
        }

        return _outcome is null ? RunAsync() : new ValueTask<bool>(Next());
    }

    public ValueTask DisposeAsync()
    {
        Release();
        return ValueTask.CompletedTask;
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<bool> RunAsync()
    {
        _outcome = await _query.Host.RunAsync(_query.Plan, _cancellationToken).ConfigureAwait(false);
        return Next();
    }

    private bool Next()
    {
        AggregationOutcome outcome = _outcome!;
        int[] order = outcome.Order;
        if (_next == 0 && _query.Skip > 0)
        {
            _next = (int)Math.Min(_query.Skip, order.Length);
        }

        long end = _query.Take == long.MaxValue ? order.Length : Math.Min(order.Length, _query.Skip + _query.Take);
        if (_next >= end)
        {
            Release();
            return false;
        }

        _cancellationToken.ThrowIfCancellationRequested();
        int count = (int)Math.Min(_batchRows, end - _next);
        ReadOnlySpan<int> groups = order.AsSpan(_next, count);
        StructStore store = Store();
        store.Truncate(0);
        ResultColumn[] columns = _query.Columns;
        for (int c = 0; c < columns.Length; c++)
        {
            columns[c].Append(outcome, store.Children[c], groups);
        }

        _current?.Dispose();
        CanonicalArena arena = _arena!;
        arena.ResetKeepingBlocks();
        int root = store.Build(arena, count);
        _current = RecordBatch.Over(arena, root, _next - _query.Skip, _current);
        _next += count;
        return true;
    }

    /// <summary>The stores of the result's columns, made once per stream from the session's pool.</summary>
    private StructStore Store()
    {
        if (_store is not null)
        {
            return _store;
        }

        VortexSessionOptions options = _query.Host.Source.Session.Options;
        DType dtype = VortexTypes.ToDType(_query.Schema, new DTypeArena());
        _store = (StructStore)ColumnStores.Create(dtype, options.EnginePool, options.Extensions);
        _arena = new CanonicalArena(64, options.EnginePool);
        return _store;
    }

    private void Release()
    {
        _done = true;
        _current?.Dispose();
        _store?.Release();
        _arena?.Reset();
    }
}

/// <summary>
/// The values of a result of one value, read from its batches: a batch is read into values once,
/// with an <c>await</c>, and each value is then a step of an index.
/// </summary>
/// <typeparam name="T">The value's type.</typeparam>
internal sealed class ValueEnumerator<T> : IAsyncEnumerator<T>
{
    private readonly IAsyncEnumerator<RecordBatch> _batches;
    private readonly VortexType _type;
    private readonly IVortexRecord? _record;
    private readonly VortexExtensionRegistry? _extensions;
    private readonly CancellationTokenSource? _linked;
    private T[] _values = [];
    private int _count;
    private int _index;

    internal ValueEnumerator(ResultQuery query, CancellationTokenSource? linked, CancellationToken cancellationToken)
    {
        _batches = query.Batches(cancellationToken);
        _type = query.Schema[0].Type;
        _record = query.RecordOf(0);
        _extensions = query.Session.Options.Extensions;
        _linked = linked;
    }

    /// <summary>The values of a result of one value, read by <paramref name="query"/>'s owner with the token its caller gave.</summary>
    internal static ValueEnumerator<T> Of(ResultQuery query, CancellationToken own, CancellationToken given)
    {
        CancellationTokenSource? linked = own.CanBeCanceled && given.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(own, given)
            : null;
        CancellationToken token = linked?.Token ?? (given.CanBeCanceled ? given : own);
        return new ValueEnumerator<T>(query, linked, token);
    }

    public T Current => _values[_index];

    /// <summary>The values of the current batch.</summary>
    internal ReadOnlySpan<T> Batch => _values.AsSpan(0, _count);

    public ValueTask<bool> MoveNextAsync()
    {
        if (++_index < _count)
        {
            return new ValueTask<bool>(true);
        }

        return NextBatchAsync();
    }

    /// <summary>Reads the next batch into values; <see cref="Current"/> is then its first.</summary>
    /// <returns>Whether there is one.</returns>
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    internal async ValueTask<bool> NextBatchAsync() => Load(await _batches.MoveNextAsync().ConfigureAwait(false));

    public async ValueTask DisposeAsync()
    {
        await _batches.DisposeAsync().ConfigureAwait(false);
        _linked?.Dispose();
        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
        {
            Array.Clear(_values);
        }
    }

    private bool Load(bool more)
    {
        _index = 0;
        if (!more)
        {
            _count = 0;
            return false;
        }

        RecordBatch batch = _batches.Current;
        CanonicalArena arena = batch.Arena;
        int rows = batch.RowCount;
        if (_values.Length < rows)
        {
            _values = new T[Math.Max(rows, Math.Min(_values.Length * 2, GroupBatches.BatchRows))];
        }

        int column = arena.GetNode(batch.RootIndex).GetFieldIndex(0);
        ResultValues.Copy(batch, arena, column, _type, _extensions, _values, _record);
        _count = rows;
        return rows > 0;
    }
}
