using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.Compute;
using Vorticity.Expressions;
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

    // On several lanes: the ranges grouped side by side, what each did, the time spent following
    // them, and the numbers of a range's groups past those every thread shares.
    private StreamingRanges? _ranges;
    private List<AggregationRun.Lane>? _lanes;
    private long _mergeTicks;
    private int[]? _numbers;

    // On a key its zones prove final as the read goes: the floors, the first row not read yet, and
    // the ranges' rows when they go side by side.
    private readonly ZoneFinality? _zones;
    private long _nextRow;
    private RowRange[] _rangeRows = [];
    private int _followed;

    // What the stream's tables hold, reserved in its session's budget as they grow, its lanes' working
    // memory admitted first; given back when the stream ends (PLAN-HIGH-CARDINALITY, H2).
    private QueryMemory? _memory;

    internal StreamingGroupBatches(AggregationQuery query, CancellationToken cancellationToken, ZoneFinality? zones = null)
    {
        _query = query;
        _cancellationToken = cancellationToken;
        _zones = zones;
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
    /// sorted, or the source reads in its order when asked, and no order but those that start with
    /// it, all one way. Descending, the rows are read in the component's order backwards.
    /// </summary>
    /// <remarks>
    /// Backwards, a group's rows come last first and a window before the order would take the groups
    /// from the other end, so a descending order streams only where nothing before it reads an order:
    /// no window, no first or last row, no tie, no custom aggregate. And only under a window after it,
    /// which stops the read: that is what the backward read is for, where a whole read is the
    /// blocking pass's, on every lane and with the blocks the zone maps settle.
    /// </remarks>
    internal static int Streaming(AggregationQuery query)
    {
        ColumnShape[] keys = query.Plan.Keys;
        ScanSource source = query.Host.Source;
        int streaming = -1;
        for (int c = 0; c < keys.Length && streaming < 0; c++)
        {
            streaming = AggregationEngine.IsSorted(source, keys[c]) || source.OrdersOnAsking(keys[c].Column.Field) ? c : -1;
        }

        if (streaming < 0)
        {
            return -1;
        }

        bool? descending = null;
        bool windowFirst = false;
        bool bounded = query.Take != long.MaxValue;
        foreach (GroupOperator op in query.Operators)
        {
            if (op is GroupWindow window)
            {
                windowFirst |= descending is null;
                bounded |= descending is not null && window.Take != long.MaxValue;
            }
            else if (op is GroupOrder order)
            {
                if (order.Keys[0].Field.Component != streaming || (descending is { } way && way != order.Keys[0].Descending))
                {
                    return -1;
                }

                descending = order.Keys[0].Descending;
            }
        }

        return descending == true && (windowFirst || !bounded || !OrderFree(query.Plan)) ? -1 : streaming;
    }

    /// <summary>Whether every aggregate of the plan gives the same answer whatever the order its rows come in.</summary>
    private static bool OrderFree(AggregationPlan plan)
    {
        foreach (IAggregateNode aggregate in plan.Aggregates)
        {
            if (aggregate.Kind is AggregateKind.First or AggregateKind.Last or AggregateKind.MinBy or AggregateKind.MaxBy or AggregateKind.Custom)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Whether a query that streams orders its groups on the streaming component descending: its rows are then read backwards.</summary>
    internal static bool Descends(AggregationQuery query)
    {
        foreach (GroupOperator op in query.Operators)
        {
            if (op is GroupOrder order)
            {
                return order.Keys[0].Descending;
            }
        }

        return false;
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
                _partition!.Follow(range, ref _numbers);
                _mergeTicks += Stopwatch.GetTimestamp() - merging;

                // The range's groups live in the partition now: its tables go, and what it held.
                range.LetGo();
                _partition.Recount();
                _nextRow = _rangeRows[_followed++].End;
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

            RecordBatch batch = _inner.Current;
            _partition!.Process(batch);
            _nextRow = Math.Max(_nextRow, batch.StartRow + batch.RowCount);
            _query.PeakGroups = Math.Max(_query.PeakGroups, _partition.Keys!.Count);
            await CloseAsync(all: false).ConfigureAwait(false);
        }
    }

    /// <summary>The end of the rows: every group closes, the null group last, and the plan keeps what the run did.</summary>
    private async ValueTask DrainAsync()
    {
        _drained = true;
        _query.Plan.LastRun = _lanes is null
            ? new AggregationRun([new AggregationRun.Lane(Stopwatch.GetTimestamp() - _started, 1, _partition!.Keys!.Count)], 0, 0, _partition.Footprint)
            : new AggregationRun([.. _lanes], _mergeTicks, _lanes.Count, _partition!.Footprint);
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

        // Every range awaited: none reserves any more.
        _memory?.Dispose();
    }

    private async ValueTask StartAsync()
    {
        AggregationHost host = _query.Host;
        host.Begin();
        _begun = true;
        _memory = new QueryMemory(host.Source.Session.Options.MemoryBudget ?? QueryMemoryBudget.Process);
        _started = Stopwatch.GetTimestamp();
        _query.PeakGroups = 0;
        AggregationPlan plan = _query.Plan;
        (ColumnShape[] columns, int[] inputs) = AggregationEngine.Columns(plan, new AggregateSlot?[plan.Aggregates.Length]);
        ScanSpec pass = AggregationEngine.PassSpec(host.Spec(_query.RowFilter), columns, plan, host.Source.Schema);
        int streaming = _zones is null ? Streaming(_query) : 0;

        // A source that brings the rows in the component's order when asked is asked, backwards
        // when descending: a dataset's key-ordered read. A sorted column read descending has its
        // splits come last one first, each in file order, which is all a group by needs: the group
        // a batch leaves open is that of its first row, and its other groups are final.
        FieldExpr ordered = plan.Keys[streaming].Column.Field;
        bool descending = Descends(_query);
        bool sorted = AggregationEngine.IsSorted(host.Source, plan.Keys[streaming]);
        if (!sorted && host.Source.OrdersOnAsking(ordered))
        {
            pass = pass with { OrderPath = ordered.Path, Descending = descending };
        }
        else if (descending)
        {
            pass = pass with { Backward = true };
        }

        // The streaming component is sorted, which its part of a composite key reads as runs; a key
        // its zones prove final is not, and is grouped as any other.
        KeyFacts facts = AggregationEngine.Facts(host.Source, plan.Keys);
        bool runs = _zones is null && plan.Keys.Length == 1;
        if (_zones is null)
        {
            facts.Sorted[streaming] = true;
        }

        _partition = new AggregationPartition(
            plan, new AggregateSlot?[plan.Aggregates.Length], columns, inputs, sorted: runs, streaming, host.Source, facts, memory: _memory)
        {
            Backward = pass.Backward,
        };

        // The blocks the zone maps settle are folded in the order of the rows, which the groups
        // close in: forward only, as the ranges grouped side by side are, and on a sorted key.
        ZoneSettling? settling = descending || _zones is not null
            ? null
            : await ZoneSettling.PlanAsync(host.Source, pass, plan, host.Metrics, _cancellationToken).ConfigureAwait(false);
        if (settling is not null)
        {
            pass = settling.Pass(pass);
        }

        _outcome = new AggregationOutcome(plan, _partition.Slots, _partition.Keys, []) { Memory = _memory };
        int degree = pass.Options.DegreeOfParallelism > 0 ? pass.Options.DegreeOfParallelism : host.Source.Session.Options.MaxDegreeOfParallelism;
        if (descending || AggregationEngine.StreamingRanges(host.Source, pass, degree) is not { } ranges)
        {
            AggregationEngine.Admit(_memory, 1);
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
        _rangeRows = ranges;
        ScanSpec lane = pass with { Options = pass.Options with { DegreeOfParallelism = 1, Prefetch = 0 } };
        QueryMemory memory = _memory;
        _ranges = new StreamingRanges(
            ranges,
            AggregationEngine.Admit(memory, degree),
            async (rows, token) =>
            {
                AggregationPartition range = new AggregationPartition(
                    plan, new AggregateSlot?[plan.Aggregates.Length], columns, inputs, sorted: runs, streaming, host.Source, facts, memory: memory);
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
        GroupKeys keys = partition.Keys!;
        int count = keys.Count;
        int nullComponent = partition.ComponentNull;
        int last = all ? -1 : partition.LastValueGroup;

        // On a key its zones prove final: the groups below the smallest key a row to come can hold.
        long floor = all ? long.MaxValue : _zones?.Floor(_nextRow) ?? long.MinValue;
        Scratch.Grow(ref _closed, count);
        Scratch.Grow(ref _open, count);
        _closedCount = 0;
        _openCount = 0;
        for (int g = 0; g < count; g++)
        {
            int component = partition.ComponentOf(g);
            if (_zones is null ? component == nullComponent || component == last : !keys.Below(g, floor))
            {
                // Open, and at the end the groups of the null component, which go out last.
                _open[_openCount++] = g;
                continue;
            }

            _closed[_closedCount++] = g;
        }

        if (_zones is not null && _closedCount > 1)
        {
            // Final together, they go out in the order of their keys, which the stream is in.
            keys.SortByKey(_closed.AsSpan(0, _closedCount));
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

    /// <summary>
    /// Closes the groups now final and passes them through the operators, reading their chosen rows
    /// before the first operator that reads one, for the groups left, or else for those delivered.
    /// </summary>
    private async ValueTask CloseAsync(bool all)
    {
        Close(all);
        if (_closedCount == 0)
        {
            return;
        }

        int operators = _query.Operators.Length;
        bool chosen = _query.Plan.Chosen.Length > 0;
        int reader = chosen ? _query.ChosenReader : operators;
        int count = Through(0, reader, _closedCount);
        if (chosen && count > 0)
        {
            await ChosenFetch.FetchAsync(_outcome!, _query.Host.Source, _pass!, _query.Host.Metrics, _closed.AsMemory(0, count), _cancellationToken)
                .ConfigureAwait(false);
        }

        _closedCount = reader < operators ? Through(reader, operators, count) : count;
    }

    /// <summary>
    /// The first <paramref name="count"/> closed groups through operators <paramref name="start"/> to
    /// <paramref name="end"/>, in the order written, and the result's own window after the last; how
    /// many are left.
    /// </summary>
    private int Through(int start, int end, int count)
    {
        GroupOperator[] operators = _query.Operators;
        for (int o = start; o < end && count > 0; o++)
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
                case GroupOrder order when _query.Plan.Keys.Length > 1 || order.Keys.Length > 1 || order.Keys[0].Descending:
                {
                    // An order that starts with the streaming component: the groups closed together
                    // hold every group of their values of it, so sorting them is sorting the result.
                    // Descending, the batches come greatest first, and the groups of one ascend.
                    (int[] sorted, int sortedCount) = GroupSelection.Order(_query, _outcome!, order, _closed, count, long.MaxValue, _cancellationToken);
                    sorted.AsSpan(0, sortedCount).CopyTo(_closed);
                    _memory?.LetGo((long)sortedCount * sizeof(int));
                    count = sortedCount;
                    break;
                }

                default:
                    // An order on the one key, ascending: the stream is in it already.
                    break;
            }
        }

        return end == operators.Length ? Window(ref _resultSkip, ref _resultTake, count) : count;
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

        // On one lane nothing runs beside the stream; ranges in flight are awaited when it is disposed.
        if (_ranges is null)
        {
            _memory?.Dispose();
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
