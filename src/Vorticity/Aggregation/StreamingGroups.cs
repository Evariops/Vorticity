using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.Compute;
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

    // Each filter on groups and what it reads their results into, by the operator's place.
    private GroupSelection.GroupFilterRun?[]? _filters;

    // On several lanes: the ranges grouped side by side, what each did, and the time spent
    // following them.
    private StreamingRanges? _ranges;
    private List<AggregationRun.Lane>? _lanes;
    private long _mergeTicks;

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
        if (_partition is null)
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
            if (_ranges is not null)
            {
                // A range grouped on its lane follows the rows before it, as its batches would have.
                if (await _ranges.NextAsync().ConfigureAwait(false) is not { } range)
                {
                    await DrainAsync().ConfigureAwait(false);
                    continue;
                }

                long merging = Stopwatch.GetTimestamp();
                _partition!.Follow(range);
                _mergeTicks += Stopwatch.GetTimestamp() - merging;
                (_lanes ??= []).Add(new AggregationRun.Lane(range.ActiveTicks, range.Ranges, range.GroupsAtEnd));
                _query.PeakGroups = Math.Max(_query.PeakGroups, _partition.Keys!.Count);
                await CloseAsync(all: false).ConfigureAwait(false);
                continue;
            }

            if (!await _inner!.MoveNextAsync().ConfigureAwait(false))
            {
                // The blocks the zone maps settled after the last batch are folded before the end.
                _partition!.Finish();
                await DrainAsync().ConfigureAwait(false);
                continue;
            }

            _partition!.Process(_inner.Current);
            _query.PeakGroups = Math.Max(_query.PeakGroups, _partition.Keys!.Count);
            await CloseAsync(all: false).ConfigureAwait(false);
        }
    }

    /// <summary>The end of the rows: every group closes, the null group last, and the plan keeps what the run did.</summary>
    private async ValueTask DrainAsync()
    {
        _drained = true;
        _query.Plan.LastRun = _lanes is null
            ? new AggregationRun([new AggregationRun.Lane(Stopwatch.GetTimestamp() - _started, 1, _partition!.Keys!.Count)], 0, 0)
            : new AggregationRun([.. _lanes], _mergeTicks, _lanes.Count);
        await CloseAsync(all: true).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        Release();
        if (_ranges is not null)
        {
            await _ranges.DisposeAsync().ConfigureAwait(false);
        }

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
        _started = Stopwatch.GetTimestamp();
        AggregationPlan plan = _query.Plan;
        (ColumnShape[] columns, int[] inputs) = AggregationEngine.Columns(plan, new AggregateSlot?[plan.Aggregates.Length]);
        ScanSpec pass = AggregationEngine.PassSpec(host.Spec(_query.RowFilter), columns, plan, host.Source.Schema);
        int streaming = Streaming(_query);
        _partition = new AggregationPartition(
            plan, new AggregateSlot?[plan.Aggregates.Length], columns, inputs, sorted: plan.Keys.Length == 1, streaming, host.Source);

        // The blocks the zone maps settle are folded in the order of the rows, which the groups
        // close in.
        ZoneSettling? settling = await ZoneSettling.PlanAsync(host.Source, pass, plan, host.Metrics, _cancellationToken).ConfigureAwait(false);
        if (settling is not null)
        {
            pass = settling.Pass(pass);
        }

        _outcome = new AggregationOutcome(plan, _partition.Slots, _partition.Keys, []);
        int degree = pass.Options.DegreeOfParallelism > 0 ? pass.Options.DegreeOfParallelism : host.Source.Session.Options.MaxDegreeOfParallelism;
        if (AggregationEngine.StreamingRanges(host.Source, pass, degree) is not { } ranges)
        {
            if (settling is not null)
            {
                _partition.Settle(settling, pass.Rows ?? new RowRange(0, long.MaxValue));
            }

            _pass = pass;
            _inner = host.Source.BatchesAsync(pass, host.Metrics).GetAsyncEnumerator(_cancellationToken);
            return;
        }

        // On several lanes, each range of rows is grouped on its own, and the ranges follow one
        // another here in the order of the rows: the structures are read once for all of them.
        if (settling is null && pass.Filter is { } filter && pass.Options.Pruning && host.Source is FileScanSource file)
        {
            BlockMask? live = await ZonePruningPlan
                .RefineAsync(file.File, file.File.LayoutTree, FunctionFieldExpr.Ranges(filter), _cancellationToken, steps: null, host.Metrics, pass.Options.UseIndexes)
                .ConfigureAwait(false);
            pass = pass with { Pruned = true, Live = live };
        }

        _pass = pass;
        ScanSpec lane = pass with { Options = pass.Options with { DegreeOfParallelism = 1, Prefetch = 0 } };
        _ranges = new StreamingRanges(
            ranges,
            degree,
            async (rows, token) =>
            {
                AggregationPartition range = new AggregationPartition(
                    plan, new AggregateSlot?[plan.Aggregates.Length], columns, inputs, sorted: plan.Keys.Length == 1, streaming, host.Source);
                if (settling is not null)
                {
                    range.Settle(settling, rows);
                }

                await AggregationEngine.RunPartitionAsync(host.Source, lane with { Rows = rows }, host.Metrics, range, token).ConfigureAwait(false);
                return range;
            },
            _cancellationToken);
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
                    // The filter's columns, store and evaluator, kept from one closed batch to the next.
                    _filters ??= new GroupSelection.GroupFilterRun?[operators.Length];
                    GroupSelection.GroupFilterRun run = _filters[o] ??= new GroupSelection.GroupFilterRun(_query, filter);
                    count = run.Keep(_outcome!, _closed, count, _cancellationToken);
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
        _ranges?.Stop();
        _current?.Dispose();
        _store?.Release();
        _arena?.Reset();
        if (_filters is not null)
        {
            foreach (GroupSelection.GroupFilterRun? run in _filters)
            {
                run?.Dispose();
            }

            _filters = null;
        }
        if (_begun && !_ended)
        {
            _ended = true;
            _query.Host.End();
        }
    }
}

