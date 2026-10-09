using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.Compute;
using Vorticity.Expressions;
using Vorticity.Layouts;
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

    // The record the result's columns are named and typed by, if any.
    private readonly (VortexSchema Record, Type Type)? _typed;

    internal AggregationQuery(AggregationHost host, AggregationPlan plan, IResultNode[] nodes, ColumnShape[] keys, VortexExpr? rows = null, GroupOperator[]? operators = null)
        : this(host, plan, nodes, keys, null, 0, long.MaxValue, rows, operators ?? [])
    {
    }

    private AggregationQuery(
        AggregationHost host, AggregationPlan plan, IResultNode[] nodes, ColumnShape[] keys, (VortexSchema Record, Type Type)? typed, long skip, long take, VortexExpr? rows,
        GroupOperator[] operators)
    {
        Host = host;
        Plan = plan;
        Nodes = nodes;
        Keys = keys;
        _typed = typed;
        Columns = NewColumns();
        Skip = skip;
        Take = take;
        RowFilter = rows;
        Operators = operators;
        ChosenReader = GroupSelection.ChosenReader(operators);
    }

    /// <summary>The conjuncts of the filters on groups that name only the key: they filter the rows.</summary>
    internal VortexExpr? RowFilter { get; }

    /// <summary>
    /// The first operator on groups that reads a column of a chosen row, before which those columns
    /// are fetched for the groups left; the number of operators when none does, the columns being
    /// fetched then for the groups delivered.
    /// </summary>
    internal int ChosenReader { get; }

    /// <summary>
    /// The most groups the last run held at once: every group of a blocking one, the open ones and a
    /// batch's closed ones of a streaming one. Kept with the plan, which the query a record's columns
    /// read it through (<c>As</c>) shares.
    /// </summary>
    internal long PeakGroups
    {
        get => Plan.PeakGroups;
        set => Plan.PeakGroups = value;
    }

    /// <summary>What follows the group by before its <c>Select</c>, in the order written.</summary>
    internal GroupOperator[] Operators { get; }

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
        return new AggregationQuery(Host, Plan, Nodes, Keys, _typed, from, count, RowFilter, Operators);
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

        return new AggregationQuery(Host, Plan, Nodes, Keys, (record, type), Skip, Take, RowFilter, Operators);
    }

    /// <summary>
    /// The result's columns, made anew: each keeps what it reads a batch of groups with, and a worker that
    /// builds the batches of a part builds with a set of its own.
    /// </summary>
    internal ResultColumn[] NewColumns()
    {
        if (_typed is not { } typed)
        {
            return Natural(Nodes, Keys);
        }

        ResultColumn[] columns = new ResultColumn[Nodes.Length];
        for (int i = 0; i < columns.Length; i++)
        {
            columns[i] = Nodes[i].Column(typed.Record[i], Keys, i, typed.Type);
        }

        return columns;
    }

    /// <summary>The result's batches: the query runs when the first is asked for.</summary>
    internal override IAsyncEnumerator<RecordBatch> Batches(CancellationToken cancellationToken) => Groups(cancellationToken);

    /// <summary>
    /// The result's batches: as the groups close, on a key that streams, or one its zones prove final
    /// as the read goes when they nearly sort; once the pass has run, on any other.
    /// </summary>
    internal IAsyncEnumerator<RecordBatch> Groups(CancellationToken cancellationToken) =>
        Plan.Blocking ? new GroupBatches(this, cancellationToken)
        : StreamingGroupBatches.Streaming(this) >= 0 ? new StreamingGroupBatches(this, cancellationToken)
        : ZoneFinality.Candidate(this) ? new ZoneDecidedGroups(this, cancellationToken)
        : new GroupBatches(this, cancellationToken);

    internal override GroupStatistics? Grouping => Plan.Grouped ? Plan.Statistics() : null;

    internal override async ValueTask<ScanPlan> ExplainAsync(CancellationToken cancellationToken)
    {
        ScanPlan scan = await Host.ExplainAsync(Plan, cancellationToken, RowFilter).ConfigureAwait(false);
        return Plan.Grouped ? scan with { Grouping = GroupPlans.Of(this) } : scan;
    }

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
    private int[] _groups = [];
    private int _count;
    private int _next;
    private StructStore? _store;
    private CanonicalArena? _arena;
    private RecordBatch? _current;
    private bool _done;

    // A result its core delivers part by part, after the groups the
    // first outcome holds, which holds the query's memory until the end: the parts left; what builds
    // their batches; the part in hand and its next batch; the batch delivered, whose store goes back to
    // the builder once the next is; the groups of the result passed so far, in the window or not; and
    // the arena a batch cut to the window is cut into.
    private ResultParts? _parts;
    private PartBuilder? _builder;
    private PartResult? _part;
    private int _slate;
    private PartSlate? _given;
    private long _position;
    private CanonicalArena? _cut;

    // A result whose groups spilled, under an order: the sort its parts'
    // rows go through, its rows in order, and the window over them, the windows after the order and the
    // result's own.
    private ExternalSort? _sort;
    private IAsyncEnumerator<RecordBatch>? _sorted;
    private long _sortSkip;
    private long _sortReach;

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

        if (_outcome is null)
        {
            return RunAsync();
        }

        if (_sorted is not null)
        {
            return NextSortedAsync();
        }

        if (_part is null && Next())
        {
            return new ValueTask<bool>(true);
        }

        return _parts is null ? new ValueTask<bool>(End()) : NextPartAsync();
    }

    /// <summary>The result let go: its sort's runs deleted, the core's workers, when it delivers part by part, stopped and awaited first.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_sorted is not null)
        {
            await _sorted.DisposeAsync().ConfigureAwait(false);
            _sorted = null;
        }

        if (_sort is not null)
        {
            await _sort.DisposeAsync().ConfigureAwait(false);
            _sort = null;
        }

        if (_parts is not null && !_done)
        {
            await _parts.StopAsync().ConfigureAwait(false);
        }

        Release();
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<bool> RunAsync()
    {
        long started = Stopwatch.GetTimestamp();
        _builder = new PartBuilder(_query, _batchRows);
        (_outcome, _groups, _count) = await _query.Host.RunAsync(_query, _builder, _cancellationToken).ConfigureAwait(false);
        _parts = _outcome.Parts;
        _position = _count;
        bool first;
        if (_parts is not null && Array.Exists(_query.Operators, op => op is GroupOrder or GroupWindow))
        {
            if (SortedWindow() is not { } sorted)
            {
                int parts = _parts.Count;
                Release();
                throw new VortexMemoryException(
                    $"The group by spilled {parts} parts of its groups to its scratch, its memory budget holding no more: an order over them sorts in runs with filters alone before it and windows alone after it, and a window without an order needs every group at once. Give its session a larger QueryMemoryBudget, or filter the rows first.");
            }

            first = await SortPartsAsync(sorted.Order, sorted.Skip, sorted.Reach).ConfigureAwait(false);
        }
        else
        {
            first = Next() || (_parts is not null ? await NextPartAsync().ConfigureAwait(false) : End());
        }

        _query.Plan.LastFirstBatchTicks = Stopwatch.GetTimestamp() - started;
        return first;
    }

    /// <summary>
    /// The order of the query's operators, with filters alone before it and windows alone after it, and
    /// the groups of the order those windows and the result's own keep, from its <c>Skip</c>-th to its
    /// reach; null for any other.
    /// </summary>
    private (int Order, long Skip, long Reach)? SortedWindow()
    {
        GroupOperator[] operators = _query.Operators;
        int order = Array.FindIndex(operators, op => op is GroupOrder);
        if (order < 0 || Array.FindIndex(operators, 0, order, op => op is not GroupFilter) >= 0)
        {
            return null;
        }

        long start = 0;
        long reach = long.MaxValue;
        for (int o = order + 1; o < operators.Length; o++)
        {
            if (operators[o] is not GroupWindow window)
            {
                return null;
            }

            (start, reach) = KeyTop.Narrowed(start, reach, window.Skip, window.Take);
        }

        (start, reach) = KeyTop.Narrowed(start, reach, _query.Skip, _query.Take);
        return (order, start, reach);
    }

    /// <summary>
    /// The groups of a result that spilled sorted in runs under its order:
    /// the groups held in memory, then each part spilled, through the operators before the order, built
    /// into the sort's rows — the order's results, the key's components that break their ties, then the
    /// result's columns — and the first batch of their order.
    /// </summary>
    private async ValueTask<bool> SortPartsAsync(int order, long skip, long reach)
    {
        GroupOrder ordered = (GroupOrder)_query.Operators[order];
        ColumnShape[] keys = _query.Keys;
        ResultColumn[] results = _query.Columns;
        List<VortexField> fields = [];
        List<SortKey> chain = [];
        for (int i = 0; i < ordered.Keys.Length; i++)
        {
            VortexType type = ordered.Keys[i].Field.Column(keys).Type;
            fields.Add(new VortexField($"$order{i}", type));
            chain.Add(new SortKey(new FieldExpr($"$order{i}"), type, ordered.Keys[i].Descending));
        }

        for (int k = 0; k < keys.Length; k++)
        {
            fields.Add(new VortexField($"$tie{k}", keys[k].Type));
            chain.Add(new SortKey(new FieldExpr($"$tie{k}"), keys[k].Type, Descending: false));
        }

        int[] delivered = new int[results.Length];
        for (int c = 0; c < results.Length; c++)
        {
            delivered[c] = fields.Count;
            fields.Add(new VortexField(results[c].Name, results[c].Type));
        }

        VortexSchema schema = VortexSchema.Create([.. fields]);
        _builder!.SortRows(order, schema, () => SortColumns(ordered, keys));
        _sort = new ExternalSort(_query.Session, schema, [.. chain], delivered, _outcome!.Memory, _batchRows);
        _sortSkip = skip;
        _sortReach = reach;
        _position = 0;
        await SortAsync(await _builder.BuildAsync(_outcome, _cancellationToken).ConfigureAwait(false)).ConfigureAwait(false);
        while (await _parts!.NextAsync(_cancellationToken).ConfigureAwait(false) is { } part)
        {
            _query.Plan.LastGroups += part.Groups;
            await SortAsync(part).ConfigureAwait(false);
        }

        _sorted = await _sort.SortedAsync(_cancellationToken).ConfigureAwait(false);
        return await NextSortedAsync().ConfigureAwait(false);
    }

    /// <summary>The columns of the sort's rows: the order's results, the key's components, then the result's own, made anew.</summary>
    private ResultColumn[] SortColumns(GroupOrder order, ColumnShape[] keys)
    {
        ResultColumn[] results = _query.NewColumns();
        ResultColumn[] columns = new ResultColumn[order.Keys.Length + keys.Length + results.Length];
        int c = 0;
        foreach (OrderKey key in order.Keys)
        {
            columns[c++] = key.Field.Column(keys);
        }

        for (int k = 0; k < keys.Length; k++)
        {
            columns[c++] = new KeyResultColumn($"$tie{k}", keys[k].Type, k);
        }

        results.CopyTo(columns, c);
        return columns;
    }

    /// <summary>A part's rows, built as the sort's, added to it, the stores they were built in given back.</summary>
    private async ValueTask SortAsync(PartResult result)
    {
        RecordBatch? batch = null;
        try
        {
            foreach (PartSlate slate in result.Slates)
            {
                batch?.Dispose();
                batch = RecordBatch.Over(slate.Arena, slate.Root, 0, batch);
                await _sort!.AddAsync(batch, _cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            batch?.Dispose();
            _builder!.Give(result);
        }
    }

    /// <summary>The next batch of the sorted rows within the window, cut where the window cuts it.</summary>
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<bool> NextSortedAsync()
    {
        while (true)
        {
            if (_position >= _sortReach || !await _sorted!.MoveNextAsync().ConfigureAwait(false))
            {
                await DisposeSortAsync().ConfigureAwait(false);
                return End();
            }

            RecordBatch batch = _sorted.Current;
            long first = _position;
            _position += batch.RowCount;
            int from = (int)Math.Clamp(_sortSkip - first, 0, batch.RowCount);
            int until = (int)Math.Clamp(_sortReach - first, from, batch.RowCount);
            if (from == until)
            {
                continue;
            }

            _cancellationToken.ThrowIfCancellationRequested();
            _current?.Dispose();
            CanonicalArena cut = _cut ??= new CanonicalArena(64, _query.Session.Options.EnginePool);
            cut.ResetKeepingBlocks();
            int root = CanonicalSlice.SliceAcross(batch.Arena, cut, batch.RootIndex, from, until - from);
            _current = RecordBatch.Over(cut, root, first + from - _sortSkip, _current);
            return true;
        }
    }

    /// <summary>The sorted rows' stream and the sort let go, its runs deleted.</summary>
    private async ValueTask DisposeSortAsync()
    {
        if (_sorted is { } sorted)
        {
            _sorted = null;
            await sorted.DisposeAsync().ConfigureAwait(false);
        }

        if (_sort is { } sort)
        {
            _sort = null;
            await sort.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The next batch of the parts of a result its core delivers part by part, within the result's
    /// window: those the workers built as they applied them, as they come, those the core spilled built
    /// once brought back, after.
    /// </summary>
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<bool> NextPartAsync()
    {
        while (true)
        {
            if (_part is { } part && _slate < part.Slates.Count)
            {
                if (Deliver(part.Slates[_slate++]))
                {
                    return true;
                }

                continue;
            }

            if (_query.Take != long.MaxValue && _position >= _query.Skip + _query.Take)
            {
                // The window is full: the workers left are stopped before the memory goes.
                await _parts!.StopAsync().ConfigureAwait(false);
                return End();
            }

            if (await _parts!.NextAsync(_cancellationToken).ConfigureAwait(false) is not { } next)
            {
                return End();
            }

            _part = next;
            _slate = 0;
            _query.Plan.LastGroups += next.Groups;
        }
    }

    /// <summary>
    /// The batch of <paramref name="slate"/>, cut to the result's window, as the current one, the store of
    /// the one before given back; false, the store given back, when the window keeps none of it.
    /// </summary>
    private bool Deliver(PartSlate slate)
    {
        long first = _position;
        _position += slate.Rows;
        int from = (int)Math.Clamp(_query.Skip - first, 0, slate.Rows);
        int until = _query.Take == long.MaxValue ? slate.Rows : (int)Math.Clamp(_query.Skip + _query.Take - first, from, slate.Rows);
        if (from == until)
        {
            _builder!.Give(slate);
            return false;
        }

        _cancellationToken.ThrowIfCancellationRequested();
        _current?.Dispose();
        GiveBack();
        _given = slate;
        long start = first + from - _query.Skip;
        if (from == 0 && until == slate.Rows)
        {
            _current = RecordBatch.Over(slate.Arena, slate.Root, start, _current);
            return true;
        }

        // A batch the window cuts: its records cut into an arena of the result's, over the store's buffers.
        CanonicalArena cut = _cut ??= new CanonicalArena(64, _query.Session.Options.EnginePool);
        cut.ResetKeepingBlocks();
        int root = CanonicalSlice.SliceAcross(slate.Arena, cut, slate.Root, from, until - from);
        _current = RecordBatch.Over(cut, root, start, _current);
        return true;
    }

    /// <summary>The store of the batch delivered back to the builder, its batch disposed.</summary>
    private void GiveBack()
    {
        if (_given is { } given)
        {
            _given = null;
            _builder!.Give(given);
        }
    }

    /// <summary>The next batch of the groups the first outcome holds, within the result's window; false once it has none left.</summary>
    private bool Next()
    {
        AggregationOutcome outcome = _outcome!;
        int[] order = _groups;
        if (_next == 0 && _query.Skip > 0)
        {
            _next = (int)Math.Min(_query.Skip, _count);
        }

        long end = _query.Take == long.MaxValue ? _count : Math.Clamp(_query.Skip + _query.Take, 0, _count);
        if (_next >= end)
        {
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

    /// <summary>The end of the result: everything it held given back.</summary>
    private bool End()
    {
        Release();
        return false;
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
        GiveBack();
        if (_part is { } part)
        {
            _part = null;
            for (int s = _slate; s < part.Slates.Count; s++)
            {
                _builder!.Give(part.Slates[s]);
            }
        }

        _store?.Release();
        _arena?.Reset();
        _cut?.Reset();

        // The outcome held in memory holds the query's memory and its spilled parts: given back last,
        // then the parts' stores.
        _outcome?.Delivered();
        _builder?.Release();
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

/// <summary>
/// The groups a query delivers, in order, once its pass has run: its operators on groups applied
/// in the order written to the groups the pass found.
/// </summary>
internal static class GroupSelection
{
    /// <summary>The groups a filter evaluates at once: their results' columns stay in the cache.</summary>
    private const int Window = 4_096;

    /// <summary>The first of <paramref name="operators"/> that reads a column of a chosen row; their number when none does.</summary>
    internal static int ChosenReader(GroupOperator[] operators)
    {
        for (int o = 0; o < operators.Length; o++)
        {
            bool reads = operators[o] switch
            {
                GroupFilter filter => Array.Exists(filter.Fields, field => field.Result is IChosenColumn),
                GroupOrder order => Array.Exists(order.Keys, key => key.Field.Result is IChosenColumn),
                _ => false,
            };
            if (reads)
            {
                return o;
            }
        }

        return operators.Length;
    }

    /// <summary>
    /// The groups the query delivers, in order: its operators applied to the groups the pass found,
    /// and the columns of the chosen rows fetched for the groups left before the first operator that
    /// reads one, or else for the groups the result delivers.
    /// </summary>
    /// <param name="query">The query, whose operators and window apply.</param>
    /// <param name="outcome">The pass's groups and states, where the chosen rows' columns go.</param>
    /// <param name="spec">The pass's scan, which the fetch takes the rows from.</param>
    /// <param name="cancellationToken">Cancels the operators and the fetch.</param>
    /// <param name="windowed">
    /// Whether the result's window falls on these groups: not on a part of a result delivered part by
    /// part, whose reader cuts the window over every part.
    /// </param>
    /// <param name="until">
    /// The operators applied, the first ones; every one by default. A part of a result sorted in runs
    /// applies those before the order alone.
    /// </param>
    internal static async ValueTask<(int[] Groups, int Count)> ApplyAsync(
        AggregationQuery query, AggregationOutcome outcome, ScanSpec spec, CancellationToken cancellationToken, bool windowed = true, int? until = null)
    {
        int operators = until ?? query.Operators.Length;
        bool chosen = query.Plan.Chosen.Length > 0;
        int reader = chosen ? Math.Min(query.ChosenReader, operators) : operators;
        int degree = spec.Options.DegreeOfParallelism > 0 ? spec.Options.DegreeOfParallelism : query.Session.Options.MaxDegreeOfParallelism;
        (int[] groups, int count) = await OperatorsAsync(query, outcome, 0, reader, outcome.Order, outcome.Count, degree, cancellationToken).ConfigureAwait(false);
        if (!chosen)
        {
            return (groups, count);
        }

        if (reader < operators)
        {
            await ChosenFetch.FetchAsync(outcome, query.Host.Source, spec, query.Host.Metrics, groups.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
            return await OperatorsAsync(query, outcome, reader, operators, groups, count, degree, cancellationToken).ConfigureAwait(false);
        }

        // No operator reads them: the result's window alone is read.
        int from = windowed ? (int)Math.Min(query.Skip, count) : 0;
        int to = windowed ? (int)Math.Min(count, Saturated(query.Skip, query.Take)) : count;
        await ChosenFetch.FetchAsync(outcome, query.Host.Source, spec, query.Host.Metrics, groups.AsMemory(from, to - from), cancellationToken).ConfigureAwait(false);
        return (groups, count);
    }

    /// <summary>
    /// Operators <paramref name="start"/> to <paramref name="end"/> of the query, applied in order to the
    /// first <paramref name="count"/> of <paramref name="groups"/>; a top-k on up to <paramref name="degree"/>
    /// tasks.
    /// </summary>
    private static async ValueTask<(int[] Groups, int Count)> OperatorsAsync(
        AggregationQuery query, AggregationOutcome outcome, int start, int end, int[] groups, int count, int degree, CancellationToken cancellationToken)
    {
        GroupOperator[] operators = query.Operators;
        for (int o = start; o < end; o++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch (operators[o])
            {
                case GroupFilter filter:
                    (groups, count) = Filter(query, outcome, filter, groups, count, cancellationToken);
                    break;
                case GroupOrder order:
                {
                    // An order followed by a window ranks only the groups the window can keep; a few of
                    // many, chunk by chunk at once.
                    long keep = o + 1 < operators.Length
                        ? operators[o + 1] is GroupWindow window ? Saturated(window.Skip, window.Take) : long.MaxValue
                        : Saturated(query.Skip, query.Take);
                    int chunks = TopChunks(query, outcome, order, count, keep, degree);
                    query.Plan.LastTopChunks = chunks;
                    (groups, count) = chunks > 1
                        ? await TopInChunksAsync(query, outcome, order, groups, count, (int)keep, chunks, cancellationToken).ConfigureAwait(false)
                        : Order(query, outcome, order, groups, count, keep, cancellationToken);
                    break;
                }

                case GroupWindow window:
                {
                    int from = (int)Math.Min(window.Skip, count);
                    int kept = (int)Math.Min(count - from, window.Take);
                    groups = groups.AsSpan(from, kept).ToArray();
                    count = kept;
                    break;
                }

                default:
                    throw new InvalidOperationException($"An operator on groups of kind {operators[o].GetType().Name} is not known.");
            }
        }

        return (groups, count);
    }

    private static long Saturated(long skip, long take) => take == long.MaxValue || skip > long.MaxValue - take ? long.MaxValue : skip + take;

    /// <summary>The groups a chunk of a top-k ranks at least: fewer, its task costs more than it shares.</summary>
    private const int TopChunk = 65_536;

    /// <summary>
    /// The chunks a top-k of <paramref name="keep"/> among <paramref name="count"/> groups ranks on tasks
    /// of their own, up to <paramref name="degree"/>, each of <see cref="TopChunk"/> groups and sixteen
    /// times its k at least; one, ranking them all at once, when the groups are too few, or when the
    /// ranking would build a column, which the chunks would share, rather than read the results it
    /// orders by into arrays and break its ties on the groups' index.
    /// </summary>
    private static int TopChunks(AggregationQuery query, AggregationOutcome outcome, GroupOrder order, int count, long keep, int degree)
    {
        if (!query.Plan.TopInChunks || keep <= 0 || keep >= count)
        {
            return 1;
        }

        int chunks = (int)Math.Clamp(Math.Min(count / TopChunk, count / (16 * keep)), 1, Math.Max(1, degree));
        if (chunks == 1 || order.Keys.Length == 0 || outcome.Keys is not { } index)
        {
            return 1;
        }

        for (int k = 0; k < query.Keys.Length; k++)
        {
            if (!index.Orders(k))
            {
                return 1;
            }
        }

        foreach (OrderKey key in order.Keys)
        {
            if (!key.Field.Column(query.Keys).ReadsOrder)
            {
                return 1;
            }
        }

        return chunks;
    }

    /// <summary>
    /// The first <paramref name="keep"/> of many groups in the order's order:
    /// the groups cut into <paramref name="chunks"/>, each chunk's first <paramref name="keep"/> ranked on
    /// a task of its own, the results it orders by read into arrays the chunk's size, then those
    /// candidates ranked once. Every group of the first k is among the first k of its chunk, ties broken
    /// by the key in both: the groups and their order are those of one ranking.
    /// </summary>
    private static async ValueTask<(int[] Groups, int Count)> TopInChunksAsync(
        AggregationQuery query, AggregationOutcome outcome, GroupOrder order, int[] groups, int count, int keep, int chunks, CancellationToken cancellationToken)
    {
        int size = (count + chunks - 1) / chunks;
        Task<(int[] Groups, int Count)>[] ranking = new Task<(int[] Groups, int Count)>[chunks];
        using CancellationTokenSource failed = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        CancellationToken token = failed.Token;
        for (int c = 0; c < chunks; c++)
        {
            int first = c * size;
            int length = Math.Min(size, count - first);
            ranking[c] = Task.Run(() => TopOfChunk(query, outcome, order, groups, first, length, keep, token), token);
        }

        await AggregationEngine.GuardedAsync(ranking, failed).ConfigureAwait(false);
        int candidates = 0;
        foreach (Task<(int[] Groups, int Count)> chunk in ranking)
        {
            candidates += (await chunk.ConfigureAwait(false)).Count;
        }

        int[] best = new int[candidates];
        int at = 0;
        foreach (Task<(int[] Groups, int Count)> chunk in ranking)
        {
            (int[] kept, int n) = await chunk.ConfigureAwait(false);
            kept.AsSpan(0, n).CopyTo(best.AsSpan(at));
            at += n;
        }

        // Each chunk's ranking held its groups as a result's: the last one alone is.
        outcome.Memory?.LetGo((long)candidates * sizeof(int));
        return Order(query, outcome, order, best, candidates, keep, cancellationToken);
    }

    /// <summary>The first <paramref name="keep"/> of a chunk of <paramref name="groups"/>, in order, its groups in an array of the pool while it ranks.</summary>
    private static (int[] Groups, int Count) TopOfChunk(
        AggregationQuery query, AggregationOutcome outcome, GroupOrder order, int[] groups, int first, int length, int keep, CancellationToken cancellationToken)
    {
        int[] chunk = QueryArrays.Rent<int>(outcome.Memory, length, "top of a group by");
        try
        {
            groups.AsSpan(first, length).CopyTo(chunk);
            return Order(query, outcome, order, chunk, length, keep, cancellationToken);
        }
        finally
        {
            QueryArrays.Return(outcome.Memory, chunk);
        }
    }

    /// <summary>
    /// The groups in the order's order, the first <paramref name="keep"/> at most. The results it
    /// reads are read into arrays of the pool when they are numbers, built into columns otherwise;
    /// the key, which breaks every tie, is read from the groups' index where it orders the keys as
    /// their column would, built otherwise.
    /// </summary>
    /// <remarks>
    /// A top-k ranks on the order's own columns first: what they put before the k-th group needs no
    /// tie-break, and the groups tied with it are ranked by the key alone. On a high-cardinality
    /// text key whose groups share their count, that is every comparison, which would otherwise
    /// copy a million keys to keep a hundred.
    /// </remarks>
    internal static (int[] Groups, int Count) Order(
        AggregationQuery query, AggregationOutcome outcome, GroupOrder order, int[] groups, int count, long keep, CancellationToken cancellationToken)
    {
        ColumnShape[] keys = query.Keys;
        GroupKeys? index = outcome.Keys;
        int width = order.Keys.Length + keys.Length;
        ReadOnlySpan<int> all = groups.AsSpan(0, count);
        ColumnOrder?[] ready = new ColumnOrder?[width];
        QueryMemory? memory = outcome.Memory;
        int[] positions = QueryArrays.Rent<int>(memory, count, "order of a group by");
        StructStore? store = null;
        CanonicalArena? arena = null;
        try
        {
            // Each column of the chain: ordered already, read into the pool or from the index, or
            // built, at its place in the store.
            List<ResultColumn> built = [];
            int[] place = new int[width];
            for (int i = 0; i < order.Keys.Length; i++)
            {
                ResultColumn column = order.Keys[i].Field.Column(keys);
                ready[i] = column.OrderOf(outcome, all, order.Keys[i].Descending);
                if (ready[i] is null)
                {
                    place[i] = built.Count;
                    built.Add(column);
                }
            }

            bool indexed = index is not null;
            for (int k = 0; k < keys.Length; k++)
            {
                if (index is not null && index.Orders(k))
                {
                    ready[order.Keys.Length + k] = ColumnOrder.ForKeys(index, groups, k);
                    continue;
                }

                indexed = false;
                place[order.Keys.Length + k] = built.Count;
                built.Add(new KeyResultColumn($"$tie{k}", keys[k].Type, k));
            }

            CanonicalNode structure = default;
            if (built.Count > 0)
            {
                VortexField[] fields = new VortexField[built.Count];
                for (int i = 0; i < fields.Length; i++)
                {
                    fields[i] = new VortexField($"${i}", built[i].Type);
                }

                VortexSessionOptions options = query.Session.Options;
                store = (StructStore)ColumnStores.Create(VortexTypes.ToDType(VortexSchema.Create(fields), new DTypeArena()), options.EnginePool, options.Extensions);
                arena = new CanonicalArena(64, options.EnginePool);
                for (int c = 0; c < built.Count; c++)
                {
                    built[c].Append(outcome, store.Children[c], all);
                }

                structure = arena.GetNode(store.Build(arena, count));
            }

            ColumnOrder[] orders = new ColumnOrder[width];
            for (int c = 0; c < width; c++)
            {
                orders[c] = ready[c]
                    ?? ColumnOrder.For(arena!, structure.GetFieldIndex(place[c]), built[place[c]].Type, c < order.Keys.Length && order.Keys[c].Descending);
            }

            for (int i = 0; i < count; i++)
            {
                positions[i] = i;
            }

            int kept;
            if (keep < count && order.Keys.Length > 0)
            {
                // The groups the order's columns put before the k-th, ranked by the whole chain;
                // then, of those tied with it, the first by the key alone.
                // An order of one column ranks in a loop typed on its values when it holds them.
                (int before, int tied) = order.Keys.Length == 1
                    ? orders[0].TopTied(positions, count, (int)keep, memory, cancellationToken)
                    : GroupSort.TopTied(positions, count, new ChainOrder(orders[..order.Keys.Length]), (int)keep, memory, cancellationToken);
                positions.AsSpan(0, before).Sort(new ChainOrder(orders));
                Span<int> ties = positions.AsSpan(before, tied);
                int need = (int)keep - before;
                kept = before + (indexed
                    ? GroupSort.TopK(ties, new IndexOrder(index!, groups, keys.Length), need, memory, cancellationToken)
                    : GroupSort.TopK(ties, new ChainOrder(orders[order.Keys.Length..]), need, memory, cancellationToken));
            }
            else
            {
                kept = GroupSort.Sort(positions, count, new ChainOrder(orders), keep, memory, cancellationToken);
            }

            // The groups in their order, the result's until it is delivered.
            memory?.Hold((long)kept * sizeof(int), "order of a group by");
            int[] ordered = new int[kept];
            for (int i = 0; i < kept; i++)
            {
                ordered[i] = groups[positions[i]];
            }

            return (ordered, kept);
        }
        finally
        {
            // What the chain read into the pool goes back; the orders over the store hold none of it.
            foreach (ColumnOrder? read in ready)
            {
                read?.Release();
            }

            QueryArrays.Return(memory, positions);
            store?.Release();
            arena?.Reset();
        }
    }

    /// <summary>The groups the filter keeps, evaluated on the columns of the results it reads, a window of groups at a time.</summary>
    internal static (int[] Groups, int Count) Filter(
        AggregationQuery query, AggregationOutcome outcome, GroupFilter filter, int[] groups, int count, CancellationToken cancellationToken)
    {
        using GroupFilterRun run = new GroupFilterRun(query, filter);
        int[] kept = groups.AsSpan(0, count).ToArray();
        return (kept, run.Keep(outcome, kept, count, cancellationToken));
    }

    /// <summary>
    /// A filter on groups and what it reads their results into: the columns, a store, an arena and
    /// the evaluator, made once for every batch of groups a stream closes.
    /// </summary>
    internal sealed class GroupFilterRun : IDisposable
    {
        private readonly ResultColumn[] _columns;
        private readonly StructStore _store;
        private readonly CanonicalArena _arena;
        private readonly FilterEvaluator _evaluator;
        private byte[] _states = [];

        internal GroupFilterRun(AggregationQuery query, GroupFilter filter)
        {
            _columns = new ResultColumn[filter.Fields.Length];
            VortexField[] fields = new VortexField[_columns.Length];
            for (int i = 0; i < _columns.Length; i++)
            {
                _columns[i] = filter.Fields[i].Column(query.Keys);
                fields[i] = new VortexField(_columns[i].Name, _columns[i].Type);
            }

            VortexSessionOptions options = query.Session.Options;
            _store = (StructStore)ColumnStores.Create(VortexTypes.ToDType(VortexSchema.Create(fields), new DTypeArena()), options.EnginePool, options.Extensions);
            _arena = new CanonicalArena(64, options.EnginePool);
            _evaluator = new FilterEvaluator(filter.Predicate);
        }

        /// <summary>Keeps in place, in their order, the first <paramref name="count"/> of <paramref name="groups"/> the filter keeps; their number.</summary>
        internal int Keep(AggregationOutcome outcome, int[] groups, int count, CancellationToken cancellationToken)
        {
            Scratch.Grow(ref _states, Math.Min(Window, Math.Max(count, 1)));
            int kept = 0;
            for (int first = 0; first < count; first += Window)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int n = Math.Min(Window, count - first);
                ReadOnlySpan<int> window = groups.AsSpan(first, n);
                _store.Truncate(0);
                for (int c = 0; c < _columns.Length; c++)
                {
                    _columns[c].Append(outcome, _store.Children[c], window);
                }

                _arena.ResetKeepingBlocks();
                int root = _store.Build(_arena, n);
                _evaluator.Evaluate(_arena, root, n, _states.AsSpan(0, n));

                // A group kept moves to the front, never past one not read yet.
                for (int i = 0; i < n; i++)
                {
                    if (_states[i] == Trilean.True)
                    {
                        groups[kept++] = groups[first + i];
                    }
                }
            }

            return kept;
        }

        public void Dispose()
        {
            _store.Release();
            _arena.Reset();
        }
    }
}
