using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;
using Vorticity.Arrays;
using Vorticity.File;
using Vorticity.Parquet.Codecs;

namespace Vorticity.Parquet.Reading;

/// <summary>
/// A row group's batches decoded a field at a time ahead of the scan's read, on the lanes of the pool
/// the scan gives it: each field's batches one after the other, up to <see cref="Slots"/>
/// batches past the last one the read released, each into a context of its own, so that a batch no
/// longer waits for the field that begins a page in it while the others idle.
/// </summary>
/// <remarks>
/// <para>
/// A field decodes batch <c>j</c> into slot <c>j % Slots</c> once the read has released batch
/// <c>j - Slots</c>, the slot's last: the slot's context reset, and the pages its reader held for the
/// batches released given back. A lane takes a field it decoded last, of the earliest batch it has
/// one of, before another's: the field's dictionary and the blocks its contexts keep from batch to
/// batch lie in that thread's cache. Else it takes the field of the earliest batch that may run. The
/// read awaits every field of the batch it asks for, rethrows the first error one met, and
/// releases each batch as it asks for the next.
/// </para>
/// <para>
/// A batch's nodes reference their pages where they lie, so a reader holds the pages a batch retires
/// until the read releases it. Only flat fields take part, within a row group read in place, every
/// batch of which is released before the group ends: the batches the page index leaves, each field
/// stepping over those it rules out before each of its own.
/// </para>
/// <para>
/// No thread waits. The read awaits its batch (<see cref="WaitAsync"/>) and the lanes' end
/// (<see cref="FinishAsync"/>), the pipeline itself the source of what it awaits, and goes on on the
/// lane that completes it. A field whose next batch begins in a page a lane of the scan still
/// decompresses is put aside until that lane is done, which resumes it: its own lane, still counted,
/// gives its thread back to the pool meanwhile, as many lanes at work or waiting as when a lane
/// waited on its thread.
/// </para>
/// </remarks>
internal sealed class FieldPipeline : IThreadPoolWorkItem, IValueTaskSource<int>, IDisposable
{
    /// <summary>The batches of a field decoded ahead of the read's, the one it holds included.</summary>
    internal const int Slots = 2;

    // What the read awaits: a batch's slot, every field of it decoded; the lanes' end; or nothing.
    private const int Nothing = -1;
    private const int Ending = -2;

    private readonly ColumnChunkReader[] _readers;
    private readonly PageLanes? _pages;
    private readonly int _lanes;
    private readonly ScanContext[] _contexts;
    private readonly int[] _nodes;
    private readonly int[] _next;
    private readonly bool[] _idle;

    /// <summary>Per field, the managed thread that decoded it last, which takes it again first.</summary>
    private readonly int[] _thread;
    private readonly int[] _pending = new int[Slots];
    private readonly ExceptionDispatchInfo?[] _errors = new ExceptionDispatchInfo?[Slots];

    /// <summary>The first error a field met, after which its later batches are not decoded: any batch awaited then rethrows it.</summary>
    private ExceptionDispatchInfo? _failed;
    private readonly List<int> _runnable = [];
    private readonly object _gate = new();
    private int _runners;

    /// <summary>The lanes queued that have not yet taken a field.</summary>
    private int _starting;
    private int _batches;

    /// <summary>Per batch of the row group, the rows its fields step over before it, pruned, and its own.</summary>
    private (int Skip, int Rows)[] _steps = [];
    private int _released;
    private bool _stopping;

    // What the read awaits, completed past the gate by whichever lane makes it due, which runs the read on, holding no field
    // meanwhile: the read handed to a thread of the pool's after each batch it awaited took 15.6 ms against 15.1 to scan
    // five zstd columns (v1 pages) at sixteen lanes.
    private ManualResetValueTaskSourceCore<int> _ready;
    private int _awaited = Nothing;

    /// <summary>The fields put aside until a lane finishes the page their next batch begins in.</summary>
    private int _parked;

    /// <summary>Per field, what makes it runnable again once that page is done.</summary>
    private readonly Action[] _resume;

