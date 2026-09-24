using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;

using Vorticity.Arrays;
using Vorticity.Arrays.Decoders.Canonical;
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
            _file, _tree, _read, _keep, _schema, _plan, Lanes, WindowFor(_take), _filter, _take, live: null, _metrics,
            cancellationToken, reverse: _reverse, compact: Compact, keepEncodings: KeepEncodings, sinkDecodes: SinkDecodes);

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

    /// <summary>The lanes the scan runs on: the degree, widened by the read-ahead.</summary>
    private int Lanes => _reverse ? 1 : Math.Max(_degree, Prefetch > 0 ? Prefetch + 1 : 1);

    /// <summary>
    /// The splits a scan keeps in flight, decoding or decoded and waiting for the consumer: its
    /// lanes, and for a take on several lanes <see cref="TakeWindowPerLane"/> times its degree, of
    /// which only the degree decode at once.
    /// </summary>
    /// <param name="take">The rows the scan takes, or null.</param>
    /// <remarks>
    /// Batches are delivered in row order, so a lane that finishes cannot start a split past the
    /// ones in flight until the consumer has taken the oldest of them: the slowest split in flight
    /// sets the pace, and every lane that finished before it waits. A take pays that at every split,
    /// since its splits are many and each is a zone decoded for a row or two; and its batches carry
    /// only the rows it takes, so the splits decoded ahead cost it little memory, where a scan whose
    /// batches hold a window of rows would hold that many windows more.
    /// </remarks>
    private int WindowFor(RowSelection? take) =>
        take is not null && _degree > 1 ? Math.Max(Lanes, TakeWindowPerLane * _degree) : Lanes;

    /// <summary>
    /// Splits in flight per lane of a take: enough for a lane on a core half as fast as the others
    /// to finish a split while each of them finishes two.
    /// </summary>
    private const int TakeWindowPerLane = 3;

    /// <summary>Starts a scan that reads only the splits <paramref name="live"/> keeps.</summary>
    /// <param name="live">The mask of live blocks the pruning pass refined, or null for every block.</param>
    /// <param name="cancellationToken">Cancels at batch boundaries.</param>
    /// <param name="zones">The zone maps that pass read, which prove some splits whole; or null.</param>
    internal IAsyncEnumerator<RecordBatch> GetAsyncEnumerator(
        BlockMask? live, CancellationToken cancellationToken, ZonePruner? zones = null) =>
        new BatchAsyncEnumerator(
            _file, _tree, _read, _keep, _schema, _plan, Lanes, WindowFor(_take), _filter, _take, live, _metrics,
            cancellationToken, reverse: _reverse, compact: Compact, keepEncodings: KeepEncodings, sinkDecodes: SinkDecodes,
            zones: zones, widenRows: WidenRows);

    /// <summary>
    /// The most rows a run of zones the zone maps prove whole is read in as one batch, rather than
    /// a batch per zone: nothing in such a zone is pruned, evaluated or dropped, so the zone buys
    /// the scan nothing there. The batch a scan that only reads would take, never past the window
    /// the plan puts the run in; 0 reads every zone alone.
    /// </summary>
    internal int WidenRows { get; init; }

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
            _file, _tree, _read, _keep, _schema, _plan, Lanes, WindowFor(proven), _filter, proven, live, _metrics,
            cancellationToken, filterProven: true, reverse: _reverse, keepEncodings: KeepEncodings, sinkDecodes: SinkDecodes);

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
/// disposes the previous batch on the caller's behalf so the arenas can be reused, and binds the
/// same object to the next batch. Every span borrowed from a batch dies with it; a reference to it
/// kept past the step reads the next batch, and kept past the last step throws. A caller who needs
/// two batches alive at once must copy.
/// </para>
/// <para>
/// <b>Why it is written out.</b> A compiler-generated async iterator allocates its state machine
/// and can allocate again per <c>MoveNextAsync</c>; one allocation per scan is acceptable, one per
/// batch is not. So <see cref="MoveNextAsync"/> plans the split and issues the read itself and
/// hands the await to a single helper, and each step binds its batch into the object the previous
/// step disposed: in a sequential scan nothing allocates per batch in steady state. A degree above
/// one puts the decode on the thread pool, where that claim covers only what the caller's thread
/// pays, not what the scan costs across every thread.
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
    private readonly int _widenRows;
    private readonly RowSelection? _take;
    private readonly BlockMask? _live;
    private readonly ScanMetrics? _metrics;
    private readonly CancellationToken _token;
    private readonly Lane[] _lanes;
    private readonly ScanSegments _segments;
    private readonly RetainedChunks _retained;
    private readonly bool _sinkDecodes;

    private SplitCursor _cursor;
    private BlockTally _decodedBlocks;
    private BlockTally _prunedBlocks;

    // The batch of the last step, which is current while `_held`. Once released it is kept for the
    // next step to bind again: a flag rather than a second reference, because the scan paths hold
    // ceilings in bytes per enumerator.
    private RecordBatch? _current;
    private bool _held;
    private Lane? _currentLane;
    private long _started;
    private long _delivered;

    // The decodes the scan's degree still allows, and the splits started and waiting for one, the
    // last `_queued` of them: both under the lock of `_lanes`.
    private int _free;
    private int _queued;
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
        int window,
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
        ZonePruner? zones = null,
        int widenRows = 0)
    {
        _compact = compact;
        _widenRows = widenRows > plan.MaxRows && compact && !filterProven && !reverse && take is null &&
            filter is not null && zones is not null
            ? widenRows
            : 0;
        _filterProven = filterProven;
        _sinkDecodes = sinkDecodes;
        _tree = tree;
        _take = take;
        _live = live;
        _metrics = metrics;
        _source = file.Segments;
        _mask = read.RootMask;
        _keep = keep.RootMask;
        _schema = schema;
        _evaluator = filter is null ? null : new FilterEvaluator(filter) { Zones = zones };
        _token = cancellationToken;
        _cursor = plan.CreateCursor(reverse);
        _segments = new ScanSegments(degree);

        // Before the first read, the file's reader learns whether the plan reads data, and chooses
        // how to read it.
        file.AnticipateReads(plan.ShareOf(file.RowCount, live, take));

        // Like the segments, the chunks decoded are the scan's and not a lane's: the lanes take
        // consecutive splits in turn, and a chunk of several splits would otherwise be decoded once
        // per lane that meets it.
        _retained = new RetainedChunks(_mask.IsAll ? tree.Root.ChildCount : _mask.NamedFieldCount, degree);

        // The blocks the plan counts, so that what is decoded and pruned is counted in its units.
        long blockRows = live?.BlockRows ?? SplitPlan.NaturalBatchRows(tree);
        _decodedBlocks = new BlockTally(blockRows);
        _prunedBlocks = new BlockTally(blockRows);

        _free = degree;
        _lanes = new Lane[window];
        for (int i = 0; i < window; i++)
        {
            _lanes[i] = new Lane(ScanContexts.Rent(file));
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
    public ScanRequest CurrentRequest =>
        new ScanRequest(_currentLane?.Rows ?? RowRange.Empty, _mask, (int)Math.Min(_cursor.Plan.MaxRows, int.MaxValue));

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
        lane.Rows = split;
        _currentLane = lane;
        lane.Sequence = _started++;
        lane.Context.Batch = lane.Sequence;
        SplitExecution.Window(lane.Context, _cursor.Plan, split);

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
                if (_widenRows != 0)
                {
                    Widen(ref split);
                }

                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Extends a split the zone maps prove whole over the splits after it that they prove whole
    /// too, up to <see cref="_widenRows"/> and the end of the window the plan puts it in, so the
    /// run is read as one batch.
    /// </summary>
    /// <remarks>
    /// The lanes take splits in turn, and a chunk read a zone at a time has every lane but one
    /// waiting on the one that decodes it; read as one split, the chunk is one lane's work. The
    /// window bounds the run as it bounds a scan's batch, and keeps it inside its span. Each split
    /// taken in counts as proven, the unit the scan's counters are kept in.
    /// </remarks>
    private void Widen(ref RowRange split)
    {
        if (!_evaluator!.Zones!.MustMatch(split))
        {
            return;
        }

        ZonePruner zones = _evaluator.Zones;
        _cursor.Plan.WindowOf(split.Start, out int lead, out int span);
        long end = Math.Min(split.Start - lead + span, split.Start + _widenRows);
        while (true)
        {
            SplitCursor probe = _cursor;
            if (!probe.TryNext(out RowRange next) || next.Start != split.End || next.End > end ||
                !zones.MustMatch(next))
            {
                return;
            }

            NotePruned(next);
            _metrics?.AddSplitProven();
            split = new RowRange(split.Start, next.End);
            _cursor = probe;
        }
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
                root = ExecuteSelected(lane, lane.Rows);
            }
            else
            {
                root = ExecuteFiltered(lane, lane.Rows);
            }

            RecordBatch batch = Deliver(lane, root, lane.Rows.Start);
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
    /// Executes the split and applies the filter, in two passes when the filter can be answered on
    /// its own -- by its column's encoding, or evaluated over its own columns -- and else in one, the
    /// filter then gathering the rows it keeps out of every column. The take's rows are pushed down
    /// the layout tree by <see cref="SplitExecution.Execute"/>, the one place that does it, shared
    /// with the scan's terminals so that a count and a batch cannot disagree on which rows a split
    /// yields.
    /// </summary>
    private int ExecuteFiltered(Lane lane, RowRange split)
    {
        if (_push != PushDeclined && Pushable() is { } pushable)
        {
            int answered = ExecutePushed(lane, split, pushable);
            if (answered >= 0)
            {
                return answered;
            }
        }

        if (FilterReadsAlone(out FieldMask columns))
        {
            if (!ZoneProven(split))
            {
                return ExecuteLate(lane, split, in columns);
            }

            int whole = SplitExecution.Execute(lane.Context, _tree, in _mask, split, null);
            lane.ReadRoot = whole;
            return ApplyFilter(lane, whole, proven: true);
        }

        int root = SplitExecution.Execute(lane.Context, _tree, in _mask, split, _take);
        lane.ReadRoot = root;
        return ApplyFilter(lane, root, ZoneProven(split));
    }

    /// <summary>Whether the filter is evaluated on its own columns, read in a pass of their own.</summary>
    /// <param name="columns">The filter's columns.</param>
    /// <remarks>
    /// Not while a comparison may still be answered by its column's encoding, which decodes nothing
    /// for the filter at all, nor with a take, whose rows are pushed down already.
    /// </remarks>
    private bool FilterReadsAlone(out FieldMask columns)
    {
        if (_evaluator is not { } evaluator || _take is not null || _filterProven ||
            (_push != PushDeclined && Pushable() is not null))
        {
            columns = default;
            return false;
        }

        return evaluator.TryOwnColumns(_tree.Root.DType, Shape, out columns);
    }

    /// <summary>The dtype of the batches: the file's own when the projection keeps everything.</summary>
    private DType Shape => _keep.IsAll ? _tree.Root.DType : _schema;


    /// <summary>
    /// On the lanes, has the filter's own columns read in the window the plan puts the split in, as
    /// one lane reads them: decoded once for the window's splits and kept, so the projection read
    /// split by split after it finds them there, and the first splits the lanes start together do
    /// not each decode them for a comparison the encoding then declines.
    /// </summary>
    private void FilterWindow(ScanContext context, RowRange split)
    {
        if (_lanes.Length > 1)
        {
            SplitExecution.Window(context, _cursor.Plan, split);
        }
    }

    /// <summary>On the lanes, puts the split back in a window of its own for what it reads next.</summary>
    private void ProjectionWindow(ScanContext context, RowRange split)
    {
        if (_lanes.Length > 1)
        {
            SplitExecution.Alone(context, split);
        }
    }

    /// <summary>
    /// The split in two passes: the filter's columns, whole, then the projection over the rows the
    /// filter kept, and nothing more when it kept none.
    /// </summary>
    /// <remarks>
    /// A column both passes read is decoded once when its chunk spans several batches: the first
    /// pass retains the window it decodes, and the second slices or gathers it there. A chunk read
    /// whole, or through a selection, retains nothing, and the second pass decodes the kept rows of
    /// it again: a cost bounded by the rows kept, and one only a chunk no larger than its batch
    /// pays in full, which the writer cuts only for tables so wide that a filter column is a
    /// sliver of what the projection reads.
    /// </remarks>
    private int ExecuteLate(Lane lane, RowRange split, in FieldMask columns)
    {
        ScanContext context = lane.Context;
        CanonicalArena arena = context.Canonical;

        // A filter of one column reads that column alone, not a struct of it, whose dtype would be
        // derived per scan.
        bool alone = columns.NamedFieldCount == 1;
        int first;
        FilterWindow(context, split);
        if (alone)
        {
            LayoutNode column = _tree.Root.GetChild(columns.GetNamedField(0));
            FieldMask whole = FieldMask.All;
            first = LayoutReaderTable.Require(in column).Execute(in column, split, in whole, context);
        }
        else
        {
            first = SplitExecution.Execute(context, _tree, in columns, split, null);
        }

        ProjectionWindow(context, split);

        int rows = arena.GetNode(first).Length;
        byte[] states = ArrayPool<byte>.Shared.Rent(Math.Max(rows, 1));

        // Room for every row: the selection writes a slot per row before deciding.
        int[] kept = ArrayPool<int>.Shared.Rent(Math.Max(rows, 1));
        try
        {
            Span<byte> window = states.AsSpan(0, rows);
            if (alone)
            {
                _evaluator!.EvaluateColumn(arena, first, rows, window);
            }
            else
            {
                _evaluator!.Evaluate(arena, first, rows, window);
            }

            int count = CanonicalFilter.Select(window, kept);
            return ExecuteKept(lane, split, kept, count, rows);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(states);
            ArrayPool<int>.Shared.Return(kept);
        }
    }

    /// <summary>
    /// The projection over the rows the filter kept, the filter answered already: nothing read when
    /// it kept none, the split read whole when it kept all, read whole and gathered when it kept
    /// most, and read over the kept rows alone otherwise.
    /// </summary>
    /// <param name="lane">The lane the split decodes in.</param>
    /// <param name="split">The split.</param>
    /// <param name="kept">The kept rows, in the split's space; moved into the file's when pushed down.</param>
    /// <param name="count">How many rows were kept.</param>
    /// <param name="rows">The split's rows.</param>
    /// <returns>The batch's root.</returns>
    private int ExecuteKept(Lane lane, RowRange split, int[] kept, int count, int rows)
    {
        ScanContext context = lane.Context;
        CanonicalArena arena = context.Canonical;
        lane.Selection = default;
        lane.Selected = 0;
        int root;
        if (count == 0)
        {
            root = CanonicalFill.BuildZeroed(context.Decode, Shape, 0, Validity.NonNullable);
            lane.ReadRoot = root;
            return root;
        }

        if (count == rows)
        {
            root = SplitExecution.Execute(context, _tree, in _keep, split, null);
        }
        else if (_evaluator!.KeepsMost(count, rows))
        {
            // A column read over most of its rows costs more than read whole and gathered, which
            // touches each row once in a kernel's stride.
            root = SplitExecution.Execute(context, _tree, in _keep, split, null);
            root = CanonicalFilter.Apply(arena, root, kept.AsSpan(0, count));
        }
        else
        {
            // The projection reads the selection in the root's row space, which is the file's.
            int origin = (int)split.Start;
            for (int i = 0; i < count; i++)
            {
                kept[i] += origin;
            }

            ScanContext.SavedSelection saved = context.ExchangeSelection(kept, count);
            try
            {
                root = SplitExecution.Execute(context, _tree, in _keep, split, null);
            }
            finally
            {
                context.RestoreSelection(in saved);
            }
        }

        lane.ReadRoot = root;
        return ProjectionTrim.Apply(arena, root, in _keep, in _keep, _schema);
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
    /// Offers the comparison to the predicate's column and, when its encoding answers, reads the
    /// projection over the rows it kept.
    /// </summary>
    /// <returns>The batch's root, or -1 when the encoding declined.</returns>
    /// <remarks>
    /// Two passes, and the first one reads a single column. When the encoding answers, the second
    /// is the one a filter evaluated over its own columns has, and the filter has nothing left to
    /// do. When it declines, the first pass has decoded that one column for nothing, once, and no
    /// split of this scan asks again.
    /// </remarks>
    private int ExecutePushed(Lane lane, RowRange split, ComparisonExpr pushable)
    {
        ScanContext context = lane.Context;

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
            return -1;
        }

        FieldMask only = FieldMask.Single(field);
        (byte[]? Field, ComparisonOp Op, FilterLiteral Literal) saved =
            context.ExchangePushedPredicate(name, pushable.Op, pushable.Value);
        int answer;
        FilterWindow(context, split);
        try
        {
            answer = SplitExecution.Execute(context, _tree, in only, split, null);
        }
        finally
        {
            context.ExchangePushedPredicate(saved.Field, saved.Op, saved.Literal);
            ProjectionWindow(context, split);
        }

        if (!context.PredicateAnswered)
        {
            _push = PushDeclined;
            return -1;
        }

        _push = PushAnswered;
        context.PredicateAnswered = false;

        int rows = (int)(split.End - split.Start);
        int[] kept = ArrayPool<int>.Shared.Rent(Math.Max(rows, 1));
        try
        {
            return ExecuteKept(lane, split, kept, Selected(context, answer, kept), rows);
        }
        finally
        {
            ArrayPool<int>.Shared.Return(kept);
        }
    }

    /// <summary>Reads the rows an answered comparison selects, in the split's space.</summary>
    /// <param name="context">The context whose arena holds the answer.</param>
    /// <param name="answer">The struct the first pass produced, holding one boolean column.</param>
    /// <param name="into">Receives the selected rows; room for every row of the answer.</param>
    /// <returns>How many were selected.</returns>
    /// <remarks>
    /// No branch on a row's answer: each row is written at the next slot and kept by its own bit,
    /// true and valid.
    /// </remarks>
    private static int Selected(ScanContext context, int answer, Span<int> into)
    {
        CanonicalArena arena = context.Canonical;
        CanonicalNode node = arena.GetNode(answer);
        if (node.Kind == CanonicalKind.Struct)
        {
            node = arena.GetNode(node.GetFieldIndex(0));
        }

        ReadOnlySpan<byte> bits = node.Bits.Span;
        int offset = node.BitOffset;
        int rows = node.Length;
        Span<int> slots = into[..rows];
        Arrays.Decoders.Canonical.ValidityMask valid =
            Arrays.Decoders.Canonical.ValidityMask.From(arena, node.Validity);
        if (valid.AllInvalid)
        {
            return 0;
        }

        int count = 0;
        if (valid.AllValid)
        {
            for (int row = 0; row < rows; row++)
            {
                int bit = offset + row;
                slots[count] = row;
                count += (bits[bit >> 3] >> (bit & 7)) & 1;
            }

            return count;
        }

        ReadOnlySpan<byte> validBits = valid.Bits;
        int validOffset = valid.BitOffset;
        for (int row = 0; row < rows; row++)
        {
            int bit = offset + row;
            int validBit = validOffset + row;
            slots[count] = row;
            count += (bits[bit >> 3] >> (bit & 7)) & (validBits[validBit >> 3] >> (validBit & 7)) & 1;
        }

        return count;
    }

    /// <summary>
    /// Whether the zone maps proved the filter selects every row of <paramref name="split"/>, which
    /// the scan then delivers without evaluating it.
    /// </summary>
    private bool ZoneProven(RowRange split)
    {
        if (_evaluator?.Zones is not ZonePruner zones || !zones.MustMatch(split))
        {
            return false;
        }

        _metrics?.AddSplitProven();
        return true;
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
                // Room for every row: the selection writes a slot per row before deciding.
                selected = ArrayPool<int>.Shared.Rent(rows);
                CanonicalFilter.Select(window, selected);
                root = CanonicalFilter.Apply(context.Canonical, root, selected.AsSpan(0, count));
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
        bool proven = ZoneProven(split);
        if (_take is null && (_evaluator is null || proven))
        {
            return ProjectionTrim.Apply(context.Canonical, root, in _mask, in _keep, _schema);
        }

        byte[] states = ArrayPool<byte>.Shared.Rent(Math.Max(rows, 1));
        int[]? local = null;
        try
        {
            Span<byte> window = states.AsSpan(0, rows);
            if (_evaluator is not null && !proven)
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
        int window = _lanes.Length;
        long assigned = _started;
        while (assigned - _delivered < window)
        {
            if (_drained || !TryNextSplit(out RowRange split))
            {
                _drained = true;
                break;
            }

            Lane lane = _lanes[(int)(assigned % window)];
            lane.Rows = split;
            lane.Sequence = assigned;
            lane.Assign(this);
            assigned++;
        }

        lock (_lanes)
        {
            _queued += (int)(assigned - _started);
            _started = assigned;
            while (_free > 0 && _queued > 0)
            {
                _free--;
                _lanes[(int)((_started - _queued) % window)].Go();
                _queued--;
            }
        }
    }

    /// <summary>
    /// Hands the decode a lane has finished to the next split waiting for one, in the order of the
    /// splits, or gives it back when none waits.
    /// </summary>
    private void HandOff()
    {
        lock (_lanes)
        {
            if (_queued > 0)
            {
                _lanes[(int)((_started - _queued) % _lanes.Length)].Go();
                _queued--;
            }
            else
            {
                _free++;
            }
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
            int root;
            try
            {
                root = await RunLaneAsync(lane).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                HandOff();
                lane.Failed(error);
                continue;
            }

            HandOff();
            lane.Done(root);
        }
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<int> RunLaneAsync(Lane lane)
    {
        ScanContext context = lane.Context;
        RowRange rows = lane.Rows;
        long batch = lane.Sequence;
        context.Batch = batch;

        // Each split is a window of its own on the lanes. Grouped as one lane would group them, the
        // splits of a window decode it once for all of them, and the lanes holding its other splits
        // wait for that decode: with a zone per split, fourteen lanes did the work of two. Alone, a
        // chunk whose encoding decodes a range is decoded split by split, each by its own lane, and
        // no row twice; one that decodes whole is still decoded once and shared.
        SplitExecution.Alone(context, rows);
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

            return ExecuteFiltered(lane, rows);
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

        // The splits no loop has started are withdrawn, since nothing would ever run them; the
        // lanes running one are awaited.
        lock (_lanes)
        {
            for (long split = _started - _queued; split < _started; split++)
            {
                _lanes[(int)(split % _lanes.Length)].Running = false;
            }

            _started -= _queued;
            _queued = 0;
        }

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
        if (batch is not null && lane is not null)
        {
            NoteDecoded(batch, lane);
        }

        batch?.Dispose();
    }

    /// <summary>
    /// Makes the batch over <paramref name="root"/> current: the one the previous step disposed,
    /// bound again so that a step allocates nothing, or a new one at the first step.
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

        _metrics.AddBlocksDecoded(_decodedBlocks.Add(lane.Rows, Wanted, _live, Proven));
    }

    private void DisposeLanes()
    {
        for (int i = 0; i < _lanes.Length; i++)
        {
            _lanes[i].Stop();
            ScanContexts.Return(_lanes[i].Context);
        }

        // Once no lane runs: the held segments and the retained chunks carry references of their own.
        _segments.Dispose();
        _retained.Dispose();
    }

    /// <summary>
    /// One split in flight, from the pump that gives it to the delivery of its batch: its own
    /// <see cref="ScanContext"/>, never shared.
    /// </summary>
    /// <remarks>
    /// A lane runs one loop for the whole scan, started with its first split: the loop waits for a
    /// split on one reusable source and reports the batch on another, and both resume their waiter
    /// on the thread pool. So a split allocates nothing, where a <c>Task.Run</c> per split would
    /// allocate a task and its promise, and a split that suspends rents its state machine. A lane
    /// is given its split when the scan's window has room and runs it when its degree does, which
    /// for a take, whose window is wider than its degree, may be later.
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

        /// <summary>Gives the lane the split in <see cref="Rows"/>, starting its loop with the first one.</summary>
        internal void Assign(BatchAsyncEnumerator owner)
        {
            Running = true;
            _done.Reset();
            _loop ??= owner.LaneLoopAsync(this);
        }

        /// <summary>Lets the lane's loop run the split it was given.</summary>
        internal void Go() => _go.SetResult(true);

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
