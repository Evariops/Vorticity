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
