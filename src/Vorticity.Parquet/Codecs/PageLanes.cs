using System;
using System.Runtime.ExceptionServices;
using System.Threading;
using Vorticity.Parquet.Metadata;

namespace Vorticity.Parquet.Codecs;

/// <summary>
/// The lanes of the pool a scan or a writer lends its columns to run their pages' codecs on: as many
/// at once as its degree, shared by every column, so that a wide file's columns and a narrow one's
/// next pages alike keep them busy.
/// </summary>
/// <param name="lanes">The codecs that may run at once.</param>
internal sealed class PageLanes(int lanes)
{
    /// <summary>The most data pages a column reader keeps decompressed or decompressing ahead of the one it reads.</summary>
    internal const int Depth = 8;

    /// <summary>
    /// The decompressed bytes past which a column reader takes no further page ahead, though it takes
    /// one at least: two of the megabyte pages other writers cut, eight of this writer's smaller ones.
    /// </summary>
    internal const int Bytes = 2 << 20;

    private int _free = lanes;

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
}

/// <summary>
/// A page's codec, run on a lane of <see cref="PageLanes"/> the column took for it, or by the column
/// itself when it comes to the page before the pool has run the lane: whichever claims the work runs
/// it, and the column waits for a lane that did.
/// </summary>
/// <param name="lanes">The lanes, one of which the column took for the work, given back when the pool runs it.</param>
internal abstract class LaneWork(PageLanes lanes) : IThreadPoolWorkItem
{
    private readonly object _gate = new();
    private int _claimed;
    private bool _done;

    /// <summary>What the work threw, which the column rethrows where it takes the page.</summary>
    internal ExceptionDispatchInfo? Error { get; private set; }

    /// <summary>Whether the work is done: run, by a lane or by the column.</summary>
    internal bool Done => Volatile.Read(ref _done);

    /// <summary>Queues the work for the pool to run on the lane taken for it.</summary>
    internal void Queue() => ThreadPool.UnsafeQueueUserWorkItem(this, preferLocal: false);

    /// <summary>Claims the work; false when a lane or the column has already.</summary>
    internal bool Claim() => Interlocked.Exchange(ref _claimed, 1) == 0;

    /// <summary>The lane's work: the codec, unless the column claimed it first; the lane given back either way.</summary>
    public void Execute()
    {
        try
        {
            if (Claim())
            {
                Perform();
                lock (_gate)
                {
                    _done = true;
                    Monitor.PulseAll(_gate);
                }
            }
        }
        finally
        {
            lanes.Give();
        }
    }

    /// <summary>The work done: here, when no lane has begun it, else once the lane that did has finished.</summary>
    internal void Complete()
    {
        if (Claim())
        {
            Perform();
            Volatile.Write(ref _done, true);
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
