using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;

using Vorticity.Arrays;
using Vorticity.Columns;
using Vorticity.Compute;
using Vorticity.Expressions;
using Vorticity.File;
using Vorticity.IO;
using Vorticity.Layouts;
using Vorticity.Types;

namespace Vorticity.Scanning;

/// <summary>The batches of one compiled scan.</summary>
/// <remarks>
/// <para>
/// Re-enumerable: every <see cref="GetAsyncEnumerator(System.Threading.CancellationToken)"/> call builds a fresh enumerator with its
/// own <see cref="ScanContext"/>, so two enumerations may run concurrently over one open file --
/// the file is thread-safe, a scan is not.
/// </para>
/// <para>
/// A batch is built in two phases over a single read: the split is planned and every segment it
/// needs is registered, one <c>ReadManyAsync</c> is awaited, then the decode runs synchronously,
/// which is why no decoder takes a <c>ValueTask</c>. Coalescing only works when the whole
/// split is registered before that read, so a layout reader that read a segment itself, or
/// registered one during execution, would double the read count without failing anything.
/// </para>
/// </remarks>
internal sealed class BatchAsyncEnumerable : IAsyncEnumerable<RecordBatch>
{
    private readonly VortexFile _file;
    private readonly LayoutTree _tree;
    private readonly Projection _read;
    private readonly Projection _keep;
    private readonly SplitPlan _plan;
    private readonly int _degree;
    private readonly bool _reverse;
    private readonly VortexExpr? _filter;
    private readonly RowSelection? _take;
    private readonly ScanMetrics? _metrics;
    private readonly DType _schema;

    /// <param name="file">The open file.</param>
    /// <param name="tree">Its parsed layout tree.</param>
    /// <param name="read">What the scan decodes: the projection unioned with the filter's columns.</param>
    /// <param name="keep">What the caller projected, and therefore what a batch carries.</param>
    /// <param name="plan">The split plan, computed under <paramref name="read"/>.</param>
    /// <param name="degree">How many splits may decode concurrently.</param>
    /// <param name="filter">The predicate, or null.</param>
    /// <param name="take">The row index list, or null.</param>
    /// <param name="metrics">The caller's sink for what the scan does, or null.</param>
    /// <param name="reverse">
    /// Whether to deliver the plan's splits last one first, for a descending walk. It asks for
    /// <paramref name="degree"/> one, because the order is the point and lanes do not keep one.
    /// </param>
    internal BatchAsyncEnumerable(
        VortexFile file,
        LayoutTree tree,
        Projection read,
        Projection keep,
        SplitPlan plan,
        int degree,
        VortexExpr? filter,
        RowSelection? take,
        ScanMetrics? metrics,
        bool reverse = false)
    {
        if (reverse && degree != 1)
        {
            throw new ArgumentOutOfRangeException(nameof(degree), degree, "a reversed walk reads one split at a time");
        }

        _file = file;
        _take = take;
        _metrics = metrics;
        _tree = tree;
        _read = read;
        _keep = keep;
        _plan = plan;
        _degree = degree;
        _filter = filter;
        _reverse = reverse;

        // Computed once, here, and into an arena of this enumerable's own. Deriving it lazily from
        // the LayoutTree's arena would mutate a structure the tree promises is immutable and
        // therefore safe for concurrent scans, and two threads reading Schema would race on its
        // arrays.
        //
        // It is the keep projection's schema, not the read projection's: a filter's own columns are
        // dropped before the batch is produced, so they were never part of what a caller was
        // promised.
        _schema = keep.IsAll
            ? tree.Root.DType
            : keep.ProjectedSchema(tree.Root.DType, new DTypeArena());
    }

    /// <summary>The schema every batch of this scan carries.</summary>
    /// <remarks>
    /// It is what <see cref="RecordBatch.DType"/> reports for every batch, and the two are
    /// asserted equal by the scan's tests: the batch's schema is what the layout reader built, and
    /// this is what the caller was promised.
    /// </remarks>
    public DType Schema => _schema;

    /// <summary>The compiled projection this scan runs under: what a batch carries.</summary>
    public Projection Projection => _keep;

    /// <summary>
    /// What the scan actually decodes. Equal to <see cref="Projection"/> unless a filter reads
    /// columns the caller did not project.
    /// </summary>
    public Projection ReadProjection => _read;

    /// <summary>Starts a scan.</summary>
    /// <param name="cancellationToken">Cancels at batch boundaries.</param>
    /// <returns>A fresh enumerator, with its own arenas.</returns>
    /// <remarks>
    /// An <c>[EnumeratorCancellation]</c> attribute is rejected on this parameter: it only has
    /// meaning on an async-iterator method, and this enumerator is hand-written precisely so that
    /// it is not one. The token is honoured directly, so <c>WithCancellation</c> behaves
    /// identically.
    /// </remarks>
    public IAsyncEnumerator<RecordBatch> GetAsyncEnumerator(CancellationToken cancellationToken = default) =>
        new BatchAsyncEnumerator(
            _file, _tree, _read, _keep, _schema, _plan, Lanes, _filter, _take, live: null, _metrics,
            cancellationToken, reverse: _reverse, compact: Compact, keepEncodings: KeepEncodings, sinkDecodes: SinkDecodes, reuseBatches: ReuseBatches);

    /// <summary>Batches decoded ahead of the consumer, on lanes of their own.</summary>
    internal int Prefetch { get; init; }

    /// <summary>Whether a filtered batch is compacted to its surviving rows rather than delivered whole with a selection.</summary>
    internal bool Compact { get; init; } = true;

