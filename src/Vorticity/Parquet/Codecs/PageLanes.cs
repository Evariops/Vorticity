using System;
using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;
using Vorticity.Parquet.Metadata;

namespace Vorticity.Parquet.Codecs;

/// <summary>
/// The lanes of the pool a scan or a writer lends its columns to run their pages' codecs on: as many
/// at once as its degree, shared by every column, so that a wide file's columns and a narrow one's
/// next pages alike keep them busy.
/// </summary>
/// <remarks>
/// A work queued waits in a queue of the lanes' own as well as the pool's: the pool's threads run the
/// oldest work waiting as they come to it, and so does any thread that helps (<see cref="Help"/>),
/// such as the scan's own lanes between two of their fields, which would otherwise hold every thread
/// of the pool while the pages they wait for queue behind them.
/// </remarks>
internal sealed class PageLanes
{
    /// <summary>The most data pages a column reader keeps decompressed or decompressing ahead of the one it reads.</summary>
    internal const int Depth = 8;

    /// <summary>
    /// The decompressed bytes past which a column reader takes no further page ahead, though it takes
    /// one at least: eight of the megabyte pages other writers cut, as many as <see cref="Depth"/>.
    /// </summary>
    internal const int Bytes = 8 << 20;

    private readonly long _budget;
    private readonly ConcurrentQueue<LaneWork> _waiting = new();
    private int _free;
    private long _ahead;

    /// <summary>
    /// Lanes for <paramref name="lanes"/> codecs at once, and the decompressed bytes all the scan's
    /// columns hold ahead: 2 MiB a lane, 48 MiB at least, six columns' worth of <see cref="Bytes"/>, so
    /// that a file's few columns of long text keep their pages ahead on few lanes as on many; a
    /// column's first page ahead may pass it. ClickBench's first file scanned in 34 ms on 8 lanes
    /// under 16 MiB, its four columns of text sharing two columns' worth, against 29 under 48.
    /// </summary>
    internal PageLanes(int lanes)
    {
        _free = lanes;
        _budget = Math.Max(48L << 20, lanes * (2L << 20));
    }

    /// <summary>
    /// Reserves <paramref name="size"/> decompressed bytes of the scan's for a page ahead: false when
    /// they would pass its budget, but for a column's <paramref name="first"/> page ahead.
    /// </summary>
    internal bool TryReserve(int size, bool first)
    {
        if (Interlocked.Add(ref _ahead, size) <= _budget || first)
        {
            return true;
        }

        Interlocked.Add(ref _ahead, -size);
        return false;
    }

    /// <summary>Gives back the bytes <see cref="TryReserve"/> reserved for a page ahead, read or dropped.</summary>
    internal void Unreserve(int size) => Interlocked.Add(ref _ahead, -size);

    /// <summary>
    /// The decompressed bytes from which a page of <paramref name="codec"/> is worth a lane: about ten
    /// microseconds of its decompression, which a hand-off to another thread costs. A smaller page is
    /// decompressed where it is read.
    /// </summary>
    internal static int Worth(CompressionCodec codec) => codec switch
    {
        CompressionCodec.Snappy or CompressionCodec.Lz4Raw => 64 << 10,
        CompressionCodec.Zstd => 16 << 10,
        _ => 4 << 10,
    };

    /// <summary>Takes a lane; false when every one is busy, and the column runs the codec itself.</summary>
    internal bool TryTake()
    {
        int free = Volatile.Read(ref _free);
        while (free > 0)
        {
            int seen = Interlocked.CompareExchange(ref _free, free - 1, free);
            if (seen == free)
            {
                return true;
            }

            free = seen;
        }

        return false;
    }

    /// <summary>Gives a lane back.</summary>
    internal void Give() => Interlocked.Increment(ref _free);

    /// <summary>Whether a work queued waits for a lane, or a column or another lane claimed it since.</summary>
    internal bool Waiting => !_waiting.IsEmpty;

