// PHASE1-CONTRACTS.md §13.3 and §13.4, and docs/03-architecture.md §§3.6-3.7.
//
// TWO PHASES, ONE READ. Per batch, in order: plan the split, REGISTER every segment it needs,
// await ONE ReadManyAsync, then Execute fully synchronously. Register and Execute are separate
// because coalescing only works when every segment of a split is registered before the read - by
// decode time there is nothing left to await, which is why no decoder takes a ValueTask.
// A layout reader that read a segment itself, or registered one during Execute, would break
// coalescing SILENTLY: the scan still works and the I/O count doubles. That is what
// Scan_OneReadManyPerBatch exists to catch.
//
// HAND-WRITTEN, NOT `yield return`. A compiler-generated async ITERATOR allocates its state machine
// and can allocate per MoveNextAsync. One allocation per SCAN is acceptable and documented; per
// BATCH is not. So MoveNextAsync is written out: it plans the split and issues the read itself, and
// hands the await to one `async ValueTask<bool>` helper.
//
// THAT HELPER IS AN ASYNC METHOD AND STILL ALLOCATES NOTHING on the path that matters, because an
// `async ValueTask<T>` boxes its state machine only at the first await that ACTUALLY SUSPENDS. A
// memory-mapped read has already completed by the time the helper is entered, so the struct stays
// on the stack and the builder returns a completed ValueTask by value.
//
// This used to be a hand-written state machine as well - IValueTaskSource<bool> over
// ManualResetValueTaskSourceCore, a stored ValueTaskAwaiter, an UnsafeOnCompleted callback, and
// GetAwaiter().GetResult() at both ends of the await. It was a faithful transcription of what the
// compiler emits, it allocated 120 BYTES PER SCAN MORE than the compiler's version (the core and
// its callback delegate), and it hid every suspension point from anyone reading the class.
//
// The one unavoidable per-batch allocation is the RecordBatch itself: §12.1 makes it a sealed class
// with readonly fields, so it cannot be recycled. Nothing else allocates in steady state, and
// ZeroAllocationsPerBatch asserts exactly that bound rather than a round number.
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

namespace Vorticity.Scan;

/// <summary>The batches of one compiled scan.</summary>
/// <remarks>
/// Re-enumerable: every <see cref="GetAsyncEnumerator(System.Threading.CancellationToken)"/> call builds a fresh enumerator with its
/// own <see cref="ScanContext"/>, so two enumerations may run concurrently over one open file
/// (docs/09-contracts.md §1: the file is thread-safe, a scan is not).
/// </remarks>
public sealed class BatchAsyncEnumerable : IAsyncEnumerable<RecordBatch>
{
    private readonly VortexFile _file;
    private readonly LayoutTree _tree;
    private readonly Projection _read;
    private readonly Projection _keep;
    private readonly SplitPlan _plan;
    private readonly int _degree;
    private readonly VortexExpr? _filter;
    private readonly RowSelection? _take;
    private readonly DType _schema;

    /// <param name="file">The open file.</param>
    /// <param name="tree">Its parsed layout tree.</param>
    /// <param name="read">What the scan decodes: the projection unioned with the filter's columns.</param>
    /// <param name="keep">What the caller projected, and therefore what a batch carries.</param>
    /// <param name="plan">The split plan, computed under <paramref name="read"/>.</param>
    /// <param name="degree">How many splits may decode concurrently.</param>
    /// <param name="filter">The predicate, or null.</param>
    /// <param name="take">The row index list, or null.</param>
    internal BatchAsyncEnumerable(
        VortexFile file,
        LayoutTree tree,
        Projection read,
        Projection keep,
        SplitPlan plan,
        int degree,
        VortexExpr? filter,
        RowSelection? take)
    {
        _file = file;
        _take = take;
        _tree = tree;
        _read = read;
        _keep = keep;
        _plan = plan;
        _degree = degree;
        _filter = filter;

        // Computed once, here, and into an arena of this enumerable's own. Deriving it lazily from
        // the LayoutTree's arena would mutate a structure the tree promises is immutable and
        // therefore safe for concurrent scans (docs/09-contracts.md §1), and two threads reading
        // Schema would race on its arrays.
        //
        // It is the KEEP projection's schema, not the read projection's: a filter's own columns are
        // dropped before the batch is produced, so they were never part of what a caller was
        // promised (docs/03-architecture.md §3.4).
        _schema = keep.IsAll
            ? tree.Root.DType
            : keep.ProjectedSchema(tree.Root.DType, new DTypeArena());
    }