    /// <summary>
    /// A pipeline of <paramref name="readers"/>, a field each, on <paramref name="lanes"/> lanes at most,
    /// which run the pages <paramref name="pages"/> queues ahead before each field.
    /// </summary>
    internal FieldPipeline(ColumnChunkReader[] readers, VortexReadOptions options, int lanes, PageLanes? pages)
    {
        _readers = readers;
        _pages = pages;
        _lanes = lanes;
        _contexts = new ScanContext[readers.Length * Slots];
        for (int i = 0; i < _contexts.Length; i++)
        {
            _contexts[i] = DetachedContexts.Shared.RentDetached(options);
        }

        _nodes = new int[readers.Length * Slots];
        _next = new int[readers.Length];
        _idle = new bool[readers.Length];
        _thread = new int[readers.Length];
        _resume = new Action[readers.Length];
        for (int field = 0; field < readers.Length; field++)
        {
            int resumed = field;
            _resume[field] = () => Resume(resumed);
        }
    }

    /// <summary>
    /// Starts the decode of the readers' next batches, each the rows its fields step over first,
    /// pruned, and its own, in <paramref name="steps"/>: the read then asks for batches 0, 1 and on.
    /// </summary>
    internal void Start(ReadOnlySpan<(int Skip, int Rows)> steps)
    {
        int wake;
        lock (_gate)
        {
            if (_steps.Length < steps.Length)
            {
                _steps = new (int Skip, int Rows)[steps.Length];
            }

            steps.CopyTo(_steps);
            _batches = steps.Length;
            _released = -1;
            _stopping = false;
            _failed = null;
            for (int slot = 0; slot < Slots; slot++)
            {
                _pending[slot] = slot < _batches ? _readers.Length : 0;
                _errors[slot] = null;
            }

            for (int field = 0; field < _readers.Length; field++)
            {
                _readers[field].HoldsRetired = true;
                _next[field] = 0;
                _idle[field] = false;
                if (_batches > 0)
                {
                    _runnable.Add(field);
                }
            }

            wake = Wake();
        }

        Queue(wake);
    }

    /// <summary>
    /// Completes once every field of batch <paramref name="batch"/> is decoded, with the slot its nodes lie in; rethrows
    /// the first error a field met.
    /// </summary>
    internal ValueTask<int> WaitAsync(int batch)
    {
        int slot = batch % Slots;
        lock (_gate)
        {
            if (_pending[slot] > 0 && _failed is null)
            {
                _ready.Reset();
                _awaited = slot;
                return new ValueTask<int>(this, _ready.Version);
            }

            if (_pending[slot] > 0)
            {
                _failed!.Throw();
            }

            _errors[slot]?.Throw();
            return new ValueTask<int>(slot);
        }
    }

    /// <summary>Field <paramref name="field"/>'s node in slot <paramref name="slot"/>, and the context whose arena holds it.</summary>
    internal int Node(int field, int slot, out ScanContext context)
    {
        context = _contexts[(field * Slots) + slot];
        return _nodes[(field * Slots) + slot];
    }

    /// <summary>Releases batch <paramref name="batch"/>, dead: its slot is free for the batch <see cref="Slots"/> on.</summary>
    internal void Release(int batch)
    {
        int wake;
        lock (_gate)
        {
            int slot = batch % Slots;
            _released = batch;
            _errors[slot] = null;
            if (batch + Slots < _batches)
            {
                _pending[slot] = _readers.Length;
            }

            for (int field = 0; field < _idle.Length; field++)
            {
                if (_idle[field] && _next[field] <= batch + Slots)
                {
                    _idle[field] = false;
                    _runnable.Add(field);
                }
            }

            wake = Wake();
        }

        Queue(wake);
    }

    /// <summary>
    /// Ends the decode: the lanes awaited, and the fields put aside, then the fields' pages held given
    /// back. Every batch the read asked for must be released first.
    /// </summary>
    internal ValueTask FinishAsync()
    {
        lock (_gate)
        {
            _stopping = true;
            _runnable.Clear();
            if (_runners > 0 || _parked > 0)
            {
                _ready.Reset();
                _awaited = Ending;
                return EndAsync(new ValueTask<int>(this, _ready.Version));
            }
        }

        Ended();
        return default;
    }

    private async ValueTask EndAsync(ValueTask<int> lanes)
    {
        await lanes.ConfigureAwait(false);
        Ended();
    }

