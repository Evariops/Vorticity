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

    internal AggregationQuery(AggregationHost host, AggregationPlan plan, IResultNode[] nodes, ColumnShape[] keys, VortexExpr? rows = null, GroupOperator[]? operators = null)
        : this(host, plan, nodes, keys, Natural(nodes, keys), 0, long.MaxValue, rows, operators ?? [])
    {
    }

    private AggregationQuery(
        AggregationHost host, AggregationPlan plan, IResultNode[] nodes, ColumnShape[] keys, ResultColumn[] columns, long skip, long take, VortexExpr? rows, GroupOperator[] operators)
    {
        Host = host;
        Plan = plan;
        Nodes = nodes;
        Keys = keys;
        Columns = columns;
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
        return new AggregationQuery(Host, Plan, Nodes, Keys, Columns, from, count, RowFilter, Operators);
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

        return new AggregationQuery(Host, Plan, Nodes, Keys, columns, Skip, Take, RowFilter, Operators);
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
        long started = Stopwatch.GetTimestamp();
        (_outcome, _groups, _count) = await _query.Host.RunAsync(_query, _cancellationToken).ConfigureAwait(false);
        bool first = Next();
        _query.Plan.LastFirstBatchTicks = Stopwatch.GetTimestamp() - started;
        return first;
    }

    private bool Next()
    {
        AggregationOutcome outcome = _outcome!;
        int[] order = _groups;
        if (_next == 0 && _query.Skip > 0)
        {
            _next = (int)Math.Min(_query.Skip, _count);
        }

        long end = _query.Take == long.MaxValue ? _count : Math.Min(_count, _query.Skip + _query.Take);
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
        _outcome?.Delivered();
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
    internal static async ValueTask<(int[] Groups, int Count)> ApplyAsync(
        AggregationQuery query, AggregationOutcome outcome, ScanSpec spec, CancellationToken cancellationToken)
    {
        int operators = query.Operators.Length;
        bool chosen = query.Plan.Chosen.Length > 0;
        int reader = chosen ? query.ChosenReader : operators;
        (int[] groups, int count) = Apply(query, outcome, 0, reader, outcome.Order, outcome.Order.Length, cancellationToken);
        if (!chosen)
        {
            return (groups, count);
        }

        if (reader < operators)
        {
            await ChosenFetch.FetchAsync(outcome, query.Host.Source, spec, query.Host.Metrics, groups.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
            return Apply(query, outcome, reader, operators, groups, count, cancellationToken);
        }

        // No operator reads them: the result's window alone is read.
        int from = (int)Math.Min(query.Skip, count);
        int until = (int)Math.Min(count, Saturated(query.Skip, query.Take));
        await ChosenFetch.FetchAsync(outcome, query.Host.Source, spec, query.Host.Metrics, groups.AsMemory(from, until - from), cancellationToken).ConfigureAwait(false);
        return (groups, count);
    }

    /// <summary>Operators <paramref name="start"/> to <paramref name="end"/> of the query, applied in order to the first <paramref name="count"/> of <paramref name="groups"/>.</summary>
    private static (int[] Groups, int Count) Apply(
        AggregationQuery query, AggregationOutcome outcome, int start, int end, int[] groups, int count, CancellationToken cancellationToken)
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
                    // An order followed by a window ranks only the groups the window can keep.
                    long keep = o + 1 < operators.Length
                        ? operators[o + 1] is GroupWindow window ? Saturated(window.Skip, window.Take) : long.MaxValue
                        : Saturated(query.Skip, query.Take);
                    (groups, count) = Order(query, outcome, order, groups, count, keep, cancellationToken);
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