    /// <summary>Whether a dictionary or run-end column reaches the consumer in its encoded form.</summary>
    internal bool KeepEncodings { get; init; }

    /// <summary>
    /// Whether the consumer reads the encoded forms itself, so that a block counts as decoded only
    /// when a column of it reached canonical form, rather than whenever it is delivered.
    /// </summary>
    internal bool SinkDecodes { get; init; }

    /// <summary>
    /// Whether each step binds its batch into the object the previous step disposed rather than
    /// allocating one: for a consumer that keeps no reference to a batch past the next step.
    /// </summary>
    internal bool ReuseBatches { get; init; }

    /// <summary>The lanes the scan runs on: the degree, widened by the read-ahead.</summary>
    private int Lanes => _reverse ? 1 : Math.Max(_degree, Prefetch > 0 ? Prefetch + 1 : 1);

    /// <summary>Starts a scan that reads only the splits <paramref name="live"/> keeps.</summary>
    /// <param name="live">The mask of live blocks the pruning pass refined, or null for every block.</param>
    /// <param name="cancellationToken">Cancels at batch boundaries.</param>
    internal IAsyncEnumerator<RecordBatch> GetAsyncEnumerator(
        BlockMask? live, CancellationToken cancellationToken) =>
        new BatchAsyncEnumerator(
            _file, _tree, _read, _keep, _schema, _plan, Lanes, _filter, _take, live, _metrics,
            cancellationToken, reverse: _reverse, compact: Compact, keepEncodings: KeepEncodings, sinkDecodes: SinkDecodes, reuseBatches: ReuseBatches);

    /// <summary>
    /// Starts a scan whose filter an exact index has already answered: it reads exactly the rows
    /// of <paramref name="proven"/> and evaluates nothing.
    /// </summary>
    /// <param name="live">The mask of live blocks, or null.</param>
    /// <param name="proven">The rows the filter selects.</param>
    /// <param name="cancellationToken">Cancels at batch boundaries.</param>
    internal IAsyncEnumerator<RecordBatch> GetAsyncEnumerator(
        BlockMask? live, RowSelection proven, CancellationToken cancellationToken) =>
        new BatchAsyncEnumerator(
            _file, _tree, _read, _keep, _schema, _plan, Lanes, _filter, proven, live, _metrics,
            cancellationToken, filterProven: true, reverse: _reverse, keepEncodings: KeepEncodings, sinkDecodes: SinkDecodes, reuseBatches: ReuseBatches);

    /// <summary>Whether the scan already has a take of the caller's.</summary>
    internal bool HasTake => _take is not null;

    /// <summary>The file this scan reads, for the pruning pass that runs before the first batch.</summary>
    internal VortexFile File => _file;

    /// <summary>The layout tree the pruning pass walks.</summary>
    internal LayoutTree Tree => _tree;

    /// <summary>The caller's metrics sink, for the pruning pass to add its own reads to.</summary>
    internal ScanMetrics? Metrics => _metrics;

    /// <summary>The split plan, which a key-ordered scan asks for the split of a row.</summary>
    internal SplitPlan Plan => _plan;

    /// <summary>How many splits may decode concurrently.</summary>
    internal int Degree => _degree;
}

/// <summary>The enumerator behind one scan, written out rather than as a <c>yield return</c>.</summary>
/// <remarks>
/// <para>
/// <b>One consumer.</b> <see cref="IAsyncEnumerator{T}"/> requires it and this type does not defend
/// against more.
/// </para>
/// <para>
/// <b><see cref="Current"/> is invalidated by the next <see cref="MoveNextAsync"/></b>, which
/// disposes the previous batch on the caller's behalf so the arenas can be reused. Every span
/// borrowed from a batch dies with it. A caller who needs two batches alive at once must copy.
/// </para>
/// <para>
/// <b>Why it is written out.</b> A compiler-generated async iterator allocates its state machine
/// and can allocate again per <c>MoveNextAsync</c>; one allocation per scan is acceptable, one per
/// batch is not. So <see cref="MoveNextAsync"/> plans the split and issues the read itself and
/// hands the await to a single helper. The one allocation left per batch is the
/// <see cref="RecordBatch"/>, and a scan whose consumer keeps no reference to a batch past the next
/// step binds each into the object the previous step disposed, which leaves none; in a sequential
/// scan nothing else allocates in steady state. A degree above one puts the decode on the thread
/// pool, where that claim covers only what the caller's thread pays, not what the scan costs across
/// every thread.
/// </para>
/// </remarks>
internal sealed class BatchAsyncEnumerator : IAsyncEnumerator<RecordBatch>
{
    private readonly LayoutTree _tree;
    private readonly ISegmentReader _source;
    private readonly FieldMask _mask;
    private readonly FieldMask _keep;
    private readonly DType _schema;
    /// <summary>
    /// The filter and what is worth preparing once for it, or null when there is none. It stands in
    /// for the expression itself so that a scan without a filter carries no extra field: these
    /// paths are held to a ceiling in bytes and one reference each would show.
    /// </summary>
    private readonly FilterEvaluator? _evaluator;
    private readonly bool _filterProven;
    private readonly bool _compact;
    private readonly RowSelection? _take;
    private readonly BlockMask? _live;
    private readonly ScanMetrics? _metrics;
    private readonly int _maxBatchRows;
    private readonly CancellationToken _token;
    private readonly Lane[] _lanes;
    private readonly ScanSegments _segments;
    private readonly RetainedChunks _retained;
    private readonly bool _sinkDecodes;
    private readonly bool _reuseBatches;

