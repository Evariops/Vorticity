using System;
using System.Threading;
using System.Threading.Tasks;

namespace Vorticity.Execution;

/// <summary>
/// Synchronous work cut into items <c>[0, count)</c>, run on its caller and on pumps of a core beside it (<see cref="CoreBatch"/>):
/// what a question spreads over its degree, on lanes of the session's that are free, never past them.
/// </summary>
/// <remarks>
/// A question that started a task a partition, or a worker a lane, computed on threads of the pool the session's degree did
/// not count: a question and a job together took more cores than the degree. Its items run here on the lanes the session
/// gives, its caller's among them, and a question whose lanes are all taken runs them on its caller alone.
/// </remarks>
internal static class CoreFor
{
    /// <summary>
    /// Runs <paramref name="body"/> for each of items <c>[0, count)</c>, on the caller and on pumps of <paramref name="core"/>,
    /// <paramref name="lanes"/> at once at most, the caller's included; rethrows the first an item threw. An item cancelled
    /// by <paramref name="cancellationToken"/> runs no further: the token is the body's to watch.
    /// </summary>
    internal static async ValueTask RunAsync(FlowCore core, int lanes, int count, Action<int> body, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (count <= 0)
        {
            return;
        }

        if (count == 1 || lanes <= 1)
        {
            for (int item = 0; item < count; item++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                body(item);
            }

            return;
        }

        CoreBatch batch = CoreBatch.Rent(core, lanes);
        try
        {
            await batch.RunAsync(new Work(body, cancellationToken), count).ConfigureAwait(false);
        }
        finally
        {
            CoreBatch.Return(batch);
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    /// <summary>The body of each item, run once it is claimed, skipped once the work is cancelled.</summary>
    private sealed class Work(Action<int> body, CancellationToken cancellationToken) : IBatchWork
    {
        public void Run(CoreBatch batch, int item)
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                body(item);
            }
        }
    }
}
