using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.Compute;
using Vorticity.Scanning;
using Vorticity.Types;
using Vorticity.Writing;

namespace Vorticity.Aggregating;

/// <summary>
/// The distinct values of a projection: a group by its elements with no aggregate, each value
/// delivered the first time it is met. A value with no aggregate is final as soon as it is seen,
/// so the result streams whatever the key, and holds the values met, not the rows read.
/// </summary>
internal sealed class DistinctQuery : ResultQuery
{
    private VortexSchema? _schema;

    internal DistinctQuery(ProjectionQuery projection)
        : this(projection, Shapes(projection), KeysOf(projection), null, 0, long.MaxValue)
    {
    }

    private DistinctQuery(ProjectionQuery projection, ColumnShape[] keys, IResultNode[] nodes, ResultColumn[]? columns, long skip, long take)
    {
        Projection = projection;
        Keys = keys;
        Nodes = nodes;
        Plan = new AggregationPlan([], keys);
        Columns = columns ?? Natural(nodes, keys);
        Skip = skip;
        Take = take;
    }

    internal ProjectionQuery Projection { get; }

    internal ColumnShape[] Keys { get; }

    internal IResultNode[] Nodes { get; }

    internal AggregationPlan Plan { get; }

    internal ResultColumn[] Columns { get; }

    internal long Skip { get; }

    internal long Take { get; }

    /// <summary>The most values the last run held at once: every one met, or a batch's on a column that streams.</summary>
    internal long PeakValues { get; set; }

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

    internal override VortexSession Session => Projection.Session;

    internal override ScanMetrics Metrics => Projection.Metrics;

    internal override IAsyncEnumerator<RecordBatch> Batches(CancellationToken cancellationToken) => new DistinctBatches(this, cancellationToken);

    internal override ValueTask<ScanPlan> ExplainAsync(CancellationToken cancellationToken) => Projection.ExplainAsync(cancellationToken);

    internal override ResultQuery As(VortexSchema record, Type type)
    {
        if (record.Count != Nodes.Length)
        {
            throw new VortexSchemaException(
                $"{type.Name} has {record.Count} members and the projection {Nodes.Length} elements: a record takes the elements in order, one member each.");
        }

        ResultColumn[] columns = new ResultColumn[Nodes.Length];
        for (int i = 0; i < columns.Length; i++)
        {
            columns[i] = Nodes[i].Column(record[i], Keys, i, type);
        }

        return new DistinctQuery(Projection, Keys, Nodes, columns, Skip, Take);
    }

    internal override ResultQuery Limit(long skip, long take)
    {
        (long from, long count) = Within(Skip, Take, skip, take);
        return new DistinctQuery(Projection, Keys, Nodes, Columns, from, count);
    }

    private static ColumnShape[] Shapes(ProjectionQuery projection)
    {
        ColumnShape[] keys = new ColumnShape[projection.Elements.Length];
        for (int i = 0; i < keys.Length; i++)
        {
            keys[i] = new ColumnShape(projection.Elements[i].Source);
        }

        return keys;
    }

    private static IResultNode[] KeysOf(ProjectionQuery projection)
    {
        IResultNode[] nodes = new IResultNode[projection.Elements.Length];
        for (int i = 0; i < nodes.Length; i++)
        {
            nodes[i] = projection.Elements[i].Key(i);
        }

        return nodes;
    }

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
/// The batches of a <c>Distinct</c>: for each batch of the scan, the values it is the first to
/// hold, numbered by the key index and written as the index holds them.
/// </summary>
internal sealed class DistinctBatches : IAsyncEnumerator<RecordBatch>
{
    private readonly DistinctQuery _query;
    private readonly CancellationToken _cancellationToken;
    private IAsyncEnumerator<RecordBatch>? _inner;
    private AggregationPartition? _partition;
    private AggregationOutcome? _outcome;
    private StructStore? _store;
    private CanonicalArena? _arena;
    private RecordBatch? _current;
    private int[] _groups = [];
    private int[] _open = [];
    private int _streaming = -1;
    private long _met;
    private bool _begun;
    private bool _ended;

    // What the index of the values met holds, reserved in the session's budget as it grows; given
    // back when the stream ends (PLAN-HIGH-CARDINALITY, H2).
    private QueryMemory? _memory;

    internal DistinctBatches(DistinctQuery query, CancellationToken cancellationToken)
    {
        _query = query;
        _cancellationToken = cancellationToken;
    }