    private SplitCursor _cursor;
    private BlockTally _decodedBlocks;
    private BlockTally _prunedBlocks;

    // The batch of the last step, which is current while `_held`. Once released it is dropped, or,
    // when the scan reuses batches, kept for the next step to bind again: a flag rather than a
    // second reference, because the scan paths hold ceilings in bytes per enumerator.
    private RecordBatch? _current;
    private bool _held;
    private Lane? _currentLane;
    private RowRange _pending;
    private long _started;
    private long _delivered;
    private bool _drained;
    private bool _disposed;

    internal BatchAsyncEnumerator(
        VortexFile file,
        LayoutTree tree,
        Projection read,
        Projection keep,
        DType schema,
        SplitPlan plan,
        int degree,
        VortexExpr? filter,
        RowSelection? take,
        BlockMask? live,
        ScanMetrics? metrics,
        CancellationToken cancellationToken,
        bool filterProven = false,
        bool reverse = false,
        bool compact = true,
        bool keepEncodings = false,
        bool sinkDecodes = false,
        bool reuseBatches = false)
    {
        _compact = compact;
        _filterProven = filterProven;
        _sinkDecodes = sinkDecodes;
        _reuseBatches = reuseBatches;
        _tree = tree;
        _take = take;
        _live = live;
        _metrics = metrics;
        _source = file.Segments;
        _mask = read.RootMask;
        _keep = keep.RootMask;
        _schema = schema;
        _evaluator = filter is null ? null : new FilterEvaluator(filter);
        _maxBatchRows = (int)Math.Min(plan.MaxRows, int.MaxValue);
        _token = cancellationToken;
        _cursor = plan.CreateCursor(reverse);
        _segments = new ScanSegments(degree);

        // Like the segments, the chunks decoded are the scan's and not a lane's: the lanes take
        // consecutive splits in turn, and a chunk of several splits would otherwise be decoded once
        // per lane that meets it.
        _retained = new RetainedChunks(_mask.IsAll ? tree.Root.ChildCount : _mask.NamedFieldCount, degree);

        // The blocks the plan counts, so that what is decoded and pruned is counted in its units.
        long blockRows = live?.BlockRows ?? SplitPlan.NaturalBatchRows(tree);
        _decodedBlocks = new BlockTally(blockRows);
        _prunedBlocks = new BlockTally(blockRows);

        _lanes = new Lane[degree];
        for (int i = 0; i < degree; i++)
        {
            _lanes[i] = new Lane(new ScanContext(file));
            // The mask, the metrics sink and the encoded delivery outlive every batch of the scan,
            // so they are set once here and never by `ResetBatch`; the readers read the mask in
            // file coordinates and add what they materialize to the sink.
            _lanes[i].Context.LiveBlocks = live;
            _lanes[i].Context.Metrics = metrics;
            _lanes[i].Context.KeepEncodings = keepEncodings;
            _lanes[i].Context.ShareRetained(_retained);
        }
    }

    /// <summary>
    /// The batch produced by the last <see cref="MoveNextAsync"/>. <b>Invalid once
    /// <see cref="MoveNextAsync"/> is called again</b>: the scan disposes it for you and reuses its
    /// arenas.
    /// </summary>
    /// <exception cref="InvalidOperationException">There is no current batch.</exception>
    public RecordBatch Current => _held ? _current! : ScanThrow.NoCurrentBatch<RecordBatch>();

    /// <summary>The request the current batch was produced for.</summary>
    /// <remarks>Exposed for diagnostics and for the scan's own tests; it changes with every batch.</remarks>
    public ScanRequest CurrentRequest => new ScanRequest(_pending, _mask, _maxBatchRows);

    /// <summary>Produces the next batch.</summary>
    /// <returns><see langword="true"/> when <see cref="Current"/> holds a new batch.</returns>
    /// <exception cref="ObjectDisposedException">The enumerator has been disposed.</exception>
    /// <exception cref="OperationCanceledException">The scan's token was cancelled.</exception>
    /// <exception cref="VortexFormatException">The file is malformed.</exception>
    /// <exception cref="VortexUnsupportedException">A projected column needs a component this build does not read.</exception>
    public ValueTask<bool> MoveNextAsync()
    {
        if (_disposed)
        {
            ScanThrow.Disposed(nameof(BatchAsyncEnumerator));
        }

        if (_lanes.Length > 1)
        {
            return MoveNextPipelinedAsync();
        }

        ReleaseCurrent();

        if (_token.IsCancellationRequested)
        {
            return ValueTask.FromCanceled<bool>(_token);
        }

        if (_drained || !TryNextSplit(out RowRange split))
        {
            _drained = true;
            return new ValueTask<bool>(false);
        }

        Lane lane = _lanes[0];
        _pending = split;
        _currentLane = lane;
        lane.Sequence = _started++;
        lane.Context.Batch = lane.Sequence;

        bool read;
        try
        {
            // Phase 1: register, and fill what the scan already holds. No I/O, no decoding, no
            // allocation. Alone on its lane, the batch never waits for another's read.
            Register(lane.Context, split);
            _segments.Claim(lane.Context.Segments, lane.Sequence, waiter: null);
            read = NoteRequests(lane.Context);
        }
        catch
        {
            _segments.Abandon(lane.Sequence);
            lane.Context.ResetBatch();
            throw;
        }

        return ReadAndCompleteAsync(lane, read);
    }

