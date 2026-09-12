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
// HAND-WRITTEN, NOT `yield return`. A compiler-generated async iterator allocates its state machine
// and can allocate per MoveNextAsync. One allocation per SCAN is acceptable and documented; per
// BATCH is not. The enumerator is therefore built over ManualResetValueTaskSourceCore<bool>, the
// pattern System.Threading.Channels uses, with the synchronous-completion fast path - which is
// every batch of a memory-mapped file - returning `new ValueTask<bool>(true)` and allocating
// nothing at all.
//
// The one unavoidable per-batch allocation is the RecordBatch itself: §12.1 makes it a sealed class
// with readonly fields, so it cannot be recycled. Nothing else allocates in steady state, and
// ZeroAllocationsPerBatch asserts exactly that bound rather than a round number.
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;

using Vorticity.Arrays;
using Vorticity.Columns;
using Vorticity.File;
using Vorticity.IO;
using Vorticity.Layouts;
using Vorticity.Types;

namespace Vorticity.Scan;

/// <summary>The batches of one compiled scan.</summary>
/// <remarks>
/// Re-enumerable: every <see cref="GetAsyncEnumerator"/> call builds a fresh enumerator with its
/// own <see cref="ScanContext"/>, so two enumerations may run concurrently over one open file
/// (docs/09-contracts.md §1: the file is thread-safe, a scan is not).
/// </remarks>
public sealed class BatchAsyncEnumerable : IAsyncEnumerable<RecordBatch>
{
    private readonly VortexFile _file;
    private readonly LayoutTree _tree;
    private readonly Projection _projection;
    private readonly SplitPlan _plan;
    private readonly int _degree;
    private readonly DType _schema;

    internal BatchAsyncEnumerable(
        VortexFile file, LayoutTree tree, Projection projection, SplitPlan plan, int degree)
    {
        _file = file;
        _tree = tree;
        _projection = projection;
        _plan = plan;
        _degree = degree;

        // Computed once, here, and into an arena of this enumerable's own. Deriving it lazily from
        // the LayoutTree's arena would mutate a structure the tree promises is immutable and
        // therefore safe for concurrent scans (docs/09-contracts.md §1), and two threads reading
        // Schema would race on its arrays.
        _schema = projection.IsAll
            ? tree.Root.DType
            : projection.ProjectedSchema(tree.Root.DType, new DTypeArena());
    }

    /// <summary>The schema every batch of this scan carries.</summary>
    /// <remarks>
    /// It is what <see cref="RecordBatch.Schema"/> reports for every batch, and the two are
    /// asserted equal by the scan's tests: the batch's schema is what the layout reader built, and
    /// this is what the caller was promised.
    /// </remarks>
    public DType Schema => _schema;

    /// <summary>The compiled projection this scan runs under.</summary>
    public Projection Projection => _projection;

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
        new BatchAsyncEnumerator(_file, _tree, _projection, _plan, _degree, cancellationToken);
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
public sealed class BatchAsyncEnumerator : IAsyncEnumerator<RecordBatch>, IValueTaskSource<bool>
{
    private readonly LayoutTree _tree;
    private readonly ISegmentSource _source;
    private readonly FieldMask _mask;
    private readonly int _maxBatchRows;
    private readonly CancellationToken _token;
    private readonly Lane[] _lanes;
    private readonly Action _onReadCompleted;

    private SplitCursor _cursor;
    private ManualResetValueTaskSourceCore<bool> _core;
    private ValueTaskAwaiter _readAwaiter;

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
        Projection projection,
        SplitPlan plan,
        int degree,
        CancellationToken cancellationToken)
    {
        _tree = tree;
        _source = file.Segments;
        _mask = projection.RootMask;
        _maxBatchRows = (int)Math.Min(plan.MaxRows, int.MaxValue);
        _token = cancellationToken;
        _cursor = plan.CreateCursor();

        _lanes = new Lane[degree];
        for (int i = 0; i < degree; i++)
        {
            _lanes[i] = new Lane(new ScanContext(file));
        }

        // Allocated once per scan, so the awaiter's continuation costs nothing per batch.
        _onReadCompleted = OnReadCompleted;
        _core.RunContinuationsAsynchronously = true;
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

        if (_drained || !_cursor.TryNext(out RowRange split))
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
            read = _source.ReadManyAsync(lane.Context.Segments, _token);
        }
        catch
        {
            lane.Context.ResetBatch();
            throw;
        }

        if (read.IsCompletedSuccessfully)
        {
            // The whole memory-mapped path lands here, and it allocates nothing.
            read.GetAwaiter().GetResult();
            return new ValueTask<bool>(CompleteBatch());
        }

        _core.Reset();
        _readAwaiter = read.GetAwaiter();
        _readAwaiter.UnsafeOnCompleted(_onReadCompleted);
        return new ValueTask<bool>(this, _core.Version);
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

    // ------------------------------------------------------------------------- IValueTaskSource

    bool IValueTaskSource<bool>.GetResult(short token) => _core.GetResult(token);

    ValueTaskSourceStatus IValueTaskSource<bool>.GetStatus(short token) => _core.GetStatus(token);

    void IValueTaskSource<bool>.OnCompleted(
        Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags) =>
        _core.OnCompleted(continuation, state, token, flags);

    // ------------------------------------------------------------------------------ the two phases

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
            int root = Execute(lane.Context, _pending);
            _current = new RecordBatch(lane.Context, root, _pending.Start);
            return true;
        }
        catch
        {
            lane.Context.ResetBatch();
            throw;
        }
    }

    private void OnReadCompleted()
    {
        bool result;
        try
        {
            ValueTaskAwaiter awaiter = _readAwaiter;
            _readAwaiter = default;
            awaiter.GetResult();
            result = CompleteBatch();
        }
        catch (Exception exception)
        {
            // The synchronous path resets the lane in MoveNextAsync's catch and CompleteBatch
            // resets it in its own; this is the third way a batch can fail - a read whose ValueTask
            // completes faulted rather than throwing inline, which is what every async
            // ISegmentSource does - and it must leave the lane in the same state as the other two.
            // Without it the failed split's registrations stay in the SegmentRequestSet (the
            // source's AbandonPending releases the owners but leaves the set registered and ready
            // to retry, per ISegmentSource), so the NEXT batch registers on top of them and issues
            // one coalesced read covering a superset of the split it is actually reading.
            try
            {
                _currentLane?.Context.ResetBatch();
            }
            catch
            {
                // The I/O failure is what the caller needs to see, not a release failure behind it.
            }

            _core.SetException(exception);
            return;
        }

        _core.SetResult(result);
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
            if (_drained || !_cursor.TryNext(out RowRange split))
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
