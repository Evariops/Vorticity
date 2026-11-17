using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Vorticity.Execution;

/// <summary>
/// The threads a session computes on beside its callers' own, shared by everything of the session: at most
/// <see cref="Lanes"/> of them at once, the session's <see cref="VortexSessionOptions.MaxDegreeOfParallelism"/>, however
/// many writes, scans and jobs there are.
/// </summary>
/// <remarks>
/// <para>
/// Work holds a lane while it computes, so that the degree is cores at work rather than threads asked for: the pumps of
/// the session's core (<see cref="FlowCore"/>), a scan's splits decoded ahead of its consumer, a job's own flow, each runs
/// only on a lane <see cref="TryEnter"/> grants, and a piece of a thread that holds one runs on it. What holds a lane never
/// waits for another, which a holder that waits for the waiter would never give back: it takes one that is free now, or
/// does the work on its own. Only what holds none, a job about to start, waits for one (<see cref="EnterAsync"/>), and the
/// pumps give theirs to it at their unit's end.
/// </para>
/// <para>
/// A lane is a count, not a thread: whichever thread holds it computes on it, a thread of the pool, or a caller's. A lane
/// given back starts a pump of the core when work waits in its queue. The time the lanes were held says how much of the
/// degree the session used, and the most held at once that the degree held.
/// </para>
/// </remarks>
internal sealed class LaneBudget
{
    private readonly SemaphoreSlim _free;
    private long _held;
    private int _peak;
    private int _waiting;

    /// <param name="lanes">The lanes, at least one.</param>
    internal LaneBudget(int lanes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(lanes);
        Lanes = lanes;
        _free = new SemaphoreSlim(lanes, lanes);
    }

    /// <summary>The lanes there are.</summary>
    internal int Lanes { get; }

    /// <summary>The core whose pumps start on a lane given back while work waits for one; set by the core.</summary>
    internal FlowCore? Core { get; set; }

    /// <summary>The time the lanes were held, every lane's added up, in <see cref="Stopwatch"/> ticks: the degree the session used.</summary>
    internal long HeldTicks => Interlocked.Read(ref _held);

    /// <summary>The lanes held now.</summary>
    internal int Held => Lanes - _free.CurrentCount;

    /// <summary>The most lanes held at once since the budget was made, or since <see cref="ResetPeak"/>, which never passes <see cref="Lanes"/>.</summary>
    internal int PeakHeld => Volatile.Read(ref _peak);

    /// <summary>The works that hold no lane and wait for one (<see cref="EnterAsync"/>): the pumps give theirs at a unit's end.</summary>
    internal int Waiting => Volatile.Read(ref _waiting);

    /// <summary>Waits for a lane, and returns when it was taken, for <see cref="Exit"/>. Only for what holds no lane.</summary>
    internal async ValueTask<long> EnterAsync(CancellationToken cancellationToken)
    {
        if (!TakeFree())
        {
            Interlocked.Increment(ref _waiting);
            try
            {
                await _free.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Decrement(ref _waiting);
            }
        }

        NotePeak();
        return Stopwatch.GetTimestamp();
    }

    /// <summary>Takes a lane if one is free, and says when, for <see cref="Exit"/>.</summary>
    internal bool TryEnter(out long entered)
    {
        if (TakeFree())
        {
            NotePeak();
            entered = Stopwatch.GetTimestamp();
            return true;
        }

        entered = 0;
        return false;
    }

    /// <summary>Gives back the lane taken at <paramref name="entered"/>, and starts a pump on it when work waits for one.</summary>
    internal void Exit(long entered)
    {
        ExitQuietly(entered);
        Core?.LaneFreed();
    }

    /// <summary>Gives back the lane taken at <paramref name="entered"/>, for the core that took it and found nothing to run.</summary>
    internal void ExitQuietly(long entered)
    {
        Interlocked.Add(ref _held, Stopwatch.GetTimestamp() - entered);
        _free.Release();
    }

    /// <summary>Takes a lane free now, without waiting: false when none is.</summary>
    private bool TakeFree() => _free.Wait(0, CancellationToken.None);

    /// <summary>Starts <see cref="PeakHeld"/> again from the lanes held now, for a measure of one work: a bench's job.</summary>
    internal void ResetPeak() => Volatile.Write(ref _peak, Held);

    /// <summary>Raises the peak to the lanes held now: once a lane taken, a compare and swap that rarely loops.</summary>
    private void NotePeak()
    {
        int held = Held;
        int peak;
        while (held > (peak = Volatile.Read(ref _peak)) && Interlocked.CompareExchange(ref _peak, held, peak) != peak)
        {
        }
    }
}