    /// <summary>The schema every batch of this scan carries.</summary>
    /// <remarks>
    /// It is what <see cref="RecordBatch.Schema"/> reports for every batch, and the two are
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
    /// <param name="cancellationToken">Cancels at batch boundaries (contract §13.4).</param>
    /// <returns>A fresh enumerator, with its own arenas.</returns>
    /// <remarks>
    /// Contract §13.4 spells this parameter <c>[EnumeratorCancellation]</c>. The attribute is
    /// rejected here (CS8424, an error under TreatWarningsAsErrors): it only has meaning on an
    /// async-iterator method, and this enumerator is hand-written precisely so that it is not one.
    /// The token is honoured directly, so <c>WithCancellation</c> behaves identically.
    /// </remarks>
    public IAsyncEnumerator<RecordBatch> GetAsyncEnumerator(CancellationToken cancellationToken = default) =>
        new BatchAsyncEnumerator(
            _file, _tree, _read, _keep, _schema, _plan, _degree, _filter, _take, live: null,
            cancellationToken);

    /// <summary>Starts a scan that reads only the splits <paramref name="live"/> keeps.</summary>
    /// <param name="live">The mask of live blocks the pruning pass refined, or null for every block.</param>
    /// <param name="cancellationToken">Cancels at batch boundaries.</param>
    internal IAsyncEnumerator<RecordBatch> GetAsyncEnumerator(
        BlockMask? live, CancellationToken cancellationToken) =>
        new BatchAsyncEnumerator(
            _file, _tree, _read, _keep, _schema, _plan, _degree, _filter, _take, live,
            cancellationToken);

    /// <summary>The file this scan reads, for the pruning pass that runs before the first batch.</summary>
    internal VortexFile File => _file;

    /// <summary>The layout tree the pruning pass walks.</summary>
    internal LayoutTree Tree => _tree;
}

/// <summary>The hand-written enumerator of docs/03-architecture.md §3.7.</summary>
/// <remarks>
/// <para>
/// <b>One consumer.</b> <see cref="IAsyncEnumerator{T}"/> requires it and this type does not defend
/// against more.
/// </para>
/// <para>
/// <b><see cref="Current"/> is invalidated by the next <see cref="MoveNextAsync"/></b>, which
/// disposes the previous batch on the caller's behalf so the arenas can be reused. Every span
/// borrowed from a batch dies with it (docs/07-dotnet-mapping.md §4). A caller who needs two
/// batches alive at once must copy.
/// </para>
/// </remarks>
public sealed class BatchAsyncEnumerator : IAsyncEnumerator<RecordBatch>
{
    private readonly LayoutTree _tree;
    private readonly ISegmentSource _source;
    private readonly FieldMask _mask;
    private readonly FieldMask _keep;
    private readonly DType _schema;
    private readonly VortexExpr? _filter;
    private readonly RowSelection? _take;
    private readonly BlockMask? _live;
    private readonly int _maxBatchRows;
    private readonly CancellationToken _token;
    private readonly Lane[] _lanes;

    private SplitCursor _cursor;