    /// <summary>Issues and awaits the batch's one read, then builds the batch from what it brought.</summary>
    /// <param name="lane">The lane the batch is being built in.</param>
    /// <param name="read">Whether the source has anything to read: nothing, when the scan held every segment.</param>
    /// <returns><see langword="true"/> when a batch was produced.</returns>
    /// <remarks>
    /// <para>
    /// An <c>async ValueTask&lt;bool&gt;</c> that never suspends allocates nothing. The state
    /// machine is a struct on the stack and <c>AsyncValueTaskMethodBuilder</c> boxes it only at the
    /// first await that actually yields, so the memory-mapped path -- where
    /// <c>ReadManyAsync</c> has already completed by the time this is entered -- runs to
    /// <c>CompleteBatch</c> without a single allocation, which is what the per-batch ratchet holds.
    /// </para>
    /// <para>
    /// A hand-rolled <see cref="IValueTaskSource{TResult}"/> would buy nothing the compiler does
    /// not already do on that path, and it would cost the one thing a reader of an async method
    /// can otherwise rely on: that every suspension and every resumption is visible as an
    /// <c>await</c>.
    /// </para>
    /// </remarks>
    private async ValueTask<bool> ReadAndCompleteAsync(Lane lane, bool read)
    {
        try
        {
            // Phase 2: exactly one coalesced read per batch, minus whatever the scan already holds
            // -- often all of it, because a segment spans every block of its chunk, and then the
            // source is not called at all. Issued here rather than by the caller so that it is
            // awaited where it is created: a ValueTask handed across a method boundary to be
            // awaited later is one an exception path can drop unawaited.
            await ReadAsync(lane.Context.Segments, read).ConfigureAwait(false);
            _segments.Publish(lane.Context.Segments, lane.Sequence);
        }
        catch
        {
            _segments.Abandon(lane.Sequence);
            // Both ways a read can fail land here -- throwing inline, which a memory-mapped source
            // does, and completing faulted, which every async one does -- and CompleteBatch resets
            // in its own catch. Without this one the failed split's registrations stay
            // in the SegmentRequestSet (the source's AbandonPending releases the owners but leaves
            // the set registered and ready to retry, per ISegmentSource), so the next batch
            // registers on top of them and issues one coalesced read covering a superset of the
            // split it is actually reading.
            try
            {
                lane.Context.ResetBatch();
            }
            catch
            {
                // The I/O failure is what the caller needs to see, not a release failure behind it.
            }

            throw;
        }

        ScanMetrics.Served(_metrics, lane.Context.Segments);
        _segments.Release(lane.Sequence);
        bool produced = CompleteBatch();

        // Once the split is decoded: what it did not borrow, no later split will.
        _retained.Release(lane.Sequence);
        return produced;
    }

