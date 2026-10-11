using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Vorticity.IO;

/// <summary>
/// The reads in flight over one object store, shared by every scan of a session that reads it: at most a
/// count of requests, the store's connections, and a count of bytes, what the link holds in flight. A
/// request takes its place for the time it is in flight, and one that finds the window full waits in
/// order, so that a large range is not passed over forever by small ones.
/// </summary>
/// <remarks>
/// What a link needs in flight is its bandwidth times its latency. A store that measures both says the
/// product (<see cref="IMeasuredObjectStore"/>), and the window holds twice it, between a floor and the
/// session's ceiling; a store that measures nothing gets the ceiling. A request larger than the window
/// enters alone.
/// </remarks>
internal sealed class ReadWindow
{
    /// <summary>The least the window holds however fast the link looks: a few ranges of a scan.</summary>
    internal const long MinimumBytes = 8L << 20;

    private readonly object _gate = new object();
    private readonly Queue<(long Bytes, TaskCompletionSource Entered)> _waiting = new Queue<(long, TaskCompletionSource)>();
    private readonly int _maxRequests;
    private readonly long _ceiling;
    private readonly IMeasuredObjectStore? _measured;
    private int _requests;
    private long _bytes;

    /// <summary>A window of <paramref name="maxRequests"/> requests and at most <paramref name="ceiling"/> bytes.</summary>
    internal ReadWindow(int maxRequests, long ceiling, IMeasuredObjectStore? measured = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxRequests);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ceiling);
        _maxRequests = maxRequests;
        _ceiling = ceiling;
        _measured = measured;
    }

    /// <summary>The requests in flight.</summary>
    internal int Requests
    {
        get
        {
            lock (_gate)
            {
                return _requests;
            }
        }
    }

    /// <summary>The bytes in flight.</summary>
    internal long Bytes
    {
        get
        {
            lock (_gate)
            {
                return _bytes;
            }
        }
    }

    /// <summary>The bytes the window holds now: twice what the link holds in flight, between the floor and the ceiling.</summary>
    internal long MaxBytes
    {
        get
        {
            long measured = _measured?.BandwidthDelayBytes ?? 0;
            return measured > 0 ? Math.Clamp(2 * measured, Math.Min(MinimumBytes, _ceiling), _ceiling) : _ceiling;
        }
    }

    /// <summary>Takes a place for a request of <paramref name="bytes"/>, at once when it fits.</summary>
    internal ValueTask EnterAsync(long bytes, CancellationToken cancellationToken)
    {
        TaskCompletionSource entered;
        lock (_gate)
        {
            if (_waiting.Count == 0 && Fits(bytes, MaxBytes))
            {
                _requests++;
                _bytes += bytes;
                return ValueTask.CompletedTask;
            }

            entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiting.Enqueue((bytes, entered));
        }

        return WaitAsync(entered, cancellationToken);
    }

    /// <summary>Gives back the place of a request of <paramref name="bytes"/>, to the requests waiting in order.</summary>
    internal void Exit(long bytes)
    {
        lock (_gate)
        {
            _requests--;
            _bytes -= bytes;
            Wake();
        }
    }

    private bool Fits(long bytes, long maxBytes) =>
        _requests < _maxRequests && (_bytes + bytes <= maxBytes || _requests == 0);

    private void Wake()
    {
        long maxBytes = MaxBytes;
        while (_waiting.TryPeek(out (long Bytes, TaskCompletionSource Entered) next))
        {
            if (next.Entered.Task.IsCompleted)
            {
                // Cancelled while it waited.
                _waiting.Dequeue();
                continue;
            }

            if (!Fits(next.Bytes, maxBytes))
            {
                return;
            }

            _waiting.Dequeue();
            if (next.Entered.TrySetResult())
            {
                _requests++;
                _bytes += next.Bytes;
            }
        }
    }

    private static async ValueTask WaitAsync(TaskCompletionSource entered, CancellationToken cancellationToken)
    {
        using (cancellationToken.Register(static state => ((TaskCompletionSource)state!).TrySetCanceled(), entered))
        {
            // Completed by the exit that frees a place, in the order the requests came.
#pragma warning disable VSTHRD003
            await entered.Task.ConfigureAwait(false);
#pragma warning restore VSTHRD003
        }
    }
}

/// <summary>
/// A store that measures its link: its connections, and the bytes its link holds in flight, its
/// bandwidth times its latency, which sizes the window of reads a session keeps in flight over it.
/// </summary>
internal interface IMeasuredObjectStore
{
    /// <summary>The requests the store sends at once at most.</summary>
    int Connections { get; }

    /// <summary>The body throughput times the median first-byte latency, in bytes; 0 before it has measured either.</summary>
    long BandwidthDelayBytes { get; }
}
