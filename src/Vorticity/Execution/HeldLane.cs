using System.Threading;
using System.Threading.Tasks;

namespace Vorticity.Execution;

/// <summary>
/// The lane a flow holds for as long as it runs, a compaction job's, given back while the flow waits for what computes
/// nothing, the store taking what the flow wrote, and taken again after: a lane held while a request is out does nothing the
/// degree is for, and another work computes on it meanwhile.
/// </summary>
/// <remarks>
/// Only the flow that holds it gives it back and takes it again, in its own order. Taken again, it is waited for while the
/// flow holds none, which is what a work that holds no lane may do (<see cref="LaneBudget.EnterAsync"/>), and a lane given
/// back goes to the works that wait for one before any work takes a free one: the flow is served by the next lane anyone
/// gives back, a unit's end, a worker's, another flow waiting on its store, even while its slices merged ahead hold theirs
/// waiting for it.
/// </remarks>
internal sealed class HeldLane(LaneBudget lanes, long entered)
{
    private long _entered = entered;

    /// <summary>The waits the lane was given back for, which were not done when the flow came to them.</summary>
    internal int GivenBack { get; private set; }

    /// <summary>Gives the lane back for good: the flow ends.</summary>
    internal void Exit() => lanes.Exit(_entered);

    /// <summary>
    /// Awaits <paramref name="wait"/>: at once when it is done, and otherwise with the lane given back meanwhile and taken
    /// again after, whatever became of the wait.
    /// </summary>
    internal ValueTask<T> WhileAsync<T>(ValueTask<T> wait) => wait.IsCompleted ? wait : GiveBackAsync(wait);

    /// <inheritdoc cref="WhileAsync{T}(ValueTask{T})"/>
    internal ValueTask WhileAsync(ValueTask wait) => wait.IsCompleted ? wait : GiveBackAsync(wait);

    private async ValueTask<T> GiveBackAsync<T>(ValueTask<T> wait)
    {
        GivenBack++;
        lanes.Exit(_entered);
        try
        {
            return await wait.ConfigureAwait(false);
        }
        finally
        {
            _entered = await lanes.EnterAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async ValueTask GiveBackAsync(ValueTask wait)
    {
        GivenBack++;
        lanes.Exit(_entered);
        try
        {
            await wait.ConfigureAwait(false);
        }
        finally
        {
            _entered = await lanes.EnterAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }
}