    /// <summary>Releases everything, whether the enumeration finished, threw, or was abandoned.</summary>
    /// <returns>A task that completes once every in-flight split has been observed and released.</returns>
    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return default;
        }

        _disposed = true;
        _drained = true;

        if (HasPendingWork())
        {
            return new ValueTask(DisposePipelinedAsync());
        }

        ReleaseCurrent();
        DisposeLanes();
        return default;
    }

    // ------------------------------------------------------------------------------ the two phases

    /// <summary>
    /// Advances to the next split the mask of live blocks does not rule out.
    /// </summary>
    /// <remarks>
    /// The prune happens here, before RegisterSegments, which is the whole point: a split the mask
    /// excludes costs no segment registration and therefore no bytes read. Skipping is a
    /// synchronous loop over the cursor because the mask answers from memory: it was refined once,
    /// before the first batch, by every structure the file carries, so a split asks it a single
    /// question rather than asking each pruner in turn.
    /// </remarks>
    private bool TryNextSplit(out RowRange split)
    {
        while (_cursor.TryNext(out split))
        {
            // A take's own skip comes first: it is a binary search over the index list, while the
            // mask reads the bit or two the split overlaps. A caller's take never held the rows it
            // skips; rows an exact index proved outside the filter are pruned by that index.
            if (_take is not null && !_take.Touches(split))
            {
                if (_filterProven)
                {
                    NotePruned(split);
                }

                continue;
            }

            // The dead blocks of a split that is read are pruned too: their rows are decoded only
            // because the split runs into them.
            NotePruned(split);
            if (_live is null || _live.AnyLive(split))
            {
                return true;
            }
        }

        return false;
    }

    private void NotePruned(RowRange split)
    {
        if (_metrics is not null)
        {
            _metrics.AddBlocksPruned(_prunedBlocks.Add(split, Wanted, _live, Proven, alive: false));
        }
    }

    /// <summary>The rows a caller's take asks for, or null; not the rows an exact index proved.</summary>
    private RowSelection? Wanted => _filterProven ? null : _take;

    /// <summary>The rows an exact index proved the filter selects, or null.</summary>
    private RowSelection? Proven => _filterProven ? _take : null;

    private void Register(ScanContext context, RowRange rows) =>
        SplitExecution.Register(context, _tree, in _mask, rows);

    /// <summary>
    /// Adds what the batch is about to ask of the source to the scan's sink: the segments it
    /// registered that the scan does not hold, and their bytes, counted at the asking -- a session
    /// cache may serve some without a read.
    /// </summary>
    /// <returns>Whether there is anything to ask.</returns>
    private bool NoteRequests(ScanContext context) => ScanMetrics.Note(_metrics, context.Segments);

    /// <summary>Reads what the scan does not hold, or completes the set when it holds everything.</summary>
    private ValueTask ReadAsync(SegmentRequestSet segments, bool read)
    {
        if (read)
        {
            return _source.ReadManyAsync(segments, _token);
        }

        segments.Complete();
        return default;
    }

    private bool CompleteBatch()
    {
        Lane lane = _currentLane!;
        try
        {
            // Cancellation is honoured before the read and after it, never inside a decode kernel:
            // the granularity of a scan's cancellation is the batch.
            _token.ThrowIfCancellationRequested();
            int root;
            if (!_compact && !_filterProven)
            {
                root = ExecuteSelected(lane, _pending);
            }
            else
            {
                root = ExecuteWithTake(lane.Context, _pending, out bool proven);
                lane.ReadRoot = root;
                root = ApplyFilter(lane, root, proven);
            }

            RecordBatch batch = Deliver(lane, root, _pending.Start);
            batch.Select(lane.Selection, lane.Selected);
            _metrics?.AddBatch(batch.SelectedRows);
            return true;
        }
        catch
        {
            lane.Context.ResetBatch();
            throw;
        }
    }

    /// <summary>
    /// Executes the split, pushing the take's rows down the layout tree rather than gathering them
    /// back out afterwards -- <see cref="SplitExecution.Execute"/>, the one place that does it,
    /// shared with the scan's terminals so that a count and a batch cannot disagree on which rows
    /// a split yields.
    /// </summary>
    /// <param name="context">The lane's context.</param>
    /// <param name="split">The rows this batch covers.</param>
    /// <param name="proven">
    /// Set when the split was executed over rows an encoding already selected, so the filter has
    /// nothing left to decide.
    /// </param>
    private int ExecuteWithTake(ScanContext context, RowRange split, out bool proven)
    {
        proven = false;
        ComparisonExpr? pushable = _push == PushDeclined ? null : Pushable();

        return pushable is null
            ? SplitExecution.Execute(context, _tree, in _mask, split, _take)
            : ExecutePushed(context, split, pushable, out proven);
    }

    /// <summary>Nobody has asked an encoding yet.</summary>
    private const byte PushUnasked = 0;

    /// <summary>An encoding answered, so every split of this scan may ask.</summary>
    private const byte PushAnswered = 1;

    /// <summary>An encoding declined, and no later split asks again.</summary>
    private const byte PushDeclined = 2;

    /// <summary>
    /// What the predicate's column said the first time it was asked. Raced by the lanes and written
    /// with the same value by all of them, which is why it needs no lock: the answer is a property
    /// of the encoding, not of the split that happened to ask.
    /// </summary>
    private byte _push;

    /// <summary>
    /// The one comparison this scan may offer to an encoding, or null when it has none to offer.
    /// </summary>
    /// <remarks>
    /// Derived rather than stored: these paths are held to a ceiling in bytes and the test is two
    /// type checks on a reference the enumerator already holds. The conditions are narrow on
    /// purpose. A conjunction is not pushed because answering one arm does not select the rows, a
    /// take is not pushed because its own selection already owns the row space, and a dotted path
    /// is not pushed because only the struct reader resolves one and it resolves a name.
    /// </remarks>
    private ComparisonExpr? Pushable() =>
        _take is null && !_filterProven && _evaluator?.Filter is ComparisonExpr comparison &&
        comparison.Field.SegmentsUtf8.Length == 1
            ? comparison
            : null;

    /// <summary>
    /// Offers the comparison to the predicate's column, and reads the rest of the split over the
    /// rows it selected.
    /// </summary>
    /// <remarks>
    /// Two passes, and the first one reads a single column. When the encoding answers, the second
    /// pass decodes the projection over the surviving rows alone -- which is the take's own pushed
    /// selection, reused -- and the filter has nothing left to do. When it declines, the first pass
    /// has decoded that one column for nothing, once, and no split of this scan asks again.
    /// </remarks>
    private int ExecutePushed(
        ScanContext context, RowRange split, ComparisonExpr pushable, out bool proven)
    {
        proven = false;
        // Resolved against the layout's struct and not against the batch's schema, which is the
        // keep projection's: a filter-only column is absent from it, and a projected one can sit at
        // another index. A mask is a path through the file's fields, so the file's dtype is the
        // only one that names them.
        byte[] name = pushable.Field.SegmentsUtf8[0];
        DType root = _tree.Root.DType;
        int field = root.Kind == DTypeKind.Struct ? root.IndexOfField(name) : -1;
        if (field < 0 || !_mask.Includes(field))
        {
            _push = PushDeclined;
            return SplitExecution.Execute(context, _tree, in _mask, split, _take);
        }

        FieldMask only = _evaluator!.OnlyField(field);
        (byte[]? Field, ComparisonOp Op, FilterLiteral Literal) saved =
            context.ExchangePushedPredicate(name, pushable.Op, pushable.Value);
        int answer;
        try
        {
            answer = SplitExecution.Execute(context, _tree, in only, split, null);
        }
        finally
        {
            context.ExchangePushedPredicate(saved.Field, saved.Op, saved.Literal);
        }

        if (!context.PredicateAnswered)
        {
            _push = PushDeclined;
            return SplitExecution.Execute(context, _tree, in _mask, split, _take);
        }

        _push = PushAnswered;
        context.PredicateAnswered = false;

        int rows = (int)(split.End - split.Start);
        int[] selected = ArrayPool<int>.Shared.Rent(Math.Max(rows, 1));
        try
        {
            int count = Selected(context, answer, split.Start, selected);
            (int[]? Buffer, int Count) previous = context.ExchangeSelection(selected, count);
            try
            {
                proven = true;
                return SplitExecution.Execute(context, _tree, in _mask, split, null);
            }
            finally
            {
                context.ExchangeSelection(previous.Buffer, previous.Count);
            }
        }
        finally
        {
            ArrayPool<int>.Shared.Return(selected);
        }
    }

    /// <summary>Reads the rows an answered comparison selects, in the file's coordinates.</summary>
    /// <param name="context">The context whose arena holds the answer.</param>
    /// <param name="answer">The struct the first pass produced, holding one boolean column.</param>
    /// <param name="start">The split's first row, which the selection is expressed against.</param>
    /// <param name="into">Receives the selected rows.</param>
    /// <returns>How many were selected.</returns>
    private static int Selected(ScanContext context, int answer, long start, Span<int> into)
    {
        CanonicalArena arena = context.Canonical;
        CanonicalNode node = arena.GetNode(answer);
        if (node.Kind == CanonicalKind.Struct)
        {
            node = arena.GetNode(node.GetFieldIndex(0));
        }

        ReadOnlySpan<byte> bits = node.Bits.Span;
        int offset = node.BitOffset;
        Arrays.Decoders.Canonical.ValidityMask valid =
            Arrays.Decoders.Canonical.ValidityMask.From(arena, node.Validity);
        int count = 0;
        for (int row = 0; row < node.Length; row++)
        {
            int bit = offset + row;
            if ((bits[bit >> 3] & (1 << (bit & 7))) != 0 && valid.IsValid(row))
            {
                into[count++] = (int)start + row;
            }
        }

        return count;
    }

    /// <summary>
    /// Runs the filter over a decoded batch and drops both the rejected rows and the columns only
    /// the filter needed.
    /// </summary>
    /// <remarks>
    /// The rejected rows go first and the trim second. The gather is skipped entirely
    /// when every row passed -- the common case for a filter that selects a whole split -- so a
    /// non-selective filter costs one evaluation pass and no copying at all.
    /// </remarks>
    /// <param name="lane">The lane the split decoded in; it receives the selection when the scan does not compact.</param>
    /// <param name="root">The decoded split.</param>
    /// <param name="proven">
    /// Whether the rows are already the ones the filter selects, which an exact index and an
    /// encoding that answered its comparison both make true.
    /// </param>
    private int ApplyFilter(Lane lane, int root, bool proven = false)
    {
        ScanContext context = lane.Context;
        lane.Selection = default;
        lane.Selected = 0;
        if (_evaluator is null)
        {
            return root;
        }

        if (_filterProven || proven)
        {
            // Every row the take gathered is one the index proved: only the trim is left.
            return ProjectionTrim.Apply(context.Canonical, root, in _mask, in _keep, _schema);
        }

        int rows = context.Canonical.GetNode(root).Length;
        byte[] states = ArrayPool<byte>.Shared.Rent(Math.Max(rows, 1));
        int[]? selected = null;
        try
        {
            Span<byte> window = states.AsSpan(0, rows);
            _evaluator!.Evaluate(context.Canonical, root, rows, window);

            int count = Trilean.CountTrue(window);
            if (count != rows && !_compact)
            {
                // The block is delivered whole and the rows that passed are marked, not copied.
                lane.Selection = SelectionWords(context.Canonical, window, rows);
                lane.Selected = count;
            }
            else if (count != rows)
            {
                selected = ArrayPool<int>.Shared.Rent(Math.Max(count, 1));
                Span<int> indices = selected.AsSpan(0, count);
                CanonicalFilter.Select(window, indices);
                root = CanonicalFilter.Apply(context.Canonical, root, indices);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(states);
            if (selected is not null)
            {
                ArrayPool<int>.Shared.Return(selected);
            }
        }

        return ProjectionTrim.Apply(context.Canonical, root, in _mask, in _keep, _schema);
    }

    /// <summary>
    /// One split decoded whole, with the rows the take asks for and the filter keeps marked as a
    /// selection instead of gathered: the delivery of a scan that does not compact.
    /// </summary>
    private int ExecuteSelected(Lane lane, RowRange split)
    {
        ScanContext context = lane.Context;
        lane.Selection = default;
        lane.Selected = 0;
        int root = SplitExecution.Execute(context, _tree, in _mask, split, null);
        lane.ReadRoot = root;
        int rows = context.Canonical.GetNode(root).Length;
        if (_take is null && _evaluator is null)
        {
            return ProjectionTrim.Apply(context.Canonical, root, in _mask, in _keep, _schema);
        }

        byte[] states = ArrayPool<byte>.Shared.Rent(Math.Max(rows, 1));
        int[]? local = null;
        try
        {
            Span<byte> window = states.AsSpan(0, rows);
            if (_evaluator is not null)
            {
                _evaluator.Evaluate(context.Canonical, root, rows, window);
            }
            else
            {
                window.Fill(Trilean.True);
            }

            if (_take is not null)
            {
                // A row outside the take is not selected, whatever the filter says of it.
                local = ArrayPool<int>.Shared.Rent(Math.Max(rows, 1));
                int wanted = _take.LocalIndices(split, local);
                byte[] kept = ArrayPool<byte>.Shared.Rent(Math.Max(rows, 1));
                try
                {
                    Span<byte> taken = kept.AsSpan(0, rows);
                    taken.Clear();
                    for (int i = 0; i < wanted; i++)
                    {
                        taken[local[i]] = 1;
                    }

                    for (int row = 0; row < rows; row++)
                    {
                        if (taken[row] == 0)
                        {
                            window[row] = Trilean.False;
                        }
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(kept);
                }
            }

            lane.Selected = Trilean.CountTrue(window);
            lane.Selection = SelectionWords(context.Canonical, window, rows);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(states);
            if (local is not null)
            {
                ArrayPool<int>.Shared.Return(local);
            }
        }

        return ProjectionTrim.Apply(context.Canonical, root, in _mask, in _keep, _schema);
    }

    /// <summary>The true states of a filter window as 64-bit words, in the batch's arena.</summary>
    private static Buffers.VortexBuffer SelectionWords(CanonicalArena arena, ReadOnlySpan<byte> window, int rows)
    {
        int words = Math.Max((rows + 63) >> 6, 1);
        Buffers.VortexBuffer buffer = arena.Allocate(words * 8, 64, out Span<byte> raw);
        Span<ulong> bits = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ulong>(raw);
        bits.Clear();
        for (int row = 0; row < rows; row++)
        {
            if (window[row] == Trilean.True)
            {
                bits[row >> 6] |= 1UL << (row & 63);
            }
        }

        return buffer;
    }

    // -------------------------------------------------------------------------------- pipelined

    // Pooled, as the lanes' own work is: the state machine of a call that suspends is rented rather
    // than allocated, so the pipelined path allocates nothing per batch in steady state either.
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<bool> MoveNextPipelinedAsync()
    {
        ReleaseCurrent();
        _token.ThrowIfCancellationRequested();
        Pump();

        if (_delivered >= _started)
        {
            return false;
        }

        Lane lane = _lanes[(int)(_delivered % _lanes.Length)];
        _delivered++;

        int root;
        try
        {
            root = await lane.Completion.ConfigureAwait(false);
        }
        catch
        {
            lane.Running = false;
            lane.Context.ResetBatch();
            throw;
        }

        lane.Running = false;
        _pending = lane.Rows;
        _currentLane = lane;

        // Every batch before this one has taken its own reference to what it needed, and is dead;
        // a chunk none of them borrowed after an earlier one is borrowed by no later one either.
        _segments.Release(lane.Sequence);
        _retained.Release(lane.Sequence);

        try
        {
            _token.ThrowIfCancellationRequested();
            RecordBatch batch = Deliver(lane, root, lane.Rows.Start);
            batch.Select(lane.Selection, lane.Selected);
            _metrics?.AddBatch(batch.SelectedRows);
        }
        catch
        {
            lane.Context.ResetBatch();
            throw;
        }

        return true;
    }

    private void Pump()
    {
        int lanes = _lanes.Length;
        while (_started - _delivered < lanes)
        {
            if (_drained || !TryNextSplit(out RowRange split))
            {
                _drained = true;
                return;
            }

            Lane lane = _lanes[(int)(_started % lanes)];
            lane.Rows = split;
            lane.Sequence = _started;
            lane.Start(this);
            _started++;
        }
    }

    /// <summary>
    /// One split on one lane, off the caller's thread: the same three phases and the same two
    /// row-level operations as the sequential path -- the take pushed down, the filter applied --
    /// so a batch is the same batch whatever the degree. Building the batch straight from the
    /// decoded split instead would silently return every row of a filtered or taken scan.
    /// </summary>
    /// <summary>A lane's loop: one split per signal, each reported on the lane, until the lane is stopped.</summary>
    /// <remarks>
    /// Its first step waits, so the call returns to the pump at once; every later step resumes on
    /// the thread pool, where the lane's sources schedule it.
    /// </remarks>
    private async Task LaneLoopAsync(Lane lane)
    {
        while (await lane.NextAsync().ConfigureAwait(false))
        {
            lane.Taken();
            try
            {
                lane.Done(await RunLaneAsync(lane).ConfigureAwait(false));
            }
            catch (Exception error)
            {
                lane.Failed(error);
            }
        }
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<int> RunLaneAsync(Lane lane)
    {
        ScanContext context = lane.Context;
        RowRange rows = lane.Rows;
        long batch = lane.Sequence;
        context.Batch = batch;
        try
        {
            Register(context, rows);

            // A segment another lane is reading is waited for rather than read twice; once it is
            // in, the claim fills it and the batch reads only what it claimed.
            while (_segments.Claim(context.Segments, batch, lane))
            {
                await lane.ReadByOthersAsync().ConfigureAwait(false);
            }

            await ReadAsync(context.Segments, NoteRequests(context)).ConfigureAwait(false);
            _segments.Publish(context.Segments, batch);
            ScanMetrics.Served(_metrics, context.Segments);
            if (!_compact && !_filterProven)
            {
                return ExecuteSelected(lane, rows);
            }

            int root = ExecuteWithTake(context, rows, out bool proven);
            lane.ReadRoot = root;
            return ApplyFilter(lane, root, proven);
        }
        catch
        {
            _segments.Abandon(batch);
            context.ResetBatch();
            throw;
        }
    }

    private async Task DisposePipelinedAsync()
    {
        ReleaseCurrent();

        for (int i = 0; i < _lanes.Length; i++)
        {
            Lane lane = _lanes[i];
            if (!lane.Running)
            {
                continue;
            }

            try
            {
                await lane.Completion.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Disposal must not throw, and an abandoned split's failure has no one to report to.
            }

            lane.Running = false;
            lane.Context.ResetBatch();
        }

        DisposeLanes();
    }

    private bool HasPendingWork()
    {
        for (int i = 0; i < _lanes.Length; i++)
        {
            if (_lanes[i].Running)
            {
                return true;
            }
        }

        return false;
    }

    private void ReleaseCurrent()
    {
        RecordBatch? batch = _held ? _current : null;
        Lane? lane = _currentLane;
        _held = false;
        _currentLane = null;
        if (!_reuseBatches)
        {
            _current = null;
        }

        if (batch is not null && lane is not null)
        {
            NoteDecoded(batch, lane);
        }

        batch?.Dispose();
    }

    /// <summary>
    /// Makes the batch over <paramref name="root"/> current: a new one, or, for a scan that reuses
    /// batches, the one the previous step disposed, bound again, so that a step allocates nothing.
    /// </summary>
    private RecordBatch Deliver(Lane lane, int root, long startRow)
    {
        if (_current is { } spare)
        {
            spare.Rebind(lane.Context, root, startRow);
        }
        else
        {
            _current = new RecordBatch(lane.Context, root, startRow);
        }

        _held = true;
        return _current;
    }

    /// <summary>
    /// Counts the blocks of the batch being released, now that its consumer is done with it: all
    /// of them for a consumer that reads the columns; for one that reads encoded forms itself, only
    /// when the filter, the consumer or the reader left some column of it in canonical form.
    /// </summary>
    private void NoteDecoded(RecordBatch batch, Lane lane)
    {
        if (_metrics is null)
        {
            return;
        }

        if (_sinkDecodes
            && !ScanMetrics.Decoded(batch.Arena, batch.RootIndex)
            && !ScanMetrics.Decoded(batch.Arena, lane.ReadRoot))
        {
            return;
        }

        _metrics.AddBlocksDecoded(_decodedBlocks.Add(_pending, Wanted, _live, Proven));
    }

    private void DisposeLanes()
    {
        for (int i = 0; i < _lanes.Length; i++)
        {
            _lanes[i].Stop();
            _lanes[i].Context.Dispose();
        }

        // Once no lane runs: the held segments and the retained chunks carry references of their own.
        _segments.Dispose();
        _retained.Dispose();
    }

    /// <summary>One decode flow: its own <see cref="ScanContext"/>, never shared.</summary>
    /// <remarks>
    /// A lane runs one loop for the whole scan, started with its first split: the loop waits for a
    /// split on one reusable source and reports the batch on another, and both resume their waiter
    /// on the thread pool. So a split allocates nothing, where a <c>Task.Run</c> per split would
    /// allocate a task and its promise, and a split that suspends rents its state machine.
    /// </remarks>
    private sealed class Lane : SegmentWaiter, IValueTaskSource<bool>, IValueTaskSource<int>
    {
        private ManualResetValueTaskSourceCore<bool> _go = new() { RunContinuationsAsynchronously = true };
        private ManualResetValueTaskSourceCore<int> _done = new() { RunContinuationsAsynchronously = true };
        private Task? _loop;

        internal Lane(ScanContext context) => Context = context;

        internal ScanContext Context { get; }

        internal RowRange Rows { get; set; }

        /// <summary>The number of the split in <see cref="Rows"/> among the splits the scan started, in the order it walks them.</summary>
        internal long Sequence { get; set; }

        /// <summary>The split as the readers decoded it, before the filter and the trim: what says whether a block was decoded.</summary>
        internal int ReadRoot { get; set; }

        /// <summary>Whether a split was started on this lane and its result not yet taken.</summary>
        internal bool Running { get; set; }

        /// <summary>The split in flight, to be awaited once.</summary>
        internal ValueTask<int> Completion => new ValueTask<int>(this, _done.Version);

        /// <summary>Hands the split in <see cref="Rows"/> to the lane's loop, starting the loop with the first one.</summary>
        internal void Start(BatchAsyncEnumerator owner)
        {
            Running = true;
            _done.Reset();
            _loop ??= owner.LaneLoopAsync(this);
            _go.SetResult(true);
        }

        /// <summary>Ends the loop; the lane holds no split.</summary>
        internal void Stop()
        {
            if (_loop is not null && !Running)
            {
                _go.SetResult(false);
                _loop = null;
            }
        }

        /// <summary>The next split, or false once the lane is stopped.</summary>
        internal ValueTask<bool> NextAsync() => new ValueTask<bool>(this, _go.Version);

        /// <summary>Makes the lane ready for its next split; called by the loop before it reports.</summary>
        internal void Taken() => _go.Reset();

        internal void Done(int root) => _done.SetResult(root);

        internal void Failed(Exception error) => _done.SetException(error);

        bool IValueTaskSource<bool>.GetResult(short token) => _go.GetResult(token);

        ValueTaskSourceStatus IValueTaskSource<bool>.GetStatus(short token) => _go.GetStatus(token);

        void IValueTaskSource<bool>.OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags) =>
            _go.OnCompleted(continuation, state, token, flags);

        int IValueTaskSource<int>.GetResult(short token) => _done.GetResult(token);

        ValueTaskSourceStatus IValueTaskSource<int>.GetStatus(short token) => _done.GetStatus(token);

        void IValueTaskSource<int>.OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags) =>
            _done.OnCompleted(continuation, state, token, flags);

        /// <summary>The rows the filter kept, as words in the batch's arena, when the block is delivered whole.</summary>
        internal Buffers.VortexBuffer Selection { get; set; }

        internal int Selected { get; set; }
    }
}
