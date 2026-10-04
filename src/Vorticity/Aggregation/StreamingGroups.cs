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
/// The batches of a group by whose key the statistics say is sorted: a group is final as soon as a
/// row of a greater key is read, so after each batch every group but the last one met, and the
/// null group, goes out, in key order, and the partition keeps the open ones alone. The first
/// batch waits for the first group closed, not for the file; the memory is the groups still open.
/// </summary>
/// <remarks>
/// The operators on groups run on each closed batch of groups in the order written: a filter on
/// its results, a window by counting, and an order on the key ascending, which the stream already
/// is. Once a window has delivered its last group the scan is asked for nothing more.
/// </remarks>
internal sealed class StreamingGroupBatches : IAsyncEnumerator<RecordBatch>
{
    private readonly AggregationQuery _query;
    private readonly CancellationToken _cancellationToken;
    private readonly int _batchRows;
    private readonly long[] _skips;
    private readonly long[] _takes;
    private long _resultSkip;
    private long _resultTake;
    private IAsyncEnumerator<RecordBatch>? _inner;
    private AggregationPartition? _partition;
    private AggregationOutcome? _outcome;
    private int[] _closed = [];
    private int _closedCount;
    private int _closedNext;
    private int[] _open = [];
    private int _openCount;
    private bool _keepPending;
    private bool _drained;
    private bool _done;
    private bool _begun;
    private bool _ended;
    private StructStore? _store;
    private CanonicalArena? _arena;
    private RecordBatch? _current;
    private long _position;

    internal StreamingGroupBatches(AggregationQuery query, CancellationToken cancellationToken)
    {
        _query = query;
        _cancellationToken = cancellationToken;
        int asked = query.Host.Spec().Options.BatchRows;
        _batchRows = asked > 0 ? asked : GroupBatches.BatchRows;
        _skips = new long[query.Operators.Length];
        _takes = new long[query.Operators.Length];
        for (int o = 0; o < query.Operators.Length; o++)
        {
            if (query.Operators[o] is GroupWindow window)
            {
                _skips[o] = window.Skip;
                _takes[o] = window.Take;
            }
        }

        _resultSkip = query.Skip;
        _resultTake = query.Take;
    }

    /// <summary>Whether a query's groups stream: one key the statistics say is sorted, and no order but by it, ascending.</summary>
    internal static bool Streams(AggregationQuery query)
    {
        if (query.Plan.Keys.Length != 1 || !AggregationEngine.IsSorted(query.Host.Source, query.Plan.Keys[0]))
        {
            return false;
        }

        foreach (GroupOperator op in query.Operators)
        {
            if (op is GroupOrder order && (order.Keys[0].Field.Component != 0 || order.Keys[0].Descending))
            {
                return false;
            }
        }

        return true;
    }

