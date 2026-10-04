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
    private ScanSpec? _pass;
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

    // When the pass began, for the plan's last run.
    private long _started;

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

    /// <summary>
    /// The component of a query's key its groups stream on, or -1: the first the statistics say is
    /// sorted, and no order but one that starts with it, ascending.
    /// </summary>
    internal static int Streaming(AggregationQuery query)
    {
        ColumnShape[] keys = query.Plan.Keys;
        int streaming = -1;
        for (int c = 0; c < keys.Length && streaming < 0; c++)
        {
            streaming = AggregationEngine.IsSorted(query.Host.Source, keys[c]) ? c : -1;
        }

        if (streaming < 0)
        {
            return -1;
        }

        foreach (GroupOperator op in query.Operators)
        {
            if (op is GroupOrder order && (order.Keys[0].Field.Component != streaming || order.Keys[0].Descending))
            {
                return -1;
            }
        }

        return streaming;
    }

    public RecordBatch Current => _current ?? throw new InvalidOperationException("The stream has no current batch.");

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    public async ValueTask<bool> MoveNextAsync()
    {
        if (_inner is null)
        {
            await StartAsync().ConfigureAwait(false);
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
                // The end of the rows closes every group, the null group last, once the blocks
                // the zone maps settled after the last batch are in.
                _drained = true;
                _partition!.Finish();
                _query.Plan.LastRun = new AggregationRun(
                    [new AggregationRun.Lane(System.Diagnostics.Stopwatch.GetTimestamp() - _started, 1, _partition.Keys!.Count)], 0, 0);
                await CloseAsync(all: true).ConfigureAwait(false);
                continue;
            }

            _partition!.Process(_inner.Current);
            _query.PeakGroups = Math.Max(_query.PeakGroups, _partition.Keys!.Count);
            await CloseAsync(all: false).ConfigureAwait(false);
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

    private async ValueTask StartAsync()
    {
        AggregationHost host = _query.Host;
        host.Begin();
        _begun = true;
        _started = System.Diagnostics.Stopwatch.GetTimestamp();
        AggregationPlan plan = _query.Plan;
        (ColumnShape[] columns, int[] inputs) = AggregationEngine.Columns(plan, new AggregateSlot?[plan.Aggregates.Length]);
        ScanSpec pass = AggregationEngine.PassSpec(host.Spec(_query.RowFilter), columns, plan, host.Source.Schema);
        _partition = new AggregationPartition(
            plan, new AggregateSlot?[plan.Aggregates.Length], columns, inputs, sorted: plan.Keys.Length == 1, Streaming(_query), host.Source);

        // The blocks the zone maps settle are folded in the order of the rows, which the groups
        // close in.
        if (await ZoneSettling.PlanAsync(host.Source, pass, plan, host.Metrics, _cancellationToken).ConfigureAwait(false) is { } settling)
        {
            pass = settling.Pass(pass);
            _partition.Settle(settling, pass.Rows ?? new RowRange(0, long.MaxValue));
        }

        _pass = pass;
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
        int count = partition.Keys!.Count;
        int nullComponent = partition.ComponentNull;
        int last = all ? -1 : partition.LastValueGroup;
        Scratch.Grow(ref _closed, count);
        Scratch.Grow(ref _open, count);
        _closedCount = 0;
        _openCount = 0;
        for (int g = 0; g < count; g++)
        {
            int component = partition.ComponentOf(g);
            if (component == nullComponent || component == last)
            {
                // Open, and at the end the groups of the null component, which go out last.
                _open[_openCount++] = g;
                continue;
            }

            _closed[_closedCount++] = g;
        }

        if (all)
        {
            _open.AsSpan(0, _openCount).CopyTo(_closed.AsSpan(_closedCount));
            _closedCount += _openCount;
            _openCount = 0;
        }

        _closedNext = 0;

        // The groups closed leave the partition, whether the operators deliver them or not.
        _keepPending = !all && _closedCount > 0;
    }

    /// <summary>Closes the groups now final, reads their chosen rows, and passes them through the operators, which may read those.</summary>
    private async ValueTask CloseAsync(bool all)
    {
        Close(all);
        if (_closedCount == 0)
        {
            return;
        }

        if (_query.Plan.Chosen.Length > 0)
        {
            await ChosenFetch.FetchAsync(_outcome!, _query.Host.Source, _pass!, _query.Host.Metrics, _closed.AsMemory(0, _closedCount), _cancellationToken)
                .ConfigureAwait(false);
        }

        Through();
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
                case GroupOrder order when _query.Plan.Keys.Length > 1 || order.Keys.Length > 1:
                {
                    // An order that starts with the streaming component: the groups closed together
                    // hold every group of their values of it, so sorting them is sorting the result.
                    (int[] sorted, int sortedCount) = GroupSelection.Order(_query, _outcome!, order, _closed, count, long.MaxValue, _cancellationToken);
                    sorted.AsSpan(0, sortedCount).CopyTo(_closed);
                    count = sortedCount;
                    break;
                }

                default:
                    // An order on the one key, ascending: the stream is in it already.
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
