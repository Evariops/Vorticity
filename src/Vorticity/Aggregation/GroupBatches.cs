using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.Types;
using Vorticity.Writing;

namespace Vorticity.Aggregating;

/// <summary>
/// A grouped query as the result it delivers: the plan it runs, and the columns of its result, one
/// per element of its <c>select</c>. Every reading of the result, values, records or batches, is a
/// stream of <see cref="GroupBatches"/>.
/// </summary>
internal sealed class AggregationQuery
{
    private VortexSchema? _schema;

    internal AggregationQuery(AggregationHost host, AggregationPlan plan, ResultColumn[] columns)
    {
        Host = host;
        Plan = plan;
        Columns = columns;
    }

    internal AggregationHost Host { get; }

    internal AggregationPlan Plan { get; }

    internal ResultColumn[] Columns { get; }

    /// <summary>The result's columns, by name and type.</summary>
    internal VortexSchema Schema
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

    /// <summary>The result's batches: the query runs when the first is asked for.</summary>
    internal GroupBatches Batches(CancellationToken cancellationToken) => new GroupBatches(this, cancellationToken);
}

/// <summary>
/// The batches of a grouped query's result: the groups in the order they are delivered, a batch of
/// them at a time, each column built in the stores a writer fills and viewed by the batch without a
/// copy. A batch is borrowed, valid until the next is asked for; the stores and the arena are
/// reused from one to the next, so that a batch allocates nothing once the first has sized them.
/// </summary>
internal sealed class GroupBatches : IAsyncEnumerator<RecordBatch>
{
    /// <summary>The groups of a batch, as many as the rows of a scan's batch.</summary>
    internal const int BatchRows = 65_536;

    private readonly AggregationQuery _query;
    private readonly CancellationToken _cancellationToken;
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
        if (_next >= order.Length)
        {
            Release();
            return false;
        }

        _cancellationToken.ThrowIfCancellationRequested();
        int count = Math.Min(BatchRows, order.Length - _next);
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
        _current = RecordBatch.Over(arena, root, _next, _current);
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
    private readonly GroupBatches _batches;
    private readonly IVortexRecord? _record;
    private readonly VortexExtensionRegistry? _extensions;
    private readonly CancellationTokenSource? _linked;
    private T[] _values = [];
    private int _count;
    private int _index;

    internal ValueEnumerator(GroupBatches batches, IVortexRecord? record, VortexExtensionRegistry? extensions, CancellationTokenSource? linked)
    {
        _batches = batches;
        _record = record;
        _extensions = extensions;
        _linked = linked;
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
        ResultValues.Copy(batch, arena, column, _batches.Schema[0].Type, _extensions, _values, _record);
        _count = rows;
        return rows > 0;
    }
}