/// <summary>
/// The ranges of a streaming group by's rows, each grouped on a lane of its own and handed back whole
/// in the order of the rows: as many ranges in flight as the degree, so that the memory is theirs
/// and a range's groups go out once it and those before it are done. Every range started is a task
/// awaited, here or when the stream is disposed, and cancelled when what is left is not wanted.
/// </summary>
internal sealed class StreamingRanges : IAsyncDisposable
{
    private readonly RowRange[] _ranges;
    private readonly Func<RowRange, CancellationToken, Task<AggregationPartition>> _group;
    private readonly Task<AggregationPartition>?[] _flight;
    private readonly CancellationTokenSource _stop;
    private int _next;
    private int _started;

    internal StreamingRanges(
        RowRange[] ranges, int degree, Func<RowRange, CancellationToken, Task<AggregationPartition>> group, CancellationToken cancellationToken)
    {
        _ranges = ranges;
        _group = group;
        _flight = new Task<AggregationPartition>?[Math.Max(1, Math.Min(degree, ranges.Length))];
        _stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    }

    /// <summary>The partition of the next range once it is grouped, or null after the last.</summary>
    internal async ValueTask<AggregationPartition?> NextAsync()
    {
        Start();
        if (_next == _ranges.Length)
        {
            return null;
        }

        int at = _next % _flight.Length;
        AggregationPartition partition = await _flight[at]!.ConfigureAwait(false);
        _flight[at] = null;
        _next++;
        Start();
        return partition;
    }

    /// <summary>Stops the ranges in flight: what is left of the rows is not wanted.</summary>
    internal void Stop() => _stop.Cancel();

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);

        // Their outcome is no one's now, a failure included: awaited, so that nothing is left running.
        List<Task> running = [];
        foreach (Task<AggregationPartition>? grouping in _flight)
        {
            if (grouping is not null)
            {
                running.Add(grouping);
            }
        }

        await Task.WhenAll(running).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        _stop.Dispose();
    }

    /// <summary>
    /// Starts the ranges the window has room for, on the pool, in the order of the rows: the pool
    /// takes them in that order, so the range waited for is never queued behind the others. The
    /// first is a block, which answers as the first batch of one stream would.
    /// </summary>
    private void Start()
    {
        while (_started < _ranges.Length && _started - _next < _flight.Length)
        {
            RowRange rows = _ranges[_started];
            CancellationToken token = _stop.Token;
            _flight[_started % _flight.Length] = Task.Run(() => _group(rows, token), token);
            _started++;
        }
    }
}