    private RecordBatch? _current;
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
        CancellationToken cancellationToken)
    {
        _tree = tree;
        _take = take;
        _live = live;
        _source = file.Segments;
        _mask = read.RootMask;
        _keep = keep.RootMask;
        _schema = schema;
        _filter = filter;
        _maxBatchRows = (int)Math.Min(plan.MaxRows, int.MaxValue);
        _token = cancellationToken;
        _cursor = plan.CreateCursor();

        _lanes = new Lane[degree];
        for (int i = 0; i < degree; i++)
        {
            _lanes[i] = new Lane(new ScanContext(file));
        }

        // Allocated once per scan, so the awaiter's continuation costs nothing per batch.
    }

    /// <summary>
    /// The batch produced by the last <see cref="MoveNextAsync"/>. <b>Invalid once
    /// <see cref="MoveNextAsync"/> is called again</b>: the scan disposes it for you and reuses its
    /// arenas.
    /// </summary>
    /// <exception cref="InvalidOperationException">There is no current batch.</exception>
    public RecordBatch Current => _current ?? ScanThrow.NoCurrentBatch<RecordBatch>();

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
            return new ValueTask<bool>(MoveNextPipelinedAsync());
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

        ValueTask read;
        try
        {
            // Phase 1: register. No I/O, no decoding, no allocation.
            Register(lane.Context, split);

            // Phase 2: exactly one coalesced read per batch.
#pragma warning disable CA2012 // awaited by ReadAndCompleteAsync, on the next line, exactly once
            read = _source.ReadManyAsync(lane.Context.Segments, _token);
#pragma warning restore CA2012
        }
        catch
        {
            lane.Context.ResetBatch();
            throw;
        }

        return ReadAndCompleteAsync(read, lane);
    }

    /// <summary>Awaits the batch's one read, then builds the batch from what it brought.</summary>
    /// <param name="read">The read issued by <see cref="MoveNextAsync"/>, not yet awaited.</param>
    /// <param name="lane">The lane the batch is being built in.</param>
    /// <returns><see langword="true"/> when a batch was produced.</returns>
    /// <remarks>
    /// <para>
    /// AN <c>async ValueTask&lt;bool&gt;</c> THAT NEVER SUSPENDS ALLOCATES NOTHING. The state
    /// machine is a struct on the stack and <c>AsyncValueTaskMethodBuilder</c> boxes it only at the
    /// first await that actually yields, so the memory-mapped path -- where
    /// <c>ReadManyAsync</c> has already completed by the time this is entered -- runs to
    /// <c>CompleteBatch</c> without a single allocation, which is what the per-batch ratchet holds.
    /// </para>
    /// <para>
    /// This class used to be its own <see cref="IValueTaskSource{TResult}"/> to get that property:
    /// a <c>ManualResetValueTaskSourceCore</c>, a stored <c>ValueTaskAwaiter</c>, an
    /// <c>UnsafeOnCompleted</c> callback, and <c>GetAwaiter().GetResult()</c> at both ends of the
    /// hand-written await. It bought nothing the compiler does not already do on the path that
    /// matters -- and it cost the one thing a reader of an async method can otherwise rely on,
    /// which is that every suspension and every resumption is visible as an <c>await</c>.
    /// </para>
    /// </remarks>
    private async ValueTask<bool> ReadAndCompleteAsync(ValueTask read, Lane lane)
    {
        try
        {
            await read.ConfigureAwait(false);
        }
        catch
        {
            // MoveNextAsync's catch covers a read that throws INLINE and CompleteBatch resets in
            // its own; this is the third way a batch can fail -- a read whose ValueTask completes
            // faulted, which is what every async ISegmentSource does -- and it must leave the lane
            // in the same state as the other two. Without it the failed split's registrations stay
            // in the SegmentRequestSet (the source's AbandonPending releases the owners but leaves
            // the set registered and ready to retry, per ISegmentSource), so the NEXT batch
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

        return CompleteBatch();
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
    /// The prune happens HERE, before RegisterSegments, which is the whole point: a split the mask
    /// excludes costs no segment registration and therefore no bytes read. Skipping is a
    /// synchronous loop over the cursor because the mask answers from memory -- it was refined
    /// once, before the first batch, by every structure the file carries (docs/11 §6.1), and a
    /// split asks it one question where it used to ask each pruner in turn.
    /// </remarks>
    private bool TryNextSplit(out RowRange split)
    {
        while (_cursor.TryNext(out split))
        {
            // A take's own skip comes first: it is a binary search over the index list, while the
            // mask reads the bit or two the split overlaps.
            if (_take is not null && !_take.Touches(split))
            {
                continue;
            }

            if (_live is null || _live.AnyLive(split))
            {
                return true;
            }
        }

        return false;
    }

    private void Register(ScanContext context, RowRange rows)
    {
        LayoutNode root = _tree.Root;
        LayoutReaderTable.Require(in root).RegisterSegments(in root, rows, in _mask, context.Segments);
    }

    private int Execute(ScanContext context, RowRange rows)
    {
        LayoutNode root = _tree.Root;
        return LayoutReaderTable.Require(in root).Execute(in root, rows, in _mask, context);
    }

    private bool CompleteBatch()
    {
        Lane lane = _currentLane!;
        try
        {
            // Cancellation is honoured before the read and after it, never inside a decode kernel
            // (docs/03-architecture.md §1: the granularity is the batch).
            _token.ThrowIfCancellationRequested();
            int root = ExecuteWithTake(lane.Context, _pending);
            root = ApplyFilter(lane.Context, root);
            _current = new RecordBatch(lane.Context, root, _pending.Start);
            return true;
        }
        catch
        {
            lane.Context.ResetBatch();
            throw;
        }
    }

    /// <summary>
    /// Executes the split, PUSHING the take's rows down the layout tree rather than gathering them
    /// back out afterwards.
    /// </summary>
    /// <remarks>
    /// The split itself was already chosen because it holds at least one wanted row - that is the
    /// I/O half of F5 and it was always here. This is the other half: the selection travels down
    /// the layout tree in the same coordinate space as the row range beside it, so a reader that
    /// re-partitions rows re-partitions it too and one that does not passes it on by doing nothing.
    /// At the flat leaf it reaches the array decoders, where an encoding that can honour it does
    /// (`fastlanes.bitpacked` indexes positionally, `vortex.dict` takes on the codes) and every
    /// other one falls back to decoding the node and gathering - which is exactly what this method
    /// used to do for all of them.
    ///
    /// The selection is in the SPLIT's coordinate space, and `Execute` is called with the split as
    /// its row range, so the two agree at the root by construction.
    /// </remarks>
    private int ExecuteWithTake(ScanContext context, RowRange split)
    {
        if (_take is null)
        {
            return Execute(context, split);
        }

        int rows = (int)(split.End - split.Start);
        int[] indices = ArrayPool<int>.Shared.Rent(Math.Max(rows, 1));
        try
        {
            int count = _take.LocalIndices(split, indices);
            if (count == rows)
            {
                // Every row of the split is wanted: there is nothing to push down, and pushing an
                // identity selection would cost a gather for no reason.
                return Execute(context, split);
            }

            // LocalIndices produces SPLIT-relative rows; the root's row space is the file's, which
            // is what `split` is expressed in.
            for (int i = 0; i < count; i++)
            {
                indices[i] += (int)split.Start;
            }

            (int[]? Buffer, int Count) saved = context.ExchangeSelection(indices, count);
            try
            {
                return Execute(context, split);
            }
            finally
            {
                context.ExchangeSelection(saved.Buffer, saved.Count);
            }
        }
        finally
        {
            ArrayPool<int>.Shared.Return(indices);
        }
    }

    /// <summary>
    /// Runs the filter over a decoded batch and drops both the rejected rows and the columns only
    /// the filter needed.
    /// </summary>
    /// <remarks>
    /// Two steps, in the order docs/03-architecture.md §3.4 fixes. The gather is skipped entirely
    /// when every row passed -- the common case for a filter that selects a whole split -- so a
    /// non-selective filter costs one evaluation pass and no copying at all.
    /// </remarks>
    private int ApplyFilter(ScanContext context, int root)
    {
        if (_filter is null)
        {
            return root;
        }

        int rows = context.Canonical.GetNode(root).Length;
        byte[] states = ArrayPool<byte>.Shared.Rent(Math.Max(rows, 1));
        int[]? selected = null;
        try
        {
            Span<byte> window = states.AsSpan(0, rows);
            FilterEvaluator.Evaluate(_filter, context.Canonical, root, rows, window);

            int count = Trilean.CountTrue(window);
            if (count != rows)
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

    // -------------------------------------------------------------------------------- pipelined

    private async Task<bool> MoveNextPipelinedAsync()
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
            root = await lane.Work!.ConfigureAwait(false);
        }
        catch
        {
            lane.Work = null;
            lane.Context.ResetBatch();
            throw;
        }

        lane.Work = null;
        _pending = lane.Rows;
        _currentLane = lane;

        try
        {
            _token.ThrowIfCancellationRequested();
            _current = new RecordBatch(lane.Context, root, lane.Rows.Start);
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
            lane.Work = Task.Run(() => RunLaneAsync(lane), _token);
            _started++;
        }
    }

    private async Task<int> RunLaneAsync(Lane lane)
    {
        ScanContext context = lane.Context;
        RowRange rows = lane.Rows;
        try
        {
            Register(context, rows);
            await _source.ReadManyAsync(context.Segments, _token).ConfigureAwait(false);
            return Execute(context, rows);
        }
        catch
        {
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
            Task<int>? work = lane.Work;
            if (work is null)
            {
                continue;
            }

            lane.Work = null;
            try
            {
                await work.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Disposal must not throw, and an abandoned split's failure has no one to report to.
                // Awaiting it is what keeps it from surfacing as an unobserved task exception.
            }

            lane.Context.ResetBatch();
        }

        DisposeLanes();
    }

    private bool HasPendingWork()
    {
        for (int i = 0; i < _lanes.Length; i++)
        {
            if (_lanes[i].Work is not null)
            {
                return true;
            }
        }

        return false;
    }

    private void ReleaseCurrent()
    {
        RecordBatch? batch = _current;
        _current = null;
        _currentLane = null;
        batch?.Dispose();
    }

    private void DisposeLanes()
    {
        for (int i = 0; i < _lanes.Length; i++)
        {
            _lanes[i].Context.Dispose();
        }
    }

    /// <summary>One decode flow: its own <see cref="ScanContext"/>, never shared (contract §2.2).</summary>
    private sealed class Lane
    {
        internal Lane(ScanContext context) => Context = context;

        internal ScanContext Context { get; }

        internal RowRange Rows { get; set; }

        internal Task<int>? Work { get; set; }
    }
}
