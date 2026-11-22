using System;
using System.Threading;
using System.Threading.Tasks;

namespace Vorticity.Execution;

/// <summary>
/// Work that waits on the store or the disk and computes little: started beside the session's degree, on no lane, since a
/// lane held while a request is out does nothing the degree is for. What it computes once the bytes are in, it computes
/// on whatever lane its caller holds, or on the core.
/// </summary>
/// <remarks>
/// Every start of work beside its caller outside the core is counted by the launch ratchet; one that waits goes through
/// here, so that what computes and what waits are told apart by where they start, not by a count's exceptions.
/// </remarks>
internal static class Waits
{
    /// <summary>
    /// Runs <paramref name="body"/> for each of items <c>[0, count)</c>, <paramref name="fanOut"/> at once at most, each
    /// waiting on a request: the requests in flight together, as many as the store answers well at once.
    /// </summary>
    internal static async Task ForAsync(int count, int fanOut, Func<int, CancellationToken, ValueTask> body, CancellationToken cancellationToken)
    {
        if (count <= 0)
        {
            return;
        }

        ParallelOptions options = new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(fanOut, 1, count), CancellationToken = cancellationToken };
        await Parallel.ForAsync(0, count, options, body).ConfigureAwait(false);
    }

    /// <summary>Starts <paramref name="work"/>, which waits on a request, on the pool, beside the caller: its task.</summary>
    internal static Task StartAsync(Func<Task> work) => Task.Run(work, CancellationToken.None);

    /// <summary>Starts <paramref name="work"/>, which waits on a request, on the pool, beside the caller: its task.</summary>
    internal static Task<T> StartAsync<T>(Func<Task<T>> work) => Task.Run(work, CancellationToken.None);

    /// <summary>
    /// Starts <paramref name="work"/>, which blocks its thread on reads of the disk, on the pool, beside the caller: its task,
    /// cancelled unstarted if <paramref name="cancellationToken"/> is.
    /// </summary>
    internal static Task StartBlockingAsync(Action work, CancellationToken cancellationToken) => Task.Run(work, cancellationToken);

    /// <summary>
    /// Queues <paramref name="work"/> on <paramref name="state"/> for the pool, awaited by no one: the upkeep of what no
    /// session owns, which writes to the disk or gives memory back to the system, such as a cache's writes or the process's
    /// held memory relieved.
    /// </summary>
    internal static void Upkeep<TState>(Action<TState> work, TState state) => ThreadPool.UnsafeQueueUserWorkItem(work, state, preferLocal: false);
}