    public RecordBatch Current => _current ?? throw new InvalidOperationException("The stream has no current batch.");

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    public async ValueTask<bool> MoveNextAsync()
    {
        long end = _query.Take == long.MaxValue ? long.MaxValue : _query.Skip + _query.Take;
        if (_inner is null)
        {
            ProjectionQuery projection = _query.Projection;
            projection.Host.Begin();
            _begun = true;

            // The key's columns in the form they arrive in: a dictionary groups by code.
            ScanSpec spec = projection.SourceSpec(keepEncodings: true, out _) with
            {
                SinkDecodes = true,
                PositionsUnread = true,
                Options = projection.Host.Spec().Options with { Compact = false },
            };

            // A group by with no aggregate; on a column the statistics say is sorted, a value
            // below the last one met never comes back, so the index forgets it.
            AggregationPlan plan = _query.Plan;
            for (int c = 0; c < plan.Keys.Length && _streaming < 0; c++)
            {
                _streaming = AggregationEngine.IsSorted(projection.Host.Source, plan.Keys[c]) ? c : -1;
            }

            (ColumnShape[] columns, int[] inputs) = AggregationEngine.Columns(plan, []);
            _memory = new QueryMemory(projection.Host.Source.Session.Options.MemoryBudget ?? QueryMemoryBudget.Process);
            AggregationEngine.Admit(_memory, 1);
            _partition = new AggregationPartition(plan, [], columns, inputs, sorted: _streaming == 0 && plan.Keys.Length == 1, _streaming, memory: _memory);
            _outcome = new AggregationOutcome(plan, _partition.Slots, _partition.Keys, []);
            _inner = projection.Host.Source.BatchesAsync(spec, projection.Metrics).GetAsyncEnumerator(_cancellationToken);
        }

        // Once the values asked for are met the scan is asked for nothing more.
        while (_met < end && await _inner.MoveNextAsync().ConfigureAwait(false))
        {
            if (Meet(_inner.Current, end))
            {
                return true;
            }
        }

        Release();
        return false;
    }

    public async ValueTask DisposeAsync()
    {
        Release();
        if (_inner is not null)
        {
            await _inner.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>The values <paramref name="batch"/> is the first to hold, inside the window, as the current batch; false when there are none.</summary>
    private bool Meet(RecordBatch batch, long end)
    {
        AggregationPartition partition = _partition!;
        GroupKeys index = partition.Keys!;
        int before = index.Count;
        partition.Process(batch);
        int after = index.Count;
        _query.PeakValues = Math.Max(_query.PeakValues, after);
        long first = _met;
        _met += after - before;
        long low = Math.Max(_query.Skip, first);
        long high = Math.Min(end, _met);
        if (high <= low)
        {
            Forget(after > before);
            return false;
        }

        // The groups are numbered as they are met: the new ones are a run of numbers.
        int count = (int)(high - low);
        Scratch.Grow(ref _groups, count);
        int start = before + (int)(low - first);
        for (int i = 0; i < count; i++)
        {
            _groups[i] = start + i;
        }

        StructStore store = Store();
        store.Truncate(0);
        ResultColumn[] columns = _query.Columns;
        for (int c = 0; c < columns.Length; c++)
        {
            columns[c].Append(_outcome!, store.Children[c], _groups.AsSpan(0, count));
        }

        _current?.Dispose();
        CanonicalArena own = _arena!;
        own.ResetKeepingBlocks();
        int result = store.Build(own, count);
        _current = RecordBatch.Over(own, result, low - _query.Skip, _current);
        Forget(after > before);
        return true;
    }

    /// <summary>
    /// On a key that streams, the values no row to come can hold again, forgotten once delivered:
    /// every one but the last met of the sorted component, and its null. The memory is then the
    /// values of one value of it, not every value met.
    /// </summary>
    private void Forget(bool met)
    {
        if (_streaming < 0 || !met)
        {
            return;
        }

        AggregationPartition partition = _partition!;
        int count = partition.Keys!.Count;
        int last = partition.LastValueGroup;
        int nullComponent = partition.ComponentNull;
        Scratch.Grow(ref _open, count);
        int open = 0;
        for (int g = 0; g < count; g++)
        {
            int component = partition.ComponentOf(g);
            if (component == last || component == nullComponent)
            {
                _open[open++] = g;
            }
        }

        if (open < count)
        {
            partition.Keep(_open.AsSpan(0, open));
        }
    }

    private StructStore Store()
    {
        if (_store is not null)
        {
            return _store;
        }

        VortexSessionOptions options = _query.Session.Options;
        _store = (StructStore)ColumnStores.Create(VortexTypes.ToDType(_query.Schema, new DTypeArena()), options.EnginePool, options.Extensions);
        _arena = new CanonicalArena(64, options.EnginePool);
        return _store;
    }

    private void Release()
    {
        _current?.Dispose();
        _store?.Release();
        _arena?.Reset();
        _memory?.Dispose();
        if (_begun && !_ended)
        {
            _ended = true;
            _query.Projection.Host.End();
        }
    }
}