    public RecordBatch Current => _current ?? throw new InvalidOperationException("The stream has no current batch.");

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    public async ValueTask<bool> MoveNextAsync()
    {
        if (_inner is null)
        {
            Start();
        }

        while (true)
        {
            if (_closedNext < _closedCount)
            {
                Emit();
                return true;
            }

            if (_keepPending)
            {
                // Every closed group is out: the partition keeps the open ones, numbered again.
                _partition!.Keep(_open.AsSpan(0, _openCount));
                _keepPending = false;
            }

            if (_done || _drained || Served())
            {
                Release();
                return false;
            }

            _cancellationToken.ThrowIfCancellationRequested();
            if (!await _inner!.MoveNextAsync().ConfigureAwait(false))
            {
                // The end of the rows closes every group, the null group last.
                _drained = true;
                Close(all: true);
                continue;
            }

            _partition!.Process(_inner.Current);
            _query.PeakGroups = Math.Max(_query.PeakGroups, _partition.Keys!.Count);
            Close(all: false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        Release();
        if (_inner is not null)
        {
            await _inner.DisposeAsync().ConfigureAwait(false);
        }
    }

    private void Start()
    {
        AggregationHost host = _query.Host;
        host.Begin();
        _begun = true;
        AggregationPlan plan = _query.Plan;
        (ColumnShape[] columns, int[] inputs) = AggregationEngine.Columns(plan, new AggregateSlot?[plan.Aggregates.Length]);
        ScanSpec pass = AggregationEngine.PassSpec(host.Spec(_query.RowFilter), columns);
        _partition = new AggregationPartition(plan, new AggregateSlot?[plan.Aggregates.Length], columns, inputs, sorted: true);
        _outcome = new AggregationOutcome(plan, _partition.Slots, _partition.Keys, []);
        _inner = host.Source.BatchesAsync(pass, host.Metrics).GetAsyncEnumerator(_cancellationToken);
    }

    /// <summary>
    /// The groups now final, in key order, through the operators: every group but the open ones,
    /// the group of the last key met and the null group, or every group at the end.
    /// </summary>
    private void Close(bool all)
    {
        AggregationPartition partition = _partition!;
        GroupKeys keys = partition.Keys!;
        int count = keys.Count;
        int nullGroup = keys.NullNumber;
        int last = all ? -1 : partition.LastValueGroup;
        Scratch.Grow(ref _closed, count);
        Scratch.Grow(ref _open, 2);
        _closedCount = 0;
        _openCount = 0;
        for (int g = 0; g < count; g++)
        {
            if (g == nullGroup || g == last)
            {
                if (!all)
                {
                    _open[_openCount++] = g;
                }

                continue;
            }

            _closed[_closedCount++] = g;
        }

        if (all && nullGroup >= 0)
        {
            _closed[_closedCount++] = nullGroup;
        }

        _closedNext = 0;

        // The groups closed leave the partition, whether the operators deliver them or not.
        _keepPending = !all && _closedCount > 0;
        if (_closedCount > 0)
        {
            Through();
        }
    }

    /// <summary>The closed groups through the operators on groups and the result's own window, in the order written.</summary>
    private void Through()
    {
        GroupOperator[] operators = _query.Operators;
        int count = _closedCount;
        for (int o = 0; o < operators.Length && count > 0; o++)
        {
            switch (operators[o])
            {
                case GroupFilter filter:
                {
                    (int[] kept, int keptCount) = GroupSelection.Filter(_query, _outcome!, filter, _closed, count, _cancellationToken);
                    kept.AsSpan(0, keptCount).CopyTo(_closed);
                    count = keptCount;
                    break;
                }

                case GroupWindow:
                    count = Window(ref _skips[o], ref _takes[o], count);
                    break;
                default:
                    // An order on the key, ascending: the stream is in it already.
                    break;
            }
        }

        _closedCount = Window(ref _resultSkip, ref _resultTake, count);
    }

    /// <summary>Passes over the groups a window skips and keeps those it takes, its counters carried from one closed batch to the next.</summary>
    private int Window(ref long skip, ref long take, int count)
    {
        int skipped = (int)Math.Min(skip, count);
        skip -= skipped;
        int kept = (int)Math.Min(count - skipped, take);
        if (take != long.MaxValue)
        {
            take -= kept;
        }

        if (skipped > 0)
        {
            _closed.AsSpan(skipped, kept).CopyTo(_closed);
        }

        return kept;
    }

    /// <summary>Whether a window has delivered its last group, so that no other can go out and nothing more is read.</summary>
    private bool Served()
    {
        if (_resultTake == 0)
        {
            return true;
        }

        for (int o = 0; o < _takes.Length; o++)
        {
            if (_query.Operators[o] is GroupWindow && _takes[o] == 0)
            {
                return true;
            }
        }

        return false;
    }

    private void Emit()
    {
        int count = Math.Min(_batchRows, _closedCount - _closedNext);
        ReadOnlySpan<int> groups = _closed.AsSpan(_closedNext, count);
        StructStore store = Store();
        store.Truncate(0);
        ResultColumn[] columns = _query.Columns;
        for (int c = 0; c < columns.Length; c++)
        {
            columns[c].Append(_outcome!, store.Children[c], groups);
        }

        _current?.Dispose();
        CanonicalArena arena = _arena!;
        arena.ResetKeepingBlocks();
        int root = store.Build(arena, count);
        _current = RecordBatch.Over(arena, root, _position, _current);
        _position += count;
        _closedNext += count;
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
        _done = true;
        _current?.Dispose();
        _store?.Release();
        _arena?.Reset();
        if (_begun && !_ended)
        {
            _ended = true;
            _query.Host.End();
        }
    }
}
