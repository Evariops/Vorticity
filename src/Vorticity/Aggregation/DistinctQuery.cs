using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.Compute;
using Vorticity.Layouts;
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

    // The record the result's columns are named and typed by, if any.
    private readonly (VortexSchema Record, Type Type)? _typed;

    internal DistinctQuery(ProjectionQuery projection)
        : this(projection, Shapes(projection), KeysOf(projection), null, 0, long.MaxValue)
    {
    }

    private DistinctQuery(ProjectionQuery projection, ColumnShape[] keys, IResultNode[] nodes, (VortexSchema Record, Type Type)? typed, long skip, long take)
    {
        Projection = projection;
        Keys = keys;
        Nodes = nodes;
        Plan = new AggregationPlan([], keys);
        _typed = typed;
        Columns = NewColumns();
        Skip = skip;
        Take = take;
    }

    /// <summary>
    /// The result's columns made anew: each keeps what it reads a batch of values with, and each applier
    /// of the core that delivers them (PLAN-HIGH-CARDINALITY, H13) writes with a set of its own.
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

    internal ProjectionQuery Projection { get; }

    internal ColumnShape[] Keys { get; }

    internal IResultNode[] Nodes { get; }

    internal AggregationPlan Plan { get; }

    internal ResultColumn[] Columns { get; }

    internal long Skip { get; }

    internal long Take { get; }

    /// <summary>The most values the last run held at once: every one met, or a batch's on a column that streams.</summary>
    internal long PeakValues { get; set; }

    /// <summary>What the core did on the last run's path through it (PLAN-HIGH-CARDINALITY, H13); null when the run took the values on the reader's thread, or no lane's cache filled.</summary>
    internal CoreRun? LastCore { get; set; }

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

        return new DistinctQuery(Projection, Keys, Nodes, (record, type), Skip, Take);
    }

    internal override ResultQuery Limit(long skip, long take)
    {
        (long from, long count) = Within(Skip, Take, skip, take);
        return new DistinctQuery(Projection, Keys, Nodes, _typed, from, count);
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

    // The core's path (PLAN-HIGH-CARDINALITY, H13): the pass on its own task, on several lanes, each
    // value told as it enters its part's set; the batch delivered, whose store goes back once the next
    // is; the values passed, in the window or not; the arena a batch the window cuts is cut into.
    private DistinctEmitter? _emitter;
    private Task? _pass;
    private CancellationTokenSource? _stopping;
    private PartSlate? _given;
    private long _position;
    private CanonicalArena? _cut;

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
        if (_emitter is not null)
        {
            return await NextEmittedAsync().ConfigureAwait(false);
        }

        if (_inner is null)
        {
            ProjectionQuery projection = _query.Projection;
            projection.Host.Begin();
            _begun = true;

            // A group by with no aggregate; on a column the statistics say is sorted, a value
            // below the last one met never comes back, so the index forgets it.
            AggregationPlan plan = _query.Plan;
            for (int c = 0; c < plan.Keys.Length && _streaming < 0; c++)
            {
                _streaming = AggregationEngine.IsSorted(projection.Host.Source, plan.Keys[c]) ? c : -1;
            }

            if (StartCore(projection))
            {
                return await NextEmittedAsync().ConfigureAwait(false);
            }

            // The key's columns in the form they arrive in: a dictionary groups by code.
            ScanSpec spec = projection.SourceSpec(keepEncodings: true, out _) with
            {
                SinkDecodes = true,
                PositionsUnread = true,
                Options = projection.Host.Spec().Options with { Compact = false },
            };

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
        await StopCoreAsync().ConfigureAwait(false);
        Release();
        if (_inner is not null)
        {
            await _inner.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The core's path, when the values are many lanes' work and need no early stop
    /// (PLAN-HIGH-CARDINALITY, H13): a key the core holds, no component the statistics say is sorted,
    /// no window but a skip, several lanes. The pass runs on its own task, on the lean core: α at 1, a
    /// part's floor at 256 entries and its batches at a kilobyte, so that a part takes its values in as
    /// soon as it has a few (at the floor of 4 096 and batches of their default size, a part of 150 000
    /// values over 256 parts got its first entry at the end), its caches and batches sized on the
    /// budget, which it spills under. Each value is told as it enters its part's set, once.
    /// </summary>
    /// <returns>Whether the pass started; false to take the values on this thread, as they are met.</returns>
    private bool StartCore(ProjectionQuery projection)
    {
        ScanSpec spec = projection.SourceSpec(keepEncodings: false, out _);
        int degree = spec.Options.DegreeOfParallelism > 0 ? spec.Options.DegreeOfParallelism : projection.Host.Source.Session.Options.MaxDegreeOfParallelism;
        if (!_query.Plan.CoreDistinct || _streaming >= 0 || _query.Take != long.MaxValue || degree <= 1
            || _query.Plan.CreateKeys(sorted: false, facts: null).EntryBytes == 0
            || Bounded(AggregationEngine.Facts(projection.Host.Source, _query.Keys), (projection.Host.Source.Session.Options.MemoryBudget ?? QueryMemoryBudget.Process).CeilingBytes))
        {
            return false;
        }

        // The reader counts in the degree: its continuation goes through the pool's queue, which lanes
        // as many as its threads would hold until the pass is over.
        spec = spec with { Options = spec.Options with { DegreeOfParallelism = degree - 1 } };
        int batchRows = spec.Options.BatchRows > 0 ? spec.Options.BatchRows : GroupBatches.BatchRows;
        _emitter = new DistinctEmitter(_query, batchRows);
        AggregationPlan plan = new AggregationPlan([], _query.Keys)
        {
            Core = true,
            CoreLanes = 1,
            CoreLean = true,
            Emitter = _emitter,
        };

        _stopping = CancellationTokenSource.CreateLinkedTokenSource(_cancellationToken);
        CancellationToken token = _stopping.Token;
        _pass = Task.Run(() => PassAsync(projection, spec, plan, token), token);
        return true;
    }

    /// <summary>The values the statistics allow the key at most, when they bound every component, past which the core's path pays.</summary>
    private const long CoreValues = 1L << 21;

    /// <summary>The bytes a value takes at most in the index of the reader's thread, its key and its slot.</summary>
    private const long IndexBytes = 32;

    /// <summary>
    /// Whether the statistics bound the key to <see cref="CoreValues"/> values or fewer, whose index
    /// fits in half the budget's <paramref name="ceiling"/>: on the reader's thread it stays in a core's
    /// cache, faster than the lanes' small caches that pass nearly every row to the parts; past the
    /// budget, it would fail where the core spills. Measured on 20M rows at fourteen lanes: a million
    /// values took the core ×1.30 the reader's thread, ten million ×0.44.
    /// </summary>
    private static bool Bounded(KeyFacts facts, long ceiling)
    {
        double values = 1;
        foreach (KeyBounds? bounds in facts.Bounds)
        {
            if (bounds is not { } known)
            {
                return false;
            }

            values *= (double)known.Max - known.Min + 1;
        }

        return values <= CoreValues && values * IndexBytes <= ceiling / 2;
    }

    /// <summary>
    /// The pass of the core's path: every value told as it enters its part's set, those of the parts
    /// spilled once they come back, or, when no lane's cache ever filled, every value of the lanes'
    /// tables merged at the end; the reader's stream then completed, with the failure if any.
    /// </summary>
    private async Task PassAsync(ProjectionQuery projection, ScanSpec spec, AggregationPlan plan, CancellationToken cancellationToken)
    {
        DistinctEmitter emitter = _emitter!;
        Exception? failure = null;
        try
        {
            AggregationOutcome outcome = await AggregationEngine.RunAsync(projection.Host.Source, spec, projection.Metrics, plan, cancellationToken).ConfigureAwait(false);
            try
            {
                if (outcome.Parts is CoreParts parts)
                {
                    await parts.EmitSpilledAsync(cancellationToken).ConfigureAwait(false);
                }
                else if (plan.LastRun?.Core is null && outcome.Keys is { Count: > 0 } keys)
                {
                    emitter.EmitAlone(keys, 0, keys.Count);
                }

                _query.PeakValues = Math.Max(_query.PeakValues, plan.LastGroups);
                _query.LastCore = plan.LastRun?.Core;
            }
            finally
            {
                outcome.Delivered();
            }
        }
        catch (Exception caught)
        {
            failure = caught;
        }
        finally
        {
            emitter.Complete(failure);
        }
    }

    /// <summary>The next batch of the values told, past the window's skip, cut where it cuts one.</summary>
    private async ValueTask<bool> NextEmittedAsync()
    {
        DistinctEmitter emitter = _emitter!;
        while (await emitter.Emitted.WaitToReadAsync(_cancellationToken).ConfigureAwait(false))
        {
            if (!emitter.Emitted.TryRead(out PartSlate? slate))
            {
                continue;
            }

            long first = _position;
            _position += slate.Rows;
            int from = (int)Math.Clamp(_query.Skip - first, 0, slate.Rows);
            if (from == slate.Rows)
            {
                emitter.Give(slate);
                continue;
            }

            _current?.Dispose();
            if (_given is { } given)
            {
                emitter.Give(given);
            }

            _given = slate;
            _met = _position;
            if (from == 0)
            {
                _current = RecordBatch.Over(slate.Arena, slate.Root, first - _query.Skip, _current);
                return true;
            }

            CanonicalArena cut = _cut ??= new CanonicalArena(64, _query.Session.Options.EnginePool);
            cut.ResetKeepingBlocks();
            int root = CanonicalSlice.SliceAcross(slate.Arena, cut, slate.Root, from, slate.Rows - from);
            _current = RecordBatch.Over(cut, root, 0, _current);
            return true;
        }

        await StopCoreAsync().ConfigureAwait(false);
        Release();
        return false;
    }

    /// <summary>The core's pass stopped and awaited, its batches let go: none is written once the reader is done.</summary>
    private async ValueTask StopCoreAsync()
    {
        if (_pass is { } pass)
        {
            _pass = null;
            await _stopping!.CancelAsync().ConfigureAwait(false);
            try
            {
                await pass.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _current?.Dispose();
        if (_given is { } given)
        {
            _given = null;
            _emitter!.Give(given);
        }

        _emitter?.Release();
        _stopping?.Dispose();
        _stopping = null;
        _cut?.Reset();
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

/// <summary>
/// The values of a <c>Distinct</c> its core tells as they enter their parts' sets
/// (PLAN-HIGH-CARDINALITY, H13): each applier copies them into a batch of the result's columns of its
/// own, handed to the reader once full or once its part is applied, the reader delivering them as they
/// come. The batches are pooled: one delivered goes back for the next.
/// </summary>
internal sealed class DistinctEmitter : CoreEmitter
{
    private readonly DistinctQuery _query;
    private readonly int _batchRows;
    private readonly Channel<PartSlate> _emitted = Channel.CreateUnbounded<PartSlate>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Lock _gate = new Lock();
    private readonly Stack<PartSlate> _slates = [];
    private readonly DType _dtype;
    private bool _released;

    internal DistinctEmitter(DistinctQuery query, int batchRows)
    {
        _query = query;
        _batchRows = batchRows;
        _dtype = VortexTypes.ToDType(query.Schema, new DTypeArena());
    }

    /// <summary>The batches of values handed on, in the order they were.</summary>
    internal ChannelReader<PartSlate> Emitted => _emitted.Reader;

    internal override void Emit(CoreApplier applier, GroupKeys keys, int from, int to)
    {
        if (applier.Emission is not Emission emission)
        {
            emission = new Emission(_query.NewColumns());
            applier.Emission = emission;
        }

        Copy(emission, keys, from, to);
    }

    internal override void Flush(CoreApplier applier)
    {
        if (applier.Emission is Emission emission)
        {
            Publish(emission);
        }
    }

    internal override void EmitAlone(GroupKeys keys, int from, int to)
    {
        Emission emission = new Emission(_query.NewColumns());
        Copy(emission, keys, from, to);
        Publish(emission);
    }

    /// <summary>No value comes after those handed on, or the pass failed with <paramref name="failure"/>.</summary>
    internal void Complete(Exception? failure = null) => _emitted.Writer.TryComplete(failure);

    /// <summary>A batch back in the pool once delivered; let go once the result is.</summary>
    internal void Give(PartSlate slate)
    {
        lock (_gate)
        {
            if (!_released)
            {
                _slates.Push(slate);
                return;
            }
        }

        slate.Release();
    }

    /// <summary>Every batch let go, those pooled and those never read: the result is done, its pass with it.</summary>
    internal void Release()
    {
        lock (_gate)
        {
            _released = true;
        }

        foreach (PartSlate slate in _slates)
        {
            slate.Release();
        }

        _slates.Clear();
        while (_emitted.Reader.TryRead(out PartSlate? unread))
        {
            unread.Release();
        }
    }

    /// <summary>The keys of groups [<paramref name="from"/>, <paramref name="to"/>) written into the applier's batch, handed on each time it fills.</summary>
    private void Copy(Emission emission, GroupKeys keys, int from, int to)
    {
        AggregationOutcome outcome = new AggregationOutcome(_query.Plan, [], keys, []);
        while (from < to)
        {
            PartSlate slate = emission.Slate ??= Begun();
            int count = Math.Min(to - from, _batchRows - slate.Rows);
            Scratch.Grow(ref emission.Groups, count);
            for (int i = 0; i < count; i++)
            {
                emission.Groups[i] = from + i;
            }

            slate.Append(emission.Columns, outcome, emission.Groups.AsSpan(0, count));
            from += count;
            if (slate.Rows == _batchRows)
            {
                Publish(emission);
            }
        }
    }

    /// <summary>The applier's batch, if it holds a value, built and handed on.</summary>
    private void Publish(Emission emission)
    {
        if (emission.Slate is not { Rows: > 0 } slate)
        {
            return;
        }

        emission.Slate = null;
        slate.Seal();
        if (!_emitted.Writer.TryWrite(slate))
        {
            Give(slate);
        }
    }

    /// <summary>A batch of the pool, or a new one, emptied.</summary>
    private PartSlate Begun()
    {
        PartSlate? slate;
        lock (_gate)
        {
            _slates.TryPop(out slate);
        }

        if (slate is null)
        {
            VortexSessionOptions options = _query.Session.Options;
            slate = new PartSlate((StructStore)ColumnStores.Create(_dtype, options.EnginePool, options.Extensions), new CanonicalArena(64, options.EnginePool));
        }

        slate.Begin();
        return slate;
    }

    /// <summary>What an applier writes with: its own columns, the batch it fills, and the groups of a copy.</summary>
    private sealed class Emission(ResultColumn[] columns)
    {
        internal ResultColumn[] Columns { get; } = columns;

        internal PartSlate? Slate;

        internal int[] Groups = [];
    }
}