    /// <summary>The fields' pages held given back, no lane at work.</summary>
    private void Ended()
    {
        foreach (ColumnChunkReader reader in _readers)
        {
            reader.ReleaseHeld(long.MaxValue);
            reader.HoldsRetired = false;
        }
    }

    int IValueTaskSource<int>.GetResult(short token) => _ready.GetResult(token);

    ValueTaskSourceStatus IValueTaskSource<int>.GetStatus(short token) => _ready.GetStatus(token);

    void IValueTaskSource<int>.OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags) =>
        _ready.OnCompleted(continuation, state, token, flags);

    /// <summary>
    /// Under the gate: whether what the read awaits is due, cleared then, with the slot it completes with or the error it
    /// rethrows.
    /// </summary>
    private bool Due(out int slot, out ExceptionDispatchInfo? error)
    {
        slot = _awaited;
        error = null;
        if (slot >= 0)
        {
            if (_pending[slot] > 0 && _failed is null)
            {
                return false;
            }

            error = _pending[slot] > 0 ? _failed : _errors[slot];
        }
        else if (slot != Ending || _runners > 0 || _parked > 0)
        {
            return false;
        }

        _awaited = Nothing;
        return true;
    }

    /// <summary>Completes what the read awaits, past the gate.</summary>
    private void Complete(int slot, ExceptionDispatchInfo? error)
    {
        if (error is not null)
        {
            _ready.SetException(error.SourceException);
        }
        else
        {
            _ready.SetResult(slot);
        }
    }

    /// <summary>
    /// Puts field <paramref name="field"/> aside when its next batch begins in a page a lane still decompresses, the read
    /// being unable to await it: runnable again once the lane is done (<see cref="Resume"/>). Whether it did.
    /// </summary>
    private bool Park(int field)
    {
        if (_pages is null)
        {
            return false;
        }

        ColumnChunkReader reader = _readers[field];
        (int skip, int rows) = _steps[_next[field]];
        if (reader.Unready(skip + rows) is not { } page)
        {
            return false;
        }

        // Counted aside before the lane can finish and resume it.
        lock (_gate)
        {
            _parked++;
        }

        if (page.OnFinished(_resume[field]))
        {
            reader.Counters?.AddAwaited();
            return true;
        }

        // Done meanwhile: the field goes now.
        lock (_gate)
        {
            _parked--;
        }

        return false;
    }

    /// <summary>The page field <paramref name="field"/> was put aside for is done: the field runnable again, unless the decode ends.</summary>
    private void Resume(int field)
    {
        int wake;
        bool due;
        int slot;
        ExceptionDispatchInfo? error;
        lock (_gate)
        {
            // The lane the field was put aside with, still counted among the runners, starts again.
            _parked--;
            _starting++;
            if (!_stopping)
            {
                _runnable.Add(field);
            }

            wake = Wake();
            due = Due(out slot, out error);
        }

        if (due)
        {
            Complete(slot, error);
        }

        Queue(1, here: true);
        Queue(wake);
    }

    /// <summary>
    /// A lane: the fields of the earliest batches decoded, a batch at a time, while one may run, the
    /// next taken in the same turn of the gate as the last one ends, and before each the pages
    /// decompressing ahead that wait for a lane. The lanes would otherwise hold the pool's threads
    /// while those pages queue behind them, and a field of large pages, which takes longest, would
    /// decompress each one itself.
    /// </summary>
    public void Execute()
    {
        int thread = Environment.CurrentManagedThreadId;
        bool fresh = true;
        int field = -1;
        while (true)
        {
            if (field < 0)
            {
                if (_pages is { } pages && pages.Help())
                {
                    continue;
                }

                bool due = false;
                int slot = 0;
                ExceptionDispatchInfo? error = null;
                lock (_gate)
                {
                    if (fresh)
                    {
                        _starting--;
                        fresh = false;
                    }

                    field = _stopping ? -1 : Take(thread);
                    if (field < 0)
                    {
                        _runners--;
                        due = Due(out slot, out error);
                    }
                }

                if (field < 0)
                {
                    if (due)
                    {
                        Complete(slot, error);
                    }

                    return;
                }
            }

            // A field whose next batch begins in a page a lane still decompresses waits aside for it, and the lane leaves
            // its thread to the pool, still counted: the scan's degree is the lanes at work or waiting, as when a lane
            // waited on its thread, and the field's resume takes the lane up again.
            if (Park(field))
            {
                return;
            }

            field = Decode(field, thread);
        }
    }

    public void Dispose()
    {
        foreach (ScanContext context in _contexts)
        {
            DetachedContexts.Shared.Return(context);
        }
    }

    /// <summary>
    /// Decodes field <paramref name="field"/>'s next batch into its slot, then makes it runnable again,
    /// or idle until a release; the field the lane takes next in the same turn of the gate, or -1 when
    /// none may run or pages wait for a lane, which go first.
    /// </summary>
    private int Decode(int field, int thread)
    {
        int batch = _next[field];
        int slot = batch % Slots;
        ColumnChunkReader reader = _readers[field];
        ScanContext context = _contexts[(field * Slots) + slot];
        (int skip, int rows) = _steps[batch];
        ExceptionDispatchInfo? error = null;
        try
        {
            // The slot's batch before was released: its arena reset, and the pages it held given back.
            reader.ReleaseHeld(Volatile.Read(ref _released));
            context.ResetBatch();
            reader.Batch = batch;
            if (skip > 0)
            {
                // The batches the page index ruled out before this one: stepped over, whole pages unread.
                reader.Skip(context, skip);
            }

            _nodes[(field * Slots) + slot] = reader.Read(context, rows);
        }
        catch (Exception exception)
        {
            error = ExceptionDispatchInfo.Capture(exception);
        }

        int wake;
        int next = -1;
        bool due;
        int ready;
        ExceptionDispatchInfo? failed;
        lock (_gate)
        {
            _next[field] = batch + 1;
            if (error is not null)
            {
                _errors[slot] ??= error;
                _failed ??= error;
            }
            else if (batch + 1 < _batches)
            {
                if (batch + 1 <= _released + Slots)
                {
                    _runnable.Add(field);
                }
                else
                {
                    _idle[field] = true;
                }
            }

            _pending[slot]--;
            due = Due(out ready, out failed);

            // The lane that completes what the read awaits runs the read on, on its thread, and holds no field meanwhile.
            if (!due && !_stopping && _pages?.Waiting != true)
            {
                next = Take(thread);
            }

            wake = Wake();
        }

        Queue(wake);
        if (due)
        {
            Complete(ready, failed);
        }

        return next;
    }

    /// <summary>
    /// The runnable field the lane on <paramref name="thread"/> decodes next, or -1; under the gate: one
    /// it decoded last, of the earliest batch it has one of, else the field of the earliest batch.
    /// </summary>
    private int Take(int thread)
    {
        int at = -1;
        int mine = -1;
        for (int i = 0; i < _runnable.Count; i++)
        {
            if (at < 0 || _next[_runnable[i]] < _next[_runnable[at]])
            {
                at = i;
            }

            if (_thread[_runnable[i]] == thread && (mine < 0 || _next[_runnable[i]] < _next[_runnable[mine]]))
            {
                mine = i;
            }
        }

        if (mine >= 0)
        {
            at = mine;
        }

        if (at < 0)
        {
            return -1;
        }

        int field = _runnable[at];
        _thread[field] = thread;
        _runnable[at] = _runnable[^1];
        _runnable.RemoveAt(_runnable.Count - 1);
        return field;
    }

    /// <summary>The lanes to queue for the runnable fields, as many as the lanes allow, counted as running; under the gate.</summary>
    private int Wake()
    {
        int wake = 0;
        while (_runners < _lanes && _starting < _runnable.Count)
        {
            _runners++;
            _starting++;
            wake++;
        }

        return wake;
    }

    /// <summary>
    /// Queues <paramref name="lanes"/> lanes on the pool, past the gate: a queue may wake a thread, microseconds the gate
    /// would be held for. <paramref name="here"/>: for a field the lane that finished its page resumed, on that thread's own
    /// queue, which it runs next with the page in its cache, rather than behind the pages queued for the pool's.
    /// </summary>
    private void Queue(int lanes, bool here = false)
    {
        for (int i = 0; i < lanes; i++)
        {
            ThreadPool.UnsafeQueueUserWorkItem(this, preferLocal: here);
        }
    }
}
