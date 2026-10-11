using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Vorticity.ObjectStorage.Http;

/// <summary>
/// The requests a store has in flight at most, halved when the service throttles and grown back by one
/// for each success once the service has been calm for a while, as a congestion window is. A request
/// that finds the limit reached waits in order.
/// </summary>
/// <remarks>
/// The limit halves once per episode, as a congestion window does once per round trip: the requests in
/// flight when it halved were sent under the old limit, and their throttles say nothing new. A throttle
/// of a request sent under the new limit opens the next episode.
/// </remarks>
internal sealed class ConcurrencyLimit(int maximum, TimeProvider time)
{
    /// <summary>How long the limit stays down after a throttle before it grows again.</summary>
    internal static readonly TimeSpan Calm = TimeSpan.FromSeconds(10);

    private readonly object _gate = new object();
    private readonly Queue<TaskCompletionSource> _waiting = new Queue<TaskCompletionSource>();
    private readonly int _maximum = maximum;
    private int _limit = maximum;
    private int _inFlight;
    private int _episode;
    private DateTimeOffset _calmFrom;

    /// <summary>The episode of throttling the limit is in, which a request notes once it enters.</summary>
    internal int Episode => Volatile.Read(ref _episode);

    /// <summary>The requests allowed in flight now.</summary>
    internal int Limit
    {
        get
        {
            lock (_gate)
            {
                return _limit;
            }
        }
    }

    /// <summary>Takes a place, at once when one is free.</summary>
    internal ValueTask EnterAsync(CancellationToken cancellationToken)
    {
        TaskCompletionSource waiter;
        lock (_gate)
        {
            if (_inFlight < _limit)
            {
                _inFlight++;
                return ValueTask.CompletedTask;
            }

            waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiting.Enqueue(waiter);
        }

        return WaitAsync(waiter, cancellationToken);
    }

    /// <summary>Gives a place back, to the first request waiting for one.</summary>
    internal void Exit()
    {
        lock (_gate)
        {
            _inFlight--;
            Wake();
        }
    }

    /// <summary>
    /// The service throttled a request that entered in <paramref name="episode"/>: the limit halves and
    /// stays down until the service has been calm a while, unless it already halved since the request
    /// entered.
    /// </summary>
    internal void Throttled(int episode)
    {
        lock (_gate)
        {
            if (episode != _episode)
            {
                return;
            }

            _episode++;
            _limit = Math.Max(1, _limit / 2);
            _calmFrom = time.GetUtcNow() + Calm;
        }
    }

    /// <summary>A request succeeded: once calm, the limit grows by one toward its maximum.</summary>
    internal void Succeeded()
    {
        lock (_gate)
        {
            if (_limit < _maximum && time.GetUtcNow() >= _calmFrom)
            {
                _limit++;
                Wake();
            }
        }
    }

    private void Wake()
    {
        while (_inFlight < _limit && _waiting.TryDequeue(out TaskCompletionSource? next))
        {
            if (next.TrySetResult())
            {
                _inFlight++;
            }
        }
    }

    private async ValueTask WaitAsync(TaskCompletionSource waiter, CancellationToken cancellationToken)
    {
        using (cancellationToken.Register(static state => ((TaskCompletionSource)state!).TrySetCanceled(), waiter))
        {
            // The waiter's task is completed by the exit that frees a place, which is the point.
#pragma warning disable VSTHRD003
            await waiter.Task.ConfigureAwait(false);
#pragma warning restore VSTHRD003
        }
    }
}