    /// <summary>
    /// Runs the oldest work waiting for a lane on the calling thread, passing over those a column or
    /// another thread has claimed: false when none waits.
    /// </summary>
    internal bool Help()
    {
        while (_waiting.TryDequeue(out LaneWork? work))
        {
            if (work.TryRun())
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Queues <paramref name="work"/> for the pool and for the threads that help: each work queued
    /// adds a run of the pool's, which takes one work waiting at least, so that the queue holds no
    /// more than the pool's runs to come.
    /// </summary>
    internal void Post(LaneWork work)
    {
        _waiting.Enqueue(work);
        ThreadPool.UnsafeQueueUserWorkItem(work, preferLocal: false);
    }
}


/// <summary>
/// A page's codec, run on a lane of <see cref="PageLanes"/> the column took for it, or by the column
/// itself when it comes to the page before the pool has run the lane: whichever claims the work runs
/// it. No thread waits for a lane: the column awaits the one running the page it takes next before a
/// read (<see cref="JoinAsync"/>), and a read that meets such a page where it cannot await does the
/// work again itself; a page the column gives up while its lane runs is the lane's to give back once
/// it finishes (<see cref="Abandon"/>).
/// </summary>
/// <param name="lanes">The lanes, one of which the column took for the work it queues, given back by whichever claims it.</param>
internal abstract class LaneWork(PageLanes lanes) : IThreadPoolWorkItem, IValueTaskSource
{
    // Who claimed the work, a lane or its column, and what became of it, in one word: a lane that finishes reads in the
    // same step whether the column awaits it or gave it up, and the column whether the lane has finished.
    private const int ByLane = 1;
    private const int ByColumn = 2;
    private const int Finished = 4;
    private const int Awaited = 8;
    private const int GivenUp = 16;

    // What the column awaits, completed by the lane as it finishes; its continuation runs on the pool, not on the lane,
    // which goes on with the pages after. Or what the lane calls as it finishes, on its own thread (OnFinished).
    private ManualResetValueTaskSourceCore<bool> _finished = new() { RunContinuationsAsynchronously = true };
    private Action? _onFinished;
    private int _state;
    private bool _queued;

    /// <summary>What the work threw, which the column rethrows where it takes the page.</summary>
    internal ExceptionDispatchInfo? Error { get; private set; }

    /// <summary>Whether the work is done: run, by a lane or by the column.</summary>
    internal bool Done => (Volatile.Read(ref _state) & Finished) != 0;

    /// <summary>Whether a lane has begun the work and not yet finished it.</summary>
    internal bool Running => (Volatile.Read(ref _state) & (ByLane | Finished)) == ByLane;

    /// <summary>Queues the work on the lane taken for it, for the pool or a thread that helps to run.</summary>
    internal void Queue()
    {
        _queued = true;
        lanes.Post(this);
    }

    /// <summary>A run of the pool's: the oldest work waiting for a lane, this one or another.</summary>
    public void Execute() => lanes.Help();

    /// <summary>Runs the work on a lane, unless the column or another lane has claimed it: false then.</summary>
    internal bool TryRun()
    {
        if (Interlocked.CompareExchange(ref _state, ByLane, 0) != 0)
        {
            return false;
        }

        Perform();
        int was = Interlocked.Or(ref _state, Finished);
        Free();
        if ((was & GivenUp) != 0)
        {
            Discard();
        }

        if ((was & Awaited) != 0)
        {
            if (_onFinished is { } resume)
            {
                _onFinished = null;
                resume();
            }
            else
            {
                _finished.SetResult(true);
            }
        }

        return true;
    }

    /// <summary>
    /// The work done: here when no lane has begun it, true then; true once a lane has finished it; false while one
    /// runs it, which the column awaits before the read (<see cref="JoinAsync"/>) or does again itself.
    /// </summary>
    internal bool TryComplete()
    {
        if (Interlocked.CompareExchange(ref _state, ByColumn, 0) == 0)
        {
            Perform();
            Interlocked.Or(ref _state, Finished);
            Free();
            return true;
        }

        return Done;
    }

    /// <summary>
    /// Completes once the work is done: at once when it is, or when no lane has begun it, run here then; else as the
    /// lane running it finishes. The column's alone, one await at a time.
    /// </summary>
    internal ValueTask JoinAsync()
    {
        if (TryComplete())
        {
            return default;
        }

        _onFinished = null;
        _finished.Reset();
        int was = Interlocked.Or(ref _state, Awaited);
        return (was & Finished) != 0 ? default : new ValueTask(this, _finished.Version);
    }

    /// <summary>
    /// Calls <paramref name="resume"/> once the lane running the work finishes it, on that lane's thread, which has its
    /// result in its cache: something short, which the lane does before the pages after. False, nothing called, when the
    /// work is done already, or no lane had begun it and it ran here. The column's alone, one wait at a time.
    /// </summary>
    internal bool OnFinished(Action resume)
    {
        if (TryComplete())
        {
            return false;
        }

        // Set before the lane can read it as it finishes, and taken back when it had finished already.
        _onFinished = resume;
        if ((Interlocked.Or(ref _state, Awaited) & Finished) != 0)
        {
            _onFinished = null;
            return false;
        }

        return true;
    }

    /// <summary>
    /// The work given up, its result never taken: true when what it holds is the column's to give back now, no lane
    /// having begun it or one having finished it; false while a lane runs it, which gives it back as it finishes
    /// (<see cref="Discard"/>).
    /// </summary>
    internal bool Abandon()
    {
        if (Interlocked.CompareExchange(ref _state, ByColumn | Finished, 0) == 0)
        {
            Free();
            return true;
        }

        return (Interlocked.Or(ref _state, GivenUp) & Finished) != 0;
    }

    void IValueTaskSource.GetResult(short token) => _finished.GetResult(token);

    ValueTaskSourceStatus IValueTaskSource.GetStatus(short token) => _finished.GetStatus(token);

    void IValueTaskSource.OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags) =>
        _finished.OnCompleted(continuation, state, token, flags);

    /// <summary>The codec's work, on whichever thread claimed it.</summary>
    protected abstract void Run();

    /// <summary>Gives back what the work holds, its result given up while a lane ran it: on that lane, as it finishes.</summary>
    protected abstract void Discard();

    /// <summary>Gives back the lane a queued work took, once claimed: the pool's later run of it finds nothing to do.</summary>
    private void Free()
    {
        if (_queued)
        {
            lanes.Give();
        }
    }

    /// <summary>Runs the work, keeping what it throws for the column.</summary>
    private void Perform()
    {
        try
        {
            Run();
        }
        catch (Exception exception)
        {
            Error = ExceptionDispatchInfo.Capture(exception);
        }
    }
}
