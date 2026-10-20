using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Vorticity.Writing;

/// <summary>
/// The threads a session's writes compute on, shared by every write of the session: at most <see cref="Lanes"/> of them at
/// once, the session's <see cref="VortexSessionOptions.MaxDegreeOfParallelism"/>, however many writes and objects there are.
/// </summary>
/// <remarks>
/// <para>
/// A write holds a lane while it computes and gives it back while it waits for a write before it, the store or its caller,
/// so that the degree is cores at work rather than threads asked for: a writer's helpers join a piece only on a lane
/// <see cref="TryEnter"/> grants, and a piece of a thread that holds one runs on it. The time the lanes were held says how
/// much of the degree the writes used (<see cref="Held"/>).
/// </para>
/// <para>
/// A lane is a count, not a thread: whichever thread holds it computes on it, a thread of the pool for the steps of a
/// load, or a writer's caller.
/// </para>
/// </remarks>
internal sealed class LaneBudget
{
    private readonly SemaphoreSlim _free;
    private long _held;

    /// <param name="lanes">The lanes, at least one.</param>
    internal LaneBudget(int lanes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(lanes);
        Lanes = lanes;
        _free = new SemaphoreSlim(lanes, lanes);
    }

    /// <summary>The lanes there are.</summary>
    internal int Lanes { get; }

    /// <summary>The time the lanes were held, summed over every lane given back.</summary>
    internal TimeSpan Held => TimeSpan.FromSeconds(Interlocked.Read(ref _held) / (double)Stopwatch.Frequency);

    /// <summary>Waits for a lane, and returns when it was taken, for <see cref="Exit"/>.</summary>
    internal async ValueTask<long> EnterAsync(CancellationToken cancellationToken)
    {
        await _free.WaitAsync(cancellationToken).ConfigureAwait(false);
        return Stopwatch.GetTimestamp();
    }

    /// <summary>Takes a lane if one is free, and says when, for <see cref="Exit"/>.</summary>
    internal bool TryEnter(out long entered)
    {
        if (_free.Wait(0))
        {
            entered = Stopwatch.GetTimestamp();
            return true;
        }

        entered = 0;
        return false;
    }

    /// <summary>Gives back the lane taken at <paramref name="entered"/>.</summary>
    internal void Exit(long entered)
    {
        Interlocked.Add(ref _held, Stopwatch.GetTimestamp() - entered);
        _free.Release();
    }
}

/// <summary>
/// The lane one step of a write holds, which the stages it calls into give back while they wait and take again after: a
/// slice's rows handed over while the slice before is written, then the object finished. Used by one step at a time.
/// </summary>
internal sealed class LaneHold(LaneBudget budget)
{
    private long _entered = -1;

    /// <summary>Whether the step holds its lane now.</summary>
    internal bool Held => _entered >= 0;

    /// <summary>Takes the step's lane, waiting for one.</summary>
    internal async ValueTask EnterAsync(CancellationToken cancellationToken)
    {
        if (_entered < 0)
        {
            _entered = await budget.EnterAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Gives the step's lane back, if it holds it.</summary>
    internal void Exit()
    {
        if (_entered >= 0)
        {
            budget.Exit(_entered);
            _entered = -1;
        }
    }

    /// <summary>Waits for <paramref name="task"/> on no lane: the lane given back for the wait, and taken again after it.</summary>
    internal async ValueTask WaitAsync(Task task, CancellationToken cancellationToken)
    {
        bool held = Held;
        Exit();
        try
        {
            // The step's own write, started on the pool, which no context of the caller's runs.
#pragma warning disable VSTHRD003
            await task.ConfigureAwait(false);
#pragma warning restore VSTHRD003
        }
        finally
        {
            if (held)
            {
                await EnterAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
