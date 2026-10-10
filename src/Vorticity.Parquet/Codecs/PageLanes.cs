using System;
using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using System.Threading;
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
    /// columns hold ahead: 2 MiB a lane, 16 MiB at least, which a column's first page ahead may pass.
    /// </summary>
    internal PageLanes(int lanes)
    {
        _free = lanes;
        _budget = Math.Max(16L << 20, lanes * (2L << 20));
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
/// it, and the column waits for a lane that did.
/// </summary>
/// <param name="lanes">The lanes, one of which the column took for the work it queues, given back by whichever claims it.</param>
internal abstract class LaneWork(PageLanes lanes) : IThreadPoolWorkItem
{
    private readonly object _gate = new();
    private int _claimed;
    private bool _queued;
    private bool _done;

    /// <summary>What the work threw, which the column rethrows where it takes the page.</summary>
    internal ExceptionDispatchInfo? Error { get; private set; }

    /// <summary>Whether the work is done: run, by a lane or by the column.</summary>
    internal bool Done => Volatile.Read(ref _done);

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
        if (!Claim())
        {
            return false;
        }

        Perform();
        lock (_gate)
        {
            _done = true;
            Monitor.PulseAll(_gate);
        }

        Free();
        return true;
    }

    /// <summary>The work done: here, when no lane has begun it, else once the lane that did has finished.</summary>
    internal void Complete()
    {
        if (Claim())
        {
            Perform();
            Volatile.Write(ref _done, true);
            Free();
        }
        else
        {
            Join();
        }
    }

    /// <summary>The work given up, where no lane has begun it; else waited for, as the lane that did may still hold its bytes.</summary>
    internal void Withdraw()
    {
        if (Claim())
        {
            Free();
        }
        else
        {
            Join();
        }
    }

    /// <summary>Waits for the lane that claimed the work to finish it.</summary>
    internal void Join()
    {
        lock (_gate)
        {
            while (!_done)
            {
                Monitor.Wait(_gate);
            }
        }
    }

    /// <summary>The codec's work, on whichever thread claimed it.</summary>
    protected abstract void Run();

    /// <summary>Claims the work; false when a lane or the column has already.</summary>
    private bool Claim() => Interlocked.Exchange(ref _claimed, 1) == 0;

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
