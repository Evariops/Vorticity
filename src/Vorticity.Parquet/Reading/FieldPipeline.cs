using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Threading;
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
/// read waits for every field of the batch it asks for, rethrows the first error one met, and
/// releases each batch as it asks for the next.
/// </para>
/// <para>
/// A batch's nodes reference their pages where they lie, so a reader holds the pages a batch retires
/// until the read releases it. Only flat fields take part, within a row group read in place, every
/// batch of which is released before the group ends: the batches the page index leaves, each field
/// stepping over those it rules out before each of its own.
/// </para>
/// </remarks>
internal sealed class FieldPipeline : IThreadPoolWorkItem, IDisposable
{
    /// <summary>The batches of a field decoded ahead of the read's, the one it holds included.</summary>
    internal const int Slots = 2;

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

    /// <summary>The first error a field met, after which its later batches are not decoded: any batch waited for then rethrows it.</summary>
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

    /// <summary>Waits for every field of batch <paramref name="batch"/>; the slot its nodes lie in.</summary>
    internal int Wait(int batch)
    {
        int slot = batch % Slots;
        lock (_gate)
        {
            while (_pending[slot] > 0 && _failed is null)
            {
                Monitor.Wait(_gate);
            }

            if (_pending[slot] > 0)
            {
                _failed!.Throw();
            }

            _errors[slot]?.Throw();
            return slot;
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
    /// Ends the decode: the lanes waited for, the fields' pages held given back. Every batch the read
    /// asked for must be released first.
    /// </summary>
    internal void Finish()
    {
        lock (_gate)
        {
            _stopping = true;
            _runnable.Clear();
            while (_runners > 0)
            {
                Monitor.Wait(_gate);
            }
        }

        foreach (ColumnChunkReader reader in _readers)
        {
            reader.ReleaseHeld(long.MaxValue);
            reader.HoldsRetired = false;
        }
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
                        Monitor.PulseAll(_gate);
                        return;
                    }
                }
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
        lock (_gate)
        {
            _next[field] = batch + 1;
            if (error is not null)
            {
                _errors[slot] ??= error;
                _failed ??= error;
                Monitor.PulseAll(_gate);
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

            if (--_pending[slot] == 0)
            {
                Monitor.PulseAll(_gate);
            }

            if (!_stopping && _pages?.Waiting != true)
            {
                next = Take(thread);
            }

            wake = Wake();
        }

        Queue(wake);
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

    /// <summary>Queues <paramref name="lanes"/> lanes on the pool, past the gate: a queue may wake a thread, microseconds the gate would be held for.</summary>
    private void Queue(int lanes)
    {
        for (int i = 0; i < lanes; i++)
        {
            ThreadPool.UnsafeQueueUserWorkItem(this, preferLocal: false);
        }
    }
}
